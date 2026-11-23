using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

/// <summary>
/// 所有するDLLを、実行中の呼出しが戻るまで保持します。
/// </summary>
internal sealed class NativeDllModule : IDisposable
{
    private readonly object gate = new();
    private readonly ModuleHandle handle;
    private readonly INativeDllLoader loader;
    private bool disposed;

    private NativeDllModule(ModuleHandle handle, INativeDllLoader loader)
    {
        this.handle = handle;
        this.loader = loader;
    }

    internal static NativeDllModule Load(string path, INativeDllLoader? loader = null) =>
        LoadResolved(path, loader ?? NativeDllLoader.Instance, Architecture.X64);

    /// <summary>
    /// x86の中継プロセスでDLLをロードします。
    /// </summary>
    internal static NativeDllModule LoadX86RelayLibrary(string path) =>
        LoadResolved(path, NativeDllLoader.ScriptDependencies, Architecture.X86);

    private static NativeDllModule LoadResolved(string path,
        INativeDllLoader loader, Architecture architecture)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!Path.IsPathFullyQualified(path) || path.Contains('\0'))
        {
            throw new ArgumentException("解決済みのDLLの絶対パスを指定してください。", nameof(path));
        }

        return LoadCore(Path.GetFullPath(path), loader, architecture);
    }

    internal static NativeDllModule LoadAssemblyLibrary(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name.Contains('\0') || !string.Equals(Path.GetFileName(name), name,
                StringComparison.Ordinal))
        {
            throw new ArgumentException("DLLのファイル名だけを指定してください。", nameof(name));
        }

        return LoadCore(name, NativeDllLoader.AssemblyDirectory);
    }

    private static NativeDllModule LoadCore(string name, INativeDllLoader loader,
        Architecture architecture = Architecture.X64)
    {
        if (!OperatingSystem.IsWindows()
            || RuntimeInformation.ProcessArchitecture != architecture)
        {
            throw new PlatformNotSupportedException(
                architecture == Architecture.X64
                    ? "DLLの直接ロードはWindows x64で実装しています。"
                    : "32ビットDLLの中継はWindows x86プロセスが必要です。");
        }

        var owner = new ModuleHandle(loader);
        try
        {
            owner.Load(name);
            return new NativeDllModule(owner, loader);
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    internal Function Resolve(string export, NativeDllCallSite callSite)
    {
        ArgumentException.ThrowIfNullOrEmpty(export);
        ArgumentNullException.ThrowIfNull(callSite);
        if (export.Contains('\0'))
        {
            throw new ArgumentException("export名にNULを含めることはできません。", nameof(export));
        }

        bool retained = false;
        try
        {
            Retain(ref retained);
            nint address = loader.GetExport(handle.DangerousGetHandle(), export);
            if (address == 0)
            {
                throw new EntryPointNotFoundException(export);
            }

            return new Function(this, address, callSite);
        }
        finally
        {
            if (retained)
            {
                handle.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// 新規呼出しを止め、残る呼出しが戻った時点でDLLを解放します。
    /// </summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        // 解放中のDLLから再入されても、管理用のロックを保持しません。
        handle.Dispose();
    }

    private T Invoke<T>(nint address, NativeDllCallSite callSite,
        object?[] arguments, Func<object?, T> readResult)
    {
        bool retained = false;
        try
        {
            Retain(ref retained);
            return readResult(callSite.Invoke(address, arguments));
        }
        finally
        {
            if (retained)
            {
                handle.DangerousRelease();
            }
        }
    }

    private void Retain(ref bool retained)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            handle.DangerousAddRef(ref retained);
        }
    }

    internal sealed class Function
    {
        private readonly NativeDllModule owner;
        private readonly nint address;
        private readonly NativeDllCallSite callSite;

        internal Function(NativeDllModule owner, nint address, NativeDllCallSite callSite)
        {
            this.owner = owner;
            this.address = address;
            this.callSite = callSite;
        }

        internal object? Invoke(params object?[] arguments) =>
            owner.Invoke(address, callSite, arguments, result => result);

        internal T Invoke<T>(object?[] arguments, Func<object?, T> readResult) =>
            owner.Invoke(address, callSite, arguments, readResult);

        /// <summary>
        /// 引数準備から結果読取りまで領域を保持し、結果読取り中はDLLも保持します。
        /// </summary>
        internal T InvokeWithBuffers<T>(IReadOnlyList<NativeDllBuffer> buffers,
            Func<IReadOnlyList<nint>, object?[]> prepareArguments,
            Func<object?, IReadOnlyList<nint>, T> readResult)
        {
            ArgumentNullException.ThrowIfNull(prepareArguments);
            ArgumentNullException.ThrowIfNull(readResult);
            return NativeDllBuffer.WithPointers(buffers, pointers =>
                owner.Invoke(address, callSite, prepareArguments(pointers),
                    result => readResult(result, pointers)));
        }
    }

    private sealed class ModuleHandle(INativeDllLoader loader) : SafeHandle(0, ownsHandle: true)
    {
        /// <summary>
        /// ロード済みのハンドルがないかどうかを取得します。
        /// </summary>
        public override bool IsInvalid => handle == 0;

        internal void Load(string path)
        {
            SetHandle(loader.Load(path));
            if (IsInvalid)
            {
                throw new DllNotFoundException(path);
            }
        }

        protected override bool ReleaseHandle()
        {
            loader.Free(handle);
            return true;
        }
    }
}

internal interface INativeDllLoader
{
    nint Load(string path);

    nint GetExport(nint library, string export);

    void Free(nint library);
}

internal sealed class NativeDllLoader(Func<string, nint> load)
    : INativeDllLoader
{
    private const int ErrorBadExeFormat = 193;

    internal static NativeDllLoader Instance { get; } = new(NativeLibrary.Load);

    internal static NativeDllLoader AssemblyDirectory
    {
        get;
    } = new(path => NativeLibrary.Load(path,
        typeof(NativeDllLoader).Assembly, DllImportSearchPath.AssemblyDirectory
        | DllImportSearchPath.ApplicationDirectory));

    internal static NativeDllLoader ScriptDependencies
    {
        get;
    } = new(LoadScriptLibrary);

    /// <summary>
    /// 指定された探索範囲を使用してDLLをロードします。
    /// </summary>
    public nint Load(string path) => load(path);

    private static nint LoadScriptLibrary(string path)
    {
        // 絶対パスの読込みでも探索フラグを確実に渡すため、OSを直接呼びます。
        nint module = LoadLibraryExW(path, 0,
            DllImportSearchPath.UseDllDirectoryForDependencies
            | DllImportSearchPath.System32);
        if (module != 0)
        {
            return module;
        }

        int code = Marshal.GetLastPInvokeError();
        var error = new Win32Exception(code);
        string message = $"DLLを読み込めません: {path} ({error.Message})";
        if (code == ErrorBadExeFormat)
        {
            if (RuntimeInformation.ProcessArchitecture == Architecture.X64
                && NativeDllImage.IsNativeX86Dll(path))
            {
                message = $"32ビットのネイティブDLLは64ビット実行環境では呼び出せません: {path}";
            }

            throw new BadImageFormatException(message, error);
        }

        throw new DllNotFoundException(message, error);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
        ExactSpelling = true, SetLastError = true)]
    private static extern nint LoadLibraryExW(string path, nint file,
        DllImportSearchPath flags);

    /// <summary>
    /// 名前でexportのアドレスを取得します。
    /// </summary>
    public nint GetExport(nint library, string export) => NativeLibrary.GetExport(library, export);

    /// <summary>
    /// 所有するロード参照を解放します。
    /// </summary>
    public void Free(nint library) => NativeLibrary.Free(library);
}
