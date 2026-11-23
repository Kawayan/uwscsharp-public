using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using UWSCSharp.Windows;

namespace UWSCSharp.Wpf;

/// <summary>
/// 編集画面を作らず、worker自身の入力と実行窓を所有します。
/// </summary>
internal sealed class SchedulerWorkerPlaybackHost : SchedulerPlaybackHost, IDisposable
{
    private readonly Window window = new()
    {
        Title = "UWSC# 予約worker",
        ShowInTaskbar = false,
        ShowActivated = false,
        Width = 1,
        Height = 1,
        WindowStyle = WindowStyle.None
    };
    private readonly SemaphoreSlim keyboard = new(1, 1);
    private readonly HotkeyRegistration hotkeys;
    private readonly SchedulerKeyboardClient keyboardClient;

    internal SchedulerWorkerPlaybackHost(SchedulerKeyboardClient keyboardClient)
    {
        this.keyboardClient = keyboardClient;
        nint handle = new WindowInteropHelper(window).EnsureHandle();
        hotkeys = new HotkeyRegistration(new Backend(handle));
        // 本体と同じOSホットキーを重ねて登録せず、停止画面とIPCを使います。
        hotkeys.Apply(new HotkeySettings(Enabled: false));
    }

    internal SchedulerScriptPlayback? Execution
    {
        get; set;
    }

    internal override Window Window => window;

    internal override WindowsSelectiveInputCleanup Cleanup { get; } = new();

    internal override HotkeyRegistration Hotkeys => hotkeys;

    internal override Task SendWithoutKeyboardHotkeysAsync(int key, Func<Task> send) =>
        RunKeyboardOperationAsync(() => Dispatcher.InvokeAsync(
            () => hotkeys.SendKeyboardInputAsync(key, () => keyboardClient.SendAsync(
                key, send, Execution?.CancellationToken ?? CancellationToken.None))).Task.Unwrap());

    internal override async Task RunKeyboardOperationAsync(Func<Task> operation)
    {
        await keyboard.WaitAsync().ConfigureAwait(false);
        try
        {
            await operation().ConfigureAwait(false);
        }
        finally
        {
            keyboard.Release();
        }
    }

    internal override void RefreshControls()
    {
    }

    internal override bool IsCurrent(SchedulerScriptPlayback execution) =>
        ReferenceEquals(Execution, execution);

    public void Dispose()
    {
        hotkeys.Dispose();
        window.Close();
        keyboard.Dispose();
    }

    private sealed class Backend(nint window) : IHotkeyBackend
    {
        public bool Register(int id, uint modifiers, int key) =>
            RegisterHotKey(window, id, modifiers, (uint)key);

        public bool Unregister(int id) => UnregisterHotKey(window, id)
            || Marshal.GetLastWin32Error() == 1419;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint window, int id);
}
