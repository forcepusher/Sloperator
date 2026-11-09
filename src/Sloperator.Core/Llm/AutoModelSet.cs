using Sloperator.Core.Settings;

namespace Sloperator.Core.Llm;

/// <summary>One of the models Auto picks from, as the server lists it.</summary>
/// <param name="Id">The model's name on the server.</param>
/// <param name="Named">Whether the user's words found it; false when the server has none that fits and Sloperator's own pick stands in.</param>
/// <param name="Efforts">The effort levels it takes, as the server says; empty for none.</param>
public sealed record AutoModel(string Id, bool Named, IReadOnlyList<string> Efforts);

/// <summary>
/// The three models of <see cref="AutoModels"/> on a server (the user, 2026-11-09): the newest model each one's
/// words name (<see cref="ModelChoice.Named"/>), else <c>standIn</c>, the model Sloperator picks itself
/// (<see cref="ModelChoice.Pick"/>). The same for every server and model: only the user's words tell them apart.
/// </summary>
public sealed record AutoModelSet(AutoModel Evaluation, AutoModel Daily, AutoModel Complex)
{
    public AutoModel this[AutoTier tier] => tier switch
    {
        AutoTier.Evaluation => Evaluation,
        AutoTier.Complex => Complex,
        _ => Daily,
    };

    /// <summary>How a model of the three was picked, to end "Picked automatically: …".</summary>
    public static string Why(AutoTier tier) => "your " + Name(tier).ToLowerInvariant();

    /// <summary>The setting's name, as Settings says it.</summary>
    public static string Name(AutoTier tier) => tier switch
    {
        AutoTier.Evaluation => "Evaluation model",
        AutoTier.Complex => "Complex task model",
        _ => "Daily driver model",
    };

    /// <summary>Whether there is a model to choose: two of the three differ. On LM Studio with one model, Auto just uses it.</summary>
    public bool Differ => Evaluation.Id != Daily.Id || Daily.Id != Complex.Id;

    /// <param name="catalog">What the server lists.</param>
    /// <param name="standIn">The model for one whose words fit none there; null when the server offers none.</param>
    /// <param name="efforts">The levels a model takes.</param>
    public static AutoModelSet? Resolve(AutoModels words, IReadOnlyList<ModelInfo> catalog, string? standIn, Func<string, IReadOnlyList<string>> efforts)
    {
        var chat = catalog.Where(m => m.Chat).Select(m => m.Id).ToList();
        AutoModel? Of(string wanted) => ModelChoice.Named(wanted, chat) is { } id ? new AutoModel(id, true, efforts(id))
            : standIn is not null ? new AutoModel(standIn, false, efforts(standIn)) : null;
        return Of(words.Evaluation) is { } evaluation && Of(words.Daily) is { } daily && Of(words.Complex) is { } complex
            ? new AutoModelSet(evaluation, daily, complex)
            : null;
    }
}
