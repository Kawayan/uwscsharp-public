using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeMenuDestructionTests
{
    private sealed class Platform(nint root) : OwnedListSelectionPlatform
    {
        internal Action? Preparing
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

        internal Action? Restoring
        {
            get; set;
        }

        internal NativeControlBufferOwner? Owner;
        internal int Restores;
        internal List<nint> Commands { get; } = [];

        public override bool IsVisible(nint window) => false;

        public override nint ForegroundWindow() => 77;

        public override NativeControlBufferOwner CreateControlBufferOwner(nint window)
        {
            Owner = new NativeControlBufferOwner(window);
            return Owner;
        }

        public override void Activate(nint window, CancellationToken token)
        {
            if (window == 77)
            {
                Assert.False(token.IsCancellationRequested);
                Assert.NotNull(Owner);
                Assert.Throws<ObjectDisposedException>(() => Owner.Acquire());
                Restores++;
                Restoring?.Invoke();
            }
            else
            {
                Assert.Equal(root, window);
                Preparing?.Invoke();
            }
        }

        public override void Sleep(int milliseconds, CancellationToken token) =>
            token.ThrowIfCancellationRequested();

        public override void Pump(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Pumping?.Invoke();
        }

        public override void Post(nint window, uint message, nint parameter, nint data)
        {
            Assert.Equal(root, window);
            Assert.Equal(0x111u, message);
            Assert.Equal(0, data);
            Commands.Add(parameter);
            Posting?.Invoke();
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetMenuItemID(nint menu, int position);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveMenu(nint menu, uint item, uint flags);

    [Theory]
    [InlineData("root-init", false)]
    [InlineData("root-init", true)]
    [InlineData("prepare", false)]
    [InlineData("prepare", true)]
    [InlineData("popup-init", false)]
    [InlineData("popup-init", true)]
    [InlineData("popup-wait", false)]
    [InlineData("popup-wait", true)]
    [InlineData("command-wait", true)]
    [InlineData("post", true)]
    public async Task DestructionUsesGetterResultsInsteadOfExistenceGuard(string boundary, bool on)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint menu = MenuControlTests.CreateMenu();
        bool popup = boundary.StartsWith("popup", StringComparison.Ordinal);
        nint target = popup ? MenuControlTests.CreatePopupMenu() : menu;
        bool menuAlive = true;
        bool targetAlive = true;
        try
        {
            Assert.True(MenuControlTests.AppendMenuW(target, 0, 701, "target"));
            if (popup)
            {
                Assert.True(MenuControlTests.AppendMenuW(menu, 0x10, (nuint)target, "parent"));
            }

            Assert.True(MenuControlTests.SetMenu(fixture.Root, menu));
            var platform = new Platform(fixture.Root);
            void DestroyTarget()
            {
                if (!targetAlive)
                {
                    return;
                }

                if (popup)
                {
                    Assert.True(RemoveMenu(menu, 0, 0x400));
                }
                else
                {
                    Assert.True(MenuControlTests.SetMenu(fixture.Root, 0));
                    menuAlive = false;
                }

                Assert.True(MenuControlTests.DestroyMenu(target));
                targetAlive = false;
            }

            fixture.MenuInit = (message, _, _) =>
            {
                if ((boundary == "root-init" && message == 0x116)
                    || (boundary == "popup-init" && message == 0x117))
                {
                    DestroyTarget();
                }
            };
            platform.Preparing = () =>
            {
                if (boundary == "prepare")
                {
                    DestroyTarget();
                }
            };
            platform.Pumping = () =>
            {
                if (boundary is "popup-wait" or "command-wait")
                {
                    DestroyTarget();
                }
            };
            platform.Posting = () =>
            {
                if (boundary == "post")
                {
                    DestroyTarget();
                }
            };
            await using IWindowsAutomationSession session = new WindowsAutomation(
                _ => throw new InvalidOperationException(), listSelectionPlatform: platform).CreateSession();
            Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
            Assert.Equal(Value.Numeric(boundary == "root-init" ? 0 : 1),
                await session.InvokeAsync("CLKITEM", [id, Value.String("target"),
                    Value.Numeric(4 | (boundary == "prepare" ? 0 : 512)),
                    Value.Numeric(on ? 1 : 0)], default));
            Assert.False(targetAlive);
            Assert.Equal(-1, NativeMenuControl.GetMenuItemCount(target));
            Assert.Equal(uint.MaxValue, GetMenuItemID(target, 0));
            Assert.Equal(boundary == "root-init" || !on ? []
                : new[] { boundary == "post" ? (nint)701 : unchecked((nint)uint.MaxValue) },
                platform.Commands);
            Assert.Equal(1, platform.Restores);
            Assert.Null(fixture.CallbackFailure);
        }
        finally
        {
            MenuControlTests.SetMenu(fixture.Root, 0);
            if (menuAlive)
            {
                MenuControlTests.DestroyMenu(menu);
            }
        }
    }

    [Fact]
    public async Task RootDestroyedDuringInitializationReturnsFalseAndRestoresAfterDispose()
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
            fixture.MenuInit = (message, _, _) =>
            {
                if (message == 0x116)
                {
                    Assert.True(Native.DestroyWindow(fixture.Root));
                }
            };
            var platform = new Platform(fixture.Root);
            await using IWindowsAutomationSession session = new WindowsAutomation(
                _ => throw new InvalidOperationException(), listSelectionPlatform: platform).CreateSession();
            Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
            Assert.Equal(Value.Numeric(0), await session.InvokeAsync("CLKITEM",
                [id, Value.String("target"), Value.Numeric(4 | 512)], default));
            Assert.False(Native.IsWindow(fixture.Root));
            Assert.Equal((nint)0, NativeMenuControl.GetMenu(fixture.Root));
            Assert.Empty(platform.Commands);
            Assert.Equal(1, platform.Restores);
            Assert.Null(fixture.CallbackFailure);
        }
        finally
        {
            if (Native.IsWindow(fixture.Root))
            {
                MenuControlTests.SetMenu(fixture.Root, 0);
            }

            if (NativeMenuControl.GetMenuItemCount(menu) >= 0)
            {
                MenuControlTests.DestroyMenu(menu);
            }
        }
    }

    [Fact]
    public void InvalidMenuKeepsInitialZeroNameAndStateAndReturnsNoCountCandidates()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint menu = MenuControlTests.CreateMenu();
        Assert.True(MenuControlTests.DestroyMenu(menu));
        char[] text = new char[256];
        Assert.Equal(0, NativeMenuControl.GetMenuStringW(menu, 0, text, text.Length, 0x400));
        Assert.All(text, value => Assert.Equal('\0', value));
        var state = new NativeMenuControl.MenuItemInfo
        {
            Size = (uint)Marshal.SizeOf<NativeMenuControl.MenuItemInfo>(),
            Mask = 1
        };
        Assert.False(NativeMenuControl.GetMenuItemInfoW(menu, 0, true, ref state));
        Assert.Equal(0u, state.State);
        Assert.Equal(-1, NativeMenuControl.GetMenuItemCount(menu));
        var search = new NativeMenuClickSearch(fixture.Root, 1, true,
            new Platform(fixture.Root), true, false, default);
        Assert.Equal((nint)0, search.Find(menu, "", 0));
        Assert.Null(fixture.CallbackFailure);
    }

    [Theory]
    [InlineData("prepare", false)]
    [InlineData("prepare", true)]
    [InlineData("popup-wait", false)]
    [InlineData("popup-wait", true)]
    [InlineData("command-wait", false)]
    [InlineData("command-wait", true)]
    [InlineData("post", false)]
    [InlineData("post", true)]
    [InlineData("restore", false)]
    public async Task LocalCatchDoesNotAbsorbOtherFailuresOrCancellation(string boundary, bool cancel)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint menu = MenuControlTests.CreateMenu();
        nint target = boundary == "popup-wait" ? MenuControlTests.CreatePopupMenu() : menu;
        using var cancellation = new CancellationTokenSource();
        try
        {
            Assert.True(MenuControlTests.AppendMenuW(target, 0, 701, "target"));
            if (target != menu)
            {
                Assert.True(MenuControlTests.AppendMenuW(menu, 0x10, (nuint)target, "parent"));
            }

            Assert.True(MenuControlTests.SetMenu(fixture.Root, menu));
            var platform = new Platform(fixture.Root);
            var failure = new InvalidOperationException("MENU捕捉境界の失敗");
            void Fail()
            {
                if (cancel)
                {
                    cancellation.Cancel();
                    cancellation.Token.ThrowIfCancellationRequested();
                }

                throw failure;
            }

            if (boundary == "prepare")
            {
                platform.Preparing = Fail;
            }
            else if (boundary.EndsWith("wait", StringComparison.Ordinal))
            {
                platform.Pumping = () =>
                {
                    platform.Pumping = null;
                    Fail();
                };
            }
            else if (boundary == "post")
            {
                platform.Posting = Fail;
            }
            else
            {
                platform.Restoring = Fail;
            }

            await using IWindowsAutomationSession session = new WindowsAutomation(
                _ => throw new InvalidOperationException(), listSelectionPlatform: platform).CreateSession();
            Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
            Value[] args = [id, Value.String("target"),
                Value.Numeric(4 | (boundary == "prepare" ? 0 : 512))];
            if (boundary == "popup-wait" && !cancel)
            {
                Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM", args, default));
            }
            else if (cancel)
            {
                OperationCanceledException actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                    await session.InvokeAsync("CLKITEM", args, cancellation.Token));
                Assert.Equal(cancellation.Token, actual.CancellationToken);
            }
            else
            {
                InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await session.InvokeAsync("CLKITEM", args, default));
                Assert.Same(failure, actual);
            }

            Assert.Equal(boundary is "post" or "restore" || (boundary == "popup-wait" && !cancel)
                ? 1 : 0, platform.Commands.Count);
            Assert.Equal(1, platform.Restores);
            Assert.Null(fixture.CallbackFailure);
        }
        finally
        {
            MenuControlTests.SetMenu(fixture.Root, 0);
            MenuControlTests.DestroyMenu(menu);
        }
    }
}
