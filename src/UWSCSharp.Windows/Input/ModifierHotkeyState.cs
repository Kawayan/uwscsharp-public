namespace UWSCSharp.Windows;

/// <summary>
/// 左右の修飾子を個別に保持し、押下で成立した組合せを返します。
/// </summary>
internal sealed class ModifierHotkeyState
{
    private uint pressedKeys;

    internal int Modifiers => ToModifiers(pressedKeys);

    /// <summary>
    /// 監視開始時の状態を通知せずに取り込みます。
    /// </summary>
    internal void Reset(IEnumerable<int> physicalKeys)
    {
        ArgumentNullException.ThrowIfNull(physicalKeys);
        uint next = 0;
        foreach (int key in physicalKeys)
        {
            uint bit = KeyBit(key);
            if (bit == 0)
            {
                throw new ArgumentException("左右を区別した修飾子キーを指定してください。",
                    nameof(physicalKeys));
            }

            next |= bit;
        }

        pressedKeys = next;
    }

    /// <summary>
    /// 解放・繰返し・通常キーでは通知せず、現在の保持状態だけを更新します。
    /// </summary>
    internal int? Apply(int physicalKey, bool pressed)
    {
        uint bit = KeyBit(physicalKey);
        if (bit == 0)
        {
            return null;
        }

        int previous = Modifiers;
        pressedKeys = pressed ? pressedKeys | bit : pressedKeys & ~bit;
        int current = Modifiers;
        return pressed && current != previous ? current : null;
    }

    private static uint KeyBit(int key) => key switch
    {
        0xA4 => 1,
        0xA5 => 2,
        0xA2 => 4,
        0xA3 => 8,
        0xA0 => 16,
        0xA1 => 32,
        0x5B => 64,
        0x5C => 128,
        _ => 0
    };

    private static int ToModifiers(uint keys) =>
        ((keys & 3) != 0 ? 1 : 0)
        | ((keys & 12) != 0 ? 2 : 0)
        | ((keys & 48) != 0 ? 4 : 0)
        | ((keys & 192) != 0 ? 8 : 0);
}
