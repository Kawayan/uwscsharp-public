using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeListViewStateImageTests
{
    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(0, true, 13)]
    [InlineData(1, false, 0)]
    [InlineData(1, true, 32)]
    [InlineData(2, false, 32)]
    [InlineData(2, true, 0)]
    [InlineData(3, false, 0)]
    [InlineData(3, true, 32)]
    public void ImageUsesLowBitAndNoImageOnlyActivatesOn(int image, bool on, int key)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint view = ListViewControlTests.Create(fixture, 10);
        ListViewControlTests.SetText(view, 0, "item");
        int current = image;
        List<int> keys = [];
        using var hook = new ToolbarClickTextTests.Interceptor(view, (message, parameter, data) =>
        {
            if (message == 0x102c && data == 0xf000)
            {
                Assert.Equal(0, parameter);
                return current << 12;
            }

            if (message == 0x100 && parameter is 13 or 32)
            {
                keys.Add((int)parameter);
                if (parameter == 32)
                {
                    current = on ? 2 : 1;
                }

                return 0;
            }

            return null;
        });
        Assert.Equal((1, true, true), NativeListViewControl.Click(
            view, "item", 1, on, default, platform: new OwnedListSelectionPlatform(), back: true));
        Assert.Equal(key == 0 ? [] : new[] { key }, keys);
        Assert.Null(fixture.CallbackFailure);
    }
}
