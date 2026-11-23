using System.Diagnostics;
using System.Runtime.InteropServices;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class UiaItemActionTests
{
    private sealed class TargetPointerPlatform : OwnedListSelectionPlatform
    {
        internal Native.Point Position
        {
            get; private set;
        }

        public override void Foreground(nint window)
        {
        }

        public override void MoveCursor(Native.Point point) => Position = point;
    }

    [Theory]
    [InlineData(2048)]
    [InlineData(2048 | 512)]
    public async Task TargetedPointerUsesTheChosenHandleRectangle(int flags)
    {
        nint second = 0;
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            host.Child("BUTTON", "same", 0, 10);
            second = host.Child("BUTTON", "same", 0, 60);
        });
        var platform = new TargetPointerPlatform();
        List<uint> inputs = [];
        var mouse = new ProviderItemMouse(input => inputs.Add(input.Data.Mouse.Flags),
            false, platform);
        Assert.Equal((true, true), await UiaItemActions.TryInvokeButtonAsync(
            fixture.Root, second, "same", flags, 1, default, mouse)
            .WaitAsync(TimeSpan.FromSeconds(10)));
        using var dpi = new PhysicalDpiScope();
        Assert.True(Native.GetWindowRect(second, out Native.Rect bounds));
        Assert.InRange(platform.Position.X, bounds.Left + 1, bounds.Right - 1);
        Assert.InRange(platform.Position.Y, bounds.Top + 1, bounds.Bottom - 1);
        Assert.Equal([2u, 4u], inputs);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 512)]
    [InlineData(1, 0)]
    [InlineData(1, 512)]
    [InlineData(2, 0)]
    [InlineData(2, 512)]
    public async Task TargetedButtonPatternChangesOnlyTheChosenHandle(int request, int flags)
    {
        nint first = 0;
        nint second = 0;
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            first = host.Child("BUTTON", "same", 6, 10);
            second = host.Child("BUTTON", "same", 6, 50);
            Native.SendMessageW(first, 0xf1, 2, 0);
        });
        Assert.Equal((true, true), await UiaItemActions.TryInvokeButtonAsync(
            fixture.Root, second, "same", flags, request, default)
            .WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal((nint)2, Native.SendMessageW(first, 0xf0, 0, 0));
        Assert.Equal((nint)request, Native.SendMessageW(second, 0xf0, 0, 0));
    }

    [Theory]
    [InlineData("missing", 0, 0, false, false)]
    [InlineData("target", 0x08000000, 0, false, false)]
    [InlineData("target", 3, 2, true, false)]
    [InlineData("target", 0, 0, true, true)]
    public async Task SearchSeparatesMissingRejectedAndSuccessfulCandidates(
        string name, int style, int request, bool handled, bool success)
    {
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
            host.Child("BUTTON", "target", style, 10));
        Assert.Equal((handled, success), await UiaItemActions.TryInvokeAsync(
            fixture.Root, name, 1 | 512, 1, request, default)
            .WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData(32, "second", 1)]
    [InlineData(98304, "push", 1)]
    public async Task WpfGridSelectionAndReverseAccessibleSearchComplete(
        int flags, string name, int expected)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(FixturePaths.Get("UWSCSharp.UiaFixture",
            "WPFApplication.UWSCSharp.UiaFixture.dll"));
        start.ArgumentList.Add("--actions");
        using Process process = Process.Start(start)!;
        try
        {
            string? ready = await process.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(long.TryParse(ready, out long handle), process.HasExited
                ? await process.StandardError.ReadToEndAsync() : ready);
            await using UWSCSharp.Core.IWindowsAutomationSession session = new WindowsAutomation(
                _ => throw new InvalidOperationException("物理入力は禁止です。"))
                .CreateSession();
            UWSCSharp.Core.Value id = await session.InvokeAsync("HNDTOID",
                [UWSCSharp.Core.Value.Numeric(handle)], default);
            Assert.Equal(UWSCSharp.Core.Value.Numeric(expected),
                await session.InvokeAsync("CLKITEM",
                    [id, UWSCSharp.Core.Value.String(name), UWSCSharp.Core.Value.Numeric(flags)], default)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(15)));
            if (flags == 32)
            {
                Assert.Equal("second", (await session.InvokeAsync("GETSLCTLST",
                    [id, UWSCSharp.Core.Value.Numeric(1)], default)).Display());
            }

            await process.StandardInput.WriteLineAsync("exit");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
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

    [Fact]
    public void ParameterlessCleanupInitializesOperations()
    {
        var cleanup = new ProviderCleanup();
        cleanup.ReleaseComOnly("text");
        cleanup.Finish();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task UiaCheckOperationChangesOwnedThreeStateButton(int request)
    {
        nint button = 0;
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            button = host.Child("BUTTON", "target", 6, 10);
            Native.SendMessageW(button, 0xf1, 1, 0);
        });
        await using UWSCSharp.Core.IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"))
            .CreateSession();
        UWSCSharp.Core.Value id = await session.InvokeAsync("HNDTOID",
            [UWSCSharp.Core.Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(UWSCSharp.Core.Value.Numeric(1), await session.InvokeAsync("CLKITEM",
            [id, UWSCSharp.Core.Value.String("target"),
                UWSCSharp.Core.Value.Numeric(1073742337), UWSCSharp.Core.Value.Numeric(request)],
            default).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal((nint)request, Native.SendMessageW(button, 0xf0, 0, 0));
    }

    private sealed class FakeToggle(int state, int states) : IUiaToggle
    {
        internal int Count
        {
            get; private set;
        }

        internal Action? Changed
        {
            get; set;
        }

        internal int Reads
        {
            get; private set;
        }

        public void Toggle()
        {
            Count++;
            state = (state + 1) % states;
            Changed?.Invoke();
        }

        public int CurrentState()
        {
            Reads++;
            return state;
        }
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 1, 1)]
    [InlineData(0, 2, 2)]
    [InlineData(1, 0, 2)]
    [InlineData(1, 1, 0)]
    [InlineData(1, 2, 1)]
    [InlineData(2, 0, 1)]
    [InlineData(2, 1, 2)]
    [InlineData(2, 2, 0)]
    public void ThreeStateToggleStopsAtRequestedState(int before, int desired, int count)
    {
        var toggle = new FakeToggle(before, 3);
        Assert.True(UiaItemActions.SetToggle(toggle, desired, default));
        Assert.Equal(desired, toggle.CurrentState());
        Assert.Equal(count, toggle.Count);
    }

    [Fact]
    public void UnreachableToggleStateHasFiniteAttempts()
    {
        var toggle = new FakeToggle(0, 2);
        Assert.False(UiaItemActions.SetToggle(toggle, 2, default));
        Assert.Equal(2, toggle.Count);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void InvalidToggleRequestDoesNotReadOrChangeState(int request)
    {
        var toggle = new FakeToggle(1, 3);
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            UiaItemActions.SetToggle(toggle, request, default));
        Assert.Equal(request, error.ActualValue);
        Assert.Equal(0, toggle.Count);
        Assert.Equal(0, toggle.Reads);
        Assert.Equal(1, toggle.CurrentState());
        Assert.True(WindowsOperationErrors.Contains(error));
    }

    [Fact]
    public void CancellationPrecedesInvalidToggleRequest()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var toggle = new FakeToggle(1, 3);
        Assert.Throws<OperationCanceledException>(() =>
            UiaItemActions.SetToggle(toggle, -1, cancellation.Token));
        Assert.Equal(0, toggle.Reads);
        Assert.Equal(0, toggle.Count);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public async Task InvalidUiaCheckRequestIsReportedWithoutChangingOwnedButton(int request)
    {
        nint button = 0;
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            button = host.Child("BUTTON", "target", 6, 10);
            Native.SendMessageW(button, 0xf1, 1, 0);
        });
        await using UWSCSharp.Core.IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"))
            .CreateSession();
        UWSCSharp.Core.Value id = await session.InvokeAsync("HNDTOID",
            [UWSCSharp.Core.Value.Numeric((long)fixture.Root)], default);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await session.InvokeAsync("CLKITEM",
                [id, UWSCSharp.Core.Value.String("target"),
                    UWSCSharp.Core.Value.Numeric(1073742337),
                    UWSCSharp.Core.Value.Numeric(request)], default));
        Assert.Equal((nint)1, Native.SendMessageW(button, 0xf0, 0, 0));
        List<string> printed = [];
        await new UWSCSharp.Core.Interpreter(windows: new WindowsAutomation())
            .RunAsync($"""
                TRY
                    CLKITEM(HNDTOID({(long)fixture.Root}), "target", CLK_UIA OR CLK_BTN OR CLK_BACK, {request})
                EXCEPT
                    PRINT "caught"
                ENDTRY
                """, printed.Add);
        Assert.Equal(new[] { "caught" }, printed);
        Assert.Equal((nint)1, Native.SendMessageW(button, 0xf0, 0, 0));
    }

    [Fact]
    public void CancellationAfterToggleStopsBeforeAnotherAction()
    {
        using var cancellation = new CancellationTokenSource();
        var toggle = new FakeToggle(1, 3) { Changed = cancellation.Cancel };
        Assert.ThrowsAny<OperationCanceledException>(() =>
            UiaItemActions.SetToggle(toggle, 0, cancellation.Token));
        Assert.Equal(1, toggle.Count);
    }

    [Fact]
    public void InvalidToggleStateDoesNotAct()
    {
        var toggle = new FakeToggle(3, 3);
        Assert.Throws<InvalidOperationException>(() =>
            UiaItemActions.SetToggle(toggle, 0, default));
        Assert.Equal(0, toggle.Count);
    }

    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(false, 1, true)]
    [InlineData(false, 2, true)]
    [InlineData(true, 0, true)]
    [InlineData(true, 1, false)]
    [InlineData(true, 2, false)]
    public void MsaaFocusPrecedesSingleStateAwareAction(bool current, int request, bool act)
    {
        List<string> calls = [];
        var provider = new AccessibleTestProvider(44, 0, "target", "")
        {
            TakingFocus = flags => calls.Add("focus=" + flags),
            PerformingAction = () => calls.Add("action")
        };
        AccessibleItems.PerformAction(provider, 0, current, request, default);
        Assert.Equal(act ? ["focus=1", "action"] : new[] { "focus=1" }, calls);
    }

    [Fact]
    public void MsaaProviderFailureIsNotRetried()
    {
        int actions = 0;
        var provider = new AccessibleTestProvider(44, 0, "target", "")
        {
            TakingFocus = _ => { },
            PerformingAction = () =>
            {
                actions++;
                throw new COMException("provider", unchecked((int)0x80004005));
            }
        };
        Assert.Throws<COMException>(() =>
            AccessibleItems.PerformAction(provider, 0, false, 1, default));
        Assert.Equal(1, actions);
    }

    [Theory]
    [InlineData(unchecked((int)0x80020003))]
    [InlineData(unchecked((int)0x80004001))]
    public void MissingMsaaFocusDoesNotPreventIndependentDefaultAction(int status)
    {
        int actions = 0;
        var provider = new AccessibleTestProvider(43, 0, "target", "")
        {
            TakingFocus = _ => throw new COMException("focus unsupported", status),
            PerformingAction = () => actions++
        };
        AccessibleItems.PerformAction(provider, 0, false, 1, default);
        Assert.Equal(1, actions);
    }

    [Fact]
    public void MsaaFocusOperationFailurePreventsDefaultAction()
    {
        int actions = 0;
        var provider = new AccessibleTestProvider(43, 0, "target", "")
        {
            TakingFocus = _ => throw new COMException("focus failed", unchecked((int)0x80004005)),
            PerformingAction = () => actions++
        };
        Assert.Throws<COMException>(() =>
            AccessibleItems.PerformAction(provider, 0, false, 1, default));
        Assert.Equal(0, actions);
    }

    [Theory]
    [InlineData(unchecked((int)0x80020003))]
    [InlineData(unchecked((int)0x80004001))]
    public void MissingOptionalValueKeepsReverseActionSearchAndReadContract(int status)
    {
        int actions = 0;
        var target = new AccessibleTestProvider(43, 0, "push", "");
        var unrelated = new AccessibleTestProvider(52, 0, "other", "")
        {
            ReadingValue = () => throw new COMException("value unavailable", status)
        };
        var root = new AccessibleTestProvider(10, 0, "", "", target, unrelated);
        List<string> result = AccessibleItems.ReadProvider(root, 0,
            true, true, 1, default, 7, 1, "push",
            write: (_, _, _) => actions++);
        Assert.Equal(["1"], result);
        Assert.Equal(1, actions);
        Assert.Throws<COMException>(() => AccessibleItems.ReadProvider(unrelated,
            4194304, false, true, 1, default));
    }

    [Fact]
    public void OptionalValueOperationFailureDoesNotBecomeMissingValue()
    {
        int actions = 0;
        var provider = new AccessibleTestProvider(52, 0, "push", "")
        {
            ReadingValue = () => throw new COMException("value failed", unchecked((int)0x80004005))
        };
        Assert.Throws<COMException>(() => AccessibleItems.ReadProvider(provider, 0,
            false, true, 1, default, 7, 1, "push",
            write: (_, _, _) => actions++));
        Assert.Equal(0, actions);
    }

    [Theory]
    [InlineData(unchecked((int)0x80020003))]
    [InlineData(unchecked((int)0x80004001))]
    public void MissingOptionalCaptionMetadataDoesNotStopFollowingAction(int status)
    {
        int actions = 0;
        var target = new AccessibleTestProvider(43, 0, "push", "");
        var unnamed = new AccessibleTestProvider(43, 0, "", "")
        {
            ReadingValue = () => throw new COMException("value unavailable", status),
            ReadingDescription = () => throw new COMException("description unavailable", status)
        };
        var root = new AccessibleTestProvider(10, 0, "", "", target, unnamed);
        Assert.Equal(["1"], AccessibleItems.ReadProvider(root, 0,
            true, true, 1, default, 7, 1, "push",
            write: (_, _, _) => actions++));
        Assert.Equal(1, actions);
    }

    [Theory]
    [InlineData(42, 0x200000, 0, 0)]
    [InlineData(42, 0x200000, 268435456, 1)]
    [InlineData(42, 0x100000, 268435456, 0)]
    [InlineData(44, 1, 0, 0)]
    [InlineData(44, 0x20, 0, 1)]
    [InlineData(43, 0x10, 0, 1)]
    public void MsaaActionCandidatesUseRoleAndExtendedState(
        int role, int state, int flags, int expected)
    {
        int actions = 0;
        var provider = new AccessibleTestProvider(role, state, "target", "");
        List<string> result = AccessibleItems.ReadProvider(provider, flags,
            false, true, 1, default, 7, 1, "target",
            write: (_, _, _) => actions++);
        Assert.Equal(expected, actions);
        Assert.Equal(expected == 1, result.Count != 0 && result[0] == "1");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MsaaDisabledNamedCandidateConsumesNumber(bool reverse)
    {
        var provider = new AccessibleTestProvider(10, 0, "", "",
            new(44, 1, "target", ""), new(44, 0, "target", ""));
        int actions = 0;
        List<string> result = AccessibleItems.ReadProvider(provider, 0,
            reverse, true, 1, default, 7, 1, "target",
            write: (_, _, _) => actions++);
        Assert.Equal(reverse ? 1 : 0, actions);
        Assert.Equal(reverse ? "1" : "0", Assert.Single(result));
    }
}
