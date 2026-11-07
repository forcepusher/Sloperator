using System.Security.Cryptography;
using Sloperator.Core.Llm;
using Sloperator.Core.Tools;

namespace Sloperator.Core.Agent;

/// <summary>
/// Notices when the model is stuck: the same steps over and over, or actions that leave the
/// screen exactly as it was. It never stops the run (long tasks are legitimate); it adds a note after
/// the tool results telling the model to change approach. Memory use is fixed, however long the run.
/// <para>
/// Only steps repeated in a row count. It used to count one call three times among the last twelve,
/// and 44 of 44 notes in three chats were wrong (2026-11-03): a Unity menu opened with the same two
/// clicks to reach a different item each time, and a zoom on the same spot after each action.
/// </para>
/// </summary>
internal sealed class LoopDetector
{
    /// <summary>The longest round of steps looked for, such as open a menu, pick an item, close it, look.</summary>
    private const int LongestRound = 4;

    /// <summary>Rounds in a row before the model is told it is looping.</summary>
    private const int Rounds = 3;

    /// <summary>Actions in a row that leave the screen unchanged before the model is told.</summary>
    private const int NoChangeThreshold = 3;

    // Looking at the screen or waiting repeatedly is normal (e.g. watching something for hours).
    private static readonly HashSet<string> Passive = new(StringComparer.OrdinalIgnoreCase)
        { "screenshot", "zoom", "wait", "wait_until_change", "capture_result" };

    private readonly List<string> _recent = [];
    private byte[]? _lastScreen;
    private int _unchanged;

    /// <summary>Returns a note to say after the tool results, or null if all looks fine.</summary>
    public string? Check(ToolCall call, ToolResult result)
    {
        var notes = new List<string>();

        if (!Passive.Contains(call.Name))
        {
            _recent.Add(call.Name + " " + call.Arguments.Replace(" ", ""));
            if (_recent.Count > LongestRound * Rounds)
                _recent.RemoveAt(0);
            if (RepeatedRound() is { } steps)
            {
                notes.Add(steps == 1
                    ? $"Note: you made exactly this call {Rounds} times in a row. If it is not getting you closer, stop repeating it and try a different approach."
                    : $"Note: you repeated the same {steps} calls {Rounds} times in a row. If they are not getting you closer, stop repeating them and try a different approach.");
                // Said once for these rounds: the next note takes three more.
                _recent.Clear();
            }
        }

        if (result.Image is not null)
        {
            var hash = SHA256.HashData(result.Image);
            var same = _lastScreen is not null && hash.AsSpan().SequenceEqual(_lastScreen);
            _lastScreen = hash;
            _unchanged = same && !Passive.Contains(call.Name) ? _unchanged + 1 : 0;
            if (_unchanged >= NoChangeThreshold)
                notes.Add($"Note: the screen has not changed after your last {_unchanged} actions. They are probably not working; try something different (another control or the keyboard).");
        }

        return notes.Count > 0 ? string.Join("\n", notes) : null;
    }

    /// <summary>How many steps go round when the latest ones repeat <see cref="Rounds"/> times in a row, or null.</summary>
    private int? RepeatedRound()
    {
        for (var steps = 1; steps <= LongestRound; steps++)
        {
            var last = _recent.Count - 1;
            var compared = steps * (Rounds - 1);
            if (_recent.Count >= steps * Rounds && Enumerable.Range(0, compared).All(i => _recent[last - i] == _recent[last - i - steps]))
                return steps;
        }
        return null;
    }
}
