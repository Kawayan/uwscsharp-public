using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using static UWSCSharp.Wpf.VerificationAssert;

namespace UWSCSharp.Wpf;

// 画面を表示せず、入力送信も行わずに配布EXE内の実装を検査します。
internal static class RuntimeVerification
{
    internal static async Task<int> RunAsync(string receiptPath)
    {
        var checks = new List<string>();
        var nativeModules = new Dictionary<string, string>();
        Uri? helpEntry = null;
        var helpFiles = new Dictionary<string, string>();
        string? error = null;
        try
        {
            var visual = new DrawingVisual();
            using (DrawingContext drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 2, 2));
            }

            var bitmap = new RenderTargetBitmap(2, 2, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            byte[] pixels = new byte[16];
            bitmap.CopyPixels(pixels, 8, 0);
            Require(pixels[2] == 255 && pixels[3] == 255, "WPF描画結果");
            using (var process = Process.GetCurrentProcess())
            {
                foreach (ProcessModule module in process.Modules)
                {
                    if (module.ModuleName is "coreclr.dll" or "wpfgfx_cor3.dll" or "PresentationNative_cor3.dll")
                    {
                        nativeModules.Add(module.ModuleName, module.FileName);
                    }
                }
            }

            Require(nativeModules.ContainsKey("PresentationNative_cor3.dll")
                && nativeModules.ContainsKey("wpfgfx_cor3.dll"), "ネイティブランタイム読込");
            checks.Add("bundled-wpf-rendering");

            string directory = Path.GetDirectoryName(Path.GetFullPath(receiptPath))!;
            helpEntry = VerifyHelp(directory);
            foreach ((string name, byte[] content) in LocalHelp.EmbeddedFiles())
            {
                helpFiles.Add(name, Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(content)));
            }
            checks.Add("embedded-help-extraction-button-f1-and-error-routing");
            string script = Path.Combine(directory, "検査用-" + Guid.NewGuid().ToString("N") + " script.uws");
            ScriptFileStore.Save(script, "PRINT \"日本語\"");
            byte[] original = File.ReadAllBytes(script);
            Require(original.AsSpan(0, 3).SequenceEqual(new byte[] { 239, 187, 191 }), "UTF-8 BOM");
            var output = new List<string>();
            await new Interpreter().RunAsync(Source.ReadFile(script), output.Add);
            Require(output.SequenceEqual(["日本語"]), "日本語実行結果");
            checks.Add("save-read-execute-japanese");
            using (var locked = new FileStream(script, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Expect<IOException>(() => ScriptFileStore.Save(script, "変更"));
            }

            Require(original.SequenceEqual(File.ReadAllBytes(script)), "保存失敗時の元ファイル保護");
            Require(Directory.GetFiles(directory, Path.GetFileName(script) + ".*.tmp").Length == 0, "一時ファイル解放");
            File.Delete(script);
            checks.Add("locked-save-preserves-original");

            using (var control = new ScriptPauseControl())
            using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                control.Pause();
                Task run = new Interpreter().RunAsync("PRINT 1", _ => { }, stop.Token,
                    new ScriptRunOptions([]) { PauseControl = control });
                Require(control.IsPaused && !run.IsCompleted, "一時停止");
                stop.Cancel();
                try
                {
                    await run;
                    throw new InvalidOperationException("一時停止中の取消が成功しません。");
                }
                catch (OperationCanceledException)
                {
                }
            }

            using (var control = new ScriptPauseControl())
            {
                control.Pause();
                output.Clear();
                Task run = new Interpreter().RunAsync("PRINT 2", output.Add, default,
                    new ScriptRunOptions([]) { PauseControl = control });
                Require(control.IsPaused && output.Count == 0, "再実行の一時停止");
                control.Resume();
                await run.WaitAsync(TimeSpan.FromSeconds(5));
                Require(output.SequenceEqual(["2"]), "再開後の実行結果");
            }

            checks.Add("paused-cancel-and-reexecute");

            for (int attempt = 0; attempt < 3; attempt++)
            {
                using var recorder = new BasicRecorder(0, false, [], 0, true);
                await recorder.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Require(recorder.IsRunning, "記録worker起動");
                _ = await Task.Run(recorder.Stop);
                Require(!recorder.IsRunning, "記録worker終了");
            }

            checks.Add("native-hooks-and-raw-input-start-stop-three-times");
            await RecordingUiVerification.VerifyAsync(checks);
            await InputCleanupUiVerification.VerifyAsync(checks);
            var hotkeyErrors = new List<Exception>();
            var hotkeyWindow = new MainWindow(false, reportError: hotkeyErrors.Add);
            try
            {
                HotkeySettingsUiVerification.Run(hotkeyWindow, directory,
                    checks, hotkeyErrors);
            }
            finally
            {
                hotkeyWindow.Close();
            }
        }
        catch (Exception failure)
        {
            error = failure.ToString();
        }

        File.WriteAllText(receiptPath, JsonSerializer.Serialize(new
        {
            result = error is null ? "PASS" : "FAIL",
            checks,
            error,
            native_modules = nativeModules,
            help_entry = helpEntry?.AbsoluteUri,
            help_files = helpFiles,
            runtime = Environment.Version.ToString(),
            framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            computer_use = false,
            input_injection = false,
            window_shown = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        return error is null ? 0 : 1;
    }

    private static Uri VerifyHelp(string directory)
    {
        var addresses = new List<Uri>();
        var errors = new List<Exception>();
        var window = new MainWindow(false, addresses.Add,
            Path.Combine(directory, "help-cache"), errors.Add);
        try
        {
            var button = (Button)window.FindName("HelpButton");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(addresses.Count == 1 && addresses[0].IsFile
                && File.Exists(addresses[0].LocalPath), "同梱ヘルプの展開とボタン");
            Require(window.TryHelpKey(Key.F1, ModifierKeys.None, false)
                && addresses.Count == 2, "F1ヘルプ");
            Require(window.TryHelpKey(Key.F1, ModifierKeys.None, true)
                && addresses.Count == 2, "F1の長押し抑止");
            Require(!window.TryHelpKey(Key.F1, ModifierKeys.Control, false),
                "修飾キー付きF1の非消費");
            button.IsEnabled = false;
            Require(!window.TryHelpKey(Key.F1, ModifierKeys.None, false),
                "操作禁止中のF1抑止");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(addresses.Count == 2 && errors.Count == 0,
                "操作禁止中のヘルプ抑止");
            Require(((TextBlock)window.FindName("StatusLabel")).Text == "待機中",
                "ヘルプ表示時の実行状態保持");
        }
        finally
        {
            window.Close();
        }

        var failure = new System.ComponentModel.Win32Exception("検証用起動失敗");
        window = new MainWindow(false, _ => throw failure,
            Path.Combine(directory, "help-cache"), errors.Add);
        try
        {
            ((Button)window.FindName("HelpButton"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(errors.Count == 1 && ReferenceEquals(errors[0], failure),
                "ブラウザー起動失敗の報告");
        }
        finally
        {
            window.Close();
        }

        return addresses[0];
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException($"期待した例外が発生しません: {typeof(T).Name}");
    }
}
