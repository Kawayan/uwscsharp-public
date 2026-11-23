using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace UWSCSharp.ControlFixture;

/// <summary>
/// EXECで生成した子だけを識別し、有限時間で終了する検証画面です。
/// </summary>
internal static class ExecProbe
{
    internal static void Run(string[] args)
    {
        string output = args[0];
        int milliseconds = int.Parse(args[1]);
        int code = int.Parse(args[2]);
        bool gui = args.Length > 3 && args[3] == "gui";
        nint window = gui ? CreatePhysicalWindow() : 0;
        string prepared = output + ".prepared";
        File.WriteAllText(prepared, JsonSerializer.Serialize(new
        {
            pid = Environment.ProcessId,
            window = (long)window,
            awareness = window == 0 ? -1 : GetAwarenessFromDpiAwarenessContext(GetWindowDpiAwarenessContext(window)),
            dpi = window == 0 ? 0 : GetDpiForWindow(window),
            arguments = args
        }));
        // 親の存在確認に、保存とハンドル解放が済んだ結果だけを公開します。
        File.Move(prepared, output);
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds)
        {
            while (PeekMessageW(out Message message, 0, 0, 0, 1))
            {
                TranslateMessage(ref message);
                DispatchMessageW(ref message);
            }

            Thread.Sleep(5);
        }

        if (window != 0)
        {
            DestroyWindow(window);
        }

        Environment.Exit(code);
    }

    private static nint CreatePhysicalWindow()
    {
        nint previous = SetThreadDpiAwarenessContext(-4);
        if (previous == 0)
        {
            throw new InvalidOperationException("検証画面のDPI認識を設定できません。");
        }
        try
        {
            return CreateWindowExW(0, "STATIC", "EXEC probe",
                0x10cf0000, 40, 50, 320, 240, 0, 0, 0, 0);
        }
        finally
        {
            SetThreadDpiAwarenessContext(previous);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint extended, string name,
        string text, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool PeekMessageW(out Message message,
        nint window, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref Message message);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetWindowDpiAwarenessContext(nint window);

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);

    [DllImport("user32.dll")]
    private static extern int GetAwarenessFromDpiAwarenessContext(nint context);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
}
