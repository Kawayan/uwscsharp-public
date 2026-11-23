using System.Runtime.InteropServices;
using UWSCSharp.Windows;

namespace UWSCSharp.Wpf;

internal static class SchedulerFontVerification
{
    internal static string Source(string title, bool changeFont) => $$"""
        OPTION DLGTITLE="{{title}}-message"
        MSGBOX("configured")
        OPTION DLGTITLE="{{title}}-input"
        INPUT("input","seed")
        OPTION DLGTITLE="{{title}}-select"
        SLCTBOX(SLCT_BTN+SLCT_NUM,0,,"one","two")
        """ + (changeFont ? $$"""

        OPTION DEFAULTFONT="Consolas,11"
        OPTION DLGTITLE="{{title}}-override"
        MSGBOX("overridden")
        """ : "");

    internal static async Task<Observation> ObserveAsync(
        string title, string family, int points, int buttonId)
    {
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(15));
        nint window;
        nint button;
        while (true)
        {
            window = FindWindowW(null, title);
            button = GetDlgItem(window, buttonId);
            if (button != 0)
            {
                break;
            }

            await Task.Delay(20, timeout.Token);
        }

        Native.GetWindowThreadProcessId(button, out uint process);
        // HFONTは所有プロセス内でのみ読み、別プロセスの値を解釈しません。
        if (process != Environment.ProcessId
            || Native.SendMessageTimeoutW(button, 0x31, 0, 0,
                2, 1000, out nuint font) == 0
            || font == 0
            || GetObjectW((nint)font, Marshal.SizeOf<LogFont>(),
                out LogFont actual) == 0)
        {
            throw new InvalidOperationException(
                "予約ダイアログの所有プロセス内で書体を取得できません: " + title);
        }

        uint dpi = GetDpiForWindow(window);
        int expectedHeight = -(int)Math.Ceiling(points * dpi / 72d);
        if (dpi == 0 || actual.FaceName != family
            || actual.Height != expectedHeight)
        {
            throw new InvalidOperationException(
                $"{title}: {actual.FaceName}/{actual.Height}, "
                + $"期待値={family}/{expectedHeight}, DPI={dpi}");
        }

        if (!Native.PostMessageW(button, 0xf5, 0, 0))
        {
            throw new InvalidOperationException(
                "予約ダイアログのボタン通知に失敗しました: " + title);
        }

        return new(title, actual.FaceName, actual.Height, dpi, process);
    }

    internal sealed record Observation(
        string Title, string Family, int Height, uint Dpi, uint ProcessId);

    internal static async Task<int> ReadButtonHeightAsync(
        string title, Func<int> expectedProcessId)
    {
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(15));
        while (true)
        {
            nint window = FindWindowW(null, title);
            nint button = GetDlgItem(window, 4097);
            if (button != 0)
            {
                Native.GetWindowThreadProcessId(button, out uint process);
                if (process == (uint)expectedProcessId())
                {
                    using var dpi = new PhysicalDpiScope();
                    if (!Native.GetWindowRect(button, out Native.Rect bounds)
                        || !Native.PostMessageW(button, 0xf5, 0, 0))
                    {
                        throw new InvalidOperationException(
                            "予約ダイアログを確認できません: " + title);
                    }

                    return bounds.Bottom - bounds.Top;
                }
            }

            await Task.Delay(20, timeout.Token);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LogFont
    {
        internal int Height;
        internal int Width;
        internal int Escapement;
        internal int Orientation;
        internal int Weight;
        internal byte Italic;
        internal byte Underline;
        internal byte StrikeOut;
        internal byte CharacterSet;
        internal byte OutPrecision;
        internal byte ClipPrecision;
        internal byte Quality;
        internal byte PitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string FaceName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowW(string? className, string title);

    [DllImport("user32.dll")]
    private static extern nint GetDlgItem(nint window, int id);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetObjectW(
        nint item, int size, out LogFont font);
}
