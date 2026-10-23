using System.Windows.Threading;

namespace SkyrimModPacker.UI.Services;

public interface IDraftAutosaveScheduler
{
    void Schedule(Func<Task> save);
    void Cancel();
}

/// <summary>Debounces on the UI dispatcher so editor snapshots are never read from a worker thread.</summary>
public sealed class DraftAutosaveScheduler : IDraftAutosaveScheduler, IDisposable
{
    private readonly DispatcherTimer _timer;
    private Func<Task>? _save;
    public DraftAutosaveScheduler() : this(TimeSpan.FromSeconds(2)) { }
    public DraftAutosaveScheduler(TimeSpan interval)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
        _timer.Tick += OnTick;
    }
    public void Schedule(Func<Task> save) { Cancel(); _save = save; _timer.Start(); }
    public void Cancel() { _timer.Stop(); _save = null; }
    private async void OnTick(object? sender, EventArgs e)
    {
        var save = _save;
        Cancel();
        if (save != null) await save(); // The ViewModel reports storage failures without throwing out of this callback.
    }
    public void Dispose() { Cancel(); _timer.Tick -= OnTick; }
}
