using Planner.Data;

namespace Planner.Services;

/// <summary>Общее состояние на всех страницах: идёт ли синхронизация и чем она закончилась.</summary>
public class AppState
{
    public event Action? Changed;
    public bool Syncing { get; private set; }
    public SyncRun? LastRun { get; private set; }

    public void SetSyncing(bool v) { Syncing = v; Changed?.Invoke(); }
    public void SetLastRun(SyncRun? r) { LastRun = r; Changed?.Invoke(); }
    public void Notify() => Changed?.Invoke();
}
