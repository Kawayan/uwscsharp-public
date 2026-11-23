using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class ImageOperationTests
{
    private sealed class CaptureFixture : IImageCapture
    {
        internal PixelImage Image
        {
            get; set;
        } = new(5, 3,
            [1, 2, 1, 2, 9, 1, 2, 1, 2, 9, 9, 9, 9, 1, 2]);

        public ImageArea Desktop { get; set; } = new(-2, 4, 5, 3);

        internal Action? BeforeCapture
        {
            get; set;
        }

        internal List<ImageArea> Areas { get; } = [];

        public PixelImage Capture(ImageArea area, CancellationToken token)
        {
            BeforeCapture?.Invoke();
            token.ThrowIfCancellationRequested();
            Areas.Add(area);
            int[] pixels = new int[area.Width * area.Height];
            for (int y = 0; y < area.Height; y++)
            {
                for (int x = 0; x < area.Width; x++)
                {
                    pixels[y * area.Width + x] = Image[area.X - Desktop.X + x,
                        area.Y - Desktop.Y + y];
                }
            }

            return new(area.Width, area.Height, pixels);
        }
    }

    private sealed class Folder : IDisposable
    {
        internal string Path
        {
            get;
        } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "uwsc-image-" + Guid.NewGuid().ToString("N"));

        internal Folder() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, true);
    }

    private static Value N(int value) => Value.Numeric(value);

    private static WindowsAutomation Automation(CaptureFixture capture) => new(
        _ => throw new InvalidOperationException("物理入力は禁止です。"), imageCapture: capture);

    private static ButtonControlTests.Fixture CreatePhysicalFixture()
    {
        using var dpi = new PhysicalDpiScope();
        return new ButtonControlTests.Fixture();
    }

    [Fact]
    public async Task WindowClientCropAndMouseOriginUseNativeCoordinates()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using ButtonControlTests.Fixture fixture = CreatePhysicalFixture();
        Native.Rect rect;
        using (var dpi = new PhysicalDpiScope())
        {
            Assert.True(Native.GetWindowRect(fixture.Root, out rect));
        }
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        int[] pixels =
        [
            .. Enumerable.Range(0, width * height)
                .Select(index => 0x120000 | (index / width << 8) | index % width)
        ];
        var capture = new CaptureFixture
        {
            Desktop = new(rect.Left, rect.Top, width, height),
            Image = new(width, height, pixels)
        };
        using var folder = new Folder();
        string path = System.IO.Path.Combine(folder.Path, "client.bmp");
        await using IWindowsAutomationSession session = Automation(capture).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        var origin = new Native.Point(0, 0);
        using (var dpi = new PhysicalDpiScope())
        {
            Assert.True(Native.ClientToScreen(fixture.Root, ref origin));
        }
        await session.InvokeAsync("SAVEIMG", [Value.String(path), id, N(10), N(12), N(2), N(2), N(1), N(0), N(1)], default);
        Assert.Equal(new(origin.X + 10, origin.Y + 12, 2, 2), capture.Areas[^1]);
        int expectedX = origin.X - rect.Left + 10;
        int expectedY = origin.Y - rect.Top + 12;
        int expectedColor = 0x120000 | expectedY << 8 | expectedX;
        Assert.Equal([expectedColor, 0x120000 | expectedY << 8 | expectedX + 1,
            0x120000 | (expectedY + 1) << 8 | expectedX,
            0x120000 | (expectedY + 1) << 8 | expectedX + 1], PixelImage.LoadBmp(path, default).Pixels);
        await session.InvokeAsync("MOUSEORG", [id, N(1)], default);
        Assert.Equal(N(expectedColor), await session.InvokeAsync("PEEKCOLOR", [N(10), N(12), N(3)], default));
        Assert.Equal(N(1), await session.InvokeAsync("CHKIMG",
            [Value.String(path), N(0), N(10), N(12), N(11), N(13), N(-1)], default));
        Assert.Equal((10, 12), (session.ImageResult.X, session.ImageResult.Y));
        Assert.Equal([10], session.ImageResult.AllX);
        Assert.Equal([12], session.ImageResult.AllY);
        await session.InvokeAsync("SAVEIMG", [Value.String(path), id, N(width - 2), N(height - 2), N(0), N(0), N(0), N(0), N(1)], default);
        Assert.Equal(2, PixelImage.LoadBmp(path, default).Width);
        Assert.Equal(2, PixelImage.LoadBmp(path, default).Height);
        byte[] previous = File.ReadAllBytes(path);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await session.InvokeAsync("SAVEIMG", [Value.String(path), id, N(width - 1), N(0), N(2), N(2)], default));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.InvokeAsync("SAVEIMG", [Value.String(path), N(999)], default));
        Assert.Equal(previous, File.ReadAllBytes(path));
        WindowsImageResult snapshot = session.ImageResult;
        await session.InvokeAsync("MOUSEORG", [N(999), N(1)], default);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.InvokeAsync("CHKIMG", [Value.String(path)], default));
        Assert.Same(snapshot, session.ImageResult);
    }

    [Fact]
    public async Task ShapeModePublishesCoordinatesThroughInterpreter()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var folder = new Folder();
        string path = System.IO.Path.Combine(folder.Path, "shape.bmp");
        new PixelImage(2, 1, [1, 2]).SaveBmp(path, default);
        List<string> output = [];
        await new Interpreter(windows: Automation(new CaptureFixture()))
            .RunAsync($"""
                PRINT CHKIMG("{path}", -1, , , , , -1, IMG_MSK_BGR1)
                PRINT G_IMG_X
                PRINT G_IMG_Y
                PRINT ALL_IMG_X[4]
                """, output.Add);
        Assert.Equal(["5", "0", "6", "0"], output);
    }

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task ChkImgPreservesDetectionAndCountTypes()
    {
        using var folder = new Folder();
        string match = System.IO.Path.Combine(folder.Path, "match.bmp");
        string miss = System.IO.Path.Combine(folder.Path, "miss.bmp");
        string single = System.IO.Path.Combine(folder.Path, "single.bmp");
        new PixelImage(2, 1, [1, 2]).SaveBmp(match, default);
        new PixelImage(2, 1, [3, 4]).SaveBmp(miss, default);
        new PixelImage(1, 1, [123]).SaveBmp(single, default);
        var automation = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"),
            imageCapture: new CaptureFixture(), swappedMouseButtons: false);
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync($"""
            hit = CHKIMG("{match}")
            absent = CHKIMG("{match}", , , , , , 6)
            count = CHKIMG("{match}", , , , , , -2)
            zero = CHKIMG("{miss}", , , , , , -1)
            shape = CHKIMG("{single}", -1, , , , , -1)
            PRINT VARTYPE(hit)
            PRINT VARTYPE(absent)
            PRINT VARTYPE(count)
            PRINT VARTYPE(zero)
            PRINT VARTYPE(shape)
            PRINT hit + 0
            PRINT absent + 0
            PRINT count
            PRINT zero
            PRINT shape + 0
            """, output.Add);
        Assert.Equal(["11", "11", "5", "5", "11", "1", "0", "5", "0", "1"],
            output);
    }

    [Fact]
    public void ShapeKeepsOriginalRowProgressionAndOneDimensionalRelations()
    {
        var template = new PixelImage(3, 2, [1, 2, 2, 1, 3, 3]);
        var source = new PixelImage(3, 2, [5, 6, 99, 5, 7, 7]);
        Assert.Single(ImageMatcher.Match(source, template, -1, 1, 0, default));
        Assert.Empty(ImageMatcher.Match(new(3, 2, [5, 6, 99, 8, 7, 7]),
            template, -1, 1, 0xffffff, default));
        Assert.Empty(ImageMatcher.Match(new(3, 2, [5, 6, 99, 5, 7, 8]),
            template, -1, 1, 0, default));
        Assert.Single(ImageMatcher.Match(new(3, 1, [5, 5, 6]),
            new(3, 1, [1, 1, 2]), -1, 1, 0, default));
        Assert.Single(ImageMatcher.Match(new(1, 3, [5, 5, 6]),
            new(1, 3, [1, 1, 2]), -1, 1, 0, default));
        Assert.Empty(ImageMatcher.Match(new(1, 3, [5, 6, 6]),
            new(1, 3, [1, 1, 2]), -1, 1, 0, default));
        Assert.Equal([(0, 0)], ImageMatcher.Match(
            new(1, 1, [1]), new(1, 1, [2]), -1, 1, 0, default));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(99)]
    public async Task SinglePixelShapeUsesSpecialSuccessExit(int number)
    {
        using var folder = new Folder();
        string path = System.IO.Path.Combine(folder.Path, "single.bmp");
        new PixelImage(1, 1, [123]).SaveBmp(path, default);
        await using IWindowsAutomationSession session = Automation(new CaptureFixture()).CreateSession();
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CHKIMG",
            [Value.String(path), Value.Numeric(-1), new(ValueKind.Omitted),
                new(ValueKind.Omitted), new(ValueKind.Omitted), new(ValueKind.Omitted),
                Value.Numeric(number)], default));
        Assert.Equal(0, session.ImageResult.X);
        Assert.Equal(0, session.ImageResult.Y);
        Assert.Empty(session.ImageResult.AllX);
        new PixelImage(2, 1, [1, 2]).SaveBmp(path, default);
        Assert.Equal(Value.Numeric(5), await session.InvokeAsync("CHKIMG",
            [Value.String(path), Value.Numeric(-1), new(ValueKind.Omitted),
                new(ValueKind.Omitted), new(ValueKind.Omitted), new(ValueKind.Omitted),
                Value.Numeric(-1)], default));
        IReadOnlyList<int> previous = session.ImageResult.AllX;
        new PixelImage(1, 1, [123]).SaveBmp(path, default);
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CHKIMG",
            [Value.String(path), Value.Numeric(-1)], default));
        Assert.Empty(session.ImageResult.AllX);
        Assert.Empty(session.ImageResult.AllY);
        Assert.Equal(5, previous.Count);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(int.MinValue)]
    public async Task NegativeNumbersCollectAllAndMissingExtensionUsesBmp(
        int number)
    {
        using var folder = new Folder();
        string stem = System.IO.Path.Combine(folder.Path, "template");
        new PixelImage(2, 1, [1, 2]).SaveBmp(stem + ".bmp", default);
        await using IWindowsAutomationSession session =
            Automation(new CaptureFixture()).CreateSession();
        Assert.Equal(N(5), await session.InvokeAsync("CHKIMG",
            [Value.String(stem), N(0), new(ValueKind.Omitted),
                new(ValueKind.Omitted), new(ValueKind.Omitted),
                new(ValueKind.Omitted), N(number)], default));
        Assert.Equal([-2, 0, -2, 0, 1], session.ImageResult.AllX);
        Assert.Equal([4, 4, 5, 5, 6], session.ImageResult.AllY);
        File.Copy(stem + ".bmp", stem + ".dat");
        Assert.Equal(N(1), await session.InvokeAsync("CHKIMG",
            [Value.String(stem + ".dat")], default));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ReadsIndexedPaletteWithPaddingAndOrientation(bool topDown, bool fullPalette)
    {
        using var folder = new Folder();
        string path = System.IO.Path.Combine(folder.Path, "palette.bmp");
        int entries = fullPalette ? 256 : 2;
        int offset = 54 + entries * 4;
        byte[] data = new byte[offset + 8];
        data[0] = 0x42;
        data[1] = 0x4d;
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(10), offset);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(18), 3);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(22), topDown ? -2 : 2);
        data[26] = 1;
        data[28] = 8;
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(46), fullPalette ? 0 : 2);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(54), 0xff123456);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(58), 0x00abcdef);
        new byte[] { 0, 1, 0, 255, 1, 0, 1, 255 }.CopyTo(data, offset);
        File.WriteAllBytes(path, data);
        var image = PixelImage.LoadBmp(path, default);
        int[] first = [0x123456, 0xabcdef, 0x123456];
        int[] second = [0xabcdef, 0x123456, 0xabcdef];
        Assert.Equal(topDown ? [.. first, .. second] : [.. second, .. first], image.Pixels);
        Assert.Single(ImageMatcher.Match(image,
            new(3, 2, image.Pixels), 0, 1, 0, default));
        image.SaveBmp(path, default);
        Assert.Equal(image.Pixels, PixelImage.LoadBmp(path, default).Pixels);
        File.WriteAllBytes(path, data[..^1]);
        Assert.Throws<InvalidDataException>(() => PixelImage.LoadBmp(path, default));
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(46), 257);
        File.WriteAllBytes(path, data);
        Assert.Throws<InvalidDataException>(() => PixelImage.LoadBmp(path, default));
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(46), 2);
        data[offset] = 2;
        File.WriteAllBytes(path, data);
        Assert.Throws<InvalidDataException>(() => PixelImage.LoadBmp(path, default));
    }

    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
    private static extern int GdipCreateBitmapFromFile(string path, out nint image);

    [DllImport("gdiplus.dll")]
    private static extern int GdipGetImageWidth(nint image, out uint width);

    [DllImport("gdiplus.dll")]
    private static extern int GdipGetImageHeight(nint image, out uint height);

    [DllImport("gdiplus.dll")]
    private static extern int GdipBitmapGetPixel(nint image, int x, int y, out uint color);

    [Theory]
    [InlineData(1)]
    [InlineData(90)]
    [InlineData(100)]
    public async Task JpegQualityWritesDecodableImageAndReleasesNativeFile(int quality)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var folder = new Folder();
        string path = System.IO.Path.Combine(folder.Path, "日本語.dat");
        var capture = new CaptureFixture
        {
            Desktop = new(0, 0, 32, 32),
            Image = new(32, 32, [.. Enumerable.Repeat(0x123456, 1024)])
        };
        await using IWindowsAutomationSession session = Automation(capture).CreateSession();
        await session.InvokeAsync("SAVEIMG", [Value.String(path), N(0), N(0), N(0), N(32), N(32),
            N(0), N(quality)], default);
        Assert.False(File.Exists(path));
        path += ".jpg";
        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal([0xff, 0xd8, 0xff], bytes[..3]);
        Assert.Equal([0xff, 0xd9], bytes[^2..]);
        Assert.Equal(IntPtr.Size == 8 ? 24 : 16, Marshal.SizeOf<JpegWriter.StartupInput>());
        Assert.Equal(IntPtr.Size == 8 ? 40 : 32, Marshal.SizeOf<JpegWriter.EncoderParameters>());
        var input = new JpegWriter.StartupInput { Version = 1, SuppressExternalCodecs = 1 };
        JpegWriter.Check(JpegWriter.GdiplusStartup(out nuint startup, ref input, 0));
        nint image = 0;
        try
        {
            JpegWriter.Check(GdipCreateBitmapFromFile(path, out image));
            JpegWriter.Check(GdipGetImageWidth(image, out uint width));
            JpegWriter.Check(GdipGetImageHeight(image, out uint height));
            Assert.Equal(32u, width);
            Assert.Equal(32u, height);
            JpegWriter.Check(GdipBitmapGetPixel(image, 16, 16, out uint color));
            Assert.InRange(Math.Abs((int)(color >> 16 & 255) - 0x12), 0, 30);
            Assert.InRange(Math.Abs((int)(color >> 8 & 255) - 0x34), 0, 30);
            Assert.InRange(Math.Abs((int)(color & 255) - 0x56), 0, 30);
        }
        finally
        {
            if (image != 0)
            {
                JpegWriter.Check(JpegWriter.GdipDisposeImage(image));
            }

            JpegWriter.GdiplusShutdown(startup);
        }

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            JpegWriter.Save(capture.Image, path, quality, cancellation.Token));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(folder.Path));
        Assert.Throws<InvalidOperationException>(() => JpegWriter.Save(capture.Image,
            System.IO.Path.Combine(folder.Path, "missing", "file.jpg"), quality, default));
        Assert.Single(Directory.GetFiles(folder.Path));
        File.Move(path, path + ".moved");
    }

    [Fact]
    public void CompletedTemporaryFileIsRemovedWhenCancellationArrivesBeforeCommit()
    {
        using var folder = new Folder();
        string path = System.IO.Path.Combine(folder.Path, "existing.bmp");
        File.WriteAllText(path, "previous");
        using var cancellation = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => ImageFileWriter.Write(path, temporary =>
        {
            File.WriteAllText(temporary, "new");
            cancellation.Cancel();
        }, cancellation.Token));
        Assert.Equal("previous", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public void MatcherUsesRowOrderHorizontalSkipAndRightBottomBoundary()
    {
        var image = new PixelImage(3, 3, [1, 1, 1, 1, 1, 1, 1, 1, 1]);
        var template = new PixelImage(2, 2, [1, 1, 1, 1]);
        Assert.Equal([(0, 0), (0, 1)], ImageMatcher.Match(image, template, 0, -1, 0, default));
        Assert.Equal([(0, 0)], ImageMatcher.Match(image, template, 0, 1, 0, default));
        var boundary = new PixelImage(3, 3, [0, 0, 0, 0, 1, 2, 0, 3, 4]);
        Assert.Equal([(1, 1)], ImageMatcher.Match(boundary,
            new(2, 2, [1, 2, 3, 4]), 0, -1, 0, default));
        Assert.Empty(ImageMatcher.Match(template, boundary, 0, -1, 0, default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            ImageMatcher.Match(image, template, 0, -1, 0, cancellation.Token));
    }

    [Fact]
    public void MatcherRejectsMoreThanOneMillionHits()
    {
        var source = new PixelImage(1000001, 1, new int[1000001]);
        var template = new PixelImage(1, 1, [0]);
        Assert.Throws<InvalidOperationException>(() =>
            ImageMatcher.Match(source, template, 0, -1, 0, default));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    public void TransparencyUsesExactCornerColorBeforeTolerance(int corner, int index)
    {
        int[] templatePixels = [0x123456, 0x234567, 0x345678, 0x456789];
        int[] pixels = (int[])templatePixels.Clone();
        pixels[index] = 0;
        Assert.Single(ImageMatcher.Match(new(2, 2, pixels), new(2, 2, templatePixels),
            corner, -1, 0, default));
        Assert.Empty(ImageMatcher.Match(new(2, 2, pixels), new(2, 2, templatePixels),
            0, -1, 0, default));
        Assert.Single(ImageMatcher.Match(new(2, 1, [0, 0]), new(2, 1, [1, 1]),
            1, -1, 0, default));
    }

    [Theory]
    [InlineData(0x123456, 0x133456, 1, true)]
    [InlineData(0x123456, 0x143456, 1, false)]
    [InlineData(0x123456, 0x123457, 0x10000, true)]
    [InlineData(0x123456, 0x123556, 0x100, true)]
    [InlineData(0, 0xffffff, 0xffffff, true)]
    [InlineData(0x00112233, unchecked((int)0xff112233), 0, true)]
    public void ColorWidthIsComponentToleranceAndIgnoresAlpha(
        int expected, int actual, int tolerance, bool matches)
    {
        Assert.Equal(matches, ImageMatcher.Match(new(1, 1, [actual]),
            new(1, 1, [expected]), 0, 1, tolerance, default).Count == 1);
    }

    [Fact]
    public void BmpReadsTopDown32BitAndWritesPaddedBottomUp24Bit()
    {
        using var folder = new Folder();
        string path = System.IO.Path.Combine(folder.Path, "日本語.bmp");
        byte[] bmp = new byte[62];
        bmp[0] = 0x42;
        bmp[1] = 0x4d;
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), 62);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), -2);
        bmp[26] = 1;
        bmp[28] = 32;
        byte[] pixels = [0x56, 0x34, 0x12, 0xff, 0xef, 0xcd, 0xab, 0];
        pixels.CopyTo(bmp, 54);
        File.WriteAllBytes(path, bmp);
        var image = PixelImage.LoadBmp(path, default);
        Assert.Equal([0x123456, 0xabcdef], image.Pixels);
        image.SaveBmp(path, default);
        byte[] saved = File.ReadAllBytes(path);
        Assert.Equal(24, saved[28]);
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(saved.AsSpan(22)));
        Assert.Equal([0xef, 0xcd, 0xab, 0, 0x56, 0x34, 0x12, 0], saved[54..]);
        Assert.Equal(image.Pixels, PixelImage.LoadBmp(path, default).Pixels);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => image.SaveBmp(path, cancellation.Token));
        Assert.Equal(saved, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(folder.Path));
        File.WriteAllBytes(path, saved[..^1]);
        Assert.Throws<InvalidDataException>(() => PixelImage.LoadBmp(path, default));
        saved[28] = 4;
        File.WriteAllBytes(path, saved);
        Assert.Throws<InvalidDataException>(() => PixelImage.LoadBmp(path, default));
    }

    [Fact]
    public async Task ImageWorkflowPublishesSnapshotsResetsNoHitAndPreservesOnFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var folder = new Folder();
        string path = System.IO.Path.Combine(folder.Path, "pattern.bmp");
        new PixelImage(2, 1, [1, 2]).SaveBmp(path, default);
        var capture = new CaptureFixture();
        await using IWindowsAutomationSession session = Automation(capture).CreateSession();
        Assert.Equal(N(5), await session.InvokeAsync("CHKIMG",
            [Value.String(path), N(0), N(-99), N(-99), N(99), N(99), N(-1)], default));
        WindowsImageResult snapshot = session.ImageResult;
        Assert.Equal([-2, 0, -2, 0, 1], snapshot.AllX);
        Assert.Equal([4, 4, 5, 5, 6], snapshot.AllY);
        Assert.Equal((1, 6), (snapshot.X, snapshot.Y));
        Assert.Throws<NotSupportedException>(() => ((IList<int>)snapshot.AllX)[0] = 99);
        Assert.Equal(N(1), await session.InvokeAsync("CHKIMG", [Value.String(path)], default));
        Assert.Equal((-2, 4), (session.ImageResult.X, session.ImageResult.Y));
        Assert.Empty(session.ImageResult.AllX);
        Assert.Equal(N(0), await session.InvokeAsync("CHKIMG",
            [Value.String(path), N(0), N(-2), N(4), N(2), N(6), N(6)], default));
        Assert.Equal((1, 6), (session.ImageResult.X, session.ImageResult.Y));
        Assert.Empty(session.ImageResult.AllX);
        WindowsImageResult partial = session.ImageResult;
        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
            await session.InvokeAsync("CHKIMG", [Value.String(path + ".missing")], default));
        Assert.Same(partial, session.ImageResult);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await session.InvokeAsync("CHKIMG", [Value.String(path), N(0), N(99)], default));
        Assert.Same(partial, session.ImageResult);
        using var cancellation = new CancellationTokenSource();
        capture.BeforeCapture = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync("CHKIMG", [Value.String(path)], cancellation.Token));
        Assert.Same(partial, session.ImageResult);
        capture.BeforeCapture = null;
        capture.Image = new(5, 3, [.. Enumerable.Repeat(9, 15)]);
        Assert.Equal(N(0), await session.InvokeAsync("CHKIMG",
            [Value.String(path), N(0), N(-2), N(4), N(2), N(6), N(-1)], default));
        Assert.Equal(WindowsImageResult.Empty, session.ImageResult);
        Assert.Equal([-2, 0, -2, 0, 1], snapshot.AllX);
        await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await session.InvokeAsync("CHKIMG", [Value.String(path)], default));
    }

    [Theory]
    [InlineData(0, 0x563412)]
    [InlineData(3, 0x123456)]
    [InlineData(4, 0x12)]
    [InlineData(5, 0x34)]
    [InlineData(6, 0x56)]
    public async Task PeekColorConvertsNonSymmetricComponents(int mode, int expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var capture = new CaptureFixture { Image = new(1, 1, [0x123456]), Desktop = new(-2, 4, 1, 1) };
        await using IWindowsAutomationSession session = Automation(capture).CreateSession();
        Assert.Equal(N(expected), await session.InvokeAsync("PEEKCOLOR", [N(-2), N(4), N(mode)], default));
        Assert.Equal(N(-1), await session.InvokeAsync("PEEKCOLOR", [N(-3), N(4)], default));
        Assert.Single(capture.Areas);
    }

    [Fact]
    public async Task InterpreterPublishesGlobalsAndSaveImageUsesSameCapture()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var folder = new Folder();
        string path = System.IO.Path.Combine(folder.Path, "pattern.bmp");
        string saved = System.IO.Path.Combine(folder.Path, "saved.bmp");
        new PixelImage(2, 1, [1, 2]).SaveBmp(path, default);
        var capture = new CaptureFixture();
        List<string> output = [];
        var runner = new Interpreter(windows: Automation(capture));
        await runner.RunAsync($"""
            PRINT CHKIMG("{path}", , , , , , -1)
            PRINT G_IMG_X
            PRINT G_IMG_Y
            PRINT ALL_IMG_X[1]
            PRINT ALL_IMG_Y[1]
            SAVEIMG("{saved}", 0, -2, 4, 2, 1, FALSE, 0, IMG_FORE)
            PRINT CHKIMG("{saved}", 0, -2, 4, 2, 6, 2)
            PRINT G_IMG_X
            PRINT G_IMG_Y
            """, output.Add);
        Assert.Equal(["5", "1", "6", "0", "4", "1", "0", "4"], output);
        Assert.Equal([1, 2], PixelImage.LoadBmp(saved, default).Pixels);
        output.Clear();
        capture.Image = new(5, 3, [.. Enumerable.Repeat(9, 15)]);
        await runner.RunAsync($"""
            PRINT CHKIMG("{path}", , , , , , -1)
            PRINT G_IMG_X
            PRINT G_IMG_Y
            PRINT LENGTH(ALL_IMG_X)
            """, output.Add);
        Assert.Equal(["0", "-999999", "-999999", "0"], output);
    }

    [DllImport("gdi32.dll")]
    private static extern nint CreateBitmap(int width, int height,
        uint planes, uint bits, nint pixels);

    [DllImport("gdi32.dll")]
    private static extern uint SetPixel(nint dc, int x, int y, uint color);

    [Fact]
    public void NativeCaptureReadsDedicatedMemoryDcAndReleasesTemporaryObjects()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(40, Marshal.SizeOf<ImageNative.BitmapHeader>());
        nint memory = ImageNative.CreateCompatibleDC(0);
        Assert.NotEqual(0, memory);
        nint bitmap = CreateBitmap(2, 2, 1, 32, 0);
        Assert.NotEqual(0, bitmap);
        nint previous = ImageNative.SelectObject(memory, bitmap);
        Assert.NotEqual(0, previous);
        try
        {
            Assert.NotEqual(uint.MaxValue, SetPixel(memory, 0, 0, 0x563412));
            Assert.NotEqual(uint.MaxValue, SetPixel(memory, 1, 1, 0xefcdab));
            for (int i = 0; i < 20; i++)
            {
                PixelImage image = NativeImageCapture.CaptureFromDC(memory, new(0, 0, 2, 2), default);
                Assert.Equal(0x123456, image[0, 0]);
                Assert.Equal(0xabcdef, image[1, 1]);
            }

            Assert.Throws<Win32Exception>(() =>
                NativeImageCapture.CaptureFromDC(-1, new(0, 0, 2, 2), default));
            Assert.Throws<ArgumentException>(() =>
                NativeImageCapture.CaptureFromDC(memory, new(0, 0, int.MaxValue, 2), default));
        }
        finally
        {
            ImageNative.SelectObject(memory, previous);
            Assert.True(ImageNative.DeleteObject(bitmap));
            Assert.True(ImageNative.DeleteDC(memory));
        }
    }
}
