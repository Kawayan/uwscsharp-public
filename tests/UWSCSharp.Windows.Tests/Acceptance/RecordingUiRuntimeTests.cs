using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

public sealed class RecordingUiRuntimeTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "SandboxOnly")]
    public async Task WpfRestoresSavedTrayStateOnNextProcessStart()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var fixture = AssociationLaunchFixture.Create(output.WriteLine);
        string state = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "UWSCSharp", "start-in-tray");
        Exception? primary = null;
        bool created = false;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            CancellationToken token = deadline.Token;
            Assert.False(File.Exists(state));
            created = true;
            string receipt = Path.Combine(fixture.DirectoryPath, "tray-exit.txt");
            Process saver = fixture.StartEditor("--verify-tray-exit-state", receipt);
            await saver.WaitForExitAsync(token);
            Assert.True(saver.ExitCode == 0, File.Exists(receipt + ".error")
                ? await File.ReadAllTextAsync(receipt + ".error", token) : "トレイ終了の診断");
            Assert.Equal("Passed", await File.ReadAllTextAsync(receipt, token));
            Assert.True(File.Exists(state));

            Process first = fixture.StartEditor("--single-instance");
            nint firstWindow = await FindMainWindowAsync(first, token);
            for (int sample = 0; sample < 10; sample++)
            {
                Assert.False(Native.IsWindowVisible(firstWindow));
                Assert.False(Native.IsIconic(firstWindow));
                await Task.Delay(30, token);
            }

            Process sender = fixture.StartEditor("--single-instance");
            await sender.WaitForExitAsync(token);
            Assert.Equal(0, sender.ExitCode);
            Assert.True(Native.IsWindowVisible(firstWindow));
            Assert.True(Native.PostMessageW(firstWindow, 0x10, 0, 0));
            await first.WaitForExitAsync(token);
            Assert.Equal(0, first.ExitCode);
            Assert.False(File.Exists(state));

            Process next = fixture.StartEditor();
            nint nextWindow = await FindMainWindowAsync(next, token);
            while (!Native.IsWindowVisible(nextWindow))
            {
                Assert.False(next.HasExited);
                await Task.Delay(30, token);
            }

            Assert.False(Native.IsIconic(nextWindow));
            Assert.True(Native.PostMessageW(nextWindow, 0x10, 0, 0));
            await next.WaitForExitAsync(token);
            Assert.Equal(0, next.ExitCode);
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            try
            {
                await fixture.CleanupAsync(primary);
            }
            finally
            {
                if (created)
                {
                    File.Delete(state);
                }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "SandboxOnly")]
    public async Task WpfMinimizeToTrayRestoresTheSameWindow(bool startInTray)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var fixture = AssociationLaunchFixture.Create(output.WriteLine);
        Exception? primary = null;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            CancellationToken token = deadline.Token;
            Process owner = startInTray
                ? fixture.StartEditor("--start-in-tray", "--minimize-to-tray", "--single-instance")
                : fixture.StartEditor("--minimize-to-tray", "--single-instance");
            nint window = await FindMainWindowAsync(owner, token);
            if (startInTray)
            {
                Assert.False(Native.IsWindowVisible(window));
                Assert.False(Native.IsIconic(window));
                await RestoreAsync();
            }

            while (!Native.IsWindowVisible(window))
            {
                await Task.Delay(30, token);
            }

            for (int attempt = 0; attempt < 2; attempt++)
            {
                Assert.True(Native.PostMessageW(window, 0x112, 0xf020, 0));
                while (Native.IsWindowVisible(window))
                {
                    Assert.False(owner.HasExited);
                    await Task.Delay(30, token);
                }

                await RestoreAsync();
            }

            Assert.True(Native.PostMessageW(window, 0x10, 0, 0));
            await owner.WaitForExitAsync(token);
            Assert.Equal(0, owner.ExitCode);

            async Task RestoreAsync()
            {
                Process sender = fixture.StartEditor("--single-instance");
                await sender.WaitForExitAsync(token);
                Assert.Equal(0, sender.ExitCode);
                Assert.True(Native.IsWindowVisible(window));
                Assert.False(Native.IsIconic(window));
            }
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            await fixture.CleanupAsync(primary);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "SandboxOnly")]
    public async Task SingleInstanceRunRejectsRequestsDuringActivity(bool recording)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var fixture = AssociationLaunchFixture.Create(output.WriteLine);
        Exception? primary = null;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            CancellationToken token = deadline.Token;
            const string activeFile = "active.uws";
            const string incomingFile = "incoming.uws";
            string active = Path.Combine(fixture.AppDirectory, activeFile);
            string incoming = Path.Combine(fixture.AppDirectory, incomingFile);
            string release = Path.Combine(fixture.AppDirectory, "release.txt");
            string receipt = Path.Combine(fixture.AppDirectory, "incoming.txt");
            await File.WriteAllTextAsync(active, """
                WHILE !FOPEN("release.txt",F_EXISTS)
                    SLEEP(0.05)
                WEND
                """, Encoding.UTF8, token);
            await File.WriteAllTextAsync(incoming, """
                DIM f=FOPEN("incoming.txt",F_WRITE8)
                FPUT(f,PARAM_STR[0],0)
                FCLOSE(f)
                """, Encoding.UTF8, token);

            Process owner = recording ? fixture.StartEditor("--single-instance")
                : fixture.StartEditor("--single-instance", "--run", activeFile);
            nint main = await FindMainWindowAsync(owner, token);
            if (!recording)
            {
                // 初回実行の開始表示を待たず、後続の読込み要求を送ります。
                Process early = fixture.StartEditor("--single-instance", incomingFile);
                await early.WaitForExitAsync(token);
                Assert.Equal(1, early.ExitCode);
            }

            await AssociationEditorIsolationTests.SnapshotAsync(main, expand: true,
                token: token, invokeButton: recording ? "RecordButton" : null);
            string status = recording ? "記録中" : "実行中";
            AssociationEditorIsolationTests.EditorSnapshot before =
                await WaitForStatusAsync(status);
            if (!recording)
            {
                Assert.Equal(active, before.File);
            }

            Process rejected = fixture.StartEditor("--single-instance", "--run",
                incomingFile, "拒否");
            await rejected.WaitForExitAsync(token);
            Assert.Equal(1, rejected.ExitCode);
            Assert.False(File.Exists(receipt));
            Assert.Equal(before, await AssociationEditorIsolationTests.SnapshotAsync(
                main, token: token));

            if (recording)
            {
                await AssociationEditorIsolationTests.SnapshotAsync(main,
                    token: token, invokeButton: "RecordButton");
                await WaitForStatusAsync("記録終了");
                await AssociationEditorIsolationTests.SnapshotAsync(main,
                    draft: "PRINT 123", token: token);
            }
            else
            {
                await File.WriteAllTextAsync(release, "", token);
                await WaitForStatusAsync("実行が完了しました");
            }

            Process accepted = fixture.StartEditor("--single-instance", "--run",
                incomingFile, "受付");
            if (recording)
            {
                await AnswerDiscardAsync(main, owner.Id, accept: true, token);
            }

            await accepted.WaitForExitAsync(token);
            Assert.Equal(0, accepted.ExitCode);
            await WaitForStatusAsync("実行が完了しました");
            Assert.Equal("受付", (await File.ReadAllTextAsync(receipt, token)).Trim());

            async Task<AssociationEditorIsolationTests.EditorSnapshot> WaitForStatusAsync(
                string expected)
            {
                while (true)
                {
                    Assert.False(owner.HasExited);
                    AssociationEditorIsolationTests.EditorSnapshot editor =
                        await AssociationEditorIsolationTests.SnapshotAsync(
                            main, token: token);
                    if (editor.Status.StartsWith(expected, StringComparison.Ordinal))
                    {
                        return editor;
                    }

                    await Task.Delay(30, token);
                }
            }
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            await fixture.CleanupAsync(primary);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "SandboxOnly")]
    public async Task SingleInstanceRunPreservesScriptArguments(bool existing)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var fixture = AssociationLaunchFixture.Create(output.WriteLine);
        Exception? primary = null;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            const string filename = "実行 引数.uws";
            string script = Path.Combine(fixture.AppDirectory, filename);
            string receipt = Path.Combine(fixture.AppDirectory, "run-result.txt");
            await File.WriteAllTextAsync(script, """
                DIM f=FOPEN("run-result.txt",F_WRITE8)
                FPUT(f,PARAM_STR[0],0)
                FPUT(f,PARAM_STR[1],0)
                FPUT(f,PARAM_STR[2],0)
                FCLOSE(f)
                """, Encoding.UTF8, deadline.Token);
            Process? owner = null;
            nint originalWindow = 0;
            if (existing)
            {
                owner = fixture.StartEditor("--single-instance", "--start-in-tray");
                originalWindow = await FindMainWindowAsync(owner, deadline.Token);
                Assert.False(Native.IsWindowVisible(originalWindow));
            }

            string[] arguments = ["", "日本語 空白", "引用\"符\\"];
            Process sender = fixture.StartEditor("--single-instance", "--run",
                filename, arguments[0], arguments[1], arguments[2]);
            if (existing)
            {
                await sender.WaitForExitAsync(deadline.Token);
                Assert.Equal(0, sender.ExitCode);
            }

            while (!File.Exists(receipt))
            {
                Assert.False((owner ?? sender).HasExited);
                await Task.Delay(30, deadline.Token);
            }

            string[] actual = [];
            while (actual.Length < arguments.Length)
            {
                try
                {
                    actual = await File.ReadAllLinesAsync(receipt, deadline.Token);
                }
                catch (IOException)
                {
                    // スクリプトがファイルを閉じるまで待ちます。
                }

                await Task.Delay(30, deadline.Token);
            }

            Assert.Equal(arguments, actual);
            Assert.False((owner ?? sender).HasExited);
            if (owner is not null)
            {
                Assert.Equal(originalWindow,
                    await FindMainWindowAsync(owner, deadline.Token));
                Assert.True(Native.IsWindowVisible(originalWindow));
                Assert.True(Native.PostMessageW(originalWindow, 0x10, 0, 0));
                await owner.WaitForExitAsync(deadline.Token);
                Assert.Equal(0, owner.ExitCode);
                Assert.False(Native.IsWindow(originalWindow));

                Process restarted = fixture.StartEditor(
                    "--single-instance", "--start-in-tray");
                nint restartedWindow = await FindMainWindowAsync(
                    restarted, deadline.Token);
                Assert.False(Native.IsWindowVisible(restartedWindow));
                Process restore = fixture.StartEditor("--single-instance");
                await restore.WaitForExitAsync(deadline.Token);
                Assert.Equal(0, restore.ExitCode);
                Assert.True(Native.IsWindowVisible(restartedWindow));
                Assert.True(Native.PostMessageW(restartedWindow, 0x10, 0, 0));
                await restarted.WaitForExitAsync(deadline.Token);
                Assert.Equal(0, restarted.ExitCode);
            }
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            await fixture.CleanupAsync(primary);
        }
    }

    [Fact]
    [Trait("Category", "SandboxOnly")]
    public async Task WpfExecutableRegistersAndRemovesLogonStartup()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        const string name = "UWSCSharp.Wpf";
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run");
        object? prior = key.GetValue(name, null,
            RegistryValueOptions.DoNotExpandEnvironmentNames);
        RegistryValueKind kind = prior is null
            ? RegistryValueKind.None : key.GetValueKind(name);
        string executable = FixturePaths.Get("UWSCSharp.Wpf", "UWSCSharp.Wpf.exe");
        try
        {
            string? command = null;
            foreach (bool enable in new[] { true, true, false, false })
            {
                (int code, string output) = await SafeArrayFrontendTests.RunAsync(
                    executable, [enable ? "--enable-logon-startup" : "--disable-logon-startup"]);
                Assert.True(code == 0, output);
                object? value = key.GetValue(name);
                if (enable)
                {
                    Assert.Equal($"\"{executable}\" --single-instance --start-in-tray",
                        value);
                    Assert.Equal(RegistryValueKind.String, key.GetValueKind(name));
                    command = Assert.IsType<string>(value);
                }
                else
                {
                    Assert.Null(value);
                }
            }

            string prefix = $"\"{executable}\" ";
            Assert.StartsWith(prefix, command);
            var registered = new ProcessStartInfo(executable, Assert.IsType<string>(command)[prefix.Length..])
            {
                UseShellExecute = false
            };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await VerifyRestartAsync(registered, terminate: false, deadline.Token,
                useRegisteredArguments: true);
        }
        finally
        {
            if (prior is null)
            {
                key.DeleteValue(name, throwOnMissingValue: false);
            }
            else
            {
                key.SetValue(name, prior, kind);
            }
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [Trait("Category", "SandboxOnly")]
    public async Task WpfExecutableStartsWithRequestedTrayState(
        bool startInTray, bool singleInstance, bool openOnStartup)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var start = new ProcessStartInfo(
            FixturePaths.Get("UWSCSharp.Wpf", "UWSCSharp.Wpf.exe"))
        {
            UseShellExecute = false
        };
        if (startInTray)
        {
            start.ArgumentList.Add("--start-in-tray");
        }

        if (singleInstance)
        {
            start.ArgumentList.Add("--single-instance");
        }

        string? initialFile = openOnStartup ? Path.Combine(Path.GetTempPath(),
            $"初回 {Guid.NewGuid():N}.uws") : null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        const string initialSource = "PRINT 789";
        Process? process = null;
        try
        {
            if (initialFile is not null)
            {
                await File.WriteAllTextAsync(initialFile, initialSource, Encoding.UTF8,
                    deadline.Token);
                start.WorkingDirectory = Path.GetDirectoryName(initialFile)!;
                start.ArgumentList.Add(Path.GetFileName(initialFile));
            }

            process = Process.Start(start)!;
            nint main = await FindMainWindowAsync(process, deadline.Token);

            if (startInTray)
            {
                for (int sample = 0; sample < 20; sample++)
                {
                    Assert.False(Native.IsWindowVisible(main));
                    Assert.False(Native.IsIconic(main));
                    await Task.Delay(25, deadline.Token);
                }
            }
            else
            {
                while (!Native.IsWindowVisible(main))
                {
                    Assert.False(process.HasExited);
                    await Task.Delay(25, deadline.Token);
                }
            }

            if (initialFile is not null)
            {
                AssociationEditorIsolationTests.EditorSnapshot editor =
                    await AssociationEditorIsolationTests.SnapshotAsync(
                        main, expand: true, token: deadline.Token);
                Assert.Equal(initialSource, editor.Source);
                Assert.Equal(initialFile, editor.File);
            }

            if (singleInstance && !openOnStartup)
            {
                start.ArgumentList.Clear();
                start.ArgumentList.Add("--single-instance");
                using Process second = Process.Start(start)!;
                try
                {
                    await second.WaitForExitAsync(deadline.Token);
                    Assert.Equal(0, second.ExitCode);
                    Assert.True(Native.IsWindowVisible(main));
                }
                finally
                {
                    if (!second.HasExited)
                    {
                        second.Kill(entireProcessTree: true);
                        await second.WaitForExitAsync();
                    }
                }

                string script = Path.Combine(Path.GetTempPath(),
                    $"転送 {Guid.NewGuid():N}.uws");
                try
                {
                    const string source = "PRINT 321";
                    await File.WriteAllTextAsync(script, source, Encoding.UTF8,
                        deadline.Token);
                    start.WorkingDirectory = Path.GetDirectoryName(script)!;
                    start.ArgumentList.Add(Path.GetFileName(script));
                    using Process forward = Process.Start(start)!;
                    try
                    {
                        await forward.WaitForExitAsync(deadline.Token);
                        Assert.Equal(0, forward.ExitCode);
                    }
                    finally
                    {
                        if (!forward.HasExited)
                        {
                            forward.Kill();
                            await forward.WaitForExitAsync();
                        }
                    }

                    AssociationEditorIsolationTests.EditorSnapshot editor =
                        await AssociationEditorIsolationTests.SnapshotAsync(
                            main, expand: true, token: deadline.Token);
                    Assert.Equal(source, editor.Source);
                    Assert.Equal(script, editor.File);
                    await VerifyConcurrentTransfersAsync(start, main,
                        deadline.Token);
                    start.ArgumentList[1] = Path.GetFileName(script);

                    const string draft = "PRINT 654";
                    AssociationEditorIsolationTests.EditorSnapshot edited =
                        await AssociationEditorIsolationTests.SnapshotAsync(
                            main, draft, token: deadline.Token);
                    using Process rejected = Process.Start(start)!;
                    try
                    {
                        await AnswerDiscardAsync(main, process.Id, accept: false,
                            deadline.Token);
                        await rejected.WaitForExitAsync(deadline.Token);
                        Assert.Equal(1, rejected.ExitCode);
                        Assert.Equal(edited,
                            await AssociationEditorIsolationTests.SnapshotAsync(
                                main, token: deadline.Token));
                        Assert.True(Native.PostMessageW(main, 0x10, 0, 0));
                        nint no = await WaitForDiscardButtonAsync(
                            main, process.Id, 7, deadline.Token);
                        using Process duringClose = Process.Start(start)!;
                        try
                        {
                            await duringClose.WaitForExitAsync(deadline.Token);
                            Assert.Equal(1, duringClose.ExitCode);
                            Assert.True(Native.PostMessageW(no, 0xf5, 0, 0));
                            while (Native.IsWindow(no))
                            {
                                await Task.Delay(25, deadline.Token);
                            }

                            Assert.Equal(edited,
                                await AssociationEditorIsolationTests.SnapshotAsync(
                                    main, token: deadline.Token));
                            using Process retry = Process.Start(start)!;
                            try
                            {
                                await AnswerDiscardAsync(main, process.Id,
                                    accept: true, deadline.Token);
                                await retry.WaitForExitAsync(deadline.Token);
                                Assert.Equal(0, retry.ExitCode);
                            }
                            finally
                            {
                                if (!retry.HasExited)
                                {
                                    retry.Kill();
                                    await retry.WaitForExitAsync();
                                }
                            }

                            await AssociationEditorIsolationTests.SnapshotAsync(
                                main, draft, token: deadline.Token);
                        }
                        finally
                        {
                            if (!duringClose.HasExited)
                            {
                                duringClose.Kill();
                                await duringClose.WaitForExitAsync();
                            }
                        }
                    }
                    finally
                    {
                        if (!rejected.HasExited)
                        {
                            rejected.Kill();
                            await rejected.WaitForExitAsync();
                        }
                    }
                }
                finally
                {
                    File.Delete(script);
                }
            }

            Assert.True(Native.PostMessageW(main, 0x10, 0, 0));
            if (singleInstance && !openOnStartup)
            {
                await AnswerDiscardAsync(main, process.Id, accept: true,
                    deadline.Token);
            }

            await process.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, process.ExitCode);

            if (singleInstance && !openOnStartup)
            {
                await VerifyRestartAsync(start, terminate: true, deadline.Token);
                await VerifyRestartAsync(start, terminate: false, deadline.Token);
            }
        }
        finally
        {
            if (process is not null)
            {
                using (process)
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync();
                    }
                }
            }

            if (initialFile is not null)
            {
                File.Delete(initialFile);
            }
        }
    }

    private static async Task<nint> FindMainWindowAsync(Process process,
        CancellationToken token)
    {
        nint main = 0;
        while (main == 0)
        {
            Assert.False(process.HasExited);
            Native.EnumWindows((window, _) =>
            {
                Native.GetWindowThreadProcessId(window, out uint owner);
                if (owner != process.Id)
                {
                    return true;
                }

                var title = new StringBuilder(128);
                Native.GetWindowTextW(window, title, title.Capacity);
                if (title.ToString() != "UWSC#（記録・一時停止 試作版）")
                {
                    return true;
                }

                main = window;
                return false;
            }, 0);
            await Task.Delay(25, token);
        }

        return main;
    }

    private static async Task VerifyRestartAsync(ProcessStartInfo start,
        bool terminate, CancellationToken token, bool useRegisteredArguments = false)
    {
        if (!useRegisteredArguments)
        {
            start.ArgumentList.Clear();
            start.ArgumentList.Add("--single-instance");
            start.ArgumentList.Add("--start-in-tray");
        }

        using Process restarted = Process.Start(start)!;
        try
        {
            nint main = await FindMainWindowAsync(restarted, token);
            Assert.False(Native.IsWindowVisible(main));

            var senderStart = new ProcessStartInfo(start.FileName)
            {
                UseShellExecute = false
            };
            senderStart.ArgumentList.Add("--single-instance");
            using Process sender = Process.Start(senderStart)!;
            try
            {
                await sender.WaitForExitAsync(token);
                Assert.Equal(0, sender.ExitCode);
                Assert.True(Native.IsWindowVisible(main));
            }
            finally
            {
                if (!sender.HasExited)
                {
                    sender.Kill();
                    await sender.WaitForExitAsync();
                }
            }

            if (terminate)
            {
                await TerminateDuringTransferAsync(senderStart, restarted, main, token);
            }
            else
            {
                Assert.True(Native.PostMessageW(main, 0x10, 0, 0));
            }

            await restarted.WaitForExitAsync(token);
            Assert.Equal(!terminate, restarted.ExitCode == 0);
        }
        finally
        {
            if (!restarted.HasExited)
            {
                restarted.Kill(entireProcessTree: true);
                await restarted.WaitForExitAsync();
            }
        }
    }

    private static async Task TerminateDuringTransferAsync(
        ProcessStartInfo start, Process receiver, nint main, CancellationToken token)
    {
        string path = Path.Combine(Path.GetTempPath(),
            $"終了時の転送 {Guid.NewGuid():N}.uws");
        try
        {
            await File.WriteAllTextAsync(path, "PRINT 456", Encoding.UTF8, token);
            await AssociationEditorIsolationTests.SnapshotAsync(
                main, "PRINT 123", expand: true, token: token);
            start.ArgumentList.Add(path);
            using Process sender = Process.Start(start)!;
            try
            {
                _ = await WaitForDiscardButtonAsync(main, receiver.Id, 7, token);
                Assert.False(sender.HasExited);
                receiver.Kill();
                await sender.WaitForExitAsync(token);
                Assert.Equal(1, sender.ExitCode);
            }
            finally
            {
                if (!sender.HasExited)
                {
                    sender.Kill();
                    await sender.WaitForExitAsync();
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task VerifyConcurrentTransfersAsync(
        ProcessStartInfo start, nint main, CancellationToken token)
    {
        var sources = new Dictionary<string, string>();
        var senders = new List<Process>();
        try
        {
            for (int index = 0; index < 4; index++)
            {
                string path = Path.Combine(start.WorkingDirectory,
                    $"同時転送 {Guid.NewGuid():N}.uws");
                string source = $"PRINT {index}";
                sources.Add(path, source);
                await File.WriteAllTextAsync(path, source, Encoding.UTF8, token);
            }

            foreach (string path in sources.Keys)
            {
                start.ArgumentList[1] = Path.GetFileName(path);
                senders.Add(Process.Start(start)!);
            }

            await Task.WhenAll(senders.Select(sender => sender.WaitForExitAsync(token)));
            Assert.All(senders, sender => Assert.Equal(0, sender.ExitCode));
            AssociationEditorIsolationTests.EditorSnapshot editor =
                await AssociationEditorIsolationTests.SnapshotAsync(main, token: token);
            Assert.True(sources.TryGetValue(editor.File, out string? expected),
                editor.File);
            Assert.Equal(expected, editor.Source);
        }
        finally
        {
            foreach (Process sender in senders)
            {
                using (sender)
                {
                    if (!sender.HasExited)
                    {
                        sender.Kill();
                        await sender.WaitForExitAsync();
                    }
                }
            }

            foreach (string path in sources.Keys)
            {
                File.Delete(path);
            }
        }
    }

    private static async Task AnswerDiscardAsync(nint main, int process,
        bool accept, CancellationToken token)
    {
        const int yes = 6;
        const int no = 7;
        nint button = await WaitForDiscardButtonAsync(main, process,
            accept ? yes : no, token);
        Assert.True(Native.PostMessageW(button, 0xf5, 0, 0));
    }

    private static async Task<nint> WaitForDiscardButtonAsync(
        nint main, int process, int buttonId, CancellationToken token)
    {
        nint button = 0;
        while (button == 0)
        {
            Native.EnumWindows((window, _) =>
            {
                Native.GetWindowThreadProcessId(window, out uint owner);
                if (owner != process || Native.GetWindow(window, 4) != main
                    || !Native.IsWindowVisible(window))
                {
                    return true;
                }

                button = MessageBoxIntegrationTests.GetDlgItem(window, buttonId);
                return button == 0;
            }, 0);
            if (button == 0)
            {
                await Task.Delay(25, token);
            }
        }

        return button;
    }

    [Theory]
    [InlineData("--verify-runtime", 16, false)]
    [InlineData("--verify-recording-os", 4, true)]
    [InlineData("--verify-tray", 10, true)]
    public async Task WpfRuntimeVerifiesRecordingFailureAndRecovery(
        string mode, int expectedChecks, bool windowShown)
        => await VerifyAsync(mode, expectedChecks, windowShown, false);

    // 停止設定の検証はホットキー登録を変更するため、Windows Sandbox内でだけ実行します。
    [Fact]
    [Trait("Category", "SandboxOnly")]
    public Task StopOptionsRestoreAfterCancellationAndCompletion()
        => VerifyAsync("--verify-stop-options", 4, true, false);

    [Fact]
    [Trait("Category", "SandboxOnly")]
    public Task WindowPositionPreservesVisibilityAndRestoration()
        => VerifyAsync("--verify-window-position", 8, true, false);

    [Fact]
    [Trait("Category", "SandboxOnly")]
    public Task WindowSettingsSaveAndApplyToMainWindow()
        => VerifyAsync("--verify-window-settings", 13, true, false);

    [Fact]
    [Trait("Category", "SandboxOnly")]
    public Task LogSettingsSaveAndApplyToNormalFrontends()
        => VerifyAsync("--verify-log-settings", 8, true, false);

    [Fact]
    [Trait("Category", "SandboxOnly")]
    public Task AssociationRegistrationAndOwnership()
        => VerifyAsync("--verify-association-registration", 18, true, false);

    [Fact]
    [Trait("Category", "SandboxOnly")]
    public Task BackgroundRecordingSavesAndReplaysFromMainWindow()
        => VerifyAsync("--verify-background-recording", 2, true, true);

    [Fact]
    [Trait("Category", "SandboxOnly")]
    public Task HighLevelRecordingSavesAndReplaysFromMainWindow()
        => VerifyAsync("--verify-high-level-recording", 2, true, true);

    [Fact]
    [Trait("Category", "SandboxOnly")]
    public Task RecordingClipboardCopiesOnlyEligibleResults()
        => VerifyAsync("--verify-recording-clipboard", 18, true, false);

    [Fact]
    [Trait("Category", "SandboxOnly")]
    public Task ScriptDropSingleFilePreparation()
        => VerifyAsync("--verify-script-drop", 20, true, false);

    [Fact]
    [Trait("Category", "SandboxOnly")]
    public Task ScriptDropShortcutExplorerTransfer()
        => VerifyAsync("--verify-script-drop-shortcuts-explorer", 6, true, true);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [Trait("Category", "SandboxOnly")]
    public Task ScriptDropExplorerTransfer(int attempt)
    {
        output.WriteLine("Explorer検証の反復: {0}", attempt);
        return VerifyAsync("--verify-script-drop-explorer", 6, true, true);
    }

    [Fact]
    [Trait("Category", "SandboxOnly")]
    public Task ScriptDropOleProcessTransfer()
        => VerifyAsync("--verify-script-drop-ole-process", 5, true, true);

    [Fact]
    [Trait("Category", "SandboxOnly")]
    public Task ScriptDropOleTransfer()
        => VerifyAsync("--verify-script-drop-ole", 5, true, true);

    [Fact]
    [Trait("Category", "SandboxOnly")]
    public Task RecordingCallbackTimeout()
        => VerifyAsync("--verify-recording-timeout", 2, true, true);

    [Fact]
    [Trait("Category", "SandboxOnly")]
    public Task ScriptHotkeySameWindowReleaseAndRerun()
        => VerifyAsync("--verify-script-hotkeys", 7, true, false);

    [Fact]
    [Trait("Category", "SandboxOnly")]
    public Task ScriptHotkeyPauseDefersUntilResume()
        => VerifyAsync("--verify-script-hotkey-pause", 9, true, true);

    private async Task VerifyAsync(string mode, int expectedChecks,
        bool windowShown, bool inputInjection)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "UWSCSharp.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        string configuration = Path.GetFileName(
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        string[] configurations = ["debug", "release"];
        Assert.Contains(configuration, configurations);
        string executable = Path.Combine(root.FullName,
            "artifacts", "bin", "UWSCSharp.Wpf", configuration, "UWSCSharp.Wpf.exe");
        Assert.True(File.Exists(executable), executable);
        string directory = Path.Combine(Path.GetTempPath(), "uwsc-recording-ui-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        string receipt = Path.Combine(directory, "runtime.json");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = windowShown ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(receipt);
        using var process = new Process { StartInfo = start };
        bool started = false;
        try
        {
            started = process.Start();
            Assert.True(started);
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
            output.WriteLine(await stdout);
            output.WriteLine(await stderr);
            if (File.Exists(receipt + ".error"))
            {
                output.WriteLine(await File.ReadAllTextAsync(receipt + ".error"));
            }

            Assert.True(File.Exists(receipt), "検証結果が生成されていません。");
            if (File.Exists(receipt + ".foreground"))
            {
                output.WriteLine(await File.ReadAllTextAsync(receipt + ".foreground"));
            }

            string json = await File.ReadAllTextAsync(receipt);
            output.WriteLine(json);
            using var document = JsonDocument.Parse(json);
            JsonElement result = document.RootElement;
            Assert.True(process.ExitCode == 0,
                $"終了コード {process.ExitCode}: {json}{DisconnectedSessionNote()}");
            Assert.Equal("PASS", result.GetProperty("result").GetString());
            string?[] checks =
            [
                .. result.GetProperty("checks").EnumerateArray()
                    .Select(item => item.GetString())
            ];
            Assert.Equal(expectedChecks, checks.Length);
            if (mode == "--verify-background-recording")
            {
                Assert.Contains("background-recording-setting-restored-after-restart",
                    checks);
            }

            if (mode == "--verify-high-level-recording")
            {
                Assert.Contains("high-level-recording-setting-restored-after-restart",
                    checks);
            }

            Assert.Contains(mode == "--verify-window-position"
                ? "position-rerun-after-cancellation"
                : mode == "--verify-window-settings"
                ? "editing-blocked-during-playback-and-restored-afterwards"
                : mode == "--verify-log-settings"
                ? "editing-is-blocked-during-playback-and-restored-afterwards"
                : mode == "--verify-association-registration"
                ? "conflict-preserves-changed-command"
                : mode == "--verify-background-recording"
                ? "background-recording-save-and-replay"
                : mode == "--verify-high-level-recording"
                ? "high-level-recording-save-and-replay"
                : mode == "--verify-recording-clipboard"
                ? "copy-manual-stop-exact-unicode-source"
                : mode == "--verify-script-drop-shortcuts-explorer"
                ? "shortcut-0"
                : mode == "--verify-script-drop-explorer"
                ? "single-first"
                : mode is "--verify-script-drop-ole" or "--verify-script-drop-ole-process"
                ? "ole-single-script-copy"
                : mode == "--verify-script-drop"
                ? "single-script-path-and-empty-arguments"
                : mode == "--verify-script-hotkey-pause"
                ? "paused-hotkey-0-delivered-after-resume"
                : mode == "--verify-script-hotkeys"
                ? "same-window-exit-7-registration-released"
                : mode == "--verify-stop-options"
                ? "normal-completion-restores-only-changed-options"
                : mode == "--verify-tray"
                ? "exit-disposes-notification-icon"
                : inputInjection
                ? "callback-timeout-stop-retry"
                : windowShown
                ? "native-release-delay-and-close-retry"
                : "recording-pending-stop-close-and-retry", checks);
            Assert.Equal(windowShown, result.GetProperty("window_shown").GetBoolean());
            Assert.Equal(inputInjection, result.GetProperty("input_injection").GetBoolean());
        }
        finally
        {
            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    // リモートデスクトップ切断中はWPFの描画結果が空になるため、失敗理由を判別できるようにします。
    private static string DisconnectedSessionNote()
    {
        const int Disconnected = 4;
        if (!WTSQuerySessionInformationW(0, -1, 8, out nint buffer, out int bytes))
        {
            return "";
        }

        try
        {
            return bytes >= sizeof(int) && Marshal.ReadInt32(buffer) == Disconnected
                ? Environment.NewLine + "現在のセッションはリモートデスクトップ切断中です。"
                    + "切断中はWPFの描画結果が空になるため、接続した状態で再実行してください。"
                : "";
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(
        nint server, int session, int infoClass, out nint buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(nint memory);
}
