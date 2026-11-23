namespace UWSCSharp.Windows;

// 原版のキー正規化とメッセージ形式を、OSへの送信から分離します。
internal readonly record struct KeyboardEncoding(
    int Key, uint Scan, bool Extended)
{
    internal static KeyboardEncoding Create(
        int raw, Func<uint, uint> mapVirtualKey)
    {
        // スキャンコードは正規化前の値で取得します。
        uint scan = mapVirtualKey((uint)raw);
        bool extended = raw is 0xa1 or 0xa3 or 0xa5 or 901
            or >= 33 and <= 40 or 45 or 46 or 111;
        int key = raw switch
        {
            0xa1 => 16,
            0xa3 => 17,
            0xa5 => 18,
            901 => 13,
            _ => raw
        };
        return new(key, scan, extended);
    }

    internal uint DirectMessage(bool up) => up ? 0x101u : 0x100u;

    internal uint DirectParameter(bool up) => 1u
        | ((Scan & 0xff) << 16)
        | (Extended ? 0x01000000u : 0)
        | (up ? 0xc0000000u : 0);
}

// 汎用および右側の修飾キーだけを直接入力用スロットへ記録します。
internal sealed class DirectKeyboardState
{
    internal int Shift
    {
        get; private set;
    }

    internal int Control
    {
        get; private set;
    }

    internal int Alt
    {
        get; private set;
    }

    internal bool ApplyModifier(int raw, bool up)
    {
        int value = up ? 0 : raw;
        switch (raw)
        {
            case 16:
            case 0xa1:
                Shift = value;
                return true;
            case 17:
            case 0xa3:
                Control = value;
                return true;
            case 18:
            case 0xa5:
                Alt = value;
                return true;
            default:
                return false;
        }
    }
}
