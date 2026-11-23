using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal enum ControlTextKind
{
    Tab,
    Tree,
    ListView,
    Column,
    Status,
    Toolbar
}

internal sealed class NativeControlTextBuffer : IDisposable
{
    private readonly nint process;
    private readonly nint address;
    private readonly byte[] bytes;
    private readonly int textBytes;
    private readonly bool wide;
    private readonly ControlTextKind kind;
    private int disposed;

    internal nint ItemAddress
    {
        get
        {
            ThrowIfDisposed();
            return address + textBytes;
        }
    }

    internal nint TextAddress
    {
        get
        {
            ThrowIfDisposed();
            return address;
        }
    }

    internal NativeControlTextBuffer(nint target, ControlTextKind kind = ControlTextKind.Tab)
    {
        Native.GetWindowThreadProcessId(target, out uint id);
        this.kind = kind;
        textBytes = kind is ControlTextKind.Status or ControlTextKind.Toolbar ? 131072 : 512;
        wide = IntPtr.Size == 8;
        if (id != Environment.ProcessId)
        {
            process = Native.OpenProcess(0x1038, false, id);
            if (process == 0)
            {
                throw WindowsOperationErrors.Record(
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }

            try
            {
                if (!Native.IsWow64Process2(process, out ushort machine,
                    out ushort native))
                {
                    throw WindowsOperationErrors.Record(
                        new Win32Exception(Marshal.GetLastWin32Error()));
                }

                wide = machine == 0 && native == 0x8664;
                if (!(wide || machine == 0x14c || (machine == 0 && native == 0x14c))
                    || (wide && IntPtr.Size != 8))
                {
                    throw WindowsOperationErrors.Record(
                        new NotSupportedException("TABの対象bitnessは未対応です。"));
                }

                bytes = new byte[Size(kind, wide)];
                address = Native.VirtualAllocEx(process, 0, (nuint)Size(kind, wide),
                    0x3000, 4);
                if (address == 0)
                {
                    throw WindowsOperationErrors.Record(
                        new Win32Exception(Marshal.GetLastWin32Error()));
                }
            }
            catch
            {
                Native.CloseHandle(process);
                throw;
            }
        }
        else
        {
            bytes = new byte[Size(kind, wide)];
            address = Marshal.AllocHGlobal(Size(kind, wide));
        }

        if (kind is ControlTextKind.Status or ControlTextKind.Toolbar)
        {
            return;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(512),
            kind == ControlTextKind.Column ? 4u : 1u);
        int textOffset = kind switch
        {
            ControlTextKind.Tree or ControlTextKind.ListView => wide ? 24 : 16,
            _ => wide ? 16 : 12
        };
        if (kind == ControlTextKind.ListView && !wide)
        {
            textOffset = 20;
        }

        if (wide)
        {
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(512 + textOffset), (long)address);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(512 + textOffset), unchecked((uint)address));
        }

        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(512 + textOffset + (wide ? 8 : 4)), 256);
    }

    private static int Size(ControlTextKind kind, bool wide) =>
        kind == ControlTextKind.Status ? 131072
            : kind == ControlTextKind.Toolbar ? 131072 + (wide ? 32 : 20)
            : 512 + (kind switch
            {
                ControlTextKind.Tree => wide ? 56 : 40,
                ControlTextKind.ListView => wide ? 88 : 60,
                ControlTextKind.Column => wide ? 56 : 44,
                _ => wide ? 48 : 28
            });

    internal void Prepare(nint item = 0, int column = 0,
        int state = 0, int stateMask = 0)
    {
        ThrowIfDisposed();
        Array.Clear(bytes, 0, textBytes);
        if (kind == ControlTextKind.Tree)
        {
            if (wide)
            {
                BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(520), (long)item);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(516), unchecked((uint)item));
            }
        }

        if (kind == ControlTextKind.ListView)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(516), checked((int)item));
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(520), column);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(524), state);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(528), stateMask);
        }

        if (process == 0)
        {
            Marshal.Copy(bytes, 0, address, bytes.Length);
        }
        else if (!Native.WriteProcessMemory(process, address, bytes,
            (nuint)bytes.Length, out nuint written) || written != (nuint)bytes.Length)
        {
            throw WindowsOperationErrors.Record(
                new Win32Exception(Marshal.GetLastWin32Error()));
        }
    }

    internal string Read(int characters = -1)
    {
        ThrowIfDisposed();
        int size = characters < 0 ? textBytes : checked(characters * 2);
        if (size > textBytes)
        {
            throw new InvalidOperationException("controlの文字列bufferを超える読取りです。");
        }

        if (process == 0)
        {
            Marshal.Copy(address, bytes, 0, size);
        }
        else if (!Native.ReadProcessMemory(process, address, bytes, (nuint)size, out nuint read)
            || read != (nuint)size)
        {
            throw WindowsOperationErrors.Record(
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        ReadOnlySpan<char> value = MemoryMarshal.Cast<byte, char>(bytes.AsSpan(0, size));
        int end = value.IndexOf('\0');
        return new string(end < 0 ? value : value[..end]);
    }

    internal void ReadTextBytesRetaining(byte[] destination)
    {
        ThrowIfDisposed();
        if (destination.Length > textBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(destination));
        }

        if (process == 0)
        {
            Marshal.Copy(address, destination, 0, destination.Length);
        }
        else
        {
            // 失敗や部分読取りでも呼出し側の既知byteを残します。
            _ = Native.ReadProcessMemory(process, address, destination,
                (nuint)destination.Length, out _);
        }
    }

    /// <summary>
    /// 先頭の構造体を、部分読取りを許さず取得します。
    /// </summary>
    internal byte[] ReadTextPrefix(int size)
    {
        ThrowIfDisposed();
        if (size < 0 || size > textBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        byte[] result = new byte[size];
        if (process == 0)
        {
            Marshal.Copy(address, result, 0, size);
        }
        else if (size != 0 && (!Native.ReadProcessMemory(process, address,
            result, (nuint)size, out nuint read) || read != (nuint)size))
        {
            throw WindowsOperationErrors.Record(
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        return result;
    }

    internal void WriteTextBytesRetaining(byte[] source)
    {
        ThrowIfDisposed();
        if (source.Length > textBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }

        if (process == 0)
        {
            Marshal.Copy(source, 0, address, source.Length);
        }
        else
        {
            // 元の共有領域の既知byteを退避領域へ引き継ぎます。
            _ = Native.WriteProcessMemory(process, address, source,
                (nuint)source.Length, out _);
        }
    }

    internal bool Is64Bit => wide;

    internal byte[] ReadItemBytes()
    {
        ThrowIfDisposed();
        int size = bytes.Length - textBytes;
        byte[] result = new byte[size];
        if (process == 0)
        {
            Marshal.Copy(ItemAddress, result, 0, size);
        }
        else if (!Native.ReadProcessMemory(process, ItemAddress, result, (nuint)size,
            out nuint copied) || copied != (nuint)size)
        {
            throw WindowsOperationErrors.Record(
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        return result;
    }

    internal string ReadStringAt(nint pointer, CancellationToken token)
    {
        ThrowIfDisposed();
        if (((nuint)pointer & 1) != 0)
        {
            throw WindowsOperationErrors.Record(
                new InvalidOperationException("文字列pointerの境界が不正です。"));
        }

        var result = new System.Text.StringBuilder();
        byte[] block = new byte[512];
        nuint cursor = (nuint)pointer;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int page = Environment.SystemPageSize;
            // 終端がpage末尾にあっても、次の未読取りpageまで要求しません。
            int size = Math.Min(block.Length, page - (int)(cursor % (nuint)page));
            if (!Native.ReadProcessMemory(process == 0 ? -1 : process,
                unchecked((nint)cursor), block, (nuint)size, out nuint copied)
                || copied != (nuint)size)
            {
                throw WindowsOperationErrors.Record(
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }

            ReadOnlySpan<char> text = MemoryMarshal.Cast<byte, char>(block.AsSpan(0, size));
            int end = text.IndexOf('\0');
            int length = end < 0 ? text.Length : end;
            if (length > NativeListControl.MaximumCharacters - result.Length)
            {
                throw WindowsOperationErrors.Record(
                    new InvalidOperationException("文字列pointerの読取り上限を超えました。"));
            }

            result.Append(text[..length]);
            if (end >= 0)
            {
                return result.ToString();
            }

            cursor = checked(cursor + (nuint)size);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    }

    /// <summary>
    /// 所有する領域とhandleの解放を一度だけ要求します。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        if (process == 0)
        {
            Marshal.FreeHGlobal(address);
        }
        else
        {
            try
            {
                Native.VirtualFreeEx(process, address, 0, 0x8000);
            }
            finally
            {
                Native.CloseHandle(process);
            }
        }
    }
}
