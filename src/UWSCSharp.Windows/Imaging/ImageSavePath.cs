namespace UWSCSharp.Windows;

/// <summary>
/// 保存する画像形式に合わせて拡張子を補います。
/// </summary>
internal static class ImageSavePath
{
    internal static string Resolve(string path, bool jpeg) =>
        WindowsOperationErrors.FileAccess(() =>
        {
            string normalized = path.TrimEnd('.');
            if (Path.GetFileName(normalized).Length == 0)
            {
                throw WindowsOperationErrors.Record(
                    new ArgumentException("画像の保存ファイル名がありません。"));
            }

            string extension = Path.GetExtension(normalized);
            bool matches = jpeg
                ? extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".jpe", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                : extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase);
            return matches ? normalized
                : normalized + (jpeg ? ".jpg" : ".bmp");
        });
}
