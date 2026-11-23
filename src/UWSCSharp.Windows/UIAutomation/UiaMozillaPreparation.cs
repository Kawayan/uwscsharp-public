using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UWSCSharp.Windows;

internal interface IUiaPreparationBackend
{
    void Sleep(int milliseconds, CancellationToken token);

    void Pump(CancellationToken token);
}

/// <summary>
/// 実行session内のMozilla対象切替と準備順序を管理します。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class UiaMozillaPreparation(IUiaPreparationBackend? backend = null)
{
    private readonly IUiaPreparationBackend operations = backend ?? new NativeUiaPreparationBackend();
    private readonly object gate = new();
    private nint lastRoot;

    internal T Execute<T>(nint root, Func<Action?, T> read, CancellationToken token)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (root == lastRoot)
            {
                return read(null);
            }

            // 原版と同様、初期化前に対象を記録します。失敗時にも巻き戻しません。
            lastRoot = root;
            return read(() => Prepare(token));
        }
    }

    private void Prepare(CancellationToken token)
    {
        for (int cycle = 0; cycle < 4; cycle++)
        {
            token.ThrowIfCancellationRequested();
            operations.Sleep(40, token);
            for (int iteration = 0; iteration < 40; iteration++)
            {
                token.ThrowIfCancellationRequested();
                operations.Sleep(0, token);
                operations.Pump(token);
            }
        }

        token.ThrowIfCancellationRequested();
    }
}

[SupportedOSPlatform("windows")]
internal sealed class NativeUiaPreparationBackend : IUiaPreparationBackend
{
    public void Sleep(int milliseconds, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (milliseconds == 0)
        {
            Thread.Sleep(0);
        }
        else if (token.WaitHandle.WaitOne(milliseconds))
        {
            token.ThrowIfCancellationRequested();
        }

        token.ThrowIfCancellationRequested();
    }

    public void Pump(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!Native.PeekMessageW(out Native.Message message, 0, 0, 0, 1))
            {
                return;
            }

            if (message.Id == 0x12)
            {
                throw new OperationCanceledException("UIA準備queueの終了が要求されました。");
            }

            Native.TranslateMessage(ref message);
            Native.DispatchMessageW(ref message);
        }
    }
}
