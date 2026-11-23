using System.Buffers;

namespace UWSCSharp.Windows;

/// <summary>
/// 呼出し元が所有する出力ストリームを、上限付きで終端まで読みます。
/// </summary>
internal static class ProcessOutputReader
{
    internal const int DefaultMaximumBytes = 16 * 1024 * 1024;

    internal static async Task<byte[]> ReadAsync(Stream source,
        int maximumBytes = DefaultMaximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            using var output = new MemoryStream();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int requested = (int)Math.Min(buffer.Length,
                    (long)maximumBytes - output.Length + 1);
                int count = await source.ReadAsync(
                    buffer.AsMemory(0, requested), cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (count == 0)
                {
                    return output.ToArray();
                }

                if (output.Length + count > maximumBytes)
                {
                    throw new ProcessOutputLimitException(
                        maximumBytes, output.Length + count);
                }

                output.Write(buffer, 0, count);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}

/// <summary>
/// 一部の出力を成功値として返さず、上限超過を通知します。
/// </summary>
internal sealed class ProcessOutputLimitException(int maximumBytes,
    long observedBytes) : IOException(
        $"コマンド出力が上限{maximumBytes}バイトを超えました。")
{
    internal int MaximumBytes => maximumBytes;

    internal long ObservedBytes => observedBytes;
}
