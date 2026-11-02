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
    public void SendAlwaysReturnsBeforeDuringAndAfterTheUiThreadRuns()
    {
        var result = ScenarioRunner.Run("send");

        result.AssertExitCode(0);
        result.AssertEvents(
            "SendBeforeStartRejected|",
            "SendWhileRunningRan|ui",
            "SendException|sent failure",
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
