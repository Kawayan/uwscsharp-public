using System.Runtime.InteropServices;
using System.Text;
using UWSCSharp.Core;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class ShortcutKeyAcceptanceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(16, 160)]
    [InlineData(160, 16)]
    [InlineData(17, 162)]
    [InlineData(162, 17)]
    [InlineData(18, 164)]
    [InlineData(164, 18)]
    public async Task PreservesHeldLeftModifierAliases(int held, int requested)
    {
        await using PumpedControlFixture fixture =
            await PumpedControlFixture.CreateAsync(_ => { });
        List<(int Key, bool Up)> sent = [];
        await using IWindowsAutomationSession session = new WindowsAutomation(input =>
            sent.Add((input.Data.Keyboard.Vk, (input.Data.Keyboard.Flags & 2) != 0)),
            windowActivationBackend: new Activation()).CreateSession();
        Value id = await Register(session, fixture.Root);
        await session.InvokeAsync(BuiltinFunctionNames.Kbd,
            [Value.Numeric(held), Value.Numeric(1)], default);
        sent.Clear();
        await session.InvokeAsync(BuiltinFunctionNames.ScKey,
            [id, Value.Numeric(requested), Value.Numeric(65)], default);
        Assert.Equal([(65, false), (65, true)], sent);
        sent.Clear();
        await session.InvokeAsync(BuiltinFunctionNames.Kbd,
            [Value.Numeric(held), Value.Numeric(2)], default);
        Assert.Single(sent);
        Assert.True(sent[0].Up);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task IgnoresDirectOriginAndPreservesBothInputLedgers(
        bool foregroundId, bool cancel)
    {
        nint targetEdit = 0;
        nint otherEdit = 0;
        await using PumpedControlFixture target =
            await PumpedControlFixture.CreateAsync(owner =>
                targetEdit = owner.Child("EDIT", "", 0, 20),
                rootClass: "#32770");
        await using PumpedControlFixture other =
            await PumpedControlFixture.CreateAsync(owner =>
                otherEdit = owner.Child("EDIT", "", 0, 20),
                rootClass: "#32770");
        var direct = new DirectInputTests.Backend
        {
            Target = otherEdit
        };
        using var cancellation = new CancellationTokenSource();
        List<(int Key, bool Up)> sent = [];
        List<int> guarded = [];
        await using IWindowsAutomationSession session = new WindowsAutomation(
            input =>
            {
                bool up = (input.Data.Keyboard.Flags & 2) != 0;
                sent.Add((input.Data.Keyboard.Vk, up));
                if (cancel && input.Data.Keyboard.Vk == 16 && !up)
                {
                    cancellation.Cancel();
                }
            }, directBackend: direct,
            keyboardInputGuard: (key, send) =>
            {
                guarded.Add(key);
                send();
            }).CreateSession();
        Value targetId = await Register(session, targetEdit);
        Value otherId = await Register(session, other.Root);
        // 物理CTRLと、別windowの直接SHIFT/Aを独立して保持します。
        await session.InvokeAsync(BuiltinFunctionNames.Kbd,
            [Value.Numeric(17), Value.Numeric(1)], default);
        await session.InvokeAsync(BuiltinFunctionNames.MouseOrg,
            [otherId, Value.Numeric(2)], default);
        await session.InvokeAsync(BuiltinFunctionNames.Kbd,
            [Value.Numeric(16), Value.Numeric(1)], default);
        await session.InvokeAsync(BuiltinFunctionNames.Kbd,
            [Value.Numeric(65), Value.Numeric(1)], default);
        direct.Messages.Clear();
        direct.Modifiers.Clear();
        sent.Clear();
        guarded.Clear();
        if (foregroundId)
        {
            await MakeForegroundAsync(target.Root);
        }

        async Task Send() => await session.InvokeAsync(BuiltinFunctionNames.ScKey,
            [foregroundId ? Value.Numeric(0) : targetId,
                Value.Numeric(17), Value.Numeric(16), Value.Numeric(65)],
            cancellation.Token);
        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(Send);
            Assert.Equal([(16, false), (16, true)], sent);
            Assert.Equal([16], guarded);
        }
        else
        {
            await Send();
            Assert.Equal([(16, false), (65, false), (65, true), (16, true)], sent);
            Assert.Equal([16, 65], guarded);
        }

        Assert.Empty(direct.Messages);
        Assert.Empty(direct.Modifiers);
        if (!foregroundId)
        {
            var state = new Native.GuiThreadInfo
            {
                Size = (uint)Marshal.SizeOf<Native.GuiThreadInfo>()
            };
            uint owner = Native.GetWindowThreadProcessId(targetEdit, out _);
            Assert.True(Native.GetGUIThreadInfo(owner, ref state));
            Assert.Equal(target.Root, Native.GetForegroundWindow());
            Assert.Equal(targetEdit, state.Focus);
        }

        // SCKEY後もKBDは元のdirect先と保持SHIFTを使います。
        sent.Clear();
        await session.InvokeAsync(BuiltinFunctionNames.Kbd,
            [Value.Numeric(66), Value.Numeric(0)], default);
        Assert.Empty(sent);
        Assert.Equal((16, 0, 0, true), Assert.Single(direct.Modifiers));
        Assert.Equal([0x100u, 0x101u],
            direct.Messages.Select(message => message.Message));
        Assert.All(direct.Messages,
            message => Assert.Equal(otherEdit, message.Window));
        direct.Messages.Clear();
        await session.InvokeAsync(BuiltinFunctionNames.Kbd,
            [Value.Numeric(65), Value.Numeric(2)], default);
        Assert.Equal(0x101u, Assert.Single(direct.Messages).Message);
        await session.InvokeAsync(BuiltinFunctionNames.Kbd,
            [Value.Numeric(16), Value.Numeric(2)], default);
        await session.InvokeAsync(BuiltinFunctionNames.MouseOrg,
            [Value.Numeric(0)], default);
        sent.Clear();
        await session.InvokeAsync(BuiltinFunctionNames.Kbd,
            [Value.Numeric(17), Value.Numeric(2)], default);
        Assert.Equal([(17, true)], sent);
    }

    [Theory]
    [InlineData("existing")]
    [InlineData("destroyed")]
    [InlineData("focus_cleared")]
    [InlineData("missing_snapshot")]
    public async Task SendsPhysicalKeysToExplicitChildAndReleasesShift(
        string initialForeground)
    {
        nint edit = 0;
        await using PumpedControlFixture fixture =
            await PumpedControlFixture.CreateAsync(owner =>
                edit = owner.Child("EDIT", "", 0, 20), rootClass: "#32770");
        await using PumpedControlFixture? previous =
            initialForeground == "existing" ? null
                : await CreateForegroundFixtureAsync();
        if (previous is not null)
        {
            await MakeForegroundAsync(previous.Root);
            if (initialForeground == "destroyed")
            {
                nint oldRoot = previous.Root;
                await previous.DisposeAsync();
                Assert.False(Native.IsWindow(oldRoot));
            }
            else if (initialForeground == "focus_cleared")
            {
                // 対象を一度隠してから、前の所有threadのfocusだけを解除します。
                Native.ShowWindow(fixture.Root, 0);
                output.WriteLine($"clear target={fixture.Root:X}"
                    + $" previous={previous.Root:X}"
                    + $" foreground={Native.GetForegroundWindow():X}");
                Assert.NotEqual(0, Native.SendMessageTimeoutW(previous.Root,
                    0x111, 0x7f00, previous.Root, Native.SmtoAbortIfHung,
                    1000, out _));
                Assert.True(Native.IsWindowVisible(previous.Root));
                Assert.False(Native.IsWindowVisible(fixture.Root));
                output.WriteLine(DescribeForeground());
                Assert.Equal(previous.Root, Native.GetForegroundWindow());
                uint previousThread = Native.GetWindowThreadProcessId(
                    previous.Root, out _);
                var cleared = new Native.GuiThreadInfo
                {
                    Size = (uint)Marshal.SizeOf<Native.GuiThreadInfo>()
                };
                Assert.True(Native.GetGUIThreadInfo(previousThread, ref cleared));
                Assert.Equal(previous.Root, cleared.Active);
                Assert.Equal(0, cleared.Focus);
                // 対象を非アクティブ表示で戻し、前の前面を維持します。
                Native.ShowWindow(fixture.Root, 4);
                Assert.True(Native.IsWindowVisible(fixture.Root));
                Assert.Equal(previous.Root, Native.GetForegroundWindow());
            }
        }

        output.WriteLine($"initial={initialForeground}"
            + $" foreground={Native.GetForegroundWindow():X}");
        MissingForegroundSnapshot? lookup = initialForeground == "missing_snapshot"
            ? new() : null;
        List<(nint Foreground, nint Focus, nint Active, bool Found)> states = [];
        await using IWindowsAutomationSession session =
            new WindowsAutomation(input =>
            {
                var state = new Native.GuiThreadInfo
                {
                    Size = (uint)Marshal.SizeOf<Native.GuiThreadInfo>()
                };
                uint owner = Native.GetWindowThreadProcessId(edit, out _);
                Marshal.SetLastPInvokeError(0);
                bool found = Native.GetGUIThreadInfo(owner, ref state);
                int error = Marshal.GetLastPInvokeError();
                nint foreground = Native.GetForegroundWindow();
                states.Add((foreground, state.Focus, state.Active, found));
                output.WriteLine($"key={input.Data.Keyboard.Vk} flags={input.Data.Keyboard.Flags}"
                    + $" foreground={foreground:X} focus={state.Focus:X}"
                    + $" active={state.Active:X} expected={edit:X}"
                    + $" owner={owner} info={found} error={error}");
                NativeInputSender.Send(input);
            }, windowLookup: lookup).CreateSession();
        Value id = await Register(session, edit);
        if (initialForeground == "focus_cleared")
        {
            Assert.Equal(previous!.Root, Native.GetForegroundWindow());
        }

        await session.InvokeAsync(BuiltinFunctionNames.ScKey,
            [id, Value.Numeric(16), Value.Numeric(65), Value.Numeric(66)], default);
        string text = "";
        for (int attempt = 0; attempt < 100; attempt++)
        {
            var buffer = new StringBuilder(32);
            Assert.NotEqual(0, Native.ReadText(edit, Native.WmGetText,
                32, buffer, Native.SmtoAbortIfHung, 1000, out _));
            text = buffer.ToString();
            if (text == "AB")
            {
                break;
            }

            await Task.Delay(20);
        }

        Assert.Equal("AB", text);
        Assert.Equal(0, new NativeKeyStateBackend().ReadAsync(16) & 0x8000);
        await session.InvokeAsync(BuiltinFunctionNames.ScKey,
            [Value.Numeric(0), Value.Numeric(67)], default);
        for (int attempt = 0; attempt < 100; attempt++)
        {
            var buffer = new StringBuilder(32);
            Assert.NotEqual(0, Native.ReadText(edit, Native.WmGetText,
                32, buffer, Native.SmtoAbortIfHung, 1000, out _));
            text = buffer.ToString();
            if (text == "ABc")
            {
                break;
            }

            await Task.Delay(20);
        }

        Assert.Equal("ABc", text);
        if (lookup is not null)
        {
            Assert.Equal(1, lookup.ZeroReads);
            Assert.True(lookup.NativeReads > 0);
        }

        Assert.Equal(8, states.Count);
        Assert.All(states, state =>
        {
            Assert.True(state.Found);
            Assert.Equal(fixture.Root, state.Foreground);
            Assert.Equal(fixture.Root, state.Active);
            Assert.Equal(edit, state.Focus);
        });
    }

    [Fact]
    public async Task SuccessfulActivationReturnCannotReplaceObservedForeground()
    {
        await using PumpedControlFixture target =
            await PumpedControlFixture.CreateAsync(_ => { });
        await using PumpedControlFixture previous =
            await CreateForegroundFixtureAsync();
        await MakeForegroundAsync(previous.Root);
        List<int> sent = [];
        await using IWindowsAutomationSession session = new WindowsAutomation(
            input => sent.Add(input.Data.Keyboard.Vk),
            windowActivationBackend: new LyingActivation()).CreateSession();
        Value id = await Register(session, target.Root);
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await session.InvokeAsync(BuiltinFunctionNames.ScKey,
                [id, Value.Numeric(65)], default));
        Assert.Empty(sent);
        Assert.Contains("requested=True", error.Message);
        Assert.Contains("foreground=", error.Message);
    }

    private static Task<PumpedControlFixture> CreateForegroundFixtureAsync() =>
        PumpedControlFixture.CreateAsync(owner =>
        {
            owner.Command = (_, command) =>
            {
                if (command == 0x7f00)
                {
                    // focus解除はデスクトップ全体の前面解除とは区別します。
                    SetFocus(0);
                }
            };
        }, title: "SCKEY previous owned foreground");

    private static string DescribeForeground()
    {
        nint window = Native.GetForegroundWindow();
        var className = new StringBuilder(256);
        Native.GetClassNameW(window, className, className.Capacity);
        uint thread = Native.GetWindowThreadProcessId(window, out uint process);
        return $"cleared foreground={window:X} class={className}"
            + $" thread={thread} process={process}";
    }

    private sealed class MissingForegroundSnapshot : IWindowLookupEnvironment
    {
        private readonly NativeWindowLookupEnvironment native = new();

        internal int ZeroReads
        {
            get; private set;
        }

        internal int NativeReads
        {
            get; private set;
        }

        /// <summary>
        /// 初回だけ0を返し、以後は実OSの前面を取得します。
        /// </summary>
        public nint ForegroundWindow()
        {
            if (ZeroReads == 0)
            {
                ZeroReads++;
                return 0;
            }

            NativeReads++;
            return native.ForegroundWindow();
        }

        /// <summary>
        /// 実OSのカーソル位置を取得します。
        /// </summary>
        public Native.Point CursorPosition() => native.CursorPosition();

        /// <summary>
        /// 実OSで指定位置にあるwindowを取得します。
        /// </summary>
        public nint WindowAt(Native.Point point) => native.WindowAt(point);

        /// <summary>
        /// 実OSの最上位windowを取得します。
        /// </summary>
        public nint RootOf(nint window) => native.RootOf(window);
    }

    [Fact]
    [Trait("Category", "SandboxOnly")]
    public async Task ObserveOwnedForegroundDeactivation()
    {
        await using PumpedControlFixture target =
            await PumpedControlFixture.CreateAsync(_ => { });
        Native.ShowWindow(target.Root, 0);
        foreach (bool otherThread in new[] { false, true })
        {
            foreach (bool posted in new[] { false, true })
            {
                var completed = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                await using PumpedControlFixture previous =
                    await PumpedControlFixture.CreateAsync(owner =>
                    {
                        owner.Command = (_, command) =>
                        {
                            if (command != 0x7f00)
                            {
                                return;
                            }

                            nint focus = SetFocus(0);
                            Marshal.SetLastPInvokeError(0);
                            nint active = SetActiveWindow(
                                otherThread ? target.Root : 0);
                            int error = Marshal.GetLastPInvokeError();
                            output.WriteLine($"callback posted={posted}"
                                + $" otherThread={otherThread} error={error}"
                                + $" oldActive={active:X} oldFocus={focus:X}"
                                + " " + DescribeForeground());
                            completed.TrySetResult();
                        };
                    });
                await MakeForegroundAsync(previous.Root);
                if (posted)
                {
                    Assert.True(Native.PostMessageW(previous.Root,
                        0x111, 0x7f00, previous.Root));
                }
                else
                {
                    Assert.NotEqual(0, Native.SendMessageTimeoutW(previous.Root,
                        0x111, 0x7f00, previous.Root, Native.SmtoAbortIfHung,
                        1000, out _));
                }

                await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
                await Task.Delay(50);
                uint thread = Native.GetWindowThreadProcessId(previous.Root, out _);
                var state = new Native.GuiThreadInfo
                {
                    Size = (uint)Marshal.SizeOf<Native.GuiThreadInfo>()
                };
                Assert.True(Native.GetGUIThreadInfo(thread, ref state));
                output.WriteLine($"after posted={posted} otherThread={otherThread}"
                    + $" active={state.Active:X} focus={state.Focus:X}"
                    + " " + DescribeForeground());
            }
        }
    }

    private static async Task MakeForegroundAsync(nint root)
    {
        Native.SetForegroundWindow(root);
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (Native.GetForegroundWindow() == root)
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Equal(root, Native.GetForegroundWindow());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RestoresHiddenTargetAndReleasesOnlyNewModifiers(
        bool cancel, bool minimized)
    {
        await using PumpedControlFixture fixture =
            await PumpedControlFixture.CreateAsync(_ => { });
        Native.ShowWindow(fixture.Root, minimized ? 6 : 0);
        Assert.Equal(minimized, Native.IsIconic(fixture.Root));
        using var cancellation = new CancellationTokenSource();
        List<(int Key, bool Up)> sent = [];
        var automation = new WindowsAutomation(input =>
        {
            sent.Add((input.Data.Keyboard.Vk,
                (input.Data.Keyboard.Flags & 2) != 0));
            if (cancel && input.Data.Keyboard.Vk == 16
                && (input.Data.Keyboard.Flags & 2) == 0)
            {
                cancellation.Cancel();
            }
        }, windowActivationBackend: new Activation());
        await using IWindowsAutomationSession session = automation.CreateSession();
        Value id = await Register(session, fixture.Root);
        await session.InvokeAsync(BuiltinFunctionNames.Kbd,
            [Value.Numeric(17), Value.Numeric(1)], default);
        sent.Clear();
        async Task Send() => await session.InvokeAsync(BuiltinFunctionNames.ScKey,
            [id, Value.Numeric(17), Value.Numeric(16), Value.Numeric(65)],
            cancellation.Token);
        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(Send);
            Assert.Equal([(16, false), (16, true)], sent);
        }
        else
        {
            await Send();
            Assert.Equal([(16, false), (65, false), (65, true), (16, true)], sent);
        }

        if (minimized)
        {
            Assert.True(Native.IsIconic(fixture.Root));
        }
        else
        {
            Assert.False(Native.IsWindowVisible(fixture.Root));
        }
        sent.Clear();
        await session.InvokeAsync(BuiltinFunctionNames.Kbd,
            [Value.Numeric(17), Value.Numeric(2)], default);
        Assert.Equal([(17, true)], sent);
    }

    private static ValueTask<Value> Register(
        IWindowsAutomationSession session, nint handle) =>
        session.InvokeAsync(BuiltinFunctionNames.HndToId,
            [Value.Numeric((long)handle)], default);

    private sealed class Activation : IWindowActivation
    {
        public void ShowNormal(nint window) => Native.ShowWindow(window, 1);

        public bool RequestForeground(nint window) =>
            Native.SetForegroundWindow(window);
    }

    private sealed class LyingActivation : IWindowActivation
    {
        public void ShowNormal(nint window) => Native.ShowWindow(window, 1);

        public bool RequestForeground(nint window) => true;
    }

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetActiveWindow(nint window);
}
