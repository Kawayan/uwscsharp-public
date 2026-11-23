namespace UWSCSharp.Windows;

internal static class NativeTreePath
{
    internal static string[] Split(string desired)
    {
        const string marker = "\u0001\uffe5";
        string remaining = desired.Replace("\\\\", marker, StringComparison.Ordinal);
        List<string> segments = [];
        while (true)
        {
            remaining = NativeListControl.Trim(remaining);
            if (remaining.Length == 0)
            {
                break;
            }

            int delimiter = remaining.IndexOf('\\');
            if (delimiter == 0)
            {
                remaining = remaining[1..];
                continue;
            }

            // 原版の引数32は最大件数ではなく、許可する最大添字です。
            if (segments.Count > 32)
            {
                throw WindowsOperationErrors.Record(
                    new InvalidOperationException("TREEVIEWのパス要素は33個までです。"));
            }

            string segment = delimiter < 0 ? remaining : remaining[..delimiter];
            segments.Add(segment.Replace(marker, "\\", StringComparison.Ordinal));
            remaining = delimiter < 0 ? "" : remaining[(delimiter + 1)..];
        }

        return [.. segments];
    }
}
