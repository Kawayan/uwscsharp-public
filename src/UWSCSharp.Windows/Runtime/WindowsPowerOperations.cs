using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// Windowsのセッション終了、電源状態の変更、スクリーンセーバーの起動を要求します。
/// </summary>
internal static partial class WindowsPowerOperations
{
    private const uint ExitReboot = 2;
    private const uint ExitForce = 4;
    private const uint ExitPowerOff = 8;
    private const uint PlannedApplicationReason = 0x80040000;
    private const uint SystemCommandMessage = 0x0112;
    private const int MonitorPowerCommand = 0xF170;
    private const int ScreenSaverCommand = 0xF140;
    private const int BroadcastWindow = 0xFFFF;
    private const uint DisplayRequired = 2;
    private const uint TokenQuery = 8;
    private const uint TokenAdjustPrivileges = 32;
    private const uint PrivilegeEnabled = 2;
    private static readonly object PrivilegeLock = new();

    internal static void ExecuteSystemCommand(PowerCommandRequest request) =>
        Execute(request, RequestExit, RequestSuspend,
            WithShutdownPrivilege, PostDesktopMessage, SetDisplayExecutionState,
            static _ => throw new InvalidOperationException(
                $"{BuiltinFunctionNames.Poff}: アプリの再起動は実行元で処理します。"));

    internal static void Execute(PowerCommandRequest request,
        Action<uint> exit, Action<bool> suspend, Action<Action> withPrivilege,
        Action<nint, uint, nint, nint> postMessage,
        Action<uint> setExecutionState, Action<bool> restartApplication)
    {
        uint force = request.Force ? ExitForce : 0;
        switch (request.Command)
        {
            case PowerCommand.PowerOff:
                withPrivilege(() => exit(ExitPowerOff | force));
                break;
            case PowerCommand.LogOff:
                exit(force);
                break;
            case PowerCommand.Reboot:
                withPrivilege(() => exit(ExitReboot | force));
                break;
            case PowerCommand.Hibernate:
            case PowerCommand.Standby:
                withPrivilege(() => suspend(
                    request.Command == PowerCommand.Hibernate));
                break;
            case PowerCommand.MonitorLowPower:
                postMessage(BroadcastWindow, SystemCommandMessage,
                    MonitorPowerCommand, 1);
                break;
            case PowerCommand.MonitorOff:
                postMessage(BroadcastWindow, SystemCommandMessage,
                    MonitorPowerCommand, 2);
                break;
            case PowerCommand.MonitorOn:
                postMessage(BroadcastWindow, SystemCommandMessage,
                    MonitorPowerCommand, -1);
                setExecutionState(DisplayRequired);
                break;
            case PowerCommand.ScreenSaver:
                postMessage(BroadcastWindow, SystemCommandMessage,
                    ScreenSaverCommand, 0);
                break;
            case PowerCommand.RestartApplication:
                restartApplication(request.RestartScript);
                break;
            default:
                throw new NotSupportedException(
                    $"{BuiltinFunctionNames.Poff}: このWindows操作は未対応です。");
        }
    }

    private static void RequestExit(uint flags)
    {
        if (!ExitWindowsEx(flags, PlannedApplicationReason))
        {
            throw Error(nameof(ExitWindowsEx), Marshal.GetLastPInvokeError());
        }
    }

    private static void RequestSuspend(bool hibernate)
    {
        if (!SetSuspendState(hibernate, false, false))
        {
            throw Error(nameof(SetSuspendState), Marshal.GetLastPInvokeError());
        }
    }

    private static void PostDesktopMessage(
        nint window, uint message, nint command, nint parameter)
    {
        if (!Native.PostMessageW(window, message, command, parameter))
        {
            throw Error(nameof(Native.PostMessageW),
                Marshal.GetLastPInvokeError());
        }
    }

    private static void SetDisplayExecutionState(uint flags)
    {
        if (SetThreadExecutionState(flags) == 0)
        {
            throw new InvalidOperationException(
                $"{BuiltinFunctionNames.Poff}: 画面のアイドルタイマーを更新できません。");
        }
    }

    internal static void WithShutdownPrivilege(Action operation)
    {
        lock (PrivilegeLock)
        {
            if (!OpenProcessToken(GetCurrentProcess(),
                TokenQuery | TokenAdjustPrivileges,
                out SafeAccessTokenHandle token))
            {
                throw Error(nameof(OpenProcessToken), Marshal.GetLastPInvokeError());
            }

            using (token)
            {
                if (!LookupPrivilegeValueW(null, "SeShutdownPrivilege", out Luid luid))
                {
                    throw Error(nameof(LookupPrivilegeValueW),
                        Marshal.GetLastPInvokeError());
                }

                var enabled = new TokenPrivileges(1, luid, PrivilegeEnabled);
                bool adjusted = AdjustTokenPrivileges(token, false, in enabled,
                    (uint)Marshal.SizeOf<TokenPrivileges>(), out TokenPrivileges previous,
                    out _);
                CheckAdjustment(adjusted, Marshal.GetLastPInvokeError());

                Exception? operationError = null;
                try
                {
                    operation();
                }
                catch (Exception error)
                {
                    operationError = error;
                    throw;
                }
                finally
                {
                    if (previous.Count != 0)
                    {
                        RestorePrivilege(token, previous, operationError);
                    }
                }
            }
        }
    }

    private static void RestorePrivilege(
        SafeAccessTokenHandle token,
        TokenPrivileges previous, Exception? operationError)
    {
        bool adjusted = AdjustTokenPrivileges(token, false, in previous, 0, 0, 0);
        int error = Marshal.GetLastPInvokeError();
        if (adjusted && error == 0)
        {
            return;
        }

        Win32Exception restoreError = Error("AdjustTokenPrivileges（復元）", error);
        if (operationError is not null)
        {
            throw new AggregateException(operationError, restoreError);
        }

        throw restoreError;
    }

    private static void CheckAdjustment(bool adjusted, int error)
    {
        // APIは特権が割り当てられていない場合も成功値を返します。
        if (!adjusted || error != 0)
        {
            throw Error(nameof(AdjustTokenPrivileges), error);
        }
    }

    private static Win32Exception Error(string operation, int error) =>
        new(error, $"{BuiltinFunctionNames.Poff}: {operation} に失敗しました"
            + $"（Win32 {error}: {new Win32Exception(error).Message}）。");
}
