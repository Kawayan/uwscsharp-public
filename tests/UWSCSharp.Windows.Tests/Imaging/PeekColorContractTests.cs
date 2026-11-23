using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class PeekColorContractTests
{
    private sealed class Clipboard : IClipboardBackend
    {
        internal Action? Read;
        internal int Reads;

        public string ReadText(CancellationToken token) => throw new NotSupportedException();

        public void WriteText(string text, CancellationToken token) => throw new NotSupportedException();

        public PixelImage ReadImage(CancellationToken token)
        {
            Reads++;
            Read?.Invoke();
            return new(1, 1, [0x123456]);
        }

        public void WriteImage(PixelImage image, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class CaptureFixture : IImageCapture
    {
        internal Action? ReadBounds;

        public ImageArea Desktop
        {
            get
            {
                ReadBounds?.Invoke();
                return new(0, 0, 1, 1);
            }
        }

        public PixelImage Capture(ImageArea area, CancellationToken token) =>
            new(1, 1, [0x123456]);
    }

    private static Value N(int value) => Value.Numeric(value);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringBoundsReadIsNotReturnedAsMissingColor(bool clipboard)
    {
        using var cancellation = new CancellationTokenSource();
        var image = new Clipboard { Read = clipboard ? cancellation.Cancel : null };
        var capture = new CaptureFixture { ReadBounds = clipboard ? null : cancellation.Cancel };
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"),
            imageCapture: capture, clipboardBackend: image).CreateSession();
        OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync(BuiltinFunctionNames.PeekColor,
                [N(5), N(5), N(3), N(clipboard ? 1 : 0)], cancellation.Token));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
    }

    [Theory]
    [InlineData(0, 0x563412)]
    [InlineData(3, 0x123456)]
    [InlineData(4, 0x12)]
    [InlineData(5, 0x34)]
    [InlineData(6, 0x56)]
    public async Task ClipboardAndScreenUseTheSameColorComponents(int mode, int expected)
    {
        var image = new Clipboard();
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"),
            imageCapture: new CaptureFixture(), clipboardBackend: image).CreateSession();
        foreach (int source in new[] { 0, 1 })
        {
            Assert.Equal(N(expected), await session.InvokeAsync(BuiltinFunctionNames.PeekColor,
                [N(0), N(0), N(mode), N(source)], default));
        }

        Assert.Equal(1, image.Reads);
    }
}
