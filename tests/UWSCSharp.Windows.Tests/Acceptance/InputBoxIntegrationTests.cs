using System.Runtime.InteropServices;
using System.Text;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class InputBoxIntegrationTests
{
    [Fact]
    public async Task StoppingScriptClosesInputOwnedByThread()
    {
        string title = "uwsc-input-thread-stop-" + Guid.NewGuid();
        using var stop = new CancellationTokenSource();
        List<string> output = [];
        Task run = new Interpreter(windows: new WindowsAutomation()).RunAsync($$"""
            OPTION DLGTITLE="{{title}}"
            THREAD Worker()
            WHILE TRUE
                SLEEP(0.01)
            WEND
            PROCEDURE Worker()
                answer = INPUT("入力", "initial")
                PRINT "returned"
            FEND
            """, output.Add, stop.Token);

        try
        {
            nint window = await MessageBoxIntegrationTests.FindWindowAsync(title);
            nint edit = MessageBoxIntegrationTests.GetDlgItem(window, 201);
            Assert.NotEqual(0, edit);
            Assert.Equal("initial", MessageBoxIntegrationTests.ReadText(edit));

            await stop.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => run.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(Native.IsWindow(window));
            Assert.Empty(output);
        }
        finally
        {
            if (!run.IsCompleted)
            {
                await stop.CancelAsync();
                await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5)));
            }
        }
    }

    [Theory]
    [InlineData(4097, "001.50", 0)]
    [InlineData(4098, "canceled", 0)]
    [InlineData(4097, "", 2048)]
    [InlineData(4097, "", 16384, false)]
    [InlineData(4098, "canceled", 16384, true)]
    public async Task ScriptThreadReceivesInputResult(
        int buttonId, string expected, int dropCount, bool targetEdit = true)
    {
        string title = "uwsc-input-thread-" + Guid.NewGuid();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        List<string> output = [];
        Task run = new Interpreter(windows: new WindowsAutomation()).RunAsync($$"""
            OPTION DLGTITLE = "{{title}}"
            PUBLIC done = FALSE
            THREAD Worker()
            WHILE done = FALSE
                SLEEP(0.01)
            WEND
            PROCEDURE Worker()
                answer = INPUT("入力", "001.50")
                IF VARTYPE(answer) = VAR_EMPTY THEN
                    PRINT "canceled"
                ELSE
                    PRINT answer
                ENDIF
                done = TRUE
            FEND
            """, output.Add, timeout.Token);

        try
        {
            nint window = await MessageBoxIntegrationTests.FindWindowAsync(title);
            if (dropCount > 0)
            {
                nint edit = MessageBoxIntegrationTests.GetDlgItem(window, 201);
                Assert.NotEqual(0, edit);
                string[] paths = [.. Enumerable.Range(0, dropCount)
                    .Select(index => $@"C:\日本語 path\folder\項目-{index:D4}-😀.txt")];
                string dropped = string.Join("\t", paths);
                Assert.True(dropped.Length > ushort.MaxValue);
                string actual = await ReadAfterPostingDropAsync(
                    targetEdit ? edit : window, edit, "001.50", paths,
                    "001.50".Length + dropped.Length, timeout.Token);
                Assert.Equal("001.50" + dropped, actual);
                if (buttonId == 4097)
                {
                    expected = actual;
                }
            }

            nint button = MessageBoxIntegrationTests.GetDlgItem(window, buttonId);
            Assert.NotEqual(0, button);
            Assert.True(Native.PostMessageW(button, 0xf5, 0, 0));
            await run;
            Assert.Equal([expected], output);
            Assert.False(Native.IsWindow(window));
        }
        finally
        {
            if (!run.IsCompleted)
            {
                await timeout.CancelAsync();
                await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5)));
            }
        }
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("001.50", false)]
    [InlineData("1e3", false)]
    [InlineData("日本語😀", false)]
    [InlineData(" 01 \t", false)]
    [InlineData("secret", true)]
    public async Task AcceptingTheDefaultPreservesTextAndPasswordDisplay(string value, bool password)
    {
        await using var service = new NativeDialogService();
        string title = "uwsc-input-" + Guid.NewGuid();
        Task<Value> show = service.ShowInputAsync(new object(),
            new("確認", value, password, null, null), new(title), 0, default);
        nint window = await MessageBoxIntegrationTests.FindWindowAsync(title);
        nint edit = MessageBoxIntegrationTests.GetDlgItem(window, 201);
        Assert.NotEqual(0, edit);
        Assert.Equal(value, MessageBoxIntegrationTests.ReadText(edit));
        Assert.NotEqual(0, Native.SendMessageTimeoutW(edit, 0xd2, 0, 0, 2, 1000, out nuint glyph));
        Assert.Equal(password, glyph != 0);
        Native.PostMessageW(edit, 0x100, 13, 0);
        Native.PostMessageW(edit, 0x101, 13, 0);
        Value result = await show.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ValueKind.String, result.Kind);
        Assert.Equal(value, result.Text);
    }

    [Theory]
    [InlineData("close")]
    [InlineData("escape")]
    [InlineData("cancel")]
    public async Task UserCancellationReturnsEmptyInsteadOfAnEmptyString(string action)
    {
        await using var service = new NativeDialogService();
        string title = "uwsc-input-cancel-" + Guid.NewGuid();
        Task<Value> show = service.ShowInputAsync(new object(),
            new("確認", "initial", false, null, null), new(title), 0, default);
        nint window = await MessageBoxIntegrationTests.FindWindowAsync(title);
        if (action == "close")
        {
            Native.PostMessageW(window, 0x10, 0, 0);
        }
        else if (action == "cancel")
        {
            Native.PostMessageW(MessageBoxIntegrationTests.GetDlgItem(window, 4098), 0xf5, 0, 0);
        }
        else
        {
            Native.PostMessageW(MessageBoxIntegrationTests.GetDlgItem(window, 201), 0x100, 27, 0);
        }

        Assert.Equal(ValueKind.Empty, (await show.WaitAsync(TimeSpan.FromSeconds(10))).Kind);
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    [InlineData(true, 1024)]
    [InlineData(false, 16384, true)]
    [InlineData(true, 16384, true)]
    public async Task FirstDropAppendsUnicodePathsJoinedWithTabs(
        bool targetEdit, int count, bool longFirstPath = false)
    {
        await using var service = new NativeDialogService();
        string title = "uwsc-input-drop-" + Guid.NewGuid();
        Task<Value> show = service.ShowInputAsync(new object(),
            new("確認", "initial", false, null, null), new(title), 0, default);
        nint window = await MessageBoxIntegrationTests.FindWindowAsync(title);
        nint edit = MessageBoxIntegrationTests.GetDlgItem(window, 201);
        string[] paths = [.. Enumerable.Range(0, count)
            .Select(index => $@"C:\日本語 path\folder\項目-{index:D4}-😀.txt")];
        if (longFirstPath)
        {
            paths[0] = @"C:\日本語 path\"
                + string.Concat(Enumerable.Repeat(@"長いフォルダー\", 32))
                + "先頭-😀.txt";
            Assert.True(paths[0].Length > 260);
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string expected = "initial" + string.Join("\t", paths);
        Assert.Equal(expected, await ReadAfterPostingDropAsync(
            targetEdit ? edit : window, edit, "initial", paths,
            expected.Length, deadline.Token));
        Native.PostMessageW(MessageBoxIntegrationTests.GetDlgItem(window, 4097), 0xf5, 0, 0);
        Value result = await show.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ValueKind.String, result.Kind);
        Assert.Equal(expected, result.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "SandboxOnly")]
    public async Task LaterDropsReplaceTextEvenAfterEditing(bool firstToEdit)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using var service = new NativeDialogService();
        string title = "uwsc-input-repeat-drop-" + Guid.NewGuid();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        CancellationToken token = deadline.Token;
        Task<Value> show = service.ShowInputAsync(new object(),
            new("確認", "edited", false, null, null), new(title), 0, token);
        nint window = await MessageBoxIntegrationTests.FindWindowAsync(title);
        nint edit = MessageBoxIntegrationTests.GetDlgItem(window, 201);
        Assert.NotEqual(0, edit);
        string first = @"C:\日本語 A.txt";
        string second = @"C:\B😀.txt";
        string appended = "edited" + first;
        Assert.Equal(appended, await ReadAfterPostingDropAsync(
            firstToEdit ? edit : window, edit, "edited", [first],
            appended.Length, token));

        nint revised = Marshal.StringToHGlobalUni("revised");
        try
        {
            Assert.NotEqual(0, Native.SendMessageW(edit, 0x0c, 0, revised));
        }
        finally
        {
            Marshal.FreeHGlobal(revised);
        }

        Assert.Equal(second, await ReadAfterPostingDropAsync(
            firstToEdit ? window : edit, edit, "revised", [second],
            second.Length, token));
        string expected = first + "\t" + second;
        Assert.Equal(expected, await ReadAfterPostingDropAsync(window, edit,
            second, [first, second], expected.Length, token));
        Assert.True(Native.PostMessageW(
            MessageBoxIntegrationTests.GetDlgItem(window, 4097), 0xf5, 0, 0));
        Assert.Equal(Value.String(expected), await show.WaitAsync(token));
    }

    [Fact]
    public async Task EditedTextAndPreviousInputPositionAreIndependentOfMessageBox()
    {
        await using var service = new NativeDialogService();
        string title = "uwsc-input-position-" + Guid.NewGuid();
        Task<Value> show = service.ShowInputAsync(new object(),
            new("確認", "initial", false, 80, 90), new(title), 0, default);
        nint window = await MessageBoxIntegrationTests.FindWindowAsync(title);
        nint edit = MessageBoxIntegrationTests.GetDlgItem(window, 201);
        nint text = Marshal.StringToHGlobalUni("変更した文字列");
        try
        {
            Assert.NotEqual(0, Native.SendMessageTimeoutW(edit, 0xc, 0, text, 2, 1000, out _));
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }

        Native.PostMessageW(MessageBoxIntegrationTests.GetDlgItem(window, 4097), 0xf5, 0, 0);
        Assert.Equal("変更した文字列", (await show.WaitAsync(TimeSpan.FromSeconds(10))).Text);
        Task<int> message = service.ShowMessageAsync(new object(),
            new("確認", 1, 200, 210, 0, false), new(title), 0, default);
        window = await MessageBoxIntegrationTests.FindWindowAsync(title);
        Native.PostMessageW(window, 0x10, 0, 0);
        await message.WaitAsync(TimeSpan.FromSeconds(10));
        show = service.ShowInputAsync(new object(),
            new("確認", "", false, -1, -1), new(title), 0, default);
        window = await MessageBoxIntegrationTests.FindWindowAsync(title);
        using (var dpi = new PhysicalDpiScope())
        {
            Assert.True(Native.GetWindowRect(window, out Native.Rect rect));
            Assert.Equal(80, rect.Left);
            Assert.Equal(90, rect.Top);
        }

        Native.PostMessageW(window, 0x10, 0, 0);
        await show.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task NativeUnicodeKeystrokesReplaceTheInitialSelection()
    {
        await using var service = new NativeDialogService();
        string title = "uwsc-input-keys-" + Guid.NewGuid();
        Task<Value> show = service.ShowInputAsync(new object(),
            new("確認", "initial", false, null, null), new(title), 0, default);
        nint window = await MessageBoxIntegrationTests.FindWindowAsync(title);
        nint edit = MessageBoxIntegrationTests.GetDlgItem(window, 201);
        await UiInputDriver.ActivateAsync(window);
        var info = new Native.GuiThreadInfo { Size = (uint)Marshal.SizeOf<Native.GuiThreadInfo>() };
        Assert.True(Native.GetGUIThreadInfo(Native.GetWindowThreadProcessId(window, out _), ref info));
        Assert.Equal(edit, info.Focus);
        foreach (char character in "編集😀")
        {
            foreach (uint flags in new uint[] { 4, 6 })
            {
                NativeInputSender.Send(new Native.Input
                {
                    Type = 1,
                    Data = new Native.InputUnion
                    {
                        Keyboard = new Native.KeyboardInput { Scan = character, Flags = flags }
                    }
                });
            }
        }

        foreach (uint flags in new uint[] { 0, 2 })
        {
            NativeInputSender.Send(new Native.Input
            {
                Type = 1,
                Data = new Native.InputUnion
                {
                    Keyboard = new Native.KeyboardInput { Vk = 13, Flags = flags }
                }
            });
        }

        Assert.Equal("編集😀", (await show.WaitAsync(TimeSpan.FromSeconds(10))).Text);
    }

    [Fact]
    public async Task CallerCancellationAndSessionDisposalCloseOwnedInput()
    {
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        string title = "uwsc-input-session-" + Guid.NewGuid();
        session.ConfigureDialogOption(Keywords.DlgTitle, [Value.String(title)], default);
        using var cancellation = new CancellationTokenSource();
        Task<Value> first = session.InvokeAsync(BuiltinFunctionNames.Input,
            [Value.String("確認")], cancellation.Token).AsTask();
        await MessageBoxIntegrationTests.FindWindowAsync(title);
        cancellation.Cancel();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            first.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Task<Value> second = session.InvokeAsync(BuiltinFunctionNames.Input,
            [Value.String("確認")], default).AsTask();
        await MessageBoxIntegrationTests.FindWindowAsync(title);
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
    }

    private static async Task<string> ReadAfterPostingDropAsync(
        nint target, nint edit, string initial, string[] paths,
        int expectedLength, CancellationToken token)
    {
        // 長さの照会と本文の取得の間で、入力が更新される競合を避けます。
        var buffer = new StringBuilder(
            Math.Max(expectedLength, initial.Length) + 2);
        using var drop = FileDropMemory.Create(paths, 0, 0, token);
        drop.Post(target, token);
        while (true)
        {
            buffer.Clear();
            _ = Native.GetWindowTextW(edit, buffer, buffer.Capacity);
            string actual = buffer.ToString();
            if (actual != initial)
            {
                return actual;
            }

            await Task.Delay(10, token);
        }
    }
}
