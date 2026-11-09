using System.Globalization;
using System.Text.RegularExpressions;

namespace Sloperator.Core.Llm;

/// <summary>What is known about one model a server offers; nulls where nothing says.</summary>
public sealed record ModelInfo(string Id)
{
    /// <summary>False for models that can't hold a chat: embedding, reranking and speech models.</summary>
    public bool Chat { get; init; } = true;

    /// <summary>Whether it takes images; null when nothing says.</summary>
    public bool? Vision { get; init; }

    /// <summary>Parameters, in billions; for a mixture of experts, all of them.</summary>
    public double? Params { get; init; }

    /// <summary>Parameters a mixture of experts uses for each token, in billions ("30B-A3B": 3).</summary>
    public double? ActiveParams { get; init; }

    /// <summary>Bits per weight of its quantization (Q6_K: 6, F16: 16).</summary>
    public double? Bits { get; init; }

    /// <summary>Size of its files on the server.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>Loaded on the server, so it answers without loading first.</summary>
    public bool Loaded { get; init; }

    /// <summary>Whether it calls tools (OpenAI-style "tools"); null when nothing says.</summary>
    public bool? Tools { get; init; }

    /// <summary>Whether it costs nothing, on a service that prices its models (Kilo, OpenRouter); null where nothing is priced.</summary>
    public bool? Free { get; init; }

    /// <summary>Dollars for a million input tokens, where the server prices its models; null where it doesn't.</summary>
    public decimal? InputPrice { get; init; }

    /// <summary>Dollars for a million output tokens, where the server prices its models; null where it doesn't.</summary>
    public decimal? OutputPrice { get; init; }

    /// <summary>
    /// The server's own score for it on <see cref="ModelChoice.ScoreName"/> (OpenRouter's
    /// /benchmarks); null where the server reports none. A server that reports scores is judged by
    /// them alone, not by <see cref="AgentBenchmark"/>.
    /// </summary>
    public double? Score { get; init; }

    /// <summary>Percent it solves of the server's own test of an agent calling tools (OpenRouter's τ²-bench airline); null where it reports none.</summary>
    public double? ToolScore { get; init; }

    /// <summary>
    /// The effort levels it can be asked to think with (see <see cref="Settings.Efforts"/>): empty when
    /// it takes none, null when nothing says, which is taken as none.
    /// </summary>
    public IReadOnlyList<string>? Efforts { get; init; }
}

/// <summary>
/// The model to use when none was picked by hand, or the picked one isn't on the server: the
/// cheapest of the strongest ones the server offers (<see cref="Pick"/>). Strength is the server's
/// own score where it reports scores (<see cref="ModelInfo.Score"/>), else the score on an agent
/// benchmark (<see cref="AgentBenchmark"/>) where known, else size. Sizes and quantizations come
/// from the server where it says (LM Studio does, see <see cref="ServerProbe"/>), else from the
/// names, which usually carry them ("qwen3.8-27b@q6_k_xl").
/// </summary>
public static partial class ModelChoice
{
    /// <summary>What the server's own scores are (<see cref="ModelInfo.Score"/>).</summary>
    public const string ScoreName = "the Artificial Analysis Intelligence Index";

    /// <summary>
    /// How much weaker than the strongest a model may score and still be picked for being cheaper,
    /// and how much dearer than the cheapest one counts as as cheap: a tenth. On OpenRouter on
    /// 2026-11-03 that kept Claude Opus 5.5 (58.4), GPT-6 Astra, GPT-6.1 Sol and Claude Sonnet 5.5
    /// (54.2, 7% below) and left out GPT-6.1 Terra (51.0, 13% below) and Claude Haiku 5.5 (36.2):
    /// the user wanted Sonnet 5.5 rather than Haiku, which would spoil a first impression.
    /// </summary>
    internal const double Tolerance = 0.1;

    /// <summary>
    /// <paramref name="picked"/> while the server offers it (a server that lists nothing is taken at
    /// its word), or the name it lists that model by (<see cref="AsListed"/>), otherwise the cheapest
    /// of the strongest models it offers (<see cref="Pick"/>). Null when it offers none that can chat.
    /// </summary>
    public static string? Choose(string picked, IReadOnlyList<ModelInfo> offered) =>
        !string.IsNullOrEmpty(picked) && (offered.Count == 0 || offered.Any(m => m.Id == picked))
            ? picked
            : AsListed(picked, offered) ?? Pick(offered)?.Id;

    /// <summary>
    /// The name a server lists <paramref name="picked"/> by, null when it lists none: of two names
    /// for it, the one written alike or else the shortest (an alias rather than a dated one).
    /// </summary>
    private static string? AsListed(string picked, IReadOnlyList<ModelInfo> offered)
    {
        var same = offered.Where(m => m.Chat && IsSameModel(m.Id, picked)).Select(m => m.Id).ToList();
        return same.Find(id => Written(id) == Written(picked)) ?? same.MinBy(id => id.Length);
    }

    /// <summary>
    /// Whether two names are one model. Each server writes names its own way: Sonnet 5.5 is
    /// anthropic/claude-sonnet-5-5 on proxyapi.ru, anthropic/claude-sonnet-5.5 on OpenRouter and
    /// claude-sonnet-5-5 on Anthropic's API, and Sonnet 4.5 has a date on some (…-4-5-20250929).
    /// OpenRouter's list in Anthropic's format marks its million-token models with a tag in
    /// brackets (anthropic/claude-haiku-5.5[1m], 9 November 2026). So the maker in front, how the
    /// words are joined, a date and a tag in brackets at the end don't count; anything else does:
    /// Sonnet 5 isn't Sonnet 5.5, nor x:free the x that costs money.
    /// </summary>
    public static bool IsSameModel(string a, string b) =>
        a == b || (DateAtEnd().Replace(Written(a), "") is { Length: > 0 } name && name == DateAtEnd().Replace(Written(b), ""));

    /// <summary>A model's name without its maker or a tag in brackets at its end, in <see cref="Words"/>.</summary>
    private static string Written(string id) => string.Join(' ', Words(TagAtEnd().Replace(id[(id.LastIndexOf('/') + 1)..], "")));

    // "[1m]"
    [GeneratedRegex(@"\[[^\[\]]*\]\s*$")]
    private static partial Regex TagAtEnd();

    /// <summary>Letters and numbers, a version as one word whichever way it is written: claude-opus-5-5-20261001 is claude, opus, 5.5, 20261001; qwen3.8-27b is qwen, 3.8, 27, b.</summary>
    internal static List<string> Words(string name) =>
        Word().Matches(name.ToLowerInvariant()).Select(m => m.Value.Replace('-', '.').Replace('_', '.')).ToList();

    // A version's parts are one or two digits, each ending the number: not a date (-20261001) or a size (-27b).
    [GeneratedRegex(@"[a-z]+|\d+(?:[.\-_]\d{1,2}(?![\da-z]))*")]
    private static partial Regex Word();

    // In Written's words: "20250929", "2025.09.29".
    [GeneratedRegex(@" (?:\d{8}|\d{4}\.\d{2}\.\d{2})$")]
    private static partial Regex DateAtEnd();

    /// <summary>
    /// The best value: of the models about as strong as the strongest (<see cref="Strongest"/>), the
    /// cheapest. About as strong is as able (takes images, calls tools, a whole file) and scoring at
    /// most a tenth below it (<see cref="Tolerance"/>). Cheapest by three input tokens to one output
    /// token (<see cref="Blended"/>), and within a tenth of the cheapest counts as as cheap; between
    /// those, the one that does best on the server's own test of calling tools
    /// (<see cref="ModelInfo.ToolScore"/>), then the stronger. Where nothing is scored or priced
    /// (LM Studio, Ollama), simply the strongest. Where the server offers a Claude model fit to work
    /// the screen, all this is among Claude models alone (<see cref="OffersClaude"/>), and without
    /// scores from the server it goes by Claude's own line (<see cref="ClaudeByFamily"/>).
    /// </summary>
    public static ModelInfo? Pick(IEnumerable<ModelInfo> offered)
    {
        var all = offered.ToList();
        return !OffersClaude(all) ? PickAmong(all) : ClaudeByFamily(all) ?? PickAmong(all.Where(IsClaude));
    }

    /// <summary>
    /// How a model picked here came to be picked, to end "Sloperator uses …": it reads the same
    /// on any server.
    /// </summary>
    public static string Why(ModelInfo picked, IReadOnlyList<ModelInfo> offered) =>
        OffersClaude(offered) && ClaudeByFamily(offered)?.Id == picked.Id && ClaudeModel.Parse(picked.Id).Family is { } family
            ? $"the newest Claude {char.ToUpperInvariant(family[0])}{family[1..]} on the server"
            : BestValue;

    /// <summary>The usual <see cref="Why"/>.</summary>
    public const string BestValue = "the cheapest of the strongest models on the server";

    /// <summary>
    /// The model <paramref name="wanted"/> names among <paramref name="models"/>, as switch_model reads a name
    /// (<see cref="Like"/>, then <see cref="Newest"/> of several): "Claude Haiku" is the newest Haiku there.
    /// Null when none fits, or nothing is asked.
    /// </summary>
    public static string? Named(string wanted, IReadOnlyList<string> models) =>
        string.IsNullOrWhiteSpace(wanted) ? null : Like(wanted.Trim(), models) is { Count: > 0 } like ? Newest(like) : null;

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
    /// strongest by <see cref="Strongest"/> on what the names tell ("gemma" is the 31b
    /// rather than the 12b), and the shortest name (an alias rather than a dated one).
    /// </summary>
    public static string Newest(IReadOnlyList<string> like)
    {
        var newest = like.GroupBy(VersionOf).MaxBy(g => g.Key)!.OrderBy(id => id.Length).ToList();
        return Strongest(newest.Select(FromName))?.Id ?? newest[0];
    }

    /// <summary>The first number in the name that isn't a date (claude-3-5-haiku-20241022: 3.5, gemma-4-12b-it: 4); 0 for none.</summary>
    private static Version VersionOf(string name) =>
        Words(name).Where(w => char.IsDigit(w[0]) && w.Split('.')[0].Length <= 4)
            .Select(w => Version.TryParse(w.Contains('.') ? w : w + ".0", out var v) ? v : null)
            .FirstOrDefault(v => v is not null) ?? new Version(0, 0);

    private static bool HasRun(List<string> words, List<string> run)
    {
        for (var i = 0; i + run.Count <= words.Count; i++)
            if (words.Skip(i).Take(run.Count).SequenceEqual(run))
                return true;
        return false;
    }

    /// <summary>
    /// The newest fit Sonnet, else Opus, else Haiku, on a server that reports no scores: without
    /// them nothing tells Sonnet from Opus (Anthropic's API lists neither scores nor prices, and
    /// the table has no Claude 5.5 yet). Sonnet is half the price of Opus and about as strong
    /// (54.2 to 58.4 on OpenRouter on 2026-11-03), which is where the scores land too. The user
    /// chose this on 2026-11-04; Fable and Mythos cost more than twice as much, and are for
    /// picking by hand. Null when the server reports scores or offers none of the three.
    /// </summary>
    private static ModelInfo? ClaudeByFamily(IReadOnlyList<ModelInfo> offered)
    {
        if (offered.Any(m => m.Score is not null))
            return null;
        var fit = Ranked(offered.Where(IsClaude)).Where(x => x.Able == (false, false, false)).Select(x => x.Model).ToList();
        return new[] { "sonnet", "opus", "haiku" }
            .Select(family => fit.Where(m => ClaudeModel.Parse(m.Id).Family == family).MaxBy(m => ClaudeModel.Parse(m.Id).Version))
            .FirstOrDefault(m => m is not null);
    }

    /// <summary>
    /// Whether the server offers a Claude model fit to work the screen: one that can chat, takes
    /// images and calls tools, as far as anything says. The one exception to choosing every model
    /// the same way, and only for the auto-pick: the user asked on 2026-11-04 to prefer Claude
    /// models there, so where there are any, <see cref="Pick"/> chooses among them alone.
    /// </summary>
    public static bool OffersClaude(IEnumerable<ModelInfo> offered) =>
        Ranked(offered.Where(IsClaude)) is [var best, ..] && best.Able == (false, false, false);

    private static bool IsClaude(ModelInfo m) => m.Id.Contains("claude", StringComparison.OrdinalIgnoreCase);

    private static ModelInfo? PickAmong(IEnumerable<ModelInfo> offered)
    {
        var ranked = Ranked(offered);
        if (ranked.Count == 0)
            return null;
        var top = ranked[0];
        if (top.Score is not > 0)
            return top.Model;
        var near = ranked.Where(x => x.Able == top.Able && x.Score >= top.Score * (1 - Tolerance)).ToList();
        if (near.Select(x => Blended(x.Model)).Where(p => p is not null).Min() is not { } cheapest)
            return top.Model;
        return near.Where(x => Blended(x.Model) <= cheapest * (1 + (decimal)Tolerance))
            .OrderByDescending(x => x.Model.ToolScore ?? -1) // stable: then the stronger
            .First().Model;
    }

    /// <summary>
    /// The strongest model that can chat. One that takes images comes first (without them the agent
    /// can't see the screen; where the server doesn't say, the benchmark table may), then one not
    /// listed as unable to call tools (a music model, a safety classifier). Then the best score: the
    /// server's own where it reports any (<see cref="ModelInfo.Score"/>), else the one on
    /// <see cref="AgentBenchmark"/>, and a model without one after every model with one;
    /// then the most parameters, a mixture of experts counted as the dense model it is about as
    /// strong as (<see cref="DenseEquivalent"/>); then, between equal scores and sizes, a later
    /// version scored as the one it follows; then the most bits per weight; then one already
    /// loaded, which answers without loading; then the server's own order. A size or quantization
    /// nothing tells counts below every known one, and a file far too small for its size and
    /// quantization (<see cref="LooksIncomplete"/>) comes last.
    /// </summary>
    public static ModelInfo? Strongest(IEnumerable<ModelInfo> offered) => Ranked(offered).FirstOrDefault().Model;

    /// <summary>The models that can chat, strongest first (see <see cref="Strongest"/>), with what makes them able and their score.</summary>
    private static List<(ModelInfo Model, (bool, bool, bool) Able, double? Score)> Ranked(IEnumerable<ModelInfo> offered)
    {
        var chat = offered.Where(m => m.Chat).ToList();
        var byServer = chat.Any(m => m.Score is not null);
        return chat
            .Select(m => (Model: m, Rating: AgentBenchmark.Rate(m)))
            .Select(x => (x.Model, x.Rating, Able: (LooksIncomplete(x.Model), SeesImages(x.Model, x.Rating) == false, x.Model.Tools == false),
                Score: byServer ? x.Model.Score : x.Rating?.Score))
            .OrderBy(x => x.Able)
            .ThenByDescending(x => x.Score ?? -1)
            .ThenByDescending(x => DenseEquivalent(x.Model) ?? -1)
            .ThenByDescending(x => x.Rating is { Exact: false })
            .ThenByDescending(x => x.Model.Bits ?? -1)
            .ThenByDescending(x => x.Model.Loaded)
            .Select(x => (x.Model, x.Able, x.Score))
            .ToList();
    }

    /// <summary>
    /// Dollars for a million tokens, three of input to one of output, the blend Artificial Analysis
    /// prices models by: an agent sends far more than it gets back. Null where either price is unknown.
    /// </summary>
    internal static decimal? Blended(ModelInfo m) => m.InputPrice is { } input && m.OutputPrice is { } output ? (3 * input + output) / 4 : null;

    /// <summary>Whether the model takes images: what the server says, else what the benchmark table says of it; null when neither says.</summary>
    private static bool? SeesImages(ModelInfo m, BenchmarkRating? rating) =>
        m.Vision ?? (rating is { Exact: true } own ? own.Basis.Vision : null);

    /// <summary>
    /// Billions of parameters of a dense model as strong as this one. A mixture of experts is about
    /// as strong as the geometric mean of its total and active parameters, a common rule of thumb:
    /// 30B-A3B is about 9.5B, 235B-A22B about 72B.
    /// </summary>
    internal static double? DenseEquivalent(ModelInfo m) =>
        m.Params is { } total && m.ActiveParams is { } active && active < total ? Math.Sqrt(total * active) : m.Params;

    /// <summary>
    /// Files under half of what the parameters take at that many bits can't be the whole model. The
    /// user's LM Studio lists such a file as "27B, 4-bit" at 2.3 GB, and it fails to load.
    /// </summary>
    internal static bool LooksIncomplete(ModelInfo m) =>
        m.SizeBytes is { } size && m.Params is { } total && m.Bits is { } bits && size < total * 1e9 * bits / 8 / 2;

    /// <summary>
    /// "27B, 6-bit, 79.8% on Terminal-Bench 2.1", "27B, 5-bit, scored as Qwen3.5 9B: 29.2% on
    /// Terminal-Bench 2.1", "30B-A3B, 4-bit", "27B", "54.2 on the Artificial Analysis Intelligence
    /// Index, $2 in and $10 out a million tokens"; null when nothing is known. The server's own score
    /// where it gave one, as that is what the model was judged by.
    /// </summary>
    public static string? Describe(ModelInfo m)
    {
        var parts = new List<string>();
        if (m.Params is { } total)
            parts.Add(m.ActiveParams is { } active ? $"{Count(total)}-A{Count(active)}" : Count(total));
        if (m.Bits is { } bits)
            parts.Add(bits.ToString("0.#", CultureInfo.InvariantCulture) + "-bit");
        if (m.Score is { } own)
            parts.Add(own.ToString("0.0", CultureInfo.InvariantCulture) + " on " + ScoreName);
        else if (AgentBenchmark.Rate(m) is { } rating)
        {
            var score = rating.Score.ToString("0.0", CultureInfo.InvariantCulture) + "% on " + AgentBenchmark.Name;
            parts.Add(rating.Exact ? score : $"scored as {rating.Basis.Model}: {score}");
        }
        if (m.InputPrice is { } input && m.OutputPrice is { } output)
            parts.Add(input == 0 && output == 0 ? "free" : $"{Dollars(input)} in and {Dollars(output)} out a million tokens");
        return parts.Count > 0 ? string.Join(", ", parts) : null;

        static string Count(double billions) => billions >= 1
            ? billions.ToString("0.#", CultureInfo.InvariantCulture) + "B"
            : (billions * 1000).ToString("0", CultureInfo.InvariantCulture) + "M";

        static string Dollars(decimal price) => "$" + price.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// What a model's name tells: its size ("27b", "1.5B", "135m", "8x7b" for eight experts of 7B,
    /// "30b-a3b" for 30B with 3B active), its quantization ("q6_k_xl", "iq2_s", "f16", "4bit",
    /// "awq") and whether it chats at all ("embed", "rerank", "whisper"…).
    /// </summary>
    public static ModelInfo FromName(string id)
    {
        var (total, active) = ParseSize(id);
        return new ModelInfo(id) { Chat = !NotChat().IsMatch(id), Params = total, ActiveParams = active, Bits = ParseBits(id) };
    }

    /// <summary>
    /// Billions of parameters in a name, or in a size such as LM Studio's "30B-A3B". A name with
    /// several sizes in it ("…-7b-instruct-1m", the last being its context) has the largest.
    /// </summary>
    internal static (double? Total, double? Active) ParseSize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return (null, null);
        double? total = null;
        foreach (Match m in SizeToken().Matches(text))
        {
            var n = Number(m.Groups["n"].Value) * (m.Groups["unit"].Value is "m" or "M" ? 0.001 : 1);
            if (m.Groups["experts"].Success)
                n *= Number(m.Groups["experts"].Value);
            total = Math.Max(total ?? 0, n);
        }
        var active = ActiveToken().Match(text);
        return (total, total is not null && active.Success ? Number(active.Groups["n"].Value) : null);
    }

    /// <summary>Bits per weight a quantization's name gives ("Q6_K_XL", "IQ2_S", "BF16", "8bit", "AWQ"); null when it gives none.</summary>
    internal static double? ParseBits(string? text)
    {
        if (string.IsNullOrEmpty(text) || QuantToken().Match(text) is not { Success: true } m)
            return null;
        return m.Groups["bits"].Success ? Number(m.Groups["bits"].Value) : 4; // AWQ and GPTQ are 4-bit
    }

    private static double Number(string text) => double.Parse(text, CultureInfo.InvariantCulture);

    // "27b", "1.5b", "135m", "8x7b"; not the 3 of "qwen3" or "a3b", nor "2026.27" in "2026.27b".
    [GeneratedRegex(@"(?<![a-z0-9])(?:(?<experts>\d{1,3})x)?(?<n>\d{1,3}\.\d{1,2}|\d{1,4})(?<unit>[bm])(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex SizeToken();

    // The parameters a mixture of experts uses per token: "-a3b", "-A22B".
    [GeneratedRegex(@"(?<![a-z0-9])a(?<n>\d{1,3}(?:\.\d{1,2})?)b(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex ActiveToken();

    // GGUF types (q4_k_m, iq2_s, q8_0), floats (f16, bf16, fp8), MLX and other formats (4bit,
    // 8-bit, int4, w8a8, mxfp4); AWQ and GPTQ name no bits.
    [GeneratedRegex(@"(?<![a-z0-9])(?:i?q(?<bits>[1-8])|b?fp?(?<bits>8|16|32)|(?<bits>[2-8])-?bits?|int(?<bits>4|8)|w(?<bits>4|8)a\d+|(?:mx|nv)fp(?<bits>4|8)|awq|gptq)(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex QuantToken();

    // Embedding, reranking and speech models: they can't hold a chat.
    [GeneratedRegex(@"embed|rerank|whisper|dall-e|moderation|transcribe|(?<![a-z])tts(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex NotChat();
}
