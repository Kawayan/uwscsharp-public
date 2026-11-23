using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;

namespace UWSCSharp.Windows;

internal sealed class NativeDirectInputBackend : IDirectInputBackend
{
    private static readonly object ModifierLock = new();

    /// <summary>
    /// 指定した送信先が有効かを確認します。
    /// </summary>
    public bool IsWindow(nint window) =>
        window != 0 && Native.IsWindow(window);

    /// <summary>
    /// 正規化前のキーからスキャンコードを取得します。
    /// </summary>
    public uint MapVirtualKey(uint key) =>
        Native.MapVirtualKeyW(key, 0);

    /// <summary>
    /// 呼出しスレッドのキー状態を取得します。
    /// </summary>
    public short GetKeyState(int key) => Native.GetKeyState(key);

    /// <summary>
    /// 対象のキューへ入力メッセージを登録します。
    /// </summary>
    public void Post(nint window, uint message, uint wParam,
        uint lParam)
    {
        using var dpi = new PhysicalDpiScope();
        Check(Native.PostMessageW(window, message,
            unchecked((nint)(nuint)wParam),
            unchecked((nint)(nuint)lParam)));
    }

    private static Native.Rect Bounds(nint window)
    {
        if (Native.DwmGetWindowAttribute(window, 9,
            out Native.Rect rect, Marshal.SizeOf<Native.Rect>()) != 0)
        {
            Check(Native.GetWindowRect(window, out rect));
        }

        return rect;
    }

    private static DirectTargetCandidate[] Candidates(
        nint root, int mode)
    {
        using var dpi = new PhysicalDpiScope();
        var candidates = new List<DirectTargetCandidate>();
        nint underCursor = Native.GetPhysicalCursorPos(out Native.Point cursor)
            ? Native.WindowFromPoint(cursor) : 0;
        Exception? enumerationError = null;
        Native.EnumChildWindows(root, (window, _) =>
        {
            try
            {
                if (!Native.IsWindow(window))
                {
                    return true;
                }

                var buffer = new StringBuilder(256);
                Native.RealGetWindowClassW(window, buffer,
                    (uint)buffer.Capacity);
                string label = DirectTargetSelector.NormalizeLabel(
                    buffer.ToString());
                if (label.Length != 0)
                {
                    candidates.Add(new(window, label,
                        Native.IsWindowVisible(window),
                        Native.IsWindowEnabled(window),
                        unchecked((uint)Native.GetWindowLongW(window, -16)),
                        Native.GetWindow(window, 5) != 0, Bounds(window),
                        window == underCursor));
                }

                return true;
            }
            catch (Win32Exception) when (!Native.IsWindow(window))
            {
                // 列挙途中で消滅した候補は対象から外します。
                return true;
            }
            catch (Exception ex)
            {
                // 管理例外をネイティブcallback境界の外へ直接流しません。
                enumerationError = ex;
                return false;
            }
        }, 0);
        if (enumerationError is not null)
        {
            ExceptionDispatchInfo.Capture(enumerationError).Throw();
        }

        return DirectTargetSelector.Select(candidates,
            Native.IsWindowVisible(root), mode);
    }

    /// <summary>
    /// 座標を含む候補を選び、子相対座標へ変換します。
    /// </summary>
    public DirectTarget SelectMouse(nint root,
        Native.Point position)
    {
        // 候補矩形・原点・相対座標を送信時と同じ物理座標系へ揃えます。
        using var dpi = new PhysicalDpiScope();
        DirectTargetCandidate[] candidates = Candidates(
            root, DirectTargetSelector.MouseMode);
        var origin = new Native.Point(0, 0);
        Check(Native.ClientToScreen(root, ref origin));
        var screen = new Native.Point(checked(origin.X + position.X),
            checked(origin.Y + position.Y));
        nint selected = DirectTargetSelector.HitTest(
            candidates, screen);
        if (selected == 0)
        {
            return new(root, position);
        }

        Native.Rect rect = candidates.First(
            candidate => candidate.Window == selected).Rect;
        return new(selected, new(checked(screen.X - rect.Left),
            checked(screen.Y - rect.Top)));
    }

    /// <summary>
    /// 記録したクリック位置またはEDIT系候補から送信先を選びます。
    /// </summary>
    public nint SelectKeyboard(nint root,
        Native.Point? lastClick)
    {
        if (lastClick is Native.Point point)
        {
            return SelectMouse(root, point).Window;
        }

        nint target = DirectTargetSelector.SelectOccurrence(
            Candidates(root, DirectTargetSelector.KeyboardMode), 1);
        return target == 0 ? root : target;
    }

    /// <summary>
    /// 修飾キーを一時設定して送信し、状態とスレッド接続を解除します。
    /// </summary>
    public void WithModifiers(nint window,
        DirectKeyboardState state, bool includeAlt, Action send)
    {
        if (state.Shift == 0 && state.Control == 0
            && (!includeAlt || state.Alt == 0))
        {
            send();
            return;
        }

        // Attachからdetachまで同一スレッド上で同期的に完了させます。
        lock (ModifierLock)
        {
            uint current = Native.GetCurrentThreadId();
            uint target = Native.GetWindowThreadProcessId(window, out _);
            bool attached = current != target;
            if (attached)
            {
                Check(Native.AttachThreadInput(current, target, true));
            }

            byte[] keyboard = new byte[256];
            bool modified = false;
            try
            {
                Check(Native.GetKeyboardState(keyboard));
                ApplyModifiers(keyboard, state, includeAlt);
                modified = true;
                Check(Native.SetKeyboardState(keyboard));
                send();
                Thread.Sleep(4);
            }
            finally
            {
                try
                {
                    if (modified)
                    {
                        // 原版は元の修飾状態を復元せず、該当6/9要素を0にします。
                        ClearModifiers(keyboard, includeAlt);
                        Check(Native.SetKeyboardState(keyboard));
                    }
                }
                finally
                {
                    if (attached)
                    {
                        Check(Native.AttachThreadInput(current, target, false));
                    }
                }
            }
        }
    }

    internal static void ApplyModifiers(byte[] keyboard,
        DirectKeyboardState state, bool includeAlt)
    {
        ClearModifiers(keyboard, includeAlt);
        Set(16, state.Shift);
        Set(17, state.Control);
        if (includeAlt)
        {
            Set(18, state.Alt);
        }

        void Set(int generic, int raw)
        {
            if (raw != 0)
            {
                keyboard[generic] = 0x80;
                keyboard[raw] |= 0x80;
            }
        }
    }

    internal static void ClearModifiers(byte[] keyboard,
        bool includeAlt)
    {
        keyboard[16] = 0;
        keyboard[17] = 0;
        keyboard[0xa0] = 0;
        keyboard[0xa1] = 0;
        keyboard[0xa2] = 0;
        keyboard[0xa3] = 0;
        if (includeAlt)
        {
            keyboard[18] = 0;
            keyboard[0xa4] = 0;
            keyboard[0xa5] = 0;
        }
    }

    private static void Check(bool success)
    {
        if (!success)
        {
            throw WindowsOperationErrors.Record(
                new Win32Exception(Marshal.GetLastWin32Error()));
        }
    }
}
