using Avalonia.Threading;

namespace Sloperator.App.ViewModels;

/// <summary>
/// An action run on the window's thread once calls to <see cref="Restart"/> stop coming for a moment: a
/// burst of changes on disk is read once it is over (a build writes hundreds of files, an edit is written in
/// parts), a selection dragged goes into the box once it rests. Nothing runs before the first call.
/// </summary>
internal sealed class Debounce
{
    private readonly DispatcherTimer _timer;

    public Debounce(TimeSpan delay, Action action)
    {
        // Not the constructor that takes the handler: that one starts the timer at once.
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = delay };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            action();
        };
    }

    /// <summary>The action runs once the delay goes by without another call.</summary>
    public void Restart()
    {
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Nothing runs until the next <see cref="Restart"/>.</summary>
    public void Stop() => _timer.Stop();
}
