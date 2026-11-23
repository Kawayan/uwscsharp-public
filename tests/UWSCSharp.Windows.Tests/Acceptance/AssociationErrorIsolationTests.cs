using System.Diagnostics;
using System.Text;
using UWSCSharp.Windows;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class AssociationErrorIsolationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ErrorDialogKeepsOtherExecutionIndependent(bool missingFile)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var fixture = AssociationLaunchFixture.Create();
        Exception? primary = null;
        try
        {
            string directory = Path.Combine(fixture.DirectoryPath, "日本語 scripts");
            Directory.CreateDirectory(directory);
            string healthyScript = Path.Combine(directory, "正常な実行.uws");
            await File.WriteAllTextAsync(healthyScript, """
                DIM f=FOPEN("healthy.ready",F_WRITE8)
                FCLOSE(f)
                WHILE !FOPEN("healthy.go",F_EXISTS)
                    SLEEP(0.05)
                WEND
                f=FOPEN("healthy.result",F_WRITE8)
                FPUT(f,PARAM_STR[0],0)
                FCLOSE(f)
                EXITEXIT(0)
                """, new UTF8Encoding(true));
            Process healthy = fixture.Start(healthyScript, "独立した引数");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while (!File.Exists(Path.Combine(directory, "healthy.ready")))
            {
                Assert.False(healthy.HasExited);
                await Task.Delay(50, timeout.Token);
            }

            string failingScript = Path.Combine(directory,
                missingFile ? "存在しない script.uws" : "未処理例外.uws");
            if (!missingFile)
            {
                await File.WriteAllTextAsync(failingScript, "PRINT 1/0",
                    new UTF8Encoding(true));
            }

            Process failing = fixture.Start(failingScript);
            Assert.NotEqual(healthy.Id, failing.Id);
            nint dialog = 0;
            while (dialog == 0)
            {
                Assert.False(failing.HasExited, "エラー画面を表示する前に終了しました。");
                dialog = FindOwnedDialog(failing.Id);
                if (dialog == 0)
                {
                    await Task.Delay(50, timeout.Token);
                }
            }

            nint owner = Native.GetWindow(dialog, 4);
            Assert.NotEqual(nint.Zero, owner);
            Native.GetWindowThreadProcessId(owner, out uint ownerProcess);
            Assert.Equal((uint)failing.Id, ownerProcess);
            Assert.True(Native.IsWindowVisible(owner));
            Assert.False(Native.IsWindowEnabled(owner));
            string message = ReadDialogText(dialog);
            Assert.Contains(missingFile ? Path.GetFileName(failingScript)
                : "ゼロで除算できません。", message);

            Assert.False(healthy.HasExited);
            Assert.False(File.Exists(Path.Combine(directory, "healthy.result")));
            await File.WriteAllTextAsync(Path.Combine(directory, "healthy.go"), "go");
            await healthy.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, healthy.ExitCode);
            Assert.Equal("独立した引数",
                (await File.ReadAllTextAsync(Path.Combine(directory, "healthy.result"))).Trim());

            // 別プロセスの正常終了後にも、今回のエラー画面が残っていることを確認する。
            Assert.False(failing.HasExited);
            Assert.True(Native.IsWindow(dialog));
            nint button = FindAcceptButton(dialog);
            Assert.NotEqual(nint.Zero, button);
            Assert.True(Native.PostMessageW(button, 0x00f5, 0, 0));
            while (Native.IsWindow(dialog) || !Native.IsWindowEnabled(owner))
            {
                Assert.False(failing.HasExited);
                await Task.Delay(50, timeout.Token);
            }

            Assert.True(Native.IsWindowVisible(owner));
            Assert.True(failing.CloseMainWindow());
            await failing.WaitForExitAsync(timeout.Token);
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

    private static nint FindOwnedDialog(int processId)
    {
        var matches = new List<nint>();
        Native.EnumWindows((window, data) =>
        {
            Native.GetWindowThreadProcessId(window, out uint process);
            var name = new StringBuilder(64);
            Native.GetClassNameW(window, name, name.Capacity);
            if (process == (uint)processId && name.ToString() == "#32770"
                && Native.IsWindowVisible(window))
            {
                matches.Add(window);
            }

            return true;
        }, 0);
        Assert.True(matches.Count <= 1, "所有するエラー画面が複数あります。");
        return matches.Count == 0 ? 0 : matches[0];
    }

    private static string ReadDialogText(nint dialog)
    {
        var messages = new List<string>();
        bool readSucceeded = true;
        Native.EnumChildWindows(dialog, (window, data) =>
        {
            var name = new StringBuilder(64);
            Native.GetClassNameW(window, name, name.Capacity);
            if (name.ToString() == "Static")
            {
                var text = new StringBuilder(4096);
                nint read = Native.ReadText(window,
                    Native.WmGetText, text.Capacity, text,
                    Native.SmtoAbortIfHung, 1000, out _);
                readSucceeded &= read != 0;
                if (text.Length > 0)
                {
                    messages.Add(text.ToString());
                }
            }

            return true;
        }, 0);
        Assert.True(readSucceeded, "エラー本文の読取りが失敗しました。");
        return string.Join("\n", messages);
    }

    private nint FindAcceptButton(nint dialog)
    {
        Assert.NotEqual(nint.Zero, Native.SendMessageTimeoutW(dialog,
            0x0400, 0, 0, Native.SmtoAbortIfHung, 1000, out nuint defaultButton));
        output.WriteLine($"dialog-default=0x{defaultButton:x}");
        Assert.Equal(0x534bu, (uint)(defaultButton >> 16));
        var matches = new List<nint>();
        var candidates = new List<string>();
        Native.EnumChildWindows(dialog, (window, data) =>
        {
            var name = new StringBuilder(64);
            Native.RealGetWindowClassW(window, name, (uint)name.Capacity);
            int id = Native.GetDlgCtrlID(window);
            candidates.Add($"id={id},class={name}");
            if (name.ToString().Equals("Button", StringComparison.OrdinalIgnoreCase)
                && Native.IsWindowVisible(window) && Native.IsWindowEnabled(window))
            {
                matches.Add(window);
            }

            return true;
        }, 0);
        output.WriteLine(string.Join(";", candidates));
        nint button = Assert.Single(matches);
        var text = new StringBuilder(128);
        Assert.NotEqual(nint.Zero, Native.ReadText(button,
            Native.WmGetText, text.Capacity, text,
            Native.SmtoAbortIfHung, 1000, out _));
        string caption = text.ToString().Replace("&", "", StringComparison.Ordinal).Trim();
        output.WriteLine($"accept-button={Native.GetDlgCtrlID(button)},caption={caption}");
        Assert.Equal("OK", caption);
        return button;
    }
}
