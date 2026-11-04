# Threading reliability validation

This records validation scope, not a claim of WPF or Avalonia reliability parity. Passing the test suite and publishing
with Native AOT do not establish long-running production reliability.

## Lifecycle hardening

- Reproduced cleanup failure after native window loss: `Close()` left the closed queue installed as the thread's current
  queue. A regression test now verifies cleanup and successful creation of a replacement queue.
- Track resource release separately from logical closure, so owner-thread cleanup still executes after earlier rejection
  of work. Detach window user data before releasing the managed handle.
- Handle [`WM_NCDESTROY`](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-ncdestroy) to abandon pending work without requiring another post or waiting for thread exit. Track window
  destruction so later cleanup does not act on a reused native window handle.
- Roll back failed construction. Fault injection after owner-handle acquisition, window creation, user-data attachment,
  and exit-monitor registration verifies exception preservation, window removal and successful retry.
- Cover the full native window callback with exception routing, including managed-handle lookup and destruction handling.

The concurrent-producer regression runs eight queue lifecycles, each with four producers and 128 synchronous/asynchronous
invocations per producer. It checks completion, cancellation, shutdown faults, callback thread ownership, and at-most-once
execution. Running asynchronous callbacks in this test finish without requiring UI continuations after shutdown.
This is a bounded concurrency regression, not a sustained-load benchmark or exhaustive exploration of thread interleavings.

## Production qualification still required

1. Define the application's supported Windows versions and architectures, peak posting rate, callback duration, producer
   count, and maximum session length.
2. Run sustained-load and soak tests with representative UI callbacks, real input/rendering, nested modal loops, COM
   interactions, and repeated application shutdown. Include CPU contention and thread-pool starvation.
3. Record enqueue-to-start latency distributions, input/rendering latency, backlog growth, managed allocations, native
   handles, process memory, dropped work and completed/canceled/faulted invocations. Establish application-specific limits.
4. Extend native-failure injection beyond construction stages to failed wake messages, timer setup, window binding and
   teardown. Existing full-message-queue tests exercise actual wake retry, but do not substitute for all native failures.
5. Review cancellation/start/shutdown races and modal reentrancy independently. Add deterministic regressions for every
   reproduced issue, run them under JIT and Native AOT, and keep the workload tests reproducible in CI or scheduled runs.

The queue is unbounded. Applications producing work faster than the UI thread can process it must coalesce updates or
apply backpressure upstream. Canceling an invocation does not remove its queue entry immediately. Started asynchronous
callbacks that need UI continuations must finish while the UI message loop is still running.
