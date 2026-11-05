using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DirectN;

namespace Scarlet.DirectN.Threading.LoadTest;

internal sealed class UiLoop : IAsyncDisposable
{
    private readonly TaskCompletionSource<DirectNMessageQueue> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private uint _nativeThreadId;

    public UiLoop()
    {
        _thread = new Thread(Run) { Name = "Load test UI", IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task<DirectNMessageQueue> Ready => _ready.Task;

    private void Run()
    {
        DirectNMessageQueue? queue = null;
        Exception? failure = null;
        try
        {
            _nativeThreadId = Functions.GetCurrentThreadId();
            queue = DirectNMessageQueue.GetOrCreateForCurrentThread();
            _ready.SetResult(queue);
            int result;
            while ((result = Functions.GetMessageW(out var message, default, 0, 0)) > 0)
            {
                Functions.TranslateMessage(message);
                Functions.DispatchMessageW(message);
            }

            if (result < 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            _ready.TrySetException(exception);
        }
        finally
        {
            try
            {
                UiPulse.Stop();
                queue?.Close();
            }
            catch (Exception exception)
            {
                failure = failure is null ? exception : new AggregateException(failure, exception);
            }

            if (failure is null)
            {
                _stopped.SetResult();
            }
            else
            {
                _stopped.SetException(failure);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_stopped.Task.IsCompleted && (!_ready.Task.IsCompletedSuccessfully ||
            !_ready.Task.Result.TryPost(() => Functions.PostQuitMessage(0))))
        {
            const uint WM_QUIT = 0x0012;
            Functions.PostThreadMessageW(_nativeThreadId, WM_QUIT, default, default);
        }

        await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        // The completion source is signaled inside the thread's finally block. Also wait for actual thread termination
        // before allowing Main to return and tear down the native runtime.
        if (!_thread.Join(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("The load-test UI thread did not terminate after cleanup.");
        }
    }
}

internal static unsafe partial class UiPulse
{
    [ThreadStatic] private static Distribution? t_gaps;
    [ThreadStatic] private static long t_last;
    [ThreadStatic] private static nuint t_timer;
    [ThreadStatic] private static long t_ticks;

    public static long Ticks => t_ticks;

    public static void Start(Distribution gaps)
    {
        t_gaps = gaps;
        t_last = Stopwatch.GetTimestamp();
        t_ticks = 0;
        t_timer = SetTimer(0, 0, 16, &Tick);
        if (t_timer == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    public static void Stop()
    {
        // Include the final partial interval, so stopping before the next timer delivery cannot hide a long stall.
        t_gaps?.Record(Stopwatch.GetTimestamp() - t_last);
        if (t_timer != 0)
        {
            KillTimer(0, t_timer);
            t_timer = 0;
        }

        t_gaps = null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void Tick(nint hwnd, uint message, nuint id, uint time)
    {
        try
        {
            var now = Stopwatch.GetTimestamp();
            t_gaps?.Record(now - t_last);
            t_last = now;
            t_ticks++;
        }
        catch (Exception exception)
        {
            Environment.FailFast("Load test timer failed.", exception);
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nuint SetTimer(nint hwnd, nuint id, uint interval,
        delegate* unmanaged[Stdcall]<nint, uint, nuint, uint, void> callback);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool KillTimer(nint hwnd, nuint id);
}
