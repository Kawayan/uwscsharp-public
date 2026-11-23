using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using static UWSCSharp.Wpf.VerificationAssert;

namespace UWSCSharp.Wpf;

internal static class LauncherSessionVerification
{
    internal static async Task<int> RunAsync(
        string directory, string receipt, string phase)
    {
        string[] stage = phase.Split(':');
        Require(stage.Length == 2, "保存・復元の段階と種別を指定する");
        string kind = stage[1];
        LauncherCommandKind targetKind = kind switch
        {
            "script" => LauncherCommandKind.Script,
            "document" => LauncherCommandKind.Document,
            "executable" => LauncherCommandKind.Executable,
            _ => throw new InvalidOperationException("診断の種別が不正です。")
        };
        phase = stage[0];
        bool scriptTarget = targetKind == LauncherCommandKind.Script;
        bool documentTarget = targetKind == LauncherCommandKind.Document;
        Directory.CreateDirectory(directory);
        Dictionary<string, string>? documents = documentTarget
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(
                await File.ReadAllTextAsync(Path.Combine(directory, "document-targets.json")))
            : null;
        string ChildBase(string name) => Path.Combine(directory, "child-" + name);
        string settingsPath = Path.Combine(directory, "launcher.json");
        string target = Path.Combine(directory, "targets",
            scriptTarget ? "子 script.uws" : "UWSCSharp.ControlFixture.exe");
        string missing = Path.Combine(directory, "removed.uws");
        string[] literal = ["", "日本語 \"quotes\"", @"tail \", "a&b|c>file"];
        var errors = new List<Exception>();
        var main = new MainWindow(false, confirmDiscard: () => true,
            reportError: errors.Add, launcherSettingsPath: settingsPath);
        var access = new MainWindow.LauncherVerificationAccess(main);
        var children = new List<Process>();
        bool transferred = false;
        try
        {
            main.Show();
            if (phase == "save")
            {
                await File.WriteAllTextAsync(missing, "PRINT 1");
                LauncherCommand Command(string name) => new(name,
                    Path.GetRelativePath(AppContext.BaseDirectory, documentTarget ? documents![name] : target),
                    targetKind,
                    documentTarget ? [] : scriptTarget ? [ChildBase(name), .. literal]
                        : ["--launcher-child-probe", ChildBase(name) + ".json",
                            ChildBase(name) + ".go", .. literal], AppContext.BaseDirectory);
                LauncherGroup group = new LauncherGroup("保存する階層", [])
                    .WithSlot(0, Command("first"))
                    .WithSlot(17, new LauncherCommand("消失した対象", missing,
                        LauncherCommandKind.Script, [], AppContext.BaseDirectory))
                    .WithSlot(34, Command("second"));
                access.SaveSettings(new LauncherSettings().WithSlot(34, group));
                await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
                {
                    result = "Saved",
                    owner_pid = Environment.ProcessId
                }));
                return 0;
            }

            Require(phase == "run", "診断の段階を識別する");
            LauncherSettings loaded = access.Settings
                ?? throw new InvalidOperationException("保存した設定を読み込めません。");
            Require(loaded.Slots.Take(34).All(entry => entry is null)
                && loaded.Slots[34] is LauncherGroup, "上位35番目の階層を復元する");
            var restored = (LauncherGroup)loaded.Slots[34]!;
            Require(restored.Slots[0] is LauncherCommand && restored.Slots[17] is LauncherCommand
                && restored.Slots[34] is LauncherCommand
                && restored.Slots.Count(entry => entry is not null) == 3,
                "階層の空き位置と35番目の起動項目を復元する");
            Require(((LauncherCommand)restored.Slots[0]!).Kind == targetKind
                && ((LauncherCommand)restored.Slots[34]!).Kind == targetKind,
                "復元した起動項目の種別を保持する");
            var checks = new List<string> { "restart-restores-root-and-group-slot35" };
            byte[] saved = await File.ReadAllBytesAsync(settingsPath);
            access.SetSource("// 再起動後の未保存原稿");
            access.SetOutput("保持する実行結果");
            object Snapshot() => (access.Source, access.Output,
                access.Status, access.IsModified, access.ScriptPath);
            object before = Snapshot();

            access.ClickGroupChild(0);
            Process first = await ReadChildAsync("first");
            File.Delete(missing);
            access.ClickGroupChild(1);
            await WaitFor(() => errors.Count == 1);
            byte[] afterFailure = await File.ReadAllBytesAsync(settingsPath);
            Require(errors[0] is FileNotFoundException && before.Equals(Snapshot())
                && ReferenceEquals(loaded, access.Settings) && !first.HasExited
                && saved.SequenceEqual(afterFailure),
                "実際の消失ファイルで登録・原稿・出力と実行中の子を保持する");
            checks.Add("missing-file-keeps-registration-editor-output-and-running-child");

            access.ClickGroupChild(2);
            Process second = await ReadChildAsync("second");
            Require(first.Id != second.Id && !first.HasExited && !second.HasExited
                && before.Equals(Snapshot()), "別の子として連続起動する");
            checks.Add("group-menu-starts-two-independent-children-with-literal-arguments");
            await File.WriteAllTextAsync(ChildBase("first") + ".go", "go");
            await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Require(first.ExitCode == 0 && File.Exists(ChildBase("first") + (scriptTarget ? ".done" : ".json.done"))
                && !second.HasExited, "片方の正常終了は他方を止めない");
            checks.Add("first-child-completes-without-stopping-second");
            main.Close();
            Require(!second.HasExited && before.Equals(Snapshot()), "本体を閉じても子を止めない");
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
            {
                result = "Passed",
                target_kind = kind,
                checks,
                owner_pid = Environment.ProcessId,
                first_pid = first.Id,
                second_pid = second.Id
            }));
            transferred = true;
            return 0;

            async Task<Process> ReadChildAsync(string name)
            {
                string childBase = ChildBase(name);
                if (scriptTarget)
                {
                    await WaitFor(() => File.Exists(childBase + ".ready"));
                    string pid = await File.ReadAllTextAsync(childBase + ".pid");
                    var scriptChild = Process.GetProcessById(int.Parse(pid.Trim(), CultureInfo.InvariantCulture));
                    children.Add(scriptChild);
                    string[] received = await File.ReadAllLinesAsync(childBase + ".args");
                    Require(received.SequenceEqual([Path.GetDirectoryName(target), .. literal]),
                        "スクリプトで作業ディレクトリと全引数を照合する");
                    return scriptChild;
                }

                string path = childBase + ".json";
                await WaitFor(() => File.Exists(path));
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
                JsonElement root = document.RootElement;
                var child = Process.GetProcessById(root.GetProperty("pid").GetInt32());
                children.Add(child);
                Require(root.GetProperty("working_directory").GetString()
                        == Path.GetDirectoryName(documentTarget ? documents![name] : target)
                    && root.GetProperty("arguments").EnumerateArray()
                        .Select(value => value.GetString())
                        .SequenceEqual(documentTarget ? [documents![name]] : literal),
                    "起動先で作業ディレクトリと全引数を照合する");
                return child;
            }
        }
        finally
        {
            main.Close();
            foreach (Process child in children)
            {
                try
                {
                    if (!transferred && !child.HasExited)
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
