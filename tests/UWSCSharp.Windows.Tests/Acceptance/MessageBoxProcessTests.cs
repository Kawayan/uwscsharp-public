using System.Diagnostics;
using System.Text;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class MessageBoxProcessTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task StopButtonCancelsPendingSelection(bool wpf, bool popup)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null
            && !File.Exists(Path.Combine(root.FullName, "UWSCSharp.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        string configuration = Path.GetFileName(
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        string name = wpf ? "UWSCSharp.Wpf" : "UWSCSharp.Cli";
        string executable = Path.Combine(root.FullName, "artifacts", "bin",
            name, configuration, name + ".exe");
        DirectoryInfo directory =
            Directory.CreateTempSubdirectory("uwsc-selection-stop-");
        string title = "uwsc-selection-stop-" + Guid.NewGuid();
        try
        {
            string script = Path.Combine(directory.FullName, "selection.uws");
            string select = popup
                ? "DIM items[1]=\"A\",\"B\"\nanswer=POPUPMENU(items,500,300)"
                : "answer=SLCTBOX(SLCT_LST OR SLCT_NUM,0,\"選択\",\"A\",\"B\")";
            await File.WriteAllTextAsync(script, $$"""
                OPTION DLGTITLE="{{title}}"
                STOPFORM(TRUE,20,20)
                PRINT "started"
                {{select}}
                DIM f=FOPEN("result.txt",F_WRITE8)
                FPUT(f,answer,0)
                FCLOSE(f)
                PRINT answer
                """, new UTF8Encoding(true));
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = directory.FullName
            };
            start.ArgumentList.Add(script);
            using Process process = Process.Start(start)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            try
            {
                nint selection = popup
                    ? await PopupMenuIntegrationTests.FindPopupAsync(process.Id)
                    : await MessageBoxIntegrationTests
                        .FindWindowAsync(title, process.Id);
                nint stop = await MessageBoxIntegrationTests.FindWindowAsync(
                    "UWSC# 再生コントロール", process.Id);
                if (wpf)
                {
                    using var timeout = new CancellationTokenSource(
                        TimeSpan.FromSeconds(10));
                    Assert.True(await UiaControl.InvokeAsync(
                        stop, "■ 停止", 1, timeout.Token)
                        .WaitAsync(timeout.Token));
                }
                else
                {
                    nint button =
                        MessageBoxIntegrationTests.GetDlgItem(stop, 1);
                    Assert.NotEqual(0, button);
                    if (popup)
                    {
                        Assert.True(Native.PostMessageW(button, 0xf5, 0, 0));
                    }
                    else
                    {
                        ClickOwnedStopButton(process.Id, button);
                    }
                }
                await process.WaitForExitAsync()
                    .WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(process.ExitCode == 130,
                    $"{process.ExitCode}\n{await stdout}\n{await stderr}");
                if (!wpf)
                {
                    Assert.Equal("started", (await stdout).Trim());
                }

                Assert.False(File.Exists(
                    Path.Combine(directory.FullName, "result.txt")));
                Assert.False(Native.IsWindow(selection));
                Assert.False(Native.IsWindow(stop));
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
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static void ClickOwnedStopButton(int processId, nint button)
    {
        var display = new NativeDisplayPlatform();
        Native.Point previous = display.CursorPosition();
        using var dpi = new PhysicalDpiScope();
        Assert.True(Native.GetWindowRect(button, out Native.Rect bounds));
        var point = new Native.Point(
            (bounds.Left + bounds.Right) / 2,
            (bounds.Top + bounds.Bottom) / 2);
        Assert.Equal(button, Native.WindowFromPoint(point));
        Native.GetWindowThreadProcessId(button, out uint owner);
        Assert.Equal((uint)processId, owner);
        try
        {
            Assert.True(Native.SetCursorPos(point.X, point.Y));
            foreach (uint flags in new uint[] { 2, 4 })
            {
                NativeInputSender.Send(new Native.Input
                {
                    Type = 0,
                    Data = new Native.InputUnion
                    {
                        Mouse = new Native.MouseInput { Flags = flags }
                    }
                });
            }
        }
        finally
        {
            Native.SetCursorPos(previous.X, previous.Y);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CliAndWpfReturnDialogResultsFromCalledScript(
        bool wpf, bool nested)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "UWSCSharp.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        string configuration = Path.GetFileName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        string name = wpf ? "UWSCSharp.Wpf" : "UWSCSharp.Cli";
        string binaryDirectory = Path.Combine(root.FullName, "artifacts", "bin", name, configuration);
        string executable = Path.Combine(binaryDirectory, name + ".exe");
        Assert.True(File.Exists(executable), executable);
        string directory = Path.Combine(Path.GetTempPath(), "uwsc-b3-process-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        string title = "uwsc-process-msg-" + Guid.NewGuid();
        try
        {
            string main = Path.Combine(directory, "日本語 main.uws");
            await File.WriteAllTextAsync(main, """
                CALL child
                DIM f=FOPEN("result.txt",F_WRITE8)
                FPUT(f,answer,0)
                FCLOSE(f)
                """, new UTF8Encoding(true));
            await File.WriteAllTextAsync(Path.Combine(directory, "child.uws"), $$"""
                OPTION DLGTITLE="{{title}}", DEFAULTFONT="Arial,18", POSITION=100,120
                PUBLIC answer=0
                answer=MSGBOX("日本語の本文",BTN_YES+BTN_NO,,,BTN_NO)
                answer=answer+"|"+VARTYPE(answer)
                OPTION DLGTITLE="{{title}}-input"
                answer=answer+"|"+INPUT("入力確認","001.50",TRUE,80,90)
                OPTION DLGTITLE="{{title}}-selection"
                answer=answer+"|"+SLCTBOX(SLCT_BTN+SLCT_NUM,0,,"A","B")
                {{(nested
                    ? "DIM menu[3]=\"操作\",\"{開く\",\"保存}\",\"終了\""
                    : "DIM menu[2]=\"第一\",\"第二\",\"第三\"")}}
                answer=answer+"|"+POPUPMENU(menu,500,300)
                """, new UTF8Encoding(true));
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = binaryDirectory
            };
            start.ArgumentList.Add(main);
            using Process process = Process.Start(start)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            try
            {
                nint window = await MessageBoxIntegrationTests.FindWindowAsync(title, process.Id);
                nint button = MessageBoxIntegrationTests.GetDlgItem(window, 4104);
                Assert.NotEqual(0, button);
                Assert.True(Native.PostMessageW(button, 0xf5, 0, 0));
                window = await MessageBoxIntegrationTests.FindWindowAsync(title + "-input", process.Id);
                button = MessageBoxIntegrationTests.GetDlgItem(window, 4097);
                Assert.NotEqual(0, button);
                Assert.True(Native.PostMessageW(button, 0xf5, 0, 0));
                window = await MessageBoxIntegrationTests.FindWindowAsync(title + "-selection", process.Id);
                button = MessageBoxIntegrationTests.GetDlgItem(window, 0x5001);
                Assert.NotEqual(0, button);
                Assert.True(Native.PostMessageW(button, 0xf5, 0, 0));
                nint popup = await PopupMenuIntegrationTests
                    .FindPopupAsync(process.Id);
                PopupMenuIntegrationTests.SendMenuKey(0x28, process.Id);
                if (nested)
                {
                    PopupMenuIntegrationTests.SendMenuKey(0x27, process.Id);
                }

                PopupMenuIntegrationTests.SendMenuKey(0x28, process.Id);
                PopupMenuIntegrationTests.SendMenuKey(0x0d, process.Id);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
                Assert.True(process.ExitCode == 0, $"{process.ExitCode}\n{await stdout}\n{await stderr}");
                Assert.False(Native.IsWindow(popup));
                Assert.Equal(nested ? "8|3|001.50|1|2" : "8|3|001.50|1|1",
                    (await File.ReadAllTextAsync(
                    Path.Combine(directory, "result.txt"))).Trim());
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
        finally
        {
            // 所有するプロセスが終了してから、この試験の作業フォルダを削除します。
            Directory.Delete(directory, recursive: true);
        }
    }
}
