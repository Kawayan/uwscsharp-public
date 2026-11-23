using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using UWSCSharp.Core;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

public sealed class BalloonFontFallbackTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("Arial")]
    [InlineData("Meiryo")]
    [InlineData("Tahoma")]
    public async Task JapaneseCharactersHaveDistinctVisibleGlyphs(string family)
    {
        ObservePrimaryFont(family);
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        PixelImage first = await ShowAndCapture(session, family, "日");
        PixelImage second = await ShowAndCapture(session, family, "本");
        PixelImage third = await ShowAndCapture(session, family, "語");
        SaveImage("FIRST", first);
        SaveImage("SECOND", second);
        SaveImage("THIRD", third);
        Assert.False(Same(first, second) && Same(first, third),
            "日・本・語が同じ欠字画像になっています。");
    }

    private static bool Same(PixelImage first, PixelImage second) =>
        first.Width == second.Width && first.Height == second.Height
            && first.Pixels.SequenceEqual(second.Pixels);

    [Theory]
    [InlineData(0x0000FF)]
    [InlineData(0xFF0000)]
    public async Task TextColorPreservesBgrForFallbackGlyphs(int color)
    {
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        PixelImage image = await ShowAndCapture(session, "Arial", "日本語", color);
        int rgb = ((color & 0xff) << 16) | (color & 0xff00) | ((color >> 16) & 0xff);
        SaveImage("COLOR", image);
        Assert.Contains(Enumerable.Range(5, image.Height - 10)
            .SelectMany(y => Enumerable.Range(5, image.Width - 10)
                .Select(x => image[x, y])), pixel => pixel == rgb);
    }

    private void ObservePrimaryFont(string family)
    {
        nint dc = CreateCompatibleDC(0);
        Assert.NotEqual(0, dc);
        nint font = CreateFontW(-32, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 3, 0, family);
        Assert.NotEqual(0, font);
        nint previous = SelectObject(dc, font);
        try
        {
            var face = new StringBuilder(128);
            Assert.NotEqual(0, GetTextFaceW(dc, face.Capacity, face));
            ushort[] glyphs = new ushort[3];
            Assert.Equal(3u, GetGlyphIndicesW(dc, "日本語", 3, glyphs, 1));
            output.WriteLine(JsonSerializer.Serialize(new
            {
                family,
                face = face.ToString(),
                glyphs
            }));
        }
        finally
        {
            SelectObject(dc, previous);
            DeleteObject(font);
            DeleteDC(dc);
        }
    }

    private static async Task<PixelImage> ShowAndCapture(IWindowsAutomationSession session,
        string family, string text, int color = 0)
    {
        await session.InvokeAsync(BuiltinFunctionNames.Balloon,
            [Value.String(text), Value.Numeric(80), Value.Numeric(90), Value.Numeric(0),
                Value.Numeric(16), Value.String(family), Value.Numeric(color), Value.Numeric(0xFFFFFF)],
            CancellationToken.None);
        Value id = await session.InvokeAsync(BuiltinFunctionNames.GetId,
            [Value.String(BuiltinWindowSelectors.GetFukidasiWin)], CancellationToken.None);
        Value native = await session.InvokeAsync(BuiltinFunctionNames.IdToHnd,
            [id], CancellationToken.None);
        nint window = (nint)(long)native.AsNumber();
        Assert.NotEqual(0, window);
        await Task.Delay(150);
        using var dpi = new PhysicalDpiScope();
        Assert.True(Native.GetWindowRect(window, out Native.Rect bounds));
        return new NativeImageCapture().Capture(new(bounds.Left, bounds.Top,
            bounds.Right - bounds.Left, bounds.Bottom - bounds.Top), CancellationToken.None);
    }

    private void SaveImage(string label, PixelImage image)
    {
        string file = Path.Combine(Path.GetTempPath(), "uwsc-font-image-" + Guid.NewGuid().ToString("N") + ".bmp");
        try
        {
            image.SaveBmp(file, CancellationToken.None);
            output.WriteLine("FONT_PRODUCT_" + label + " " + Convert.ToBase64String(File.ReadAllBytes(file)));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateFontW(int height, int width, int escape, int orientation,
        int weight, uint italic, uint underline, uint strike, uint charset, uint precision,
        uint clip, uint quality, uint pitch, string face);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetTextFaceW(nint dc, int count, StringBuilder name);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetGlyphIndicesW(nint dc, string text, int count,
        [Out] ushort[] glyphs, uint flags);
}
