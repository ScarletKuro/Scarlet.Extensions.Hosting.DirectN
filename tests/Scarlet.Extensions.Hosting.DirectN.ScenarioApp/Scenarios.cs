using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DirectN;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Scarlet.Extensions.Hosting.DirectN.TrayIcon;

using IServiceProvider = System.IServiceProvider;

namespace Scarlet.Extensions.Hosting.DirectN.ScenarioApp;

internal static unsafe partial class Scenarios
{
    private const string ModalCaption = "Scarlet scenario modal";

    private static nint s_modal;

    public static bool TryConfigure(string scenario, IServiceCollection services)
    {
        switch (scenario)
        {
            case "stop":
                AddDirectN(services, ShutdownMode.OnExplicitShutdown);
                services.AddDirectNComponent<ProbeComponent>();
                OnStarted(services, sp =>
                {
                    var thread = sp.GetRequiredService<IDirectNThread>();
                    Program.Report("Started", thread.Context.IsRunning ? "running" : "not-running");
                    thread.SynchronizationContext.Post(_ =>
                    {
                        Program.Report("Posted", Program.Where());
                        sp.GetRequiredService<IHostApplicationLifetime>().StopApplication();
                    }, null);
                });
                return true;

            case "window-close":
                AddDirectN(services, ShutdownMode.OnLastWindowClose);
                services.AddDirectNComponent<WindowComponent>();
                OnStarted(services, sp => sp.GetRequiredService<IDirectNThread>().SynchronizationContext.Post(_ =>
                {
                    Program.Report("ClosingWindow", Program.Where());
                    WindowComponent.Window!.Close();
                }, null));
                return true;

            case "handle-exit":
                AddDirectN(services, ShutdownMode.OnExplicitShutdown);
                services.AddDirectNComponent<ProbeComponent>();
                OnStarted(services, sp =>
                {
                    Program.Report("HandleApplicationExit", Program.Where());
                    sp.GetRequiredService<IDirectNThread>().HandleApplicationExit();
                });
                return true;

            case "startup-failure":
                AddDirectN(services, ShutdownMode.OnExplicitShutdown);
                services.AddDirectNComponent<ProbeComponent>();
                services.AddDirectNComponent<FailingComponent>();
                return true;

            case "prestart-post":
                AddDirectN(services, ShutdownMode.OnExplicitShutdown);
                services.AddDirectNComponent<ProbeComponent>();
                services.AddHostedService<PrestartPostService>();
                // Stopping from the posted work itself would race with the host still finishing its startup.
                OnStarted(services, sp => sp.GetRequiredService<IHostApplicationLifetime>().StopApplication());
                return true;

            case "unhandled-handled":
                AddDirectN(services, ShutdownMode.OnExplicitShutdown);
                OnStarted(services, sp =>
                {
                    var thread = sp.GetRequiredService<IDirectNThread>();
                    thread.UnhandledException += (_, e) =>
                    {
                        Program.Report("UnhandledException", $"{Program.Where()}:{e.Exception.Message}");
                        e.Handled = true;
                    };
                    thread.SynchronizationContext.Post(_ => throw new InvalidOperationException("posted failure"), null);
                    thread.SynchronizationContext.Post(_ =>
                    {
                        Program.Report("StillRunning", Program.Where());
                        sp.GetRequiredService<IHostApplicationLifetime>().StopApplication();
                    }, null);
                });
                return true;

            case "unhandled-crash":
                AddDirectN(services, ShutdownMode.OnExplicitShutdown);
                OnStarted(services, sp => sp.GetRequiredService<IDirectNThread>().SynchronizationContext.Post(_ =>
                {
                    Program.Report("Throwing", Program.Where());
                    throw new InvalidOperationException("unhandled posted failure");
                }, null));
                return true;

            case "modal-post":
                AddDirectN(services, ShutdownMode.OnExplicitShutdown);
                OnStarted(services, sp =>
                {
                    var context = sp.GetRequiredService<IDirectNThread>().SynchronizationContext;
                    context.Post(_ =>
                    {
                        // Queued before the message box opens: must run inside its modal loop.
                        context.Post(CloseModal, context);
                        ShowModal();
                        Program.Report("ModalClosed", Program.Where());
                        sp.GetRequiredService<IHostApplicationLifetime>().StopApplication();
                    }, null);
                });
                return true;

            case "send":
                services.AddDirectN(options =>
                {
                    options.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                    // A Send queued while the loop no longer runs (OnExit) must be released by the shutdown, not hang.
                    options.OnExit = (sp, _) =>
                    {
                        var context = sp.GetRequiredService<IDirectNThread>().SynchronizationContext;
                        s_sendDuringExit = Task.Run(() => SendAndReport(context, "SendDuringExit"));
                        Thread.Sleep(300);
                    };
                });
                services.AddHostedService(sp => new SendBeforeStartService(sp.GetRequiredService<IDirectNThread>()));
                OnStarted(services, sp =>
                {
                    var thread = sp.GetRequiredService<IDirectNThread>();
                    SendAndReport(thread.SynchronizationContext, "SendWhileRunning");
                    try
                    {
                        thread.SynchronizationContext.Send(_ => throw new FormatException("sent failure"), null);
                    }
                    catch (FormatException ex)
                    {
                        Program.Report("SendException", ex.Message);
                    }

                    sp.GetRequiredService<IHostApplicationLifetime>().StopApplication();
                });
                return true;

            case "execution-context":
                AddDirectN(services, ShutdownMode.OnExplicitShutdown);
                services.AddHostedService(sp => new ContextPostService(sp.GetRequiredService<IDirectNThread>()));
                OnStarted(services, sp =>
                {
                    var context = sp.GetRequiredService<IDirectNThread>().SynchronizationContext;
                    ContextPostService.Request.Value = "running-caller";
                    context.Post(_ =>
                    {
                        Program.Report("RunningPost", ContextPostService.Request.Value);
                        ContextPostService.Request.Value = "left-by-running";
                    }, null);
                    ContextPostService.Request.Value = null;
                    context.Post(_ =>
                    {
                        Program.Report("LaterPost", ContextPostService.Request.Value);
                        sp.GetRequiredService<IHostApplicationLifetime>().StopApplication();
                    }, null);
                });
                return true;

            case "started-failure":
                AddDirectN(services, ShutdownMode.OnExplicitShutdown);
                services.AddSingleton<GuardedDependency>();
                services.AddDirectNComponent<DependentComponent>();
                services.AddHostedService<FailingStartedService>();
                return true;

            case "post-after-exit":
                AddDirectN(services, ShutdownMode.OnExplicitShutdown);
                OnStarted(services, sp => sp.GetRequiredService<IDirectNThread>().HandleApplicationExit());
                return true;

            case "threading":
                ThreadingScenario.Configure(services);
                return true;

            case "tray":
                AddDirectN(services, ShutdownMode.OnExplicitShutdown);
                services.AddDirectNTrayIcon<ScenarioTrayIcon>();
                OnStarted(services, sp => sp.GetRequiredService<IDirectNThread>().HandleApplicationExit());
                return true;

            default:
                return false;
        }
    }

    public static void AfterRun(string scenario, IDirectNThread thread)
    {
        if (scenario == "post-after-exit")
        {
            thread.SynchronizationContext.Post(_ => Program.Report("RanAfterExit"), null);
            Program.Report("PostAfterExit", "no-throw");
        }

        if (scenario == "send")
        {
            Program.Report("SendDuringExitFinished", s_sendDuringExit!.Wait(TimeSpan.FromSeconds(10)) ? "yes" : "hung");
            SendAndReport(thread.SynchronizationContext, "SendAfterExit");
        }
    }

    private static Task? s_sendDuringExit;

    internal static void SendAndReport(SynchronizationContext context, string name)
    {
        try
        {
            context.Send(_ => Program.Report($"{name}Ran", Program.Where()), null);
        }
        catch (InvalidOperationException)
        {
            Program.Report($"{name}Rejected");
        }
    }

    private static void AddDirectN(IServiceCollection services, ShutdownMode shutdownMode) =>
        services.AddDirectN(options =>
        {
            options.ShutdownMode = shutdownMode;
            options.OnStartup = (_, context) => Program.Report("OnStartup", $"{Program.Where()}:{(context.Application is null ? "no-app" : "app")}");
            options.OnExit = (_, _) => Program.Report("OnExit", Program.Where());
        });

    private static void OnStarted(IServiceCollection services, Action<IServiceProvider> action) =>
        services.AddHostedService(sp => new OnStartedService(sp.GetRequiredService<IHostApplicationLifetime>(), sp, action));

    private static void ShowModal()
    {
        fixed (char* text = "Closed by the scenario", caption = ModalCaption)
        {
            Functions.MessageBoxW(default, new PWSTR { Value = (nint)text }, new PWSTR { Value = (nint)caption }, MESSAGEBOX_STYLE.MB_OK);
        }
    }

    private static void CloseModal(object? state)
    {
        // The message box of this thread (so a box of another process can never match).
        s_modal = 0;
        EnumThreadWindows(Functions.GetCurrentThreadId(), &FindDialog, 0);
        if (s_modal == 0)
        {
            // Not created yet: try again from the queue (which keeps running inside the modal loop).
            Thread.Sleep(10);
            ((SynchronizationContext)state!).Post(CloseModal, state);
            return;
        }

        Program.Report("RanDuringModal", Program.Where());
        const nint IDOK = 1;
        EndDialog(s_modal, IDOK);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int FindDialog(nint hwnd, nint lParam)
    {
        var className = stackalloc char[16];
        var length = GetClassNameW(hwnd, className, 16);
        if (new ReadOnlySpan<char>(className, length).SequenceEqual("#32770"))
        {
            s_modal = hwnd;
            return 0;
        }

        return 1;
    }

    [LibraryImport("user32.dll")]
    private static partial int EnumThreadWindows(uint threadId, delegate* unmanaged[Stdcall]<nint, nint, int> callback, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial int GetClassNameW(nint hwnd, char* className, int maxCount);

    [LibraryImport("user32.dll")]
    private static partial int EndDialog(nint hwnd, nint result);
}
