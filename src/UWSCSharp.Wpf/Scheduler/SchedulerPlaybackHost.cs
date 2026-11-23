using System.Windows;
using System.Windows.Threading;
using UWSCSharp.Core;
using UWSCSharp.Windows;

namespace UWSCSharp.Wpf;

/// <summary>
/// 予約1実行が利用する画面・入力・ホットキーの所有者です。
/// </summary>
internal abstract class SchedulerPlaybackHost
{
    internal abstract Window Window
    {
        get;
    }

    internal Dispatcher Dispatcher => Window.Dispatcher;

    internal abstract WindowsSelectiveInputCleanup Cleanup
    {
        get;
    }

    internal abstract HotkeyRegistration? Hotkeys
    {
        get;
    }

    internal abstract Task SendWithoutKeyboardHotkeysAsync(int key, Func<Task> send);

    internal abstract Task RunKeyboardOperationAsync(Func<Task> operation);

    internal abstract void RefreshControls();

    internal virtual void ExecuteSystemPowerCommand(
        PowerCommandRequest request) =>
        WindowsPowerOperations.ExecuteSystemCommand(request);

    internal abstract bool IsCurrent(SchedulerScriptPlayback execution);
}
