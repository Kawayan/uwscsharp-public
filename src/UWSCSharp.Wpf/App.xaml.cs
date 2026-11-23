using System.IO;
using System.Text;
using System.Windows;
using UWSCSharp.Core;
using UWSCSharp.IO;

namespace UWSCSharp.Wpf;

/// <summary>
/// WPF版の起動を管理します。
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// 通常起動と診断用検査の起動を切り替えます。
    /// </summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args is ["--scheduler-worker", .. string[] workerOptions])
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);
            int result = 1;
            try
            {
                result = await SchedulerWorkerEntry.RunAsync(workerOptions);
            }
            catch (Exception error)
            {
                ReportCommandLineError(error);
            }
            finally
            {
                Shutdown(result);
            }
            return;
        }

        if (UWSCSharp.Windows.WindowsAudioHost.TryRun(e.Args, out int audioExit))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(audioExit);
            return;
        }

        if (UWSCSharp.Windows.WindowsCommandHost.TryRun(e.Args, out int commandExit))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(commandExit);
            return;
        }

        if (e.Args is [string command, ..]
            && VerificationCommands.TryGetValue(command,
                out VerificationCommand verification)
            && (verification.RejectInvalidArguments
                || (e.Args.Length == verification.ArgumentCount
                    && Array.TrueForAll(e.Args, static value => value is not null))))
        {
            await RunVerificationAsync(e, verification);
            return;
        }

        if (string.Equals(Path.GetFileName(Environment.ProcessPath),
            "UWSCSharp.Association.exe", StringComparison.OrdinalIgnoreCase))
        {
            if (e.Args.Length > 0 && e.Args[0].StartsWith("--msi-association-",
                StringComparison.Ordinal))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                base.OnStartup(e);
                int code = MsiAssociationCommand.Run(e.Args, Environment.ProcessPath!);
                Shutdown(code);
                return;
            }

            base.OnStartup(e);
            var window = new MainWindow();
            MainWindow = window;
            await window.ApplyStartupWindowSettingsAsync();
            window.Show();
            try
            {
                if (e.Args.Length < 1 ||
                    !string.Equals(Path.GetExtension(e.Args[0]), ".uws",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        "関連付け起動には.uwsファイルを指定してください。");
                }

                var options = ScriptRunOptions.FromCommandLine(
                    e.Args, Environment.CurrentDirectory);
                string source = Source.ReadFile(options.SourcePath!);
                int result = await window.RunCommandLineAsync(source, options);
                if (result == 0 && window.IsVisible)
                {
                    window.Close();
                }
            }
            catch (Exception error)
            {
                window.ReportAssociationError(error);
            }

            return;
        }

        if (e.Args is ["--enable-logon-startup"] or ["--disable-logon-startup"])
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);
            try
            {
                LogonStartupRegistration.SetEnabled(e.Args[0] == "--enable-logon-startup");
                Shutdown(0);
            }
            catch (Exception error)
            {
                ReportCommandLineError(error);
                Shutdown(1);
            }

            return;
        }

        string? interactiveFile = e.Args is ["--single-instance", string file]
            && string.Equals(Path.GetExtension(file), ".uws", StringComparison.OrdinalIgnoreCase)
            ? file : null;
        bool runRequested = e.Args is ["--single-instance", "--run", ..];
        InteractiveInstance.RunRequest? run = null;
        if (runRequested)
        {
            try
            {
                var options = ScriptRunOptions.FromCommandLine(e.Args[2..],
                    Environment.CurrentDirectory);
                string path = options.SourcePath!;
                if (!Path.GetExtension(path).Equals(".uws", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("--run には .uws ファイルを指定してください。");
                }

                run = new InteractiveInstance.RunRequest(path, [.. options.Arguments]);
            }
            catch (ArgumentException error)
            {
                ReportCommandLineError(error);
                Shutdown(2);
                return;
            }
        }

        bool windowOptions = e.Args.All(argument => argument is
            "--single-instance" or "--start-in-tray" or "--minimize-to-tray")
            && e.Args.Distinct().Count() == e.Args.Length;
        bool singleInstance = interactiveFile is not null || runRequested
            || (windowOptions && e.Args.Contains("--single-instance"));
        bool startInTray = windowOptions && e.Args.Contains("--start-in-tray");
        bool minimizeToTray = windowOptions && e.Args.Contains("--minimize-to-tray");
        if (!windowOptions && !singleInstance)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);
            int result = 1;
            try
            {
                try
                {
                    Console.OutputEncoding = new UTF8Encoding(false);
                }
                catch (IOException)
                {
                    // コンソールを持たないGUI起動でも実行を続けます。
                }

                var options = ScriptRunOptions.FromCommandLine(
                    e.Args, Environment.CurrentDirectory);
                string source = Source.ReadFile(options.SourcePath!);
                var window = new MainWindow(commandLine: true)
                {
                    ShowActivated = false
                };
                MainWindow = window;
                await window.ApplyStartupWindowSettingsAsync();
                window.Show();
                result = await window.RunCommandLineAsync(source, options);
            }
            catch (Exception error)
            {
                ReportCommandLineError(error);
            }
            finally
            {
                Shutdown(result);
            }

            return;
        }

        base.OnStartup(e);
        InteractiveInstance? instance = singleInstance ? new() : null;
        if (instance is { IsFirst: false })
        {
            using (instance)
            {
                try
                {
                    string? path = interactiveFile is null ? null
                        : Path.GetFullPath(interactiveFile);
                    bool opened = await instance.RequestOpenAsync(path, run);
                    Shutdown(opened ? 0 : 1);
                }
                catch (Exception error)
                {
                    ReportCommandLineError(error);
                    Shutdown(1);
                }
            }

            return;
        }

        var main = new MainWindow();
        MainWindow = main;
        await main.ApplyStartupWindowSettingsAsync();
        string statePath = ApplicationInfo.SettingsPath("start-in-tray");
        main.ShowAtStartup(startInTray, minimizeToTray, statePath,
            restoreStartupState: windowOptions);
        if (interactiveFile is not null)
        {
            main.OpenInteractiveLaunch(Path.GetFullPath(interactiveFile));
        }

        if (instance is not null)
        {
            Exit += (_, _) => instance.Dispose();
            if (run is null)
            {
                _ = ListenForInteractiveLaunchAsync(instance, main);
            }
        }

        await main.InitializeSchedulerAsync();
        if (run is null)
        {
            return;
        }

        if (!main.StartInteractiveLaunch(run))
        {
            Shutdown(1);
            return;
        }

        // 初回の実行を開始してから後続の要求を受け付けます。
        _ = ListenForInteractiveLaunchAsync(instance!, main);
    }

    private static async Task ListenForInteractiveLaunchAsync(
        InteractiveInstance instance, MainWindow main)
    {
        try
        {
            await instance.ListenAsync(main.Dispatcher, main.OpenInteractiveLaunch,
                main.StartInteractiveLaunch);
        }
        catch (OperationCanceledException)
        {
            // アプリの終了時に受付を取り消します。
        }
        catch (Exception error)
        {
            main.ReportAssociationError(error);
        }
    }

    internal static void ReportCommandLineError(Exception error)
    {
        try
        {
            Console.Error.WriteLine(ScriptLogHost.FormatDiagnostic(error));
        }
        catch (Exception outputError) when (outputError is IOException or InvalidOperationException)
        {
            // 診断先を失っても、無人起動でダイアログは開きません。
        }
    }
}
