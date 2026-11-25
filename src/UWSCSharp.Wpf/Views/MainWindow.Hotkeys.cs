using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using UWSCSharp.Windows;

namespace UWSCSharp.Wpf;

public partial class MainWindow
{
    private void InitializeHotkeys(object? sender, EventArgs e)
    {
        windowHandle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(windowHandle).AddHook(HotkeyMessage);
        hotkeys = new HotkeyRegistration(new WindowHotkeyBackend(windowHandle));
        ApplyHotkeySettings(SettingsPath);
        RecordButton.ToolTip = "記録開始・終了。現在のキーは下部に表示します";
        PauseButton.ToolTip = "一時停止・再開。押下中はUPまで保留。現在のキーは下部に表示します";
    }

    private void ApplyHotkeySettings(string path)
    {
        string loadWarning = "";
        try
        {
            hotkeySettings = HotkeySettings.LoadForStartup(path);
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            hotkeySettings = new();
            loadWarning = "設定を読めないため既定値を使用します。";
        }

        hotkeys!.Apply(hotkeySettings);
        HotkeyLabel.Text = loadWarning + hotkeys.Status;
    }

    private void SettingsClick(object sender, RoutedEventArgs e)
    {
        if (cancellation is not null || recording.HasRecorder
            || recording.IsStarting || recording.IsStopping || hotkeys is null)
        {
            return;
        }

        var dialog = new HotkeySettingsWindow(hotkeySettings) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } candidate)
        {
            return;
        }

        SaveHotkeySettings(candidate, SettingsPath);
    }

    private void SaveHotkeySettings(HotkeySettings candidate, string path)
    {
        if (candidate.Enabled && launcherHotkeys is not null
            && launcherHotkeys.RegisteredShortcuts.Any(shortcut =>
                shortcut.Modifiers == candidate.Modifiers
                && candidate.Keys.Contains(shortcut.VirtualKey)))
        {
            HotkeyLabel.Text = "ランチ用ホットキーと競合するため、本体キーを変更できません。";
            return;
        }

        // 予約競合で未登録だった場合は、保存失敗時も未登録へ戻します。
        HotkeySettings previous = hotkeySettings.HasReservedShortcut
            ? new(Enabled: false) : hotkeySettings;
        if (!hotkeys!.Apply(candidate))
        {
            HotkeyLabel.Text = hotkeys.Status;
            return;
        }

        try
        {
            candidate.Save(path);
            hotkeySettings = candidate;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            hotkeys.Apply(previous);
            ShowError(error);
        }

        HotkeyLabel.Text = hotkeys.Status;
    }

    private sealed class WindowHotkeyBackend(nint window) : IHotkeyBackend
    {
        public bool Register(int id, uint modifiers, int key) =>
            RegisterHotKey(window, id, modifiers, (uint)key);

        public bool Unregister(int id) => UnregisterHotKey(window, id)
            || Marshal.GetLastWin32Error() == 1419;
    }

    private Task SendWithoutKeyboardHotkeysAsync(int key, Func<Task> send) =>
        RunKeyboardOperationAsync(() => Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                if (hotkeys is null)
                {
                    await SendWithLauncherHotkeysAsync(key, send);
                }
                else
                {
                    await hotkeys.SendKeyboardInputAsync(key,
                        () => SendWithLauncherHotkeysAsync(key, send));
                }
            }
            finally
            {
                if (hotkeys is not null)
                {
                    HotkeyLabel.Text = hotkeys.Status;
                }
                RefreshLauncherHotkeyNotice();
            }
        }).Task.Unwrap());

    private Task RunKeyboardOperationAsync(Func<Task> operation) =>
        RunKeyboardOperationCoreAsync(operation, CancellationToken.None);

    private async Task RunKeyboardOperationCoreAsync(Func<Task> operation,
        CancellationToken cancellationToken)
    {
        await keyboardOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await operation().ConfigureAwait(false);
        }
        finally
        {
            keyboardOperationGate.Release();
        }
    }

    private nint HotkeyMessage(nint hwnd, int message,
        nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x312)
        {
            handled = true;
            if (launcherHotkeys?.Accepts((int)wParam) == true)
            {
                int id = (int)wParam;
                if (Native.IsWindowEnabled(hwnd) && !launcherClosed)
                {
                    _ = Dispatcher.BeginInvoke(new Action(async () =>
                    {
                        if (!launcherClosed && Native.IsWindowEnabled(hwnd)
                            && launcherHotkeys?.Accepts(id) == true)
                        {
                            await OpenLauncherFromHotkeyAsync(id);
                        }
                    }));
                }

                return 0;
            }

            if (hotkeys?.IsRegistered((int)wParam) != true)
            {
                return 0;
            }

            // 確認画面中の所有ウィンドウへ処理を再入させません。
            if (!Native.IsWindowEnabled(hwnd))
            {
                return 0;
            }

            switch ((int)wParam)
            {
                case 1:
                    RecordClick(this, new RoutedEventArgs());
                    break;
                case 2:
                    PauseClick(this, new RoutedEventArgs());
                    break;
                case HotkeyRegistration.StopHotkeyId:
                    if (recording.HasRecorder || cancellation is not null
                        || scheduledPlayback is not null)
                    {
                        StopClick(this, new RoutedEventArgs());
                    }

                    break;
                case 4:
                    PlayClick(this, new RoutedEventArgs());
                    break;
            }
        }

        return 0;
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(nint hwnd,
        int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);
}
