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

                    // On the UI thread itself, Send runs inline.
                    thread.SynchronizationContext.Post(_ => SendAndReport(thread.SynchronizationContext, "SendOnUiThread"), null);
                    sp.GetRequiredService<IHostApplicationLifetime>().StopApplication();
                });
                return true;

            case "exit-cancelled":
                services.AddDirectN(options =>
                {
                    options.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                    // An ApplicationExiting handler that vetoes every exit: the host stopping must still end the loop.
                    options.OnStartup = (_, context) => context.Application!.ApplicationExiting += (_, e) =>
                    {
                        Program.Report("ExitVetoed", Program.Where());
                        e.Cancel = true;
                    };
                    options.OnExit = (_, _) => Program.Report("OnExit", Program.Where());
                });
                OnStarted(services, sp => sp.GetRequiredService<IHostApplicationLifetime>().StopApplication());
                return true;

            case "canceled-during-init":
                AddDirectN(services, ShutdownMode.OnExplicitShutdown);
                services.AddDirectNComponent<SlowComponent>();
                services.AddHostedService(sp => new ContextPostService(sp.GetRequiredService<IDirectNThread>()));
                return true;

            case "directn-error-before-start":
                AddDirectN(services, ShutdownMode.OnExplicitShutdown);
                services.AddDirectNComponent(_ => new FaultyWindowComponent(failBeforeStart: true, throwOnDispose: false));
                return true;

            case "directn-error-after-start":
                AddDirectN(services, ShutdownMode.OnExplicitShutdown);
                services.AddDirectNComponent(_ => new FaultyWindowComponent(failBeforeStart: false, throwOnDispose: false));
                OnStarted(services, sp => sp.GetRequiredService<IDirectNThread>().SynchronizationContext.Post(_ =>
                {
                    Program.Report("FailingWindow", Program.Where());
                    global::DirectN.Functions.SendMessageW(FaultyWindowComponent.Window!.Handle, FaultyWindow.FailMessage, default, default);
                }, null));
                return true;

            case "cleanup-failures":
                services.AddDirectN(options =>
                {
                    options.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    options.OnExit = (_, _) =>
                    {
                        Program.Report("OnExit", Program.Where());
                        throw new InvalidOperationException("exit failure");
                    };
                });
                services.AddDirectNComponent<ProbeComponent>();
                services.AddDirectNComponent(_ => new FaultyWindowComponent(failBeforeStart: false, throwOnDispose: true));
                services.AddDirectNComponent<ThrowingDisposeComponent>();
                OnStarted(services, sp => sp.GetRequiredService<IHostApplicationLifetime>().StopApplication());
                return true;

            case "unhandled-handler-throws":
                AddDirectN(services, ShutdownMode.OnExplicitShutdown);
                OnStarted(services, sp =>
                {
                    var thread = sp.GetRequiredService<IDirectNThread>();
                    thread.UnhandledException += (_, _) => throw new FormatException("handler failure");
                    thread.SynchronizationContext.Post(_ =>
                    {
                        Program.Report("Throwing", Program.Where());
                        throw new InvalidOperationException("posted failure");
                    }, null);
                });
                return true;

            case "shutdown-timeout":
                services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(1));
                services.AddDirectN(options =>
                {
                    options.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                    // Cleanup that outlives the shutdown timeout: the host must give up waiting, not hang.
                    options.OnExit = (_, _) =>
                    {
                        Program.Report("OnExitStarted", Program.Where());
                        Thread.Sleep(TimeSpan.FromSeconds(4));
                        Program.Report("OnExitFinished", Program.Where());
                    };
                });
                OnStarted(services, sp =>
                {
                    s_stopRequested = System.Diagnostics.Stopwatch.StartNew();
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

            case "await-after-exit":
                AddDirectN(services, ShutdownMode.OnExplicitShutdown);
                services.AddHostedService<SlowStopService>();
                OnStarted(services, sp => sp.GetRequiredService<IDirectNThread>().SynchronizationContext.Post(_ => _ = SlowStopService.AwaitStopOnUiThread(sp), null));
                return true;

            case "modal-error":
                AddDirectN(services, ShutdownMode.OnExplicitShutdown);
                services.AddDirectNComponent(_ => new FaultyWindowComponent(failBeforeStart: false, throwOnDispose: false));
                OnStarted(services, sp =>
                {
                    var context = sp.GetRequiredService<IDirectNThread>().SynchronizationContext;
                    context.Post(_ =>
                    {
                        // Both run inside the message box's modal loop, which discards DirectN's request to quit.
                        context.Post(_ => global::DirectN.Functions.SendMessageW(FaultyWindowComponent.Window!.Handle, FaultyWindow.FailMessage, default, default), null);
                        CloseModalLater(context);
                        ShowModal();
                        Program.Report("ModalClosed", Program.Where());
                    }, null);
                });
                return true;

            case "fatal-dialog":
                services.AddDirectN(options =>
                {
                    options.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    options.ShowFatalErrorDialog = true;
                    options.OnStartup = (_, _) => StartDialogCloser(Functions.GetCurrentThreadId());
                    options.OnExit = (_, _) => Program.Report("OnExit", Program.Where());
                });
                services.AddDirectNComponent(_ => new FaultyWindowComponent(failBeforeStart: true, throwOnDispose: false));
                return true;

            case "onexit-modal":
                services.AddDirectN(options =>
                {
                    options.ShutdownMode = ShutdownMode.OnLastWindowClose;
                    options.OnExit = (_, _) =>
                    {
                        // The application still has a window when the host stops it: closing it must not answer this box.
                        var context = SynchronizationContext.Current!;
                        CloseModalLater(context);
                        ShowModal();
                        Program.Report("OnExitModal", s_modalClosedByScenario ? "answered-by-user" : "closed-by-itself");
                    };
                });
                services.AddDirectNComponent<WindowComponent>();
                OnStarted(services, sp => sp.GetRequiredService<IHostApplicationLifetime>().StopApplication());
                return true;

            case "exit-before-started":
                services.AddDirectN(options =>
                {
                    options.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    options.OnStartup = (sp, _) =>
                    {
                        // Runs in the loop before the application has been reported as started.
                        var thread = sp.GetRequiredService<IDirectNThread>();
                        thread.SynchronizationContext.Post(_ =>
                        {
                            Program.Report("HandleApplicationExit", Program.Where());
                            thread.HandleApplicationExit();
                        }, null);
                    };
                    options.OnExit = (_, _) => Program.Report("OnExit", Program.Where());
                });
                return true;

            case "ui-execution-context":
                services.AddDirectN(options =>
                {
                    options.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    options.OnStartup = (sp, _) =>
                    {
                        Program.Report("OnStartup", Program.HostStartValue.Value);
                        var thread = sp.GetRequiredService<IDirectNThread>();
                        using (ExecutionContext.SuppressFlow())
                        {
                            thread.SynchronizationContext.Post(_ =>
                            {
                                Program.Report("FlowSuppressedPost", Program.HostStartValue.Value);
                                thread.HandleApplicationExit();
                            }, null);
                        }
                    };
                });
                return true;

            case "environment-exit":
                services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(10));
                AddDirectN(services, ShutdownMode.OnExplicitShutdown);
                services.AddHostedService<SlowStopService>();
                OnStarted(services, sp => sp.GetRequiredService<IDirectNThread>().SynchronizationContext.Post(_ =>
                {
                    Program.Report("EnvironmentExit", Program.Where());
                    Environment.Exit(5);
                }, null));
                return true;

            case "session-end":
                AddDirectN(services, ShutdownMode.OnExplicitShutdown);
                services.AddDirectNComponent<ProbeComponent>();
                services.AddHostedService<SlowStopService>();
                OnStarted(services, sp =>
                {
                    var context = sp.GetRequiredService<IDirectNThread>().SynchronizationContext;
                    uint uiThreadId = 0;
                    context.Send(_ => uiThreadId = Functions.GetCurrentThreadId(), null);
                    var window = FindSessionEndWindow(uiThreadId);

                    // What Windows sends when the user logs off. The process may be terminated once WM_ENDSESSION returns.
                    const uint WM_QUERYENDSESSION = 0x0011, WM_ENDSESSION = 0x0016;
                    var allowed = Functions.SendMessageW(new HWND(window), WM_QUERYENDSESSION, default, default).Value;
                    Program.Report("QueryEndSession", allowed.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    Functions.SendMessageW(new HWND(window), WM_ENDSESSION, new WPARAM { Value = 1 }, default);
                    Program.Report("EndSessionReturned", SlowStopService.Stopped ? "host-stopped" : "host-still-running");
                });
                return true;

            default:
                return false;
        }
    }

    private static bool s_modalClosedByScenario;

    private static void StartDialogCloser(uint uiThreadId)
    {
        var closer = new Thread(() =>
        {
            while (true)
            {
                s_modal = 0;
                EnumThreadWindows(uiThreadId, &FindDialog, 0);
                if (s_modal != 0)
                {
                    Program.Report("ClosingDialog", Program.Where());
                    const uint WM_COMMAND = 0x0111, TDM_CLICK_BUTTON = 0x0400 + 102;
                    const nint IDOK = 1;
                    Functions.PostMessageW(new HWND(s_modal), TDM_CLICK_BUTTON, new WPARAM { Value = (nuint)IDOK }, default);
                    Functions.PostMessageW(new HWND(s_modal), WM_COMMAND, new WPARAM { Value = (nuint)IDOK }, default);
                    return;
                }

                Thread.Sleep(20);
            }
        })
        {
            IsBackground = true,
        };
        closer.Start();
    }

    private static nint FindSessionEndWindow(uint uiThreadId)
    {
        s_sessionWindow = 0;
        EnumThreadWindows(uiThreadId, &FindSessionWindow, 0);
        return s_sessionWindow != 0 ? s_sessionWindow : throw new InvalidOperationException("No session end window.");
    }

    private static nint s_sessionWindow;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int FindSessionWindow(nint hwnd, nint lParam)
    {
        var className = stackalloc char[128];
        var length = GetClassNameW(hwnd, className, 128);
        if (new ReadOnlySpan<char>(className, length).StartsWith("Scarlet.Extensions.Hosting.DirectN.SessionEnd."))
        {
            s_sessionWindow = hwnd;
            return 0;
        }

        return 1;
    }

    public static void AfterRun(string scenario, IDirectNThread thread)
    {
        if (scenario == "post-after-exit")
        {
            thread.SynchronizationContext.Post(_ => Program.Report("RanAfterExit"), null);
            Program.Report("PostAfterExit", "no-throw");
        }

        if (scenario == "shutdown-timeout")
        {
            // ShutdownTimeout (1 s) once: disposing the host doesn't wait for the UI thread again after StoppingAsync gave up.
            Program.Report("StoppedWithinTimeout", s_stopRequested!.Elapsed < TimeSpan.FromSeconds(2.5) ? "yes" : $"no ({s_stopRequested.Elapsed})");
        }

        if (scenario == "send")
        {
            Program.Report("SendDuringExitFinished", s_sendDuringExit!.Wait(TimeSpan.FromSeconds(10)) ? "yes" : "hung");
            SendAndReport(thread.SynchronizationContext, "SendAfterExit");
        }
    }

    private static Task? s_sendDuringExit;
    private static System.Diagnostics.Stopwatch? s_stopRequested;

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

    /// <summary>
    /// Closes the message box after a while, so it only closes earlier if something else closes it.
    /// </summary>
    private static void CloseModalLater(SynchronizationContext context) =>
        _ = Task.Delay(500).ContinueWith(_ => context.Post(CloseModal, context), TaskScheduler.Default);

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
        s_modalClosedByScenario = true;
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
