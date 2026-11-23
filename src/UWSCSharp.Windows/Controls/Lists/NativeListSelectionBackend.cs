using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal interface IListSelectionPlatform
{
    NativeControlBufferOwner CreateControlBufferOwner(nint root) => new(root);

    short KeyState(int key) => Native.GetKeyState(key);

    void Activate(nint root, CancellationToken token);

    bool IsWindow(nint window);

    bool IsHostWindow(nint window);

    uint CurrentThread();

    uint WindowThread(nint window);

    bool Attach(uint target, uint current, bool attach);

    void Foreground(nint window);

    void Focus(nint window);

    void Priority(uint value);

    void LockUpdates(nint window);

    nint Send(nint window, uint message, nint parameter, nint data,
        bool timed, uint timeout, uint flags);

    void Sleep(int milliseconds, CancellationToken token);

    void Pump(CancellationToken token);
    uint ClassStyle(nint window);
    Native.Rect Bounds(nint window);
    Native.Point Cursor();
    void MoveCursor(Native.Point point);
    bool OwnsPointer(nint root, Native.Point point) => false;
    Native.Point ToScreen(nint window, Native.Point point)
    {
        Native.ClientToScreen(window, ref point);
        return point;
    }

    void Post(nint window, uint message, nint parameter, nint data) =>
        Native.PostMessageW(window, message, parameter, data);

    int Metric(int index);
    bool IsIconic(nint window);
    bool IsVisible(nint window);
    nint ForegroundWindow();
}

internal sealed class NativeListSelectionBackend(nint root, nint target,
    bool combo, bool back, IListSelectionPlatform? platform = null,
    bool move = false) : IListSelectionBackend
{
    private readonly IListSelectionPlatform operations = platform ?? new NativeListSelectionPlatform();
    private Native.Point savedCursor;
    private bool restoreCursor;

    public void PrepareComboCursor(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (move)
        {
            Native.Rect bounds = operations.Bounds(target);
            operations.MoveCursor(new Native.Point(bounds.Left, bounds.Top));
        }

        savedCursor = operations.Cursor();
        restoreCursor = back;
        if (back)
        {
            Native.Rect bounds = operations.Bounds(target);
            int edge = WindowsOperationErrors.Subtract(
                WindowsOperationErrors.Add(operations.Metric(76), operations.Metric(78)), 10);
            int x = bounds.Right < edge
                ? WindowsOperationErrors.Add(bounds.Right, 4)
                : WindowsOperationErrors.Subtract(bounds.Left, 4);
            int y = WindowsOperationErrors.Add(bounds.Top, bounds.Bottom) / 2;
            operations.MoveCursor(new Native.Point(x, y));
        }

        token.ThrowIfCancellationRequested();
    }

    public void RestoreComboCursor()
    {
        if (restoreCursor)
        {
            restoreCursor = false;
            operations.MoveCursor(savedCursor);
        }
    }

    public bool OpenCombo(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if ((operations.ClassStyle(target) & 3) == 0)
        {
            return false;
        }

        try
        {
            operations.Send(target, 0x14f, 1, 0, false, 0, 0);
            Yield(20, token);
            return true;
        }
        catch
        {
            // 表示通知中の取消でも表示状態を解除します。
            CloseCombo();
            throw;
        }
    }

    public void CloseCombo() => operations.Send(target, 0x14f, 0, 0, false, 0, 0);

    public void ActivateRoot(CancellationToken token) => operations.Activate(root, token);

    public void PrepareRoot(CancellationToken token)
    {
        Yield(1, token);
        if (!operations.IsWindow(root) || operations.IsHostWindow(root))
        {
            return;
        }

        operations.Send(root, 0, 0, 0, true, 8000, 0);
        token.ThrowIfCancellationRequested();
        Yield(1, token);
    }

    public void SetSelection(int index, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        operations.Send(target, combo ? 0x14eu : 0x186u, index, 0, false, 0, 0);
        token.ThrowIfCancellationRequested();
    }

    public nint GetSelection(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        nint selected = operations.Send(target, combo ? 0x147u : 0x188u, 0, 0, false, 0, 0);
        token.ThrowIfCancellationRequested();
        return selected;
    }

    public void LockUpdates(bool locked) => operations.LockUpdates(locked ? root : 0);

    /// <summary>
    /// BACK指定に応じて対象の入力接続とfocusを準備します。
    /// </summary>
    public void PrepareTarget(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!back)
        {
            uint current = operations.CurrentThread();

            uint targetThread = operations.WindowThread(root);
            if (operations.Attach(targetThread, current, true))
            {
                try
                {
                    operations.Foreground(root);
                    operations.Focus(target);
                }
                finally
                {
                    operations.Attach(targetThread, current, false);
                }
            }
        }
    }

    public void PressKey(int key, CancellationToken token)
    {
        PrepareTarget(token);

        try
        {
            operations.Priority(0x100);
            token.ThrowIfCancellationRequested();
            try
            {
                operations.Send(target, 0x100, key, 0, true, 1000, 2);
            }
            finally
            {
                // 取消時もkeyupを送り、送信前のpriority変更を終了します。
                operations.Send(target, 0x101, key, unchecked((nint)(int)0xc0000000), true, 1000, 2);
            }

            token.ThrowIfCancellationRequested();
            operations.Send(target, 0, 0, 0, true, 1000, 2);
            operations.Send(root, 0, 0, 0, true, 1000, 2);
            token.ThrowIfCancellationRequested();
        }
        finally
        {
            operations.Priority(0x20);
        }
    }

    public void Sleep(int milliseconds, CancellationToken token) => operations.Sleep(milliseconds, token);

    public void Yield(int iterations, CancellationToken token)
    {
        for (int index = 0; index < iterations; index++)
        {
            token.ThrowIfCancellationRequested();
            operations.Sleep(0, token);
            operations.Pump(token);
        }
    }
}

internal partial class NativeListSelectionPlatform : IListSelectionPlatform
{
    public virtual short KeyState(int key) => Native.GetKeyState(key);

    public virtual NativeControlBufferOwner CreateControlBufferOwner(nint root) => new(root);

    private readonly IUiaPreparationBackend preparation;

    internal NativeListSelectionPlatform()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        preparation = new NativeUiaPreparationBackend();
    }

    public virtual void Activate(nint root, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        AccessibleActivation.Prepare(root, new NativeAccessibleActivationBackend(), token);
    }

    public virtual bool IsWindow(nint window) => Native.IsWindow(window);

    public virtual bool IsHostWindow(nint window)
    {
        using var process = Process.GetCurrentProcess();
        return window == process.MainWindowHandle;
    }

    public virtual uint CurrentThread() => Native.GetCurrentThreadId();

    public virtual uint WindowThread(nint window) => Native.GetWindowThreadProcessId(window, out _);

    public virtual bool Attach(uint target, uint current, bool attach) => Native.AttachThreadInput(target, current, attach);

    public virtual void Foreground(nint window) => Native.SetForegroundWindow(window);

    public virtual void Focus(nint window) => Native.SetFocus(window);

    public virtual void Priority(uint value) => SetPriorityClass(GetCurrentProcess(), value);

    public virtual void LockUpdates(nint window) => LockWindowUpdate(window);

    public virtual nint Send(nint window, uint message, nint parameter, nint data,
        bool timed, uint timeout, uint flags)
    {
        if (!timed)
        {
            return Native.SendMessageW(window, message, parameter, data);
        }

        Native.SendMessageTimeoutW(window, message, parameter, data, flags, timeout, out nuint result);
        return unchecked((nint)result);
    }

    public virtual void Sleep(int milliseconds, CancellationToken token) => preparation.Sleep(milliseconds, token);

    public virtual void Pump(CancellationToken token) => preparation.Pump(token);

    public virtual uint ClassStyle(nint window) => GetClassLongW(window, -26);

    public virtual bool IsIconic(nint window) => Native.IsIconic(window);

    public virtual bool IsVisible(nint window) => Native.IsWindowVisible(window);

    public virtual nint ForegroundWindow() => Native.GetForegroundWindow();

    public virtual bool OwnsPointer(nint root, Native.Point point)
    {
        if (!Native.GetCursorPos(out Native.Point actual))
        {
            throw WindowsOperationErrors.Record(
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }

        if (actual.X != point.X || actual.Y != point.Y || !Native.IsWindow(root))
        {
            return false;
        }

        nint hit = Native.WindowFromPoint(actual);
        return hit != 0 && Native.GetAncestor(hit, 3) == Native.GetAncestor(root, 3);
    }

    public virtual Native.Rect Bounds(nint window)
    {
        if (Native.DwmGetWindowAttribute(window, 9, out Native.Rect rect, 16) != 0)
        {
            Native.GetWindowRect(window, out rect);
        }

        return rect;
    }

    public virtual Native.Point Cursor()
    {
        Native.GetCursorPos(out Native.Point point);
        return point;
    }

    public virtual void MoveCursor(Native.Point point)
    {
        if (!Native.SetCursorPos(point.X, point.Y))
        {
            throw WindowsOperationErrors.Record(
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }
    }

    public virtual Native.Point ToScreen(nint window, Native.Point point)
    {
        Native.ClientToScreen(window, ref point);
        return point;
    }

    public virtual void Post(nint window, uint message, nint parameter, nint data) =>
        Native.PostMessageW(window, message, parameter, data);

    public virtual int Metric(int index) => ImageNative.GetSystemMetrics(index);
}
