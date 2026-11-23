using System.Buffers;

namespace UWSCSharp.Windows;

/// <summary>
/// 呼出しが保持するポインターから終端までのバイト列を読み取ります。
/// </summary>
internal static class NativeDllTextContents
{
    private const int MaxExternalBytes = 16 * 1024 * 1024;

    internal static ReadOnlySpan<byte> Read(nint address, bool wide,
        CancellationToken token, IReadOnlyList<byte[]>? buffers = null,
        IReadOnlyList<nint>? pointers = null)
    {
        if (address == 0)
        {
            return [];
        }

        if (buffers is not null && pointers is not null)
        {
            for (int index = 0; index < buffers.Count; index++)
            {
                nuint offset = unchecked((nuint)address - (nuint)pointers[index]);
                if (offset < (nuint)buffers[index].Length)
                {
                    ReadOnlySpan<byte> contents = buffers[index].AsSpan((int)offset);
                    int end = FindTerminator(contents, wide);
                    if (end < 0)
                    {
                        throw new InvalidOperationException(
                            "DLL戻り値の文字列に、引数領域内の終端がありません。");
                    }

                    return contents[..end];
                }
            }
        }

        return ReadExternal(address, wide, token);
    }

    private static ReadOnlySpan<byte> ReadExternal(nint address, bool wide,
        CancellationToken token)
    {
        nint process = Native.GetCurrentProcess();
        int pageSize = Environment.SystemPageSize;
        byte[] page = new byte[pageSize];
        var contents = new ArrayBufferWriter<byte>(pageSize);
        nuint current = (nuint)address;
        while (contents.WrittenCount < MaxExternalBytes)
        {
            token.ThrowIfCancellationRequested();
            int offset = contents.WrittenCount;
            int pageRemaining = pageSize - (int)(current % (nuint)pageSize);
            int count = Math.Min(pageRemaining, MaxExternalBytes - offset);
            if (!Native.ReadProcessMemory(process, (nint)current, page,
                (nuint)count, out nuint read) || read != (nuint)count)
            {
                throw new InvalidOperationException(
                    "DLL戻り値の文字列を読み取れません。");
            }

            contents.Write(page.AsSpan(0, count));
            // ページをまたぐUTF-16コード単位は前のページの末尾から調べます。
            int scan = wide ? offset & ~1 : offset;
            int end = FindTerminator(contents.WrittenSpan[scan..], wide);
            if (end >= 0)
            {
                return contents.WrittenSpan[..(scan + end)];
            }

            current = checked(current + (nuint)count);
        }

        throw new InvalidOperationException(
            "DLL戻り値の文字列に、読取り上限の16 MiB以内の終端がありません。");
    }

    private static int FindTerminator(ReadOnlySpan<byte> contents, bool wide)
    {
        if (!wide)
        {
            return contents.IndexOf((byte)0);
        }

        for (int index = 1; index < contents.Length; index += 2)
        {
            if (contents[index - 1] == 0 && contents[index] == 0)
            {
                return index - 1;
            }
        }

        return -1;
    }
}
