using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using UWSCSharp.Core;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

public sealed class MessageBoxIntegrationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(2, true)]
    public async Task PublishedMessageBoxReturnsItsTypedResult(
        int value, bool close = false)
    {
        await using IWindowsAutomationSession session =
            new WindowsAutomation().CreateSession();
        string title = "uwsc-msg-" + Guid.NewGuid();
        session.ConfigureDialogOption(Keywords.DlgTitle, [Value.String(title)], default);
        Task<Value> show = session.InvokeAsync(BuiltinFunctionNames.MsgBox,
            [Value.String("確認"), Value.Numeric(close ? 1 : 127)],
            default).AsTask();
        nint window = 0;
        try
        {
            window = await FindWindowAsync(title);
            if (close)
            {
                Assert.True(Native.PostMessageW(window, 0x10, 0, 0));
            }
            else
            {
                nint button = GetDlgItem(window, 4096 + value);
                Assert.NotEqual(0, button);
                Assert.True(Native.PostMessageW(button, 0xf5, 0, 0));
            }

            Value result = await show.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(Value.Numeric(value), result);
            Assert.Equal((ushort)3, result.NativeVariantType);
            Assert.Equal(value, result.GetUnchangedNativeScalar());
            await WaitForDestructionAsync(window);
        }
        finally
        {
            if (window != 0 && Native.IsWindow(window))
            {
                Native.PostMessageW(window, 0x10, 0, 0);
            }
        }
    }

    [Theory]
    [InlineData(13, 8)]
    [InlineData(27, 2)]
    public async Task KeyboardAndCloseUseThePublishedResult(int key, int expected)
    {
        await using var service = new NativeDialogService();
        string title = "uwsc-key-" + Guid.NewGuid();
        Task<int> show = service.ShowMessageAsync(new object(),
            MessageBoxRequest.Read([Value.String("確認"), Value.Numeric(12),
                new(ValueKind.Omitted), new(ValueKind.Omitted), Value.Numeric(8)]),
            new(title), 0, default);
        nint window = await FindWindowAsync(title);
        nint button = GetDlgItem(window, 4104);
        Assert.True(Native.PostMessageW(button, 0x100, key, 0));
        Assert.True(Native.PostMessageW(button, 0x101, key, 0));
        Assert.Equal(expected, await show.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task ClosingAnOkOnlyWindowReturnsCancelAndReusesItsPosition()
    {
        await using var service = new NativeDialogService();
        string title = "uwsc-position-" + Guid.NewGuid();
        Task<int> first = service.ShowMessageAsync(new object(),
            new("確認", 1, 80, 90, 0, false), new(title), 0, default);
        nint window = await FindWindowAsync(title);
        using (var dpi = new PhysicalDpiScope())
        {
            Assert.True(Native.GetWindowRect(window, out Native.Rect rect));
            Assert.Equal(80, rect.Left);
            Assert.Equal(90, rect.Top);
        }

        Native.PostMessageW(window, 0x10, 0, 0);
        Assert.Equal(2, await first.WaitAsync(TimeSpan.FromSeconds(10)));
        Task<int> second = service.ShowMessageAsync(new object(),
            new("確認", 1, -1, -1, 0, false), new(title), 0, default);
        window = await FindWindowAsync(title);
        using (var dpi = new PhysicalDpiScope())
        {
            Assert.True(Native.GetWindowRect(window, out Native.Rect rect));
            Assert.Equal(80, rect.Left);
            Assert.Equal(90, rect.Top);
        }

        Native.PostMessageW(window, 0x10, 0, 0);
        Assert.Equal(2, await second.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task ExecutionCancellationDestroysTheWindowAndPreservesTheToken()
    {
        await using var service = new NativeDialogService();
        using var cancellation = new CancellationTokenSource();
        string title = "uwsc-cancel-" + Guid.NewGuid();
        Task<int> show = service.ShowMessageAsync(new object(),
            new("確認", 1, null, null, 0, false), new(title), 0, cancellation.Token);
        nint window = await FindWindowAsync(title);
        cancellation.Cancel();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            show.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        await WaitForDestructionAsync(window);
    }

    [Fact]
    public async Task IndependentOwnersRemainOpenUntilTheirOwnClose()
    {
        await using var service = new NativeDialogService();
        object firstOwner = new();
        object secondOwner = new();
        string firstTitle = "uwsc-owner-a-" + Guid.NewGuid();
        string secondTitle = "uwsc-owner-b-" + Guid.NewGuid();
        Task<int> first = service.ShowMessageAsync(firstOwner,
            new("first", 1, null, null, 0, false), new(firstTitle), 0, default);
        Task<int> second = service.ShowMessageAsync(secondOwner,
            new("second", 1, null, null, 0, false), new(secondTitle), 0, default);
        nint firstWindow = await FindWindowAsync(firstTitle);
        nint secondWindow = await FindWindowAsync(secondTitle);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await service.CloseOwnerAsync(firstOwner, canceled.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(Native.IsWindow(firstWindow));
        Assert.True(Native.IsWindow(secondWindow));
        Native.PostMessageW(secondWindow, 0x10, 0, 0);
        Assert.Equal(2, await second.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task LongTextFitsTheWorkAreaAndUsesTheSameFontForButtons()
    {
        await using var service = new NativeDialogService();
        string title = "uwsc-long-" + Guid.NewGuid();
        string message = new('W', 10000);
        Task<int> show = service.ShowMessageAsync(new object(),
            new(message, 1, null, null, 0, false), new(title, "Arial", 18), 0, default);
        nint window = await FindWindowAsync(title);
        using (var dpi = new PhysicalDpiScope())
        {
            Assert.True(Native.GetWindowRect(window, out Native.Rect rect));
            Assert.True(rect.Left >= Native.GetSystemMetrics(76));
            Assert.True(rect.Top >= Native.GetSystemMetrics(77));
            Assert.True(rect.Right <= Native.GetSystemMetrics(76) + Native.GetSystemMetrics(78));
            Assert.True(rect.Bottom <= Native.GetSystemMetrics(77) + Native.GetSystemMetrics(79));
        }

        nint text = GetDlgItem(window, 200);
        nint button = GetDlgItem(window, 4097);
        Assert.Equal(message, ReadText(text));
        nint format = Marshal.AllocHGlobal(92);
        try
        {
            Marshal.Copy(new byte[92], 0, format, 92);
            Marshal.WriteInt32(format, 92);
            Assert.NotEqual(0, Native.SendMessageTimeoutW(text, 0x43a, 0, format,
                2, 1000, out _));
            Assert.Equal("Arial", Marshal.PtrToStringUni(format + 26));
            Assert.Equal(360, Marshal.ReadInt32(format, 12));
        }
        finally
        {
            Marshal.FreeHGlobal(format);
        }

        Native.SendMessageTimeoutW(button, 0x31, 0, 0, 2, 1000, out nuint buttonFont);
        Assert.NotEqual((nuint)0, buttonFont);
        Native.PostMessageW(window, 0x10, 0, 0);
        await show.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task RootDisposalCancelsForkDialogsBeforeWaitingForForkRelease()
    {
        await using IWindowsAutomationSession root = new WindowsAutomation().CreateSession();
        IWindowsAutomationSession child = root.Fork();
        string title = "uwsc-fork-" + Guid.NewGuid();
        child.ConfigureDialogOption(Keywords.DlgTitle, [Value.String(title)], default);
        Task<Value> show = child.InvokeAsync(BuiltinFunctionNames.MsgBox,
            [Value.String("child")], default).AsTask();
        nint window = await FindWindowAsync(title);
        Task disposing = root.DisposeAsync().AsTask();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => show.WaitAsync(TimeSpan.FromSeconds(10)));
        await WaitForDestructionAsync(window);
        await child.DisposeAsync();
        await disposing.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("https://example.com/", true, true)]
    [InlineData("http://example.com/", true, true)]
    [InlineData("mailto:test@example.com", true, true)]
    [InlineData("file:///C:/test.txt", true, false)]
    [InlineData("https://example.com/", false, false)]
    public async Task LinkNotificationsOpenOnlyAllowedSchemesWhenEnabled(
        string address, bool enabled, bool expected)
    {
        List<Uri> opened = [];
        await using var service = new NativeDialogService(opened.Add);
        string title = "uwsc-link-" + Guid.NewGuid();
        Task<int> show = service.ShowMessageAsync(new object(),
            new(address, 1, null, null, 0, enabled), new(title), 0, default);
        nint window = await FindWindowAsync(title);
        NotifyLinkClick(window, address.Length);
        Assert.Equal(expected ? [address] : [], opened.Select(uri => uri.AbsoluteUri));

        Native.PostMessageW(window, 0x10, 0, 0);
        await show.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("http")]
    [InlineData("https")]
    [Trait("Category", "SandboxOnly")]
    public async Task WebLinkReachesLoopbackThroughTheDefaultBrowser(string scheme)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        using RegistryKey? choice = Registry.CurrentUser.OpenSubKey(
            $@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\{scheme}\UserChoice");
        string? handler = choice?.GetValue("ProgId") as string;
        output.WriteLine($"{scheme}_user_choice={handler ?? "(none)"}");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        CancellationToken token = deadline.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string route = "/uwsc-msgbox-" + Guid.NewGuid().ToString("N");
        string address = $"{scheme}://127.0.0.1:{port}{route}";
        await using var service = new NativeDialogService();
        string title = $"uwsc-{scheme}-" + Guid.NewGuid();
        Task<int> show = service.ShowMessageAsync(new object(),
            new(address, 1, null, null, 0, true), new(title), 0, default);
        nint window = await FindWindowAsync(title);
        NotifyLinkClick(window, address.Length);

        Task received = ReceiveAsync();
        Task completed = await Task.WhenAny(show, received);
        if (completed == show)
        {
            await deadline.CancelAsync();
            await Record.ExceptionAsync(() => received);
            await show;
            Assert.Fail("リンク通知の後にMSGBOXが終了しました。");
        }

        try
        {
            await received;
        }
        catch (OperationCanceledException error) when (token.IsCancellationRequested)
        {
            throw new TimeoutException(
                "既定のブラウザーからゲスト内の要求が届きませんでした。", error);
        }

        Assert.False(show.IsCompleted);
        Assert.True(Native.PostMessageW(window, 0x10, 0, 0));
        await show.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForDestructionAsync(window);

        async Task ReceiveAsync()
        {
            while (true)
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(token);
                using NetworkStream stream = client.GetStream();
                if (scheme == "https")
                {
                    byte[] prefix = new byte[6];
                    await stream.ReadExactlyAsync(prefix, token);
                    Assert.Equal(0x16, prefix[0]);
                    Assert.Equal(0x03, prefix[1]);
                    Assert.Equal(0x01, prefix[5]);
                    output.WriteLine("https_client_hello="
                        + Convert.ToHexString(prefix));
                    return;
                }

                using var reader = new StreamReader(stream, leaveOpen: true);
                string? request = await reader.ReadLineAsync(token);
                if (request is null)
                {
                    continue;
                }

                string? userAgent = null;
                while (await reader.ReadLineAsync(token) is { Length: > 0 } header)
                {
                    if (header.StartsWith("User-Agent:", StringComparison.OrdinalIgnoreCase))
                    {
                        userAgent = header["User-Agent:".Length..].Trim();
                    }
                }

                await stream.WriteAsync(
                    "HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"u8.ToArray(), token);
                if (request.StartsWith($"GET {route} ", StringComparison.Ordinal))
                {
                    Assert.False(string.IsNullOrEmpty(userAgent));
                    output.WriteLine($"request={request}, user_agent={userAgent}");
                    return;
                }
            }
        }
    }

    [Fact]
    public async Task LinkLaunchFailureClosesMessageBoxAndAllowsNextRequest()
    {
        const string address = "https://example.invalid/";
        var launchError = new Win32Exception(1155);
        await using var service = new NativeDialogService(_ => throw launchError);
        var request = new MessageBoxRequest(address, 1, null, null, 0, true);
        object owner = new();
        string title = "uwsc-link-failure-" + Guid.NewGuid();
        Task<int> show = service.ShowMessageAsync(
            owner, request, new(title), 0, default);
        nint window = await FindWindowAsync(title);

        NotifyLinkClick(window, address.Length);

        Win32Exception failure = await Assert.ThrowsAsync<Win32Exception>(
            () => show.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Same(launchError, failure);
        await WaitForDestructionAsync(window);

        string nextTitle = "uwsc-link-recovery-" + Guid.NewGuid();
        Task<int> next = service.ShowMessageAsync(
            owner, request, new(nextTitle), 0, default);
        nint nextWindow = await FindWindowAsync(nextTitle);
        nint button = GetDlgItem(nextWindow, 4097);
        Assert.NotEqual(0, button);
        Assert.True(Native.PostMessageW(button, 0xf5, 0, 0));
        Assert.Equal(1, await next.WaitAsync(TimeSpan.FromSeconds(10)));
        await WaitForDestructionAsync(nextWindow);
    }

    private static void NotifyLinkClick(nint window, int length)
    {
        // SDK x64のENLINK（Pack=4、NMHDR=24 bytes）を独立したバッファで送ります。
        nint buffer = Marshal.AllocHGlobal(52);
        try
        {
            Marshal.Copy(new byte[52], 0, buffer, 52);
            Marshal.WriteIntPtr(buffer, 0, GetDlgItem(window, 200));
            Marshal.WriteIntPtr(buffer, 8, 200);
            Marshal.WriteInt32(buffer, 16, 0x70b);
            Marshal.WriteInt32(buffer, 24, 0x202);
            Marshal.WriteInt32(buffer, 48, length);
            Assert.NotEqual(0, Native.SendMessageTimeoutW(window, 0x4e, 200, buffer,
                2, 1000, out _));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public async Task SessionDisposalClosesItsActiveMessageBox()
    {
        IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        string title = "uwsc-dispose-" + Guid.NewGuid();
        session.ConfigureDialogOption(Keywords.DlgTitle, [Value.String(title)], default);
        Task<Value> show = session.InvokeAsync(BuiltinFunctionNames.MsgBox,
            [Value.String("first\0second")], default).AsTask();
        try
        {
            nint window = await FindWindowAsync(title);
            Assert.Equal("first␀second", ReadText(GetDlgItem(window, 200)));
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => show);
            await WaitForDestructionAsync(window);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(BuiltinFunctionNames.MsgBox)]
    [InlineData(BuiltinFunctionNames.Input)]
    [InlineData(BuiltinFunctionNames.SlctBox)]
    public async Task DialogTitleChangesForNextDisplayAndStaysWithinRun(
        string function)
    {
        var automation = new WindowsAutomation();
        await using IWindowsAutomationSession root = automation.CreateSession();
        await using IWindowsAutomationSession child = root.Fork();
        await using IWindowsAutomationSession other = automation.CreateSession();
        string scriptName = "title-" + Guid.NewGuid();
        string otherName = "other-" + Guid.NewGuid();
        root.ConfigureDialogContext(scriptName);
        other.ConfigureDialogContext(otherName);

        Value[] arguments = function == BuiltinFunctionNames.SlctBox
            ? [Value.Numeric(1), Value.Numeric(0), Value.String("候補")]
            : [Value.String("本文")];
        await ShowAndCloseAsync(root, "UWSC# - " + scriptName);
        string first = "設定-" + scriptName;
        root.ConfigureDialogOption(Keywords.DlgTitle, [Value.String(first)], default);
        await ShowAndCloseAsync(child, first);
        string second = "変更-" + scriptName;
        child.ConfigureDialogOption(Keywords.DlgTitle, [Value.String(second)], default);
        await ShowAndCloseAsync(root, second);
        await ShowAndCloseAsync(other, "UWSC# - " + otherName);

        async Task ShowAndCloseAsync(IWindowsAutomationSession session, string title)
        {
            Task<Value> show = session.InvokeAsync(function, arguments,
                default).AsTask();
            nint window = await FindWindowAsync(title);
            Assert.True(Native.PostMessageW(window, 0x10, 0, 0));
            await show.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForDestructionAsync(window);
        }
    }

    internal static async Task WaitForDestructionAsync(nint window)
    {
        // 完了通知はWM_NCDESTROY中に届くため、OSの破棄完了も期限付きで待ちます。
        var timer = Stopwatch.StartNew();
        while (Native.IsWindow(window) && timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(10);
        }

        Assert.False(Native.IsWindow(window));
    }

    internal static async Task<nint> FindWindowAsync(string title, int? processId = null)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            nint found = 0;
            Native.EnumWindows((window, _) =>
            {
                Native.GetWindowThreadProcessId(window, out uint process);
                if (process == (processId ?? Environment.ProcessId) && Native.IsWindowVisible(window)
                    && ReadText(window) == title)
                {
                    found = window;
                    return false;
                }

                return true;
            }, 0);
            if (found != 0)
            {
                return found;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("所有するMSGBOXが表示されませんでした。");
    }

    internal static string ReadText(nint window)
    {
        int length = GetWindowTextLengthW(window);
        var text = new StringBuilder(length + 1);
        Native.GetWindowTextW(window, text, text.Capacity);
        return text.ToString();
    }

    [DllImport("user32.dll")]
    internal static extern nint GetDlgItem(nint window, int id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(nint window);
}
