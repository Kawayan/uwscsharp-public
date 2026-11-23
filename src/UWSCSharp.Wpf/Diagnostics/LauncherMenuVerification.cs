using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using UWSCSharp.Core;
using UWSCSharp.IO;
using UWSCSharp.Windows;
using static UWSCSharp.Wpf.VerificationAssert;

namespace UWSCSharp.Wpf;

internal static class LauncherMenuVerification
{
    internal static async Task<int> RunAsync(string directory, string receipt)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "launcher.json");
        var checks = new List<string>();
        var errors = new List<Exception>();
        var launched = new List<LauncherCommand>();
        bool confirmReset = false;
        var launcherBackend = new LauncherMenuHotkeyBackend();
        var main = new MainWindow(false, confirmDiscard: () => true,
            reportError: errors.Add, launcherSettingsPath: path,
            launchCommand: command => launched.Add(command),
            confirmLauncherReset: () => confirmReset);
        var access = new MainWindow.LauncherVerificationAccess(main);
        try
        {
            access.ConfigureHotkey(launcherBackend,
                Path.Combine(directory, "UWSC.INI"));
            main.Show();
            access.SetSource("// 未保存の原稿");
            access.SetOutput("保持する出力");
            string status = access.Status;
            Require(!File.Exists(path) && access.Settings is not null, "既定値を上書き保存しない");
            access.OpenMenu();
            Require(access.Menu!.Items.Count == 5, "空メニューと編集・再読込み");
            access.Menu!.IsOpen = false;
            Modal<LauncherSettingsWindow>(window => window.Close(), access.EditSettings);
            Require(!File.Exists(path), "取消でファイルを作らない");
            checks.Add("empty-menu-and-cancel-without-save");

            string target = Path.Combine(directory, "日本語 script.uws");
            await File.WriteAllTextAsync(target, """
                DIM f=FOPEN("menu.result",F_WRITE8)
                FPUT(f,PARAM_STR[0],0)
                FPUT(f,PARAM_STR[1],0)
                FCLOSE(f)
                WHILE !FOPEN("menu.go",F_EXISTS)
                    SLEEP(0.05)
                WEND
                EXITEXIT(0)
                """, new UTF8Encoding(true));
            Modal<LauncherSettingsWindow>(window =>
            {
                Named<ListBox>(window, "ランチの登録位置").SelectedIndex = 34;
                Modal<LauncherCommandWindow>(command =>
                {
                    Named<TextBox>(command, "項目のタイトル").Text = "実行_項目";
                    Named<TextBox>(command, "対象ファイルのパス").Text =
                        Path.GetRelativePath(AppContext.BaseDirectory, target);
                    Named<TextBox>(command, "引数の値").Text = "";
                    Click(command, "追加");
                    Named<TextBox>(command, "引数の値").Text = "日本語 with spaces";
                    Click(command, "追加");
                    Named<TextBox>(command, "対象ファイルのパス").Text = "document.txt";
                    Click(command, "反映");
                    Require(command.IsVisible
                        && !string.IsNullOrEmpty(Named<TextBlock>(command, "項目の入力エラー").Text),
                        "文書の引数は拒否して画面へ戻す");
                    Named<TextBox>(command, "対象ファイルのパス").Text =
                        Path.GetRelativePath(AppContext.BaseDirectory, target);
                    Click(command, "反映");
                }, () => Click(window, "起動項目を登録・編集"));
                Click(window, "設定を保存");
            }, access.EditSettings);
            var saved = (LauncherCommand)access.Settings!.Slots[34]!;
            Require(saved.Path == target && saved.Arguments.SequenceEqual(["", "日本語 with spaces"]),
                "登録時の絶対パスと空引数を保存");
            checks.Add("command-dialog-literal-arguments-and-absolute-path");

            Modal<LauncherSettingsWindow>(window =>
            {
                Named<ListBox>(window, "ランチの登録位置").SelectedIndex = 0;
                Modal<Window>(group =>
                {
                    Named<TextBox>(group, "階層の名称").Text = "階層_空";
                    Click(group, "反映");
                }, () => Click(window, "階層を作成・名称変更"));
                Click(window, "階層を開く");
                Require(!FindAll<Button>(window).Single(button => Equals(button.Content, "階層を作成・名称変更")).IsEnabled,
                    "子階層では階層を作れない");
                Click(window, "上位へ戻る");
                Click(window, "設定を保存");
            }, access.EditSettings);
            access.OpenMenu();
            var emptyGroup = (MenuItem)access.Menu!.Items[0];
            var executable = (MenuItem)access.Menu!.Items[1];
            Require(!emptyGroup.IsEnabled && ((TextBlock)executable.Header).Text == "実行_項目",
                "空階層と下線を含むタイトルを表示");
            executable.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(launched.Count == 1 && ReferenceEquals(saved, launched[0])
                && launched[0].Arguments.SequenceEqual(saved.Arguments), "同じメニューから起動要求");
            access.Menu!.IsOpen = false;
            checks.Add("single-level-group-empty-state-and-menu-launch");

            byte[] previousBytes = await File.ReadAllBytesAsync(path);
            LauncherSettings previous = access.Settings!;
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Modal<LauncherSettingsWindow>(window =>
                {
                    Named<ListBox>(window, "ランチの登録位置").SelectedIndex = 34;
                    Click(window, "選択を削除");
                    Click(window, "設定を保存");
                    Require(window.IsVisible && !string.IsNullOrEmpty(Named<TextBlock>(window, "登録設定のエラー").Text),
                        "保存失敗は画面を残して表示");
                    window.Close();
                }, access.EditSettings);
            }

            byte[] afterFailure = await File.ReadAllBytesAsync(path);
            Require(ReferenceEquals(previous, access.Settings)
                && previousBytes.SequenceEqual(afterFailure), "保存失敗で旧設定を保持");
            checks.Add("save-failure-keeps-dialog-current-menu-and-bytes");

            await File.WriteAllTextAsync(path, "invalid settings");
            access.ReloadSettings();
            Require(ReferenceEquals(previous, access.Settings) && access.SettingsError is not null,
                "再読込み失敗は直前のメニューを保持");
            access.OpenMenu();
            Require(!access.Menu!.Items.OfType<MenuItem>()
                .Single(item => Equals(item.Header, "登録を編集…")).IsEnabled, "破損中は編集不可");
            access.Menu!.IsOpen = false;
            access.ResetSettings();
            Require(await File.ReadAllTextAsync(path) == "invalid settings", "初期化の取消");
            confirmReset = true;
            access.ResetSettings();
            string backup = Directory.GetFiles(directory, "*.rejected.json").Single();
            Require(await File.ReadAllTextAsync(backup) == "invalid settings"
                && access.SettingsError is null && access.Settings!.Slots.All(item => item is null),
                "明示的な退避初期化");
            checks.Add("reload-failure-edit-block-and-confirmed-backup-reset");

            string brokenPath = Path.Combine(directory, "broken-startup.json");
            await File.WriteAllTextAsync(brokenPath, "broken startup");
            var broken = new MainWindow(false, confirmDiscard: () => true,
                reportError: errors.Add, launcherSettingsPath: brokenPath);
            try
            {
                var brokenAccess =
                    new MainWindow.LauncherVerificationAccess(broken);
                broken.Show();
                brokenAccess.OpenMenu();
                Require(brokenAccess.Settings is null && brokenAccess.SettingsError is not null
                    && !string.IsNullOrEmpty(brokenAccess.Notice)
                    && !brokenAccess.Menu!.Items.OfType<MenuItem>()
                        .Single(item => Equals(item.Header, "登録を編集…")).IsEnabled
                    && await File.ReadAllTextAsync(brokenPath) == "broken startup",
                    "破損設定の初回起動は通知し、原文を保持");
            }
            finally
            {
                broken.Close();
            }

            checks.Add("invalid-startup-preserves-original-and-disables-editing");

            access.OverrideLaunch(_ => throw new FileNotFoundException(
                "起動失敗の検証"));
            await access.LaunchAsync(saved);
            Require(errors.Count == 1 && access.Source == "// 未保存の原稿"
                && access.Output == "保持する出力" && access.Status == status,
                "起動失敗で原稿・出力・再生状態を変更しない");
            checks.Add("launch-failure-preserves-editor-output-and-status");

            access.OverrideLaunch(null);
            int childId = await access.LaunchAsync(saved)
                ?? throw new InvalidOperationException("実スクリプトのPIDがありません。");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var child = Process.GetProcessById(childId);
            try
            {
                await File.WriteAllTextAsync(Path.Combine(directory, "menu.go"), "go");
                await child.WaitForExitAsync(timeout.Token);
                Require(child.ExitCode == 0, "実スクリプトの正常終了");
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync();
                }
            }
            string result = Path.Combine(directory, "menu.result");
            while (!File.Exists(result))
            {
                await Task.Delay(25, timeout.Token);
            }

            Require((await File.ReadAllLinesAsync(result)).SequenceEqual(["", "日本語 with spaces"]),
                "主画面の起動経路から実プロセスへ引数を渡す");
            checks.Add("main-menu-path-starts-real-associated-script");
            await VerifyHotkeysAsync();
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
            {
                result = "Passed",
                dpi = System.Windows.Media.VisualTreeHelper.GetDpi(main).PixelsPerInchX,
                checks
            }));
            return 0;
        }
        finally
        {
            launcherBackend.FailRelease.Clear();
            main.Close();
            access.DisposeHotkeys();
        }

        async Task VerifyHotkeysAsync()
        {
            byte[] original = await File.ReadAllBytesAsync(path);
            Modal<LauncherSettingsWindow>(window =>
            {
                SetShortcut(window, 119);
                window.Close();
            }, access.EditSettings);
            byte[] afterCancel = await File.ReadAllBytesAsync(path);
            Require(original.SequenceEqual(afterCancel)
                && launcherBackend.Keys.Count == 0, "取消で登録とJSONを変えない");
            checks.Add("hotkey-cancel-keeps-json-and-registration");

            Modal<LauncherSettingsWindow>(window =>
            {
                SetShortcut(window, 119);
                Click(window, "設定を保存");
            }, access.EditSettings);
            int firstId = launcherBackend.Keys.Single().Key;
            Require(access.AcceptsHotkey(firstId)
                && LauncherSettingsStore.Load(path).Shortcut == new ScriptHotkey(119, 2),
                "画面からCtrl+F8を保存する");
            string blockedPath = Path.Combine(directory, "blocked-main-hotkeys.json");
            access.SaveMainHotkeySettings(new HotkeySettings(Modifiers: 2), blockedPath);
            Require(!File.Exists(blockedPath) && access.HotkeyStatus.Contains("競合"),
                "本体設定からの逆方向の競合を拒否する");
            checks.Add("hotkey-dialog-save-and-reverse-conflict");

            access.Menu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
            bool handled = false;
            access.DispatchHotkey(firstId, ref handled);
            await WaitUntil(() => access.Menu?.IsOpen == true);
            Require(handled && access.Menu?.IsOpen == true,
                "有効なランチ通知からメニューを開く");
            access.Menu!.IsOpen = false;
            checks.Add("hotkey-message-opens-menu-through-dispatcher");
            await VerifyPositionAsync(firstId);

            launcherBackend.FailRegister = true;
            Modal<LauncherSettingsWindow>(window =>
            {
                SetShortcut(window, 120);
                Click(window, "設定を保存");
                Require(window.IsVisible
                    && Named<TextBlock>(window, "登録設定のエラー").Text.Length != 0,
                    "登録失敗は編集を残す");
                window.Close();
            }, access.EditSettings);
            launcherBackend.FailRegister = false;
            Require(access.AcceptsHotkey(firstId)
                && LauncherSettingsStore.Load(path).Shortcut == new ScriptHotkey(119, 2),
                "登録失敗で前のJSONと受付を保つ");
            checks.Add("hotkey-registration-failure-keeps-editor-and-old-key");

            access.DispatchHotkey(firstId, ref handled);
            launcherBackend.FailRelease.Add(firstId);
            var second = new LauncherSettings(access.Settings!.Slots, new ScriptHotkey(120, 2));
            access.ApplyHotkeySettings(second);
            access.RefreshHotkeyNotice();
            await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(access.Menu?.IsOpen != true
                && !access.AcceptsHotkey(firstId)
                && access.HotkeyNotice.Contains("Ctrl+F8")
                && access.HotkeyNotice.Contains("回収未完了"),
                "旧通知を破棄し未回収のキーを表示する");
            Modal<LauncherSettingsWindow>(window =>
            {
                SetShortcut(window, 121);
                Click(window, "設定を保存");
                Require(window.IsVisible, "未回収中は次のキー変更を拒否する");
                window.Close();
            }, access.EditSettings);
            Require(LauncherSettingsStore.Load(path).Shortcut == new ScriptHotkey(120, 2),
                "回収失敗後も新設定を維持する");
            checks.Add("hotkey-pending-label-blocks-change-and-drops-stale-message");

            access.OpenMenu();
            MenuItem retry = access.Menu!.Items.OfType<MenuItem>()
                .Single(item => Equals(item.Header, "ホットキーを再回収"));
            retry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(access.PendingHotkeys == 1, "再回収失敗を保持する");
            launcherBackend.FailRelease.Clear();
            retry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(access.PendingHotkeys == 0
                && launcherBackend.Keys.Single().Value == new ScriptHotkey(120, 2),
                "メニューから旧キーだけを再回収する");
            access.Menu!.IsOpen = false;
            checks.Add("hotkey-menu-retry-keeps-active-registration");

            LauncherSettings external = access.Settings!.WithSlot(33,
                new LauncherGroup("別の保存", []));
            LauncherSettingsStore.Save(path, external);
            byte[] externalBytes = await File.ReadAllBytesAsync(path);
            Modal<LauncherSettingsWindow>(window =>
            {
                SetShortcut(window, 121);
                Click(window, "設定を保存");
                Require(window.IsVisible
                    && Named<TextBlock>(window, "登録設定のエラー").Text
                        .Contains("再読込み", StringComparison.Ordinal),
                    "古い原稿の保存を拒否して編集を残す");
                window.Close();
            }, access.EditSettings);
            byte[] afterConflict = await File.ReadAllBytesAsync(path);
            Require(externalBytes.SequenceEqual(afterConflict)
                && launcherBackend.Keys.Single().Value == new ScriptHotkey(120, 2),
                "別の保存と変更前のキー登録を保持する");
            access.ReloadSettings();
            Require(access.Settings!.Slots[33]?.Title == "別の保存",
                "再読込みで別の保存を反映する");
            checks.Add("settings-conflict-keeps-newer-file-and-active-hotkey");

            await File.WriteAllTextAsync(path, "broken");
            access.ReloadSettings();
            Require(access.SettingsError is not null
                && access.ActiveHotkey == new ScriptHotkey(120, 2),
                "破損再読込みでも旧登録を維持する");
            confirmReset = true;
            access.ResetSettings();
            Require(access.Settings!.Shortcut is null
                && launcherBackend.Keys.Count == 0 && access.SettingsError is null,
                "明示初期化で設定と登録を解除する");
            checks.Add("hotkey-corrupt-reload-and-explicit-reset");

            Modal<LauncherSettingsWindow>(window =>
            {
                SetShortcut(window, 119);
                Click(window, "設定を保存");
            }, access.EditSettings);
            int finalId = launcherBackend.Keys.Single().Key;
            launcherBackend.FailRelease.Add(finalId);
            main.Close();
            Require(access.IsClosed && !access.AcceptsHotkey(finalId)
                && errors.Any(error => error.Message.Contains("ランチ用ホットキーの回収")),
                "終了時の回収失敗を通知して受付を止める");
            checks.Add("hotkey-close-reports-unrecovered-registration");
        }

        static void SetShortcut(LauncherSettingsWindow window, int key)
        {
            FindAll<CheckBox>(window).Single(box => Equals(box.Content, "ランチ用ホットキー"))
                .IsChecked = true;
            Named<ComboBox>(window, "ランチの修飾キー").SelectedIndex = 1;
            Named<ComboBox>(window, "ランチのキー").SelectedIndex = 36 + key - 112;
        }

        async Task VerifyPositionAsync(int id)
        {
            string ini = access.PositionSettingsPath!;
            await File.WriteAllTextAsync(ini, "; keep\n[LUNCHMENU]\nPosition=240,180\nOther=keep\n");
            byte[] before = await File.ReadAllBytesAsync(ini);
            await PositionDialog(async dialog =>
            {
                Require(Named<TextBox>(dialog, "横座標").Text == "240", "位置の読込み");
                Named<TextBox>(dialog, "横座標").Text = "500";
                dialog.Close();
                await Task.CompletedTask;
            });
            byte[] afterCancel = await File.ReadAllBytesAsync(ini);
            Require(before.SequenceEqual(afterCancel), "位置の取消でINIを保持する");
            checks.Add("position-dialog-cancel-preserves-ini");

            await PositionDialog(async dialog =>
            {
                TextBox x = Named<TextBox>(dialog, "横座標");
                x.Text = "2147483648";
                Click(dialog, "保存");
                Require(dialog.IsVisible && Named<TextBlock>(dialog, "ランチ位置のエラー")
                    .Text.Length > 0, "位置の整数範囲外を拒否する");
                x.Text = "320";
                Named<TextBox>(dialog, "縦座標").Text = "220";
                Click(dialog, "保存");
                await WaitUntil(() => !dialog.IsVisible);
            });
            ApplicationLauncherSettings settings = await ApplicationLauncherSettings.LoadAsync(ini);
            Require(settings.X == 320 && settings.Y == 220
                && (await File.ReadAllTextAsync(ini)).Contains("Other=keep"), "位置だけを保存する");
            checks.Add("position-dialog-validates-and-preserves-other-keys");
            await PositionDialog(async dialog =>
            {
                Named<TextBox>(dialog, "横座標").Text = "600";
                using (var locked = new FileStream(ini, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    Click(dialog, "保存");
                    await WaitUntil(() => FindAll<Button>(dialog)
                        .Single(button => Equals(button.Content, "保存")).IsEnabled);
                    Require(dialog.IsVisible && Named<TextBlock>(dialog, "ランチ位置のエラー")
                        .Text.Contains("保存できません"), "位置の保存失敗を表示する");
                }
                dialog.Close();
            });
            Require((await ApplicationLauncherSettings.LoadAsync(ini)).X == 320,
                "位置の保存失敗で前の値を保持する");
            checks.Add("position-save-failure-keeps-editor-and-file");

            foreach ((int x, int y) in new[] { (320, 220), (480, 300) })
            {
                await new ApplicationLauncherSettings(x, y).SaveAsync(ini);
                await access.OpenFromHotkeyAsync(id);
                await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                ContextMenu menu = access.Menu!;
                Point origin = menu.PointToScreen(new Point());
                Require(Math.Abs(origin.X - x) <= 2 && Math.Abs(origin.Y - y) <= 2,
                    $"ランチの画面座標 expected={x},{y} actual={origin}");
                menu.IsOpen = false;
            }
            checks.Add("position-hotkey-reloads-ini-and-places-menu");
            foreach ((int x, int y) in new[] { (-10000, -10000), (10000, 10000) })
            {
                await new ApplicationLauncherSettings(x, y).SaveAsync(ini);
                await access.OpenFromHotkeyAsync(id);
                await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                ContextMenu menu = access.Menu!;
                var source = (HwndSource)PresentationSource.FromVisual(menu)!;
                using var dpi = new PhysicalDpiScope();
                nint monitor = Native.MonitorFromWindow(source.Handle, 0);
                var info = new Native.MonitorInfo
                {
                    Size = (uint)Marshal.SizeOf<Native.MonitorInfo>()
                };
                Require(menu.IsOpen && monitor != 0
                    && Native.GetMonitorInfoW(monitor, ref info)
                    && Native.GetWindowRect(source.Handle, out Native.Rect bounds)
                    && bounds.Left >= info.Monitor.Left && bounds.Top >= info.Monitor.Top
                    && bounds.Right <= info.Monitor.Right && bounds.Bottom <= info.Monitor.Bottom,
                    $"画面外の指定 {x},{y} でもランチ全体を画面内に表示する");
                menu.IsOpen = false;
            }
            checks.Add("offscreen-position-keeps-menu-on-monitor");
            await new ApplicationLauncherSettings(480, 300).SaveAsync(ini);
            main.Hide();
            try
            {
                await access.OpenFromHotkeyAsync(id);
                await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Point origin = access.Menu!.PointToScreen(new Point());
                Require(access.Menu!.IsOpen && Math.Abs(origin.X - 480) <= 2
                    && Math.Abs(origin.Y - 300) <= 2, "本体非表示でも固定位置に開く");
                access.Menu!.IsOpen = false;
            }
            finally
            {
                main.Show();
            }
            int errorCount = errors.Count;
            using (var locked = new FileStream(ini, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await access.OpenFromHotkeyAsync(id);
            }
            Require(errors.Count == errorCount + 1 && access.Menu?.IsOpen != true
                && access.AcceptsHotkey(id), "位置の読込み失敗で通知しキーを保持する");
            checks.Add("position-hidden-owner-and-read-failure");

            access.OpenMenu();
            Require(access.UsesButtonPlacement,
                "ボタンからの位置を維持する");
            access.Menu!.IsOpen = false;
            access.OpenMenu(atPointer: true);
            Require(access.Menu!.Placement == System.Windows.Controls.Primitives.PlacementMode.MousePoint,
                "トレイからの位置を維持する");
            access.Menu!.IsOpen = false;
            await new ApplicationLauncherSettings().SaveAsync(ini);
            await access.OpenFromHotkeyAsync(id);
            Require(access.Menu!.Placement == System.Windows.Controls.Primitives.PlacementMode.MousePoint,
                "-1,-1はカーソル位置を使う");
            access.Menu!.IsOpen = false;
            checks.Add("position-cursor-mode-and-other-entry-points");
        }

        async Task PositionDialog(Func<LauncherPositionWindow, Task> action)
        {
            Exception? failure = null;
            Task? operation = null;
            _ = main.Dispatcher.BeginInvoke(new Action(() => operation = RunAsync()),
                DispatcherPriority.ApplicationIdle);
            access.EditPosition();
            if (operation is not null)
            {
                await operation;
            }
            if (failure is not null)
            {
                throw failure;
            }

            async Task RunAsync()
            {
                LauncherPositionWindow dialog = Application.Current.Windows
                    .OfType<LauncherPositionWindow>().Single();
                try
                {
                    await WaitUntil(() => FindAll<Button>(dialog)
                        .Single(button => Equals(button.Content, "保存")).IsEnabled);
                    await action(dialog);
                }
                catch (Exception error)
                {
                    failure = error;
                    dialog.Close();
                }
            }
        }

        static async Task WaitUntil(Func<bool> predicate)
        {
            var timer = Stopwatch.StartNew();
            while (!predicate())
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(10))
                {
                    throw new TimeoutException("ランチ画面の状態待機が期限を超えました。");
                }
                await Task.Delay(20);
            }
        }

        void Modal<T>(Action<T> action, Action open) where T : Window
        {
            Exception? failure = null;
            main.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    T dialog = Application.Current.Windows.OfType<T>()
                        .Last(window => !ReferenceEquals(window, main));
                    action(dialog);
                }
                catch (Exception error)
                {
                    failure = error;
                    foreach (Window window in Application.Current.Windows.Cast<Window>()
                        .Where(window => !ReferenceEquals(window, main)).ToArray())
                    {
                        window.Close();
                    }
                }
            }), DispatcherPriority.ApplicationIdle);
            open();
            if (failure is not null)
            {
                throw failure;
            }
        }

        static T Named<T>(DependencyObject parent, string name) where T : DependencyObject =>
            FindAll<T>(parent).Single(item => AutomationProperties.GetName(item) == name);

        static void Click(DependencyObject parent, string text) => FindAll<Button>(parent)
            .Single(button => Equals(button.Content, text)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        static IEnumerable<T> FindAll<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent is T result)
            {
                yield return result;
            }

            foreach (DependencyObject child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            {
                foreach (T item in FindAll<T>(child))
                {
                    yield return item;
                }
            }
        }
    }

    private sealed class LauncherMenuHotkeyBackend : IHotkeyBackend
    {
        internal Dictionary<int, ScriptHotkey> Keys { get; } = [];

        internal HashSet<int> FailRelease { get; } = [];

        internal bool FailRegister
        {
            get; set;
        }

        public bool Register(int id, uint modifiers, int key)
        {
            if (FailRegister)
            {
                return false;
            }

            Keys.Add(id, new ScriptHotkey(key, (int)(modifiers & 15)));
            return true;
        }

        public bool Unregister(int id) => !FailRelease.Contains(id) && Keys.Remove(id);
    }
}
