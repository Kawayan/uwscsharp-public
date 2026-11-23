namespace UWSCSharp.Windows;

/// <summary>
/// 登録時の所有者と、観測した失効・明示削除を保持します。
/// </summary>
internal sealed class WindowRegistrationSnapshot(
    nint handle, WindowIdentity? identity, IWindowIdentityPlatform platform)
{
    private int expired;
    private int removed;

    internal nint Handle => handle;

    internal WindowIdentity? Identity => identity;

    internal bool IsExpired => Volatile.Read(ref expired) != 0;

    internal bool IsRemoved => Volatile.Read(ref removed) != 0;

    internal void Expire() => Interlocked.Exchange(ref expired, 1);

    internal void Remove()
    {
        Expire();
        Interlocked.Exchange(ref removed, 1);
    }

    internal bool Validate()
    {
        if (IsExpired)
        {
            return false;
        }

        if (identity is null || platform.ReadIdentity(handle) != identity)
        {
            Expire();
            return false;
        }

        return !IsExpired;
    }
}

/// <summary>
/// 実行共有の数値IDと、入力禁止に使う登録時の情報を管理します。
/// </summary>
internal sealed class WindowRegistrationTable(IWindowIdentityPlatform platform) : IDisposable
{
    private readonly object gate = new();
    private readonly List<WindowRegistrationSnapshot> entries = [];
    private bool disposed;
    private long version;

    internal IWindowIdentityPlatform Platform => platform;

    internal int Register(nint handle) => RegisterCore(handle, null);

    internal int RegisterExpected(nint handle, WindowIdentity expected) =>
        expected.ProcessId == 0 || expected.ThreadId == 0 ? -1 : RegisterCore(handle, expected);

    private int RegisterCore(nint handle, WindowIdentity? expected)
    {
        if (handle == 0)
        {
            return -1;
        }

        while (true)
        {
            long observedVersion;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                observedVersion = version;
            }

            // 古い照会が別の登録や明示無効化の後に確定される場合は、照会をやり直します。
            WindowIdentity? observed = platform.ReadIdentity(handle);
            WindowIdentity? identity = observed is { ProcessId: > 0, ThreadId: > 0 }
                ? observed : null;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (version != observedVersion)
                {
                    continue;
                }

                if (expected is not null && identity != expected)
                {
                    return -1;
                }

                int match = -1;
                int existing = -1;
                for (int index = 0; index < entries.Count; index++)
                {
                    WindowRegistrationSnapshot entry = entries[index];
                    if (entry.Handle != handle || entry.IsRemoved)
                    {
                        continue;
                    }

                    existing = index;
                    if (!entry.IsExpired && entry.Identity == identity)
                    {
                        match = index;
                    }
                    else
                    {
                        entry.Expire();
                    }
                }

                if (identity is null && existing >= 0)
                {
                    // 照会不能は新しい所有者の発見ではなく、生ハンドルの登録IDを維持します。
                    entries[existing].Expire();
                    version++;
                    return existing + 1;
                }

                if (match >= 0)
                {
                    return match + 1;
                }

                entries.Add(new WindowRegistrationSnapshot(handle, identity, platform));
                version++;
                return entries.Count;
            }
        }
    }

    internal WindowRegistrationSnapshot? GetRegistration(int id)
    {
        lock (gate)
        {
            return id > 0 && id <= entries.Count && !entries[id - 1].IsRemoved
                ? entries[id - 1] : null;
        }
    }

    // 通常の解決では従来のHWNDを返し、入力禁止専用の失効判定を追加しません。
    internal nint Resolve(int id) => GetRegistration(id)?.Handle ?? 0;

    internal void Invalidate(nint handle)
    {
        lock (gate)
        {
            version++;
            foreach (WindowRegistrationSnapshot entry in entries)
            {
                if (entry.Handle == handle)
                {
                    entry.Remove();
                }
            }
        }
    }

    /// <summary>
    /// 共有表を終了し、保持中の対象も失効させます。
    /// </summary>
    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            foreach (WindowRegistrationSnapshot entry in entries)
            {
                entry.Remove();
            }

            entries.Clear();
        }
    }
}
