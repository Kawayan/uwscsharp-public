using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// 解決済みの絶対パスごとにDLLを保持し、実行終了時に所有参照を解放します。
/// </summary>
internal sealed class NativeDllModuleCache(INativeDllLoader? loader = null) : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private bool disposed;

    internal NativeDllModule.Function Resolve(string path, string export, NativeDllCallSite callSite)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(export);
        ArgumentNullException.ThrowIfNull(callSite);
        if (export.Contains('\0'))
        {
            throw new ArgumentException("export名にNULを含めることはできません。", nameof(export));
        }

        if (!Path.IsPathFullyQualified(path) || path.Contains('\0'))
        {
            throw new ArgumentException("解決済みのDLLの絶対パスを指定してください。", nameof(path));
        }

        string key = Path.GetFullPath(path);
        Entry entry;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!entries.TryGetValue(key, out entry!))
            {
                entry = new Entry(key, loader ?? NativeDllLoader.ScriptDependencies);
                entries.Add(key, entry);
            }
        }

        NativeDllModule module;
        try
        {
            module = entry.GetModule();
        }
        catch
        {
            lock (gate)
            {
                if (entries.TryGetValue(key, out Entry? current) && ReferenceEquals(current, entry))
                {
                    entries.Remove(key);
                }
            }

            entry.Dispose();
            throw;
        }

        return module.Resolve(export, callSite);
    }

    /// <summary>
    /// ロード完了を待たずに終了を要求し、遅れてロードされたDLLも解放します。
    /// </summary>
    public void Dispose()
    {
        Entry[] owned;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            owned = [.. entries.Values];
            entries.Clear();
        }

        List<Exception> errors = [];
        foreach (Entry entry in owned)
        {
            try
            {
                entry.Dispose();
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }

        if (errors.Count > 0)
        {
            throw new AggregateException(errors);
        }
    }

    private sealed class Entry : IDisposable
    {
        private readonly object gate = new();
        private readonly Lazy<NativeDllModule> loading;
        private NativeDllModule? module;
        private bool disposed;

        internal Entry(string path, INativeDllLoader loader)
        {
            loading = new Lazy<NativeDllModule>(() => Load(path, loader));
        }

        internal NativeDllModule GetModule() => loading.Value;

        private NativeDllModule Load(string path, INativeDllLoader loader)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
            }

            var loaded = NativeDllModule.Load(path, loader);
            lock (gate)
            {
                if (!disposed)
                {
                    module = loaded;
                    return loaded;
                }
            }

            loaded.Dispose();
            throw new ObjectDisposedException(nameof(NativeDllModuleCache));
        }

        public void Dispose()
        {
            NativeDllModule? owned;
            lock (gate)
            {
                disposed = true;
                owned = module;
                module = null;
            }

            owned?.Dispose();
        }
    }
}

/// <summary>
/// 公開構文の接続前に、実行単位のDLL所有を利用する内部接点です。
/// </summary>
internal interface INativeDllModuleSource
{
    NativeDllModule.Function ResolveNativeDll(
        NativeDllDeclaration declaration, string workingDirectory,
        NativeDllCallSite callSite);

    NativeDllModule.Function ResolveNativeDll(string path, string export, NativeDllCallSite callSite);
}
