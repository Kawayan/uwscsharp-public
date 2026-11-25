namespace UWSCSharp.Windows;

// 低レベル入力フックとWinEventで使う値です。
// フックのwParamはnintで届くため、パターン照合で比較できるようint定数にします。
internal static partial class Native
{
    internal const int WhKeyboardLl = 13;
    internal const int WhMouseLl = 14;

    internal const int WmQuit = 0x0012;
    internal const int WmKeyDown = 0x0100;
    internal const int WmKeyUp = 0x0101;
    internal const int WmSysKeyDown = 0x0104;
    internal const int WmSysKeyUp = 0x0105;
    internal const int WmMouseMove = 0x0200;
    internal const int WmLButtonDown = 0x0201;
    internal const int WmLButtonUp = 0x0202;
    internal const int WmRButtonDown = 0x0204;
    internal const int WmRButtonUp = 0x0205;
    internal const int WmMButtonDown = 0x0207;
    internal const int WmMButtonUp = 0x0208;
    internal const int WmMouseWheel = 0x020a;
    internal const int WmXButtonDown = 0x020b;
    internal const int WmXButtonUp = 0x020c;
    internal const int WmMouseHWheel = 0x020e;

    // KBDLLHOOKSTRUCT.flags と MSLLHOOKSTRUCT.flags の注入入力ビットです。
    internal const uint LlkhfExtended = 0x01;
    internal const uint LlkhfInjected = 0x10;
    internal const uint LlmhfInjected = 0x01;

    // 右Shiftのスキャンコードです。VK_SHIFTを左右に分けるために使います。
    internal const uint RightShiftScanCode = 0x36;

    internal const uint EventSystemMoveSizeStart = 0x000a;
    internal const uint EventSystemMoveSizeEnd = 0x000b;
    internal const uint EventObjectStateChange = 0x800a;
    internal const uint EventObjectLocationChange = 0x800b;
}
