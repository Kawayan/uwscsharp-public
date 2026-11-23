using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using UWSCSharp.Core;

namespace UWSCSharp.IO;

public sealed partial class ScriptFiles
{
    // 同じプロセス内のINI更新を通常パスごとに直列化し、ロック表の増大を防ぎます。
    private static readonly SemaphoreSlim[] IniMutationGates =
        [.. Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1))];

    private sealed record IniMutationLine(string Text, string Ending);

    private static ValueTask<Value> UpdateIniAsync(
        string path, string section, string key, string? value,
        CancellationToken cancellationToken) =>
        UpdateIniAsync(path, [(section, key, value)], cancellationToken);

    internal static async ValueTask UpdateIniValuesAsync(
        string path, string section,
        IReadOnlyList<(string Key, string Value)> values,
        CancellationToken cancellationToken)
    {
        _ = await UpdateIniAsync(path,
            [.. values.Select(value => (section, value.Key, (string?)value.Value))],
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<Value> UpdateIniAsync(
        string path,
        IReadOnlyList<(string Section, string Key, string? Value)> changes,
        CancellationToken cancellationToken)
    {
        uint hash = unchecked((uint)StringComparer.OrdinalIgnoreCase.GetHashCode(path));
        SemaphoreSlim gate = IniMutationGates[hash % (uint)IniMutationGates.Length];
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Mutex の取得と解放を同じワーカースレッドで行います。
            return await Task.Run(() => UpdateIniWithMutex(path, changes,
                cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static Value UpdateIniWithMutex(string path,
        IReadOnlyList<(string Section, string Key, string? Value)> changes,
        CancellationToken cancellationToken)
    {
        string mutexPath = ResolveIniMutexPath(path);
        byte[] key = Encoding.UTF8.GetBytes(mutexPath.ToUpperInvariant());
        string name = "UWSCSharp.Ini." + Convert.ToHexString(SHA256.HashData(key));
        using var mutex = new Mutex(false, name, new NamedWaitHandleOptions
        {
            CurrentUserOnly = true,
            CurrentSessionOnly = false
        });
        try
        {
            if (WaitHandle.WaitAny([mutex, cancellationToken.WaitHandle]) != 0)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
        catch (AbandonedMutexException)
        {
            // 終了したプロセスが残した Mutex を取得しました。
            // 確定済みの INI を読み直してから更新します。
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return UpdateIniDocumentAsync(path, changes, cancellationToken)
                .AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    // INI本体が未作成でも、同じフォルダーの別名から同じ排他名を得ます。
    private static string ResolveIniMutexPath(string path)
    {
        if (!OperatingSystem.IsWindows()
            || Path.GetDirectoryName(path) is not { Length: > 0 } directory)
        {
            return path;
        }

        const uint openExisting = 3;
        const uint backupSemantics = 0x02000000;
        using SafeFileHandle handle = CreateFileW(directory, 0,
            FileShare.ReadWrite | FileShare.Delete, 0, openExisting,
            backupSemantics, 0);
        if (handle.IsInvalid)
        {
            return path;
        }

        var buffer = new StringBuilder(260);
        uint length = GetFinalPathNameByHandleW(handle, buffer,
            (uint)buffer.Capacity, 0);
        if (length >= buffer.Capacity && length <= 32768)
        {
            buffer.EnsureCapacity((int)length);
            length = GetFinalPathNameByHandleW(handle, buffer,
                (uint)buffer.Capacity, 0);
        }

        if (length == 0 || length >= buffer.Capacity)
        {
            return path;
        }

        string resolved = buffer.ToString();
        const string extendedPrefix = @"\\?\";
        const string uncPrefix = @"\\?\UNC\";
        if (resolved.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            resolved = @"\\" + resolved[uncPrefix.Length..];
        }
        else if (resolved.StartsWith(extendedPrefix, StringComparison.Ordinal)
            && Path.IsPathFullyQualified(resolved[extendedPrefix.Length..]))
        {
            resolved = resolved[extendedPrefix.Length..];
        }

        return Path.Combine(resolved, Path.GetFileName(path));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access,
        FileShare share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file,
        StringBuilder path, uint capacity, uint flags);

    private static async ValueTask<Value> UpdateIniDocumentAsync(
        string path,
        IReadOnlyList<(string Section, string Key, string? Value)> changes,
        CancellationToken cancellationToken)
    {
        byte[] bytes;
        bool exists = true;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            exists = false;
            bytes = [];
        }

        Encoding encoding = exists ? IniEncoding(bytes) : ScriptEncodings.Ansi;
        List<IniMutationLine> lines = ParseIniMutationLines(ScriptFileText.Decode(bytes), cancellationToken);
        string ending = lines.FirstOrDefault(line => line.Ending.Length > 0)?.Ending ?? "\r\n";
        bool changed = false;
        foreach ((string section, string key, string? value) in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (int start, int end) = FindIniMutationSection(lines, section, cancellationToken);
            if (value is null)
            {
                int index = start < 0 || key.Length == 0 ? -1
                    : FindIniMutationKey(lines, start, end, key, cancellationToken);
                if (start < 0 || (key.Length > 0 && index < 0))
                {
                    continue;
                }

                if (key.Length == 0)
                {
                    lines.RemoveRange(start, end - start);
                }
                else
                {
                    lines.RemoveAt(index);
                }
            }
            else if (start < 0)
            {
                TerminateIniMutationLine(lines, lines.Count - 1, ending);
                lines.Add(new IniMutationLine("[" + section + "]", ending));
                lines.Add(new IniMutationLine(key + "=" + value, ending));
            }
            else
            {
                int index = FindIniMutationKey(lines, start, end, key, cancellationToken);
                if (index >= 0)
                {
                    lines[index] = lines[index] with
                    {
                        Text = key + "=" + value
                    };
                }
                else
                {
                    TerminateIniMutationLine(lines, end - 1, ending);
                    lines.Insert(end, new IniMutationLine(key + "=" + value, ending));
                }
            }

            changed = true;
        }

        if (!changed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Value.Empty;
        }

        var text = new StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            if ((i & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            text.Append(lines[i].Text).Append(lines[i].Ending);
        }

        byte[] body;
        string content = text.ToString();
        try
        {
            body = encoding.GetBytes(content);
        }
        catch (EncoderFallbackException)
        {
            encoding = new UTF8Encoding(false, true);
            body = encoding.GetBytes(content);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await SaveIniMutationAsync(path, exists, encoding.GetPreamble(), body,
            cancellationToken).ConfigureAwait(false);
        return Value.Empty;
    }

    private static List<IniMutationLine> ParseIniMutationLines(
        string text, CancellationToken cancellationToken)
    {
        var lines = new List<IniMutationLine>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if ((i & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (text[i] is not ('\r' or '\n'))
            {
                continue;
            }

            int end = i;
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
            }

            lines.Add(new IniMutationLine(text[start..end], text[end..(i + 1)]));
            start = i + 1;
        }

        if (start < text.Length)
        {
            lines.Add(new IniMutationLine(text[start..], ""));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return lines;
    }

    private static (int Start, int End) FindIniMutationSection(
        List<IniMutationLine> lines, string section, CancellationToken cancellationToken)
    {
        int start = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if ((i & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            string line = lines[i].Text.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                if (start >= 0)
                {
                    return (start, i);
                }

                if (line[1..^1].Trim().Equals(section, StringComparison.OrdinalIgnoreCase))
                {
                    start = i;
                }
            }
        }

        return (start, lines.Count);
    }

    private static int FindIniMutationKey(List<IniMutationLine> lines,
        int start, int end, string key, CancellationToken cancellationToken)
    {
        for (int i = start + 1; i < end; i++)
        {
            if ((i & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            string line = lines[i].Text.Trim();
            if (line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals >= 0 && line[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static void TerminateIniMutationLine(
        List<IniMutationLine> lines, int index, string ending)
    {
        if (index >= 0 && lines[index].Ending.Length == 0)
        {
            lines[index] = lines[index] with
            {
                Ending = ending
            };
        }
    }

    private static async Task SaveIniMutationAsync(string path,
        bool exists, byte[] preamble, byte[] body, CancellationToken cancellationToken)
    {
        string temporary = Path.Combine(Path.GetDirectoryName(path)!,
            ".uwsc-ini-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool owned = false;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                owned = true;
                await stream.WriteAsync(preamble, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            // 置換成功後は取消を投げず、確定した保存を成功として返します。
            if (exists)
            {
                File.Replace(temporary, path, null);
            }
            else
            {
                File.Move(temporary, path, false);
            }
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
                    throw new AggregateException("INI保存失敗後の一時ファイル削除に失敗しました。", failure, cleanup);
                }
            }

            throw;
        }
    }
}
