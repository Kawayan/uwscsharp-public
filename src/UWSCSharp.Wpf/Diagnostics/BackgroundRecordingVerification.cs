using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using static UWSCSharp.Wpf.VerificationAssert;

namespace UWSCSharp.Wpf;

internal static class BackgroundRecordingVerification
{
    internal static async Task<int> RunAsync(string receipt, bool background = true)
    {
        Require(SandboxEnvironment.IsCurrent, "Sandbox専用です。");
        string settings = MainWindow.RecordingVerificationAccess.SettingsPath;
        byte[]? saved = File.Exists(settings)
            ? await File.ReadAllBytesAsync(settings) : null;
        var target = new Window
        {
            Title = "背景記録の検証 " + Guid.NewGuid().ToString("N"),
            Left = 24,
            Top = 24,
            Width = 260,
            Height = 160
        };
        try
        {
            new RecordingSettings(OmitIdleTimeAndMouseMoves: false).Save(settings);
            var main = new MainWindow(false);
            try
            {
                main.Show();
                var access = new MainWindow.RecordingVerificationAccess(main);
                Require(!access.OmitIdleOptionChecked, "保存前の省略設定");
                access.ClickOmitIdleOption();
                Require(!access.RelativeMouseOptionChecked, "保存前の相対座標設定");
                access.ClickRelativeMouseOption();
                if (background)
                {
                    access.ClickBackgroundOption(true);
                }
                else
                {
                    access.ClickHighLevelOption();
                }

                var configured = RecordingSettings.Load(settings);
                Require(configured.BackgroundPlayback == background
                    && configured.RecordButtonClicks == !background
                    && configured.OmitIdleTimeAndMouseMoves
                    && configured.RelativeMouseCoordinates,
                    "設定保存");
            }
            finally
            {
                main.Close();
            }

            target.Show();
            nint root = new WindowInteropHelper(target).Handle;
            nint button = Native.CreateWindowExW(0, "BUTTON", "実行",
                0x50000000, 20, 20, 120, 40, root, 42, 0, 0);
            Require(button != 0, "検証ボタンの作成");
            await DiagnosticInputTarget.PrepareAsync(target);
            var foregrounds = new List<nint>();
            string observed = receipt + ".click";
            HwndSource.FromHwnd(root).AddHook(
                (nint _, int message, nint command, nint sender, ref bool handled) =>
                {
                    if (message == 0x111 && sender == button && command == 42)
                    {
                        nint active = Native.GetForegroundWindow();
                        foregrounds.Add(active);
                        if (foregrounds.Count == 1)
                        {
                            File.WriteAllText(observed, active.ToString());
                        }
                    }

                    return 0;
                });
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false
            };
            start.ArgumentList.Add("--verify-background-recording-after-restart");
            start.ArgumentList.Add(JsonSerializer.Serialize(new ButtonTarget(
                (long)root, (long)button, (uint)Environment.ProcessId, background)));
            start.ArgumentList.Add(receipt);
            using Process child = Process.Start(start)
                ?? throw new InvalidOperationException("WPFを起動できません。");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try
            {
                await child.WaitForExitAsync(timeout.Token);
                if (child.ExitCode == 0)
                {
                    Require(foregrounds.Count == 2 && foregrounds[0] == root,
                        "別プロセスでの記録と再生通知");
                    string[] playback = await File.ReadAllLinesAsync(
                        receipt + ".foreground");
                    Require(nint.TryParse(playback[0], out nint before)
                        && before != root && before != 0, "再生開始時の前面");
                    nint after = nint.Parse(playback[1]);
                    if (background)
                    {
                        Require(foregrounds[1] == before && after == before,
                            "前面を維持した再生通知");
                    }

                    await File.WriteAllLinesAsync(receipt + ".foreground",
                    [
                        "before=" + DescribeForeground(before, root, before),
                        "notification=" + DescribeForeground(foregrounds[1], root, before),
                        "after=" + DescribeForeground(after, root, before),
                        "switches=" + string.Join(",", playback.Skip(2)
                            .Select(value => DescribeForeground(nint.Parse(value),
                                root, before)))
                    ]);
                }

                return child.ExitCode;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                if (!child.HasExited)
                {
                    child.Kill();
                    await child.WaitForExitAsync();
                }

                throw new TimeoutException("再起動後の検証が終了しませんでした。");
            }
        }
        finally
        {
            target.Close();
            if (saved is null)
            {
                File.Delete(settings);
            }
            else
            {
                await File.WriteAllBytesAsync(settings, saved);
            }
        }
    }

    internal static async Task<int> RunAfterRestartAsync(
        string targetJson, string receipt)
    {
        Require(SandboxEnvironment.IsCurrent, "Sandbox専用です。");
        try
        {
            ButtonTarget target = JsonSerializer.Deserialize<ButtonTarget>(targetJson);
            await VerifyAsync(receipt, target);
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
            {
                result = "PASS",
                checks = new[]
                {
                    target.Background ? "background-recording-setting-restored-after-restart"
                        : "high-level-recording-setting-restored-after-restart",
                    target.Background ? "background-recording-save-and-replay"
                        : "high-level-recording-save-and-replay"
                },
                window_shown = true,
                input_injection = true
            }));
            return 0;
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
            {
                result = "FAIL",
                error = error.ToString()
            }));
            return 1;
        }
    }

    private static async Task VerifyAsync(string receipt, ButtonTarget target)
    {
        var errors = new List<Exception>();
        var main = new MainWindow(false, confirmDiscard: () => true,
            reportError: errors.Add, verifyRecordingInput: true)
        {
            ShowActivated = false
        };
        var access = new MainWindow.RecordingVerificationAccess(main);
        var tray = new MainWindow.TrayVerificationAccess(main);
        try
        {
            main.Show();
            Require(access.BackgroundOptionChecked == target.Background
                && access.HighLevelOptionChecked == !target.Background,
                "再起動後の記録方式");
            Require(access.OmitIdleOptionChecked, "再起動後の省略設定表示");
            Require(access.RelativeMouseOptionChecked, "再起動後の相対座標設定表示");

            nint root = (nint)target.Window;
            nint button = (nint)target.Button;
            Native.GetWindowThreadProcessId(button, out uint owner);
            Require(owner == target.Process && owner != (uint)Environment.ProcessId
                && Native.IsChild(root, button), "別プロセスの検証ボタン");
            await DiagnosticInputTarget.PrepareAsync(main);
            ClickButton(main, access.RecordButton);
            await RecordingClipboardVerification.WaitAsync(
                () => access.IsRunning && !access.IsStarting);
            tray.Hide();
            Require(!main.IsVisible && tray.Icon?.Visible == true,
                "記録中のトレイ格納");
            if (Native.IsIconic(root))
            {
                const int restore = 9;
                _ = Native.ShowWindow(root, restore);
                await RecordingClipboardVerification.WaitAsync(
                    () => !Native.IsIconic(root));
            }

            _ = Native.SetForegroundWindow(root);
            await DiagnosticInputTarget.PrepareAsync(root, owner);
            try
            {
                await RecordingClipboardVerification.WaitAsync(
                    () => !Native.IsIconic(root)
                        && Native.GetForegroundWindow() == root
                        && TryMoveCursorToButton(button));
            }
            catch (TimeoutException error)
            {
                using var dpi = new PhysicalDpiScope();
                _ = Native.GetWindowRect(root, out Native.Rect window);
                _ = Native.GetWindowRect(button, out Native.Rect bounds);
                var point = new Native.Point((bounds.Left + bounds.Right) / 2,
                    (bounds.Top + bounds.Bottom) / 2);
                nint hit = Native.WindowFromPoint(point);
                var hitClass = new System.Text.StringBuilder(256);
                _ = Native.GetClassNameW(hit, hitClass, hitClass.Capacity);
                throw new InvalidOperationException(
                    $"対象={root} 前面={Native.GetForegroundWindow()} ボタン={button} 最小化={Native.IsIconic(root)} "
                    + $"対象矩形=({window.Left},{window.Top},{window.Right},{window.Bottom}) "
                    + $"ボタン矩形=({bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom}) "
                    + $"位置=({point.X},{point.Y}) 検出={hit}/{hitClass}", error);
            }
            Require(!main.IsVisible && main.WindowState != WindowState.Minimized
                && tray.Icon?.Visible == true && Native.GetForegroundWindow() == root,
                "記録時のメイン画面の表示と入力先");
            try
            {
                SendMouse(2);
            }
            finally
            {
                SendMouse(4);
            }

            await RecordingClipboardVerification.WaitAsync(
                () => File.Exists(receipt + ".click"));
            await access.FinishRecordingAsync();
            Require(!access.HasRecorder && errors.Count == 0
                && !main.IsVisible && tray.Icon?.Visible == true,
                "格納中の記録停止と回収");
            tray.Restore();
            string source = access.Source;
            int flags = (int)(target.Background
                ? ClickItemFlags.Button | ClickItemFlags.Api | ClickItemFlags.Background
                : ClickItemFlags.Button);
            Require(source.StartsWith("CLKITEM(GETID(", StringComparison.Ordinal)
                && source.TrimEnd().EndsWith($",\"実行\",{flags},1,1)",
                    StringComparison.Ordinal), "記録スクリプト: " + source);
            string script = Path.Combine(Path.GetDirectoryName(receipt)!,
                target.Background ? "背景記録.uws" : "高レベル記録.uws");
            await DiagnosticInputTarget.PrepareAsync(main);
            nint mainHandle = new WindowInteropHelper(main).Handle;
            var selectPath = Task.Run(() => SelectSavePathAsync(mainHandle, script));
            access.SaveButton.RaiseEvent(new RoutedEventArgs(
                System.Windows.Controls.Button.ClickEvent));
            await selectPath;
            Require(access.ScriptPath == script && File.Exists(script),
                $"保存先: {access.ScriptPath ?? "未設定"}、エラー: "
                + string.Join(", ", errors.Select(error => error.Message)));
            Require(await File.ReadAllTextAsync(script) == source, "記録の保存");
            access.SetSource("");
            Require(access.IsModified, "編集欄を空にした後の変更状態");
            access.LoadScript(script);
            Require(access.Source == source && access.ScriptPath == script
                && !access.IsModified, "保存した記録の再読込");
            await DiagnosticInputTarget.PrepareAsync(main);
            const uint foregroundEvent = 3;
            List<(nint Window, uint Time)> switches = [];
            ForegroundEvent callback = (_, _, window, _, _, _, time) =>
                switches.Add((window, time));
            nint hook = SetWinEventHook(foregroundEvent, foregroundEvent,
                0, callback, 0, 0, 0);
            Require(hook != 0, "前面切替の監視を開始");
            try
            {
                nint foreground = Native.GetForegroundWindow();
                uint started = unchecked((uint)Environment.TickCount);
                Require(await access.PlayAsync() == 0, "保存した記録の再生");
                uint finished = unchecked((uint)Environment.TickCount);
                nint after = Native.GetForegroundWindow();
                // 別スレッドで発生した通知を監視側のメッセージループで受け取ります。
                await Task.Delay(100);
                await File.WriteAllLinesAsync(receipt + ".foreground",
                [
                    foreground.ToString(),
                    after.ToString(),
                    .. switches.Where(change =>
                        unchecked(change.Time - started) <= unchecked(finished - started))
                        .Select(change => change.Window.ToString())
                ]);
            }
            finally
            {
                bool removed = UnhookWinEvent(hook);
                GC.KeepAlive(callback);
                Require(removed, "前面切替の監視を終了");
            }
        }
        finally
        {
            if (access.HasRecorder)
            {
                await access.FinishRecordingAsync();
            }

            access.DiscardDraft();
            main.Close();
        }
    }

    private delegate void ForegroundEvent(nint hook, uint eventId,
        nint window, int objectId, int childId, uint threadId, uint time);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint first, uint last,
        nint module, ForegroundEvent callback, uint process, uint thread,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);

    private static string DescribeForeground(nint window, nint target, nint initial)
        => window == target ? "target" : window == initial ? "initial"
            : window == 0 ? "none" : "other";

    private static async Task SelectSavePathAsync(nint owner, string path)
    {
        nint dialog = 0;
        try
        {
            await RecordingClipboardVerification.WaitAsync(() =>
            {
                nint foreground = Native.GetForegroundWindow();
                if (Native.GetWindow(foreground, 4) != owner)
                {
                    return false;
                }

                dialog = foreground;
                return true;
            });
            var window = AutomationElement.FromHandle(dialog);
            Require(window.Current.Name == "UTF-8（BOM付き）で保存",
                "所有する保存ダイアログ");
            AutomationElement? fileName = window.FindFirst(TreeScope.Descendants,
                new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty,
                        ControlType.Edit),
                    new PropertyCondition(AutomationElement.AutomationIdProperty,
                        "1001")));
            Require(fileName is not null, "保存ダイアログのファイル名欄");
            fileName!.SetFocus();
            Require(Native.GetForegroundWindow() == dialog,
                "ファイル名を入力するダイアログ");
            SendKey(0x11, 0, 0);
            try
            {
                SendKey(0x41, 0, 0);
                SendKey(0x41, 0, 2);
            }
            finally
            {
                SendKey(0x11, 0, 2);
            }

            foreach (char character in path)
            {
                SendKey(0, character, 4);
                SendKey(0, character, 6);
            }

            SendKey(0x0d, 0, 0);
            SendKey(0x0d, 0, 2);
        }
        catch
        {
            if (dialog != 0)
            {
                _ = Native.PostMessageW(dialog, 0x10, 0, 0);
            }

            throw;
        }
    }

    private readonly record struct ButtonTarget(
        long Window, long Button, uint Process, bool Background);

    private static void ClickButton(
        Window main, System.Windows.Controls.Button button)
    {
        nint owner = new WindowInteropHelper(main).Handle;
        using var dpi = new PhysicalDpiScope();
        var center = new Point(button.ActualWidth / 2, button.ActualHeight / 2);
        Point screen = button.PointToScreen(center);
        var point = new Native.Point((int)screen.X, (int)screen.Y);
        Require(button.IsEnabled && button.InputHitTest(center) is not null
            && Native.GetForegroundWindow() == owner
            && Native.WindowFromPoint(point) == owner, "検証ボタンの入力先");
        Require(Native.SetCursorPos(point.X, point.Y), "検証ボタンへ移動");
        try
        {
            SendMouse(2);
        }
        finally
        {
            SendMouse(4);
        }
    }

    private static bool TryMoveCursorToButton(nint button)
    {
        using var dpi = new PhysicalDpiScope();
        Require(Native.GetWindowRect(button, out Native.Rect bounds), "ボタン位置");
        var point = new Native.Point((bounds.Left + bounds.Right) / 2,
            (bounds.Top + bounds.Bottom) / 2);
        return Native.WindowFromPoint(point) == button
            && Native.SetCursorPos(point.X, point.Y);
    }

    private static void SendKey(ushort key, ushort scan, uint flags) =>
        NativeInputSender.Send(new Native.Input
        {
            Type = 1,
            Data = new Native.InputUnion
            {
                Keyboard = new Native.KeyboardInput
                {
                    Vk = key,
                    Scan = scan,
                    Flags = flags
                }
            }
        });

    private static void SendMouse(uint flags) =>
        NativeInputSender.Send(new Native.Input
        {
            Type = 0,
            Data = new Native.InputUnion
            {
                Mouse = new Native.MouseInput
                {
                    Flags = flags
                }
            }
        });
}
