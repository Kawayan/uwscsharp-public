using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class HotkeyReservationProbe
{
    internal static int Run(string directory, int key)
    {
        var elapsed = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(directory, "acquire")))
        {
            if (File.Exists(Path.Combine(directory, "release")))
            {
                return 0;
            }
            if (elapsed.Elapsed > TimeSpan.FromSeconds(60))
            {
                throw new TimeoutException("登録開始の通知がありません。");
            }
            Thread.Sleep(25);
        }
        const int identifier = 0x5910;
        if (!RegisterHotKey(0, identifier, 0x4000, (uint)key))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        try
        {
            File.WriteAllText(Path.Combine(directory, "registered.prepared"),
                JsonSerializer.Serialize(new
                {
                    pid = Environment.ProcessId,
                    key
                }));
            File.Move(Path.Combine(directory, "registered.prepared"), Path.Combine(directory, "registered.json"));
            while (!File.Exists(Path.Combine(directory, "release")))
            {
                if (elapsed.Elapsed > TimeSpan.FromSeconds(60))
                {
                    throw new TimeoutException("登録解除の通知がありません。");
                }
                Thread.Sleep(25);
            }
        }
        finally
        {
            if (!UnregisterHotKey(0, identifier))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        return 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
}
