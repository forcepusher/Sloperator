using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Sloperator.Core.Render;

/// <summary>
/// The PC's Edge (else Chrome) run headless with one page open over its DevTools protocol, for the
/// scene renderer: commands go out and their answers come back by id, while events (a request the
/// page makes, an error it throws) go to <see cref="Event"/> as they come. Each start gets a profile
/// folder of its own under the root; ones an earlier Sloperator left (and their browsers) are cleared
/// first. Unlike <see cref="Comfy.ComfyFrontend"/>, which waits for one answer at a time, it reads
/// all the time, as a page held at a request waits for the answer to that event.
/// </summary>
public sealed partial class DevToolsBrowser : IAsyncDisposable
{
    private readonly Process _process;
    private readonly ClientWebSocket _socket;
    private readonly string _profile;
    private readonly int _port;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _waiting = new();
    private readonly SemaphoreSlim _sending = new(1, 1);
    private readonly CancellationTokenSource _closing = new();
    private readonly Task _reading;
    private int _nextId;
    private int _disposed;

    /// <summary>An event the page sent (its method and params), on the thread that reads them: answer quickly or hand it on.</summary>
    public event Action<string, JsonObject>? Event;

    /// <summary>Whether the browser runs headless, or as a normal browser with its window hidden (headless turned off by policy).</summary>
    public bool Headless { get; }

    private DevToolsBrowser(Process process, ClientWebSocket socket, string profile, int port, bool headless)
    {
        (_process, _socket, _profile, _port, Headless) = (process, socket, profile, port, headless);
        _reading = Task.Run(ReadAsync);
    }

    private static int _launches;

    /// <summary>The PC's browsers can't run headless: later starts go straight to a hidden window.</summary>
    private static bool _headlessRefused;

    /// <summary>Starts a browser with a profile folder of its own under <paramref name="profileRoot"/> and connects to its page.</summary>
    /// <param name="browsers">The programs to try, best first; null for the PC's Edge and Chrome.</param>
    /// <param name="arguments">More switches for the browser (GPU, colour profile).</param>
    public static async Task<DevToolsBrowser> LaunchAsync(string profileRoot, IReadOnlyList<string>? browsers, IEnumerable<string> arguments, CancellationToken ct)
    {
        browsers ??= Web.HeadlessBrowser.FindExecutables();
        if (browsers.Count == 0)
            throw new RenderException("Drawing a scene needs Edge or Chrome, and neither is on this PC.");
        ClearLeftovers(profileRoot);
        var extra = arguments.ToList();
        var failures = new List<string>();
        foreach (var exe in browsers)
            foreach (var headless in _headlessRefused ? [false] : new[] { true, false })
            {
                try
                {
                    return await StartAsync(exe, headless, profileRoot, extra, ct).ConfigureAwait(false);
                }
                catch (RenderException ex)
                {
                    failures.Add($"{Path.GetFileNameWithoutExtension(exe)} {(headless ? "headless" : "in a hidden window")}: {ex.Message}");
                }
            }
        throw new RenderException(string.Join(" ", failures));
    }

    private static async Task<DevToolsBrowser> StartAsync(string exe, bool headless, string profileRoot, List<string> extra, CancellationToken ct)
    {
        var profile = Path.Combine(profileRoot, $"{Environment.ProcessId}-{Interlocked.Increment(ref _launches)}");
        Directory.CreateDirectory(profile);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WindowStyle = headless ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden,
        };
        IEnumerable<string> mode = headless
            ? ["--headless=new"]
            : ["--window-position=-32000,-32000", "--window-size=1280,800"];
        // A page drawn frame by frame must not be throttled as a background tab is.
        foreach (var arg in mode.Concat(extra).Concat(
                 [
                     "--no-first-run", "--no-default-browser-check", "--disable-extensions", "--disable-sync", "--mute-audio",
                     "--hide-scrollbars", "--do-not-de-elevate", "--disable-background-timer-throttling",
                     "--disable-backgrounding-occluded-windows", "--disable-renderer-backgrounding",
                     "--remote-debugging-port=0", "--remote-allow-origins=*", $"--user-data-dir={profile}", "about:blank",
                 ]))
            psi.ArgumentList.Add(arg);
        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new RenderException("The browser did not start.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new RenderException($"The browser could not be started: {ex.Message}");
        }
        Platform.ChildProcesses.Tie(process);
        var said = new StringBuilder();
        _ = KeepTailAsync(process.StandardError, said);
        _ = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        try
        {
            var waited = Stopwatch.StartNew();
            int port;
            while ((port = ReadPort(profile)) == 0)
            {
                ct.ThrowIfCancellationRequested();
                if (process.HasExited && Tail(said).Contains("Headless mode is disallowed", StringComparison.OrdinalIgnoreCase))
                {
                    _headlessRefused = true;
                    throw new RenderException("The browser won't run headless on this PC (its HeadlessModeEnabled policy is off).");
                }
                if (process.HasExited && waited.Elapsed > TimeSpan.FromSeconds(10))
                    throw new RenderException($"The browser closed at once (exit code {process.ExitCode}).{Tail(said)}");
                if (waited.Elapsed > TimeSpan.FromSeconds(30))
                    throw new RenderException($"The browser did not open its debugging port within 30 seconds.{Tail(said)}");
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var targets = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", ct).ConfigureAwait(false)) as JsonArray;
            var url = (string?)targets?.FirstOrDefault(t => (string?)t?["type"] == "page")?["webSocketDebuggerUrl"]
                      ?? throw new RenderException("The browser offered no page to draw in.");
            var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.Zero;
            try
            {
                await socket.ConnectAsync(new Uri(url), ct).ConfigureAwait(false);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
            return new DevToolsBrowser(process, socket, profile, port, headless);
        }
        catch (Exception ex) when (ex is RenderException or HttpRequestException or WebSocketException or System.Text.Json.JsonException
                                       or OperationCanceledException)
        {
            await EndAsync(process, profile, 0).ConfigureAwait(false);
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
                throw;
            throw ex as RenderException ?? new RenderException($"The browser's debugging port did not answer: {ex.Message}");
        }
    }

    /// <summary>Sends a command and waits for its answer (its "result"); a refusal throws with what the browser said.</summary>
    public async Task<JsonObject> CallAsync(string method, JsonObject? parameters, TimeSpan timeout, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var answer = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[id] = answer;
        try
        {
            var message = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() };
            await SendAsync(message, ct).ConfigureAwait(false);
            var reply = await answer.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
            if (reply["error"] is JsonObject error)
                throw new RenderException($"The browser refused {method}: {(string?)error["message"]}");
            return reply["result"] as JsonObject ?? new JsonObject();
        }
        catch (TimeoutException)
        {
            throw new RenderException($"The browser did not answer {method} within {timeout.TotalSeconds:0} seconds.");
        }
        finally
        {
            _waiting.TryRemove(id, out _);
        }
    }

    /// <summary>Sends a command without waiting for its answer (one for an event, from the thread that reads them).</summary>
    public Task SendAsync(string method, JsonObject parameters) =>
        SendAsync(new JsonObject { ["id"] = Interlocked.Increment(ref _nextId), ["method"] = method, ["params"] = parameters }, CancellationToken.None);

    private async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _sending.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            throw new RenderException("The connection to the browser broke: " + ex.Message);
        }
        finally
        {
            _sending.Release();
        }
    }

    /// <summary>Runs <paramref name="expression"/> in the page (a promise is waited for) and gives back its value; a script error throws with its message.</summary>
    public async Task<JsonNode?> EvaluateAsync(string expression, TimeSpan timeout, CancellationToken ct)
    {
        var result = await CallAsync("Runtime.evaluate", new JsonObject
        {
            ["expression"] = expression,
            ["awaitPromise"] = true,
            ["returnByValue"] = true,
        }, timeout, ct).ConfigureAwait(false);
        if (result["exceptionDetails"] is JsonObject error)
        {
            var message = (string?)error["exception"]?["description"] ?? (string?)error["text"] ?? error.ToJsonString();
            throw new RenderException("The page's script failed: " + message.Split('\n')[0].Clip(400));
        }
        return result["result"]?["value"]?.DeepClone();
    }

    private async Task ReadAsync()
    {
        var buffer = new byte[256 * 1024];
        var received = new MemoryStream();
        try
        {
            while (!_closing.IsCancellationRequested)
            {
                received.SetLength(0);
                WebSocketReceiveResult r;
                do
                {
                    r = await _socket.ReceiveAsync(buffer, _closing.Token).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close)
                        return;
                    received.Write(buffer, 0, r.Count);
                } while (!r.EndOfMessage);
                if (JsonNode.Parse(received.GetBuffer().AsSpan(0, (int)received.Length)) is not JsonObject message)
                    continue;
                if (message["id"] is JsonValue idValue && idValue.TryGetValue<int>(out var id))
                {
                    if (_waiting.TryGetValue(id, out var waiting))
                        waiting.TrySetResult(message);
                }
                else if ((string?)message["method"] is { } method)
                {
                    try { Event?.Invoke(method, message["params"] as JsonObject ?? new JsonObject()); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { /* a handler's failure isn't the connection's */ }
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or System.Text.Json.JsonException or InvalidOperationException)
        {
        }
        finally
        {
            foreach (var waiting in _waiting.Values)
                waiting.TrySetException(new RenderException("The browser closed its connection."));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        _closing.Cancel();
        try { _socket.Dispose(); }
        catch (ObjectDisposedException) { }
        try { await _reading.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        await EndAsync(_process, _profile, _port).ConfigureAwait(false);
        _closing.Dispose();
    }

    /// <summary>Ends the browser with what it started (or, when it handed over to a copy of itself, asks that one to close) and deletes its profile.</summary>
    private static async Task EndAsync(Process process, string profile, int port)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            }
            else if (port > 0)
                await CloseOverPortAsync(port).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException) { }
        process.Dispose();
        for (var i = 0; i < 15 && Directory.Exists(profile); i++)
        {
            try { Directory.Delete(profile, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Task.Delay(200).ConfigureAwait(false); }
        }
    }

    private static async Task CloseOverPortAsync(int port)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            if ((string?)JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/version").ConfigureAwait(false))?["webSocketDebuggerUrl"] is not { } url)
                return;
            using var socket = new ClientWebSocket();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await socket.ConnectAsync(new Uri(url), cts.Token).ConfigureAwait(false);
            await socket.SendAsync(Encoding.UTF8.GetBytes("""{"id":1,"method":"Browser.close"}"""), WebSocketMessageType.Text, true, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or WebSocketException
                                       or System.Text.Json.JsonException or UriFormatException or InvalidOperationException)
        {
        }
    }

    /// <summary>The port in the profile's DevToolsActivePort (both its lines written), or 0.</summary>
    private static int ReadPort(string profile)
    {
        try
        {
            using var stream = new FileStream(Path.Combine(profile, "DevToolsActivePort"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var lines = new StreamReader(stream).ReadToEnd().Split('\n', StringSplitOptions.TrimEntries);
            return lines.Length >= 2 && int.TryParse(lines[0], out var port) && port > 0 && lines[1].StartsWith("/devtools/browser/", StringComparison.Ordinal) ? port : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Ends the browsers whose profile is a folder under <paramref name="profileRoot"/> of a Sloperator
    /// no longer running, and deletes those folders: what a Sloperator that ended without closing them left.
    /// </summary>
    private static void ClearLeftovers(string profileRoot)
    {
        if (!Directory.Exists(profileRoot))
            return;
        string root;
        try { root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profileRoot)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return; }
        foreach (var process in new[] { "msedge", "chrome" }.SelectMany(Process.GetProcessesByName))
            using (process)
            {
                try
                {
                    if (Platform.ProcessCommandLine.Of(process.Id) is { } line && !line.Contains("--type=", StringComparison.Ordinal) &&
                        Platform.ProcessCommandLine.Switch(line, "--user-data-dir") is { Length: > 0 } dir &&
                        Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir))) is { } parent &&
                        string.Equals(parent, root, StringComparison.OrdinalIgnoreCase) &&
                        LaunchFolder().Match(Path.GetFileName(Path.TrimEndingDirectorySeparator(dir))) is { Success: true } m && !Running(int.Parse(m.Groups[1].Value)))
                        process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or InvalidOperationException
                                               or System.ComponentModel.Win32Exception) { }
            }
        foreach (var folder in Directory.EnumerateDirectories(profileRoot))
            if (LaunchFolder().Match(Path.GetFileName(folder)) is { Success: true } m && !Running(int.Parse(m.Groups[1].Value)))
                try { Directory.Delete(folder, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [GeneratedRegex(@"^(\d+)-\d+$")]
    private static partial Regex LaunchFolder();

    /// <summary>Whether <paramref name="pid"/> is this Sloperator or another one running now.</summary>
    private static bool Running(int pid)
    {
        if (pid == Environment.ProcessId)
            return true;
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName.StartsWith("Sloperator", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string Tail(StringBuilder said)
    {
        string text;
        lock (said) text = said.ToString();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "" : " It said: " + string.Join(" / ", lines.TakeLast(3)).Clip(400);
    }

    private static async Task KeepTailAsync(StreamReader output, StringBuilder said)
    {
        try
        {
            while (await output.ReadLineAsync().ConfigureAwait(false) is { } line)
                lock (said)
                {
                    said.AppendLine(line);
                    if (said.Length > 8192) said.Remove(0, said.Length - 4096);
                }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }
}

/// <summary>A scene that couldn't be drawn, said so the model can fix it.</summary>
public sealed class RenderException(string message) : Exception(message);
