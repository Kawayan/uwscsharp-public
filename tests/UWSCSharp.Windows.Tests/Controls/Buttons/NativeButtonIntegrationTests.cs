using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed partial class NativeButtonOperationTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 2)]
    public async Task PublicQueryUsesRealThreadMarkerAndRawMsaa(bool marker, int expected)
    {
        string className = (marker ? ".NET-BroadcastEventWindow." : "OwnedNativeRoot.")
            + Guid.NewGuid().ToString("N");
        nint instance = GetModuleHandleW(null);
        nint name = Marshal.StringToHGlobalUni(className);
        var definition = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            Instance = instance,
            ClassName = name,
            Procedure = NativeLibrary.GetExport(GetModuleHandleW("user32.dll"), "DefWindowProcW")
        };
        try
        {
            Assert.NotEqual((ushort)0, RegisterClassExW(ref definition));
            try
            {
                int acquisitions = 0;
                var actions = new ConcurrentQueue<string>();
                await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
                {
                    host.Child("BUTTON", "target", 6, 10);
                    host.AccessibleObject = parameter =>
                    {
                        Interlocked.Increment(ref acquisitions);
                        Guid id = NativeButtonAccessibleApi.AccessibleId;
                        var provider = new ButtonIntegrationProvider(actions.Enqueue);
                        return ButtonLresultFromObject(ref id, (nuint)parameter, provider);
                    };
                }, initializeOle: true, rootClass: className);
                Assert.Equal(marker, NativeButtonRouteGate.Matches(fixture.Root,
                    new NativeButtonRouteGatePlatform(), default));
                await using IWindowsAutomationSession session = new WindowsAutomation(
                    _ => throw new InvalidOperationException("物理入力は禁止です。"),
                    listSelectionPlatform: new IntegratedButtonPlatform()).CreateSession();
                Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
                Assert.Equal(Value.Numeric(expected), await session.InvokeAsync("CHKBTN",
                    [id, Value.String("target")], default));
                Assert.Equal(marker, Volatile.Read(ref acquisitions) > 0);
                if (marker)
                {
                    Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
                        [id, Value.String("target"), Value.Numeric(1 | 512)], default));
                    Assert.Equal(["select:1", "action", "select:1", "action"], actions);
                    Assert.True(Volatile.Read(ref acquisitions) >= 5);
                }
            }
            finally
            {
                Assert.True(UnregisterClassW(className, instance));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(name);
        }
    }

    private sealed class IntegratedButtonPlatform : OwnedListSelectionPlatform
    {
        public override nint ForegroundWindow() => 0;

        public override bool IsIconic(nint window) => true;

        public override void Post(nint window, uint message, nint parameter, nint data)
            => throw new InvalidOperationException("native postへのfallbackは禁止です。");

        public override void Sleep(int milliseconds, CancellationToken token)
            => token.ThrowIfCancellationRequested();

        public override void Pump(CancellationToken token)
            => token.ThrowIfCancellationRequested();

        public override Native.Rect Bounds(nint window) => new()
        {
            Left = 10,
            Top = 20,
            Right = 20,
            Bottom = 30
        };
    }

    [DllImport("oleacc.dll", EntryPoint = "LresultFromObject")]
    private static extern nint ButtonLresultFromObject(ref Guid id, nuint parameter,
        [MarshalAs(UnmanagedType.IUnknown)] object provider);
}

/// <summary>
/// 操作要求を記録し、失敗HRESULTを返す所有MSAA providerです。
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class ButtonIntegrationProvider : AccessibleTestProvider, IAccessibleNative
{
    private readonly Action<string> record;

    internal ButtonIntegrationProvider(Action<string> record)
        : base(44, 0x20, "provider", "")
    {
        this.record = record;
    }

    void IAccessibleNative.Select(int flags, object child)
    {
        record("select:" + flags);
        throw new InvalidOperationException("所有providerのSelect失敗です。");
    }

    void IAccessibleNative.DoDefaultAction(object child)
    {
        record("action");
        throw new InvalidOperationException("所有providerのDefaultAction失敗です。");
    }
}
