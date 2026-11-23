using System.Text;

namespace UWSCSharp.Windows;

/// <summary>
/// 展開済みの登録語を文法へ変換し、読込みが終わるまで一時ファイルを所有します。
/// </summary>
internal static class SpeechRecognitionGrammarFile
{
    internal static string Render(IEnumerable<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        var text = new StringBuilder(
            "<GRAMMAR>\r\n<RULE NAME=\"request\" TOPLEVEL=\"ACTIVE\">\r\n"
            + "<L PROPNAME=\"participants\">\r\n");
        foreach (string word in words)
        {
            ArgumentNullException.ThrowIfNull(word);
            // 空判定だけで全角空白と制御文字を除き、残す語自体は加工しません。
            if (word.Any(character => character > '\u0020' && character != '\u3000'))
            {
                text.Append("<P>").Append(word).Append("</P>\r\n");
            }
        }

        return text.Append("</L>\r\n</RULE>\r\n</GRAMMAR>\r\n").ToString();
    }

    /// <summary>
    /// 文法を保存して読込み処理へ渡し、成功・失敗・取消のいずれでも回収します。
    /// </summary>
    internal static async Task UseAsync(IEnumerable<string> words,
        Func<string, CancellationToken, Task> load,
        CancellationToken token = default, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(load);
        token.ThrowIfCancellationRequested();
        string text = Render(words);
        string path = Path.Combine(directory ?? Path.GetTempPath(),
            "UWSC-recognition-" + Guid.NewGuid().ToString("N") + ".xml");
        bool created = false;
        Exception? primary = null;
        try
        {
            await using (var stream = new FileStream(path, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                created = true;
                await using var writer = new StreamWriter(stream,
                    new UTF8Encoding(true), leaveOpen: true);
                await writer.WriteAsync(text.AsMemory(), token).ConfigureAwait(false);
                await writer.FlushAsync(token).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();
            await load(path, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        catch (Exception failure)
        {
            primary = failure;
            throw;
        }
        finally
        {
            if (created)
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception cleanup) when (primary is not null)
                {
                    throw new AggregateException(primary, cleanup);
                }
            }
        }
    }
}
