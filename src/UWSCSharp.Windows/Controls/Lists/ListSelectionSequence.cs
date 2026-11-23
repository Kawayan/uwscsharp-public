namespace UWSCSharp.Windows;

internal interface IListSelectionBackend
{
    void ActivateRoot(CancellationToken token);
    void PrepareRoot(CancellationToken token);
    void SetSelection(int index, CancellationToken token);
    nint GetSelection(CancellationToken token);
    void LockUpdates(bool locked);
    void PressKey(int key, CancellationToken token);
    void Sleep(int milliseconds, CancellationToken token);
    void Yield(int iterations, CancellationToken token);
    bool OpenCombo(CancellationToken token);
    void CloseCombo();
    void PrepareComboCursor(CancellationToken token);
    void RestoreComboCursor();
}

/// <summary>
/// 単一LIST/COMBOの設定・通知・確認と再試行を管理します。
/// </summary>
internal static class ListSelectionSequence
{
    internal static bool Execute(int index, bool combo, bool back,
        IListSelectionBackend backend, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!back)
        {
            backend.ActivateRoot(token);
        }

        bool opened = false;
        bool succeeded = false;
        try
        {
            if (combo)
            {
                backend.PrepareComboCursor(token);
                opened = backend.OpenCombo(token);
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                token.ThrowIfCancellationRequested();
                backend.SetSelection(index, token);
                if (!back)
                {
                    backend.ActivateRoot(token);
                }

                backend.PrepareRoot(token);
                try
                {
                    backend.LockUpdates(true);
                    backend.PressKey(index == 0 ? 40 : 38, token);
                    backend.PressKey(index == 0 ? 38 : 40, token);
                }
                finally
                {
                    // 取消やkey callbackの例外でも描画lockの解除を要求します。
                    backend.LockUpdates(false);
                }

                backend.Yield(20, token);
                backend.PrepareRoot(token);
                if (backend.GetSelection(token) == index)
                {
                    if (opened)
                    {
                        backend.PressKey(13, token);
                    }

                    token.ThrowIfCancellationRequested();
                    succeeded = true;
                    return true;
                }

                backend.Sleep(combo ? 100 : 50, token);
                backend.Yield(20, token);
            }

            token.ThrowIfCancellationRequested();
            return false;
        }
        finally
        {
            // 失敗や取消で表示したdropdownを残さないようにします。
            try
            {
                if (opened && !succeeded)
                {
                    backend.CloseCombo();
                }
            }
            finally
            {
                if (combo)
                {
                    backend.RestoreComboCursor();
                }
            }
        }
    }
}
