using System.Security.Cryptography;
using System.Text;

namespace UWSCSharp.Wpf;

/// <summary>
/// 同梱したヘルプを利用者のキャッシュへ展開します。
/// </summary>
internal sealed class LocalHelp
{
    private static readonly TimeSpan CacheMutexTimeout = TimeSpan.FromSeconds(5);

    private const string ResourcePrefix = "UWSCSharp.Help/";
    private readonly Dictionary<string, byte[]> files =
        new(StringComparer.Ordinal);
    private readonly string directory;
    private readonly Action<Uri> open;

    internal LocalHelp(string cacheRoot, Action<Uri> open,
        IReadOnlyDictionary<string, byte[]>? resources = null)
    {
        if (!Path.IsPathFullyQualified(cacheRoot))
        {
            throw new ArgumentException(
                "ヘルプの保存先には絶対パスを指定してください。",
                nameof(cacheRoot));
        }

        this.open = open;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach ((string name, byte[] content) in
            (resources ?? EmbeddedFiles()).OrderBy(
                pair => pair.Key, StringComparer.Ordinal))
        {
            if (!IsDocument(name))
            {
                throw new InvalidDataException(
                    $"ヘルプの収録名が不正です: {name}");
            }

            byte[] snapshot = [.. content];
            files.Add(name, snapshot);
            hash.AppendData(Encoding.UTF8.GetBytes(name));
            hash.AppendData([0]);
            hash.AppendData(SHA256.HashData(snapshot));
        }

        string[] requiredFiles =
        [
            "index.html", "assets/help.css", "assets/help.js",
            "assets/search-index.js"
        ];
        foreach (string required in requiredFiles)
        {
            if (!files.ContainsKey(required))
            {
                throw new InvalidDataException(
                    $"同梱ヘルプに必要なファイルがありません: {required}");
            }
        }

        directory = Path.Combine(cacheRoot,
            Convert.ToHexString(hash.GetHashAndReset()));
    }

    internal void Open() => open(Materialize());

    internal Uri Materialize()
    {
        string lockName = "UWSCSharp.Help." + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(directory.ToUpperInvariant())));
        using var mutex = new Mutex(false, lockName);
        bool acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(CacheMutexTimeout);
            }
            catch (AbandonedMutexException)
            {
                // 前の展開が終了しなかった場合も、全ファイルを再検査します。
                acquired = true;
            }

            if (!acquired)
            {
                throw new IOException(
                    "ヘルプの展開を待機できませんでした。もう一度開いてください。");
            }

            return MaterializeFiles();
        }
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private Uri MaterializeFiles()
    {
        Directory.CreateDirectory(directory);
        foreach ((string name, byte[] content) in files)
        {
            string destination = Path.Combine(directory, name);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (MatchesFile(destination, content))
            {
                continue;
            }

            WriteCompleteFile(destination, content);
        }

        return new Uri(Path.Combine(directory, "index.html"));
    }

    internal static IReadOnlyDictionary<string, byte[]> EmbeddedFiles()
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        System.Reflection.Assembly assembly = typeof(LocalHelp).Assembly;
        foreach (string name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            using Stream source = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidDataException("同梱ヘルプを読み込めません。");
            using var content = new MemoryStream();
            source.CopyTo(content);
            result.Add(name[ResourcePrefix.Length..], content.ToArray());
        }

        return result;
    }

    private static bool IsDocument(string name)
    {
        if (name is "index.html" or "assets/help.css" or "assets/help.js"
            or "assets/search-index.js")
        {
            return true;
        }

        if (!name.StartsWith("pages/", StringComparison.Ordinal))
        {
            return false;
        }

        name = name["pages/".Length..];
        return name.EndsWith(".html", StringComparison.Ordinal)
            && name.Length > 5 && name[0] is >= 'a' and <= 'z'
            && name[..^5].All(character =>
                character is >= 'a' and <= 'z'
                    or >= '0' and <= '9' or '-');
    }

    private static void WriteCompleteFile(string destination, byte[] content)
    {
        string temporary = destination + "."
            + Guid.NewGuid().ToString("N") + ".tmp";
        Exception? primary = null;
        try
        {
            using (var stream = new FileStream(temporary,
                FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        catch (Exception failure)
        {
            primary = failure;
            throw;
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception cleanup) when (primary is not null)
            {
                primary.Data["UWSCSharp.HelpCleanupError"] = cleanup;
            }
        }
    }

    private static bool MatchesFile(string path, byte[] expected)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        // 別の起動による完成ファイルの置換を妨げません。
        using var stream = new FileStream(path, FileMode.Open,
            FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length != expected.Length)
        {
            return false;
        }

        byte[] actual = new byte[expected.Length];
        stream.ReadExactly(actual);
        return actual.AsSpan().SequenceEqual(expected);
    }
}
