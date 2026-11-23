using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

/// <summary>
/// 予約開始時の標準通知音をWindowsへ依頼します。
/// </summary>
public static partial class WindowsScheduleStartSound
{
    /// <summary>
    /// 標準通知音を依頼します。成功は実際に聞こえることの保証ではありません。
    /// </summary>
    public static void Play()
    {
        if (!MessageBeep(0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "予約の開始音を依頼できません。");
        }
    }
}
