using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UWSCSharp.ControlFixture;

/// <summary>
/// Sandbox内の検証画像と遅延描画だけを所有します。
/// </summary>
internal sealed class ClipboardProbeOwner : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        internal nint Window;
        internal uint Id;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal int X;
        internal int Y;
        internal uint Private;
    }

    private delegate nint Procedure(nint window, uint message,
        nint wParam, nint lParam);

    private readonly ManualResetEventSlim ready = new();
    private readonly Thread thread;
    private readonly Procedure procedure;
    private nint previous;
    private volatile byte[]? delayed;
    private Action? pending;
    private int[]? image;
    private int delay;
    private int renders;
    private Exception? failure;

    internal sealed record LockEvent(uint Thread, string Operation);

    internal List<LockEvent> LockEvents { get; } = [];

    internal nint Window
    {
        get; private set;
    }

    internal int Renders => Volatile.Read(ref renders);

    internal string? Failure => failure?.ToString();

    internal ClipboardProbeOwner()
    {
        if (!OperatingSystem.IsWindows()
            || !Environment.UserName.Equals("WDAGUtilityAccount", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Sandboxの検証アカウントだけで実行してください。");
        }

        procedure = Dispatch;
        thread = new Thread(() =>
        {
            try
            {
                Window = CreateWindowExW(0x08000000, "STATIC", "UWSC owned clipboard",
                    0x80000000, 160, 180, 96, 64, 0, 0, 0, 0);
                Check(Window != 0);
                previous = SetWindowLongPtrW(Window, -4,
                    Marshal.GetFunctionPointerForDelegate(procedure));
                Check(previous != 0);
                ready.Set();
                while (GetMessageW(out Message message, 0, 0, 0) > 0)
                {
                    TranslateMessage(ref message);
                    DispatchMessageW(ref message);
                }
            }
            catch (Exception error)
            {
                failure = error;
                ready.Set();
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(10)) || failure is not null)
        {
            throw new InvalidOperationException("clipboard所有スレッドを開始できません。", failure);
        }
    }

    private nint Dispatch(nint window, uint message, nint wParam, nint lParam)
    {
        try
        {
            if (image is not null && message is 0xf or 0x317 or 0x318)
            {
                nint dc = message == 0xf ? GetDC(window) : wParam;
                Check(dc != 0);
                try
                {
                    Draw(dc);
                }
                finally
                {
                    if (message == 0xf)
                    {
                        ReleaseDC(window, dc);
                        ValidateRect(window, 0);
                    }
                }

                return 1;
            }

            if (message == 0x8003)
            {
                pending?.Invoke();
                return 1;
            }

            if (message is 0x8001 or 0x8002)
            {
                Check(message == 0x8001 ? OpenClipboard(Window) : CloseClipboard());
                LockEvents.Add(new(GetCurrentThreadId(), message == 0x8001 ? "open" : "close"));
                return 1;
            }

            if (message == 0x305 && (uint)wParam == 8 && delayed is byte[] data)
            {
                Thread.Sleep(delay);
                Publish(8, data);
                Interlocked.Increment(ref renders);
                return 0;
            }

            if (message == 2)
            {
                PostQuitMessage(0);
            }

            return CallWindowProcW(previous, window, message, wParam, lParam);
        }
        catch (Exception error)
        {
            // 管理例外をネイティブcallback境界へ出しません。
            failure = error;
            return 0;
        }
    }

    internal void Set(string kind, uint format, byte[] data)
    {
        pending = () => SetCore(kind, format, data);
        try
        {
            Check(SendMessageW(Window, 0x8003, 0, 0) != 0);
        }
        finally
        {
            pending = null;
        }
    }

    internal void ShowImage(int[] pixels)
    {
        pending = () =>
        {
            image = pixels;
            Check(SetWindowPos(Window, -1, 160, 180, 96, 64, 0x50));
            Check(InvalidateRect(Window, 0, false));
            UpdateWindow(Window);
        };
        try
        {
            Check(SendMessageW(Window, 0x8003, 0, 0) != 0);
        }
        finally
        {
            pending = null;
        }
    }

    private void Draw(nint dc)
    {
        Fill(dc, new(0, 0, 96, 64), 0x202020);
        foreach ((int left, int top) in new[] { (10, 10), (14, 10), (10, 14) })
        {
            for (int index = 0; index < 4; index++)
            {
                int x = left + index % 2;
                int y = top + index / 2;
                Fill(dc, new(x, y, x + 1, y + 1), image![index]);
            }
        }
    }

    private static void Fill(nint dc, Rect rect, int rgb)
    {
        uint bgr = (uint)((rgb & 255) << 16 | (rgb & 0xff00) | (rgb >> 16 & 255));
        nint brush = CreateSolidBrush(bgr);
        Check(brush != 0);
        try
        {
            Check(FillRect(dc, ref rect, brush) != 0);
        }
        finally
        {
            DeleteObject(brush);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect(int left, int top, int right, int bottom)
    {
        internal int Left = left;
        internal int Top = top;
        internal int Right = right;
        internal int Bottom = bottom;
    }

    private void SetCore(string kind, uint format, byte[] data)
    {
        Check(OpenClipboard(Window));
        try
        {
            delayed = null;
            renders = 0;
            Check(EmptyClipboard());
            if (kind == "empty")
            {
                return;
            }

            if (kind is "delayed" or "slow-delayed")
            {
                delay = kind == "slow-delayed" ? 150 : 0;
                delayed = data;
                Marshal.SetLastPInvokeError(0);
                _ = SetClipboardData(8, 0);
                Check(Marshal.GetLastPInvokeError() == 0);
            }
            else if (kind == "bitmap")
            {
                nint dc = GetDC(0);
                Check(dc != 0);
                nint bitmap = 0;
                try
                {
                    bitmap = CreateCompatibleBitmap(dc, 2, 2);
                    Check(bitmap != 0);
                    nint memory = CreateCompatibleDC(dc);
                    Check(memory != 0);
                    try
                    {
                        nint old = SelectObject(memory, bitmap);
                        Check(old != 0 && old != -1);
                        try
                        {
                            for (int index = 0; index < 4; index++)
                            {
                                int position = 40 + index * 4;
                                uint color = (uint)(data[position] << 16
                                    | data[position + 1] << 8 | data[position + 2]);
                                Check(SetPixel(memory, index % 2, index / 2, color) != uint.MaxValue);
                            }
                        }
                        finally
                        {
                            SelectObject(memory, old);
                        }
                    }
                    finally
                    {
                        DeleteDC(memory);
                    }

                    Check(SetClipboardData(2, bitmap) != 0);
                    bitmap = 0;
                }
                finally
                {
                    if (bitmap != 0)
                    {
                        DeleteObject(bitmap);
                    }

                    ReleaseDC(0, dc);
                }
            }
            else
            {
                Publish(format, data);
            }
        }
        finally
        {
            Check(CloseClipboard());
        }
    }

    private static void Publish(uint format, byte[] data)
    {
        nint memory = GlobalAlloc(2, (nuint)data.Length);
        Check(memory != 0);
        try
        {
            nint pointer = GlobalLock(memory);
            Check(pointer != 0);
            try
            {
                Marshal.Copy(data, 0, pointer, data.Length);
            }
            finally
            {
                GlobalUnlock(memory);
            }

            Check(SetClipboardData(format, memory) != 0);
            memory = 0;
        }
        finally
        {
            if (memory != 0)
            {
                GlobalFree(memory);
            }
        }
    }

    internal bool Available(uint format) => IsClipboardFormatAvailable(format);

    internal byte[] ReadDib()
    {
        Check(OpenClipboard(0));
        try
        {
            nint memory = GetClipboardData(8);
            Check(memory != 0);
            int size = checked((int)GlobalSize(memory));
            if (size > 8192)
            {
                throw new InvalidOperationException("検証画像が上限を超えました。");
            }

            nint pointer = GlobalLock(memory);
            Check(pointer != 0);
            try
            {
                byte[] data = new byte[size];
                Marshal.Copy(pointer, data, 0, size);
                return data;
            }
            finally
            {
                GlobalUnlock(memory);
            }
        }
        finally
        {
            Check(CloseClipboard());
        }
    }

    internal void Lock() => Check(SendMessageW(Window, 0x8001, 0, 0) != 0);

    internal void Unlock() => Check(SendMessageW(Window, 0x8002, 0, 0) != 0);

    private static void Check(bool success)
    {
        if (!success)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    /// <summary>
    /// 検証用データを破棄し、所有スレッドを終了します。
    /// </summary>
    public void Dispose()
    {
        Set("empty", 0, []);
        Check(PostMessageW(Window, 0x10, 0, 0));
        if (!thread.Join(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidOperationException("clipboard所有スレッドを終了できません。");
        }

        ready.Dispose();
        GC.KeepAlive(procedure);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint ex, string className, string title,
        uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowLongPtrW(nint window, int index, nint value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CallWindowProcW(nint previous, nint window,
        uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out Message message, nint window, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref Message message);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int code);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(nint owner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint format, nint memory);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetClipboardData(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint GlobalSize(nint memory);

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageW(nint window, uint message, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint flags, nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(nint memory);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalFree(nint memory);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint SelectObject(nint dc, nint value);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern uint SetPixel(nint dc, int x, int y, uint color);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint bitmap);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint window, nint after,
        int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ValidateRect(nint window, nint rect);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(nint window, nint rect, bool erase);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(nint window);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateSolidBrush(uint color);

    [DllImport("user32.dll")]
    private static extern int FillRect(nint dc, ref Rect rect, nint brush);
}
