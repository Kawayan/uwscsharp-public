namespace UWSCSharp.Core;

/// <summary>
/// キー状態の取得方法を区別します。
/// </summary>
internal enum KeyStateKind
{
    Pressed,
    Toggle,
    Ime
}

/// <summary>
/// キー状態の公開指定をWin32のキーと取得方法へ変換します。
/// </summary>
internal readonly record struct KeyStateRequest(int Key, KeyStateKind Kind)
{
    internal const int NumLock = 10000;
    internal const int CapsLock = 10001;
    internal const int ScrollLock = 10002;
    internal const int KanaLock = 10003;
    internal const int Ime = 10004;

    internal static KeyStateRequest Read(Value value)
    {
        int code = NumericOperations.Integer(value);
        return code switch
        {
            NumLock => new(0x90, KeyStateKind.Toggle),
            CapsLock => new(0x14, KeyStateKind.Toggle),
            ScrollLock => new(0x91, KeyStateKind.Toggle),
            KanaLock => new(0x15, KeyStateKind.Toggle),
            Ime => new(0, KeyStateKind.Ime),
            901 => new(0x0d, KeyStateKind.Pressed),
            >= 0 and <= 255 => new(code, KeyStateKind.Pressed),
            _ => throw new InvalidOperationException(
                "GETKEYSTATEのキーコードが範囲外です。")
        };
    }
}
