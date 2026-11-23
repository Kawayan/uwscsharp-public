using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using UWSCSharp.Core;
using UWSCSharp.IO;
using static UWSCSharp.Wpf.SettingsVerification;
using static UWSCSharp.Wpf.VerificationAssert;

namespace UWSCSharp.Wpf;

internal static class LogSettingsVerification
{
    internal static async Task<int> RunAsync(string receipt)
    {
        // ゲストの既存INIを退避し、個人設定のあるホストで実行しません。
        if (Environment.UserName != "WDAGUtilityAccount")
        {
            throw new InvalidOperationException("ログ設定の検証はSandbox専用です。");
        }

        string directory = Path.GetDirectoryName(Path.GetFullPath(receipt))!;
        string path = IniPath;
        byte[]? previous = File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;
        var checks = new List<string>();
        string? error = null;
        var main = new MainWindow(false, confirmDiscard: () => true,
            reportError: failure => throw failure);
        var access = new MainWindow.SettingsVerificationAccess(main);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            main.Show();
            await Modal(async dialog =>
            {
                Require(Box(dialog, "MaximumLines").Text == "2000"
                    && Box(dialog, "FontFamilyName").Text == "ＭＳ ゴシック"
                    && Box(dialog, "FontSizePoints").Text == "10", "未設定時の表示");
                dialog.Close();
                await Task.CompletedTask;
            });
            Require(!File.Exists(path), "取消でINIを作らない");
            checks.Add("defaults-and-cancel-do-not-create-settings");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path,
                "; keep\n[SET]\nLogLines=2\nLogFont=Arial,10\nOther=kept\n",
                new UTF8Encoding(true));
            byte[] before = await File.ReadAllBytesAsync(path);
            await Modal(async dialog =>
            {
                Box(dialog, "MaximumLines").Text = "0";
                ClickSave(dialog);
                Require(Error(dialog).Text.Length > 0, "不正な行数を拒否");
                Box(dialog, "MaximumLines").Text = "3";
                Box(dialog, "FontSizePoints").Text = "NaN";
                ClickSave(dialog);
                byte[] afterInvalid = await File.ReadAllBytesAsync(path);
                Require(dialog.IsVisible && before.SequenceEqual(afterInvalid),
                    "不正サイズで元INIを保持");
                Box(dialog, "FontFamilyName").Text = "Consolas";
                Box(dialog, "FontSizePoints").Text = "12.5";
                ClickSave(dialog);
                await WaitUntil(() => !dialog.IsVisible);
            });
            ScriptLogDefaults saved = await ScriptLogDefaults.LoadAsync();
            Require(saved.MaximumLines == 3 && saved.Font.Family == "Consolas"
                && saved.Font.SizePoints == 12.5
                && (await File.ReadAllTextAsync(path)).Contains("Other=kept"), "2キーを保存");
            checks.Add("validation-and-two-key-save-preserve-other-settings");

            before = await File.ReadAllBytesAsync(path);
            using (var locked = new FileStream(path, FileMode.Open,
                FileAccess.Read, FileShare.None))
            {
                await Modal(async dialog =>
                {
                    Require(!Save(dialog).IsEnabled && !Box(dialog, "MaximumLines").IsEnabled
                        && Error(dialog).Text.Length > 0, "読込み失敗で編集と保存を拒否");
                    dialog.Close();
                    await Task.CompletedTask;
                }, allowLoadError: true);
            }

            checks.Add("read-failure-disables-editing-and-saving");

            await Modal(async dialog =>
            {
                using var locked = new FileStream(path, FileMode.Open,
                    FileAccess.Read, FileShare.Read);
                Box(dialog, "MaximumLines").Text = "4";
                ClickSave(dialog);
                await WaitUntil(() => Save(dialog).IsEnabled);
                Require(dialog.IsVisible && Error(dialog).Text.Length > 0, "保存失敗は画面を残す");
                dialog.Close();
            });
            byte[] afterFailure = await File.ReadAllBytesAsync(path);
            Require(before.SequenceEqual(afterFailure), "保存失敗で両方の値を保持");
            checks.Add("failed-save-keeps-dialog-and-original-bytes");

            string script = Path.Combine(directory, "log-settings.uws");
            string source = "OPTION LOGFILE=6\nPRINT 1\nPRINT 2\nPRINT 3\nPRINT 4";
            int code = await main.RunCommandLineAsync(source,
                new ScriptRunOptions([]) { SourcePath = script });
            Require(code == 0 && access.LogText == "2\r\n3\r\n4\r\n"
                && access.LogFont.Family == "Consolas"
                && access.LogFont.SizePoints == 12.5,
                "通常WPFで保存値を適用");
            code = await main.RunCommandLineAsync("OPTION LOGLINES=1\n" + source,
                new ScriptRunOptions([]) { SourcePath = script });
            byte[] afterWpf = await File.ReadAllBytesAsync(path);
            Require(code == 0 && access.LogText == "4\r\n"
                && before.SequenceEqual(afterWpf), "OPTION優先とINI非変更");
            checks.Add("wpf-next-run-uses-saved-settings-and-option-priority");

            await File.WriteAllTextAsync(script, source, new UTF8Encoding(true));
            await VerifyFrontendAsync("UWSCSharp.Cli", script, "2\r\n3\r\n4\r\n");
            await VerifyFrontendAsync("UWSCSharp.Wpf", script, "2\r\n3\r\n4\r\n");
            await File.WriteAllTextAsync(script, "OPTION LOGLINES=1\n" + source,
                new UTF8Encoding(true));
            await VerifyFrontendAsync("UWSCSharp.Cli", script, "4\r\n");
            byte[] afterCli = await File.ReadAllBytesAsync(path);
            Require(before.SequenceEqual(afterCli), "CLIのOPTIONでINI非変更");
            checks.Add("cli-reloads-saved-settings-and-option-priority");
            await VerifyFrontendAsync("UWSCSharp.Wpf", script, "4\r\n");
            checks.Add("restarted-wpf-reloads-saved-settings-and-option-priority");

            using (var stop = new CancellationTokenSource())
            {
                access.SetPlayback(stop);
                Require(!access.CanEditLogSettings, "再生中は設定不可");
                access.OpenLogSettings();
                Require(!Application.Current.Windows.OfType<LogSettingsWindow>().Any(), "再生中の入口拒否");
                access.SetPlayback(null);
                Require(access.CanEditLogSettings, "終了後は設定可能");
            }

            checks.Add("editing-is-blocked-during-playback-and-restored-afterwards");
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

        Task Modal(Func<LogSettingsWindow, Task> action,
            bool allowLoadError = false)
            => RunSettingsModalAsync(main.Dispatcher,
                access.ClickLogSettings, action, allowLoadError,
                "設定の読込み", "ログ設定の処理が完了しませんでした。");

        static TextBox Box(LogSettingsWindow dialog, string name) => (TextBox)dialog.FindName(name);
        static Task WaitUntil(Func<bool> predicate)
            => WaitUntilAsync(predicate,
                "ログ設定の処理が完了しませんでした。");

        static async Task VerifyFrontendAsync(string frontend, string script, string expected)
        {
            string configuration = new DirectoryInfo(AppContext.BaseDirectory).Name;
            string executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", frontend, configuration, frontend + ".exe"));
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            start.ArgumentList.Add(script);
            using Process process = Process.Start(start)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Require(process.ExitCode == 0, await stderr);
                _ = await stdout;
                Require(await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(script)!, "UWSC.LOG"))
                    == expected, frontend + "で設定とOPTIONを適用");
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
        }
    }
}
