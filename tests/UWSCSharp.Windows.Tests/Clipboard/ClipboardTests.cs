using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class ClipboardTests
{
    private sealed class Backend : IClipboardBackend
    {
        internal string Text = "日本語😀";
        internal PixelImage Image = new(2, 1, [0x123456, 0xabcdef]);
        internal Exception? Failure;

        public string ReadText(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Failure is not null)
            {
                throw Failure;
            }

            return Text;
        }

        public void WriteText(string text, CancellationToken token)
        {
            _ = ReadText(token);
            Text = text;
        }

        public PixelImage ReadImage(CancellationToken token)
        {
            _ = ReadText(token);
            return Image;
        }

        public void WriteImage(PixelImage image, CancellationToken token)
        {
            _ = ReadText(token);
            Image = image;
        }
    }

    private sealed class CaptureFixture : IImageCapture
    {
        public ImageArea Desktop => new(0, 0, 2, 1);

        public PixelImage Capture(ImageArea area, CancellationToken token) =>
            new(area.Width, area.Height, [0x123456, 0xabcdef]);
    }

    [Fact]
    public async Task ScriptRoutesTextAndImageClipboardOperations()
    {
        var backend = new Backend();
        var automation = new WindowsAutomation(_ => throw new InvalidOperationException(),
            imageCapture: new CaptureFixture(), clipboardBackend: backend);
        var output = new List<string>();
        await new Interpreter(windows: automation).RunAsync("""
            PRINT GETSTR(0)
            SENDSTR(0,"changed")
            PRINT GETSTR(0)
            PRINT PEEKCOLOR(0,0,COL_RGB,TRUE)
            PRINT CHKIMG()
            PRINT G_IMG_X
            PRINT G_IMG_Y
            SAVEIMG()
            PRINT PEEKCOLOR(1,0,COL_RGB,TRUE)
            """, output.Add);
        Assert.Equal(["日本語😀", "changed", "1193046", "1", "0", "0", "11259375"], output);
        Assert.Equal([0x123456, 0xabcdef], backend.Image.Pixels);
    }

    [Fact]
    public async Task EmptyTextBoundsFailureAndCancellationPreserveState()
    {
        var backend = new Backend { Text = "" };
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException(), imageCapture: new CaptureFixture(),
            clipboardBackend: backend).CreateSession();
        Assert.Equal(Value.String(""), await session.InvokeAsync("GETSTR", [Value.Numeric(0)], default));
        Assert.Equal(Value.Numeric(-1), await session.InvokeAsync("PEEKCOLOR",
            [Value.Numeric(-1), Value.Numeric(0), Value.Numeric(3), Value.Numeric(1)], default));
        backend.Failure = new InvalidOperationException("clipboardは使用中です。");
        WindowsImageResult previous = session.ImageResult;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.InvokeAsync("CHKIMG", [], default));
        Assert.Same(previous, session.ImageResult);
        backend.Failure = null;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync("SENDSTR", [Value.Numeric(0), Value.String("new")], cancellation.Token));
        Assert.Equal("", backend.Text);
    }

    [Fact]
    public void DibEncoderUsesBottomUpBgrWithPaddedRows()
    {
        var image = new PixelImage(3, 2, [1, 2, 3, 0xabcdef, 0x123456, 0]);
        byte[] data = ClipboardBackend.EncodeDib(image, default);
        Assert.Equal(64, data.Length);
        Assert.Equal(40, BitConverter.ToInt32(data, 0));
        Assert.Equal(3, BitConverter.ToInt32(data, 4));
        Assert.Equal(2, BitConverter.ToInt32(data, 8));
        Assert.Equal(24, BitConverter.ToInt16(data, 14));
        Assert.Equal(new byte[] { 0xef, 0xcd, 0xab, 0x56, 0x34, 0x12,
            0, 0, 0, 0, 0, 0, 1, 0, 0, 2, 0, 0, 3, 0, 0, 0, 0, 0 },
            data[40..]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ClipboardBackend.EncodeDib(image, cancellation.Token));
    }
}
