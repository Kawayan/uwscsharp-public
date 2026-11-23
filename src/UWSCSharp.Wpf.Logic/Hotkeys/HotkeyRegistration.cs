using System.Runtime.ExceptionServices;

namespace UWSCSharp.Wpf;

// ネイティブ登録を差し替えて競合と復旧を検証します。
internal interface IHotkeyBackend
{
    bool Register(int id, uint modifiers, int key);

    bool Unregister(int id);
}

internal sealed class HotkeyRegistration(IHotkeyBackend backend) : IDisposable
{
    internal const int StopHotkeyId = 3;
    private readonly List<int> active = [];
    private HotkeySettings? current;
    private bool sendingKeyboardInput;
    private bool stopSuppressed;
    private bool disposed;

    internal bool StopSuppressed => stopSuppressed;

    internal string Status { get; private set; } = "ホットキー未登録";

    internal int[] ActiveKeys => current is { Enabled: true }
        ? [.. active.Order().Select(id => current.Keys[id - 1])] : [];

    internal bool IsRegistered(int id) => current is not null && active.Contains(id);

    internal bool Apply(HotkeySettings settings)
    {
        CheckKeyboardInput();
        if (settings.HasReservedShortcut)
        {
            Status = HotkeySettings.ReservationWarning;
            return false;
        }

        settings.Validate();
        HotkeySettings? previous = current;
        if (!Release())
        {
            Status = "ホットキーを解放できませんでした。設定変更を中止しました";
            return false;
        }
        if (TryRegister(settings))
        {
            current = settings;
            Status = Describe(settings);
            return true;
        }

        if (!Release())
        {
            current = null;
            Status = "競合したホットキーの解放に失敗しました。画面ボタンを使用してください";
            return false;
        }
        bool restored = previous is not null && TryRegister(previous);
        current = restored ? previous : null;
        if (!restored)
        {
            Release();
        }

        Status = restored
            ? "ホットキーが競合しています。前の設定を復元しました：" + Describe(previous!)
            : "ホットキー登録に失敗しました。画面ボタンを使用してください";
        return false;
    }

    internal void SetStopSuppressed(bool suppressed)
    {
        CheckKeyboardInput();
        if (current is null && active.Count != 0)
        {
            throw new InvalidOperationException("設定が不明なホットキーが残っています。");
        }

        if (suppressed)
        {
            if (active.Contains(StopHotkeyId))
            {
                if (!backend.Unregister(StopHotkeyId))
                {
                    Status = "停止ホットキーを解除できませんでした。";
                    throw new InvalidOperationException(Status);
                }

                active.Remove(StopHotkeyId);
            }

            stopSuppressed = true;
        }
        else
        {
            // 復元に失敗しても、後から設定を適用して復旧できる状態を保ちます。
            stopSuppressed = false;
            if (current is { Enabled: true } && !active.Contains(StopHotkeyId))
            {
                if (!backend.Register(StopHotkeyId, current.Modifiers | 0x4000,
                    current.Keys[StopHotkeyId - 1]))
                {
                    Status = "停止ホットキーを復元できませんでした。画面ボタンを使用してください";
                    throw new InvalidOperationException(Status);
                }

                active.Add(StopHotkeyId);
            }
        }

        Status = current is null ? "ホットキー未登録" : Describe(current);
    }

    private string Describe(HotkeySettings settings) =>
        settings.Describe() + (settings.Enabled && stopSuppressed ? "（停止ホットキーのみ無効）" : "");

    internal void SendKeyboardInput(int key, Action send)
    {
        ArgumentNullException.ThrowIfNull(send);
        SendKeyboardInputAsync(key, () =>
        {
            send();
            return Task.CompletedTask;
        }).GetAwaiter().GetResult();
    }

    // 呼出し元のコンテキストへ戻り、同じスレッドでホットキーを復元します。
    internal async Task SendKeyboardInputAsync(int key, Func<Task> send)
    {
        ArgumentNullException.ThrowIfNull(send);
        CheckKeyboardInput();
        if (current is null && active.Count != 0)
        {
            throw new InvalidOperationException(
                "対応する設定が不明なホットキーが残っているため入力を送れません。");
        }

        uint modifier = key switch
        {
            16 or 160 or 161 => 4,
            17 or 162 or 163 => 2,
            18 or 164 or 165 => 1,
            91 or 92 => 8,
            _ => 0
        };
        int[] selected = current is { Enabled: true }
            ? [.. active.Where(id => current.Keys[id - 1] == key
                || (current.Modifiers & modifier) != 0)] : [];
        var removed = new List<int>();
        var errors = new List<Exception>();
        sendingKeyboardInput = true;
        try
        {
            try
            {
                foreach (int id in selected)
                {
                    if (!backend.Unregister(id))
                    {
                        throw new InvalidOperationException(
                            "入力と競合するホットキーを解除できませんでした。");
                    }

                    active.Remove(id);
                    removed.Add(id);
                }

                await send();
            }
            catch (Exception error)
            {
                errors.Add(error);
            }

            foreach (int id in removed)
            {
                try
                {
                    if (!backend.Register(id, current!.Modifiers | 0x4000,
                        current.Keys[id - 1]))
                    {
                        throw new InvalidOperationException(
                            "入力後のホットキー再登録に失敗しました。");
                    }

                    active.Add(id);
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
            }
        }
        finally
        {
            sendingKeyboardInput = false;
        }

        if (errors.Count != 0)
        {
            Status = "入力中のホットキー制御に失敗しました。画面ボタンを使用してください";
            if (errors.Count == 1)
            {
                ExceptionDispatchInfo.Capture(errors[0]).Throw();
            }

            throw new AggregateException("入力とホットキーの復旧に失敗しました。", errors);
        }
    }

    private void CheckKeyboardInput(bool allowDisposed = false)
    {
        ObjectDisposedException.ThrowIf(disposed && !allowDisposed, this);
        if (sendingKeyboardInput)
        {
            throw new InvalidOperationException(
                "キー送信中はホットキー設定を変更できません。");
        }
    }

    private bool TryRegister(HotkeySettings settings)
    {
        if (!settings.Enabled)
        {
            return true;
        }

        for (int index = 0; index < settings.Keys.Length; index++)
        {
            int id = index + 1;
            if (id == StopHotkeyId && stopSuppressed)
            {
                continue;
            }
            if (!backend.Register(id, settings.Modifiers | 0x4000,
                settings.Keys[index]))
            {
                return false;
            }

            active.Add(id);
        }

        return true;
    }

    private bool Release()
    {
        foreach (int id in active.ToArray())
        {
            if (backend.Unregister(id))
            {
                active.Remove(id);
            }
        }

        return active.Count == 0;
    }

    /// <summary>
    /// 登録した全ホットキーを解放します。
    /// </summary>
    public void Dispose()
    {
        CheckKeyboardInput(allowDisposed: true);
        disposed = true;
        Release();
    }
}
