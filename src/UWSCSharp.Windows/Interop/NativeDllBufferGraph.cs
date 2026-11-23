using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

/// <summary>
/// 所有領域の中へ、別の所有領域を指すポインターを置く位置を表します。
/// </summary>
internal readonly record struct NativeDllBufferLink(
    int SourceBuffer, int SourceOffset, int TargetBuffer, int TargetOffset = 0);

/// <summary>
/// 領域間の参照を接続し、呼出しごとに独立した領域を結果読取りまで所有します。
/// </summary>
internal sealed class NativeDllBufferGraph
{
    private readonly byte[][] initialBytes;
    private readonly NativeDllBufferLink[] links;
    private readonly INativeDllBufferAllocator? allocator;

    internal NativeDllBufferGraph(IReadOnlyList<byte[]> buffers,
        IReadOnlyList<NativeDllBufferLink> links,
        INativeDllBufferAllocator? allocator = null)
    {
        ArgumentNullException.ThrowIfNull(buffers);
        ArgumentNullException.ThrowIfNull(links);
        initialBytes = [.. buffers.Select(bytes =>
        {
            ArgumentNullException.ThrowIfNull(bytes);
            return (byte[])bytes.Clone();
        })];
        this.links = [.. links];
        this.allocator = allocator;
        ValidateLinks();
    }

    /// <summary>
    /// 結果は所有領域のコピーから読みます。コピー内のポインターは呼出し後に使えません。
    /// </summary>
    internal T Invoke<T>(NativeDllModule.Function function,
        Func<IReadOnlyList<nint>, object?[]> prepareArguments,
        Func<object?, IReadOnlyList<byte[]>, T> readResult)
    {
        ArgumentNullException.ThrowIfNull(readResult);
        return Invoke(function, prepareArguments,
            (result, bytes, _) => readResult(result, bytes));
    }

    internal T Invoke<T>(NativeDllModule.Function function,
        Func<IReadOnlyList<nint>, object?[]> prepareArguments,
        Func<object?, IReadOnlyList<byte[]>, IReadOnlyList<nint>, T> readResult)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(prepareArguments);
        ArgumentNullException.ThrowIfNull(readResult);
        List<NativeDllBuffer> buffers = [];
        try
        {
            foreach (byte[] bytes in initialBytes)
            {
                var buffer = NativeDllBuffer.Allocate(bytes.Length, allocator);
                try
                {
                    buffers.Add(buffer);
                }
                catch
                {
                    buffer.Dispose();
                    throw;
                }

                buffer.Write(0, bytes);
            }

            return function.InvokeWithBuffers(buffers, pointers =>
            {
                foreach (NativeDllBufferLink link in links)
                {
                    Marshal.WriteIntPtr(pointers[link.SourceBuffer] + link.SourceOffset,
                        pointers[link.TargetBuffer] + link.TargetOffset);
                }

                return prepareArguments(pointers);
            }, (result, pointers) =>
            {
                byte[][] copies = new byte[initialBytes.Length][];
                for (int index = 0; index < copies.Length; index++)
                {
                    copies[index] = new byte[initialBytes[index].Length];
                    Marshal.Copy(pointers[index], copies[index], 0, copies[index].Length);
                }

                return readResult(result, Array.AsReadOnly(copies), pointers);
            });
        }
        finally
        {
            for (int index = buffers.Count - 1; index >= 0; index--)
            {
                buffers[index].Dispose();
            }
        }
    }

    private void ValidateLinks()
    {
        foreach (NativeDllBufferLink link in links)
        {
            if ((uint)link.SourceBuffer >= (uint)initialBytes.Length
                || (uint)link.TargetBuffer >= (uint)initialBytes.Length)
            {
                throw new ArgumentException("参照先と参照元に存在する領域を指定してください。", nameof(links));
            }

            int sourceLength = initialBytes[link.SourceBuffer].Length;
            int targetLength = initialBytes[link.TargetBuffer].Length;
            if (link.SourceOffset < 0 || link.SourceOffset > sourceLength - nint.Size
                || link.TargetOffset < 0 || link.TargetOffset > targetLength)
            {
                throw new ArgumentException("ポインターの配置位置と参照先が領域を超えています。", nameof(links));
            }
        }

        int previousBuffer = -1;
        int previousEnd = 0;
        foreach (NativeDllBufferLink link in links
            .OrderBy(link => link.SourceBuffer).ThenBy(link => link.SourceOffset))
        {
            if (link.SourceBuffer == previousBuffer && link.SourceOffset < previousEnd)
            {
                throw new ArgumentException("ポインターの配置領域を重複させることはできません。", nameof(links));
            }

            previousBuffer = link.SourceBuffer;
            previousEnd = link.SourceOffset + nint.Size;
        }
    }
}
