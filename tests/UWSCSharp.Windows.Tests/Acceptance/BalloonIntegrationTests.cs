using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using UWSCSharp.Core;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

public sealed class BalloonIntegrationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FixedBalloonUsesTheDesktopParentAndPreservesItsLifetime(bool enableAfterShowing)
    {
        nint handle;
        await using (IWindowsAutomationSession session = new WindowsAutomation().CreateSession())
        {
            nint foreground = Native.GetForegroundWindow();
            if (enableAfterShowing)
            {
                await Call(session, BuiltinFunctionNames.Balloon,
                    Value.String("before"), Value.Numeric(80), Value.Numeric(90));
            }
            else
            {
                session.ConfigureDialogOption(Keywords.FixBalloon, [], default);
            }

            await Call(session, BuiltinFunctionNames.Balloon,
                Value.String("fixed"), Value.Numeric(80), Value.Numeric(90));
            int id = await Id(session);
            handle = await Handle(session, id);
            if (enableAfterShowing)
            {
                Assert.NotEqual(BalloonGetDesktopWindow(), Native.GetParent(handle));
                session.ConfigureDialogOption(Keywords.FixBalloon, [], default);
                await Call(session, BuiltinFunctionNames.Fukidasi,
                    Value.String("updated"), Value.Numeric(100), Value.Numeric(120));
                Assert.Equal(handle, await Handle(session, await Id(session)));
            }

            Assert.Equal(BalloonGetDesktopWindow(), Native.GetParent(handle));
            uint style = unchecked((uint)Native.GetWindowLongW(handle, -16));
            Assert.NotEqual(0u, style & 0x40000000);
            Assert.Equal(0u, style & 0x80000000);
            Assert.Equal(foreground, Native.GetForegroundWindow());
            Assert.True(IsWindowVisible(handle));
            await Call(session, BuiltinFunctionNames.Balloon);
            Assert.False(IsWindowVisible(handle));
            Assert.Equal(id, await Id(session));
            await Call(session, BuiltinFunctionNames.Fukidasi, Value.String("again"));
            Assert.True(IsWindowVisible(handle));
            Assert.Equal(handle, await Handle(session, await Id(session)));
        }

        Assert.False(Native.IsWindow(handle));
        await using IWindowsAutomationSession next = new WindowsAutomation().CreateSession();
        await Call(next, BuiltinFunctionNames.Balloon, Value.String("next run"));
        nint nextHandle = await Handle(next, await Id(next));
        Assert.NotEqual(BalloonGetDesktopWindow(), Native.GetParent(nextHandle));
        Assert.Equal(0u, unchecked((uint)Native.GetWindowLongW(nextHandle, -16)) & 0x40000000);
    }

    [Fact]
    public async Task FixedBalloonReattachesWhenShownAfterErasing()
    {
        await using IWindowsAutomationSession session =
            new WindowsAutomation().CreateSession();
        await Call(session, BuiltinFunctionNames.Balloon,
            Value.String("before"), Value.Numeric(80), Value.Numeric(90));
        int id = await Id(session);
        nint handle = await Handle(session, id);
        nint desktop = BalloonGetDesktopWindow();
        Assert.NotEqual(desktop, Native.GetParent(handle));

        await Call(session, BuiltinFunctionNames.Balloon);
        Assert.False(IsWindowVisible(handle));
        session.ConfigureDialogOption(Keywords.FixBalloon, [], default);
        Assert.False(IsWindowVisible(handle));
        Assert.NotEqual(desktop, Native.GetParent(handle));

        await Call(session, BuiltinFunctionNames.Fukidasi,
            Value.String("after"));
        Assert.True(IsWindowVisible(handle));
        Assert.Equal(id, await Id(session));
        Assert.Equal(handle, await Handle(session, id));
        Assert.Equal(desktop, Native.GetParent(handle));
        Native.Rect bounds = Bounds(handle);
        Assert.Equal((80, 90), (bounds.Left, bounds.Top));
    }

    [Theory]
    [InlineData(-100000, -100000, true, true)]
    [InlineData(100000, 100000, false, true)]
    [InlineData(-100000, -100000, true, false)]
    [InlineData(100000, 100000, false, false)]
    public async Task BalloonClampsOffScreenCoordinatesAfterErasing(
        int x, int y, bool minimum, bool fixedDesktop)
    {
        await using IWindowsAutomationSession session =
            new WindowsAutomation().CreateSession();
        if (fixedDesktop)
        {
            session.ConfigureDialogOption(Keywords.FixBalloon, [], default);
        }
        const string initialText = "balloon positioned at the work area edge";
        await Call(session, BuiltinFunctionNames.Fukidasi,
            Value.String(initialText), Value.Numeric(x), Value.Numeric(y));

        int id = await Id(session);
        nint handle = await Handle(session, id);
        nint parent = Native.GetParent(handle);
        if (fixedDesktop)
        {
            Assert.Equal(BalloonGetDesktopWindow(), parent);
        }
        Assert.True(IsWindowVisible(handle));
        Native.Rect bounds = Bounds(handle);
        Native.Rect work = Monitor(handle).Work;
        if (minimum)
        {
            Assert.Equal((work.Left, work.Top), (bounds.Left, bounds.Top));
        }
        else
        {
            Assert.Equal((work.Right, work.Bottom),
                (bounds.Right, bounds.Bottom));
        }

        await Call(session, BuiltinFunctionNames.Balloon);
        Assert.False(IsWindowVisible(handle));
        await Call(session, BuiltinFunctionNames.Balloon,
            Value.String(initialText));
        Assert.True(IsWindowVisible(handle));
        Assert.Equal(handle, await Handle(session, await Id(session)));
        Assert.Equal(parent, Native.GetParent(handle));
        Assert.Equal(bounds, Bounds(handle));

        await Call(session, BuiltinFunctionNames.Fukidasi,
            Value.String("W"));
        Native.Rect smaller = Bounds(handle);
        Assert.True(smaller.Right - smaller.Left < bounds.Right - bounds.Left);
        if (minimum)
        {
            Assert.Equal((work.Left, work.Top), (smaller.Left, smaller.Top));
        }
        else
        {
            Assert.Equal((work.Right, work.Bottom),
                (smaller.Right, smaller.Bottom));
            Assert.True(smaller.Left > bounds.Left);
        }

        Assert.Equal(handle, await Handle(session, await Id(session)));
    }

    [Theory]
    [InlineData(BuiltinFunctionNames.Balloon, BuiltinFunctionNames.Fukidasi, false)]
    [InlineData(BuiltinFunctionNames.Fukidasi, BuiltinFunctionNames.Balloon, false)]
    [InlineData(BuiltinFunctionNames.Balloon, BuiltinFunctionNames.Fukidasi, true)]
    [InlineData(BuiltinFunctionNames.Fukidasi, BuiltinFunctionNames.Balloon, true)]
    [InlineData(BuiltinFunctionNames.Fukidasi, BuiltinFunctionNames.Balloon,
        true, true)]
    [InlineData(BuiltinFunctionNames.Balloon, BuiltinFunctionNames.Fukidasi,
        true, true)]
    public async Task AliasesReuseTheWindowAndEraseKeepsItsIdWithoutActivatingIt(
        string firstFunction, string secondFunction, bool eraseWithEmptyText,
        bool specifyEraseCoordinates = false)
    {
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { });
        await UiInputDriver.ActivateAsync(fixture.Root);
        fixture.Invoke(() =>
        {
            SetFocus(fixture.Root);
        });
        nint foreground = Native.GetForegroundWindow();
        Assert.Equal(fixture.Root, foreground);
        nint focus = Focus();
        Assert.NotEqual(0, focus);
        bool hostQueried = false;
        var automation = WindowsAutomation.WithHostWindows((_, _) =>
        {
            hostQueried = true;
            return ValueTask.FromResult(fixture.Root);
        });
        await using IWindowsAutomationSession session = automation.CreateSession();
        Assert.Equal(-1, await Id(session));
        Assert.False(hostQueried);
        Assert.Equal(Value.String(""), await Call(session, firstFunction,
            Value.String("first"), Value.Numeric(80), Value.Numeric(90)));
        int id = await Id(session);
        nint handle = await Handle(session, id);
        Assert.NotEqual(0, handle);
        Assert.Equal(Value.String(""), await Call(session, secondFunction,
            Value.String("second"), Value.Numeric(100), Value.Numeric(120)));
        Assert.Equal(id, await Id(session));
        Assert.Equal(handle, await Handle(session, id));
        Native.Rect bounds = Bounds(handle);
        Assert.Equal((100, 120), (bounds.Left, bounds.Top));
        await Call(session, firstFunction,
            Value.String("move x"), Value.Numeric(140));
        bounds = Bounds(handle);
        Assert.Equal((140, 120), (bounds.Left, bounds.Top));
        await Call(session, secondFunction,
            Value.String("move y"), new(ValueKind.Omitted), Value.Numeric(160));
        bounds = Bounds(handle);
        Assert.Equal((140, 160), (bounds.Left, bounds.Top));
        Value[] eraseArguments = (eraseWithEmptyText, specifyEraseCoordinates) switch
        {
            (true, true) => [Value.String(""), Value.Numeric(180), Value.Numeric(200)],
            (true, false) => [Value.String("")],
            _ => []
        };
        Value erased = await Call(session, firstFunction, eraseArguments);
        Assert.Equal(Value.String(""), erased);
        Assert.True(Native.IsWindow(handle));
        Assert.False(IsWindowVisible(handle));
        Assert.Equal(bounds, Bounds(handle));
        Assert.Equal(id, await Id(session));
        Assert.Equal(Value.String(""),
            await Call(session, secondFunction, Value.String("again")));
        Assert.True(IsWindowVisible(handle));
        Assert.Equal(handle, await Handle(session, id));
        bounds = Bounds(handle);
        Assert.Equal((140, 160), (bounds.Left, bounds.Top));
        Assert.Equal(foreground, Native.GetForegroundWindow());
        Assert.Equal(focus, Focus());
        Assert.Equal((nint)3, SendMessageW(handle, 0x21, 0, 0));
        await session.DisposeAsync();
        Assert.False(Native.IsWindow(handle));
    }

    [Theory]
    [InlineData(BuiltinFunctionNames.Fukidasi)]
    [InlineData(BuiltinFunctionNames.Balloon)]
    public async Task DialogTitleDoesNotReplaceBalloonText(string function)
    {
        await using IWindowsAutomationSession session =
            new WindowsAutomation().CreateSession();
        session.ConfigureDialogOption(Keywords.DlgTitle,
            [Value.String("最初のタイトル")], default);
        await Call(session, function, Value.String("最初の本文"));
        int id = await Id(session);
        nint handle = await Handle(session, id);
        Assert.Equal("最初の本文",
            MessageBoxIntegrationTests.ReadText(handle));

        session.ConfigureDialogOption(Keywords.DlgTitle,
            [Value.String("次のタイトル")], default);
        Assert.Equal("最初の本文",
            MessageBoxIntegrationTests.ReadText(handle));

        await Call(session, function);
        await Call(session, function, Value.String("再表示の本文"));
        Assert.Equal(id, await Id(session));
        Assert.Equal(handle, await Handle(session, id));
        Assert.True(IsWindowVisible(handle));
        Assert.Equal("再表示の本文",
            MessageBoxIntegrationTests.ReadText(handle));
    }

    [Fact]
    public async Task WorkerDisposalInvalidatesItsIdWhileRootRemainsAlive()
    {
        await using IWindowsAutomationSession root = new WindowsAutomation().CreateSession();
        await Call(root, BuiltinFunctionNames.Balloon, Value.String("root"));
        int rootId = await Id(root);
        nint rootHandle = await Handle(root, rootId);
        IWindowsAutomationSession child = root.Fork();
        int childId;
        nint childHandle;
        await using (child)
        {
            Assert.Equal(-1, await Id(child));
            await Call(child, BuiltinFunctionNames.Fukidasi, Value.String("child"), Value.Numeric(200), Value.Numeric(220));
            childId = await Id(child);
            childHandle = await Handle(child, childId);
            Assert.NotEqual(rootHandle, childHandle);
            await Call(child, BuiltinFunctionNames.Balloon);
        }

        Assert.False(Native.IsWindow(childHandle));
        Assert.Equal(0, await Handle(root, childId));
        Assert.Equal(rootHandle, await Handle(root, rootId));
        await using IWindowsAutomationSession next = root.Fork();
        await Call(next, BuiltinFunctionNames.Balloon, Value.String("next"));
        Assert.True(await Id(next) > childId);
        Assert.Equal(0, await Handle(root, childId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task ShortTextAndEveryTailFitTheWindowRegion(int direction)
    {
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        await Call(session, BuiltinFunctionNames.Balloon, Value.String("日本語 & ABC"),
            Value.Numeric(80), Value.Numeric(90), Value.Numeric(direction), Value.Numeric(16),
            Value.String("Arial"), Value.Numeric(0), Value.Numeric(0xFFFFFF));
        nint handle = await Handle(session, await Id(session));
        Native.Rect bounds = Bounds(handle);
        nint region = CreateRectRgn(0, 0, 0, 0);
        try
        {
            Assert.NotEqual(0, GetWindowRgn(handle, region));
            Assert.NotEqual(0, GetRgnBox(region, out Native.Rect shape));
            Assert.InRange(shape.Left, 0, bounds.Right - bounds.Left);
            Assert.InRange(shape.Top, 0, bounds.Bottom - bounds.Top);
            Assert.InRange(shape.Right, 1, bounds.Right - bounds.Left);
            Assert.InRange(shape.Bottom, 1, bounds.Bottom - bounds.Top);
        }
        finally
        {
            DeleteObject(region);
        }

        PixelImage image = await Capture(handle);
        Assert.Contains(image.Pixels, pixel => (pixel & 0xFFFFFF) == 0xFFFFFF);
        Assert.Contains(image.Pixels, pixel => (pixel & 0xFFFFFF) == 0);
        string path = Path.Combine(Path.GetTempPath(), "uwsc-balloon-" + Guid.NewGuid().ToString("N") + ".bmp");
        try
        {
            image.SaveBmp(path, CancellationToken.None);
            output.WriteLine("BALLOON_IMAGE " + direction + " " + Convert.ToBase64String(File.ReadAllBytes(path)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(0, 0x00FF00, 0x00FF00)]
    [InlineData(0, 0x0000FF, 0xFF0000)]
    [InlineData(0, 1, 0x010000)]
    [InlineData(0, 0, 0)]
    [InlineData(0, null, 0)]
    [InlineData(0, 0x00FF00, 0x00FF00, false,
        BuiltinFunctionNames.Fukidasi)]
    [InlineData(1, 0xFFFFFF, 0xFFFFFF, false,
        BuiltinFunctionNames.Fukidasi)]
    [InlineData(2, 0xFFFFFF, 0xFFFFFF, false,
        BuiltinFunctionNames.Fukidasi)]
    [InlineData(3, 0xFFFFFF, 0xFFFFFF, false,
        BuiltinFunctionNames.Fukidasi)]
    [InlineData(4, 0xFFFFFF, 0xFFFFFF, false,
        BuiltinFunctionNames.Fukidasi)]
    [InlineData(0, 0x00FF00, 0x00FF00, true)]
    [InlineData(1, 0xFFFFFF, 0xFFFFFF, true)]
    [InlineData(2, 0xFFFFFF, 0xFFFFFF, true)]
    [InlineData(3, 0xFFFFFF, 0xFFFFFF, true)]
    [InlineData(4, 0xFFFFFF, 0xFFFFFF, true)]
    [InlineData(0, 0x00FF00, 0x00FF00, true,
        BuiltinFunctionNames.Fukidasi)]
    [InlineData(1, 0xFFFFFF, 0xFFFFFF, true,
        BuiltinFunctionNames.Fukidasi)]
    [InlineData(2, 0xFFFFFF, 0xFFFFFF, true,
        BuiltinFunctionNames.Fukidasi)]
    [InlineData(3, 0xFFFFFF, 0xFFFFFF, true,
        BuiltinFunctionNames.Fukidasi)]
    [InlineData(4, 0xFFFFFF, 0xFFFFFF, true,
        BuiltinFunctionNames.Fukidasi)]
    public async Task ColorKeyTransparencyRemovesTheFrameOnlyForMinusTwo(
        int direction, int? backgroundColor = 0xFFFFFF,
        uint frameRgb = 0xFFFFFF, bool fixedDesktop = false,
        string function = BuiltinFunctionNames.Balloon)
    {
        await using IWindowsAutomationSession session =
            new WindowsAutomation().CreateSession();
        if (fixedDesktop)
        {
            session.ConfigureDialogOption(Keywords.FixBalloon, [], default);
        }

        async Task ShowAsync(int transparency)
        {
            await Call(session, function,
                Value.String("Frame"), Value.Numeric(80), Value.Numeric(90),
                Value.Numeric(direction), Value.Numeric(16), Value.String("Arial"),
                Value.Numeric(0xFF00FF),
                backgroundColor is int color ? Value.Numeric(color)
                    : new(ValueKind.Omitted), Value.Numeric(transparency));
        }

        await ShowAsync(-1);
        nint handle = await Handle(session, await Id(session));
        if (fixedDesktop)
        {
            Assert.Equal(BalloonGetDesktopWindow(), Native.GetParent(handle));
        }
        Native.Rect bounds = Bounds(handle);
        PixelImage withFrame = await Capture(handle);
        await ShowAsync(-2);
        Assert.Equal(handle, await Handle(session, await Id(session)));
        Assert.Equal(bounds, Bounds(handle));
        PixelImage withoutFrame = await Capture(handle);
        await Call(session, function);
        PixelImage desktop = await Capture(handle);

        int removedFramePixels = 0;
        int unchangedTextPixels = 0;
        for (int index = 0; index < desktop.Pixels.Length; index++)
        {
            uint framed = (uint)withFrame.Pixels[index] & 0xFFFFFF;
            uint frameless = (uint)withoutFrame.Pixels[index] & 0xFFFFFF;
            uint background = (uint)desktop.Pixels[index] & 0xFFFFFF;
            if (framed != frameless)
            {
                Assert.Equal(frameRgb, framed);
                Assert.Equal(background, frameless);
                removedFramePixels++;
            }

            if (framed == 0xFF00FF && frameless == framed
                && background != framed)
            {
                unchangedTextPixels++;
            }
        }

        Assert.True(removedFramePixels > 0);
        Assert.True(unchangedTextPixels > 0);
        int row = direction == 1 ? desktop.Height - 4 : 3;
        int inside = row * desktop.Width + desktop.Width / 2;
        Assert.Equal(desktop.Pixels[inside], withFrame.Pixels[inside]);
        Assert.Equal(desktop.Pixels[inside], withoutFrame.Pixels[inside]);
    }

    [Theory]
    [InlineData(-1, 0xFFFFFF)]
    [InlineData(-1, 0xFFFFFE)]
    [InlineData(-2, 0xFFFFFF)]
    public async Task ColorKeyDoesNotHideText(int transparency, int color)
    {
        await using IWindowsAutomationSession session =
            new WindowsAutomation().CreateSession();
        await Call(session, BuiltinFunctionNames.Fukidasi,
            Value.String("Text"), Value.Numeric(80), Value.Numeric(90),
            Value.Numeric(0), Value.Numeric(16), Value.String("Arial"),
            Value.Numeric(color), Value.Numeric(0xFFFFFF),
            Value.Numeric(transparency));
        nint handle = await Handle(session, await Id(session));
        Assert.True(GetLayeredWindowAttributes(handle,
            out uint key, out _, out uint flags));
        Assert.Equal(1u, flags);
        Assert.NotEqual((uint)color, key);
        Assert.NotEqual(0xFFFFFFu, key);
    }

    [Theory]
    [InlineData(-2, 1, 255)]
    [InlineData(-1, 1, 255)]
    [InlineData(0, 2, 255)]
    [InlineData(127, 2, 128)]
    [InlineData(255, 2, 0)]
    [InlineData(-2, 1, 255, BuiltinFunctionNames.Fukidasi, true)]
    [InlineData(-1, 1, 255, BuiltinFunctionNames.Fukidasi, true)]
    [InlineData(0, 2, 255, BuiltinFunctionNames.Fukidasi, true)]
    [InlineData(127, 2, 128, BuiltinFunctionNames.Fukidasi, true)]
    [InlineData(255, 2, 0, BuiltinFunctionNames.Fukidasi, true)]
    public async Task TransparencyUsesTheAdoptedAlphaAndSeparateColorKey(
        int value, uint flags, byte alpha,
        string function = BuiltinFunctionNames.Balloon,
        bool fixedDesktop = false)
    {
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        if (fixedDesktop)
        {
            session.ConfigureDialogOption(Keywords.FixBalloon, [], default);
        }

        await Call(session, function, Value.String("transparent"), Value.Numeric(80),
            Value.Numeric(90), Value.Numeric(0), Value.Numeric(16), Value.String("Arial"),
            Value.Numeric(0), Value.Numeric(0xFFFFFF), Value.Numeric(value));
        nint handle = await Handle(session, await Id(session));
        if (fixedDesktop)
        {
            Assert.Equal(BalloonGetDesktopWindow(), Native.GetParent(handle));
        }

        Assert.True(GetLayeredWindowAttributes(handle, out uint key, out byte observedAlpha, out uint observedFlags));
        Assert.Equal(flags, observedFlags);
        if (flags == 2)
        {
            Assert.Equal(alpha, observedAlpha);
        }
        else
        {
            Assert.Equal(value == -1 ? 0xFFFFFEu : 0xFFFFFFu, key);
        }

        output.WriteLine(JsonSerializer.Serialize(new
        {
            value,
            key,
            observedAlpha,
            observedFlags
        }));

        await Call(session, BuiltinFunctionNames.Fukidasi,
            Value.String("opaque"));
        Assert.Equal(handle, await Handle(session, await Id(session)));
        Assert.True(IsWindowVisible(handle));
        Assert.True(GetLayeredWindowAttributes(handle, out _,
            out observedAlpha, out observedFlags));
        Assert.Equal(2u, observedFlags);
        Assert.Equal((byte)255, observedAlpha);
    }

    [Theory]
    [InlineData(BuiltinFunctionNames.Balloon, 0d, "", 22, "Consolas")]
    [InlineData(BuiltinFunctionNames.Fukidasi, 0d, "", 22, "Consolas")]
    [InlineData(BuiltinFunctionNames.Balloon, -999d, "Arial", 6, "Arial")]
    [InlineData(BuiltinFunctionNames.Fukidasi, 5d, "Arial", 6, "Arial")]
    [InlineData(BuiltinFunctionNames.Balloon, -0.9, "Arial", 22, "Arial")]
    [InlineData(BuiltinFunctionNames.Fukidasi, 0.9, "Arial", 22, "Arial")]
    [InlineData(BuiltinFunctionNames.Balloon, 6.9, "Arial", 6, "Arial")]
    [InlineData(BuiltinFunctionNames.Fukidasi, 10.75, "Arial", 10, "Arial")]
    [Trait("Category", "SandboxOnly")]
    public async Task FontBoundariesMatchDefaultOrMinimumRendering(
        string function, double size, string family, int expectedSize,
        string expectedFamily)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using IWindowsAutomationSession session =
            new WindowsAutomation().CreateSession();
        session.ConfigureDialogOption(Keywords.DefaultFont,
            [Value.String("Consolas,22")], default);
        await Call(session, function, Value.String("Font probe"),
            Value.Numeric(80), Value.Numeric(90), Value.Numeric(0),
            Value.Numeric(expectedSize), Value.String(expectedFamily));
        int id = await Id(session);
        nint handle = await Handle(session, id);
        Native.Rect expected = Bounds(handle);
        await Call(session, function, Value.String("Font probe"),
            Value.Numeric(80), Value.Numeric(90), Value.Numeric(0),
            Value.Numeric(size), Value.String(family));
        Assert.Equal(id, await Id(session));
        Assert.Equal(handle, await Handle(session, id));
        Assert.Equal(expected, Bounds(handle));
    }

    [Theory]
    [InlineData(BuiltinFunctionNames.Fukidasi, 5, 0, "吹出しの向き")]
    [InlineData(BuiltinFunctionNames.Balloon, 5, 0, "吹出しの向き")]
    [InlineData(BuiltinFunctionNames.Fukidasi, 0, -3, "透明化")]
    [InlineData(BuiltinFunctionNames.Fukidasi, 0, 256, "透明化")]
    [InlineData(BuiltinFunctionNames.Balloon, 0, -3, "透明化")]
    [InlineData(BuiltinFunctionNames.Balloon, 0, 256, "透明化")]
    public async Task InvalidArgumentsLeaveTheDisplayedBalloonUntouched(
        string function, int direction, int transparency, string message)
    {
        await using IWindowsAutomationSession session =
            new WindowsAutomation().CreateSession();
        await Call(session, function, Value.String("before"),
            Value.Numeric(80), Value.Numeric(90));
        int id = await Id(session);
        nint handle = await Handle(session, id);
        Assert.True(IsWindowVisible(handle));
        Native.Rect before = Bounds(handle);

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Call(session, function, Value.String("invalid"),
                    Value.Numeric(140), Value.Numeric(160),
                    Value.Numeric(direction), new(ValueKind.Omitted),
                    new(ValueKind.Omitted), new(ValueKind.Omitted),
                    new(ValueKind.Omitted), Value.Numeric(transparency)));
        Assert.Contains(message, error.Message);
        Assert.Equal(id, await Id(session));
        Assert.Equal(handle, await Handle(session, id));
        Assert.True(IsWindowVisible(handle));
        Assert.Equal(before, Bounds(handle));

        await Call(session, function, Value.String("after"),
            Value.Numeric(140), Value.Numeric(160));
        Assert.Equal(id, await Id(session));
        Assert.Equal(handle, await Handle(session, id));
        Native.Rect after = Bounds(handle);
        Assert.Equal((140, 160), (after.Left, after.Top));
    }

    [Theory]
    [InlineData(BuiltinFunctionNames.Fukidasi)]
    [InlineData(BuiltinFunctionNames.Balloon)]
    public async Task InvalidEmptyTextRequestDoesNotHideBalloon(
        string function)
    {
        await using IWindowsAutomationSession session =
            new WindowsAutomation().CreateSession();
        await Call(session, function, Value.String("before"),
            Value.Numeric(80), Value.Numeric(90));
        int id = await Id(session);
        nint handle = await Handle(session, id);
        Native.Rect bounds = Bounds(handle);
        Assert.True(IsWindowVisible(handle));

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Call(session, function, Value.String(""),
                    Value.Numeric(140), Value.Numeric(160),
                    Value.Numeric(5)));
        Assert.Contains("吹出しの向き", error.Message);
        Assert.Equal(id, await Id(session));
        Assert.Equal(handle, await Handle(session, id));
        Assert.Equal(bounds, Bounds(handle));
        Assert.True(IsWindowVisible(handle));

        Assert.Equal(Value.String(""),
            await Call(session, function, Value.String("")));
        Assert.False(IsWindowVisible(handle));
        Assert.Equal(id, await Id(session));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedReplacementReleasesTheBalloonAndItsId(
        bool fixedDesktop)
    {
        await using IWindowsAutomationSession session =
            new WindowsAutomation().CreateSession();
        if (fixedDesktop)
        {
            session.ConfigureDialogOption(Keywords.FixBalloon, [], default);
        }

        await Call(session, BuiltinFunctionNames.Balloon,
            Value.String("before"), Value.Numeric(80), Value.Numeric(90));
        int id = await Id(session);
        Assert.True(id > 0);
        nint handle = await Handle(session, id);
        Assert.True(IsWindowVisible(handle));
        nint parent = Native.GetParent(handle);
        Assert.Equal(fixedDesktop, parent == BalloonGetDesktopWindow());

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Call(session, BuiltinFunctionNames.Fukidasi,
                    Value.String("too large"), Value.Numeric(80),
                    Value.Numeric(90), Value.Numeric(0),
                    Value.Numeric(100_000)));
        Assert.Contains("吹出しを配置できません", error.Message);
        Assert.False(Native.IsWindow(handle));
        Assert.Equal(-1, await Id(session));
        Assert.Equal(0, await Handle(session, id));

        Assert.Equal(Value.String(""), await Call(session,
            BuiltinFunctionNames.Fukidasi, Value.String("after")));
        int nextId = await Id(session);
        Assert.True(nextId > id);
        nint nextHandle = await Handle(session, nextId);
        Assert.True(IsWindowVisible(nextHandle));
        Assert.Equal(fixedDesktop,
            Native.GetParent(nextHandle) == BalloonGetDesktopWindow());
        Assert.Equal(0, await Handle(session, id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterDisplayDestroysTheNonmodalWindow(bool entireRun)
    {
        await using var service = new NativeDialogService();
        using var ownerStop = new CancellationTokenSource();
        using var runStop = new CancellationTokenSource();
        object owner = new();
        await service.ShowBalloonAsync(owner, BalloonRequest.Read([Value.String("lifetime")]),
            new("balloon"), ownerStop.Token, runStop.Token, CancellationToken.None);
        nint handle = 0;
        await service.GetBalloonIdAsync(owner, value => { handle = value; return 1; }, CancellationToken.None);
        await (entireRun ? runStop : ownerStop).CancelAsync();
        for (int attempt = 0; attempt < 100 && Native.IsWindow(handle); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.False(Native.IsWindow(handle));
        Assert.Equal(-1, await service.GetBalloonIdAsync(owner, _ => 1, CancellationToken.None));
    }

    [Fact]
    public async Task LongTextWrapsWithinTheWorkAreaAndScrollChangesTheVisibleLines()
    {
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        string text = string.Join("\n", Enumerable.Range(0, 300).Select(index => $"行{index}: long text 日本語"));
        await Call(session, BuiltinFunctionNames.Balloon, Value.String(text), Value.Numeric(int.MaxValue),
            Value.Numeric(int.MaxValue), Value.Numeric(4), Value.Numeric(16));
        nint handle = await Handle(session, await Id(session));
        Native.Rect bounds = Bounds(handle);
        MonitorInformation info = Monitor(handle);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            bounds.Left,
            bounds.Top,
            bounds.Right,
            bounds.Bottom,
            workLeft = info.Work.Left,
            workTop = info.Work.Top,
            workRight = info.Work.Right,
            workBottom = info.Work.Bottom
        }));
        Assert.True(bounds.Left >= info.Work.Left && bounds.Top >= info.Work.Top);
        Assert.True(bounds.Right <= info.Work.Right && bounds.Bottom <= info.Work.Bottom);
        PixelImage before = await Capture(handle);
        SendMessageW(handle, 0x20a, unchecked((nint)(-120 << 16)), 0);
        PixelImage after = await Capture(handle);
        Assert.False(before.Pixels.SequenceEqual(after.Pixels));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScriptThreadEndDestroysItsBalloonBeforeRootEnds(bool fixedDesktop)
    {
        var output = new ConcurrentQueue<string>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await new Interpreter(windows: new WindowsAutomation()).RunAsync(
            (fixedDesktop ? "OPTION FIXBALLOON\n" : "") + """
            PUBLIC childId=-1
            PUBLIC done=FALSE
            BALLOON("root",80,90)
            rootHandle=IDTOHND(GETID(GET_BALLOON_WIN))
            THREAD Worker()
            WHILE !done
                SLEEP(0.01)
            WEND
            WHILE IDTOHND(childId)<>0
                SLEEP(0.01)
            WEND
            PRINT IDTOHND(childId)
            PRINT IDTOHND(GETID(GET_FUKIDASI_WIN))=rootHandle
            EXITEXIT
            PROCEDURE Worker()
                FUKIDASI("child",200,220)
                childId=GETID(GET_BALLOON_WIN)
                done=TRUE
            FEND
            """, output.Enqueue, cancellation.Token);
        Assert.Equal(["0", "1"], output);
    }

    [Fact]
    public async Task SingleLongWordCanBeScrolledToItsDistinctLastLine()
    {
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        await Call(session, BuiltinFunctionNames.Balloon,
            Value.String(new string('W', 5000) + "末尾e\u0301😀"), Value.Numeric(80), Value.Numeric(90),
            Value.Numeric(0), Value.Numeric(16));
        nint handle = await Handle(session, await Id(session));
        PixelImage before = await Capture(handle);
        SendMessageW(handle, 0x20a, unchecked((nint)(-12000 << 16)), 0);
        PixelImage after = await Capture(handle);
        Assert.False(before.Pixels.SequenceEqual(after.Pixels));
    }

    private static Task<Value> Call(IWindowsAutomationSession session, string name, params Value[] values)
        => session.InvokeAsync(name, values, CancellationToken.None).AsTask();

    private static async Task<int> Id(IWindowsAutomationSession session) =>
        (int)(await Call(session, BuiltinFunctionNames.GetId, Value.String(BuiltinWindowSelectors.GetFukidasiWin))).AsNumber();

    private static async Task<nint> Handle(IWindowsAutomationSession session, int id) =>
        (nint)(long)(await Call(session, BuiltinFunctionNames.IdToHnd, Value.Numeric(id))).AsNumber();

    private static async Task<PixelImage> Capture(nint handle)
    {
        await Task.Delay(150);
        using var dpi = new PhysicalDpiScope();
        Assert.True(Native.GetWindowRect(handle, out Native.Rect bounds));
        return new NativeImageCapture().Capture(new(bounds.Left, bounds.Top,
            bounds.Right - bounds.Left, bounds.Bottom - bounds.Top), CancellationToken.None);
    }

    private static Native.Rect Bounds(nint handle)
    {
        using var dpi = new PhysicalDpiScope();
        Assert.True(Native.GetWindowRect(handle, out Native.Rect bounds));
        return bounds;
    }

    private static MonitorInformation Monitor(nint handle)
    {
        using var dpi = new PhysicalDpiScope();
        var info = new MonitorInformation { Size = (uint)Marshal.SizeOf<MonitorInformation>() };
        Assert.True(GetMonitorInfoW(Native.MonitorFromWindow(handle, 2), ref info));
        return info;
    }

    private static nint Focus()
    {
        var info = new GuiThreadInformation { Size = (uint)Marshal.SizeOf<GuiThreadInformation>() };
        Assert.True(GetGUIThreadInfo(0, ref info));
        return info.Focus;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInformation
    {
        public uint Size;
        public uint Flags;
        public nint Active;
        public nint Focus;
        public nint Capture;
        public nint Menu;
        public nint MoveSize;
        public nint Caret;
        public Native.Rect CaretBounds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInformation
    {
        public uint Size;
        public Native.Rect Monitor;
        public Native.Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageW(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "GetDesktopWindow")]
    private static extern nint BalloonGetDesktopWindow();
    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint handle);
    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInformation info);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")]
    private static extern bool GetLayeredWindowAttributes(nint window, out uint color, out byte alpha, out uint flags);
    [DllImport("gdi32.dll")]
    private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("user32.dll")]
    private static extern int GetWindowRgn(nint window, nint region);
    [DllImport("gdi32.dll")]
    private static extern int GetRgnBox(nint region, out Native.Rect bounds);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInformation info);
}
