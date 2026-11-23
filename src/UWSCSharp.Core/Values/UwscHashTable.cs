using System.Collections.Immutable;

namespace UWSCSharp.Core;

/// <summary>
/// 内容更新時に新しい値を作る連想配列です。
/// </summary>
public sealed class UwscHashTable
{
    private readonly ImmutableDictionary<string, Value> values;
    private readonly ImmutableList<string> keys;
    private readonly StringComparer comparer;
    private readonly Lazy<ImmutableArray<string>> sortedKeys;

    private UwscHashTable(int flags, ImmutableDictionary<string, Value> values,
        ImmutableList<string> keys)
    {
        Flags = flags;
        this.values = values;
        this.keys = keys;
        comparer = (flags & HashConstants.CaseCare) != 0
            ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        sortedKeys = new(() => [.. keys.Order(comparer)]);
    }

    /// <summary>
    /// 宣言時の大小文字・並び順の指定です。
    /// </summary>
    public int Flags
    {
        get;
    }

    /// <summary>
    /// 登録されているキーの数です。
    /// </summary>
    public int Count => values.Count;

    /// <summary>
    /// 指定された設定で空の連想配列を生成します。
    /// </summary>
    public static UwscHashTable Create(int flags = 0)
    {
        if ((flags & ~(HashConstants.CaseCare | HashConstants.Sort)) != 0)
        {
            throw new InvalidOperationException("HASHTBLの設定が不正です。");
        }

        StringComparer comparer = (flags & HashConstants.CaseCare) != 0
            ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        return new(flags, ImmutableDictionary.Create<string, Value>(comparer), []);
    }

    /// <summary>
    /// 読出し順のキーを変更不可のsnapshotとして取得します。
    /// </summary>
    public IReadOnlyList<string> Keys() => (Flags & HashConstants.Sort) != 0
        ? sortedKeys.Value : keys;

    /// <summary>
    /// キーが存在するかを取得します。
    /// </summary>
    public bool Contains(string key) => values.ContainsKey(key);

    /// <summary>
    /// キーに対応する値を取得し、不在ならEMPTYを返します。
    /// </summary>
    public Value Get(string key) => values.TryGetValue(key, out Value value)
        ? value : Value.Empty;

    /// <summary>
    /// キーを追加または更新した新しい連想配列を返します。
    /// </summary>
    public UwscHashTable Set(string key, Value value)
    {
        bool exists = Contains(key);
        if (!exists && Count == UwscArray.MaximumElements)
        {
            throw new InvalidOperationException("HASHTBLの要素数が上限を超えました。");
        }

        return new(Flags, values.SetItem(key, value), exists ? keys : keys.Add(key));
    }

    /// <summary>
    /// キーを除去した新しい連想配列を返します。
    /// </summary>
    public UwscHashTable Remove(string key) => Contains(key)
        ? new(Flags, values.Remove(key), keys.Remove(key, comparer)) : this;

    /// <summary>
    /// 設定を維持して全要素を消去します。
    /// </summary>
    public UwscHashTable Clear() => Create(Flags);

    /// <summary>
    /// 順列番号に対応するキーを取得します。
    /// </summary>
    public string KeyAt(int index)
    {
        if (index < 0 || index >= Count)
        {
            throw new InvalidOperationException("HASHTBLの順列番号が範囲外です。");
        }

        return Keys()[index];
    }
}

internal static class HashConstants
{
    internal static readonly IReadOnlyDictionary<string, int> Values =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["HASH_KEY"] = Key,
            ["HASH_VAL"] = Val,
            ["HASH_EXISTS"] = Exists,
            ["HASH_REMOVE"] = Remove,
            ["HASH_REMOVEALL"] = RemoveAll,
            ["HASH_CASECARE"] = CaseCare,
            ["HASH_SORT"] = Sort
        };

    internal const int Key = -101;
    internal const int Val = -102;
    internal const int Exists = -103;
    internal const int Remove = -104;
    internal const int RemoveAll = -109;
    internal const int CaseCare = 4096;
    internal const int Sort = 8192;
}
