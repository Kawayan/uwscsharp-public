using System.Runtime.ExceptionServices;
using UWSCSharp.Core;

namespace UWSCSharp.Wpf;

internal readonly record struct LauncherHotkeyBinding(int Id, ScriptHotkey Shortcut);

// 保存の確定とOS登録の回収を分け、失敗した登録も終了まで保持します。
internal sealed class LauncherHotkeyRegistration(IHotkeyBackend backend) : IDisposable
{
    private readonly Dictionary<int, ScriptHotkey> registered = [];
    private readonly List<Exception> cleanupErrors = [];
    private int nextId = 0x1000;
    private int? acceptingId;
    private bool changing;
    private bool disposed;
    private bool sendingKeyboardInput;

    internal ScriptHotkey? ActiveShortcut => acceptingId is int id
        ? registered[id] : null;

    internal LauncherHotkeyBinding[] PendingBindings =>
        [.. registered.Where(pair => pair.Key != acceptingId)
            .Select(pair => new LauncherHotkeyBinding(pair.Key, pair.Value))];

    internal ScriptHotkey[] RegisteredShortcuts => [.. registered.Values];

    internal Exception[] CleanupErrors => [.. cleanupErrors];

    internal bool IsBusy => changing;

    internal string Status { get; private set; } = "ランチ用ホットキーは未設定です。";

    internal bool Accepts(int id) => !disposed && !sendingKeyboardInput
        && acceptingId == id;

    // 送信中だけ有効キーを外し、未回収キーと重なる入力は先に拒否します。
    internal async Task SendKeyboardInputAsync(int key, Func<Task> send)
    {
        ArgumentNullException.ThrowIfNull(send);
        BeginChange();
        sendingKeyboardInput = true;
        int? removedId = null;
        ScriptHotkey? removedShortcut = null;
        var errors = new List<Exception>();
        try
        {
            try
            {
                if (PendingBindings.Any(binding => Matches(binding.Shortcut)))
                {
                    throw new InvalidOperationException(
                        "未回収のランチ用ホットキーと入力が競合します。再回収してから実行してください。");
                }

                if (ActiveShortcut is ScriptHotkey shortcut && Matches(shortcut))
                {
                    int id = acceptingId!.Value;
                    if (!backend.Unregister(id))
                    {
                        throw new InvalidOperationException(
                            "入力と競合するランチ用ホットキーを解除できませんでした。");
                    }

                    // 有効な設定は保持し、通知の受付だけを送信完了まで止めます。
                    removedId = id;
                    removedShortcut = shortcut;
                }

                await send();
            }
            catch (Exception error)
            {
                errors.Add(error);
            }

            if (removedId is int restoreId && removedShortcut is ScriptHotkey restore)
            {
                try
                {
                    if (!backend.Register(restoreId,
                        (uint)restore.Modifiers | 0x4000, restore.VirtualKey))
                    {
                        throw new InvalidOperationException(
                            "入力後のランチ用ホットキー再登録に失敗しました。設定を再読込みしてください。");
                    }
                }
                catch (Exception error)
                {
                    registered.Remove(restoreId);
                    acceptingId = null;
                    errors.Add(error);
                }
            }

            RefreshStatus();
            if (errors.Count != 0)
            {
                Status += " 入力中のホットキー制御に失敗しました。";
                if (errors.Count == 1)
                {
                    ExceptionDispatchInfo.Capture(errors[0]).Throw();
                }

                throw new AggregateException("入力とランチ用ホットキーの復旧に失敗しました。", errors);
            }
        }
        finally
        {
            sendingKeyboardInput = false;
            changing = false;
        }

        bool Matches(ScriptHotkey shortcut)
        {
            int modifier = key switch
            {
                16 or 160 or 161 => 4,
                17 or 162 or 163 => 2,
                18 or 164 or 165 => 1,
                91 or 92 => 8,
                _ => 0
            };
            return shortcut.VirtualKey == key || (shortcut.Modifiers & modifier) != 0;
        }
    }

    internal void Apply(LauncherSettings candidate,
        IEnumerable<ScriptHotkey> occupied, Action persist)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(persist);
        BeginChange();
        try
        {
            candidate.ValidateShortcutAvailability(occupied);
            if (candidate.Shortcut == ActiveShortcut)
            {
                // 同じキーのまま登録項目を保存する場合は、OS登録を入れ替えません。
                persist();
                RefreshStatus();
                return;
            }

            if (PendingBindings.Length != 0)
            {
                throw new InvalidOperationException(
                    "未回収のランチ用ホットキーがあります。再回収してからキーを変更してください。");
            }

            int? candidateId = null;
            if (candidate.Shortcut is ScriptHotkey shortcut)
            {
                // 遅れて届く旧通知を受け付けないよう、解放済みのIDも再利用しません。
                if (nextId > 0x3fff)
                {
                    throw new InvalidOperationException(
                        "ランチ用ホットキーの登録IDを使い切りました。本体を再起動してください。");
                }

                int id = nextId++;
                if (!backend.Register(id, (uint)shortcut.Modifiers | 0x4000,
                    shortcut.VirtualKey))
                {
                    throw new InvalidOperationException(
                        "ランチ用ホットキーを登録できません。前の設定を保持しています。");
                }

                registered.Add(id, shortcut);
                candidateId = id;
            }

            try
            {
                persist();
            }
            catch
            {
                if (candidateId is int id)
                {
                    cleanupErrors.Clear();
                    Release(id);
                }

                RefreshStatus();
                throw;
            }

            int? previousId = acceptingId;
            acceptingId = candidateId;
            cleanupErrors.Clear();
            if (previousId is int oldId)
            {
                Release(oldId);
            }

            RefreshStatus();
        }
        finally
        {
            changing = false;
        }
    }

    internal bool RetryCleanup()
    {
        BeginChange(allowDisposed: true);
        try
        {
            cleanupErrors.Clear();
            foreach (LauncherHotkeyBinding binding in PendingBindings)
            {
                Release(binding.Id);
            }

            RefreshStatus();
            return PendingBindings.Length == 0;
        }
        finally
        {
            changing = false;
        }
    }

    private void BeginChange(bool allowDisposed = false)
    {
        ObjectDisposedException.ThrowIf(disposed && !allowDisposed, this);
        if (changing)
        {
            throw new InvalidOperationException("ランチ用ホットキーの変更処理には再入できません。");
        }

        changing = true;
    }

    private void Release(int id)
    {
        try
        {
            if (backend.Unregister(id))
            {
                registered.Remove(id);
                return;
            }

            cleanupErrors.Add(new InvalidOperationException(
                $"ランチ用ホットキーを解除できませんでした（登録ID: {id}）。"));
        }
        catch (Exception error)
        {
            cleanupErrors.Add(error);
        }
    }

    private void RefreshStatus()
    {
        Status = ActiveShortcut is null
            ? "ランチ用ホットキーは未設定です。"
            : "ランチ用ホットキーを登録しました。";
        if (PendingBindings.Length != 0)
        {
            Status += " 回収未完了のキーがあります。キーの変更前に再回収してください。";
        }
    }

    /// <summary>
    /// 通知の受付を止め、有効・未回収の全登録を回収します。
    /// </summary>
    public void Dispose()
    {
        BeginChange(allowDisposed: true);
        try
        {
            disposed = true;
            acceptingId = null;
            cleanupErrors.Clear();
            foreach (int id in registered.Keys.ToArray())
            {
                Release(id);
            }

            RefreshStatus();
        }
        finally
        {
            changing = false;
        }
    }
}
