# DirectN threading load test

A standalone, headless Win32 message loop using the production dispatcher. It offers callbacks at a configurable total
rate from independent asynchronous producers. `post` uses `TryPost(Action)`; `invoke` uses `InvokeAsync(Action)` and
observes every returned task. It does not require the Generic Host, a TCP endpoint, Orleans, or a backend connection.

The initial target is a cash-register fleet using older Pentium N3530-class processors, 8 GB RAM, Windows 10/11, and
multi-day application sessions. Rates below are exploratory test points, not estimates of the application's traffic.
No results on the development machine establish performance on that fleet.

## Build a standalone executable

From the repository root:

```powershell
dotnet publish tools/Scarlet.DirectN.Threading.LoadTest -c Release -r win-x64 -p:PublishAot=true -o artifacts/load-test-bin
```

Copy the published directory to a representative test till. This command produces a 64-bit executable; confirm the
Windows installation is 64-bit. A 32-bit Windows installation requires a separately built and validated `win-x86` target.
Record the machine model, exact Windows edition/build, power profile and competing application load with each report.

## Short baseline runs

Run each workload separately, starting at a low rate on the actual machine:

```powershell
.\Scarlet.DirectN.Threading.LoadTest.exe --seconds 60 --rate 100 --producers 1 --callback-us 100 --mode post --output post-100.json
.\Scarlet.DirectN.Threading.LoadTest.exe --seconds 60 --rate 1000 --producers 4 --callback-us 100 --mode invoke --output invoke-1000.json
```

`--rate` is the total requested callbacks per second, divided among producers. Scheduling uses roughly 10 ms batches;
Windows timer granularity and CPU contention affect the actual offered rate. Check accepted/completed counts and elapsed
time rather than assuming the requested rate was achieved.

`--callback-us` is a minimum wall-clock duration of synthetic CPU spinning inside each UI callback, not a replay of actual
application work. Try several callback durations and producer counts. `--max-pending` defaults to 4096 and limits the
harness backlog; it does not change the library's unbounded queue. An overload run stops producers and drains accepted
work, with a 30-second drain deadline. Use a smaller backlog limit with very long callbacks.

Exit codes: `0` completed and drained; `2` backlog limit reached and drained; `130` operator stopped with Ctrl+C and drained;
`1` runtime error or failed drain; `64` invalid arguments. Exit 0 does not mean performance met an application-specific SLA.
Shells may translate the Ctrl+C exit status; `OperatorCanceled` in the JSON report records graceful operator cancellation.

## Soak run

After short runs establish a sustainable rate, use that rate for a 48-hour run:

```powershell
.\Scarlet.DirectN.Threading.LoadTest.exe --seconds 172800 --rate 100 --producers 4 --callback-us 100 --mode invoke --output soak.json 2> soak-progress.log
```

The shown rate is only a starting example. The maximum duration is seven days. Ctrl+C requests graceful draining and a
final report. Progress is written to stderr once per minute, including pending work, memory and resource counts. The JSON
report is written at completion; a machine shutdown or forced process termination can prevent the final report.

## What the report measures

- Actual accepted/completed callbacks, peak backlog, and whether the backlog limit stopped the run.
- Submission-to-start latency, including the dispatcher call overhead: mean, p50/p95/p99 and maximum. Percentiles use a bounded reservoir of 32,768 samples; count,
  mean and maximum cover the full run. Rare latency spikes can be absent from sampled percentiles; inspect maximum too.
- Gaps between Win32 timer callbacks, nominally requested every 16 ms, including the final partial interval. Delivered
  timer ticks are reported separately. This is a message-loop progress proxy, not actual input latency or frame time.
- Process CPU as a percentage of one logical core, process-wide allocated bytes, and GC collection counts.
- Before/after memory and kernel-handle counts; memory, kernel handles and GDI/USER object counts sampled every 500 ms.
  Sampled peaks can miss shorter spikes. The snapshots are taken while the UI queue is still alive, before final cleanup.
- Windows build, OS/process architecture, runtime, logical processors and whether dynamic code is supported.

Instrumentation, runtime startup, producers and monitoring all contribute to these numbers. A short before/after memory
increase is not proof of a leak: examine the long-term trend across GC cycles and runtime warmup. The sampling buffers
and retained outstanding invocation tasks are bounded; records are not retained for every callback in a multi-day run.

## Application qualification

Use the current WPF application as the functional and responsiveness baseline. Replay the same TCP requests, Orleans
stream/subscription activity and background jobs against both applications, including requests while coupon/payment
dialogs are open, disconnect/reconnect cycles and host shutdown. Keep backend latency separate from UI scheduling latency.

Treat business UI requests as commands that need explicit completion/failure handling, normally through `InvokeAsync`.
Keep TCP/Orleans I/O and CPU-heavy processing outside UI callbacks. Apply bounded admission upstream and define how busy
requests are handled. Coalescing is appropriate for replaceable status displays; do not silently coalesce distinct business
commands. Application policy must decide whether multiple dialog-opening requests may overlap during modal reentrancy.

This harness exercises synchronous callbacks only. It does not validate real rendering, modal dialogs, COM interactions,
asynchronous dialog lifetimes, Orleans delivery semantics, network faults, or thread-pool starvation. Those remain separate
integration tests in the application. See [the reliability plan](../../docs/ThreadingReliability.md).
The native harness build also does not establish Native AOT compatibility of Orleans or the application's other dependencies.
