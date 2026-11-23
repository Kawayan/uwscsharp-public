namespace UWSCSharp.Windows;

internal static class ImageFileWriter
{
    internal static void Write(string path, Action<string> write, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string fullPath = WindowsOperationErrors.FileAccess(() => Path.GetFullPath(path));
        string temporary = Path.Combine(Path.GetDirectoryName(fullPath)!,
            $".uwsc-image-{Guid.NewGuid():N}.tmp");
        try
        {
            write(temporary);
            token.ThrowIfCancellationRequested();
            WindowsOperationErrors.FileAccess(() => File.Move(temporary, fullPath, true));
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
