using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal interface IButtonAccessibleLibrary
{
    nint Load();

    nint Export(nint library, string name);

    void Free(nint library);
}

/// <summary>
/// 受け手の寿命でoleaccと解決済みの関数を保持します。
/// </summary>
internal sealed class NativeButtonAccessibleSource(
    IButtonAccessibleLibrary? loader = null) : IButtonAccessibleSource, IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int PointCall(Native.Point point, out nint result,
        ref NativeButtonVariant child);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int WindowCall(nint root, uint objectId,
        ref Guid id, out nint result);

    private readonly IButtonAccessibleLibrary loader = loader ?? new Library();
    private nint library;
    private PointCall? point;
    private WindowCall? window;
    private bool disposed;

    public bool Available()
    {
        Initialize();
        return point is not null;
    }

    public int FromPoint(Native.Point position, out nint result,
        ref NativeButtonVariant child)
    {
        Initialize();
        PointCall call = point ?? throw new EntryPointNotFoundException(
            "AccessibleObjectFromPointがありません。");
        return call(position, out result, ref child);
    }

    public int FromWindow(nint root, out nint result)
    {
        Initialize();
        WindowCall call = window ?? throw new EntryPointNotFoundException(
            "AccessibleObjectFromWindowがありません。");
        Guid id = NativeButtonAccessibleApi.AccessibleId;
        return call(root, 0xfffffffcu, ref id, out result);
    }

    private void Initialize()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (library != 0)
        {
            return;
        }

        library = loader.Load();
        if (library == 0)
        {
            return;
        }

        nint address = loader.Export(library, "AccessibleObjectFromPoint");
        if (address != 0)
        {
            point = Marshal.GetDelegateForFunctionPointer<PointCall>(address);
        }

        address = loader.Export(library, "AccessibleObjectFromWindow");
        if (address != 0)
        {
            window = Marshal.GetDelegateForFunctionPointer<WindowCall>(address);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        point = null;
        window = null;
        nint previous = library;
        library = 0;
        if (previous != 0)
        {
            loader.Free(previous);
        }
    }

    private sealed class Library : IButtonAccessibleLibrary
    {
        public nint Load()
            => NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory,
                "oleacc.dll"), out nint result) ? result : 0;

        public nint Export(nint library, string name)
            => NativeLibrary.TryGetExport(library, name, out nint result) ? result : 0;

        public void Free(nint library) => NativeLibrary.Free(library);
    }
}
