using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeTreeStateBufferTests
{
    public static IEnumerable<object[]> ImageDecisions()
    {
        int[] offSpaces = [0, 0, 1, 0];
        int[] onSpaces = [0, 1, 0, 1];
        for (int image = 0; image < 16; image++)
        {
            yield return [image, false, offSpaces[image % 4]];
            yield return [image, true, onSpaces[image % 4]];
        }
    }

    [Theory]
    [MemberData(nameof(ImageDecisions))]
    public void StateImageUsesOriginalLowBitDecision(int image, bool on, int expectedSpaces)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SliderControlTests.InitializeControls(2);
        using var fixture = new ButtonControlTests.Fixture();
        nint tree = fixture.Child("SysTreeView32", "", 0, 10);
        TreeControlTests.Add(tree, 0, "item");
        int spaces = 0;
        int current = image;
        using var hook = new ToolbarClickTextTests.Interceptor(tree, (message, parameter, pointer) =>
        {
            if (message == 0x113e && Marshal.ReadInt32(pointer) == 8)
            {
                Marshal.WriteInt32(pointer, IntPtr.Size == 8 ? 16 : 8, current << 12);
                return 0;
            }

            if (message == 0x100 && parameter == 32)
            {
                spaces++;
                current = on ? 2 : 1;
                return 0;
            }

            return null;
        });
        Assert.Equal((true, true), NativeTreeControl.Click(tree, "item", on, default,
            platform: new OwnedListSelectionPlatform()));
        Assert.Equal(expectedSpaces, spaces);
        Assert.Null(fixture.CallbackFailure);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 0)]
    [InlineData(4, 0)]
    [InlineData(5, 0)]
    [InlineData(6, 1)]
    [InlineData(7, 0)]
    [InlineData(8, 0)]
    [InlineData(9, 0)]
    [InlineData(10, 1)]
    [InlineData(11, 0)]
    [InlineData(12, 0)]
    [InlineData(13, 0)]
    [InlineData(14, 1)]
    [InlineData(15, 0)]
    public void QueryUsesOnlyLowTwoImageBits(int image, int expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SliderControlTests.InitializeControls(2);
        using var fixture = new ButtonControlTests.Fixture();
        nint tree = fixture.Child("SysTreeView32", "", 0, 10);
        TreeControlTests.Add(tree, 0, "item");
        int reads = 0;
        int keys = 0;
        using var hook = new ToolbarClickTextTests.Interceptor(tree, (message, parameter, pointer) =>
        {
            if (message == 0x113e && Marshal.ReadInt32(pointer) == 8)
            {
                reads++;
                Marshal.WriteInt32(pointer, IntPtr.Size == 8 ? 16 : 8, image << 12);
                return 0;
            }

            keys += message == 0x100 && parameter == 32 ? 1 : 0;
            return null;
        });
        Assert.Equal((true, expected), NativeTreeControl.Check(tree, "item", default,
            platform: new OwnedListSelectionPlatform()));
        Assert.Equal(1, reads);
        Assert.Equal(0, keys);
        Assert.Null(fixture.CallbackFailure);
    }

    [Fact]
    public void StateGetterUsesNameAddressAndReadsWrittenStateDespiteZeroResult()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SliderControlTests.InitializeControls(2);
        using var fixture = new ButtonControlTests.Fixture();
        nint tree = fixture.Child("SysTreeView32", "", 0x100, 10);
        nint item = TreeControlTests.Add(tree, 0, "check");
        using var owner = new NativeControlBufferOwner(fixture.Root);
        var buffer = new NativeTreeClickBuffer(owner);
        nint textAddress = 0;
        int states = 0;
        using var hook = new ToolbarClickTextTests.Interceptor(tree, (message, parameter, pointer) =>
        {
            if (message != 0x113e)
            {
                return null;
            }

            if (Marshal.ReadInt32(pointer) == 0x41)
            {
                textAddress = Marshal.ReadIntPtr(pointer, IntPtr.Size == 8 ? 24 : 16);
                return null;
            }

            states++;
            Assert.Equal(0, parameter);
            Assert.Equal(textAddress + 512, pointer);
            Assert.Equal(8, Marshal.ReadInt32(pointer));
            Assert.Equal(item, Marshal.ReadIntPtr(pointer, IntPtr.Size == 8 ? 8 : 4));
            Assert.Equal(0xf000, Marshal.ReadInt32(pointer, IntPtr.Size == 8 ? 20 : 12));
            Assert.Equal(0, Marshal.ReadInt32(pointer, IntPtr.Size == 8 ? 16 : 8));
            Assert.Equal(0, Marshal.ReadInt64(textAddress));
            Assert.Equal(0, Marshal.ReadIntPtr(pointer, IntPtr.Size == 8 ? 24 : 16));
            if (states == 1)
            {
                Marshal.WriteInt32(pointer, IntPtr.Size == 8 ? 16 : 8, 0x2000);
            }

            return 0;
        });
        Assert.Equal("check", buffer.ReadItem(tree, item, default).Name);
        Assert.Equal(2, buffer.ReadStateImage(tree, item, default));
        Assert.Equal(0, buffer.ReadStateImage(tree, item, default));
        Assert.Equal(2, states);
    }

    [Theory]
    [InlineData("on")]
    [InlineData("off")]
    [InlineData("cancel_state")]
    [InlineData("cancel_space")]
    public async Task CheckboxAndCancellationShareOwnerUntilBeforeRestore(string outcome)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SliderControlTests.InitializeControls(2);
        using var fixture = new ButtonControlTests.Fixture();
        nint tree = fixture.Child("SysTreeView32", "", 0x100, 10);
        nint item = TreeControlTests.Add(tree, 0, "check");
        using var cancellation = new CancellationTokenSource();
        int stateReads = 0;
        int legacyReads = 0;
        int spaces = 0;
        int keyUps = 0;
        List<nint> pointers = [];
        using var hook = new ToolbarClickTextTests.Interceptor(tree, (message, parameter, pointer) =>
        {
            if (message == 0x113e)
            {
                pointers.Add(pointer);
                if (Marshal.ReadInt32(pointer) == 8)
                {
                    stateReads++;
                    if (outcome == "cancel_state")
                    {
                        cancellation.Cancel();
                    }
                }
            }

            if (message == 0x1127)
            {
                legacyReads++;
            }

            if (message == 0x100 && parameter == 32)
            {
                spaces++;
                if (outcome == "cancel_space")
                {
                    cancellation.Cancel();
                }
            }

            if (message == 0x101 && parameter == 32)
            {
                keyUps++;
            }

            return null;
        });
        Native.ShowWindow(fixture.Root, 0);
        int restores = 0;
        ToolbarClickTextTests.RestorePlatform? platform = null;
        platform = new ToolbarClickTextTests.RestorePlatform
        {
            Restoring = () =>
            {
                restores++;
                Assert.True(platform!.Buffers.Released);
                Assert.True(platform.Buffers.Closed);
            },
        };
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException(), listSelectionPlatform: platform).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Value[] args = [id, Value.String("check"), Value.Numeric(16), Value.Numeric(outcome == "off" ? 0 : 1)];
        if (outcome.StartsWith("cancel", StringComparison.Ordinal))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.InvokeAsync("CLKITEM", args, cancellation.Token));
        }
        else
        {
            Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM", args, default));
        }

        Assert.Equal(0, legacyReads);
        Assert.Equal(1, stateReads);
        Assert.Equal(outcome is "on" or "cancel_space" ? 1 : 0, spaces);
        Assert.Equal(spaces, keyUps);
        Assert.All(pointers, pointer => Assert.Equal(platform.Buffers.Address + 512, pointer));
        Assert.Equal(1, restores);
        Assert.Equal(item, Native.SendMessageW(tree, 0x110a, 9, 0));
        int expected = outcome is "on" or "cancel_space" ? 0x2000 : 0x1000;
        Assert.Equal(expected, (int)Native.SendMessageW(tree, 0x1127, item, 0xf000) & 0xf000);
        Assert.Null(fixture.CallbackFailure);
    }
}
