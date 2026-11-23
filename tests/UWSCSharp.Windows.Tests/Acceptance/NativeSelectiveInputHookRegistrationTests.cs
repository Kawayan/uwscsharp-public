using System.ComponentModel;
using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class NativeSelectiveInputHookRegistrationTests
{
    [Fact]
    public async Task InvalidatedNativeHookReleasesReferenceAndReportsError()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { });
        fixture.Invoke(() =>
        {
            var backend = new TrackingBackend();
            var registration = new SelectiveInputHookRegistration(backend, Pass, Pass);
            try
            {
                registration.Register(SelectiveInputDevices.Keyboard);
                Assert.True(UnhookWindowsHookEx(Assert.Single(backend.Handles)));
                AggregateException error = Assert.Throws<AggregateException>(registration.Unregister);
                Assert.Equal(1404, Assert.IsType<Win32Exception>(Assert.Single(error.InnerExceptions)).NativeErrorCode);
                Assert.False(registration.IsCallbackRooted);
                registration.Register(SelectiveInputDevices.Mouse);
                registration.Unregister();
                Assert.False(registration.IsCallbackRooted);
            }
            finally
            {
                registration.Unregister();
            }
        });
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    public async Task ReleasesRealHooksBeforeOwnerThreadExits(int devices, int count)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { });
        fixture.Invoke(() =>
        {
            var backend = new TrackingBackend();
            var registration = new SelectiveInputHookRegistration(backend, Pass, Pass);
            try
            {
                for (int iteration = 0; iteration < 2; iteration++)
                {
                    backend.Handles.Clear();
                    registration.Register((SelectiveInputDevices)devices);
                    Assert.True(registration.IsCallbackRooted);
                    Assert.Equal(count, backend.Handles.Count);
                    registration.Unregister();
                    Assert.False(registration.IsCallbackRooted);
                    foreach (nint handle in backend.Handles)
                    {
                        // 所有スレッドが生存中に、OSが解除済みのハンドルを拒否することを確認します。
                        bool removed = UnhookWindowsHookEx(handle);
                        int error = Marshal.GetLastPInvokeError();
                        Assert.False(removed);
                        Assert.Equal(1404, error);
                    }
                }
            }
            finally
            {
                registration.Unregister();
            }
        });
    }

    private static nint Pass(int code, nuint message, nint data) =>
        CallNextHookEx(0, code, message, data);

    private sealed class TrackingBackend : ISelectiveInputHookBackend
    {
        private readonly NativeSelectiveInputHookBackend native = new();

        internal List<nint> Handles { get; } = [];

        public (nint Handle, int Error) Install(int type, SelectiveInputHookProcedure procedure)
        {
            (nint handle, int error) = native.Install(type, procedure);
            if (handle != 0)
            {
                Handles.Add(handle);
            }

            return (handle, error);
        }

        public (bool Success, int Error) Remove(nint hook) => native.Remove(hook);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
}
