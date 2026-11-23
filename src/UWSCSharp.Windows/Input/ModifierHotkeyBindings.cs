namespace UWSCSharp.Windows;

/// <summary>
/// 成立した修飾子の組合せを登録IDへ対応付けます。
/// </summary>
internal sealed class ModifierHotkeyBindings
{
    private readonly ModifierHotkeyState state = new();
    private readonly Dictionary<int, int> byModifier = [];

    internal int Count => byModifier.Count;

    internal bool Contains(int id) => byModifier.ContainsValue(id);

    internal bool CanRegister(int id, int modifiers) =>
        modifiers is > 0 and <= 15
        && !Contains(id) && !byModifier.ContainsKey(modifiers);

    internal bool Register(int id, int modifiers)
    {
        if (!CanRegister(id, modifiers))
        {
            return false;
        }

        byModifier.Add(modifiers, id);
        return true;
    }

    internal bool Remove(int id)
    {
        foreach (KeyValuePair<int, int> entry in byModifier)
        {
            if (entry.Value == id)
            {
                byModifier.Remove(entry.Key);
                return true;
            }
        }

        return false;
    }

    internal void Reset(IEnumerable<int> physicalKeys) => state.Reset(physicalKeys);

    internal void Clear()
    {
        byModifier.Clear();
        state.Reset([]);
    }

    internal int? Apply(int physicalKey, bool pressed)
    {
        int? modifiers = state.Apply(physicalKey, pressed);
        return modifiers is { } combination && byModifier.TryGetValue(combination, out int id)
            ? id : null;
    }

    /// <summary>
    /// 低レベル通知の汎用修飾子コードを左右別のコードへ変換します。
    /// </summary>
    internal static int Normalize(uint key, uint scanCode, uint flags) => key switch
    {
        0x10 when scanCode == 0x2A => 0xA0,
        0x10 when scanCode == 0x36 => 0xA1,
        0x11 => (flags & 1) == 0 ? 0xA2 : 0xA3,
        0x12 => (flags & 1) == 0 ? 0xA4 : 0xA5,
        >= 0xA0 and <= 0xA5 or 0x5B or 0x5C => (int)key,
        _ => 0
    };
}
