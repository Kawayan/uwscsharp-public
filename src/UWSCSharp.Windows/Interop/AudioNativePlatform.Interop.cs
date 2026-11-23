using System.Runtime.InteropServices;
using System.Text;

namespace UWSCSharp.Windows;

internal sealed partial class AudioNativePlatform
{
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint mciSendStringW(string command, StringBuilder result, uint capacity, nint callback);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool mciGetErrorStringW(uint code, StringBuilder text, uint capacity);

    [DllImport("kernel32.dll", EntryPoint = "Beep", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeBeep(uint frequency, uint milliseconds);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveOutGetNumDevs();
}
