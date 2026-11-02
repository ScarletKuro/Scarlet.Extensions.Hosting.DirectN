namespace Scarlet.Extensions.Hosting.DirectN.Tests;

/// <summary>
/// End-to-end lifecycle tests: each runs a real host with a DirectN application in a ScenarioApp process.
/// </summary>
public class ScenarioTests
{
    [Fact]
    public void HostStopRunsStartupComponentsAndCleanupOnTheUiThreadInOrder()
    {
        var result = ScenarioRunner.Run("stop");

        result.AssertExitCode(0);
        result.AssertEvents(
            "OnStartup|ui:app",
            "ComponentInit|ui",
            "Started|running",
            "Posted|ui",
            "OnExit|ui",
            "ComponentDispose|ui",
            "HostStopped|not-running");
    }

    [Fact]
    public void ClosingTheLastWindowStopsTheHost()
    {
        var result = ScenarioRunner.Run("window-close");

        result.AssertExitCode(0);
        result.AssertEvents(
            "OnStartup|ui:app",
            "WindowCreated|ui",
            "ClosingWindow|ui",
            "OnExit|ui",
            "HostStopped|not-running");
    }

    [Fact]
    public void HandleApplicationExitFromAnotherThreadStopsTheHost()
    {
        var result = ScenarioRunner.Run("handle-exit");

        result.AssertExitCode(0);
        result.AssertEvents(
            "OnStartup|ui:app",
            "ComponentInit|ui",
            "HandleApplicationExit|other",
            "OnExit|ui",
            "ComponentDispose|ui",
            "HostStopped|not-running");
    }

    [Fact]
    public void ComponentFailureFailsHostStartupAndDisposesComponentsInReverseOrder()
    {
        var result = ScenarioRunner.Run("startup-failure");

        result.AssertExitCode(ScenarioRunner.HostFailedExitCode);
        result.AssertEvents(
            "OnStartup|ui:app",
            "ComponentInit|ui",
            "FailingComponentInit|ui",
            "OnExit|ui",
            "FailingComponentDispose|ui",
            "ComponentDispose|ui",
            "HostFailed|InvalidOperationException: boom");
    }

    [Fact]
    public void CleanupOutlivingTheShutdownTimeoutDoesntHangTheHost()
    {
        var result = ScenarioRunner.Run("shutdown-timeout");

        result.AssertExitCode(0);
        result.AssertEvents(
            "OnExitStarted|ui",
            "HostStopped|not-running",
            "StoppedWithinTimeout|yes");
    }

    [Fact]
    public void HostStopEndsTheApplicationEvenWhenExitingIsVetoed()
    {
        var result = ScenarioRunner.Run("exit-cancelled");

        result.AssertExitCode(0);
        result.AssertEvents(
            "ExitVetoed|ui",
            "OnExit|ui",
            "HostStopped|not-running");
    }

    [Fact]
    public void StartupCancelledDuringInitializationNeverRunsTheMessageLoop()
    {
        var result = ScenarioRunner.Run("canceled-during-init");

        result.AssertExitCode(0);

        // No PrestartPost: the work posted before startup never ran, the loop never started.
        result.AssertEvents(
            "OnStartup|ui:app",
            "SlowInitStarted|ui",
            "SlowInitFinished|ui",
            "OnExit|ui",
            "SlowComponentDispose|ui",
            "StartCanceled|not-running");
    }

    [Fact]
    public void DirectNErrorBeforeTheLoopRunsFailsHostStartupWithTheError()
    {
        var result = ScenarioRunner.Run("directn-error-before-start");

        result.AssertExitCode(ScenarioRunner.HostFailedExitCode);
        result.AssertEvents(
            "OnStartup|ui:app",
            "FaultyWindowCreated|ui",
            "OnExit|ui",
            "HostFailed|InvalidOperationException: window failure");
    }

    [Fact]
    public void DirectNErrorAfterStartIsLoggedAndStopsTheHostWithoutADialog()
    {
        var result = ScenarioRunner.Run("directn-error-after-start");

        result.AssertExitCode(0);
        result.AssertEvents(
            "OnStartup|ui:app",
            "FaultyWindowCreated|ui",
            "FailingWindow|ui",
            "OnExit|ui",
            "HostStopped|not-running");
        Assert.Contains(result.Output, line => line.Contains("DirectN reported an error", StringComparison.Ordinal));
    }

    [Fact]
    public void CleanupFailuresAreLoggedAndEverythingElseIsStillCleanedUp()
    {
        var result = ScenarioRunner.Run("cleanup-failures");

        result.AssertExitCode(0);
        result.AssertEvents(
            "ComponentInit|ui",
            "FaultyWindowCreated|ui",
            "OnExit|ui",
            "ThrowingDispose|ui",
            "ComponentDispose|ui",
            "HostStopped|not-running");
        Assert.Contains(result.Output, line => line.Contains("window dispose failure", StringComparison.Ordinal));
        Assert.Contains(result.Output, line => line.Contains("exit failure", StringComparison.Ordinal));
        Assert.Contains(result.Output, line => line.Contains("component dispose failure", StringComparison.Ordinal));
    }

    [Fact]
    public void UnhandledExceptionHandlerThatThrowsTerminatesTheProcessWithBothExceptions()
    {
        var result = ScenarioRunner.Run("unhandled-handler-throws");

        Assert.True(result.ExitCode is not 0 and not ScenarioRunner.HostFailedExitCode, result.Describe());
        Assert.Contains("Throwing|ui", result.Events);
        Assert.Contains(result.Output, line => line.Contains("posted failure", StringComparison.Ordinal) && line.Contains("handler failure", StringComparison.Ordinal));
    }

    [Fact]
    public void PostsRunInTheirCallersExecutionContextAlsoWhenPostedBeforeStart()
    {
        var result = ScenarioRunner.Run("execution-context");

        result.AssertExitCode(0);
        result.AssertEvents(
            "OnStartup|ui:app",
            "PrestartPost|prestart-caller",
            "RunningPost|running-caller",
            "LaterPost|",
            "OnExit|ui",
            "HostStopped|not-running");
    }

    [Fact]
    public void SendAlwaysReturnsBeforeDuringAndAfterTheUiThreadRuns()
    {
        var result = ScenarioRunner.Run("send");

        result.AssertExitCode(0);
        result.AssertEvents(
            "SendBeforeStartRejected|",
            "SendWhileRunningRan|ui",
            "SendException|sent failure",
            "SendOnUiThreadRan|ui",
            "SendDuringExitRejected|",
            "HostStopped|not-running",
            "SendDuringExitFinished|yes",
            "SendAfterExitRejected|");
    }

    [Fact]
    public void LaterStartupFailureCleansUpTheApplicationBeforeItsDependenciesAreDisposed()
    {
        var result = ScenarioRunner.Run("started-failure");

        result.AssertExitCode(ScenarioRunner.HostFailedExitCode);
        result.AssertEvents(
            "OnStartup|ui:app",
            "DependentComponentInit|ui",
            "OnExit|ui",
            "DependentComponentDispose|ui:dependency-alive",
            "DependencyDisposed|other",
            "HostFailed|InvalidOperationException: started failure");
    }

    [Fact]
    public void WorkPostedBeforeTheUiThreadExistsRunsOnceStarted()
    {
        var result = ScenarioRunner.Run("prestart-post");

        result.AssertExitCode(0);
        result.AssertEvents(
            "PrestartPosting|not-running",
            "OnStartup|ui:app",
            "ComponentInit|ui",
            "PrestartPostRan|ui",
            "OnExit|ui",
            "ComponentDispose|ui",
            "HostStopped|not-running");
    }

    [Fact]
    public void HandledUnhandledExceptionKeepsTheApplicationRunning()
    {
        var result = ScenarioRunner.Run("unhandled-handled");

        result.AssertExitCode(0);
        result.AssertEvents(
            "OnStartup|ui:app",
            "UnhandledException|ui:posted failure",
            "StillRunning|ui",
            "OnExit|ui",
            "HostStopped|not-running");
    }

    [Fact]
    public void UnhandledExceptionIsLoggedAndTerminatesTheProcess()
    {
        var result = ScenarioRunner.Run("unhandled-crash");

        Assert.True(result.ExitCode is not 0 and not ScenarioRunner.HostFailedExitCode, result.Describe());
        Assert.Contains("Throwing|ui", result.Events);
        Assert.DoesNotContain(result.Events, e => e.StartsWith("HostStopped", StringComparison.Ordinal));
        Assert.Contains(result.Output, line => line.Contains("Unhandled exception on the DirectN UI thread", StringComparison.Ordinal));
    }

    [Fact]
    public void PostedWorkRunsInsideAModalLoop()
    {
        var result = ScenarioRunner.Run("modal-post");

        result.AssertExitCode(0);
        result.AssertEvents(
            "OnStartup|ui:app",
            "RanDuringModal|ui",
            "ModalClosed|ui",
            "OnExit|ui",
            "HostStopped|not-running");
    }

    [Fact]
    public void PostingAfterTheHostIsDisposedDoesNotThrow()
    {
        var result = ScenarioRunner.Run("post-after-exit");

        result.AssertExitCode(0);
        result.AssertEvents(
            "OnStartup|ui:app",
            "OnExit|ui",
            "HostStopped|not-running",
            "PostAfterExit|no-throw");
    }

    [Fact]
    public void JoinableTaskFactorySwitchesToTheUiThreadBeforeAndAfterItStarts()
    {
        var result = ScenarioRunner.Run("threading");

        result.AssertExitCode(0);
        result.AssertEvents(
            "EarlySwitch|ui",
            "SwitchedToMain|ui",
            "RunOnMain|ui",
            "AsyncLazy|ui:ui:1",
            "BackToBackground|other",
            "HostStopped|not-running");
    }

    [Fact]
    public void TrayIconIsCreatedAndDisposedOnTheUiThread()
    {
        var result = ScenarioRunner.Run("tray");

        result.AssertExitCode(0);
        Assert.Equal(
            ["OnStartup|ui:app", "TrayInit|ui", "OnExit|ui", "TrayDispose|ui", "HostStopped|not-running"],
            result.Events.Where(e => !e.StartsWith("ProductVersion|", StringComparison.Ordinal)));
        Assert.Contains(result.Events, e => e.StartsWith("ProductVersion|1.0.0", StringComparison.Ordinal));
    }
}
