using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class NativeScriptHotkeyRegistrationTests
{
    [Fact]
    public async Task ModifierOnlyRegistrationCanBeRemovedAndRegisteredAgain()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using var source = new WindowsScriptHotkeySource(new NativeScriptHotkeyPlatform());
        var key = new ScriptHotkey(0, 6);
        Assert.True(source.TryRegister(key, () => { }));
        Assert.True(source.TryUnregister(key));
        Assert.True(source.TryRegister(key, () => { }));
    }

    [Fact]
    public async Task ConflictRemovalAndThreadShutdownReleaseNativeRegistration()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var key = new ScriptHotkey(0x7A, 6);
        Assert.False(SelectiveInputEmergencyShortcut.IsReserved(key));
        await using var first = new WindowsScriptHotkeySource(new NativeScriptHotkeyPlatform());
        await using var second = new WindowsScriptHotkeySource(new NativeScriptHotkeyPlatform());
        Assert.True(first.TryRegister(key, () => { }));
        Assert.False(second.TryRegister(key, () => { }));
        Assert.True(first.TryUnregister(key));
        Assert.True(second.TryRegister(key, () => { }));
        await second.DisposeAsync();
        Assert.True(first.TryRegister(key, () => { }));
    }
}
