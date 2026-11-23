using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class ToolbarControlTests
{
    [DllImport("kernel32.dll")]
    private static extern nint VirtualAlloc(nint address, nuint size,
        uint type, uint protection);

    [DllImport("kernel32.dll")]
    private static extern bool VirtualProtect(nint address, nuint size,
        uint protection, out uint previous);

    [DllImport("kernel32.dll")]
    private static extern bool VirtualFree(nint address, nuint size, uint type);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Button
    {
        public int Image;
        public int Id;
        public byte State;
        public byte Style;
        public ushort Reserved;
        public nuint Data;
        public nint Text;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Info
    {
        public uint Size;
        public uint Mask;
        public int Id;
        public int Image;
        public byte State;
        public byte Style;
        public ushort Width;
        public nuint Data;
        public nint Text;
        public int Capacity;
    }

    internal static void Add(nint toolbar, string? text, int id,
        byte state = 4, byte style = 0)
    {
        nint stringIndex = -1;
        if (text is not null)
        {
            nint value = Marshal.StringToHGlobalUni(text + "\0");
            try
            {
                stringIndex = Native.SendMessageW(toolbar, 0x44d, 0, value);
                Assert.True(stringIndex >= 0);
            }
            finally
            {
                Marshal.FreeHGlobal(value);
            }
        }

        var button = new Button { Image = -2, Id = id, State = state, Style = style, Text = stringIndex };
        nint pointer = Marshal.AllocHGlobal(Marshal.SizeOf<Button>());
        try
        {
            Marshal.StructureToPtr(button, pointer, false);
            Assert.NotEqual(0, Native.SendMessageW(toolbar, 0x444, 1, pointer));
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    internal static nint Create(ButtonControlTests.Fixture fixture,
        int top, int style = 0, string className = "ToolbarWindow32")
    {
        SliderControlTests.InitializeControls();
        nint toolbar = fixture.Child(className, "", style | 14, top);
        Native.SendMessageW(toolbar, 0x41e, Marshal.SizeOf<Button>(), 0);
        return toolbar;
    }

    private static Value N(long value) => Value.Numeric(value);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoundedButtonInfoTextRequiresDirectText(bool setDirectText)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint toolbar = Create(fixture, 10);
        Add(toolbar, "pool-name", 701);
        nint text = Marshal.StringToHGlobalUni("direct-name");
        nint pointer = Marshal.AllocHGlobal(Marshal.SizeOf<Info>());
        try
        {
            var info = new Info
            {
                Size = (uint)Marshal.SizeOf<Info>(),
                Mask = 2,
                Text = text,
                Capacity = 12,
            };
            if (setDirectText)
            {
                Marshal.StructureToPtr(info, pointer, false);
                Assert.NotEqual(0,
                    Native.SendMessageW(toolbar, 0x440, 701, pointer));
            }

            // GETBUTTONINFOでpool文字列を読めるという前提を採用しません。
            Marshal.WriteInt16(text, 0);
            Marshal.StructureToPtr(info, pointer, false);
            Assert.Equal(0,
                Native.SendMessageW(toolbar, 0x43f, 701, pointer));
            Assert.Equal(setDirectText ? "direct-name" : "",
                Marshal.PtrToStringUni(text));
            string expected = setDirectText ? "direct-name" : "pool-name";
            Assert.Equal(expected.Length,
                Native.SendMessageW(toolbar, 0x44b, 701, text));
            Assert.Equal(expected, Marshal.PtrToStringUni(text));
            Assert.Null(fixture.CallbackFailure);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
            Marshal.FreeHGlobal(text);
        }
    }

    [Fact]
    public void PointerReadStopsBeforeInaccessiblePageAndRejectsBadAddresses()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        using var buffer = new NativeControlTextBuffer(fixture.Root, ControlTextKind.Toolbar);
        int page = Environment.SystemPageSize;
        nint memory = VirtualAlloc(0, (nuint)(page * 2), 0x3000, 4);
        Assert.NotEqual(0, memory);
        try
        {
            Assert.True(VirtualProtect(memory + page, (nuint)page, 1, out _));
            Marshal.WriteInt16(memory + page - 4, 'Z');
            Marshal.WriteInt16(memory + page - 2, 0);
            Assert.Equal("Z", buffer.ReadStringAt(memory + page - 4, default));
            Assert.Throws<System.ComponentModel.Win32Exception>(() => buffer.ReadStringAt(2, default));
            Assert.Throws<InvalidOperationException>(() => buffer.ReadStringAt(1, default));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => buffer.ReadStringAt(memory, cancellation.Token));
        }
        finally
        {
            Assert.True(VirtualFree(memory, 0, 0x8000));
        }
    }

    [Fact]
    public async Task ReadsNormalizedCaptionsAndFiltersButtonStates()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(IntPtr.Size == 8 ? 32 : 20, Marshal.SizeOf<Button>());
        using var fixture = new ButtonControlTests.Fixture();
        nint toolbar = Create(fixture, 10);
        Add(toolbar, " &Alpha... ", 101);
        Add(toolbar, "Alpha", 102);
        Add(toolbar, "disabled", 103, state: 0);
        Add(toolbar, "hidden", 104, state: 12);
        Add(toolbar, "A", 105);
        Add(toolbar, null, 106);
        Add(toolbar, null, 0, style: 1);
        string longText = new('長', 4097);
        Add(toolbar, longText, 107);
        nint disabled = Create(fixture, 60, 0x08000000);
        Add(disabled, "disabled control", 108);
        nint hidden = Create(fixture, 100);
        Add(hidden, "hidden control", 109);
        Native.ShowWindow(hidden, 0);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        Assert.Equal(N(5), await session.InvokeAsync("GETITEM", [id, N(64), N(999)], default));
        Assert.Equal(["Alpha", "Alpha", "disabled", longText, "disabled control"], session.AllItems);
        Assert.Equal(N(3), await session.InvokeAsync("GETITEM", [id, N(64), N(-1), N(0), N(1)], default));
        Assert.Equal(["Alpha", "Alpha", longText], session.AllItems);
        fixture.Child("Button", "button", 0, 150);
        Assert.Equal(N(6), await session.InvokeAsync("GETITEM", [id, N(65)], default));
        Assert.Equal("button", session.AllItems[0]);
    }

    [Fact]
    public async Task ReadsOverriddenTextAndAppendsTopLevelMenuCaptions()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint toolbar = Create(fixture, 10);
        Add(toolbar, "original", 101);
        nint text = Marshal.StringToHGlobalUni(" &Changed... ");
        nint pointer = Marshal.AllocHGlobal(Marshal.SizeOf<Info>());
        try
        {
            var info = new Info { Size = (uint)Marshal.SizeOf<Info>(), Mask = 2, Text = text };
            Marshal.StructureToPtr(info, pointer, false);
            Assert.NotEqual(0, Native.SendMessageW(toolbar, 0x440, 101, pointer));
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
            Marshal.FreeHGlobal(text);
        }

        nint menu = MenuControlTests.CreateMenu();
        try
        {
            Assert.True(MenuControlTests.AppendMenuW(menu, 0, 201, "&File"));
            Assert.True(MenuControlTests.AppendMenuW(menu, 3, 202, "disabled menu"));
            Assert.True(MenuControlTests.AppendMenuW(menu, 0, 203, "A"));
            Assert.True(MenuControlTests.AppendMenuW(menu, 0x800, 0, null));
            Assert.True(MenuControlTests.SetMenu(fixture.Root, menu));
            List<string> output = [];
            await new Interpreter(windows: new WindowsAutomation(
                _ => throw new InvalidOperationException("物理入力は禁止です。")))
                .RunAsync($"""
                    id = HNDTOID({(long)fixture.Root})
                    FOR item IN GETITEM(id, ITM_TOOLBAR)
                        PRINT item
                    NEXT
                    """, output.Add);
            Assert.Equal(["Changed", "File", "disabled menu", "A", ""], output);
            await using IWindowsAutomationSession session = fixture.Session();
            Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
            Assert.Equal(N(3), await session.InvokeAsync("GETITEM",
                [id, N(64), N(999), N(0), N(1)], default));
            Assert.Equal(["Changed", "File", "A"], session.AllItems);
            IReadOnlyList<string> snapshot = session.AllItems;
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.InvokeAsync("GETITEM", [id, N(64)], cancellation.Token));
            Assert.Same(snapshot, session.AllItems);
        }
        finally
        {
            MenuControlTests.SetMenu(fixture.Root, 0);
            MenuControlTests.DestroyMenu(menu);
        }
    }
}
