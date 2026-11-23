using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal interface INativeControlBufferPlatform
{
    nint Open(nint root);

    nint Allocate(nint process);

    void Free(nint process, nint address);

    void Close(nint process);
}

internal sealed class NativeControlBufferPlatform : INativeControlBufferPlatform
{
    public nint Open(nint root)
    {
        Native.GetWindowThreadProcessId(root, out uint process);
        return Native.OpenProcess(0x438, false, process);
    }

    public nint Allocate(nint process) =>
        Native.VirtualAllocEx(process, 0, NativeControlBufferOwner.Capacity, 0x1000, 4);

    public void Free(nint process, nint address) =>
        Native.VirtualFreeEx(process, address, 0, 0x8000);

    public void Close(nint process) => Native.CloseHandle(process);
}

/// <summary>
/// 単一の同期実行経路で借用するroot processの領域を所有します。
/// </summary>
internal sealed class NativeControlBufferOwner : IDisposable
{
    internal const int Capacity = 4096;
    internal nint Root => root;
    private readonly nint root;
    private readonly INativeControlBufferPlatform platform;
    private nint process;
    private nint address;
    private int disposed;
    private bool? wide;

    internal NativeControlBufferOwner(nint root,
        INativeControlBufferPlatform? platform = null)
    {
        this.root = root;
        this.platform = platform ?? new NativeControlBufferPlatform();
    }

    internal (nint Process, nint Address) Acquire()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (address != 0)
        {
            return (process, address);
        }

        nint opened = platform.Open(root);
        if (opened == 0)
        {
            throw WindowsOperationErrors.Record(
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        try
        {
            nint allocated = platform.Allocate(opened);
            if (allocated == 0)
            {
                throw WindowsOperationErrors.Record(
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }

            process = opened;
            address = allocated;
            return (process, address);
        }
        catch
        {
            // 確保失敗時もhandleを失わず、次の候補で再試行できます。
            platform.Close(opened);
            throw;
        }
    }

    internal void ReadRetainingBytes(byte[] destination)
    {
        if (destination.Length > Capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(destination));
        }

        (nint handle, nint pointer) = Acquire();
        // 原本の返値無視を再現し、未書込み部分は呼出し側の既知byteを残します。
        Native.ReadProcessMemory(handle, pointer, destination, (nuint)destination.Length, out _);
    }

    internal void WriteRetainingBytes(byte[] source)
    {
        if (source.Length > Capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }

        (nint handle, nint pointer) = Acquire();
        // 返値だけでは分岐せず、対象側に残った既知byteを次の転送で読みます。
        Native.WriteProcessMemory(handle, pointer, source, (nuint)source.Length, out _);
    }

    internal bool Is64Bit
    {
        get
        {
            nint handle = Acquire().Process;
            if (wide.HasValue)
            {
                return wide.Value;
            }

            if (!Native.IsWow64Process2(handle, out ushort machine, out ushort native))
            {
                throw WindowsOperationErrors.Record(
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }

            bool value = machine == 0 && native == 0x8664;
            if (!(value || machine == 0x14c || (machine == 0 && native == 0x14c))
                || (value && IntPtr.Size != 8))
            {
                throw WindowsOperationErrors.Record(
                    new NotSupportedException("対象processのbitnessは未対応です。"));
            }

            wide = value;
            return value;
        }
    }

    /// <summary>
    /// 借用中の同期APIが復帰した後で、一度だけ領域を解放します。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0 || address == 0)
        {
            return;
        }

        try
        {
            platform.Free(process, address);
        }
        finally
        {
            try
            {
                platform.Close(process);
            }
            finally
            {
                process = 0;
                address = 0;
            }
        }
    }
}
