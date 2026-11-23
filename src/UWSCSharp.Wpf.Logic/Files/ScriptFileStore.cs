using System.IO;
using System.Text;

namespace UWSCSharp.Wpf;

internal static class ScriptFileStore
{
    internal static void Save(string path, string source)
    {
        string destination = Path.GetFullPath(path);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // 全文の書込みが成功するまで既存ファイルへ触れません。
            using (var stream = new FileStream(temporary,
                FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = new StreamWriter(stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: true,
                        throwOnInvalidBytes: true), leaveOpen: true))
                {
                    writer.Write(source);
                }

                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(destination))
            {
                File.Replace(temporary, destination, null);
            }
            else
            {
                File.Move(temporary, destination);
            }
        }
        catch (EncoderFallbackException error)
        {
            throw new IOException(
                $"UTF-8 へ変換できない文字があります。保存先: {destination}",
                error);
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
