using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sloperator.Core.Llm;
using Sloperator.Core.Settings;

namespace Sloperator.Core.Tools;

/// <summary>The app's side of <see cref="ListModelsTool"/>, <see cref="SwitchModelTool"/> and <see cref="SetEffortTool"/>: the server, key, model and effort in its top bar.</summary>
public interface IModelServerSwitch
{
    /// <summary>The server in use, as the top bar shows it.</summary>
    ServerSettings Current { get; }

    /// <summary>
    /// Puts them in the top bar, as typing them there would, and checks the server. The run going
    /// on keeps its model; the user's next message goes to the new one. An empty model is
    /// Sloperator's own pick, the strongest one the server offers.
    /// </summary>
    Task SwitchAsync(string baseUrl, string apiKey, string model);

    /// <summary>Picks the effort in the top bar, as its dropdown would: saved, and used from the user's next message on.</summary>
    Task SetEffortAsync(string level);
}

/// <summary>
/// The agent's view of the model Sloperator itself runs on: the server, the model and effort in use,
/// and the models the server offers, for <see cref="SwitchModelTool"/> to pick from.
/// </summary>
/// <param name="http">Makes the client the server is checked with; a new one each time when null (tests give their own).</param>
public sealed class ListModelsTool(Func<HttpClient>? http = null) : Tool
{
    public override string Name => "list_models";

    public override string Description =>
        "The model server Sloperator itself runs on (you are that model): its address, the model and effort in use, and the models it offers.";

    public override ToolRisk Risk => ToolRisk.ReadOnly;

    public override JsonObject Parameters => Schema();

    public override string Describe(JsonObject args) => "Show Sloperator's model server";

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.ModelServer is not { } sw)
            return ToolResult.Error(ModelTools.NoTopBar);
        var current = sw.Current;
        var probe = await ModelTools.ProbeAsync(http, new ServerSettings { BaseUrl = current.BaseUrl, ApiKey = current.ApiKey, Model = current.Model }, ct).ConfigureAwait(false);
        var text = $"Sloperator runs on {ModelTools.Named(current.ModelInUse)} at {current.BaseUrl}, " + (current.ApiKey.Length > 0 ? "with an API key." : "without an API key.");
        text += current.EffortInUse is { } effort ? $" Effort: {effort} (it takes {string.Join(", ", current.EffortLevels)})." : " The model has no effort setting.";
        return new ToolResult(text + (probe.Reachable ? "\n" + ModelTools.ModelList(probe.Models) : $"\nThe server doesn't answer now: {probe.Message}"));
    }
}

/// <summary>
/// The agent connects Sloperator itself to another model: a local LM Studio, Claude, OpenRouter, or
/// back to Kilo's free ones. Asked to on Kilo's free Nemotron, it had nothing to do it with:
/// Sloperator's window is left out of its screenshots, so for five minutes it clicked through other
/// apps looking for a settings screen and switched nothing.
/// </summary>
/// <param name="http">Makes the client the server is checked with; a new one each time when null (tests give their own).</param>
public sealed class SwitchModelTool(Func<HttpClient>? http = null) : Tool
{
    public override string Name => "switch_model";

    public override string Description =>
        "Switches Sloperator itself (you are its model) to another server, API key and model, the way the user would in Sloperator's top bar " +
        "(which you can't see or click: Sloperator's window is left out of screenshots). Use it when the user asks to connect, switch to or use " +
        "another model or AI: LM Studio on this PC is http://localhost:1234/v1 (Ollama http://localhost:11434/v1), Claude is api.anthropic.com, " +
        "OpenRouter https://openrouter.ai/api/v1, Kilo's free models " + KiloGateway.Address + ". " +
        "The server is checked first and nothing changes if it doesn't answer or turns the key down. This task finishes on the model you are; " +
        "the user's next message goes to the new one.";

    public override ToolRisk Risk => ToolRisk.Risky;

    public override JsonObject Parameters => Schema(
        ("address", Str("The server's address, such as http://localhost:1234/v1"), true),
        ("api_key", Str("The API key the user gave for that server; leave it out for a server without one (an address in use or used before keeps its key)"), false),
        ("model", Str("The model to use, as the server lists it or in words (\"opus 5.5\", \"haiku\": the newest such); leave it out for the strongest one the server offers"), false));

    // Never the key itself: the tool card shows this.
    public override string Describe(JsonObject args) =>
        $"Connect Sloperator to {GetString(args, "address")}" + (GetString(args, "model") is { Length: > 0 } m ? $", model {m}" : "") +
        (GetString(args, "api_key") is { Length: > 0 } ? ", with an API key" : "");

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.ModelServer is not { } sw)
            return ToolResult.Error(ModelTools.NoTopBar);
        var current = sw.Current;
        var address = RequireString(args, "address").Trim();
        // The same address again keeps its key, so "use model X" needs no key from the user; so does
        // one in the top bar's saved servers ("switch back to Claude").
        var key = GetString(args, "api_key")?.Trim() is { Length: > 0 } given ? given
            : SameServer(address, current.BaseUrl) ? current.ApiKey
            : current.Saved.FirstOrDefault(s => SameServer(address, s.BaseUrl))?.ApiKey ?? "";
        var wanted = GetString(args, "model")?.Trim() ?? "";
        var probe = await ModelTools.ProbeAsync(http, new ServerSettings { BaseUrl = address, ApiKey = key }, ct).ConfigureAwait(false);
        if (probe.KeyRefused)
            return ToolResult.Error($"Nothing changed: {probe.Message} Ask the user for the right API key.");
        if (!probe.Reachable)
            return ToolResult.Error($"Nothing changed: {probe.Message}{AppHint(address)} Check the address with the user, and that the server runs.");
        if (probe.Models.Count == 0)
            return ToolResult.Error("Nothing changed: the server answers but offers no models to chat with. A model has to be downloaded or loaded on it first.");

        var model = "";
        var others = "";
        if (wanted.Length > 0)
        {
            var like = ModelTools.Like(wanted, probe.Models);
            if (like.Count == 0)
                return ToolResult.Error($"Nothing changed: the server has no model \"{wanted}\".\n" +
                                        ModelTools.ModelList(probe.Models) + "\nSwitch again with one of them, or without model for the strongest.");
            model = like.Count == 1 ? like[0] : ModelTools.Best(like);
            if (like.Count > 1)
                others = $" (the newest or strongest of {like.Count} like \"{wanted}\": {string.Join(", ", like.Where(m => m != model).Take(20))}; switch again with one of their names for another)";
        }
        var url = probe.BaseUrl ?? address;
        await sw.SwitchAsync(url, key, model).ConfigureAwait(false);

        var inUse = model.Length > 0 ? model : probe.Model ?? probe.Models[0];
        var how = model.Length > 0 ? others : " (the strongest it offers; pass model to pick another)";
        var vision = probe.Vision == false && (model.Length == 0 || model == probe.Model)
            ? $" Note: the server says {inUse} doesn't take images, so on it Sloperator can't see or use the screen; tell the user, or connect a model that sees images." : "";
        return new ToolResult($"Sloperator is now connected to {url} and uses {inUse}{how}" + (key.Length > 0 ? ", with the API key." : ".") + vision +
                              " The top bar shows it. This task finishes on the model it started with; the user's next message goes to the new one, " +
                              "so tell the user it is done and that they can send their next request.");
    }

    private static bool SameServer(string a, string b) =>
        ServerAddress.Candidates(a).Intersect(ServerAddress.Candidates(b), StringComparer.OrdinalIgnoreCase).Any();

    /// <summary>Nothing on LM Studio's or Ollama's own port: its server is off more often than not. The port is what names the program.</summary>
    private static string AppHint(string address) =>
        ServerAddress.Candidates(address).FirstOrDefault() is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Port switch
            {
                1234 => " If it is LM Studio, its server has to be switched on (Developer tab, Status: Running)" +
                        (uri.IsLoopback ? "." : ", with Serve on Local Network on for other PCs to reach it."),
                11434 => " If it is Ollama, the Ollama app has to run.",
                _ => "",
            }
            : "";
}

/// <summary>
/// The agent sets how hard Sloperator's model thinks, as the top bar's effort dropdown does (the
/// user, 2 November 2026: it could switch the model and server, not the effort). Read-only for
/// approval: it changes no file and nothing outside Sloperator.
/// </summary>
public sealed class SetEffortTool : Tool
{
    public override string Name => "set_effort";

    public override string Description =>
        "Sets how hard Sloperator's model (you) thinks, as the effort dropdown in Sloperator's top bar does: more is slower and costs more. " +
        "Use it when the user asks you to think harder, less or at a given effort. This task finishes on the effort it started with; " +
        "the user's next message gets the new one.";

    public override ToolRisk Risk => ToolRisk.ReadOnly;

    public override JsonObject Parameters => Schema(
        ("level", Str("How hard the model thinks, least to most", [.. Efforts.Levels]), true));

    public override string Describe(JsonObject args) => $"Set Sloperator's effort to {GetString(args, "level")}";

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.ModelServer is not { } sw)
            return ToolResult.Error(ModelTools.NoTopBar);
        var current = sw.Current;
        var wanted = RequireString(args, "level").Trim().ToLowerInvariant().Replace("extra high", "xhigh").Replace("extra-high", "xhigh");
        if (!Efforts.Levels.Contains(wanted))
            throw new ToolArgumentException($"level must be one of {string.Join(", ", Efforts.Levels)}.");
        // As the top bar's dropdown, greyed out for such a model: an effort it would never be sent.
        if (current.EffortLevels.Count == 0)
            return ToolResult.Error($"Nothing changed: {ModelTools.Named(current.ModelInUse)} has no effort setting Sloperator can send" +
                                    " (on LM Studio it is set in LM Studio, per model). Tell the user.");
        await sw.SetEffortAsync(wanted).ConfigureAwait(false);
        var inUse = current.EffortInUse ?? wanted;
        var nearest = inUse == wanted ? "" : $" {ModelTools.Named(current.ModelInUse)} doesn't take {wanted}, so it gets {inUse}, the nearest it takes ({string.Join(", ", current.EffortLevels)}).";
        return new ToolResult($"Sloperator's effort is now {inUse}.{nearest} The top bar shows it. This task finishes on the effort it started with; " +
                              "the user's next message is answered with the new one, so tell the user it is done.");
    }
}

/// <summary>What the model tools share.</summary>
internal static partial class ModelTools
{
    /// <summary>
    /// The models <paramref name="wanted"/> names: the one it names exactly; else those whose name
    /// holds its words in a row, written any way ("opus 5.5" is claude-opus-5-5 and
    /// anthropic/claude-opus-5.5, "haiku" every Haiku, but "opus 5" not Opus 5.5); else those
    /// holding it as written. The user, 3 November 2026: "haiku 5.5", "opus 5.5", or even "haiku"
    /// or "opus" should work.
    /// </summary>
    public static IReadOnlyList<string> Like(string wanted, IReadOnlyList<string> models)
    {
        if (models.FirstOrDefault(m => m.Equals(wanted, StringComparison.OrdinalIgnoreCase)) is { } exact)
            return [exact];
        var words = Words(wanted);
        var like = words.Count == 0 ? [] : models.Where(m => HasRun(Words(m), words)).ToList();
        return like.Count > 0 ? like : models.Where(m => m.Contains(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Of several models named alike, the same way for every model and server: the highest version
    /// its name gives ("haiku" is Haiku 5.5 rather than 4.5 or 3.5); between equal versions the
    /// strongest by <see cref="ModelChoice.Strongest"/> on what the names tell ("gemma" is the 31b
    /// rather than the 12b), and the shortest name (an alias rather than a dated one).
    /// </summary>
    public static string Best(IReadOnlyList<string> like)
    {
        var newest = like.GroupBy(VersionOf).MaxBy(g => g.Key)!.OrderBy(id => id.Length).ToList();
        return ModelChoice.Strongest(newest.Select(ModelChoice.FromName))?.Id ?? newest[0];
    }

    /// <summary>The first number in the name that isn't a date (claude-3-5-haiku-20241022: 3.5, gemma-4-12b-it: 4); 0 for none.</summary>
    private static Version VersionOf(string name) =>
        Words(name).Where(w => char.IsDigit(w[0]) && w.Split('.')[0].Length <= 4)
            .Select(w => Version.TryParse(w.Contains('.') ? w : w + ".0", out var v) ? v : null)
            .FirstOrDefault(v => v is not null) ?? new Version(0, 0);

    /// <summary>Letters and numbers, a version as one word whichever way it is written: claude-opus-5-5-20261001 is claude, opus, 5.5, 20261001; qwen3.8-27b is qwen, 3.8, 27, b.</summary>
    private static List<string> Words(string name) =>
        Word().Matches(name.ToLowerInvariant()).Select(m => m.Value.Replace('-', '.').Replace('_', '.')).ToList();

    private static bool HasRun(List<string> words, List<string> run)
    {
        for (var i = 0; i + run.Count <= words.Count; i++)
            if (words.Skip(i).Take(run.Count).SequenceEqual(run))
                return true;
        return false;
    }

    // A version's parts are one or two digits, each ending the number: not a date (-20261001) or a size (-27b).
    [GeneratedRegex(@"[a-z]+|\d+(?:[.\-_]\d{1,2}(?![\da-z]))*")]
    private static partial Regex Word();

    public const string NoTopBar = "Sloperator's model server can't be changed from this run: the user sets it in the top bar of Sloperator's window.";

    public static async Task<ProbeResult> ProbeAsync(Func<HttpClient>? http, ServerSettings server, CancellationToken ct)
    {
        using var client = http?.Invoke() ?? LlmHttp.Create(TimeSpan.FromSeconds(15));
        return await ServerProbe.CheckAsync(client, server, ct).ConfigureAwait(false);
    }

    public static string Named(string model) => model.Length > 0 ? model : "no model yet";

    public static string ModelList(IReadOnlyList<string> models) =>
        models.Count == 0 ? "It offers no models." : $"Models it offers ({models.Count}): " + string.Join(", ", models.Take(60)) + (models.Count > 60 ? ", …" : "");
}
