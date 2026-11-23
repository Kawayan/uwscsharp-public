using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class UiaMissingPatternRouteTests
{
    [ComVisible(true)]
    [Guid("d6dd68d1-86fd-4332-8666-9abedea2d24c")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IRawProvider
    {
        int Options();

        [return: MarshalAs(UnmanagedType.IUnknown)]
        object? Pattern(int id);

        [return: MarshalAs(UnmanagedType.Struct)]
        object? Property(int id);

        [return: MarshalAs(UnmanagedType.Interface)]
        IRawProvider? Host();
    }

    [ComVisible(true)]
    [Guid("54fcb24b-e18e-47a2-b4d3-eccbe77599a2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IInvokeProvider
    {
        void Invoke();
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class Provider : IRawProvider, IInvokeProvider
    {
        private readonly nint window;
        private readonly int failure;
        private readonly int controlType;
        private IRawProvider? host;
        internal int Actions;
        internal int PatternRequests;

        internal Provider(nint window, int failure, int controlType)
        {
            this.window = window;
            this.failure = failure;
            this.controlType = controlType;
            Marshal.ThrowExceptionForHR(UiaHostProviderFromHwnd(window, out host));
        }

        public int Options() => 0xa2;

        public object? Pattern(int id)
        {
            if (id is not (10000 or 10010 or 10015))
            {
                return null;
            }

            Interlocked.Increment(ref PatternRequests);
            return failure == 0 ? null : this;
        }

        public object? Property(int id) => id switch
        {
            30003 => controlType,
            30005 => "target",
            30009 or 30022 => false,
            30010 or 30016 or 30017 => true,
            30041 => controlType == 50002,
            30020 => unchecked((int)window),
            _ => null
        };

        public IRawProvider? Host() => host;

        public void Invoke()
        {
            Interlocked.Increment(ref Actions);
            throw new COMException("操作開始後の失敗", failure);
        }

        internal void Disconnect()
        {
            UiaReturnRawElementProvider(window, 0, 0, null);
            if (host is not null && Marshal.IsComObject(host))
            {
                Marshal.ReleaseComObject(host);
            }

            host = null;
        }
    }

    [DllImport("UIAutomationCore.dll")]
    private static extern int UiaHostProviderFromHwnd(nint window,
        [MarshalAs(UnmanagedType.Interface)] out IRawProvider? provider);

    [DllImport("UIAutomationCore.dll")]
    private static extern nint UiaReturnRawElementProvider(nint window,
        nuint parameter, nint objectId,
        [MarshalAs(UnmanagedType.Interface)] IRawProvider? provider);

    [DllImport("oleacc.dll")]
    private static extern nint LresultFromObject(ref Guid iid, nuint parameter,
        [MarshalAs(UnmanagedType.IUnknown)] object provider);

    [Theory]
    [InlineData(0, 1)]
    [InlineData(536870912, 0)]
    [InlineData(1073741824, 0)]
    [InlineData(32768, 1)]
    public async Task MissingUiaPatternFallsBackOnlyOnDefaultRoute(int route, int expected)
    {
        await Run(route, 0, expected);
    }

    [Theory]
    [InlineData(50002, 44, 1)]
    [InlineData(50013, 45, 1)]
    [InlineData(50019, 37, 8)]
    public async Task MissingToggleOrSelectionPatternCanReachMatchingMsaaKind(
        int controlType, int role, int kind)
    {
        await Run(0, 0, 1, controlType, role, kind);
    }

    [Theory]
    [InlineData(unchecked((int)0x80040204))]
    [InlineData(unchecked((int)0x80004002))]
    [InlineData(unchecked((int)0x80004005))]
    public async Task StartedUiaActionDoesNotReplayThroughMsaa(int failure)
    {
        await Run(0, failure, 0);
    }

    private static async Task Run(int route, int failure, int expected,
        int controlType = 50000, int role = 43, int kind = 1)
    {
        int accessibleActions = 0;
        var accessible = new AccessibleTestProvider(role, 0, "target", "")
        {
            TakingFocus = _ => { },
            PerformingAction = () => Interlocked.Increment(ref accessibleActions)
        };
        Provider? provider = null;
        ToolbarClickTextTests.Interceptor? hook = null;
        using (PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            provider = new Provider(host.Root, failure, controlType);
            host.AccessibleObject = parameter =>
            {
                Guid iid = typeof(IAccessibleNative).GUID;
                return LresultFromObject(ref iid, (nuint)parameter, accessible);
            };
            hook = new ToolbarClickTextTests.Interceptor(host.Root,
                (message, parameter, objectId) =>
                {
                    if (message == 0x3d && unchecked((int)objectId) == -25)
                    {
                        return UiaReturnRawElementProvider(host.Root,
                            (nuint)parameter, objectId, provider);
                    }

                    if (message == 2)
                    {
                        provider.Disconnect();
                    }

                    return null;
                });
        }, initializeOle: true))
        {
            await using IWindowsAutomationSession session = new WindowsAutomation(
                _ => throw new InvalidOperationException("物理入力は禁止です。"))
                .CreateSession();
            Value id = await session.InvokeAsync("HNDTOID",
                [Value.Numeric((long)fixture.Root)], default);
            if (failure == 0)
            {
                Assert.Equal(Value.Numeric(expected), await session.InvokeAsync("CLKITEM",
                    [id, Value.String("target"), Value.Numeric(route | kind | 512)], default));
            }
            else
            {
                Exception error = await Record.ExceptionAsync(async () =>
                    await session.InvokeAsync("CLKITEM",
                        [id, Value.String("target"), Value.Numeric(513)], default));
                Assert.NotNull(error);
                if (failure == unchecked((int)0x80004002))
                {
                    Assert.IsType<InvalidCastException>(error);
                }
                else
                {
                    Assert.IsType<COMException>(error);
                }
                Assert.Equal(failure, error.HResult);
                Assert.Equal(1, provider!.Actions);
            }

            Assert.Equal(expected, Volatile.Read(ref accessibleActions));
            if (route is 0 or 1073741824)
            {
                Assert.True(provider!.PatternRequests > 0);
            }
        }

        GC.KeepAlive(hook);
    }
}
