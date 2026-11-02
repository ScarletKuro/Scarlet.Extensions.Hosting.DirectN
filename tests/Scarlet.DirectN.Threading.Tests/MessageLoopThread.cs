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
    public void StopWithoutDraining() => _stop = true;

    private volatile bool _stop;
    private volatile uint _nativeThreadId;

    private void Run()
    {
        _nativeThreadId = Functions.GetCurrentThreadId();
        _queue.SetResult(DirectNMessageQueue.GetOrCreateForCurrentThread());
        while (!_stop && Functions.GetMessageW(out var msg, default, 0, 0) > 0)
        {
            Functions.TranslateMessage(msg);
            Functions.DispatchMessageW(msg);
        }
    }
}
