using System.Text;
using UWSCSharp.Core;

namespace UWSCSharp.IO;

public sealed partial class ScriptFiles
{
    private static Encoding IniEncoding(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }))
        {
            return new UnicodeEncoding(false, true, true);
        }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
        {
            return new UnicodeEncoding(true, true, true);
        }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
        {
            return new UTF8Encoding(true, true);
        }
        else
        {
            try
            {
                _ = new UTF8Encoding(false, true).GetString(bytes);
                return new UTF8Encoding(false, true);
            }
            catch (DecoderFallbackException)
            {
                return ScriptEncodings.Ansi;
            }
        }
    }

    private sealed record DirectoryEntry(string Name, long Order);

    private static IReadOnlyList<string> ListDirectory(string path,
        string pattern, bool includeHidden, int order,
        CancellationToken token, DirectoryEnumerator enumerateDirectory,
        string? baseDirectory = null)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (baseDirectory is not null)
            {
                path = Path.GetFullPath(path.Length == 0 ? "." : path,
                    baseDirectory);
            }

            if (pattern.Length == 0)
            {
                return Array.AsReadOnly(Array.Empty<string>());
            }

            bool directories = pattern == "\\";
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                AttributesToSkip = 0,
                MatchCasing = MatchCasing.CaseInsensitive,
                MatchType = MatchType.Win32,
                IgnoreInaccessible = false
            };
            var entries = new List<DirectoryEntry>();
            foreach (FileSystemInfo entry in enumerateDirectory(
                path, directories ? "*" : pattern, options))
            {
                token.ThrowIfCancellationRequested();
                bool isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
                bool hidden = (entry.Attributes & FileAttributes.Hidden) != 0;
                if (isDirectory == directories && (includeHidden || !hidden))
                {
                    if (entries.Count == UwscArray.MaximumElements)
                    {
                        throw new InvalidOperationException(
                            "GETDIRの取得数が配列の上限を超えました。");
                    }

                    long metric = order switch
                    {
                        1 => entry is FileInfo file ? file.Length : 0,
                        2 => entry.CreationTimeUtc.Ticks,
                        3 => entry.LastWriteTimeUtc.Ticks,
                        4 => entry.LastAccessTimeUtc.Ticks,
                        _ => 0
                    };
                    entries.Add(new DirectoryEntry(entry.Name, metric));
                }
            }

            try
            {
                entries.Sort((left, right) =>
                {
                    token.ThrowIfCancellationRequested();
                    int comparison = left.Order.CompareTo(right.Order);
                    return comparison != 0 ? comparison : CompareNames(left.Name, right.Name);
                });
            }
            catch (InvalidOperationException error) when (
                error.InnerException is OperationCanceledException && token.IsCancellationRequested)
            {
                // .NETの並べ替えが包んだ取消を、通常の取消として伝えます。
                token.ThrowIfCancellationRequested();
                throw;
            }

            string[] names = new string[entries.Count];
            for (int i = 0; i < names.Length; i++)
            {
                if ((i & 255) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }

                names[i] = entries[i].Name;
            }

            token.ThrowIfCancellationRequested();
            return Array.AsReadOnly(names);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException or ArgumentException)
        {
            token.ThrowIfCancellationRequested();
            return [];
        }
    }

    private static int CompareNames(string left, string right)
    {
        int a = 0;
        int b = 0;
        while (a < left.Length && b < right.Length)
        {
            if (char.IsAsciiDigit(left[a]) && char.IsAsciiDigit(right[b]))
            {
                int endA = a;
                int endB = b;
                while (endA < left.Length && char.IsAsciiDigit(left[endA]))
                {
                    endA++;
                }

                while (endB < right.Length && char.IsAsciiDigit(right[endB]))
                {
                    endB++;
                }

                while (a < endA && left[a] == '0')
                {
                    a++;
                }

                while (b < endB && right[b] == '0')
                {
                    b++;
                }

                int result = (endA - a).CompareTo(endB - b);
                if (result == 0)
                {
                    result = left.AsSpan(a, endA - a)
                        .SequenceCompareTo(right.AsSpan(b, endB - b));
                }

                if (result != 0)
                {
                    return result;
                }

                a = endA;
                b = endB;
            }
            else
            {
                int result = char.ToUpperInvariant(left[a])
                    .CompareTo(char.ToUpperInvariant(right[b]));
                if (result != 0)
                {
                    return result;
                }

                a++;
                b++;
            }
        }

        int lengthResult = (left.Length - a).CompareTo(right.Length - b);
        return lengthResult != 0 ? lengthResult
            : StringComparer.Ordinal.Compare(left, right);
    }

    private static bool DeleteFiles(string path, CancellationToken token,
        Action<string> deleteFile)
    {
        bool attempted = false;
        bool failed = false;
        try
        {
            token.ThrowIfCancellationRequested();
            string directory = Path.GetDirectoryName(path)!;
            string pattern = Path.GetFileName(path);
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                AttributesToSkip = 0,
                MatchCasing = MatchCasing.CaseInsensitive,
                MatchType = MatchType.Win32,
                IgnoreInaccessible = false
            };
            foreach (string file in Directory.EnumerateFiles(
                directory, pattern, options))
            {
                token.ThrowIfCancellationRequested();
                attempted = true;
                try
                {
                    deleteFile(file);
                }
                catch (Exception ex) when (ex is IOException
                    or UnauthorizedAccessException)
                {
                    // 一件の失敗では残りの削除を中断しません。
                    failed = true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException or ArgumentException)
        {
            token.ThrowIfCancellationRequested();
            return false;
        }

        token.ThrowIfCancellationRequested();
        return attempted && !failed;
    }
}
