using System.IO;

namespace UWSCSharp.Wpf;

internal static class AtomicFileWriter
{
    internal enum CommitMode
    {
        Move,
        Replace,
        ReplaceIfPresent,
        Overwrite
    }

    internal static void Write(string temporary, string destination,
        Action<FileStream> write, CommitMode mode, string cleanupErrorKey,
        string? backup = null, Action<string, string?>? beforeCommit = null)
    {
        Exception? primary = null;
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
            {
                write(file);
                file.Flush(flushToDisk: true);
            }

            beforeCommit?.Invoke(temporary, backup);
            if (mode == CommitMode.Replace
                || (mode == CommitMode.ReplaceIfPresent && File.Exists(destination)))
            {
                File.Replace(temporary, destination, backup);
            }
            else if (mode == CommitMode.Overwrite)
            {
                File.Move(temporary, destination, overwrite: true);
            }
            else
            {
                File.Move(temporary, destination);
            }
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (Exception cleanup) when (primary is not null)
            {
                primary.Data[cleanupErrorKey] = cleanup;
            }
        }
    }
}
