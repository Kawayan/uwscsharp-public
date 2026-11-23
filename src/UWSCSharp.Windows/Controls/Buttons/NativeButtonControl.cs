using System.Text;

namespace UWSCSharp.Windows;

internal static class NativeButtonControl
{
    internal static bool IsClass(string label)
        => label.Contains(ControlClassNames.Uppercase.Button, StringComparison.Ordinal)
            || label.Contains("BTN", StringComparison.Ordinal)
            || label.Contains("CHECKBOX", StringComparison.Ordinal)
            || label.Contains("3STATE", StringComparison.Ordinal);

    internal static string ReadName(nint window, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var buffer = new StringBuilder(301);
        // 原本の通常caption取得は長さ問い合わせも成否分岐も行いません。
        Native.GetWindowTextW(window, buffer, buffer.Capacity);
        token.ThrowIfCancellationRequested();
        return buffer.ToString();
    }

    internal static bool Click(nint root, nint target, int request, int kind,
        IListSelectionPlatform platform, CancellationToken token,
        IButtonAccessibleOperations? accessible = null)
    {
        token.ThrowIfCancellationRequested();
        bool back = (kind & 512) != 0;
        var preparation = new NativeListSelectionBackend(root, target,
            combo: false, back: back, platform: platform);
        if (!back)
        {
            preparation.ActivateRoot(token);
        }

        int role = 0;
        bool accessibleAction = false;
        int before = Read(root, target, accessible, token,
            ref role, ref accessibleAction, classify: true);
        bool retry = role == 44;
        token.ThrowIfCancellationRequested();
        if ((kind & 1024) != 0)
        {
            Native.Rect bounds = platform.Bounds(target);
            int x = WindowsOperationErrors.Add(bounds.Left,
                WindowsOperationErrors.Subtract(bounds.Right, bounds.Left) / 2);
            int y = WindowsOperationErrors.Add(bounds.Top,
                WindowsOperationErrors.Subtract(bounds.Bottom, bounds.Top) / 2);
            platform.MoveCursor(new Native.Point(x, y));
            token.ThrowIfCancellationRequested();
        }

        if ((kind & 0x3800) != 0)
        {
            NativeControlMouse.Post(target, kind, new Native.Point(0, 0), platform, token);
            return true;
        }

        int desired = request is >= 0 and <= 2 ? request : 1;
        if (before == desired)
        {
            preparation.PrepareTarget(token);
            return true;
        }

        Act(false);
        if (retry)
        {
            preparation.Sleep(20, token);
            preparation.Yield(20, token);
            preparation.Sleep(20, token);
            preparation.Yield(20, token);
            token.ThrowIfCancellationRequested();
            int after = Read(root, target, accessible, token,
                ref role, ref accessibleAction, classify: false);
            token.ThrowIfCancellationRequested();
            if ((before == 2 || after == 2 || desired == 2) && after != desired)
            {
                Act(true);
            }
        }

        return true;

        void Act(bool additional)
        {
            if (accessibleAction && accessible is not null)
            {
                if (additional && !back)
                {
                    preparation.ActivateRoot(token);
                }

                // 原本は補助操作のboolを名前一致成功へ反映しません。
                accessible.Act(root, target, token);
                token.ThrowIfCancellationRequested();
            }
            else
            {
                NativeControlMouse.Post(target, 0, new Native.Point(0, 0), platform, token);
            }
        }
    }

    internal static int Read(nint root, nint target,
        IButtonAccessibleOperations? accessible, CancellationToken token,
        ref int role, ref bool accessibleAction, bool classify)
    {
        token.ThrowIfCancellationRequested();
        if (accessible is not null && accessible.Matches(root, token))
        {
            int state = accessible.Read(root, target, token,
                ref role, out accessibleAction);
            token.ThrowIfCancellationRequested();
            return state;
        }

        int result = unchecked((int)Native.SendMessageW(target, 0xf0, 0, 0));
        token.ThrowIfCancellationRequested();
        if (classify)
        {
            var name = new StringBuilder(256);
            Native.GetClassNameW(target, name, name.Capacity);
            string className = NativeListNameMatcher.AsciiUpper(name.ToString());
            if (className.Contains("CHECKBOX", StringComparison.Ordinal)
                || className.Contains("3STATE", StringComparison.Ordinal))
            {
                role = 44;
            }
        }

        // 再取得のnative分岐では以前のrole/操作byteを書き換えません。
        token.ThrowIfCancellationRequested();
        return result;
    }
}
