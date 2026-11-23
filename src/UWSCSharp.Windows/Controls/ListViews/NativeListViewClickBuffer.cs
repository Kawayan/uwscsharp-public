using System.Buffers.Binary;
using System.Runtime.InteropServices;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

internal sealed class NativeListViewClickBuffer(NativeControlBufferOwner owner)
{
    private byte[]? transfer;
    private readonly byte[] firstPosition = new byte[8];
    private readonly byte[] selectedPosition = new byte[8];

    internal nint Root => owner.Root;

    internal int RowCount
    {
        get;
        private set;
    }

    internal List<string> ReadNames(nint target, CancellationToken token)
    {
        List<string> result = [];
        VisitNames(target, token, (_, name) =>
        {
            result.Add(name);
            return true;
        }, () => result.Clear());
        return result;
    }

    internal bool VisitNames(nint target, CancellationToken token,
        Func<int, string, bool> visit, Action? beginColumn = null)
    {
        token.ThrowIfCancellationRequested();
        if ((Native.GetWindowLongW(target, -16) & 0x1000) != 0)
        {
            throw WindowsOperationErrors.Record(
                new NotSupportedException("owner dataのLISTVIEWは未対応です。"));
        }

        nint address = owner.Acquire().Address;
        bool wide = owner.Is64Bit;
        transfer ??= new byte[wide ? 0x244 : 0x23c];
        Array.Clear(transfer);
        int characters = 0;
        for (int column = 0; column < 8; column++)
        {
            beginColumn?.Invoke();
            RowCount = WindowsOperationErrors.Int32(Native.SendMessageW(target, 0x1004, 0, 0));
            _ = WindowsOperationErrors.Subtract(RowCount, 1);
            if (RowCount > UwscArray.MaximumElements)
            {
                throw WindowsOperationErrors.Record(
                    new InvalidOperationException("LISTVIEWの項目数が範囲外です。"));
            }

            bool nonempty = false;
            for (int row = 0; row < RowCount; row++)
            {
                token.ThrowIfCancellationRequested();
                BinaryPrimitives.WriteUInt32LittleEndian(transfer.AsSpan(512), 1);
                BinaryPrimitives.WriteInt32LittleEndian(transfer.AsSpan(516), row);
                BinaryPrimitives.WriteInt32LittleEndian(transfer.AsSpan(520), column);
                if (wide)
                {
                    BinaryPrimitives.WriteInt64LittleEndian(transfer.AsSpan(536), (long)address);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(transfer.AsSpan(532), unchecked((uint)address));
                }

                BinaryPrimitives.WriteInt32LittleEndian(transfer.AsSpan(wide ? 544 : 536), 256);
                owner.WriteRetainingBytes(transfer);
                Native.SendMessageW(target, 0x1073, row, address + 512);
                token.ThrowIfCancellationRequested();
                owner.ReadRetainingBytes(transfer);
                token.ThrowIfCancellationRequested();
                string name = Name(transfer);
                nonempty |= name.Length != 0;
                if (name.Length > NativeListControl.MaximumCharacters - characters)
                {
                    throw WindowsOperationErrors.Record(
                        new InvalidOperationException("LISTVIEWの文字列取得上限を超えました。"));
                }

                characters += name.Length;
                if (!visit(row, name))
                {
                    return false;
                }
            }

            if (nonempty)
            {
                break;
            }
        }

        return true;
    }

    internal bool SetState(nint target, int row, int state,
        CancellationToken token, uint stateMask = 3, int? column = null,
        Func<bool>? suppressClear = null)
    {
        token.ThrowIfCancellationRequested();
        byte[] bytes = transfer ?? throw new InvalidOperationException("名前取得前の状態転送です。");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(512), 8);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(516), row);
        if (column.HasValue)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(520), column.Value);
        }

        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(524), state);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(528), stateMask);
        owner.WriteRetainingBytes(bytes);
        bool suppress = suppressClear?.Invoke() ?? false;
        token.ThrowIfCancellationRequested();
        if (state == 0 && suppress)
        {
            return true;
        }

        nint result = Native.SendMessageW(target, 0x102b, row, owner.Acquire().Address + 512);
        token.ThrowIfCancellationRequested();
        return result != 0 && Native.IsWindow(target);
    }

    internal Native.Point ReadFirstPosition(nint target, CancellationToken token) =>
        ReadPosition(target, 0, firstPosition, token);

    internal Native.Point ReadSelectedPosition(nint target, int row, CancellationToken token) =>
        ReadPosition(target, row, selectedPosition, token);

    private Native.Point ReadPosition(nint target, int row, byte[] bytes,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Native.SendMessageW(target, 0x1010, row, owner.Acquire().Address);
        token.ThrowIfCancellationRequested();
        owner.ReadRetainingBytes(bytes);
        token.ThrowIfCancellationRequested();
        return new Native.Point(BinaryPrimitives.ReadInt32LittleEndian(bytes),
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)));
    }

    private static string Name(byte[] bytes)
    {
        ReadOnlySpan<char> text = MemoryMarshal.Cast<byte, char>(bytes.AsSpan(0, 512));
        int end = text.IndexOf('\0');
        return NativeListControl.Trim(new string(end < 0 ? text : text[..end]));
    }
}
