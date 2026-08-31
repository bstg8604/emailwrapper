using System.Threading;

namespace EmailClient;

/// <summary>
/// Keeps one Purplemail running at a time. Without this, an app that lives in the tray is easy to
/// start twice — the window is hidden, so re-launching from the Start menu looks like the way to
/// get it back — and the second instance fights the first over IMAP IDLE and settings.json.
///
/// A mutex answers "am I first?"; a named event is how the second instance says "come to the
/// front" before it exits, since the first one owns the window it can't touch directly.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    // Per-user, not machine-wide: two people signed into the same machine each get their own
    // Purplemail. "Local\" scopes the name to the session, which is that boundary.
    private const string MutexName = @"Local\Purplemail.SingleInstance";
    private const string SignalName = @"Local\Purplemail.ShowWindow";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _signal;
    private RegisteredWaitHandle? _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle signal, bool isFirst)
    {
        _mutex = mutex;
        _signal = signal;
        IsFirstInstance = isFirst;
    }

    public bool IsFirstInstance { get; }

    public static SingleInstance Acquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
        return new SingleInstance(mutex, signal, createdNew);
    }

    /// <summary>Asks the already-running instance to show itself. Called from the doomed second one.</summary>
    public void SignalFirstInstance() => _signal.Set();

    /// <summary>
    /// Runs <paramref name="onSignal"/> whenever another launch asks us to surface. The callback
    /// arrives on a thread-pool thread, so it marshals onto the UI thread itself.
    /// </summary>
    public void ListenForActivation(Action onSignal)
    {
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _signal,
            (_, _) => onSignal(),
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        if (IsFirstInstance)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Never acquired on this thread — nothing to release.
            }
        }
        _signal.Dispose();
        _mutex.Dispose();
    }
}
