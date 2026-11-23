using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using static UWSCSharp.Wpf.VerificationAssert;

namespace UWSCSharp.Wpf;

internal static class LauncherActivityVerification
{
    internal static async Task<int> RunAsync(string directory, string receipt,
        LauncherCommandKind targetKind = LauncherCommandKind.Executable)
    {
        Directory.CreateDirectory(directory);
        var errors = new List<Exception>();
        var checks = new List<string>();
        var children = new List<Process>();
        var main = new MainWindow(false, confirmDiscard: () => true,
            reportError: errors.Add, launcherSettingsPath: Path.Combine(directory, "launcher.json"),
            copyRecordingText: _ => throw new InvalidOperationException("コピーはこの検証で無効です。"));
        var access = new MainWindow.LauncherVerificationAccess(main);
        Task<int>? playing = null;
        try
        {
            main.Show();
            access.DisableRecordingCopy();
            bool scriptTarget = targetKind == LauncherCommandKind.Script;
            bool documentTarget = targetKind == LauncherCommandKind.Document;
            Dictionary<string, string>? documents = documentTarget
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(
                    await File.ReadAllTextAsync(Path.Combine(directory, "document-targets.json")))
                : null;
            string target = Path.Combine(directory, "targets",
                scriptTarget ? "子 script.uws" : "UWSCSharp.ControlFixture.exe");
            string[] literal = ["", "日本語 activity", "日本語 \"quoted\"", @"tail \", "a&b|c>file"];
            string ChildBase(string name) => Path.Combine(directory, "child-" + name);
            LauncherCommand Command(string name) => new(name, documentTarget ? documents![name] : target,
                targetKind,
                documentTarget ? [] : scriptTarget ? [ChildBase(name), .. literal]
                    : ["--launcher-child-probe", ChildBase(name) + ".json", ChildBase(name) + ".go", .. literal],
                AppContext.BaseDirectory);
            access.SaveSettings(new LauncherSettings([Command("play"), Command("record")]));
            string source = """
                DIM f=FOPEN("play.ready",F_WRITE8)
                FCLOSE(f)
                DIM started=GETTIME()
                WHILE !FOPEN("play.go",F_EXISTS)
                    IF GETTIME()-started>30 THEN EXITEXIT(2)
                    SLEEP(0.05)
                WEND
                EXITEXIT(0)
                """;
            string script = Path.Combine(directory, "running.uws");
            await File.WriteAllTextAsync(script, source, new UTF8Encoding(true));
            access.SetScript(script, source);
            playing = access.PlayAsync();
            await WaitFor(() => File.Exists(Path.Combine(directory, "play.ready"))
                && access.IsPlaying);
            await LaunchDuringAsync("play", 0);
            Require(access.IsPlaying && !playing.IsCompleted,
                "ランチ起動後も実際の再生を継続する");
            checks.Add("real-playback-keeps-running-through-menu-launch-and-blocks-registration");
            await File.WriteAllTextAsync(Path.Combine(directory, "play.go"), "go");
            Require(await playing.WaitAsync(TimeSpan.FromSeconds(10)) == 0, "実際の再生を正常終了する");
            playing = null;
            Require(access.CanEditSettings, "再生終了後は登録編集に戻る");
            checks.Add("playback-completion-restores-registration-editing");

            access.StartRecording();
            await WaitFor(() => access.HasRecorder && access.IsRecording
                && !access.IsStartingRecording && access.Status.StartsWith("記録中", StringComparison.Ordinal));
            await LaunchDuringAsync("record", 1);
            Require(access.HasRecorder && access.IsRecording
                && !access.RecordingRecoveryPending, "ランチ起動後も実際の記録を継続する");
            checks.Add("real-recorder-keeps-running-through-menu-launch-and-blocks-registration");
            await access.FinishRecordingAsync();
            Require(!access.HasRecorder && !access.RecordingRecoveryPending
                && access.CanEditSettings && errors.Count == 0, "記録終了と回収後は登録編集に戻る");
            checks.Add("recording-stop-cleans-up-and-restores-registration-editing");
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
            {
                result = "Passed",
                checks,
                real_playback = true,
                real_basic_recorder = true,
                target_kind = documentTarget ? "document" : scriptTarget ? "script" : "executable",
                mouse_input = false
            }));
            return 0;

            async Task LaunchDuringAsync(string name, int index)
            {
                Require(!access.CanEditSettings && access.CanOpenMenu,
                    "動作中は起動可能・登録編集禁止");
                access.SetOutput("ランチ起動前からある出力 " + name);
                object before = Snapshot();
                access.OpenMenu();
                MenuItem edit = access.Menu!.Items.OfType<MenuItem>()
                    .Single(item => Equals(item.Header, "登録を編集…"));
                Require(!edit.IsEnabled, "動作中の登録編集項目は無効");
                int windows = Application.Current.Windows.Count;
                access.EditSettings();
                Require(Application.Current.Windows.Count == windows, "編集の実行ガードでダイアログを開かない");
                var item = (MenuItem)access.Menu!.Items[index];
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                access.Menu!.IsOpen = false;
                string childBase = ChildBase(name);
                Process child;
                if (scriptTarget)
                {
                    await WaitFor(() => File.Exists(childBase + ".ready"));
                    string pid = await File.ReadAllTextAsync(childBase + ".pid");
                    child = Process.GetProcessById(int.Parse(pid.Trim(), CultureInfo.InvariantCulture));
                    children.Add(child);
                    string[] received = await File.ReadAllLinesAsync(childBase + ".args");
                    Require(received.SequenceEqual([Path.GetDirectoryName(target), .. literal]),
                        "実スクリプトの作業ディレクトリと全引数");
                }
                else
                {
                    await WaitFor(() => File.Exists(childBase + ".json"));
                    using var document = JsonDocument.Parse(await File.ReadAllTextAsync(childBase + ".json"));
                    JsonElement received = document.RootElement;
                    child = Process.GetProcessById(received.GetProperty("pid").GetInt32());
                    children.Add(child);
                    Require(received.GetProperty("working_directory").GetString()
                            == Path.GetDirectoryName(documentTarget ? documents![name] : target)
                        && received.GetProperty("arguments").EnumerateArray().Select(value => value.GetString())
                            .SequenceEqual(documentTarget ? [documents![name]] : literal),
                        "既定アプリまたは実EXEの作業ディレクトリと全引数");
                }

                Require(before.Equals(Snapshot()) && errors.Count == 0,
                    "動作中の原稿・出力・表示状態を保持");
                Require(name != "play" || !File.Exists(Path.Combine(directory, "play.go")), "子の解放前に本体を解放しない");
                await File.WriteAllTextAsync(childBase + ".go", "go");
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Require(name != "play" || !File.Exists(Path.Combine(directory, "play.go")), "子の解放後も本体を解放しない");
                Require(child.ExitCode == 0 && File.Exists(childBase + (scriptTarget ? ".done" : ".json.done")), "起動先の正常終了");
            }

            object Snapshot() => (access.Source, access.Output, access.ScriptPath,
                access.IsModified, access.IsReadOnly);
        }
        catch (Exception error) when (errors.Count > 0)
        {
            throw new AggregateException("動作中のランチ検証で通知されたエラー", [error, .. errors]);
        }
        finally
        {
            access.CancelPlayback();
            if (playing is not null)
            {
                await playing.WaitAsync(TimeSpan.FromSeconds(10));
            }

            if (access.HasRecorder)
            {
                await access.FinishRecordingAsync();
            }

            main.Close();
            foreach (Process child in children)
            {
                try
                {
                    if (!child.HasExited)
                    {
                        child.Kill(entireProcessTree: true);
                        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    }
                }
                finally
                {
                    child.Dispose();
                }
            }
        }

        static async Task WaitFor(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!condition())
            {
                await Task.Delay(20, timeout.Token);
            }
        }
    }
}
