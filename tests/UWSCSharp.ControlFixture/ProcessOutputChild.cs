using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UWSCSharp.ControlFixture;

/// <summary>
/// 親の標準ハンドルを継承する、画面を使わない子を作ります。
/// </summary>
internal static class ProcessOutputChild
{
    internal static void Start(string marker)
    {
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("実行元のパスがありません。");
        var startup = new StartupInfo
        {
            Size = Marshal.SizeOf<StartupInfo>(),
            Flags = 0x100,
            Input = GetStdHandle(-10),
            Output = GetStdHandle(-11),
            Error = GetStdHandle(-12)
        };
        var command = new StringBuilder($"\"{executable}\" --process-output-probe hold 0 37 \"{marker}\"");
        if (!CreateProcessW(executable, command, 0, 0, true,
            0x08000000, 0, null, ref startup, out ProcessInfo information))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        using var process = new SafeFileHandle(information.Process, true);
        using var thread = new SafeFileHandle(information.Thread, true);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public uint X;
        public uint Y;
        public uint Width;
        public uint Height;
        public uint XChars;
        public uint YChars;
        public uint Fill;
        public uint Flags;
        public ushort Show;
        public ushort ReservedSize;
        public nint ReservedData;
        public nint Input;
        public nint Output;
        public nint Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetStdHandle(int id);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string application,
        StringBuilder command, nint processSecurity, nint threadSecurity,
        bool inheritHandles, uint flags, nint environment, string? directory,
        ref StartupInfo startup, out ProcessInfo information);
}
