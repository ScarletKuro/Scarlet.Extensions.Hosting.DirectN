using System.Diagnostics;

namespace Scarlet.Extensions.Hosting.DirectN.Tests;

/// <summary>
/// Runs a ScenarioApp scenario in its own process (DirectN allows one Application per process) and collects its events.
/// Set SCARLET_SCENARIO_APP to run another build of it, e.g. the Native AOT published one.
/// </summary>
internal static class ScenarioRunner
{
    public const int HostFailedExitCode = 3;

    private const string ExecutableName = "Scarlet.Extensions.Hosting.DirectN.ScenarioApp.exe";

    public static ScenarioResult Run(string scenario)
    {
        var startInfo = new ProcessStartInfo(FindExecutable(), scenario)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = startInfo };
        var output = new List<string>();
        var lockObject = new Lock();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (lockObject)
                {
                    output.Add(e.Data);
                }
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (lockObject)
                {
                    output.Add(e.Data);
                }
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Scenario '{scenario}' did not exit.{Environment.NewLine}{string.Join(Environment.NewLine, output)}");
        }

        // Flushes the asynchronous output readers.
        process.WaitForExit();
        lock (lockObject)
        {
            return new ScenarioResult(scenario, process.ExitCode, [.. output]);
        }
    }

    private static string FindExecutable()
    {
        if (Environment.GetEnvironmentVariable("SCARLET_SCENARIO_APP") is { Length: > 0 } configured)
        {
            return configured;
        }

        // tests/<this project>/bin/<configuration>/<tfm>/ -> tests/ScenarioApp/bin/<configuration>/<tfm>/
        var baseDirectory = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var tfm = baseDirectory.Name;
        var configuration = baseDirectory.Parent!.Name;
        var testsDirectory = baseDirectory.Parent!.Parent!.Parent!.Parent!.FullName;
        var path = Path.Combine(testsDirectory, "Scarlet.Extensions.Hosting.DirectN.ScenarioApp", "bin", configuration, tfm, ExecutableName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("ScenarioApp not found; build the solution or set SCARLET_SCENARIO_APP.", path);
        }

        return path;
    }
}

internal sealed record ScenarioResult(string Scenario, int ExitCode, IReadOnlyList<string> Output)
{
    /// <summary>
    /// The reported events, in order, as "name|value".
    /// </summary>
    public IReadOnlyList<string> Events { get; } = [.. Output.Where(line => line.StartsWith("EVT|", StringComparison.Ordinal)).Select(line => line[4..])];

    public string Describe() => $"Scenario '{Scenario}' exited with {ExitCode}.{Environment.NewLine}{string.Join(Environment.NewLine, Output)}";

    public void AssertEvents(params string[] expected)
    {
        Assert.True(Events.SequenceEqual(expected), $"Expected events:{Environment.NewLine}{string.Join(Environment.NewLine, expected)}{Environment.NewLine}{Environment.NewLine}{Describe()}");
    }

    public void AssertExitCode(int expected) => Assert.True(ExitCode == expected, Describe());
}
