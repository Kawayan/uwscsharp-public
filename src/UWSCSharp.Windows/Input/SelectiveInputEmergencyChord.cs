using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// 複数のフックから届く物理通知を、待機せずに同じ状態へ反映します。
/// </summary>
internal sealed class SelectiveInputEmergencyChord
{
    private const int F11Down = 0x100;
    private const int Armed = 0x200;
    private const int Consuming = 0x400;
    private const int Initialized = 0x800;
    private const int PressedKeys = 0x1ff;
    private const int ObservedKeysShift = 12;
    private int state;

    internal bool IsConsuming => (Volatile.Read(ref state) & Consuming) != 0;

    internal void Reset(IEnumerable<int> pressed, bool mergeObserved = false)
    {
        int keys = 0;
        foreach (int key in pressed)
        {
            int bit = key == SelectiveInputEmergencyShortcut.Key.VirtualKey
                ? F11Down : ModifierBit(key);
            if (bit == 0)
            {
                throw new ArgumentException("初期状態には左右別の修飾子とF11を指定してください。", nameof(pressed));
            }
            keys |= bit;
        }

        int previous;
        int next;
        do
        {
            previous = Volatile.Read(ref state);
            // 採取中に通知されたキーは、押下・解放の最後の通知を優先します。
            int observed = mergeObserved
                ? (previous >> ObservedKeysShift) & PressedKeys : 0;
            next = Initialized | (keys & ~observed) | (previous & observed);
            if ((next & (F11Down | 0x3f)) == 0)
            {
                next |= Armed;
            }
        }
        while (Interlocked.CompareExchange(ref state, next, previous) != previous);
    }

    internal void Uninitialize() => Interlocked.Exchange(ref state, 0);

    internal (bool Request, bool Consume) Apply(uint key, uint scan,
        uint flags, nuint extra, nuint message)
    {
        if ((flags & 0x12) != 0 || extra == 1
            || message is not (0x100 or 0x101 or 0x104 or 0x105))
        {
            return (false, false);
        }

        bool f11 = key == SelectiveInputEmergencyShortcut.Key.VirtualKey;
        int bit = f11 ? F11Down : ModifierBit(ModifierHotkeyBindings.Normalize(key, scan, flags));
        if (bit == 0)
        {
            return (false, false);
        }

        bool pressed = message is 0x100 or 0x104;
        int previous;
        int next;
        bool request;
        bool consume;
        do
        {
            previous = Volatile.Read(ref state);
            next = pressed ? previous | bit : previous & ~bit;
            request = false;
            consume = false;
            if ((previous & Initialized) != 0)
            {
                if (f11)
                {
                    request = pressed && (previous & F11Down) == 0
                        && (previous & Armed) != 0
                        && Modifiers(previous) == SelectiveInputEmergencyShortcut.Key.Modifiers;
                    if (request)
                    {
                        next |= Consuming;
                    }
                    consume = ((previous | next) & Consuming) != 0;
                    if (!pressed)
                    {
                        next &= ~Consuming;
                    }
                }

                if ((next & (F11Down | 0x3f)) == 0)
                {
                    next |= Armed;
                }
            }
            else
            {
                next |= bit << ObservedKeysShift;
            }
        }
        while (Interlocked.CompareExchange(ref state, next, previous) != previous);
        return (request, consume);
    }

    private static int ModifierBit(int key) => key switch
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

    private static int Modifiers(int keys) =>
        ((keys & 3) != 0 ? 1 : 0)
        | ((keys & 12) != 0 ? 2 : 0)
        | ((keys & 48) != 0 ? 4 : 0)
        | ((keys & 192) != 0 ? 8 : 0);
}
