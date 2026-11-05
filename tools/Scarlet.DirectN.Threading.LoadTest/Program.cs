using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scarlet.DirectN.Threading;

namespace Scarlet.DirectN.Threading.LoadTest;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += static (_, eventArgs) => Console.Error.WriteLine(eventArgs.ExceptionObject);
        if (args.Contains("--help", StringComparer.Ordinal))
        {
            Console.WriteLine("Options: --seconds 30 --rate 1000 --producers 4 --callback-us 100 --max-pending 4096 --mode post|invoke --output report.json");
            return 0;
        }

        LoadOptions options;
        try
        {
            options = LoadOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 64;
        }

        try
        {
            var report = await RunAsync(options).ConfigureAwait(false);
            var json = JsonSerializer.Serialize(report, ReportJsonContext.Default.LoadReport);
            Console.WriteLine(json);
            if (options.Output is { } path)
            {
                await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
            }

            return report.OperatorCanceled ? 130 : report.BacklogLimitReached ? 2 : 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task<LoadReport> RunAsync(LoadOptions options)
    {
        await using var loop = new UiLoop();
        var queue = await loop.Ready.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        var latency = new Distribution();
        var timerGaps = new Distribution();
        await queue.InvokeAsync(() => UiPulse.Start(timerGaps)).ConfigureAwait(false);
        // Establish queue delivery before recording the process baseline.
        await queue.InvokeAsync(() => { }).ConfigureAwait(false);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var before = ResourceSnapshot.Capture(process);
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes();
        var collectionsBefore = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(options.Seconds));
        using var monitoring = new CancellationTokenSource();
        long accepted = 0, completed = 0, pending = 0, peakPending = 0;
        var limitReached = 0;
        var operatorCanceled = 0;
        ConsoleCancelEventHandler onCancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Interlocked.Exchange(ref operatorCanceled, 1);
            try
            {
                stop.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // A handler already in flight can finish after it was unregistered during shutdown.
            }
        };
        Console.CancelKeyPress += onCancel;
        var watch = Stopwatch.StartNew();
        var peaks = before;
        var monitor = Task.Run(async () =>
        {
            using var tick = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            try
            {
                var nextProgress = TimeSpan.FromSeconds(60);
                while (await tick.WaitForNextTickAsync(monitoring.Token).ConfigureAwait(false))
                {
                    process.Refresh();
                    var current = ResourceSnapshot.Capture(process);
                    peaks = peaks.Peak(current);
                    if (watch.Elapsed >= nextProgress)
                    {
                        Console.Error.WriteLine($"{DateTimeOffset.UtcNow:O} elapsed={watch.Elapsed} accepted={Interlocked.Read(ref accepted)} completed={Interlocked.Read(ref completed)} pending={Interlocked.Read(ref pending)} privateMiB={current.PrivateBytes / 1048576.0:F1} workingSetMiB={current.WorkingSetBytes / 1048576.0:F1} handles={current.Handles} gdi={current.GdiObjects} user={current.UserObjects}");
                        nextProgress += TimeSpan.FromSeconds(60);
                    }
                }
            }
            catch (OperationCanceledException) when (monitoring.IsCancellationRequested)
            {
            }
        });

        try
        {
            var producers = Enumerable.Range(0, options.Producers).Select(producer => Task.Run(async () =>
            {
                // Timer granularity produces small bursts; this is offered load, not a precision scheduler.
                using var tick = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
                var outstanding = new List<Task>();
                long sent = 0;
                try
                {
                    while (watch.Elapsed.TotalSeconds < options.Seconds &&
                        await tick.WaitForNextTickAsync(stop.Token).ConfigureAwait(false))
                    {
                        await ObserveCompletedAsync(outstanding).ConfigureAwait(false);

                        var totalDue = (long)(watch.Elapsed.TotalSeconds * options.Rate);
                        var due = (totalDue + options.Producers - 1 - producer) / options.Producers;
                        while (sent < due && !stop.IsCancellationRequested && watch.Elapsed.TotalSeconds < options.Seconds)
                        {
                            // Bound retained task references even during a large catch-up batch after a scheduler stall.
                            if (outstanding.Count >= options.MaxPending)
                            {
                                await ObserveCompletedAsync(outstanding).ConfigureAwait(false);
                                if (outstanding.Count >= options.MaxPending)
                                {
                                    Interlocked.Exchange(ref limitReached, 1);
                                    stop.Cancel();
                                    break;
                                }
                            }

                            var backlog = Interlocked.Increment(ref pending);
                            if (backlog > options.MaxPending)
                            {
                                Interlocked.Decrement(ref pending);
                                Interlocked.Exchange(ref limitReached, 1);
                                stop.Cancel();
                                break;
                            }

                            UpdateMaximum(ref peakPending, backlog);
                            var postedAt = Stopwatch.GetTimestamp();
                            void Callback()
                            {
                                try
                                {
                                    queue.VerifyAccess();
                                    latency.Record(Stopwatch.GetTimestamp() - postedAt);
                                    var until = Stopwatch.GetTimestamp() + options.CallbackMicroseconds * Stopwatch.Frequency / 1_000_000;
                                    while (Stopwatch.GetTimestamp() < until)
                                    {
                                        Thread.SpinWait(1);
                                    }

                                    Interlocked.Increment(ref completed);
                                }
                                finally
                                {
                                    Interlocked.Decrement(ref pending);
                                }
                            }

                            if (options.Mode == "invoke")
                            {
                                outstanding.Add(queue.InvokeAsync(Callback));
                            }
                            else if (!queue.TryPost(Callback))
                            {
                                Interlocked.Decrement(ref pending);
                                throw new InvalidOperationException("The UI queue rejected workload before shutdown.");
                            }

                            Interlocked.Increment(ref accepted);
                            sent++;
                        }
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                }

                await Task.WhenAll(outstanding).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            })).ToArray();
            await Task.WhenAll(producers).ConfigureAwait(false);
            // The barrier runs after accepted callbacks and ensures safe, final snapshots of UI-owned distributions.
            var distributions = await queue.InvokeAsync(() =>
            {
                UiPulse.Stop();
                return (Latency: latency.Snapshot(), TimerGap: timerGaps.Snapshot(), TimerTicks: UiPulse.Ticks);
            }).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            watch.Stop();
            if (accepted != completed || pending != 0 || (distributions.TimerTicks == 0 && operatorCanceled == 0 && limitReached == 0))
            {
                throw new InvalidOperationException("Work was lost, remained pending, or the UI timer made no progress.");
            }

            monitoring.Cancel();
            await monitor.ConfigureAwait(false);
            process.Refresh();
            var after = ResourceSnapshot.Capture(process);
            peaks = peaks.Peak(after);
            return new LoadReport(DateTimeOffset.UtcNow, Environment.OSVersion.VersionString,
                System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount,
                System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
                System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported,
                options, watch.Elapsed.TotalSeconds, accepted, completed, peakPending, limitReached != 0, operatorCanceled != 0,
                accepted / watch.Elapsed.TotalSeconds, (process.TotalProcessorTime - cpuBefore).TotalSeconds / watch.Elapsed.TotalSeconds * 100,
                GC.GetTotalAllocatedBytes() - allocatedBefore,
                Enumerable.Range(0, 3).Select(generation => GC.CollectionCount(generation) - collectionsBefore[generation]).ToArray(),
                before, after, peaks, distributions.Latency, distributions.TimerGap, distributions.TimerTicks);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            stop.Cancel();
            monitoring.Cancel();
            await monitor.ConfigureAwait(false);
        }
    }

    private static void UpdateMaximum(ref long maximum, long value)
    {
        var previous = Interlocked.Read(ref maximum);
        while (value > previous)
        {
            var observed = Interlocked.CompareExchange(ref maximum, value, previous);
            if (observed == previous)
            {
                return;
            }

            previous = observed;
        }
    }

    private static async Task ObserveCompletedAsync(List<Task> tasks)
    {
        for (var index = tasks.Count - 1; index >= 0; index--)
        {
            if (tasks[index].IsCompleted)
            {
                await tasks[index].ConfigureAwait(false);
                tasks.RemoveAt(index);
            }
        }
    }
}

internal sealed record LoadOptions(int Seconds, int Rate, int Producers, int CallbackMicroseconds, int MaxPending, string Mode, string? Output)
{
    public static LoadOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || args[index] is not ("--seconds" or "--rate" or "--producers" or "--callback-us" or "--max-pending" or "--mode" or "--output") ||
                !values.TryAdd(args[index], args[index + 1]))
            {
                throw new ArgumentException("Expected unique option/value pairs; use --help for supported options.");
            }
        }

        int Number(string key, int fallback, int minimum, int maximum)
        {
            var number = values.TryGetValue(key, out var text) && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                ? parsed : values.ContainsKey(key) ? -1 : fallback;
            return number >= minimum && number <= maximum ? number : throw new ArgumentException($"{key} must be between {minimum} and {maximum}.");
        }

        var mode = values.GetValueOrDefault("--mode", "post");
        if (mode is not ("post" or "invoke"))
        {
            throw new ArgumentException("--mode must be post or invoke.");
        }

        return new LoadOptions(Number("--seconds", 30, 1, 604800), Number("--rate", 1000, 1, 1_000_000),
            Number("--producers", 4, 1, 64), Number("--callback-us", 100, 0, 100_000),
            Number("--max-pending", 4096, 16, 100_000), mode, values.GetValueOrDefault("--output"));
    }
}

internal sealed partial record ResourceSnapshot(long WorkingSetBytes, long PrivateBytes, int Handles, long ManagedHeapBytes,
    uint GdiObjects, uint UserObjects)
{
    public static ResourceSnapshot Capture(Process process)
    {
        // Open our query handle before counting handles, including in the baseline snapshot.
        var processHandle = process.Handle;
        return new(process.WorkingSet64, process.PrivateMemorySize64, process.HandleCount, GC.GetTotalMemory(false),
            GuiObjects(processHandle, 0), GuiObjects(processHandle, 1));
    }

    private static uint GuiObjects(nint processHandle, uint flags)
    {
        var count = GetGuiResources(processHandle, flags);
        var error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        return count == 0 && error != 0 ? throw new System.ComponentModel.Win32Exception(error) : count;
    }

    public ResourceSnapshot Peak(ResourceSnapshot other) => new(Math.Max(WorkingSetBytes, other.WorkingSetBytes),
        Math.Max(PrivateBytes, other.PrivateBytes), Math.Max(Handles, other.Handles), Math.Max(ManagedHeapBytes, other.ManagedHeapBytes),
        Math.Max(GdiObjects, other.GdiObjects), Math.Max(UserObjects, other.UserObjects));

    [System.Runtime.InteropServices.LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint GetGuiResources(nint process, uint flags);
}

internal sealed record DistributionReport(long Count, double MeanMs, double P50Ms, double P95Ms, double P99Ms, double MaxMs);

internal sealed record LoadReport(DateTimeOffset FinishedUtc, string OperatingSystem, string Architecture, int LogicalProcessors,
    string OperatingSystemArchitecture, string Runtime, bool DynamicCodeSupported,
    LoadOptions Options, double ElapsedSecondsIncludingDrain, long Accepted, long Completed, long PeakPending, bool BacklogLimitReached, bool OperatorCanceled,
    double CompletedPerSecondIncludingDrain, double CpuPercentOfOneCore, long AllocatedBytes, int[] GarbageCollections,
    ResourceSnapshot Before, ResourceSnapshot After, ResourceSnapshot SampledPeaks, DistributionReport DispatchLatency,
    DistributionReport UiTimerGap, long UiTimerTicks);

[JsonSerializable(typeof(LoadReport))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class ReportJsonContext : JsonSerializerContext;

// Bounded reservoir sampling: instrumentation does not retain one record per invocation during multi-day runs.
// Record and Snapshot are called exclusively on the UI thread.
internal sealed class Distribution
{
    private readonly long[] _samples = new long[32768];
    private readonly Random _random = new(12345);
    private long _count;
    private long _maximum;
    private double _sum;

    public void Record(long ticks)
    {
        _count++;
        _sum += ticks;
        _maximum = Math.Max(_maximum, ticks);
        var slot = _count <= _samples.Length ? _count - 1 : _random.NextInt64(_count);
        if (slot < _samples.Length)
        {
            _samples[slot] = ticks;
        }
    }

    public DistributionReport Snapshot()
    {
        var length = (int)Math.Min(_count, _samples.Length);
        var sorted = _samples.AsSpan(0, length).ToArray();
        Array.Sort(sorted);
        var scale = 1000.0 / Stopwatch.Frequency;
        double Percentile(double fraction) => length == 0 ? 0 : sorted[(int)Math.Ceiling(fraction * length) - 1] * scale;
        return new(_count, _count == 0 ? 0 : _sum / _count * scale, Percentile(0.50), Percentile(0.95), Percentile(0.99), _maximum * scale);
    }
}
