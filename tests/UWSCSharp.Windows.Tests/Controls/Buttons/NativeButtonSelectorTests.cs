using System.Runtime.InteropServices;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed partial class NativeButtonOperationTests
{
    [Theory]
    [InlineData("WindowsForms10.BUTTON", true)]
    [InlineData("TBitBtn", true)]
    [InlineData("TCheckBox", true)]
    [InlineData("Custom3State", true)]
    [InlineData("NotButton", true)]
    [InlineData("Static", false)]
    public async Task PublicButtonSelectorUsesNormalizedClassSubstrings(string prefix, bool found)
    {
        string name = prefix + Guid.NewGuid().ToString("N");
        nint instance = GetModuleHandleW(null);
        nint pointer = Marshal.StringToHGlobalUni(name);
        var definition = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>() };
        try
        {
            // STATICのprocedureではRealGetWindowClassWもSTATICになります。
            definition.Procedure = NativeLibrary.GetExport(
                GetModuleHandleW("user32.dll"), "DefWindowProcW");
            definition.ClassName = pointer;
            definition.Instance = instance;
            definition.Style &= ~0x4000u;
            Assert.NotEqual((ushort)0, RegisterClassExW(ref definition));
            try
            {
                using var fixture = new ButtonControlTests.Fixture();
                nint button = fixture.Child(name, "alias", 0, 10);
                Assert.Equal(DirectTargetSelector.NormalizeLabel(name), NativeSelectionTarget.Label(button));
                var platform = new Platform();
                await using IWindowsAutomationSession session = fixture.Session(platform);
                Value id = await session.InvokeAsync("HNDTOID",
                    [Value.Numeric((long)fixture.Root)], default);
                Assert.Equal(Value.Numeric(found ? 1 : 0), await session.InvokeAsync("CLKITEM",
                    [id, Value.String("alias"), Value.Numeric(1 | 512
                        | (found ? 0 : 536870912))], default));
                Assert.Equal(found ? 2 : 0, platform.Posts.Count);
                Assert.All(platform.PostWindows, window => Assert.Equal(button, window));
                if (found)
                {
                    Assert.Equal(Value.Numeric(0), await session.InvokeAsync("CHKBTN",
                        [id, Value.String("alias")], default));
                }
            }
            finally
            {
                Assert.True(UnregisterClassW(name, instance));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    [Theory]
    [InlineData(299)]
    [InlineData(300)]
    [InlineData(301)]
    [InlineData(1000)]
    public void ButtonNameUsesFixedCapacityWithoutLengthQuery(int length)
    {
        using var fixture = new ButtonControlTests.Fixture();
        string name = new('A', length);
        nint button = fixture.Child("BUTTON", name, 3, 10);
        var messages = new List<(uint Message, nint Parameter)>();
        using var hook = new ToolbarClickTextTests.Interceptor(button, (message, parameter, _) =>
        {
            if (message is 0xd or 0xe)
            {
                messages.Add((message, parameter));
            }

            return null;
        });
        Assert.Equal(name[..Math.Min(300, length)], NativeButtonControl.ReadName(button, default));
        Assert.Equal(new[] { (0xdu, (nint)301) }, messages);
    }

    [Fact]
    public async Task PublicButtonUsesTruncatedCaptionForMatching()
    {
        using var fixture = new ButtonControlTests.Fixture();
        string prefix = new('A', 300);
        fixture.Child("BUTTON", prefix + "suffix", 3, 10);
        var platform = new Platform();
        await using IWindowsAutomationSession session = fixture.Session(platform);
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String(prefix), Value.Numeric(1 | 512)], default));
        Assert.Equal(Value.Numeric(0), await session.InvokeAsync("CLKITEM",
            [id, Value.String(prefix + "suffix"), Value.Numeric(1 | 512 | 536870912)], default));
        Assert.Equal(2, platform.Posts.Count);
    }

    [Fact]
    public void FailedCaptionReadReturnsEmptyAndHonorsCancellation()
    {
        Assert.Equal("", NativeButtonControl.ReadName(0, default));
        using var fixture = new ButtonControlTests.Fixture();
        nint button = fixture.Child("BUTTON", "name", 3, 10);
        using var cancellation = new CancellationTokenSource();
        using var hook = new ToolbarClickTextTests.Interceptor(button, (message, _, _) =>
        {
            if (message == 0xd)
            {
                cancellation.Cancel();
            }

            return null;
        });
        Assert.Throws<OperationCanceledException>(() =>
            NativeButtonControl.ReadName(button, cancellation.Token));
    }
}
