using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class SelectionReadFailureTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SelectedIndexAndCountInterpretSigned32BitErrors(
        bool combo, bool zeroExtended)
    {
        using var fixture = new ButtonControlTests.Fixture();
        nint control = fixture.Child(combo ? "COMBOBOX" : "LISTBOX",
            "", combo ? 3 : 0, 10);
        nint error = zeroExtended ? unchecked((nint)(long)uint.MaxValue) : -1;
        int reads = 0;
        using var intercept = new Interceptor(control, (message, _) =>
        {
            if (message == (combo ? 0x147u : 0x18bu))
            {
                return error;
            }

            if (message is 0x148 or 0x149 or 0x188 or 0x189)
            {
                reads++;
            }

            return null;
        });
        Assert.Equal("", NativeListControl.SelectedText(control, default));
        Assert.Equal(0, reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MultipleSelectionUpdatesBeforeNextReadAndStopsAfterRequestedCount(bool cancel)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint control = fixture.Child("LISTBOX", "", 9, 10);
        foreach (string text in new[] { "first", "second", "tail" })
        {
            Native.SendText(control, 0x180, 0, text, 2, 1000, out _);
        }

        using var cancellation = new CancellationTokenSource();
        var reads = new List<nint>();
        using var intercept = new Interceptor(control, (message, index) =>
        {
            if (message == 0x189)
            {
                reads.Add(index);
                if (index == 1)
                {
                    Assert.Equal((nint)1, Native.SendMessageW(control, 0x187, 0, 0));
                    if (cancel)
                    {
                        cancellation.Cancel();
                    }
                }
            }

            return null;
        });
        if (cancel)
        {
            Assert.ThrowsAny<OperationCanceledException>(() => NativeListControl.Select(
                control, "first\tsecond", cancellation.Token, platform: new OwnedListSelectionPlatform()));
            Assert.Equal((nint)0, Native.SendMessageW(control, 0x187, 1, 0));
        }
        else
        {
            Assert.True(NativeListControl.Select(control, "first\tsecond", default,
                platform: new OwnedListSelectionPlatform()));
        }

        Assert.Equal([(nint)0, (nint)1], reads);
        Assert.Equal((nint)1, Native.SendMessageW(control, 0x187, 0, 0));
    }

    [Fact]
    public void MultipleSelectionUsesMatchCountEvenWhenSettersReturnError()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint control = fixture.Child("LISTBOX", "", 9, 10);
        Native.SendText(control, 0x180, 0, "first", 2, 1000, out _);
        using var intercept = new Interceptor(control, (message, _) => message is 0x185 or 0x186 ? -1 : null);
        Assert.True(NativeListControl.Select(control, "first", default,
            platform: new OwnedListSelectionPlatform()));
        Assert.Equal((nint)0, Native.SendMessageW(control, 0x187, 0, 0));
    }

    private delegate nint Procedure(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetLongPtr(nint window, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern nint SetLong(nint window, int index, nint value);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProcW(nint previous, nint window,
        uint message, nint wParam, nint lParam);

    private static nint SetProcedure(nint window, nint value) =>
        IntPtr.Size == 8 ? SetLongPtr(window, -4, value) : SetLong(window, -4, value);

    private sealed class Interceptor : IDisposable
    {
        private readonly nint window;
        private readonly nint previous;
        private readonly Procedure procedure;

        internal Interceptor(nint window, Func<uint, nint, nint?> response)
            : this(window, (message, parameter, _) => response(message, parameter))
        {
        }

        internal Interceptor(nint window, Func<uint, nint, nint, nint?> response)
        {
            this.window = window;
            procedure = (target, message, wParam, lParam) =>
                response(message, wParam, lParam)
                ?? CallWindowProcW(previous, target, message, wParam, lParam);
            previous = SetProcedure(window, Marshal.GetFunctionPointerForDelegate(procedure));
            Assert.NotEqual(0, previous);
        }

        public void Dispose()
        {
            SetProcedure(window, previous);
            GC.KeepAlive(procedure);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidTextIndexDoesNotOverwriteSeededNativeBuffer(bool combo)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint control = fixture.Child(combo ? "COMBOBOX" : "LISTBOX", "", combo ? 3 : 0, 10);
        Native.SendText(control, combo ? 0x143u : 0x180u, 0, "valid", 2, 1000, out _);
        byte[] before = [.. Enumerable.Repeat((byte)0xcd, 128)];
        byte[] seed = System.Text.Encoding.Unicode.GetBytes("previous\0");
        seed.CopyTo(before, 0);
        nint buffer = Marshal.AllocHGlobal(before.Length);
        try
        {
            Marshal.Copy(before, 0, buffer, before.Length);
            nint copied = Native.SendMessageW(control, combo ? 0x148u : 0x189u, 99, buffer);
            byte[] after = new byte[before.Length];
            Marshal.Copy(buffer, after, 0, after.Length);
            Assert.Equal(-1, unchecked((int)copied));
            Assert.Equal(before, after);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SingleSelectionStopsReadingAtFirstMatchingName(bool combo)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint control = fixture.Child(combo ? "COMBOBOX" : "LISTBOX", "", combo ? 3 : 0, 10);
        foreach (string text in new[] { "first", "first", "last" })
        {
            Native.SendText(control, combo ? 0x143u : 0x180u, 0, text, 2, 1000, out _);
        }

        Native.SendMessageW(control, combo ? 0x14eu : 0x186u, 2, 0);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        await session.InvokeAsync("GETITEM", [id, Value.Numeric(2)], default);
        IReadOnlyList<string> snapshot = session.AllItems;
        using var source = new CancellationTokenSource();
        var requested = new List<nint>();
        using var intercept = new Interceptor(control, (message, index) =>
        {
            if (message == (combo ? 0x148u : 0x189u))
            {
                requested.Add(index);
                if (index == 1)
                {
                    source.Cancel();
                }
            }

            return null;
        });
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("first"), Value.Numeric(2)], source.Token));
        Assert.Equal([(nint)0], requested);
        Assert.False(source.IsCancellationRequested);
        Assert.Equal((nint)0, Native.SendMessageW(control, combo ? 0x147u : 0x188u, 0, 0));
        Assert.Same(snapshot, session.AllItems);
        requested.Clear();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync("CLKITEM", [id, Value.String("missing"), Value.Numeric(2)], source.Token));
        Assert.Equal([(nint)0, (nint)1], requested);
        Assert.Equal((nint)0, Native.SendMessageW(control, combo ? 0x147u : 0x188u, 0, 0));
        Assert.Same(snapshot, session.AllItems);
    }

    [Theory]
    [InlineData(false, -1)]
    [InlineData(false, 0)]
    [InlineData(true, -1)]
    [InlineData(true, 0)]
    public async Task ComboPartialWriteExposesKnownTailAcrossTerminator(bool separate, int copied)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint first = fixture.Child("COMBOBOX", "", 3, 10);
        Native.SendText(first, 0x143, 0, "abcdef", 2, 1000, out _);
        Native.SendText(first, 0x143, 0, "x", 2, 1000, out _);
        nint failed = separate ? fixture.Child("COMBOBOX", "", 3, 80) : first;
        Native.SendText(failed, 0x143, 0, "failed", 2, 1000, out _);
        using var intercept = new Interceptor(failed, (message, index, pointer) =>
        {
            if (message != 0x148 || index != (separate ? 0 : 2))
            {
                return null;
            }

            Marshal.WriteInt16(pointer, 0, 'Y');
            Marshal.WriteInt16(pointer, 2, 'Z');
            return copied;
        });
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(3), await session.InvokeAsync("GETITEM",
            [id, Value.Numeric(2), Value.Numeric(-1)], default));
        Assert.Equal(["abcdef", "x", "YZcdef"], session.AllItems);
    }

    [Fact]
    public async Task EmptyComboTextEstablishesKnownTerminatorForLaterFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint combo = fixture.Child("COMBOBOX", "", 3, 10);
        Native.SendText(combo, 0x143, 0, "", 2, 1000, out _);
        Native.SendText(combo, 0x143, 0, "failed", 2, 1000, out _);
        using var intercept = new Interceptor(combo,
            (message, index) => message == 0x148 && index == 1 ? -1 : null);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(0), await session.InvokeAsync("GETITEM", [id, Value.Numeric(2)], default));
        Assert.Empty(session.AllItems);
    }

    [Theory]
    [InlineData(false, -1)]
    [InlineData(false, 0)]
    [InlineData(true, -1)]
    [InlineData(true, 0)]
    public async Task ClickListIgnoresTextReturnAndPreservesPublishedItems(bool combo, int copied)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint control = fixture.Child(combo ? "COMBOBOX" : "LISTBOX", "", combo ? 3 : 0, 10);
        Native.SendText(control, combo ? 0x143u : 0x180u, 0, "first", 2, 1000, out _);
        Native.SendText(control, combo ? 0x143u : 0x180u, 0, "target", 2, 1000, out _);
        Native.SendMessageW(control, combo ? 0x14eu : 0x186u, 0, 0);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        await session.InvokeAsync("GETITEM", [id, Value.Numeric(2)], default);
        IReadOnlyList<string> snapshot = session.AllItems;
        using var intercept = new Interceptor(control,
            (message, index) => message == (combo ? 0x148u : 0x189u) && index == 1 ? copied : null);
        Assert.Equal(Value.Numeric(0), await session.InvokeAsync("CLKITEM",
            [id, Value.String("target"), Value.Numeric(2 | 536870912)], default));
        Assert.Equal((nint)0, Native.SendMessageW(control, combo ? 0x147u : 0x188u, 0, 0));
        Assert.Same(snapshot, session.AllItems);
    }

    [Theory]
    [InlineData(0, -1)]
    [InlineData(0, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 0)]
    [InlineData(2, -1)]
    [InlineData(2, 0)]
    public async Task ComboFailureReusesKnownBufferWithinCall(int preceding, int copied)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint first = fixture.Child(preceding == 1 ? "LISTBOX" : "COMBOBOX", "", preceding == 1 ? 0 : 3, 10);
        Native.SendText(first, preceding == 1 ? 0x180u : 0x143u, 0, " prior ", 2, 1000, out _);
        nint failed = preceding == 0 ? first : fixture.Child("COMBOBOX", "", 3, 80);
        Native.SendText(failed, 0x143, 0, "failed", 2, 1000, out _);
        nint failedIndex = preceding == 0 ? 1 : 0;
        using var intercept = new Interceptor(failed,
            (message, index) => message == 0x148 && index == failedIndex ? copied : null);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(2), await session.InvokeAsync("GETITEM",
            [id, Value.Numeric(2), Value.Numeric(-1)], default));
        Assert.Equal(["prior", "prior"], session.AllItems);
        IReadOnlyList<string> previous = session.AllItems;
        using var firstFailure = new Interceptor(failed,
            (message, _) => message == 0x148 ? copied : null);
        Assert.Equal(Value.Numeric(0), await session.InvokeAsync("GETITEM",
            [id, Value.Numeric(2), Value.Numeric(preceding == 0 ? 1 : 2)], default));
        Assert.Empty(session.AllItems);
        Assert.Equal(["prior", "prior"], previous);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task ListEnumerationSkipsNonpositiveTextResultAndContinues(int copied)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint list = fixture.Child("LISTBOX", "", 0, 10);
        foreach (string text in new[] { "first", "failed", "last" })
        {
            Native.SendText(list, 0x180, 0, text, 2, 1000, out _);
        }

        using var intercept = new Interceptor(list,
            (message, index) => message == 0x189 && index == 1 ? copied : null);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(2), await session.InvokeAsync("GETITEM", [id, Value.Numeric(2)], default));
        Assert.Equal(["first", "last"], session.AllItems);
        if (copied < 0)
        {
            Assert.Equal(["first", "first", "last"],
                NativeListControl.ReadForNameMatching(list, default));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionFreezesPrefixAfterTimeoutOrCancellation(bool cancel)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        nint list = 0;
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            list = host.Child("LISTBOX", "", 0, 10);
            Native.SendText(list, 0x180, 0, "first", 2, 1000, out _);
            Native.SendText(list, 0x180, 0, "second", 2, 1000, out _);
        });
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        using var source = new CancellationTokenSource();
        using var intercept = new Interceptor(list, (message, index) =>
        {
            if (message == 0x18a && index == 1)
            {
                entered.Set();
                release.Wait();
                finished.Set();
            }

            return null;
        });
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"))
            .CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Task<Value> pending = session.InvokeAsync("GETITEM", [id, Value.Numeric(2)], source.Token).AsTask();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            if (cancel)
            {
                source.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            }
            else
            {
                await Assert.ThrowsAsync<TimeoutException>(() => pending);
            }

            IReadOnlyList<string> prefix = session.AllItems;
            Assert.Equal(["first"], prefix);
            Assert.Equal(Value.Numeric(0), await session.InvokeAsync("GETITEM",
                [Value.Numeric(-1), Value.Numeric(2)], default));
            release.Set();
            Assert.True(finished.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(["first"], prefix);
            Assert.Empty(session.AllItems);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task HeaderReaderPublishesPrefixBeforeLaterFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint control = ListViewControlTests.Create(fixture, 10);
        using var items = new ItemCollector();
        using var intercept = new Interceptor(control,
            (message, index) => message == 0x105f && index == 1 ? 0 : null);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await NativeListViewControl.ReadAsync(control, -1, default, onItem: items.Add));
        Assert.Equal("Name", Assert.Single(items));
        List<string> output = [];
        ScriptException failure = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: new WindowsAutomation(
                _ => throw new InvalidOperationException("物理入力は禁止です。")))
            .RunAsync($"""
                id = HNDTOID({(long)fixture.Root})
                TRY
                    GETITEM(id, 32, 1, -1)
                FINALLY
                    FOR item IN ALL_ITEM_LIST
                        PRINT item
                    NEXT
                ENDTRY
                """, output.Add));
        Assert.Equal(3, failure.Line);
        Assert.IsType<InvalidOperationException>(failure.InnerException?.InnerException);
        Assert.Equal("Name", Assert.Single(output));
    }

    [Theory]
    [InlineData(false, -1)]
    [InlineData(false, 0)]
    [InlineData(true, -1)]
    [InlineData(true, 0)]
    public async Task ItemDataFailureUsesSharedBufferAndTypeSpecificReturn(bool combo, int copied)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint control = fixture.Child(combo ? "COMBOBOX" : "LISTBOX", "", 0x10 | (combo ? 3 : 0), 10);
        Native.SendMessageW(control, combo ? 0x143u : 0x180u, 0, 0x00420041);
        Native.SendMessageW(control, combo ? 0x143u : 0x180u, 0, 0x00440043);
        using var intercept = new Interceptor(control,
            (message, index) => message == (combo ? 0x148u : 0x189u) && index == 1 ? copied : null);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        await session.InvokeAsync("GETITEM", [id, Value.Numeric(2)], default);
        string[] expected = combo ? ["AB", "AB"] : ["AB"];
        Assert.Equal(expected, session.AllItems);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnumerationReadsChangedTextWithoutLengthQuery(bool combo)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint control = fixture.Child(combo ? "COMBOBOX" : "LISTBOX", "", combo ? 3 : 0, 10);
        Native.SendText(control, combo ? 0x143u : 0x180u, 0, "a", 2, 1000, out _);
        string changed = new('長', 4097);
        int lengthQueries = 0;
        bool replaced = false;
        using var intercept = new Interceptor(control, (message, parameter) =>
        {
            if (message == (combo ? 0x149u : 0x18au))
            {
                lengthQueries++;
                return int.MinValue;
            }

            if (message == (combo ? 0x148u : 0x189u) && !replaced)
            {
                replaced = true;
                Native.SendMessageW(control, combo ? 0x144u : 0x182u, 0, 0);
                Native.SendText(control, combo ? 0x14au : 0x181u, 0, changed, 2, 1000, out _);
            }

            return null;
        });
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("GETITEM", [id, Value.Numeric(2)], default));
        Assert.Equal(changed, Assert.Single(session.AllItems));
        Assert.Equal(0, lengthQueries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NegativeCountUsesTypeSpecificEnumerationContract(bool view)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint control = view ? ListViewControlTests.Create(fixture, 10)
            : fixture.Child("LISTBOX", "", 8, 10);
        using var intercept = new Interceptor(control,
            (message, _) => message == (view ? 0x1004u : 0x18bu) ? -1 : null);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.String(""), await session.InvokeAsync("GETSLCTLST", [id], default));
        Assert.Equal(Value.Numeric(0), await session.InvokeAsync("GETITEM",
            [id, Value.Numeric(view ? 32 : 2)], default));
        Assert.Empty(session.AllItems);
    }

    [Theory]
    [InlineData(false, -1)]
    [InlineData(false, -2)]
    [InlineData(false, int.MinValue)]
    [InlineData(true, -1)]
    [InlineData(true, -2)]
    [InlineData(true, int.MinValue)]
    public async Task NegativeItemCountIsEmptyExceptCheckedMinimum(bool combo, int count)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint control = fixture.Child(combo ? "COMBOBOX" : "LISTBOX", "", combo ? 3 : 0, 10);
        Native.SendText(control, combo ? 0x143u : 0x180u, 0, "before", 2, 1000, out _);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        await session.InvokeAsync("GETITEM", [id, Value.Numeric(2)], default);
        IReadOnlyList<string> before = session.AllItems;
        Assert.Equal("before", Assert.Single(before));
        bool textRead = false;
        using var intercept = new Interceptor(control, (message, _) =>
        {
            if (message == (combo ? 0x146u : 0x18bu))
            {
                return count;
            }

            if (message == (combo ? 0x149u : 0x18au))
            {
                textRead = true;
            }

            return null;
        });
        if (count == int.MinValue)
        {
            await Assert.ThrowsAsync<OverflowException>(async () =>
                await session.InvokeAsync("GETITEM", [id, Value.Numeric(2)], default));
            Assert.NotSame(before, session.AllItems);
            Assert.Empty(session.AllItems);
        }
        else
        {
            Assert.Equal(Value.Numeric(0), await session.InvokeAsync("GETITEM", [id, Value.Numeric(2)], default));
            Assert.Empty(session.AllItems);
        }

        Assert.False(textRead);
        if (count == int.MinValue)
        {
            Assert.Throws<OverflowException>(() =>
                NativeListControl.ReadForNameMatching(control, default));
            await Assert.ThrowsAsync<OverflowException>(async () =>
                await session.InvokeAsync("CLKITEM", [id, Value.String("before"), Value.Numeric(2 | 536870912)], default));
        }
        else
        {
            Assert.Empty(NativeListControl.ReadForNameMatching(control, default));
            Assert.Equal(Value.Numeric(0), await session.InvokeAsync("CLKITEM",
                [id, Value.String("before"), Value.Numeric(2 | 536870912)], default));
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(int.MinValue)]
    public async Task NegativeListViewRowsAreEmptyExceptCheckedMinimum(int count)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint control = ListViewControlTests.Create(fixture, 10);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        await session.InvokeAsync("GETITEM", [id, Value.Numeric(32)], default);
        IReadOnlyList<string> before = session.AllItems;
        Assert.Equal(5, before.Count);
        bool itemRead = false;
        using var intercept = new Interceptor(control, (message, _) =>
        {
            if (message == 0x1004)
            {
                return count;
            }

            if (message == 0x1073)
            {
                itemRead = true;
            }

            return null;
        });
        if (count == int.MinValue)
        {
            await Assert.ThrowsAsync<OverflowException>(async () =>
                await session.InvokeAsync("GETITEM", [id, Value.Numeric(32)], default));
            Assert.NotSame(before, session.AllItems);
            Assert.Empty(session.AllItems);
        }
        else
        {
            Assert.Equal(Value.Numeric(0), await session.InvokeAsync("GETITEM", [id, Value.Numeric(32)], default));
            Assert.Empty(session.AllItems);
        }

        Assert.False(itemRead);
        Assert.Equal(Value.String(""), await session.InvokeAsync("GETSLCTLST", [id], default));
        Assert.Equal(Value.Numeric(2), await session.InvokeAsync("GETITEM",
            [id, Value.Numeric(32), Value.Numeric(1), Value.Numeric(-1)], default));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(0, int.MaxValue)]
    [InlineData(0, -1)]
    [InlineData(0, int.MinValue)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(1, int.MaxValue)]
    [InlineData(1, -1)]
    [InlineData(1, int.MinValue)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    [InlineData(2, int.MaxValue)]
    [InlineData(2, -1)]
    [InlineData(2, int.MinValue)]
    public async Task NonListViewIgnoresColumn(int kind, int column)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SliderControlTests.InitializeControls(2);
        using var fixture = new ButtonControlTests.Fixture();
        string[] names = ["LISTBOX", "COMBOBOX", "SysTreeView32"];
        nint control = fixture.Child(names[kind], "", kind == 1 ? 3 : 0, 10);
        if (kind == 2)
        {
            nint item = TreeControlTests.Add(control, 0, " chosen ");
            Native.SendMessageW(control, 0x110b, 9, item);
        }
        else
        {
            Native.SendText(control, kind == 1 ? 0x143u : 0x180u,
                0, " chosen ", 2, 1000, out _);
            Native.SendMessageW(control, kind == 1 ? 0x14eu : 0x186u, 0, 0);
        }

        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.String(" chosen "), await session.InvokeAsync("GETSLCTLST",
            [id, Value.Numeric(1), Value.Numeric(column)], default));
    }

    [Theory]
    [InlineData(false, 2047)]
    [InlineData(false, 2048)]
    [InlineData(false, 2049)]
    [InlineData(true, 2047)]
    [InlineData(true, 2048)]
    [InlineData(true, 2049)]
    public async Task SelectedTextUsesBoundedScanWithoutTruncatingItems(bool combo,
        int length)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint control = fixture.Child(combo ? "COMBOBOX" : "LISTBOX", "", combo ? 3 : 0, 10);
        string text = new('長', length);
        Native.SendText(control, combo ? 0x143u : 0x180u, 0, text, 2, 1000, out _);
        Native.SendMessageW(control, combo ? 0x14eu : 0x186u, 0, 0);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("GETITEM",
            [id, Value.Numeric(2)], default));
        IReadOnlyList<string> snapshot = session.AllItems;
        Assert.Equal(text, Assert.Single(snapshot));
        Assert.Equal(text[..Math.Min(2048, length)],
            (await session.InvokeAsync("GETSLCTLST", [id], default)).Text);
        Assert.Same(snapshot, session.AllItems);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScanLimitCountsUtf16Units(bool combo)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint control = fixture.Child(combo ? "COMBOBOX" : "LISTBOX", "", combo ? 3 : 0, 10);
        string text = new string('a', 2047) + "\ud83d\ude00tail";
        Native.SendText(control, combo ? 0x143u : 0x180u, 0, text, 2, 1000, out _);
        Native.SendMessageW(control, combo ? 0x14eu : 0x186u, 0, 0);
        string selected = NativeListControl.SelectedText(control, default);
        Assert.Equal(text[..2048], selected);
        Assert.True(char.IsHighSurrogate(selected[^1]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnselectedItemFailureDoesNotAffectSelection(bool combo)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint control = fixture.Child(combo ? "COMBOBOX" : "LISTBOX", "", combo ? 3 : 8, 10);
        Native.SendText(control, combo ? 0x143u : 0x180u, 0, "bad", 2, 1000, out _);
        Native.SendText(control, combo ? 0x143u : 0x180u, 0, "chosen", 2, 1000, out _);
        Native.SendMessageW(control, combo ? 0x14eu : 0x185u, 1, 1);
        var requested = new List<nint>();
        using var intercept = new Interceptor(control, (message, index) =>
        {
            if (message != (combo ? 0x149u : 0x18au))
            {
                return null;
            }

            requested.Add(index);
            return index == 0 ? -1 : null;
        });
        Assert.Equal("chosen", NativeListControl.SelectedText(control, default));
        Assert.NotEmpty(requested);
        Assert.All(requested, index => Assert.Equal((nint)1, index));
        requested.Clear();
        Assert.Equal(["bad", "chosen"], NativeListControl.Read(control, default));
        Assert.Empty(requested);
        Assert.Equal(["bad", "chosen"],
            NativeListControl.ReadForNameMatching(control, default));
        Assert.Empty(requested);
    }

    [Fact]
    public void UnselectedComboDoesNotReadCountOrText()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint control = fixture.Child("COMBOBOX", "", 3, 10);
        var messages = new List<uint>();
        using var intercept = new Interceptor(control, (message, _) =>
        {
            messages.Add(message);
            return null;
        });
        Assert.Equal("", NativeListControl.SelectedText(control, default));
        Assert.DoesNotContain(0x146u, messages);
        Assert.DoesNotContain(0x149u, messages);
        Assert.DoesNotContain(0x148u, messages);
    }

    [Theory]
    [InlineData(-1, "first\tchosen")]
    [InlineData(0, "chosen")]
    [InlineData(1, "first\tchosen")]
    public void SingleListUsesPerItemNonzeroSelectionState(int firstState,
        string expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint control = fixture.Child("LISTBOX", "", 0, 10);
        Native.SendText(control, 0x180, 0, "first", 2, 1000, out _);
        Native.SendText(control, 0x180, 0, "chosen", 2, 1000, out _);
        Native.SendMessageW(control, 0x186, 1, 0);
        var queries = new List<nint>();
        bool currentSelectionQueried = false;
        using var intercept = new Interceptor(control, (message, index) =>
        {
            if (message == 0x188)
            {
                currentSelectionQueried = true;
            }

            if (message == 0x187)
            {
                queries.Add(index);
                return index == 0 ? firstState : null;
            }

            return null;
        });
        Assert.Equal(expected, NativeListControl.SelectedText(control, default));
        Assert.Equal([(nint)0, (nint)1], queries);
        Assert.False(currentSelectionQueried);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectedTextFailureOrCancellationRemainsVisible(bool cancel)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint control = fixture.Child("LISTBOX", "", 8, 10);
        Native.SendText(control, 0x180, 0, "chosen", 2, 1000, out _);
        Native.SendMessageW(control, 0x185, 1, 0);
        using var cancellation = new CancellationTokenSource();
        using var intercept = new Interceptor(control, (message, _) =>
        {
            if (message != 0x18a)
            {
                return null;
            }

            if (cancel)
            {
                cancellation.Cancel();
            }

            return -1;
        });
        if (cancel)
        {
            Assert.ThrowsAny<OperationCanceledException>(() =>
                NativeListControl.SelectedText(control, cancellation.Token));
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() =>
                NativeListControl.SelectedText(control, default));
        }
    }
}
