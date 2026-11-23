using System.Text;
using UWSCSharp.Core;

namespace UWSCSharp.IO;

/// <summary>
/// スクリプトをUWS05形式のファイルに変換します。
/// </summary>
public static class ScriptScramble
{
    /// <summary>
    /// 出力先の省略時は元のファイルを.orgに保管します。
    /// </summary>
    public static void ConvertFile(string sourcePath, string? outputPath = null)
    {
        string source = Path.GetFullPath(sourcePath);
        string destination = outputPath is null ? source : Path.GetFullPath(outputPath);
        byte[] original = File.ReadAllBytes(source);
        if (ScrambledScript.HasSignature(original))
        {
            throw new IOException("入力ファイルは既にUWS05形式です。");
        }

        string text = DecodeInput(source, original);
        byte[] converted = ScrambledScript.EncodeVerified(
            text, Random.Shared.Next(ScrambledScript.SeedCount));

        Publish(destination, converted, temporary =>
        {
            if (outputPath is not null)
            {
                MoveToNewFile(temporary, destination);
                return;
            }

            string backup = Path.ChangeExtension(source, ".org");
            Publish(backup, original, backupTemporary =>
                MoveToNewFile(backupTemporary, backup));

            // 置換に失敗した場合も、確定済みの退避ファイルは保ちます。
            try
            {
                File.Replace(temporary, source, null);
            }
            catch (IOException error)
            {
                throw new IOException(ReplacementMessage(error), error);
            }
            catch (UnauthorizedAccessException error)
            {
                throw new UnauthorizedAccessException(
                    ReplacementMessage(error), error);
            }

            string ReplacementMessage(Exception error) =>
                $"入力ファイルを置き換えられません。対象: {source}"
                + $"{Environment.NewLine}退避先: {backup}"
                + $"{Environment.NewLine}{error.Message}";
        });
    }

    private static void MoveToNewFile(string temporary, string destination)
    {
        try
        {
            File.Move(temporary, destination);
        }
        catch (IOException error)
        {
            throw new IOException(SaveMessage(destination, error), error);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new UnauthorizedAccessException(
                SaveMessage(destination, error), error);
        }
    }

    private static string DecodeInput(string source, byte[] original)
    {
        bool littleEndian = original.AsSpan().StartsWith(
            (ReadOnlySpan<byte>)[0xff, 0xfe]);
        bool bigEndian = original.AsSpan().StartsWith(
            (ReadOnlySpan<byte>)[0xfe, 0xff]);
        try
        {
            return littleEndian || bigEndian
                ? new UnicodeEncoding(bigEndian, true, true)
                    .GetString(original, 2, original.Length - 2)
                : Source.DecodeText(original);
        }
        catch (DecoderFallbackException error)
        {
            string encoding = bigEndian ? "UTF-16BE" : littleEndian ? "UTF-16LE" : "文字コード";
            throw new IOException(
                $"入力ファイルの{encoding}が不正です。対象: {source}", error);
        }
    }

    private static void Publish(string destination, byte[] bytes, Action<string> publish)
    {
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!,
            ".uwsc-scramble-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool owned = false;
        try
        {
            using (FileStream stream = OpenTemporary(temporary, destination))
            {
                owned = true;
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            publish(temporary);
        }
        catch (Exception failure)
        {
            if (owned)
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception cleanup)
                {
                    throw new AggregateException(
                        "UWS05変換後の一時ファイル削除に失敗しました。",
                        failure, cleanup);
                }
            }

            throw;
        }
    }

    private static FileStream OpenTemporary(string temporary, string destination)
    {
        try
        {
            return new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None);
        }
        catch (IOException error)
        {
            throw new IOException(SaveMessage(destination, error), error);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new UnauthorizedAccessException(
                SaveMessage(destination, error), error);
        }
    }

    private static string SaveMessage(string destination, Exception error) =>
        $"ファイルを保存できません。保存先: {destination}"
        + $"{Environment.NewLine}{error.Message}";
}
