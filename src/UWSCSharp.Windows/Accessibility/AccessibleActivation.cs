using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace UWSCSharp.Windows;

internal interface IAccessibleActivationBackend
{
    bool IsWindow(nint window);
    bool AnyPopup();
    nint Parent(nint window);
    nint Foreground();
    string ClassName(nint window);
    string Title(nint window);
    int ExtendedStyle(nint window);
    uint ThreadId(nint window);
    void Attach(uint target, uint foreground, bool attach);
    void SetForeground(nint window);
    void BringToTop(nint window);
    uint LockTimeout();
    void SetLockTimeout(uint value);
    void Yield(CancellationToken token);
    nint ApplicationWindow();
}

internal static class AccessibleActivation
{
    private static readonly object Gate = new();

    internal static void Prepare(nint window,
        IAccessibleActivationBackend backend, CancellationToken token)
    {
        lock (Gate)
        {
            token.ThrowIfCancellationRequested();
            if (!backend.IsWindow(window) || Excluded(window, backend))
            {
                return;
            }

            Activate(window, backend, token);
            if (backend.Foreground() != window)
            {
                uint timeout = backend.LockTimeout();
                try
                {
                    backend.SetLockTimeout(0);
                    backend.Yield(token);
                    Activate(window, backend, token);
                    if (backend.Foreground() != window)
                    {
                        backend.Yield(token);
                        Activate(backend.ApplicationWindow(), backend, token);
                        backend.Yield(token);
                        Activate(window, backend, token);
                    }
                }
                finally
                {
                    // 取消やmessage処理の例外でも設定を復元します。
                    backend.SetLockTimeout(timeout);
                }
            }

            token.ThrowIfCancellationRequested();
            if ((backend.ExtendedStyle(window) & 0x40) != 0)
            {
                nint parent = backend.Parent(window);
                for (int level = 0; level < 2 && parent != 0; level++)
                {
                    nint ancestor = backend.Parent(parent);
                    if (ancestor == 0)
                    {
                        break;
                    }

                    parent = ancestor;
                }

                if (parent != 0)
                {
                    Activate(parent, backend, token);
                }
            }
        }
    }

    private static bool Excluded(nint window,
        IAccessibleActivationBackend backend)
    {
        if (!backend.AnyPopup())
        {
            return false;
        }

        nint parent = backend.Parent(window);
        if (parent != 0 && parent != backend.Foreground())
        {
            return false;
        }

        string label = backend.ClassName(window).ToUpperInvariant();
        return backend.Title(window).Length == 0
            && (label is "#32768" or "BASEBAR"
                or "MOZILLADROPSHADOWWINDOWCLASS"
                || label.Contains("POPUP", StringComparison.Ordinal)
                || label.Contains("UI TOOL WINDOW", StringComparison.Ordinal));
    }

    private static void Activate(nint window,
        IAccessibleActivationBackend backend, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        nint foreground = backend.Foreground();
        if (foreground == window)
        {
            return;
        }

        uint foregroundThread = backend.ThreadId(foreground);
        uint targetThread = backend.ThreadId(window);
        try
        {
            backend.Attach(targetThread, foregroundThread, true);
            backend.SetForeground(window);
        }
        finally
        {
            // 接続要求が失敗した場合も原版と同じ解除要求を行います。
            backend.Attach(targetThread, foregroundThread, false);
        }

        backend.BringToTop(window);
        token.ThrowIfCancellationRequested();
    }
}

[SupportedOSPlatform("windows")]
internal sealed partial class NativeAccessibleActivationBackend : IAccessibleActivationBackend
{
    private readonly NativeUiaPreparationBackend preparation = new();

    public bool IsWindow(nint window) => Native.IsWindow(window);
    public bool AnyPopup() => NativeAnyPopup();
    public nint Parent(nint window) => Native.GetParent(window);
    public nint Foreground() => Native.GetForegroundWindow();
    public string ClassName(nint window) => Text(window, true);
    public string Title(nint window) => Text(window, false);
    public int ExtendedStyle(nint window) => Native.GetWindowLongW(window, -20);
    public uint ThreadId(nint window) => Native.GetWindowThreadProcessId(window, out _);
    public void Attach(uint target, uint foreground, bool attach)
        => Native.AttachThreadInput(target, foreground, attach);
    public void SetForeground(nint window) => Native.SetForegroundWindow(window);
    public void BringToTop(nint window) => BringWindowToTop(window);

    public uint LockTimeout()
    {
        uint value = 0;
        SystemParametersInfoW(0x2000, 0, ref value, 0);
        return value;
    }

    public void SetLockTimeout(uint value)
        => SetSystemParametersInfoW(0x2001, 0, (nint)value, 0);

    public void Yield(CancellationToken token)
    {
        preparation.Sleep(0, token);
        preparation.Pump(token);
    }

    public nint ApplicationWindow()
    {
        using var process = Process.GetCurrentProcess();
        return process.MainWindowHandle;
    }

    private static string Text(nint window, bool className)
    {
        var buffer = new StringBuilder(301);
        if (className)
        {
            Native.GetClassNameW(window, buffer, buffer.Capacity);
        }
        else
        {
            Native.GetWindowTextW(window, buffer, buffer.Capacity);
        }

        return buffer.ToString();
    }
}
