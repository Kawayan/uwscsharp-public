using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class ChkImgCancellationTests
{
    private sealed class Clipboard(Action callback) : IClipboardBackend
    {
        public PixelImage ReadImage(CancellationToken token)
        {
            callback();
            return new(1, 1, [1]);
        }

        public string ReadText(CancellationToken token) =>
            throw new NotSupportedException();

        public void WriteText(string text, CancellationToken token) =>
            throw new NotSupportedException();

        public void WriteImage(PixelImage image, CancellationToken token) =>
            throw new NotSupportedException();
    }

    private sealed class CaptureFixture(Action callback) : IImageCapture
    {
        internal int BoundsCalls;

        public ImageArea Desktop
        {
            get
            {
                BoundsCalls++;
                callback();
                return new(0, 0, 1, 1);
            }
        }

        public PixelImage Capture(ImageArea area, CancellationToken token) =>
            throw new InvalidOperationException("取消後は画像を取得しません。");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancellationDuringTemplateOrBoundsReadPrecedesRangeError(
        bool clipboard, bool reversed)
    {
        using var cancellation = new CancellationTokenSource();
        Action ignore = () => { };
        var capture = new CaptureFixture(clipboard ? ignore : cancellation.Cancel);
        var image = new Clipboard(clipboard ? cancellation.Cancel : ignore);
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("入力は送信しません。"),
            imageCapture: capture, clipboardBackend: image).CreateSession();
        WindowsImageResult before = session.ImageResult;
        OperationCanceledException failure =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.InvokeAsync(BuiltinFunctionNames.ChkImg,
                    [Value.String(""), Value.Numeric(0),
                        Value.Numeric(reversed ? 6 : 5), Value.Numeric(0),
                        Value.Numeric(reversed ? 5 : 6), Value.Numeric(0)],
                    cancellation.Token));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Same(before, session.ImageResult);
        Assert.Equal(clipboard ? 0 : 1, capture.BoundsCalls);
    }
}
