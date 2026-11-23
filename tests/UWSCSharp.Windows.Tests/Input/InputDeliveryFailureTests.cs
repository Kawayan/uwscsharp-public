using System.ComponentModel;
using System.Runtime.InteropServices;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class InputDeliveryFailureTests
{
    [Fact]
    public async Task NativeInvalidSizeFailurePreservesErrorAndDoesNotHoldKey()
    {
        int attempts = 0;
        IWindowsAutomationSession session = new WindowsAutomation(input =>
        {
            attempts++;
            NativeInputSender.Send(input, value =>
            {
                // 不正サイズはOSで拒否されるため実入力を発生させません。
                uint sent = Native.SendInput(1, [value], 0);
                return (sent, Marshal.GetLastWin32Error());
            });
        }).CreateSession();
        Win32Exception error = await Assert.ThrowsAsync<Win32Exception>(async () =>
            await session.InvokeAsync("KBD", [Value.Numeric(65)], default));
        Assert.Equal(87, error.NativeErrorCode);
        Assert.Contains("入力送信に失敗", error.Message);
        await session.DisposeAsync();
        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task FailedDownReportsDeliveryFailureAndDoesNotInventHeldInput(int nativeError)
    {
        int attempts = 0;
        IWindowsAutomationSession session = new WindowsAutomation(input =>
        {
            attempts++;
            NativeInputSender.Send(input, _ => (0, nativeError));
        }).CreateSession();
        Win32Exception error = await Assert.ThrowsAsync<Win32Exception>(async () =>
            await session.InvokeAsync("KBD", [Value.Numeric(65)], default));
        Assert.Equal(nativeError, error.NativeErrorCode);
        Assert.Contains("入力送信に失敗", error.Message);
        if (nativeError == 0)
        {
            Assert.Contains("詳細なエラーコードは返されません", error.Message);
        }

        await session.DisposeAsync();
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task FailedUpRetainsHeldKeyForCleanupRetry()
    {
        var flags = new List<uint>();
        IWindowsAutomationSession session = new WindowsAutomation(input =>
        {
            flags.Add(input.Data.Keyboard.Flags);
            NativeInputSender.Send(input, _ => flags.Count == 2 ? (0u, 0) : (1u, 5));
        }).CreateSession();
        await Assert.ThrowsAsync<Win32Exception>(async () =>
            await session.InvokeAsync("KBD", [Value.Numeric(65)], default));
        await session.DisposeAsync();
        Assert.Equal([0u, 2u, 2u], flags);
    }
}
