using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// ABI型へ変換済みのフィールドを、通常配置または1バイト境界で配置します。
/// </summary>
internal sealed class NativeDllStructureLayout
{
    private readonly Type[] types;
    private readonly int[] offsets;
    private readonly int[] sizes;
    private readonly int? pointerSize;

    internal NativeDllStructureLayout(IReadOnlyList<Type> fieldTypes,
        bool packed, IReadOnlySet<int>? alignedPackedPointerFields = null,
        IReadOnlyList<int>? inlineFieldSizes = null, int? pointerSize = null)
    {
        ArgumentNullException.ThrowIfNull(fieldTypes);
        if (pointerSize is not (null or 4 or 8))
        {
            throw new ArgumentOutOfRangeException(nameof(pointerSize));
        }

        this.pointerSize = pointerSize;
        types = [.. fieldTypes];
        if (types.Length == 0)
        {
            throw new ArgumentException("構造体にはフィールドが必要です。", nameof(fieldTypes));
        }

        offsets = new int[types.Length];
        sizes = new int[types.Length];
        if (inlineFieldSizes is not null && inlineFieldSizes.Count != types.Length)
        {
            throw new ArgumentException("宣言と領域長の個数が一致しません。",
                nameof(inlineFieldSizes));
        }

        int length = 0;
        int alignment = 1;
        for (int index = 0; index < types.Length; index++)
        {
            bool inline = types[index] == typeof(byte[]);
            bool extended = types[index] == typeof(NativeDllExtendedArgument);
            int size = inline
                ? inlineFieldSizes?[index] ?? throw new ArgumentException(
                    "インライン領域の長さが必要です。", nameof(inlineFieldSizes))
                : extended ? NativeDllExtendedCodec.Size
                    : NativeDllScalarCodec.GetSize(types[index], pointerSize);
            ArgumentOutOfRangeException.ThrowIfNegative(size);
            sizes[index] = size;
            // 入れ子の子ポインターはPACKでもポインター幅に揃えます。
            bool aligned = !packed
                || (types[index] == typeof(nint)
                    && alignedPackedPointerFields?.Contains(index) == true);
            // 原版のEXTENDEDは10バイト幅で、通常配置では8バイト境界に揃えます。
            int fieldAlignment = aligned ? (inline ? 4 : extended ? 8 : size) : 1;
            alignment = Math.Max(alignment, fieldAlignment);
            length = Align(length, fieldAlignment);
            offsets[index] = length;
            length = checked(length + size);
        }

        DataSize = length;
        Size = Align(length, alignment);
        Offsets = Array.AsReadOnly(offsets);
    }

    /// <summary>
    /// 末尾を揃える前のフィールド領域の長さです。
    /// </summary>
    internal int DataSize
    {
        get;
    }

    internal int Size
    {
        get;
    }

    internal IReadOnlyList<int> Offsets
    {
        get;
    }

    internal bool IsHandleField(int field) => types[field] == typeof(nuint);

    internal byte[] Encode(IReadOnlyList<object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        object?[] snapshot = [.. values];
        if (snapshot.Length != types.Length)
        {
            throw new ArgumentException("宣言とフィールド数が一致しません。", nameof(values));
        }

        byte[] bytes = new byte[Size];
        for (int index = 0; index < types.Length; index++)
        {
            object? value = snapshot[index];
            if (value?.GetType() != types[index])
            {
                throw new ArgumentException("フィールドを宣言したABI型へ変換してください。", nameof(values));
            }

            byte[] encoded = value switch
            {
                byte[] inline => inline,
                NativeDllExtendedArgument extended =>
                    NativeDllExtendedCodec.Encode(extended.Number),
                _ => NativeDllScalarCodec.Encode(value, pointerSize)
            };
            if (encoded.Length != sizes[index])
            {
                throw new ArgumentException("フィールドの領域長が一致しません。",
                    nameof(values));
            }

            encoded.CopyTo(bytes, offsets[index]);
        }

        return bytes;
    }

    internal object[] Decode(ReadOnlySpan<byte> bytes, ushort controlWord = 0)
    {
        if (bytes.Length != Size)
        {
            throw new ArgumentException("構造体の領域長が宣言と一致しません。", nameof(bytes));
        }

        object[] values = new object[types.Length];
        for (int index = 0; index < types.Length; index++)
        {
            ReadOnlySpan<byte> field = bytes.Slice(offsets[index], sizes[index]);
            Type type = types[index];
            values[index] = type == typeof(byte[]) ? field.ToArray()
                : type == typeof(NativeDllExtendedArgument)
                    ? NativeDllExtendedCodec.Decode(field, controlWord)
                    : NativeDllScalarCodec.Decode(type, field, pointerSize);
        }

        return values;
    }

    private static int Align(int value, int alignment)
    {
        int remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }
}
