using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using UWSCSharp.IO;
using UWSCSharp.Windows;
using static UWSCSharp.Wpf.SettingsVerification;
using static UWSCSharp.Wpf.VerificationAssert;

namespace UWSCSharp.Wpf;

internal static class WindowSettingsVerification
{
    internal static async Task<int> RunAsync(string receipt)
    {
        if (Environment.UserName != "WDAGUtilityAccount")
        {
            throw new InvalidOperationException("本体設定の検証はSandbox専用です。");
        }

        string path = IniPath;
        byte[]? previous = File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;
        var checks = new List<string>();
        string? error = null;
        var main = new MainWindow(false, confirmDiscard: () => true,
            reportError: failure => throw failure);
        var access = new MainWindow.SettingsVerificationAccess(main);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path,
                "; keep\n[SET]\nStayOnTop=1\nDefaultFont=Arial,18\nMainSize=440,122\nLogLines=3\nLogFont=Consolas,12\n");
            await main.ApplyStartupWindowSettingsAsync();
            Require(main.Topmost && !main.IsVisible, "初回表示より前の設定");
            main.Show();
            Require(IsTopmost(main), "本体のNative最前面スタイル");
            checks.Add("startup-applies-before-first-show");

            byte[] before = await File.ReadAllBytesAsync(path);
            await Modal(async dialog =>
            {
                Require(Choice(dialog).IsChecked == true, "保存値を表示");
                Require(Family(dialog).Text == "Arial" && Size(dialog).Text == "18", "フォントの保存値を表示");
                Choice(dialog).IsChecked = false;
                Family(dialog).Text = "Consolas";
                Size(dialog).Text = "11";
                WidthField(dialog).Text = "1000";
                HeightField(dialog).Text = "700";
                ((TextBox)dialog.FindName("StopWidthPixels")).Text = "900";
                ((TextBox)dialog.FindName("StopHeightPixels")).Text = "600";
                dialog.Close();
                await Task.CompletedTask;
            });
            byte[] afterCancel = await File.ReadAllBytesAsync(path);
            Require(IsTopmost(main) && before.SequenceEqual(afterCancel)
                && access.RequestedMainSize is
                { WidthPixels: 440, HeightPixels: 122 },
                "取消で本体とINIを保持");
            checks.Add("cancel-preserves-window-and-ini");

            await Modal(async dialog =>
            {
                foreach ((string family, string size) in new[]
                {
                    ("Arial", "9.5"), ("Arial", "0"),
                    ("Arial", "-1"), ("Arial", "2147483648"),
                    ("", "9"), ("Arial,Consolas", "9")
                })
                {
                    Family(dialog).Text = family;
                    Size(dialog).Text = size;
                    ClickSave(dialog);
                    await WaitUntil(() => Save(dialog).IsEnabled);
                    Require(dialog.IsVisible && Error(dialog).Text.Length > 0
                        && Family(dialog).Text == family && Size(dialog).Text == size,
                        "不正入力で編集内容を保持");
                    byte[] unchanged = await File.ReadAllBytesAsync(path);
                    Require(before.SequenceEqual(unchanged), "不正入力でINIを保持");
                }

                dialog.Close();
            });
            checks.Add("invalid-font-input-keeps-editor-and-ini");

            await Modal(async dialog =>
            {
                Choice(dialog).IsChecked = false;
                Family(dialog).Text = "Consolas";
                Size(dialog).Text = "11";
                ClickSave(dialog);
                await WaitUntil(() => !dialog.IsVisible);
            });
            ApplicationWindowSettings saved = await ApplicationWindowSettings.LoadAsync();
            Require(!saved.StayOnTop && !IsTopmost(main)
                && saved.DialogFont is { Family: "Consolas", SizePoints: 11 },
                "保存した解除を本体へ反映し、フォントを保存");
            checks.Add("save-disables-native-topmost-and-persists-zero");

            before = await File.ReadAllBytesAsync(path);
            await Modal(async dialog =>
            {
                Choice(dialog).IsChecked = true;
                Family(dialog).Text = "Arial";
                Size(dialog).Text = "18";
                WidthField(dialog).Text = "441";
                HeightField(dialog).Text = "123";
                ((TextBox)dialog.FindName("StopWidthPixels")).Text = "500";
                ((TextBox)dialog.FindName("StopHeightPixels")).Text = "300";
                using (var locked = new FileStream(path, FileMode.Open,
                    FileAccess.Read, FileShare.Read))
                {
                    ClickSave(dialog);
                    await WaitUntil(() => Save(dialog).IsEnabled);
                    Require(dialog.IsVisible && Error(dialog).Text.Length > 0
                        && !IsTopmost(main) && Family(dialog).Text == "Arial"
                        && Size(dialog).Text == "18" && WidthField(dialog).Text == "441"
                        && HeightField(dialog).Text == "123"
                        && ((TextBox)dialog.FindName("StopWidthPixels")).Text == "500"
                        && ((TextBox)dialog.FindName("StopHeightPixels")).Text == "300"
                        && access.RequestedMainSize is
                        { WidthPixels: 440, HeightPixels: 122 },
                        "保存失敗で画面と本体の設定を保持");
                }

                byte[] afterFailure = await File.ReadAllBytesAsync(path);
                Require(before.SequenceEqual(afterFailure), "保存失敗で元INIを保持");
                ClickSave(dialog);
                await WaitUntil(() => !dialog.IsVisible);
            });
            saved = await ApplicationWindowSettings.LoadAsync();
            string document = await File.ReadAllTextAsync(path);
            Require(saved.StayOnTop && IsTopmost(main)
                && document.Contains("LogLines=3") && document.Contains("LogFont=Consolas,12")
                && document.Contains("MainSize=441,123") && document.Contains("StopSize=500,300"),
                "再保存で最前面を有効にし、ログ設定を保持して編集した寸法を適用");
            checks.Add("save-failure-keeps-state-and-retry-applies-only-after-commit");

            await Modal(async dialog =>
            {
                Require(Family(dialog).Text == "Arial" && Size(dialog).Text == "18", "画面を開き直してフォントを復元");
                dialog.Close();
                await Task.CompletedTask;
            });
            Require((await ScriptStartupDefaults.LoadAsync()).DefaultFont == "Arial,18",
                "次の実行用の読込みと保存値が一致");
            checks.Add("font-restored-on-reopen-and-next-execution-snapshot");

            using (var locked = new FileStream(path, FileMode.Open,
                FileAccess.Read, FileShare.None))
            {
                await Modal(async dialog =>
                {
                    Require(!Save(dialog).IsEnabled && !Choice(dialog).IsEnabled
                        && !Family(dialog).IsEnabled && !Size(dialog).IsEnabled
                        && !WidthField(dialog).IsEnabled && !HeightField(dialog).IsEnabled
                        && !((TextBox)dialog.FindName("StopWidthPixels")).IsEnabled
                        && !((TextBox)dialog.FindName("StopHeightPixels")).IsEnabled,
                        "読込み失敗では編集と保存を禁止");
                    dialog.Close();
                    await Task.CompletedTask;
                }, allowLoadError: true);
                var fallback = new MainWindow(false, confirmDiscard: () => true);
                var fallbackAccess = new MainWindow.SettingsVerificationAccess(fallback);
                try
                {
                    await fallback.ApplyStartupWindowSettingsAsync();
                    Require(!fallback.Topmost
                        && fallbackAccess.StatusText.Contains("読み込めない"),
                        "起動時の読込み失敗は既定値と診断");
                }
                finally
                {
                    fallback.Close();
                }
            }

            checks.Add("read-failure-blocks-save-and-startup-reports-fallback");

            await Modal(async dialog =>
            {
                foreach ((string width, string height) in new[]
                {
                    ("0", "700"), ("-1", "700"), ("1000", ""),
                    ("1.5", "700"), ("2147483648", "700")
                })
                {
                    WidthField(dialog).Text = width;
                    HeightField(dialog).Text = height;
                    ClickSave(dialog);
                    await WaitUntil(() => Save(dialog).IsEnabled);
                    Require(dialog.IsVisible && Error(dialog).Text.Length > 0,
                        "不正寸法の保存を拒否");
                    Require((await ApplicationWindowSettings.LoadAsync()).MainSize?.WidthPixels == 441,
                        "不正寸法で保存値を維持");
                }

                dialog.Close();
            });
            checks.Add("invalid-main-size-preserves-ini");

            await Modal(async dialog =>
            {
                WidthField(dialog).Text = "1000";
                HeightField(dialog).Text = "700";
                ClickSave(dialog);
                await WaitUntil(() => !dialog.IsVisible);
            });
            await Modal(async dialog =>
            {
                WidthField(dialog).Text = "";
                HeightField(dialog).Text = "";
                ClickSave(dialog);
                await WaitUntil(() => !dialog.IsVisible);
            });
            main.UpdateLayout();
            saved = await ApplicationWindowSettings.LoadAsync();
            Require(saved.MainSize is { WidthPixels: 1000, HeightPixels: 700 },
                "指定した物理寸法を保存");
            using (var physical = new PhysicalDpiScope())
            {
                Require(Native.GetWindowRect(new WindowInteropHelper(main).Handle, out Native.Rect bounds)
                    && bounds.Right - bounds.Left == 1000 && bounds.Bottom - bounds.Top >= 700,
                    "保存後に物理寸法を適用");
            }

            access.SetPanelsExpanded(true);
            main.UpdateLayout();
            Require(access.EditorBottom <= main.ActualHeight,
                "編集欄を開いても内容を保持");
            access.SetPanelsExpanded(false);
            checks.Add("main-size-save-applies-and-content-expands");

            var reopened = new MainWindow(false, confirmDiscard: () => true);
            var reopenedAccess = new MainWindow.SettingsVerificationAccess(reopened);
            try
            {
                await reopened.ApplyStartupWindowSettingsAsync();
                reopened.Show();
                reopened.UpdateLayout();
                Require(reopenedAccess.RequestedMainSize is
                { WidthPixels: 1000, HeightPixels: 700 },
                    "新しい本体へ寸法を復元");
                Require(reopened.MinWidth == 620 && reopened.SizeToContent == SizeToContent.Height,
                    "必要な操作幅と内容の自動高さを維持");
            }
            finally
            {
                reopened.Close();
            }

            checks.Add("main-size-restored-on-new-window");

            await Modal(async dialog =>
            {
                var width = (TextBox)dialog.FindName("StopWidthPixels");
                var height = (TextBox)dialog.FindName("StopHeightPixels");
                width.Text = "0";
                height.Text = "700";
                ClickSave(dialog);
                await WaitUntil(() => Save(dialog).IsEnabled);
                Require(dialog.IsVisible && Error(dialog).Text.Length > 0,
                    "停止画面の不正寸法を拒否");
                Require((await ApplicationWindowSettings.LoadAsync()).StopSize is { WidthPixels: 500, HeightPixels: 300 },
                    "不正な停止寸法を保存しない");
                width.Text = "1000";
                ClickSave(dialog);
                await WaitUntil(() => !dialog.IsVisible);
            });
            await Modal(async dialog =>
            {
                var width = (TextBox)dialog.FindName("StopWidthPixels");
                var height = (TextBox)dialog.FindName("StopHeightPixels");
                Require(width.Text == "1000" && height.Text == "700", "停止寸法を再読込み");
                width.Text = "";
                height.Text = "";
                ClickSave(dialog);
                await WaitUntil(() => !dialog.IsVisible);
            });
            saved = await ApplicationWindowSettings.LoadAsync();
            Require(saved.StopSize is { WidthPixels: 1000, HeightPixels: 700 },
                "停止寸法の空欄時に既存値を保持");
            checks.Add("stop-size-validation-save-and-empty-fields-preserve-value");

            StopWindow sizedStop = access.GetStopWindow();
            sizedStop.ShowForPlayback("size.uws", PlaybackControlView.Create(true, false, false, false));
            sizedStop.SetSize(saved.StopSize);
            using (var physical = new PhysicalDpiScope())
            {
                nint handle = new WindowInteropHelper(sizedStop).Handle;
                Require(Native.GetWindowRect(handle, out Native.Rect bounds)
                    && bounds.Right - bounds.Left == 1000 && bounds.Bottom - bounds.Top == 700,
                    "停止画面へ物理寸法を適用");
                sizedStop.SetSize(new ApplicationWindowSize(1, 1));
                Require(sizedStop.StopPlaybackButton.IsEnabled
                    && sizedStop.StopPlaybackButton.TranslatePoint(
                        new Point(0, sizedStop.StopPlaybackButton.ActualHeight), sizedStop).Y
                        <= sizedStop.ActualHeight, "小さい指定でも停止ボタンを保持");
                sizedStop.SetSize(new ApplicationWindowSize(int.MaxValue, int.MaxValue));
                Require(Native.GetWindowRect(handle, out bounds), "大きな指定を適用");
                var work = new Native.MonitorInfo { Size = (uint)Marshal.SizeOf<Native.MonitorInfo>() };
                Require(Native.GetMonitorInfoW(Native.MonitorFromPoint(new(bounds.Left, bounds.Top), 2),
                    ref work) && bounds.Right <= work.Work.Right && bounds.Bottom <= work.Work.Bottom,
                    "巨大な寸法を作業領域へ補正");
                sizedStop.SetSize(null);
                Require(sizedStop.Width == 320 && sizedStop.MinHeight == 0,
                    "次の未指定実行では既定寸法に復帰");
            }
            sizedStop.FinishPlayback();
            checks.Add("stop-size-applies-minimum-work-area-and-resets-between-runs");

            using (var stop = new CancellationTokenSource())
            {
                access.SetPlayback(stop);
                Require(!access.CanEditWindowSettings, "再生中の設定を禁止");
                access.OpenWindowSettings();
                Require(!Application.Current.Windows.OfType<WindowSettingsWindow>().Any(), "再生中の入口拒否");
                access.SetPlayback(null);
                Require(access.CanEditWindowSettings, "終了後の設定を復帰");
            }

            checks.Add("editing-blocked-during-playback-and-restored-afterwards");
        }
        catch (Exception failure)
        {
            error = failure.ToString();
        }
        finally
        {
            error = await RestoreAsync(
                access.CloseAfterPlayback, path, previous, error);
        }

        return await WriteReceiptAsync(
            receipt, checks, error);

        Task Modal(Func<WindowSettingsWindow, Task> action,
            bool allowLoadError = false)
            => RunSettingsModalAsync(main.Dispatcher,
                access.ClickWindowSettings, action, allowLoadError,
                "本体設定の読込み", "本体設定の処理が完了しませんでした。");

        static TextBox WidthField(WindowSettingsWindow dialog) => (TextBox)dialog.FindName("MainWidthPixels");
        static TextBox HeightField(WindowSettingsWindow dialog) => (TextBox)dialog.FindName("MainHeightPixels");
        static CheckBox Choice(WindowSettingsWindow dialog) => (CheckBox)dialog.FindName("StayOnTop");
        static TextBox Family(WindowSettingsWindow dialog) => (TextBox)dialog.FindName("DialogFontFamily");
        static TextBox Size(WindowSettingsWindow dialog) => (TextBox)dialog.FindName("DialogFontSize");
        static bool IsTopmost(Window window) =>
            (Native.GetWindowLongW(new WindowInteropHelper(window).Handle, -20) & 8) != 0;
        static Task WaitUntil(Func<bool> predicate)
            => WaitUntilAsync(predicate,
                "本体設定の処理が完了しませんでした。");
    }
}
