using System.ComponentModel;
using Xunit;

namespace UWSCSharp.Windows.Tests;

/// <summary>
/// 禁止と送信の両APIを偽物へ置き換え、実入力を変更せずに検証します。
/// </summary>
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class NativeInputOwnershipBackendTests
{
    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, 5)]
    [InlineData(false, 0)]
    [InlineData(false, 5)]
    public void FailedOperationIsRecordedEvenWithoutErrorCode(bool blocked, int error)
    {
        bool? observed = null;
        var backend = new NativeInputOwnershipBackend(value =>
        {
            observed = value;
            return (false, error);
        }, _ => throw new InvalidOperationException("送信してはいけません。"));
        Win32Exception failure = Assert.Throws<Win32Exception>(() => backend.SetBlocked(blocked));
        Assert.Equal(blocked, observed);
        Assert.Equal(error, failure.NativeErrorCode);
        Assert.True(WindowsOperationErrors.Contains(failure));
        Assert.Contains(blocked ? "入力禁止に失敗" : "入力禁止の解除に失敗", failure.Message);
    }

    [Fact]
    public async Task DispatcherCallsBothApisOnItsOwnerThread()
    {
        var calls = new List<(string Operation, int Thread)>();
        ushort observed = 0;
        var backend = new NativeInputOwnershipBackend(blocked =>
        {
            calls.Add((blocked ? "block" : "release", Environment.CurrentManagedThreadId));
            return (true, 5);
        }, input =>
        {
            calls.Add(("send", Environment.CurrentManagedThreadId));
            observed = input.Data.Keyboard.Vk;
            return (1, 0);
        });
        await using var dispatcher = new InputOwnershipDispatcher(backend);
        await dispatcher.SetBlockedAsync(true);
        await dispatcher.SendAsync(new Native.Input
        {
            Type = 1,
            Data = new Native.InputUnion
            {
                Keyboard = new Native.KeyboardInput { Vk = 135 }
            }
        });
        await dispatcher.DisposeAsync();
        Assert.Equal(135, observed);
        Assert.Equal(["block", "send", "release"], calls.Select(c => c.Operation));
        Assert.Single(calls.Select(c => c.Thread).Distinct());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void SendFailureUsesExistingNativeInputErrorContract(int error)
    {
        var backend = new NativeInputOwnershipBackend(
            _ => throw new InvalidOperationException("禁止してはいけません。"),
            _ => (0, error));
        Win32Exception failure = Assert.Throws<Win32Exception>(() => backend.Send(default));
        Assert.Equal(error, failure.NativeErrorCode);
        Assert.True(WindowsOperationErrors.Contains(failure));
        Assert.Contains("入力送信に失敗", failure.Message);
    }
}
