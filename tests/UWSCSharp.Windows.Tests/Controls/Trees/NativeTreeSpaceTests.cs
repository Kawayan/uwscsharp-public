using System.Runtime.InteropServices;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeTreeSpaceTests
{
    internal sealed class Platform(CancellationTokenSource cancel, string outcome, int key = 32)
        : OwnedListSelectionPlatform
    {
        internal bool Active;
        internal nint Root;
        internal nint Target;
        internal List<string> Events { get; } = [];

        public override bool Attach(uint target, uint current, bool attach)
        {
            if (Active)
            {
                Events.Add(attach ? "attach" : "detach");
                return true;
            }

            return false;
        }

        public override void Foreground(nint window)
        {
            Assert.Equal(Root, window);
            Events.Add("foreground");
        }

        public override void Focus(nint window)
        {
            Assert.Equal(Target, window);
            Events.Add("focus");
        }

        public override void Priority(uint value)
        {
            if (Active)
            {
                Events.Add("priority" + value);
            }
        }

        public override nint Send(nint window, uint message, nint parameter,
            nint data, bool timed, uint timeout, uint flags)
        {
            if (!Active)
            {
                return base.Send(window, message, parameter, data, timed, timeout, flags);
            }

            Assert.True(timed);
            Assert.Equal(1000u, timeout);
            Assert.Equal(2u, flags);
            Assert.True(window == Root || window == Target);
            if (message != 0)
            {
                Assert.Equal(Target, window);
            }

            Assert.Equal(message == 0 ? 0 : key, parameter);
            Assert.Equal(message == 0x101 ? unchecked((nint)(int)0xc0000000) : 0, data);
            Events.Add(message == 0x100 ? "down" : message == 0x101 ? "up"
                : window == Root ? "root-null" : "target-null");
            if (message == 0x100)
            {
                if (outcome == "cancel")
                {
                    cancel.Cancel();
                }
                else if (outcome == "throw")
                {
                    throw new InvalidOperationException("down失敗");
                }
            }

            return 0;
        }
    }

    [Theory]
    [InlineData(false, "success")]
    [InlineData(true, "success")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    [InlineData(false, "throw")]
    [InlineData(true, "throw")]
    public void SpaceUsesPreparedTimedKeySequence(bool back, string outcome)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SliderControlTests.InitializeControls(2);
        using var fixture = new ButtonControlTests.Fixture();
        nint tree = fixture.Child("SysTreeView32", "", 0, 10);
        TreeControlTests.Add(tree, 0, "item");
        using var cancel = new CancellationTokenSource();
        var platform = new Platform(cancel, outcome) { Root = fixture.Root, Target = tree };
        using var hook = new ToolbarClickTextTests.Interceptor(tree, (message, _, pointer) =>
        {
            if (message == 0x113e && Marshal.ReadInt32(pointer) == 8)
            {
                Marshal.WriteInt32(pointer, IntPtr.Size == 8 ? 16 : 8, 0x1000);
                platform.Active = true;
                return 0;
            }

            return null;
        });
        (bool Found, bool Success) Click() => NativeTreeControl.Click(tree, "item", true,
            cancel.Token, kind: back ? 528 : 16, platform: platform);
        if (outcome == "cancel")
        {
            Assert.ThrowsAny<OperationCanceledException>(() => Click());
        }
        else if (outcome == "throw")
        {
            Assert.Throws<InvalidOperationException>(() => Click());
        }
        else
        {
            Assert.Equal((true, true), Click());
        }

        List<string> expected = back ? [] : ["attach", "foreground", "focus", "detach"];
        expected.AddRange(["priority256", "down", "up"]);
        if (outcome == "success")
        {
            expected.AddRange(["target-null", "root-null"]);
        }

        expected.Add("priority32");
        Assert.Equal(expected, platform.Events);
        Assert.Null(fixture.CallbackFailure);
    }
}
