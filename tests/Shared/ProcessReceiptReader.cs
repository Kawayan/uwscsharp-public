using System.Globalization;
using System.Text;
using System.Text.Json;

namespace UWSCSharp.Testing;

/// <summary>
/// 検証用の通知を、共有・ロック違反だけ再試行して読み取ります。
/// </summary>
internal static class ProcessReceiptReader
{
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    internal static Task<int> ReadPidAsync(string path, CancellationToken token)
        => ReadPidAsync(path, token, ReadOnceAsync);

    internal static async Task<int> ReadPidAsync(string path, CancellationToken token,
        Func<string, CancellationToken, Task<string>> read)
        => int.Parse(await ReadPublishedTextAsync(path, token, read).ConfigureAwait(false),
            CultureInfo.InvariantCulture);

    internal static Task<JsonDocument> ReadJsonAsync(string path, CancellationToken token)
        => ReadJsonAsync(path, token, ReadOnceAsync);

    internal static async Task<JsonDocument> ReadJsonAsync(string path, CancellationToken token,
        Func<string, CancellationToken, Task<string>> read)
        => JsonDocument.Parse(await ReadPublishedTextAsync(path, token, read).ConfigureAwait(false));

    private static async Task<string> ReadPublishedTextAsync(string path, CancellationToken token,
        Func<string, CancellationToken, Task<string>> read)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return await read(path, token).ConfigureAwait(false);
            }
            catch (IOException error) when (error.HResult is SharingViolation or LockViolation)
            {
                await Task.Delay(25, token).ConfigureAwait(false);
            }
        }
    }

    private static async Task<string> ReadOnceAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        return await reader.ReadToEndAsync(token).ConfigureAwait(false);
    }
}
