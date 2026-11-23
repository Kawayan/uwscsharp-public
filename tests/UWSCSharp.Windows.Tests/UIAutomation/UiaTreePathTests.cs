using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed partial class UiaSelectionTests
{
    [Theory]
    [InlineData("parent", "child")]
    [InlineData(" 親 ", " 子 ")]
    public async Task TreeSelectionContainsItsRawParentPath(
        string parentName, string childName)
    {
        nint tree = 0;
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            SliderControlTests.InitializeControls(2);
            tree = host.Child("SysTreeView32", "", 0, 10, height: 160);
            nint parent = TreeControlTests.Add(tree, 0, parentName);
            nint child = TreeControlTests.Add(tree, parent, childName);
            Native.SendMessageW(tree, 0x1102, 2, parent);
            Native.SendMessageW(tree, 0x110b, 9, child);
        }, initializeOle: true);
        Assert.Equal("\\" + parentName + "\\" + childName,
            await UiaSelection.ReadAsync(fixture.Root, 1, 0, default));
        Native.SendMessageW(tree, 0x110b, 9, 0);
        Assert.Equal("", await UiaSelection.ReadAsync(fixture.Root, 1, 0, default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            UiaSelection.ReadAsync(fixture.Root, 1, 0, cancellation.Token));
    }
}
