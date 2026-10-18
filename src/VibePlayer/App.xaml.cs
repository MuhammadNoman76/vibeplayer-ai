using System.Windows;
using System.Windows.Threading;
using VibePlayer.Core;
using VibePlayer.Views;

namespace VibePlayer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "UI thread");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error(args.ExceptionObject as Exception, "AppDomain");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Task");
            args.SetObserved();
        };

        // Run by the build: pre-build VLC's plugin cache, then exit without showing a window.
        if (e.Args.Any(a => string.Equals(a, "--build-plugin-cache", StringComparison.OrdinalIgnoreCase)))
        {
            Shutdown(PlayerEngine.BuildPluginCache() ? 0 : 1);
            return;
        }

        // Testing the AI filter: scan a file without a window, write a JSON report, exit.
        if (e.Args.Any(a => string.Equals(a, Core.Ai.AiHeadless.Switch, StringComparison.OrdinalIgnoreCase)))
        {
            Shutdown(Core.Ai.AiHeadless.Run(e.Args));
            return;
        }

        // Screenshots and checks: render the "Scenes to skip" window off-screen with example scenes, exit.
        if (e.Args.Any(a => string.Equals(a, Views.ScenesCapture.Switch, StringComparison.OrdinalIgnoreCase)))
        {
            Shutdown(Views.ScenesCapture.Run(e.Args));
            return;
        }

        try
        {
            Launch(InstanceManager.Normalize(e.Args));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Startup");
            MessageBox.Show(AppInfo.Name + " couldn't start:\n\n" + ex.Message, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void Launch(string[] args)
    {
        // Explorer multi-select: one process leads, the others hand over their files and quit.
        if (!InstanceManager.TryLeadLaunch(args))
        {
            Shutdown();
            return;
        }

        var settings = AppSettings.Load();
        var files = args.ToList();
        var useOpenWindow = false;
        var open = InstanceManager.FindOpenWindow();

        if (open is { } existing)
        {
            var idle = existing.Title == AppInfo.Name; // nothing playing there yet
            switch (settings.InstanceMode)
            {
                case InstanceMode.UseExisting:
                    useOpenWindow = true;
                    break;
                case InstanceMode.NewWindow:
                    break;
                default:
                    if (idle && files.Count > 0)
                    {
                        useOpenWindow = true;
                        break;
                    }

                    ShutdownMode = ShutdownMode.OnExplicitShutdown; // the prompt must not count as the main window
                    var prompt = new InstanceChoiceDialog(files);
                    if (prompt.ShowDialog() != true)
                    {
                        InstanceManager.EndLaunch();
                        Shutdown();
                        return;
                    }
                    files = prompt.Files;
                    useOpenWindow = prompt.UseOpenWindow;
                    if (prompt.Remember)
                    {
                        settings.InstanceMode = useOpenWindow ? InstanceMode.UseExisting : InstanceMode.NewWindow;
                        settings.Save();
                    }
                    break;
            }

            if (useOpenWindow)
            {
                var pid = existing.Pid;
                InstanceManager.SetLaunchHandler(more => { lock (files) files.AddRange(more); });
                string[] batch;
                lock (files) batch = files.ToArray();
                if (InstanceManager.SendToWindow(pid, batch))
                {
                    // Late followers from a multi-select still go to the same window.
                    InstanceManager.SetLaunchHandler(more => InstanceManager.SendToWindow(pid, more));
                    Thread.Sleep(1200);
                    InstanceManager.EndLaunch();
                    Shutdown();
                    return;
                }
                // The other window went away - open our own instead.
            }
        }

        ShutdownMode = ShutdownMode.OnMainWindowClose;
        string[] startup;
        lock (files) startup = files.ToArray();
        var window = new MainWindow(settings, startup);
        MainWindow = window;

        InstanceManager.ListenAsWindow(a => Dispatcher.BeginInvoke(() => window.OpenFromAnotherInstance(a)));
        InstanceManager.SetLaunchHandler(a => Dispatcher.BeginInvoke(() => window.OpenFromAnotherInstance(a)));
        window.Show();

        // An update installer from an earlier session (it may still be finishing; retried for a while).
        if (AppInfo.UpdatesEnabled) _ = Task.Run(Updater.CleanUpDownloadsAsync);

        var endBatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        endBatch.Tick += (_, _) =>
        {
            endBatch.Stop();
            InstanceManager.EndLaunch();
        };
        endBatch.Start();
    }
}
