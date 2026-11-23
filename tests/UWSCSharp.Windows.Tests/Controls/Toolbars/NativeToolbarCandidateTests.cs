using System.Runtime.InteropServices;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeToolbarCandidateTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowClass
    {
        internal uint Size;
        internal uint Style;
        internal nint Procedure;
        internal int ClassExtra;
        internal int WindowExtra;
        internal nint Instance;
        internal nint Icon;
        internal nint Cursor;
        internal nint Background;
        internal nint MenuName;
        internal nint ClassName;
        internal nint SmallIcon;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetClassInfoExW(nint instance, string name, ref WindowClass value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WindowClass value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClassW(string name, nint instance);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);

    [Theory]
    [InlineData(false, 4, true, null)]
    [InlineData(false, 12, false, null)]
    [InlineData(false, 0, false, null)]
    [InlineData(true, 4, true, null)]
    [InlineData(true, 12, true, null)]
    [InlineData(true, 8, false, null)]
    [InlineData(true, 12, true, "cancel")]
    [InlineData(true, 12, true, "zero")]
    public void HiddenRequiresEnabledButtonAndOwnedShellRoot(
        bool shell, byte state, bool expected, string? failure)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string shellClass = "SHELL_TRAYWND";
        nint instance = GetModuleHandleW(null);
        nint name = Marshal.StringToHGlobalUni(shellClass);
        var value = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>() };
        try
        {
            if (shell)
            {
                Assert.True(GetClassInfoExW(0, "STATIC", ref value));
                value.ClassName = name;
                value.Instance = instance;
                // 専用processのlocal classだけを登録し、実Shellへアクセスしません。
                value.Style &= ~0x4000u;
                Assert.NotEqual((ushort)0, RegisterClassExW(ref value));
            }

            try
            {
                using var fixture = new ButtonControlTests.Fixture(rootClass: shell ? shellClass : "STATIC");
                nint toolbar = ToolbarControlTests.Create(fixture, 10);
                ToolbarControlTests.Add(toolbar, "target", 701, state: state);
                int reads = 0;
                List<string> events = [];
                using var cancellation = new CancellationTokenSource();
                using var hook = new ToolbarClickTextTests.Interceptor(toolbar, (message, _, pointer) =>
                {
                    if (message == 0x44b && pointer != 0)
                    {
                        reads++;
                        events.Add("text");
                    }

                    if (message == 0x404)
                    {
                        Assert.Equal(0, pointer);
                        events.Add("unhide");
                        if (failure == "zero")
                        {
                            return 0;
                        }
                    }

                    if (message == 0x41d)
                    {
                        events.Add("rectangle");
                    }

                    return null;
                }, after: (message, parameter, _) =>
                {
                    if (message == 0x404)
                    {
                        Assert.Equal(701, parameter);
                        if (failure == "cancel")
                        {
                            cancellation.Cancel();
                        }
                    }
                });
                if (failure == "cancel")
                {
                    Assert.ThrowsAny<OperationCanceledException>(() =>
                        NativeToolbarControl.Click(toolbar, "target", 1, false, cancellation.Token,
            platform: new OwnedListSelectionPlatform()));
                }
                else
                {
                    Assert.Equal((expected ? 1 : 0, expected, expected),
                        NativeToolbarControl.Click(toolbar, "target", 1, false, default,
            platform: new OwnedListSelectionPlatform()));
                }

                string[] expectedEvents = !expected ? [] : failure == "cancel"
                    ? ["text", "unhide"] : shell
                    ? ["text", "unhide", "rectangle"] : ["text", "rectangle"];
                Assert.Equal(expectedEvents, events);
                bool remainsHidden = (state & 8) != 0 && (!expected || failure == "zero");
                Assert.Equal(remainsHidden,
                    (Native.SendMessageW(toolbar, 0x412, 701, 0) & 8) != 0);
                Assert.Equal(expected ? 1 : 0, reads);
                Assert.Null(fixture.CallbackFailure);
            }
            finally
            {
                if (shell)
                {
                    Assert.True(UnregisterClassW(shellClass, instance));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(name);
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void SeparatorAndEmptyTextReachMatcher(byte style, bool empty)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint toolbar = ToolbarControlTests.Create(fixture, 10);
        ToolbarControlTests.Add(toolbar, "unused", 701, style: style);
        int reads = 0;
        using var hook = new ToolbarClickTextTests.Interceptor(toolbar, (message, _, pointer) =>
        {
            if (message != 0x44b)
            {
                return null;
            }

            if (pointer != 0)
            {
                reads++;
                char[] text = [.. (empty ? "" : "target"), '\0'];
                Marshal.Copy(text, 0, pointer, text.Length);
            }

            return empty ? 0 : 6;
        });
        Assert.Equal((1, true, true), NativeToolbarControl.Click(toolbar,
            empty ? "" : "target", 1, false, default,
            platform: new OwnedListSelectionPlatform()));
        Assert.Equal(1, reads);
        Assert.Null(fixture.CallbackFailure);
    }
}
