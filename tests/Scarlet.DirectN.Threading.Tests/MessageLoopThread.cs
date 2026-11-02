using DirectN;

namespace Scarlet.DirectN.Threading.Tests;

/// <summary>
/// A thread running a plain Win32 message loop with a <see cref="DirectNMessageQueue"/>.
/// </summary>
internal sealed class MessageLoopThread : IDisposable
{
    private readonly Thread _thread;
    private readonly TaskCompletionSource<DirectNMessageQueue> _queue = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MessageLoopThread()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Test message loop" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        Queue = _queue.Task.GetAwaiter().GetResult();
    }

    public DirectNMessageQueue Queue { get; }

    public Thread Thread => _thread;

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Post(_ =>
        {
            try
            {
                result.SetResult(func());
            }
            catch (Exception ex)
            {
                result.SetException(ex);
            }
        }, null);
        return result.Task;
    }

    public void Dispose()
    {
        if (_thread.IsAlive)
        {
            if (!Queue.TryPost(_ => Functions.PostQuitMessage(0), null))
            {
                const uint WM_QUIT = 0x0012;
                Functions.PostThreadMessageW(_nativeThreadId, WM_QUIT, default, default);
            }

            _thread.Join(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>
    /// Ends the loop right after the current message, leaving anything still queued (e.g. a wake-up) unprocessed,
    /// and lets the thread exit without closing the queue.
    /// </summary>
    /// <param name="linger">How long the thread stays alive, without pumping, after the loop has ended.</param>
    public void StopWithoutDraining(TimeSpan linger = default)
    {
        _linger = linger;
        _stop = true;
    }

    /// <summary>
    /// Gets the Win32 thread id.
    /// </summary>
    public uint NativeThreadId => _nativeThreadId;

    /// <summary>
    /// Completes when the loop has ended (the thread may still linger).
    /// </summary>
    public Task LoopEnded => _loopEnded.Task;

    private readonly TaskCompletionSource _loopEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _stop;
    private volatile uint _nativeThreadId;
    private TimeSpan _linger;

    private void Run()
    {
        _nativeThreadId = Functions.GetCurrentThreadId();
        _queue.SetResult(DirectNMessageQueue.GetOrCreateForCurrentThread());
        while (!_stop && Functions.GetMessageW(out var msg, default, 0, 0) > 0)
        {
            Functions.TranslateMessage(msg);
            Functions.DispatchMessageW(msg);
        }

        _loopEnded.SetResult();
        Thread.Sleep(_linger);
    }
}
