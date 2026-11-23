using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeListViewReturnTests
{
    private sealed class Platform : OwnedListSelectionPlatform
    {
        public override nint Send(nint window, uint message, nint parameter,
            nint data, bool timed, uint timeout, uint flags) => 0;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ZeroSettersDoNotStopLaterRowsOrRejectMatchedName(bool query)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint view = ListViewControlTests.Create(fixture, 10);
        ListViewControlTests.SetText(view, 0, "target");
        ListViewControlTests.SetText(view, 1, "other");
        List<int> rows = [];
        int counts = 0;
        int selectionReads = 0;
        int imageReads = 0;
        using var hook = new ToolbarClickTextTests.Interceptor(view, (message, parameter, pointer) =>
        {
            if (message == 0x102b)
            {
                rows.Add((int)parameter);
                return 0;
            }

            counts += message == 0x1004 ? 1 : 0;
            selectionReads += message == 0x102c && pointer == 3 ? 1 : 0;
            imageReads += message == 0x102c && pointer == 0xf000 ? 1 : 0;
            return null;
        });
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException(),
            listSelectionPlatform: new Platform()).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Value[] args = query ? [id, Value.String("target")]
            : [id, Value.String("target"), Value.Numeric(32)];
        Assert.Equal(Value.Numeric(query ? 0 : 1), await session.InvokeAsync(
            query ? "CHKBTN" : "CLKITEM", args, default));
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, rows);
        Assert.Equal(1, counts);
        Assert.Equal(0, selectionReads);
        Assert.Equal(1, imageReads);
        Assert.Equal(0, Native.SendMessageW(view, 0x102c, 0, 3));
        Assert.Null(fixture.CallbackFailure);
    }
}
