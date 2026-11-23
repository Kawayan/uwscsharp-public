using UWSCSharp.Core;

namespace UWSCSharp.Windows;

internal interface IKeyStateBackend
{
    short ReadAsync(int key);

    short ReadToggle(int key);

    bool ReadIme(CancellationToken token);
}

/// <summary>
/// キー状態のビット判定と左右交換を管理します。
/// </summary>
internal static class KeyStateReader
{
    internal static bool Read(KeyStateRequest request,
        IKeyStateBackend backend, bool swapped, CancellationToken token,
        MouseKeyHistory? history = null)
    {
        token.ThrowIfCancellationRequested();
        bool result;
        if (request.Kind == KeyStateKind.Ime)
        {
            result = backend.ReadIme(token);
        }
        else if (request.Kind == KeyStateKind.Toggle)
        {
            result = (backend.ReadToggle(request.Key) & 1) != 0;
        }
        else
        {
            int key = swapped ? request.Key switch
            {
                1 => 2,
                2 => 1,
                _ => request.Key
            } : request.Key;
            bool current = backend.ReadAsync(key) != 0;
            token.ThrowIfCancellationRequested();
            // 押下中でも保留分を消費し、解放後へ持ち越しません。
            bool pending = history?.Consume(key) ?? false;
            result = current || pending;
        }

        token.ThrowIfCancellationRequested();
        return result;
    }
}
