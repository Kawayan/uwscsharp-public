using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace UWSCSharp.Wpf;

internal static class LauncherProcessVerification
{
    internal static async Task<int> RunAsync(
        string settingsPath, string receipt, string release)
    {
        var errors = new List<Exception>();
        var window = new MainWindow(false, confirmDiscard: () => true,
            reportError: errors.Add);
        var access = new MainWindow.LauncherVerificationAccess(window);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            window.Show();
            access.SetSource("// 保持する未保存原稿 " + Guid.NewGuid());
            access.SetOutput("保持する実行結果");
            object Snapshot() => new
            {
                source = access.Source,
                output = access.Output,
                file = access.FileLabel,
                status = access.Status,
                path = access.ScriptPath,
                modified = access.IsModified,
                read_only = access.IsReadOnly
            };
            object before = Snapshot();
            LauncherSettings settings = LauncherSettingsStore.Load(settingsPath);
            var children = new List<int?>();
            var failures = new List<string>();
            foreach (LauncherCommand command in settings.Slots.OfType<LauncherCommand>())
            {
                try
                {
                    children.Add(LauncherProcessLauncher.Start(command, AppContext.BaseDirectory));
                }
                catch (Exception error) when (error is IOException
                    or System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    failures.Add(error.GetType().Name + ": " + error.Message);
                }
            }

            bool preserved = before.Equals(Snapshot()) && !closed && errors.Count == 0;
            string prepared = receipt + ".prepared";
            await File.WriteAllTextAsync(prepared, JsonSerializer.Serialize(new
            {
                owner_pid = Environment.ProcessId,
                children,
                failures,
                editor_preserved = preserved
            }));
            File.Move(prepared, receipt);
            var elapsed = Stopwatch.StartNew();
            while (!File.Exists(release))
            {
                if (elapsed.Elapsed > TimeSpan.FromSeconds(60))
                {
                    throw new TimeoutException("ランチ検証の終了指示が届きませんでした。");
                }

                await Task.Delay(25);
            }

            // 子が待機中のまま終了指示を受けた時点でも、原稿と表示を照合する。
            preserved &= before.Equals(Snapshot()) && !closed && errors.Count == 0;
            window.Close();
            await File.WriteAllTextAsync(receipt + ".closed", JsonSerializer.Serialize(new
            {
                editor_preserved = preserved,
                closed
            }));
            return preserved && closed ? 0 : 1;
        }
        finally
        {
            if (!closed)
            {
                window.Close();
            }
        }
    }
}
