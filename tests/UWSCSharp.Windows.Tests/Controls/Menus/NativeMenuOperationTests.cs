using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeMenuOperationTests
{
    internal sealed class Platform : OwnedListSelectionPlatform
    {
        internal List<(nint Target, uint Message, nint Parameter, nint Data)> Posts { get; } = [];
        internal List<string> Events { get; } = [];
        internal Action? Activating
        {
            get; set;
        }

        internal Action? Pumping
        {
            get; set;
        }

        internal Action? Posting
        {
            get; set;
        }


        public override void Activate(nint root, CancellationToken token)
        {
            Events.Add("activate");
            Activating?.Invoke();
        }

        public override void Sleep(int milliseconds, CancellationToken token)
        {
            Assert.Equal(0, milliseconds);
            token.ThrowIfCancellationRequested();
        }

        public override void Pump(CancellationToken token)
        {
            Events.Add("pump");
            token.ThrowIfCancellationRequested();
            Pumping?.Invoke();
        }

        public override void Post(nint target, uint message, nint parameter, nint data)
        {
            Events.Add("post");
            Posts.Add((target, message, parameter, data));
            Posting?.Invoke();
        }
    }

    [DllImport("user32.dll")]
    private static extern uint CheckMenuItem(nint menu, uint item, uint flags);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PublicOnOffPostsOnlyWhenCheckedStateDiffers(bool check, bool on)
    {
        await Verify(check, on, 701, false);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(65536u)]
    [InlineData(0x80000001u)]
    public async Task CommandIdKeepsFullDwordAndZeroPostReturnsFalse(uint command)
    {
        await Verify(false, true, command, false);
    }

    [Fact]
    public async Task SameStateSucceedsEvenWhenCommandIdIsZero()
    {
        await Verify(true, true, 0, false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeDefaultSearchUsesSameOffContract(bool check)
    {
        await Verify(check, false, 701, true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BackControlsPreparationAndStateIsReadAfterActivation(bool back)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint menu = MenuControlTests.CreateMenu();
        try
        {
            Assert.True(MenuControlTests.AppendMenuW(menu, 0, 701, "target"));
            Assert.True(MenuControlTests.SetMenu(fixture.Root, menu));
            var platform = new Platform
            {
                Activating = () => Assert.NotEqual(uint.MaxValue, CheckMenuItem(menu, 0, 0x408))
            };
            Assert.True(NativeMenuControl.Invoke(fixture.Root, "target", 1, default,
                true, platform, back));
            Assert.Equal(back ? 0 : 1, platform.Events.Count(item => item == "activate"));
            Assert.Equal(back ? 1 : 0, platform.Posts.Count);
            Assert.Equal(back ? 10 : 0, platform.Events.Count(item => item == "pump"));
            Assert.Null(fixture.CallbackFailure);
        }
        finally
        {
            MenuControlTests.SetMenu(fixture.Root, 0);
            MenuControlTests.DestroyMenu(menu);
        }
    }

    [Theory]
    [InlineData("cancel-wait")]
    [InlineData("cancel-last-wait")]
    [InlineData("throw-wait")]
    [InlineData("cancel-post")]
    [InlineData("throw-post")]
    [InlineData("id-after-post")]
    public void FailureAndPostIdBoundaries(string failure)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint menu = MenuControlTests.CreateMenu();
        using var cancel = new CancellationTokenSource();
        try
        {
            Assert.True(MenuControlTests.AppendMenuW(menu, 0, 701, "target"));
            Assert.True(MenuControlTests.SetMenu(fixture.Root, menu));
            var platform = new Platform();
            int pumps = 0;
            void Fail(bool post)
            {
                if (!post && ++pumps == 10 && failure == "cancel-last-wait")
                {
                    cancel.Cancel();
                }

                if (failure == (post ? "cancel-post" : "cancel-wait"))
                {
                    cancel.Cancel();
                }
                else if (failure == (post ? "throw-post" : "throw-wait"))
                {
                    throw new InvalidOperationException("MENU要求境界の失敗");
                }
                else if (post && failure == "id-after-post")
                {
                    Assert.True(RemoveMenu(menu, 0, 0x400));
                    Assert.True(MenuControlTests.AppendMenuW(menu, 0, 0, "replacement"));
                }
            }

            platform.Pumping = () => Fail(false);
            platform.Posting = () => Fail(true);
            if (failure.StartsWith("cancel", StringComparison.Ordinal))
            {
                Assert.ThrowsAny<OperationCanceledException>(() => NativeMenuControl.Invoke(
                    fixture.Root, "target", 1, cancel.Token, true, platform, true));
            }
            else if (failure.StartsWith("throw", StringComparison.Ordinal))
            {
                Assert.Throws<InvalidOperationException>(() => NativeMenuControl.Invoke(
                    fixture.Root, "target", 1, default, true, platform, true));
            }
            else
            {
                Assert.False(NativeMenuControl.Invoke(fixture.Root,
                    "target", 1, default, true, platform, true));
            }

            Assert.Equal(failure.EndsWith("wait", StringComparison.Ordinal) ? 0 : 1,
                platform.Posts.Count);
            Assert.All(platform.Posts, sent => Assert.Equal((nint)701, sent.Parameter));
            Assert.Null(fixture.CallbackFailure);
        }
        finally
        {
            MenuControlTests.SetMenu(fixture.Root, 0);
            MenuControlTests.DestroyMenu(menu);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveMenu(nint menu, uint item, uint flags);

    private static async Task Verify(bool check, bool on, uint command, bool defaultKind)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint menu = MenuControlTests.CreateMenu();
        try
        {
            Assert.True(MenuControlTests.AppendMenuW(menu, check ? 8u : 0, command, "target"));
            Assert.True(MenuControlTests.SetMenu(fixture.Root, menu));
            int synchronous = 0;
            fixture.MenuCommand = _ => synchronous++;
            var platform = new Platform();
            List<string> output = [];
            await new Interpreter(windows: new WindowsAutomation(
                _ => throw new InvalidOperationException(), listSelectionPlatform: platform))
                .RunAsync($"""
                    id = HNDTOID({(long)fixture.Root})
                    PRINT CLKITEM(id, "target", {(defaultKind ? 536870912 : 4) | 512}, {(on ? "TRUE" : "FALSE")})
                    """, output.Add);
            bool post = check != on;
            Assert.Equal([post && command == 0 ? "0" : "1"], output);
            Assert.Equal(0, synchronous);
            Assert.Equal(post ? 1 : 0, platform.Posts.Count);
            Assert.Equal(post ? 10 : 0, platform.Events.Count(item => item == "pump"));
            Assert.All(platform.Posts, sent =>
            {
                Assert.Equal(fixture.Root, sent.Target);
                Assert.Equal(0x111u, sent.Message);
                Assert.Equal(unchecked((nint)command), sent.Parameter);
                Assert.Equal(0, sent.Data);
            });
            Assert.Null(fixture.CallbackFailure);
        }
        finally
        {
            MenuControlTests.SetMenu(fixture.Root, 0);
            MenuControlTests.DestroyMenu(menu);
        }
    }
}
