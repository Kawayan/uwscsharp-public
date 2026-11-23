using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UWSCSharp.Windows;

/// <summary>
/// CLR変換で失われるnative VARIANTの型を保持します。
/// </summary>
[SupportedOSPlatform("windows")]
internal readonly record struct ComVariantValue(ushort Type, object? Value)
{
    // 送受信する値の型とは別に、照会用のBYREF型を保持します。
    internal ushort? ReferenceType
    {
        get; init;
    }

    internal IReadOnlyList<object>? Acquisitions
    {
        get; init;
    }

    /// <summary>
    /// 返却値を渡せなかった場合に、取得済みのRCWを解放します。
    /// </summary>
    internal void ReleaseAcquisitions(Exception primary)
    {
        if (Acquisitions is null)
        {
            return;
        }

        var errors = new List<Exception>();
        for (int i = Acquisitions.Count - 1; i >= 0; i--)
        {
            try
            {
                Marshal.ReleaseComObject(Acquisitions[i]);
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }

        if (errors.Count > 0)
        {
            primary.Data["UWSCSharp.ComReadCleanupErrors"] = new AggregateException(errors);
        }
    }

    internal static ComVariantValue Read(nint variant, int depth = 0,
        CancellationToken token = default, ComValueBudget? budget = null)
    {
        var context = new ReadContext(token, budget);
        try
        {
            return ReadCore(variant, depth, context) with
            {
                Acquisitions = context.Owned.AsReadOnly()
            };
        }
        catch (Exception primary)
        {
            var errors = new List<Exception>();
            for (int i = context.Owned.Count - 1; i >= 0; i--)
            {
                try
                {
                    Marshal.ReleaseComObject(context.Owned[i]);
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }

            if (errors.Count > 0)
            {
                primary.Data["UWSCSharp.ComReadCleanupErrors"] = new AggregateException(errors);
            }

            throw;
        }
    }

    internal sealed class ReadContext
    {
        internal readonly List<object> Owned = [];
        internal readonly ComValueBudget Budget;

        internal ReadContext(CancellationToken token = default,
            ComValueBudget? budget = null)
        {
            Token = token;
            Budget = budget ?? new ComValueBudget();
        }

        internal CancellationToken Token
        {
            get;
        }

        internal object? GetObject(nint variant)
        {
            object? value = ComOperationErrors.Validate(() =>
                Marshal.GetObjectForNativeVariant(variant));
            if (value is not null && Marshal.IsComObject(value))
            {
                try
                {
                    Owned.Add(value);
                }
                catch
                {
                    Marshal.ReleaseComObject(value);
                    throw;
                }
            }

            return value;
        }
    }

    internal static ComVariantValue ReadCore(nint variant, int depth, ReadContext context)
    {
        context.Token.ThrowIfCancellationRequested();
        if (depth > 128)
        {
            throw ComOperationErrors.Record(new InvalidOperationException("返却VARIANTの入れ子が深すぎます。"));
        }

        ushort type = unchecked((ushort)Marshal.ReadInt16(variant));
        if (type == 0x400c)
        {
            return ReadCore(Marshal.ReadIntPtr(variant, 8), depth + 1, context) with
            {
                ReferenceType = type
            };
        }

        ushort scalar = (ushort)(type & ~0x4000);
        if ((scalar & 0x2000) != 0)
        {
            nint array = Marshal.ReadIntPtr(variant, 8);
            if ((type & 0x4000) != 0)
            {
                array = Marshal.ReadIntPtr(array);
            }

            return new(scalar, ComSafeArray.Read(array, (ushort)(scalar & 0xfff), depth + 1, context))
            {
                ReferenceType = (type & 0x4000) != 0 && scalar != 0x200c ? type : null
            };
        }

        object? value = scalar switch
        {
            0 => null,
            1 => DBNull.Value,
            7 => ReadDate(variant, type),
            8 => ReadString(variant, type),
            10 when (type & 0x4000) == 0 => new ErrorWrapper(Marshal.ReadInt32(variant, 8)),
            10 => new ErrorWrapper(Marshal.ReadInt32(Marshal.ReadIntPtr(variant, 8))),
            _ => context.GetObject(variant)
        };
        return new(scalar, value)
        {
            ReferenceType = (type & 0x4000) != 0 ? type : null
        };
    }

    internal static void Write(object? value, nint destination)
    {
        if (value is ComArrayValue array)
        {
            nint pointer = ComSafeArray.Write(array);
            Marshal.WriteIntPtr(destination, 8, pointer);
            Marshal.WriteInt16(destination, unchecked((short)(0x2000 | array.ElementType)));
            return;
        }

        if (value is not ComVariantValue typed)
        {
            ComOperationErrors.Validate(() =>
            {
                Marshal.GetNativeVariantForObject(value, destination);
                return true;
            });
            return;
        }

        if (typed.Type == 7)
        {
            double date = typed.Value is DateTime time
                ? ComOperationErrors.Validate(() => time.ToOADate()) : (double)typed.Value!;
            _ = ComOperationErrors.Validate(() => DateTime.FromOADate(date));
            Marshal.WriteInt64(destination, 8, BitConverter.DoubleToInt64Bits(date));
            Marshal.WriteInt16(destination, 7);
            return;
        }

        if (typed.Type == 6)
        {
            long units = ComOperationErrors.Validate(() =>
                decimal.ToInt64((decimal)typed.Value! * 10000m));
            Marshal.WriteInt64(destination, 8, units);
            Marshal.WriteInt16(destination, 6);
            return;
        }

        if (typed.Type == 8 && typed.Value is null)
        {
            Marshal.WriteIntPtr(destination, 8, 0);
            Marshal.WriteInt16(destination, 8);
            return;
        }

        object? converted = typed.Type switch
        {
            9 => new DispatchWrapper(typed.Value),
            10 => typed.Value is ErrorWrapper error ? error : new ErrorWrapper(typed.Value!),
            13 => new UnknownWrapper(typed.Value),
            _ => typed.Value
        };
        ComOperationErrors.Validate(() =>
        {
            Marshal.GetNativeVariantForObject(converted, destination);
            return true;
        });
        if (typed.Type is 22 or 23)
        {
            Marshal.WriteInt16(destination, unchecked((short)typed.Type));
        }
    }

    private static string? ReadString(nint variant, ushort type)
    {
        nint pointer = Marshal.ReadIntPtr(variant, 8);
        if ((type & 0x4000) != 0)
        {
            pointer = Marshal.ReadIntPtr(pointer);
        }

        return pointer == 0 ? null : Marshal.PtrToStringBSTR(pointer);
    }

    private static double ReadDate(nint variant, ushort type)
    {
        nint address = (type & 0x4000) == 0 ? variant + 8 : Marshal.ReadIntPtr(variant, 8);
        double date = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(address));
        _ = ComOperationErrors.Validate(() => DateTime.FromOADate(date));
        return date;
    }
}
