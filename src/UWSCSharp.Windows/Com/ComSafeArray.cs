using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

internal sealed record ComArrayValue(ushort ElementType, int[] LowerBounds,
    int[] UpperBounds, IReadOnlyList<object?> Values);

/// <summary>
/// SAFEARRAYの境界・型・要素tagを保持してコピーします。
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class ComSafeArray
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Bound
    {
        internal uint Count;
        internal int Lower;
    }

    /// <summary>
    /// 各要素が初期値のSAFEARRAYを作成し、型と境界を保持して読み取ります。
    /// </summary>
    internal static ComVariantValue Create(ushort type,
        int[] lower, int[] upper, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        _ = Size(type);
        _ = ComOperationErrors.Validate(() =>
            UwscArray.ValidateBounds(lower, upper));
        nint variant = Allocate(IntPtr.Size == 8 ? 24 : 16);
        Exception? primary = null;
        try
        {
            Bound[] bounds = [.. Enumerable.Range(0, lower.Length)
                .Select(i => new Bound
                {
                    Count = (uint)((long)upper[i] - lower[i] + 1),
                    Lower = lower[i]
                })];
            nint array = SafeArrayCreate(type, (uint)bounds.Length, bounds);
            if (array == 0)
            {
                throw new OutOfMemoryException("SAFEARRAYを作成できません。");
            }

            Marshal.WriteIntPtr(variant, 8, array);
            Marshal.WriteInt16(variant, unchecked((short)(0x2000 | type)));
            return ComVariantValue.Read(variant, token: token);
        }
        catch (Exception ex)
        {
            primary = ex;
            throw;
        }
        finally
        {
            try
            {
                ClearAndFree(variant);
            }
            catch (Exception ex) when (primary is not null)
            {
                primary.Data["UWSCSharp.ComArrayCleanupErrors"] = ex;
            }
        }
    }

    internal static ComArrayValue Read(nint array, ushort type, int depth,
        ComVariantValue.ReadContext context)
    {
        if (array == 0)
        {
            throw ComOperationErrors.Record(
                new InvalidOperationException("SAFEARRAYのpointerがありません。"));
        }

        int rank = checked((int)SafeArrayGetDim(array));
        if (rank is < 1 or > 16 || SafeArrayGetElemsize(array) != Size(type))
        {
            throw ComOperationErrors.Record(
                new InvalidOperationException("SAFEARRAYの形状または要素型が不正です。"));
        }

        if (SafeArrayGetVartype(array, out ushort actual) == 0 && actual != type)
        {
            throw ComOperationErrors.Record(
                new InvalidOperationException("SAFEARRAYの要素型が一致しません。"));
        }

        int[] lower = new int[rank];
        int[] upper = new int[rank];
        for (int i = 0; i < rank; i++)
        {
            ComOperationErrors.ThrowIfFailed(SafeArrayGetLBound(array, (uint)i + 1, out lower[i]));
            ComOperationErrors.ThrowIfFailed(SafeArrayGetUBound(array, (uint)i + 1, out upper[i]));
        }

        int count = ComOperationErrors.Validate(() => UwscArray.ValidateBounds(lower, upper));
        context.Budget.Reserve(count);
        object?[] values = new object?[count];
        int[] indices = (int[])lower.Clone();
        int stride = IntPtr.Size == 8 ? 24 : 16;
        nint cell = Marshal.AllocCoTaskMem(stride);
        try
        {
            for (int i = 0; i < count; i++)
            {
                context.Token.ThrowIfCancellationRequested();
                if (type == 8)
                {
                    values[i] = ReadStringElement(array, indices);
                    Increment(indices, lower, upper);
                    continue;
                }

                Marshal.Copy(new byte[stride], 0, cell, stride);
                Exception? primary = null;
                try
                {
                    nint destination = type is 12 or 14 ? cell : cell + 8;
                    ComOperationErrors.ThrowIfFailed(SafeArrayGetElement(array, indices, destination));
                    if (type != 12)
                    {
                        Marshal.WriteInt16(cell, unchecked((short)type));
                    }

                    values[i] = ComVariantValue.ReadCore(cell, depth, context);
                }
                catch (Exception ex)
                {
                    primary = ex;
                    throw;
                }
                finally
                {
                    try
                    {
                        Marshal.ThrowExceptionForHR(Native.VariantClear(cell));
                    }
                    catch (Exception ex) when (primary is not null)
                    {
                        primary.Data["UWSCSharp.ComArrayCleanupErrors"] = ex;
                    }
                }

                Increment(indices, lower, upper);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(cell);
        }

        return new(type, lower, upper, Array.AsReadOnly(values));
    }

    private static ComVariantValue ReadStringElement(nint array, int[] indices)
    {
        ComOperationErrors.ThrowIfFailed(SafeArrayLock(array));
        Exception? primary = null;
        try
        {
            // GetElementのBSTR複製でnullが空文字列になるため、格納値を読みます。
            ComOperationErrors.ThrowIfFailed(
                SafeArrayPtrOfIndex(array, indices, out nint element));
            nint text = Marshal.ReadIntPtr(element);
            return new(8, text == 0 ? null : Marshal.PtrToStringBSTR(text));
        }
        catch (Exception ex)
        {
            primary = ex;
            throw;
        }
        finally
        {
            try
            {
                ComOperationErrors.ThrowIfFailed(SafeArrayUnlock(array));
            }
            catch (Exception ex) when (primary is not null)
            {
                primary.Data["UWSCSharp.ComArrayCleanupErrors"] = ex;
            }
        }
    }

    internal static nint Write(ComArrayValue value)
    {
        _ = Size(value.ElementType);
        int count = ComOperationErrors.Validate(() =>
            UwscArray.ValidateBounds(value.LowerBounds, value.UpperBounds));
        if (count != value.Values.Count)
        {
            throw ComOperationErrors.Record(
                new InvalidOperationException("SAFEARRAYの要素数が一致しません。"));
        }

        Bound[] bounds = [.. Enumerable.Range(0, value.LowerBounds.Length).Select(i =>
            new Bound { Count = (uint)(value.UpperBounds[i] - value.LowerBounds[i] + 1), Lower = value.LowerBounds[i] })];
        nint array = SafeArrayCreate(value.ElementType, (uint)bounds.Length, bounds);
        if (array == 0)
        {
            throw new OutOfMemoryException("SAFEARRAYを作成できません。");
        }

        int stride = IntPtr.Size == 8 ? 24 : 16;
        nint cell = 0;
        nint converted = 0;
        Exception? primary = null;
        bool completed = false;
        try
        {
            cell = Allocate(stride);
            converted = Allocate(stride);
            int[] indices = (int[])value.LowerBounds.Clone();
            foreach (object? element in value.Values)
            {
                Marshal.ThrowExceptionForHR(Native.VariantClear(cell));
                Marshal.ThrowExceptionForHR(Native.VariantClear(converted));
                ComVariantValue.Write(element, cell);
                nint source = ConvertCell(value.ElementType, cell, converted);

                nint data = value.ElementType switch
                {
                    8 or 9 or 13 => Marshal.ReadIntPtr(source, 8),
                    12 or 14 => source,
                    _ => source + 8
                };
                if (value.ElementType == 14)
                {
                    Marshal.WriteInt16(source, 0);
                }

                try
                {
                    ComOperationErrors.ThrowIfFailed(SafeArrayPutElement(array, indices, data));
                }
                finally
                {
                    if (value.ElementType == 14)
                    {
                        Marshal.WriteInt16(source, 14);
                    }
                }

                Increment(indices, value.LowerBounds, value.UpperBounds);
            }

            completed = true;
        }
        catch (Exception ex)
        {
            primary = ex;
            throw;
        }
        finally
        {
            var errors = new List<Exception>();
            foreach (nint memory in new[] { cell, converted })
            {
                try
                {
                    ClearAndFree(memory);
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }

            if (!completed || errors.Count > 0)
            {
                try
                {
                    Marshal.ThrowExceptionForHR(SafeArrayDestroy(array));
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }

            if (errors.Count > 0)
            {
                var cleanup = new AggregateException("SAFEARRAYの解放に失敗しました。", errors);
                if (primary is not null)
                {
                    primary.Data["UWSCSharp.ComArrayCleanupErrors"] = cleanup;
                }
                else
                {
                    throw cleanup;
                }
            }
        }

        return array;
    }

    /// <summary>
    /// COM送信と同じ規則で単要素を変換し、返却値の参照所有を渡します。
    /// </summary>
    internal static ComVariantValue ConvertElement(object? value, ushort type,
        ushort flags = 0, bool allowVariantPassthrough = true)
    {
        _ = Size(type);
        int stride = IntPtr.Size == 8 ? 24 : 16;
        nint cell = 0;
        nint converted = 0;
        ComVariantValue? result = null;
        Exception? primary = null;
        try
        {
            cell = Allocate(stride);
            converted = Allocate(stride);
            ComVariantValue.Write(value, cell);
            result = ComVariantValue.Read(ConvertCell(type, cell, converted,
                flags, allowVariantPassthrough));
        }
        catch (Exception ex)
        {
            primary = ex;
            throw;
        }
        finally
        {
            var errors = new List<Exception>();
            foreach (nint memory in new[] { cell, converted })
            {
                try
                {
                    ClearAndFree(memory);
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }

            if (errors.Count > 0)
            {
                var cleanup = new AggregateException("要素変換の解放に失敗しました。", errors);
                if (primary is not null)
                {
                    primary.Data["UWSCSharp.ComArrayCleanupErrors"] = cleanup;
                }
                else
                {
                    // 返却できない場合は、読取りで取得したRCWも回収します。
                    if (result is ComVariantValue retained)
                    {
                        retained.ReleaseAcquisitions(cleanup);
                    }
                    throw cleanup;
                }
            }
        }

        return result!.Value;
    }

    internal static nint ConvertCell(ushort type, nint cell, nint converted,
        ushort flags = 0, bool allowVariantPassthrough = true)
    {
        if ((type == 12 && allowVariantPassthrough) || Marshal.ReadInt16(cell) == type)
        {
            return cell;
        }

        if (Marshal.ReadInt16(cell) == 0 && type is 9 or 10 or 13)
        {
            ComVariantValue.Write(new ComVariantValue(type,
                type == 10 ? new ErrorWrapper(0) : null), converted);
        }
        else
        {
            ComOperationErrors.ThrowIfFailed(VariantChangeTypeEx(converted, cell,
                (uint)CultureInfo.CurrentCulture.LCID, flags, type));
        }

        return converted;
    }

    private static uint Size(ushort type) => type switch
    {
        16 or 17 => 1,
        2 or 11 or 18 => 2,
        3 or 4 or 10 or 19 or 22 or 23 => 4,
        5 or 6 or 7 or 20 or 21 => 8,
        8 or 9 or 13 => (uint)IntPtr.Size,
        12 => IntPtr.Size == 8 ? 24u : 16u,
        14 => 16,
        _ => throw ComOperationErrors.Record(new NotSupportedException("SAFEARRAYの要素型には未対応です。"))
    };

    private static void Increment(int[] indices, IReadOnlyList<int> lower, IReadOnlyList<int> upper)
    {
        for (int i = indices.Length - 1; i >= 0; i--)
        {
            if (indices[i] < upper[i])
            {
                indices[i]++;
                return;
            }

            indices[i] = lower[i];
        }
    }

    private static nint Allocate(int size)
    {
        byte[] zero = new byte[size];
        nint memory = Marshal.AllocCoTaskMem(size);
        try
        {
            Marshal.Copy(zero, 0, memory, size);
            return memory;
        }
        catch
        {
            Marshal.FreeCoTaskMem(memory);
            throw;
        }
    }

    private static void ClearAndFree(nint memory)
    {
        if (memory == 0)
        {
            return;
        }

        try
        {
            Marshal.ThrowExceptionForHR(Native.VariantClear(memory));
        }
        finally
        {
            Marshal.FreeCoTaskMem(memory);
        }
    }
}
