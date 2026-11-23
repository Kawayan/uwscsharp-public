using System.Globalization;
using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

/// <summary>
/// 代替書体を含む本文を同じ経路で計測・描画します。
/// </summary>
internal sealed partial class BalloonTextLayout(string[] lines, int width, int lineHeight)
{
    internal int Width => width;

    internal int LineHeight => lineHeight;

    internal int Height => checked(lines.Length * lineHeight);

    internal static BalloonTextLayout Create(nint dc, string text,
        int maximumWidth, CancellationToken token)
    {
        using var renderer = new Renderer(dc);
        List<string> lines = [];
        int width = 0;
        int height = renderer.Measure(" ").Height;
        foreach (string raw in text.Replace("\r\n", "\n")
            .Replace('\r', '\n').Split('\n'))
        {
            token.ThrowIfCancellationRequested();
            string paragraph = raw.Replace("\t", "        ");
            int[] boundaries = [.. StringInfo.ParseCombiningCharacters(paragraph), paragraph.Length];
            if (paragraph.Length == 0)
            {
                lines.Add("");
            }

            int startElement = 0;
            while (startElement < boundaries.Length - 1)
            {
                token.ThrowIfCancellationRequested();
                int start = boundaries[startElement];
                int chunkEnd = Math.Min(start + 4096, paragraph.Length);
                int high = Array.BinarySearch(boundaries, chunkEnd);
                high = high >= 0 ? high : ~high - 1;
                high = Math.Max(startElement + 1, high);
                int low = startElement + 1;
                int fit = startElement;
                while (low <= high)
                {
                    token.ThrowIfCancellationRequested();
                    int middle = low + (high - low) / 2;
                    (int Width, int Height) size = renderer.Measure(
                        paragraph[start..boundaries[middle]]);
                    if (size.Width <= maximumWidth)
                    {
                        fit = middle;
                        low = middle + 1;
                    }
                    else
                    {
                        high = middle - 1;
                    }
                }

                if (fit == startElement)
                {
                    throw new InvalidOperationException(
                        "指定書体の文字要素を吹出しの作業領域に収められません。");
                }

                int end = boundaries[fit];
                if (end < paragraph.Length)
                {
                    int space = paragraph.LastIndexOf(' ', end - 1, end - start);
                    int boundary = Array.BinarySearch(boundaries, space + 1);
                    if (space > start && boundary > startElement)
                    {
                        fit = boundary;
                        end = boundaries[fit];
                    }
                }

                string line = paragraph[start..end];
                (int Width, int Height) measured = renderer.Measure(line);
                width = Math.Max(width, measured.Width);
                height = Math.Max(height, measured.Height);
                lines.Add(line);
                startElement = fit;
            }
        }

        if ((long)lines.Count * height > int.MaxValue)
        {
            throw new InvalidOperationException("吹出しの本文が長すぎます。");
        }

        return new([.. lines], width, height);
    }

    internal void Draw(nint dc, int x, int y, int scroll,
        int visibleWidth, int visibleHeight, int color)
    {
        using var renderer = new Renderer(dc);
        renderer.SetClip(x, y, visibleWidth, visibleHeight);
        int first = scroll / lineHeight;
        int end = (int)Math.Min(lines.Length,
            ((long)scroll + visibleHeight + lineHeight - 1) / lineHeight);
        for (int index = first; index < end; index++)
        {
            if (lines[index].Length == 0)
            {
                continue;
            }

            int top = checked((int)((long)y + (long)index * lineHeight - scroll));
            renderer.Draw(lines[index], x, top, width + 1, lineHeight + 1, color);
        }
    }

    /// <summary>
    /// 実行内でGDI+を初期化し、全画面の解放後に終了します。
    /// </summary>
    internal sealed class Runtime : IDisposable
    {
        private nuint startup;

        internal Runtime()
        {
            var input = new JpegWriter.StartupInput
            {
                Version = 1,
                SuppressExternalCodecs = 1
            };
            Check(JpegWriter.GdiplusStartup(out startup, ref input, 0));
        }

        /// <summary>
        /// 文字描画用の初期化を終了します。
        /// </summary>
        public void Dispose()
        {
            if (startup != 0)
            {
                JpegWriter.GdiplusShutdown(startup);
                startup = 0;
            }
        }
    }

    private sealed class Renderer : IDisposable
    {
        private nint graphics;
        private nint font;
        private nint format;
        private nint brush;

        internal Renderer(nint dc)
        {
            try
            {
                Check(GdipCreateFromHDC(dc, out graphics));
                Check(GdipSetPageUnit(graphics, 2));
                Check(GdipSetTextRenderingHint(graphics, 1));
                Check(GdipCreateFontFromDC(dc, out font));
                // 代替書体を無効にせず、改行と末尾空白は自前の配置に合わせます。
                Check(GdipCreateStringFormat(0x1800, 0, out format));
                Check(GdipCreateSolidFill(0xff000000, out brush));
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal (int Width, int Height) Measure(string text)
        {
            var bounds = new RectangleF { Width = 10000000, Height = 10000000 };
            Check(GdipMeasureString(graphics, text, text.Length,
                font, ref bounds, format, out RectangleF measured, out _, out _));
            return (checked((int)Math.Ceiling(measured.Width)),
                Math.Max(1, checked((int)Math.Ceiling(measured.Height))));
        }

        internal void SetClip(int x, int y, int width, int height) =>
            Check(GdipSetClipRect(graphics, x, y, width, height, 1));

        internal void Draw(string text, int x, int y,
            int width, int height, int color)
        {
            uint bgr = (uint)color;
            uint argb = 0xff000000 | ((bgr & 0xff) << 16)
                | (bgr & 0xff00) | ((bgr >> 16) & 0xff);
            Check(GdipSetSolidFillColor(brush, argb));
            var bounds = new RectangleF
            {
                X = x,
                Y = y,
                Width = width,
                Height = height
            };
            Check(GdipDrawString(graphics, text, text.Length,
                font, ref bounds, format, brush));
        }

        /// <summary>
        /// 呼出し中に作成した描画資源を解放します。
        /// </summary>
        public void Dispose()
        {
            if (brush != 0)
            {
                GdipDeleteBrush(brush);
                brush = 0;
            }

            if (format != 0)
            {
                GdipDeleteStringFormat(format);
                format = 0;
            }

            if (font != 0)
            {
                GdipDeleteFont(font);
                font = 0;
            }

            if (graphics != 0)
            {
                GdipDeleteGraphics(graphics);
                graphics = 0;
            }
        }
    }

    private static void Check(int status)
    {
        if (status != 0)
        {
            throw WindowsOperationErrors.Record(new InvalidOperationException(
                $"吹出しの文字計測・描画に失敗しました（GDI+ status={status}）。"));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectangleF
    {
        public float X;
        public float Y;
        public float Width;
        public float Height;
    }
}
