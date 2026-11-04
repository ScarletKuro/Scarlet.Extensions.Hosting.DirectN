using System.Collections.Concurrent;

namespace Scarlet.DirectN.Threading.Tests;

public class QueueConcurrencyTests
{
    [Fact]
    public async Task ConcurrentProducersCancellationAndCloseCompleteEveryInvocationAtMostOnce()
    {
        const int Producers = 4;
        const int ItemsPerProducer = 128;
        var timeout = TimeSpan.FromSeconds(30);
        var testToken = TestContext.Current.CancellationToken;
        for (var iteration = 0; iteration < 8; iteration++)
        {
            using var loop = new MessageLoopThread();
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(testToken);
            using var gate = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var operations = new ConcurrentBag<(int Index, Task Task)>();
            var runs = new int[Producers * ItemsPerProducer];
            var readyCount = 0;
            var wrongThread = 0;
            loop.Queue.Post(() =>
            {
                entered.SetResult();
                gate.Wait(timeout, testToken);
            });
            await entered.Task.WaitAsync(timeout, testToken);
            var producers = Enumerable.Range(0, Producers).Select(producer => Task.Run(async () =>
            {
                for (var item = 0; item < ItemsPerProducer; item++)
                {
                    if (item == 32)
                    {
                        if (Interlocked.Increment(ref readyCount) == Producers)
                        {
                            ready.SetResult();
                        }

                        await resume.Task.WaitAsync(timeout, testToken);
                    }

                    var index = producer * ItemsPerProducer + item;
                    var operationToken = item % 3 == 0 ? cancel.Token : testToken;
                    void RecordExecution()
                    {
                        Interlocked.Increment(ref runs[index]);
                        if (!loop.Queue.CheckAccess())
                        {
                            Interlocked.Increment(ref wrongThread);
                        }
                    }

                    var task = item % 2 == 0
                        ? loop.Queue.InvokeAsync(RecordExecution, operationToken)
                        : loop.Queue.InvokeAsync(async () =>
                        {
                            RecordExecution();
                            // A running callback must be able to finish after Close without needing the UI loop.
                            await Task.Delay(1, testToken).ConfigureAwait(false);
                        }, operationToken);
                    operations.Add((index, task));
                }
            }, testToken)).ToArray();
            try
            {
                await ready.Task.WaitAsync(timeout, testToken);
                // The first batch is accepted. Close follows it while the remaining producer work races with shutdown.
                loop.Queue.Post(loop.Queue.Close);
                cancel.Cancel();
                resume.SetResult();
                gate.Set();
                await Task.WhenAll(producers).WaitAsync(timeout, testToken);
                var completion = Task.WhenAll(operations.Select(operation => operation.Task));
                try
                {
                    await completion.WaitAsync(timeout, testToken);
                }
                catch (Exception) when (completion.IsCompleted)
                {
                    // Check every individual outcome below, including the exact expected failure type.
                }

                Assert.Equal(Producers * ItemsPerProducer, operations.Count);
                Assert.Equal(0, wrongThread);
                Assert.Contains(operations, operation => operation.Task.IsCompletedSuccessfully);
                Assert.Contains(operations, operation => operation.Task.IsCanceled);
                Assert.Contains(operations, operation => operation.Task.IsFaulted);
                foreach (var (index, task) in operations)
                {
                    Assert.True(task.IsCompleted);
                    Assert.Equal(task.IsCompletedSuccessfully ? 1 : 0, runs[index]);
                    if (task.Exception is { } error)
                    {
                        var failure = Assert.IsType<InvalidOperationException>(Assert.Single(error.InnerExceptions));
                        Assert.Equal(DirectNMessageQueue.QueueClosedException().Message, failure.Message);
                    }
                }
            }
            finally
            {
                resume.TrySetResult();
                gate.Set();
            }
        }
    }
}
