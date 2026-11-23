using System.Diagnostics;
using System.Text;
using UWSCSharp.Core;
using UWSCSharp.IO;
using UWSCSharp.Windows;

namespace UWSCSharp.Cli;

/// <summary>
/// 通常Mainと受入検証で共通の起動・実行・回収処理を行います。
/// </summary>
internal static class CliApplication
{
    internal static async Task<int> RunAsync(
        string[] args, CliWindowsRuntime? windowsRuntime = null)
    {
        Console.OutputEncoding = new UTF8Encoding(false);

        if (args.Length > 0
            && string.Equals(args[0], "/A", StringComparison.OrdinalIgnoreCase))
        {
            return RunScramble(args);
        }

        bool supportsManual = OperatingSystem.IsWindows();
        CliLaunchOptions cli;
        try
        {
            cli = CliLaunchOptions.Parse(args, supportsManual);
            if (cli.ManualCleanup && OperatingSystem.IsWindows())
            {
                NativeConsoleRecoveryInput.Validate();
            }
        }
        catch (Exception failure) when (failure is ArgumentException or IOException
            or InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine("起動設定: " + failure.Message);
            Console.Error.WriteLine(supportsManual
                ? "Usage: UWSC# [--input-cleanup=manual] <script.uws> [parameters...]"
                : "Usage: UWSC# <script.uws> [parameters...]");
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        var stop = new CliStopRequests(() => cancellation.Cancel());
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;
            stop.Request();
        };
        Console.CancelKeyPress += handler;
        CliWindowsRuntime? runtime = OperatingSystem.IsWindows()
            ? windowsRuntime ?? new CliWindowsRuntime() : null;
        ProcessStartInfo? replacement = null;
        try
        {
            int result = await RunScriptAsync();
            if (OperatingSystem.IsWindows() && runtime is not null)
            {
                var controller = new CliInputCleanupController(
                    new WindowsCliInputCleanup(runtime.Cleanup), Console.Error);
                result = await controller.CompleteAsync(result, cli.ManualCleanup, stop,
                    static () => OperatingSystem.IsWindows()
                        ? new NativeConsoleRecoveryInput()
                        : throw new PlatformNotSupportedException("入力フック回収はWindows専用です。"));
                if (result == 0 && replacement is not null)
                {
                    if (cancellation.IsCancellationRequested || stop.ExitRequested)
                    {
                        return 130;
                    }

                    try
                    {
                        runtime.StartReplacement(replacement);
                    }
                    catch (Exception failure) when (failure is InvalidOperationException
                        or IOException or System.ComponentModel.Win32Exception)
                    {
                        Console.Error.WriteLine("再実行: " + failure.Message);
                        return 1;
                    }
                }
            }
            return result;
        }
        finally
        {
            stop.Finish();
            Console.CancelKeyPress -= handler;
        }

        async Task<int> RunScriptAsync()
        {
            try
            {
                string workingDirectory = Environment.CurrentDirectory;
                var launch = ScriptRunOptions.FromCommandLine(
                    cli.ScriptArguments, workingDirectory);
                ScriptStartupDefaults startup = await ScriptStartupDefaults.LoadAsync(
                    cancellationToken: cancellation.Token);
                var output = new CallbackScriptHost(Console.WriteLine);
                await using WindowsStopFormHost? playback = OperatingSystem.IsWindows()
                    ? new WindowsStopFormHost(output, () => stop.Request(), Path.GetFileName(launch.SourcePath!),
                        startup.StopSize is { } size ? (size.WidthPixels, size.HeightPixels) : null)
                    : null;
                var runner = new Interpreter(
                    windows: OperatingSystem.IsWindows()
                    ? runtime!.CreateAutomation((kind, token) => kind == WindowsHostWindow.Stop && OperatingSystem.IsWindows()
                        ? playback!.GetWindowHandleAsync(token) : ValueTask.FromResult((nint)0),
                        new WindowsCommandOptions
                        {
                            CommandHost = WindowsCommandHost.ForCurrentProcess()
                        }) : null,
                    files: ScriptFiles.ForScript(launch.SourcePath!));
                string source = Source.ReadFile(launch.SourcePath!);
                var options = new ScriptRunOptions(launch.Arguments)
                {
                    SourcePath = launch.SourcePath,
                    DefaultFont = startup.DefaultFont,
                    PowerOperation = OperatingSystem.IsWindows()
                        ? HandlePowerCommand : null
                };
                return await ScriptLogHost.RunScriptWithDefaultsAsync((IScriptHost?)playback ?? output,
                    launch.SourcePath!, startup.Log,
                    host => runner.RunWithExitCodeAsync(source, host, cancellation.Token, options),
                    failure => Console.Error.WriteLine("ログ保存: " + failure.Message));

                Action? HandlePowerCommand(PowerCommandRequest request)
                {
                    if (!OperatingSystem.IsWindows())
                    {
                        throw new PlatformNotSupportedException();
                    }

                    if (request.Command != PowerCommand.RestartApplication)
                    {
                        runtime!.ExecutePowerCommand(request);
                        return null;
                    }

                    ProcessStartInfo start = CliWindowsRuntime.PrepareRestart(
                        request, launch, cli.ManualCleanup, workingDirectory);
                    return () => replacement = start;
                }
            }
            catch (OperationCanceledException ex)
            {
                Console.Error.WriteLine(ScriptLogHost.FormatDiagnostic(ex, "実行を停止しました。"));
                return 130;
            }
            catch (Exception ex) when (ex is ScriptException or IOException
                or ArgumentException
                or UnauthorizedAccessException
                or System.Text.DecoderFallbackException
                or System.Text.EncoderFallbackException
                or System.ComponentModel.Win32Exception or NotSupportedException
                or InvalidOperationException or AggregateException)
            {
                Console.Error.WriteLine(ScriptLogHost.FormatDiagnostic(ex));
                return 1;
            }
        }
    }

    private static int RunScramble(string[] args)
    {
        if (args.Length is < 2 or > 3)
        {
            Console.Error.WriteLine("Usage: UWSC# /A <input.uws> [output.uws]");
            return 2;
        }

        try
        {
            ScriptScramble.ConvertFile(args[1], args.Length == 3 ? args[2] : null);
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or AggregateException)
        {
            Console.Error.WriteLine("スクランブル変換: " + error.Message);
            return 1;
        }
    }

}
