using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Sloperator.Core.Llm;
using Xunit;

namespace Sloperator.Core.Tests;

public class LlmHttpTests
{
    /// <summary>
    /// LM Studio listens on IPv4 only, and "localhost" is ::1 first: Windows took 2 s to give up on
    /// ::1 at each new connection, so a task with a model to load reached LM Studio 4 s late.
    /// </summary>
    [Fact]
    public async Task Localhost_reaches_a_server_listening_on_IPv4_only_at_once()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serving = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                using var client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
                var stream = client.GetStream();
                var request = new byte[4096];
                _ = await stream.ReadAsync(request);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
            }
        }, TestContext.Current.CancellationToken);
        try
        {
            for (var i = 0; i < 2; i++)
            {
                using var http = LlmHttp.Create(TimeSpan.FromSeconds(10));
                var watch = Stopwatch.StartNew();
                Assert.Equal("ok", await http.GetStringAsync($"http://localhost:{port}/v1/models", TestContext.Current.CancellationToken));
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"took {watch.ElapsedMilliseconds} ms");
            }
            await serving;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task A_port_nobody_listens_on_fails_rather_than_hangs()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var http = LlmHttp.Create(TimeSpan.FromSeconds(30));
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetStringAsync($"http://localhost:{port}/v1/models", TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// localhost is ::1 and 127.0.0.1, and both refuse a port nobody listens on. The second refusal, never looked at,
    /// was reported by the finalizer as a task nobody waited for (crash.log, 2026-11-10).
    /// </summary>
    [Fact]
    public async Task A_refused_connection_leaves_no_failure_for_the_finalizer()
    {
        var unobserved = new List<string>();
        void Seen(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            lock (unobserved)
                unobserved.Add(e.Exception.ToString());
        }
        TaskScheduler.UnobservedTaskException += Seen;
        try
        {
            for (var i = 0; i < 3; i++)
                Assert.True(await RefusedAsync());
            // The attempts are collected once nothing holds the connection's task: what they threw is reported then.
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            lock (unobserved)
                Assert.DoesNotContain(unobserved, e => e.Contains(nameof(LlmHttp), StringComparison.Ordinal));
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Seen;
        }
    }

    /// <summary>Whether connecting to a port nobody listens on failed, the connection's task let go of when it returns.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<bool> RefusedAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        try
        {
            (await LlmHttp.ConnectAsync(new DnsEndPoint("localhost", port), CancellationToken.None)).Dispose();
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }
}
