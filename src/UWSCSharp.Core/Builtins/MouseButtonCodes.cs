namespace UWSCSharp.Core;

/// <summary>
/// BTNのボタン種別を共有します。
/// </summary>
internal static class MouseButtonCodes
{
    internal const int Left = 0;
    internal const int Right = 1;
    internal const int Middle = 2;
    internal const int XButton1 = 3;
    internal const int XButton2 = 4;
    internal const int Wheel = 5;
    internal const int HorizontalWheel = 6;
    internal const int Touch = 7;

    internal static bool IsWheel(int button) =>
        button is Wheel or HorizontalWheel;

}
