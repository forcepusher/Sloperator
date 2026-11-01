using System.Collections.Specialized;

namespace Sloperator.App.ViewModels;

/// <summary>
/// A side panel's way into the chat's box (File inspector, Image editor, Web preview, Terminal): what it
/// picks goes in as its card, and taken off the box another way (the card's ×), the panel lets go of it too.
/// </summary>
internal sealed class BoxPicks : IDisposable
{
    /// <summary>Characters of what is picked the model is given at most, with the count of the rest.</summary>
    public const int MaxChars = 20_000;

    private readonly MainWindowViewModel? _chat;
    private readonly Action _takenOff;

    /// <summary>Set while <see cref="Put"/> changes the box: that isn't a pick taken off.</summary>
    private bool _putting;

    /// <param name="chat">The chat whose box it is; null for a panel with none (tests, the full-screen viewer).</param>
    /// <param name="takenOff">The box's last pick taken off other than by <see cref="Put"/>: the panel lets go of what it picked, if anything.</param>
    public BoxPicks(MainWindowViewModel? chat, Action takenOff)
    {
        _chat = chat;
        _takenOff = takenOff;
        if (chat is not null)
            chat.Attachments.CollectionChanged += OnBoxChanged;
    }

    /// <summary>What is picked into the box in place of what was; none takes it out.</summary>
    public void Put(IReadOnlyList<(string Path, string Caption)> picked)
    {
        _putting = true;
        try { _chat?.Pick(picked); }
        finally { _putting = false; }
    }

    private void OnBoxChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_putting || e.OldItems is null || !e.OldItems.OfType<AttachmentItem>().Any(a => a.Picked) || _chat?.Attachments.Any(a => a.Picked) == true)
            return;
        _takenOff();
    }

    /// <summary><paramref name="text"/> cut to <see cref="MaxChars"/>, with how many more <paramref name="what"/> there were.</summary>
    public static string Capped(string text, string what = "characters") =>
        text.Length <= MaxChars ? text : text[..MaxChars] + $"\n[... {text.Length - MaxChars:N0} more {what}]";

    public void Dispose()
    {
        if (_chat is not null)
            _chat.Attachments.CollectionChanged -= OnBoxChanged;
    }
}
