using System.Diagnostics;
using System.Runtime.Versioning;
using UWSCSharp.Core;
using UWSCSharp.Windows;

namespace UWSCSharp.Cli;

/// <summary>
/// Windowsの実行依存と、終了後も保持する回収一覧を作成します。
/// </summary>
[SupportedOSPlatform("windows")]
internal class CliWindowsRuntime
{
    internal WindowsSelectiveInputCleanup Cleanup { get; } = new();

    /// <summary>
    /// 通常実行では実際のNative依存を使う操作受付を返します。
    /// </summary>
    internal virtual IWindowsAutomation CreateAutomation(
        Func<WindowsHostWindow, CancellationToken, ValueTask<nint>> hostWindowHandle,
        WindowsCommandOptions commands) =>
        WindowsAutomation.WithHostWindows(hostWindowHandle, null, commands, Cleanup);

    internal virtual void ExecutePowerCommand(PowerCommandRequest request) =>
        WindowsPowerOperations.ExecuteSystemCommand(request);

    internal static ProcessStartInfo PrepareRestart(
        PowerCommandRequest request, ScriptRunOptions script, bool manualCleanup,
        string workingDirectory)
    {
        if (request.Command != PowerCommand.RestartApplication
            || !request.RestartScript)
        {
            throw new InvalidOperationException(
                $"{BuiltinFunctionNames.Poff}: CLIではP_UWSC_REEXECとTRUEを指定してください。");
        }

        return WindowsApplicationRestart.CreateStartInfo(
            WindowsCommandHost.ForCurrentProcess(), workingDirectory, true,
            script, manualCleanup ? [CliLaunchOptions.ManualCleanupOption] : []);
    }

    internal virtual void StartReplacement(ProcessStartInfo start)
    {
        using var replacement = Process.Start(start);
        if (replacement is null)
        {
            throw new InvalidOperationException("再実行のプロセスを起動できません。");
        }
    }
}
