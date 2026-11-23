using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace UWSCSharp.Windows;

internal sealed partial class NativeToolbarClickBuffer
{

    private readonly NativeControlBufferOwner owner;
    private readonly byte[] item = new byte[1024];
    private readonly byte[] text = new byte[1024];

    internal NativeToolbarClickBuffer(NativeControlBufferOwner owner)
    {
        this.owner = owner;
    }

    internal nint Root => owner.Root;

    internal bool IsShellRoot()
    {
        var name = new StringBuilder(256);
        Native.GetClassNameW(owner.Root, name, name.Capacity);
        return NativeListNameMatcher.AsciiUpper(name.ToString()) == "SHELL_TRAYWND";
    }

    internal ReadOnlySpan<byte> ReadButton(nint target, int index,
        CancellationToken token)
    {
        nint address = owner.Acquire().Address;
        Message(target, 0x417, index, address, token);
        owner.ReadRetainingBytes(item);
        token.ThrowIfCancellationRequested();
        return item;
    }

    internal string ReadText(nint target, int command, CancellationToken token)
    {
        nint length = Message(target, 0x44b, command, 0, token);
        if (length > 65534)
        {
            throw WindowsOperationErrors.Record(
                new NotSupportedException("TOOLBAR文字列の取得容量を超えています。"));
        }

        if (length >= NativeControlBufferOwner.Capacity / 2)
        {
            // 容量差は退避で扱い、共有baseに残る既知prefixを引き継ぎます。
            using var large = new NativeControlTextBuffer(target, ControlTextKind.Toolbar);
            large.Prepare();
            byte[] retained = new byte[NativeControlBufferOwner.Capacity];
            owner.ReadRetainingBytes(retained);
            large.WriteTextBytesRetaining(retained);
            try
            {
                Message(target, 0x44b, command, large.TextAddress, token);
            }
            finally
            {
                // 同期getter中に取消されても、完了済みの書込みを共有baseへ反映します。
                large.ReadTextBytesRetaining(retained);
                owner.WriteRetainingBytes(retained);
            }
        }
        else
        {
            Message(target, 0x44b, command, owner.Acquire().Address, token);
        }

        owner.ReadRetainingBytes(text);
        token.ThrowIfCancellationRequested();
        ReadOnlySpan<char> value = MemoryMarshal.Cast<byte, char>(text);
        int end = value.IndexOf('\0');
        return new string(end < 0 ? value : value[..end]);
    }

    internal (nint Result, Native.Rect Rectangle) ReadRectangle(nint target,
        int index, CancellationToken token)
    {
        nint address = owner.Acquire().Address;
        nint result = Message(target, 0x41d, index, address, token, ansi: true);
        owner.ReadRetainingBytes(item);
        token.ThrowIfCancellationRequested();
        var rectangle = new Native.Rect
        {
            Left = BinaryPrimitives.ReadInt32LittleEndian(item),
            Top = BinaryPrimitives.ReadInt32LittleEndian(item.AsSpan(4)),
            Right = BinaryPrimitives.ReadInt32LittleEndian(item.AsSpan(8)),
            Bottom = BinaryPrimitives.ReadInt32LittleEndian(item.AsSpan(12)),
        };
        return (result, rectangle);
    }

    private static nint Message(nint target, uint message, nint parameter,
        nint pointer, CancellationToken token, bool ansi = false)
    {
        token.ThrowIfCancellationRequested();
        nint result = ansi ? SendMessageA(target, message, parameter, pointer)
            : Native.SendMessageW(target, message, parameter, pointer);
        token.ThrowIfCancellationRequested();
        if (!Native.IsWindow(target))
        {
            throw WindowsOperationErrors.Record(
                new InvalidOperationException("TOOLBARが取得中に破棄されました。"));
        }

        return result;
    }
}
