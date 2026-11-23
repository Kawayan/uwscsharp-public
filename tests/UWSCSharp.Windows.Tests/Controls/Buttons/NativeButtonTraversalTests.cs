using System.Runtime.InteropServices;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed partial class NativeButtonOperationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SelectedButtonQueryStopsButClickReadsLaterCaptions(
        bool query, bool cancelLater)
    {
        using var fixture = new ButtonControlTests.Fixture();
        nint first = fixture.Child("BUTTON", "same", 3, 10);
        nint second = fixture.Child("BUTTON", "same", 3, 40);
        using var cancellation = new CancellationTokenSource();
        var captions = new List<nint>();
        var platform = new Platform();
        using var firstHook = new ToolbarClickTextTests.Interceptor(first,
            (message, _, _) =>
            {
                if (message == 0xd)
                {
                    captions.Add(first);
                }

                return null;
            });
        using var secondHook = new ToolbarClickTextTests.Interceptor(second,
            (message, _, _) =>
            {
                if (message == 0xd)
                {
                    captions.Add(second);
                    if (cancelLater)
                    {
                        cancellation.Cancel();
                    }
                }

                return null;
            });
        await using IWindowsAutomationSession session = fixture.Session(platform);
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        Value[] arguments = query ? [id, Value.String("same")]
            : [id, Value.String("same"), Value.Numeric(1 | 512)];
        if (!query && cancelLater)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.InvokeAsync("CLKITEM", arguments, cancellation.Token));
        }
        else
        {
            Assert.Equal(Value.Numeric(query ? 0 : 1),
                await session.InvokeAsync(query ? "CHKBTN" : "CLKITEM",
                    arguments, cancellation.Token));
        }

        nint[] expectedCaptions = query ? [first] : [first, second];
        Assert.Equal(expectedCaptions, captions);
        Assert.Equal(query ? 0 : 2, platform.PostWindows.Count);
        Assert.All(platform.PostWindows, window => Assert.Equal(first, window));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ZeroNumberReadsLaterNameAfterEarlierAction(bool becomesMatch)
    {
        using var fixture = new ButtonControlTests.Fixture();
        nint first = fixture.Child("BUTTON", "same", 3, 10);
        nint second = fixture.Child("BUTTON", becomesMatch ? "other" : "same", 3, 40);
        var platform = new Platform();
        int captionReads = 0;
        using var hook = new ToolbarClickTextTests.Interceptor(second,
            (message, _, _) =>
            {
                if (message == 0xd)
                {
                    captionReads++;
                }

                return null;
            });
        platform.Posting = () =>
        {
            platform.Posting = null;
            Assert.Equal(0, captionReads);
            Assert.True(SetTraversalCaption(second, becomesMatch ? "same" : "other"));
        };
        await using IWindowsAutomationSession session = fixture.Session(platform);
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("same"), Value.Numeric(1 | 512),
                Value.Numeric(1), Value.Numeric(0)], default));
        Assert.Equal(1, captionReads);
        nint[] expectedPosts = becomesMatch ? [first, first, second, second]
            : [first, first];
        Assert.Equal(expectedPosts, platform.PostWindows);
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern bool SetTraversalCaption(nint window, string text);
}
