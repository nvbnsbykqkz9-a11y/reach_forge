using System;
using System.Threading;

namespace ReachForge.Desktop;

/// <summary>
/// 1人の利用者につき1つだけ起動する（サーバーのポートと DB を共有するため）。
/// 2つ目を起動したら、1つ目のウィンドウを前に出すよう知らせて終わる。
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _signal;
    private readonly RegisteredWaitHandle? _registration;

    public SingleInstance(string name)
    {
        _mutex = new Mutex(initiallyOwned: true, $@"Local\{name}", out var created);
        IsFirst = created;
        _signal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.Activate");
        if (IsFirst)
        {
            _registration = ThreadPool.RegisterWaitForSingleObject(_signal, (_, _) => Activated?.Invoke(), null, Timeout.Infinite, executeOnlyOnce: false);
        }
    }

    public bool IsFirst { get; }

    /// <summary>別の起動から「前に出して」と頼まれた。</summary>
    public event Action? Activated;

    public void ActivateFirst() => _signal.Set();

    public void Dispose()
    {
        _registration?.Unregister(null);
        if (IsFirst) _mutex.ReleaseMutex();
        _mutex.Dispose();
        _signal.Dispose();
    }
}
