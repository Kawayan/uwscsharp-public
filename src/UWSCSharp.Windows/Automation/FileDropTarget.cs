using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

/// <summary>
/// 受信ウィンドウと、そのクライアント座標を保持します。
/// </summary>
internal readonly record struct FileDropDestination(nint Window, int X, int Y);

/// <summary>
/// 入力先を変えずに対象を復元し、所有する子の中から受信先を選びます。
/// </summary>
internal static partial class FileDropTarget
{
    private static readonly TimeSpan RestoreTimeout = TimeSpan.FromSeconds(2);
    private const int RestorePollMilliseconds = 25;

    internal static async ValueTask<FileDropDestination?> ResolveAsync(
        nint root, int? x, int? y, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RequireVisible(root);
        if (Native.IsIconic(root))
        {
            if (!ShowWindowAsync(root, 4))
            {
                throw NativeFailure();
            }

            var timer = Stopwatch.StartNew();
            while (Native.IsIconic(root))
            {
                token.ThrowIfCancellationRequested();
                RequireVisible(root);
                if (timer.Elapsed >= RestoreTimeout)
                {
                    throw new InvalidOperationException("送信先の最小化を解除できませんでした。");
                }

                await Task.Delay(RestorePollMilliseconds, token).ConfigureAwait(false);
            }
        }

        token.ThrowIfCancellationRequested();
        return Select(root, x, y, token);
    }

    private static FileDropDestination? Select(nint root,
        int? x, int? y, CancellationToken token)
    {
        using var dpi = new PhysicalDpiScope();
        RequireVisible(root);
        if (!Native.GetClientRect(root, out Native.Rect bounds))
        {
            throw NativeFailure();
        }

        int width = checked(bounds.Right - bounds.Left);
        int height = checked(bounds.Bottom - bounds.Top);
        var point = new Native.Point(x ?? width / 2, y ?? height / 2);
        if (point.X < 0 || point.Y < 0 || point.X >= width || point.Y >= height)
        {
            throw new ArgumentException("ドロップ座標がクライアント領域の外にあります。");
        }

        FileDropDestination? destination = null;
        var visited = new HashSet<nint>();
        nint current = root;
        while (visited.Add(current))
        {
            token.ThrowIfCancellationRequested();
            RequireVisible(current);
            if ((Native.GetWindowLongW(current, -20) & 0x10) != 0)
            {
                destination = new(current, point.X, point.Y);
            }

            nint child = ChildWindowFromPointEx(current, point, 7);
            if (child == 0 || child == current)
            {
                break;
            }

            if (!Native.IsChild(current, child))
            {
                throw new InvalidOperationException("送信先の子ウィンドウが変更されました。");
            }

            if (!Native.ClientToScreen(current, ref point)
                || !Native.ScreenToClient(child, ref point))
            {
                throw NativeFailure();
            }

            current = child;
        }

        token.ThrowIfCancellationRequested();
        return destination;
    }

    private static void RequireVisible(nint window)
    {
        if (window == 0 || !Native.IsWindow(window))
        {
            throw new InvalidOperationException("送信先のウィンドウが無効です。");
        }

        if (!Native.IsWindowVisible(window))
        {
            throw new InvalidOperationException("送信先のウィンドウが非表示です。");
        }
    }

    private static Win32Exception NativeFailure() => new(Marshal.GetLastPInvokeError());
}
