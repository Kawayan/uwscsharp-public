using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// 同期通知中だけ有効な引数を読み、参照先へ準備済みの値を反映します。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class NativeComEventArguments
{
    private static readonly int Stride = IntPtr.Size == 8 ? 24 : 16;
    private readonly nint arguments;
    private readonly (nint Address, ushort Type)[] references;

    internal NativeComEventArguments(in DISPPARAMS parameters)
    {
        if (parameters.cArgs < 0 || parameters.cArgs > UwscArray.MaximumElements
            || (parameters.cArgs != 0 && parameters.rgvarg == 0)
            || parameters.cNamedArgs != 0)
        {
            throw ComOperationErrors.Record(new InvalidOperationException(
                "COMイベントの引数領域または名前付き引数に対応していません。"));
        }

        arguments = parameters.rgvarg;
        references = new (nint, ushort)[parameters.cArgs];
        for (int index = 0; index < Count; index++)
        {
            nint cell = Cell(index);
            ushort type = unchecked((ushort)Marshal.ReadInt16(cell));
            if ((type & 0x4000) == 0)
            {
                continue;
            }

            nint address = Marshal.ReadIntPtr(cell, 8);
            ushort targetType = (ushort)(type & ~0x4000);
            _ = PayloadSize(targetType);
            if (address == 0)
            {
                throw ComOperationErrors.Record(new InvalidOperationException(
                    "COMイベントの参照引数がnullです。"));
            }

            references[index] = (address, targetType);
        }
    }

    internal int Count => references.Length;

    internal bool IsByReference(int index) => references[index].Address != 0;

    internal ComVariantValue Read(int index, CancellationToken token = default,
        ComValueBudget? budget = null) =>
        ComVariantValue.Read(Cell(index), token: token, budget: budget);

    private nint Cell(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
        return arguments + checked((Count - index - 1) * Stride);
    }

    /// <summary>
    /// 全参照引数の変換後に書き戻し、値渡し引数の変更は反映しません。
    /// </summary>
    internal void WriteBack(IReadOnlyList<object?> values,
        CancellationToken token = default)
    {
        if (values.Count != Count)
        {
            throw new InvalidOperationException(
                "COMイベントの引数数は変更できません。");
        }

        nint prepared = 0;
        nint source = 0;
        nint previous = 0;
        Exception? primary = null;
        try
        {
            prepared = Allocate(checked(Count * Stride));
            source = Allocate(Stride);
            previous = Allocate(Stride);
            for (int index = 0; index < Count; index++)
            {
                token.ThrowIfCancellationRequested();
                if (!IsByReference(index))
                {
                    continue;
                }

                ushort type = references[index].Type;
                nint destination = prepared + index * Stride;
                if ((type & 0x2000) != 0)
                {
                    object? value = values[index] is ComVariantValue tagged
                        ? tagged.Value : values[index];
                    if (value is not ComArrayValue array)
                    {
                        throw ComOperationErrors.Record(new InvalidOperationException(
                            "COMイベントの配列参照には配列を指定してください。"));
                    }

                    ComVariantValue.Write(array with
                    {
                        ElementType = (ushort)(type & 0xfff)
                    }, destination);
                    continue;
                }

                ComOperationErrors.ThrowIfFailed(Native.VariantClear(source));
                ComVariantValue.Write(values[index], source);
                nint converted = ComSafeArray.ConvertCell(type, source, destination);
                if (converted == source)
                {
                    ComOperationErrors.ThrowIfFailed(VariantCopy(destination, source));
                }
            }

            token.ThrowIfCancellationRequested();
            byte[] incoming = new byte[Stride];
            byte[] outgoing = new byte[Stride];
            for (int index = 0; index < Count; index++)
            {
                if (IsByReference(index))
                {
                    Commit(references[index], prepared + index * Stride,
                        previous, incoming, outgoing);
                }
            }
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            List<Exception> errors = [];
            ClearAndFree(prepared, Count, errors);
            ClearAndFree(source, 1, errors);
            // previousは、解放失敗時には借用元の所有のままです。
            Marshal.FreeCoTaskMem(previous);
            if (errors.Count > 0)
            {
                var cleanup = new AggregateException(
                    "COMイベントの書戻し準備の解放に失敗しました。", errors);
                if (primary is not null)
                {
                    primary.Data["UWSCSharp.ComEventArgumentCleanupErrors"] = cleanup;
                }
                else
                {
                    throw cleanup;
                }
            }
        }
    }

    private static void Commit((nint Address, ushort Type) reference,
        nint prepared, nint previous, byte[] incoming, byte[] outgoing)
    {
        int size = PayloadSize(reference.Type);
        bool wholeVariant = reference.Type == 12;
        int offset = reference.Type is 12 or 14 ? 0 : 8;
        Marshal.Copy(prepared + offset, incoming, 0, size);
        Marshal.Copy(reference.Address, outgoing, 0, size);
        Marshal.WriteInt16(previous, 0);
        Marshal.Copy(outgoing, 0, previous + offset, size);
        if (!wholeVariant)
        {
            Marshal.WriteInt16(previous, unchecked((short)reference.Type));
        }

        // 別の引数と参照先が同じ場合も、直前の値を一度だけ解放します。
        ComOperationErrors.ThrowIfFailed(Native.VariantClear(previous));
        if (reference.Type == 14)
        {
            incoming[0] = 0;
            incoming[1] = 0;
        }

        Marshal.Copy(incoming, 0, reference.Address, size);
        // 参照先へ移したBSTR・配列・interfaceの所有を準備領域から外します。
        Marshal.WriteInt16(prepared, 0);
    }

    private static int PayloadSize(ushort type) => type switch
    {
        16 or 17 => 1,
        2 or 11 or 18 => 2,
        3 or 4 or 10 or 19 or 22 or 23 => 4,
        5 or 6 or 7 or 20 or 21 => 8,
        8 or 9 or 13 => IntPtr.Size,
        12 => Stride,
        14 => 16,
        _ when (type & 0xf000) == 0x2000 => IntPtr.Size,
        _ => throw ComOperationErrors.Record(new NotSupportedException(
            "COMイベントの参照引数の型には対応していません。"))
    };

    private static nint Allocate(int bytes)
    {
        if (bytes == 0)
        {
            return 0;
        }

        nint memory = Marshal.AllocCoTaskMem(bytes);
        try
        {
            Marshal.Copy(new byte[bytes], 0, memory, bytes);
            return memory;
        }
        catch
        {
            Marshal.FreeCoTaskMem(memory);
            throw;
        }
    }

    private static void ClearAndFree(nint memory, int count,
        List<Exception> errors)
    {
        if (memory == 0)
        {
            return;
        }

        try
        {
            for (int index = 0; index < count; index++)
            {
                int result = Native.VariantClear(memory + index * Stride);
                if (result < 0)
                {
                    errors.Add(Marshal.GetExceptionForHR(result)!);
                }
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(memory);
        }
    }
}
