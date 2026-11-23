using System.Globalization;
using System.Runtime.CompilerServices;

namespace UWSCSharp.Core;

/// <summary>
/// PRINT専用の表記と、実行内の参照番号を管理します。
/// </summary>
internal sealed class PrintFormatter
{
    private readonly ConditionalWeakTable<object, ObjectIdentity> objects = new();
    private long nextObjectId;

    internal string Format(Value value) => value.Kind switch
    {
        ValueKind.Null => "NULL",
        ValueKind.Nothing => "NOTHING",
        ValueKind.Error => "ERROR",
        ValueKind.Number => FormatNumber(value),
        ValueKind.Object when value.ObjectReference is object reference =>
            FormatObject(reference),
        _ => value.Display()
    };

    private static string FormatNumber(Value value)
    {
        _ = value.AsNumber();
        object? scalar = value.GetUnchangedNativeScalar();
        if (value.NativeVariantType == 7 && scalar is double or DateTime)
        {
            DateTime date = scalar is DateTime time
                ? time : DateTime.FromOADate((double)scalar);
            return date.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
                CultureInfo.InvariantCulture);
        }

        if (value.NativeVariantType == 11 && scalar is bool flag)
        {
            return flag ? "True" : "False";
        }

        return scalar switch
        {
            decimal number => number.ToString("G29", CultureInfo.InvariantCulture),
            float number => number.ToString("G", CultureInfo.InvariantCulture),
            double number => number.ToString("G", CultureInfo.InvariantCulture),
            sbyte or byte or short or ushort or int or uint or long or ulong =>
                ((IFormattable)scalar).ToString(null, CultureInfo.InvariantCulture),
            _ => value.Display()
        };
    }

    private string FormatObject(object reference)
    {
        ObjectIdentity identity = objects.GetValue(reference,
            _ => new ObjectIdentity(checked(++nextObjectId)));
        return "OBJECT(" + identity.Id.ToString(CultureInfo.InvariantCulture) + ")";
    }

    // 番号の管理で参照先の寿命を延ばさず、外部のメンバーも呼び出しません。
    private sealed record ObjectIdentity(long Id);
}
