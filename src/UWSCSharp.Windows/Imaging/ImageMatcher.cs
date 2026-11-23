namespace UWSCSharp.Windows;

internal static class ImageMatcher
{
    internal static IReadOnlyList<(int X, int Y)> Match(
        PixelImage source, PixelImage template, int transparent,
        int number, int tolerance, CancellationToken token)
    {
        if (transparent == -1 && template.Width == 1 && template.Height == 1)
        {
            token.ThrowIfCancellationRequested();
            return new List<(int X, int Y)> { (0, 0) }.AsReadOnly();
        }

        List<(int X, int Y)> hits = [];
        int transparentColor = transparent switch
        {
            1 => template[0, 0],
            2 => template[template.Width - 1, 0],
            3 => template[0, template.Height - 1],
            4 => template[template.Width - 1, template.Height - 1],
            _ => -1
        };
        int blueTolerance = tolerance >> 16 & 255;
        int greenTolerance = tolerance >> 8 & 255;
        int redTolerance = tolerance & 255;
        for (int y = 0; y <= source.Height - template.Height; y++)
        {
            for (int x = 0; x <= source.Width - template.Width; x++)
            {
                token.ThrowIfCancellationRequested();
                bool matches = true;
                for (int ty = 0; ty < template.Height && matches && transparent != -1; ty++)
                {
                    token.ThrowIfCancellationRequested();
                    for (int tx = 0; tx < template.Width; tx++)
                    {
                        int expected = template[tx, ty];
                        if (expected == transparentColor)
                        {
                            continue;
                        }

                        int actual = source[x + tx, y + ty];
                        if (Math.Abs((actual & 255) - (expected & 255)) > blueTolerance
                            || Math.Abs((actual >> 8 & 255) - (expected >> 8 & 255)) > greenTolerance
                            || Math.Abs((actual >> 16 & 255) - (expected >> 16 & 255)) > redTolerance)
                        {
                            matches = false;
                            break;
                        }
                    }
                }

                if (transparent == -1)
                {
                    matches = ShapeAt(source, template, x, y, token);
                }

                if (matches)
                {
                    if (hits.Count == 1000000)
                    {
                        throw WindowsOperationErrors.Record(
                            new InvalidOperationException("画像の一致件数が上限を超えました。"));
                    }

                    hits.Add((x, y));
                    if (number > 0 && hits.Count == number)
                    {
                        return hits.AsReadOnly();
                    }

                    // 原版と同じく横の重複を飛ばし、縦は一行ずつ進めます。
                    x += template.Width - 1;
                }
            }
        }

        token.ThrowIfCancellationRequested();
        return hits.AsReadOnly();
    }
    private static bool ShapeAt(PixelImage source, PixelImage template, int x, int y,
        CancellationToken token)
    {
        if (template.Height == 1)
        {
            for (int tx = 0; tx < template.Width - 1; tx++)
            {
                token.ThrowIfCancellationRequested();
                if ((template[tx, 0] == template[tx + 1, 0])
                    != (source[x + tx, y] == source[x + tx + 1, y]))
                {
                    return false;
                }
            }

            return true;
        }

        if (template.Width == 1)
        {
            for (int ty = 0; ty < template.Height - 1; ty++)
            {
                token.ThrowIfCancellationRequested();
                if ((template[0, ty] == template[0, ty + 1])
                    != (source[x, y + ty] == source[x, y + ty + 1]))
                {
                    return false;
                }
            }

            return true;
        }

        for (int ty = 0; ty < template.Height - 1; ty++)
        {
            token.ThrowIfCancellationRequested();
            int row = ty;
            for (int tx = 0; tx < template.Width - 1; tx++)
            {
                int expected = template[tx, row];
                int actual = source[x + tx, y + row];
                if ((expected == template[tx + 1, row])
                    != (actual == source[x + tx + 1, y + row]))
                {
                    return false;
                }

                // 原版は最初の列の比較後に行pointerを次行へ切り替えます。
                row = ty + 1;
                if ((expected == template[tx, row])
                    != (actual == source[x + tx, y + row]))
                {
                    return false;
                }
            }
        }

        return true;
    }

}
