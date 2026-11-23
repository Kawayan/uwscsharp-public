namespace UWSCSharp.Core;
/// <summary>
/// 変更不可の行優先配列です。各次元の上下限を範囲に含みます。
/// </summary>
public sealed class UwscArray
{
    public const int MaximumElements = 1_000_000;
    private readonly int[] bounds;
    private readonly int[] lowerBounds;
    private readonly Value[] values;
    private readonly Func<Value, CancellationToken, Value>? convertElement;
    private readonly Value defaultElement;

    private UwscArray(int[] bounds, Value[] values, int[]? lowerBounds = null,
        ushort? nativeElementType = null,
        Func<Value, CancellationToken, Value>? convertElement = null,
        Value defaultElement = default)
    {
        this.bounds = bounds;
        this.values = values;
        this.convertElement = convertElement;
        this.defaultElement = defaultElement;
        this.lowerBounds = lowerBounds ?? new int[bounds.Length];
        LowerBounds = System.Array.AsReadOnly(this.lowerBounds);
        NativeElementType = nativeElementType;
        UpperBounds = System.Array.AsReadOnly(bounds);
        Values = System.Array.AsReadOnly(values);
    }

    /// <summary>
    /// 各次元の下限の添字です。
    /// </summary>
    public IReadOnlyList<int> LowerBounds
    {
        get;
    }

    internal ushort? NativeElementType
    {
        get;
    }

    internal UwscArray WithNativeElementType(ushort type) =>
        new(bounds, values, lowerBounds, type,
            type == NativeElementType ? convertElement : null,
            type == NativeElementType ? defaultElement : default);

    /// <summary>
    /// 型付き配列の更新時変換と拡張時の初期値を保持します。
    /// </summary>
    internal UwscArray WithElementConversion(Value initial,
        Func<Value, CancellationToken, Value> convert)
    {
        ArgumentNullException.ThrowIfNull(convert);
        if (NativeElementType is null)
        {
            throw new InvalidOperationException("要素型のない配列へ変換規則は設定できません。");
        }

        return new(bounds, values, lowerBounds, NativeElementType, convert, initial);
    }

    /// <summary>
    /// 指定した次元の要素数を取得します。
    /// </summary>
    public int GetLength(int dimension) => checked(bounds[dimension] - lowerBounds[dimension] + 1);

    /// <summary>
    /// 各次元の上限の添字。
    /// </summary>
    public IReadOnlyList<int> UpperBounds
    {
        get;
    }

    /// <summary>
    /// 行優先の順序で格納された要素。
    /// </summary>
    public IReadOnlyList<Value> Values
    {
        get;
    }

    /// <summary>
    /// 配列の次元数。
    /// </summary>
    public int Rank => bounds.Length;

    /// <summary>
    /// 配列の総要素数。
    /// </summary>
    public int Count => values.Length;

    /// <summary>
    /// 指定した各次元の上限と初期値から配列を作成します。
    /// </summary>
    public static UwscArray Create(
        IEnumerable<int> upperBounds, IEnumerable<Value>? initial = null)
    {
        int[] bounds = [.. upperBounds];
        if (bounds.Length is < 1 or > 16)
        {
            throw new InvalidOperationException("配列の次元数が範囲外です。");
        }

        foreach (int bound in bounds)
        {
            if (bound < -1 || (bound == -1 && bounds.Length
                != 1))
            {
                throw new InvalidOperationException(
                    "配列の上限が範囲外です。");
            }

        }

        return CreateBounded(new int[bounds.Length], bounds, initial);
    }

    /// <summary>
    /// 各次元の上下限と初期値から配列を作成します。
    /// </summary>
    public static UwscArray CreateBounded(IEnumerable<int> lowerBounds,
        IEnumerable<int> upperBounds, IEnumerable<Value>? initial = null)
    {
        int[] lower = [.. lowerBounds];
        int[] bounds = [.. upperBounds];
        int count = ValidateBounds(lower, bounds);
        var values = new Value[count];
        if (initial is not null)
        {
            int i = 0;
            foreach (Value value in initial)
            {
                if (i == values.Length)
                {
                    throw new InvalidOperationException("配列の初期値が多すぎます。");
                }

                values[i++] = value;
            }
        }

        return new(bounds, values, lower);
    }

    internal static int ValidateBounds(IReadOnlyList<int> lower, IReadOnlyList<int> bounds)
    {
        if (bounds.Count is < 1 or > 16 || lower.Count != bounds.Count)
        {
            throw new InvalidOperationException("配列の次元数が範囲外です。");
        }

        long count = 1;
        for (int axis = 0; axis < bounds.Count; axis++)
        {
            long length = (long)bounds[axis] - lower[axis] + 1;
            if (length < 0 || length > MaximumElements)
            {
                throw new InvalidOperationException("配列の境界が範囲外です。");
            }

            count *= length;
            if (count > MaximumElements)
            {
                throw new InvalidOperationException(
                    "配列の要素数が上限を超えました。");
            }
        }

        return (int)count;
    }

    /// <summary>
    /// 値の列から1次元配列を作成します。
    /// </summary>
    public static UwscArray FromValues(IEnumerable<Value> source)
    {
        var values = new List<Value>();
        foreach (Value value in source)
        {
            if (values.Count == MaximumElements)
            {
                throw new InvalidOperationException(
                    "配列の要素数が上限を超えました。");
            }

            values.Add(value);
        }

        return new([values.Count - 1], [.. values]);
    }

    private (int Offset, int Count) Region(
        IReadOnlyList<int> indices)
    {
        if (indices.Count > Rank)
        {
            throw new InvalidOperationException("配列の添字が多すぎます。");
        }

        int offset = 0;
        int stride = Count;
        for (int i = 0; i < indices.Count; i++)
        {
            if (indices[i] < lowerBounds[i] || indices[i] > bounds[i])
            {
                throw new InvalidOperationException(
                    "配列の添字が範囲外です。");
            }

            stride /= GetLength(i);
            offset += (indices[i] - lowerBounds[i]) * stride;
        }

        return (offset, stride);
    }

    /// <summary>
    /// 指定した添字の要素を取得します。
    /// </summary>
    public Value Get(params int[] indices)
    {
        (int offset, int count) = Region(indices);
        return indices.Length == Rank ? values[offset]
            : Value.FromArray(new UwscArray(bounds[indices.Length..],
            values.AsSpan(offset, count).ToArray(), lowerBounds[indices.Length..],
            NativeElementType, convertElement, defaultElement));
    }

    /// <summary>
    /// 指定した添字の要素を変更した新しい配列を返します。
    /// </summary>
    public UwscArray Set(IReadOnlyList<int> indices,
        Value replacement) => Set(indices, replacement, CancellationToken.None);

    internal UwscArray Set(IReadOnlyList<int> indices,
        Value replacement, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        (int offset, int count) = Region(indices);
        var copy = (Value[])values.Clone();
        if (indices.Count == Rank)
        {
            copy[offset] = ConvertElement(replacement, token);
        }
        else
        {
            UwscArray array = replacement.AsArray();
            if (!bounds[indices.Count..].SequenceEqual(
                array.bounds) || !lowerBounds[indices.Count..].SequenceEqual(array.lowerBounds))
            {
                throw new InvalidOperationException(
                    "部分配列の形状が一致しません。");
            }

            for (int index = 0; index < array.values.Length; index++)
            {
                copy[offset + index] = ConvertElement(array.values[index], token);
            }
        }

        token.ThrowIfCancellationRequested();
        return new((int[])bounds.Clone(), copy, (int[])lowerBounds.Clone(),
            NativeElementType, convertElement, defaultElement);
    }

    /// <summary>
    /// 形状を維持し、内容を置き換えた配列を返します。
    /// </summary>
    public UwscArray WithValues(
        IEnumerable<Value> replacement) => WithValues(replacement, CancellationToken.None);

    internal UwscArray WithValues(IEnumerable<Value> replacement,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var copy = new Value[Count];
        System.Array.Fill(copy, defaultElement);
        int index = 0;
        foreach (Value value in replacement)
        {
            if (index == copy.Length)
            {
                throw new InvalidOperationException("配列の初期値が多すぎます。");
            }

            copy[index++] = ConvertElement(value, token);
        }

        token.ThrowIfCancellationRequested();
        return new(bounds, copy, lowerBounds, NativeElementType,
            convertElement, defaultElement);
    }

    /// <summary>
    /// 第1次元の上限を変更し、他の上下限を維持した配列を返します。
    /// </summary>
    public UwscArray Resize(int upperBound) => Resize(upperBound, CancellationToken.None);

    internal UwscArray Resize(int upperBound, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        int[] newBounds = (int[])bounds.Clone();
        newBounds[0] = upperBound;
        int count = ValidateBounds(lowerBounds, newBounds);
        var resized = new Value[count];
        System.Array.Fill(resized, defaultElement);
        values.AsSpan(0, Math.Min(values.Length, count)).CopyTo(resized);
        token.ThrowIfCancellationRequested();
        return new(newBounds, resized, lowerBounds, NativeElementType,
            convertElement, defaultElement);
    }

    private Value ConvertElement(Value value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Value result = convertElement is null ? value : convertElement(value, token);
        token.ThrowIfCancellationRequested();
        return result;
    }
}
