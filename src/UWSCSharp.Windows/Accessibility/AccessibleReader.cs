using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace UWSCSharp.Windows;

[SupportedOSPlatform("windows")]
internal static partial class AccessibleReader
{
    internal static void Location(IAccessibleNative target,
        out int x, out int y, out int width, out int height, object child)
    {
        (int Left, int Top, int Width, int Height) bounds = WindowsOperationErrors.Provider(() =>
        {
            target.Location(out int left, out int top,
                out int horizontal, out int vertical, child);
            return (left, top, horizontal, vertical);
        });
        x = bounds.Left;
        y = bounds.Top;
        width = bounds.Width;
        height = bounds.Height;
    }

    internal static string Read(nint root, int screenX, int screenY,
        int mode, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        IAccessibleNative? accessible = null;
        List<object> owned = [];
        Exception? cleanupPrimary1 = null;
        try
        {
            Guid iid = typeof(IAccessibleNative).GUID;
            object child = 0;
            int result = root == 0
                ? AccessibleObjectFromPoint(new Native.Point(screenX, screenY),
                    out accessible, out child)
                : AccessibleObjectFromWindow(root, 0xfffffffcu, ref iid, out accessible);
            if (child is not null && Marshal.IsComObject(child))
            {
                owned.Add(child);
            }

            if (result == unchecked((int)0x80004002))
            {
                return "";
            }

            WindowsOperationErrors.ThrowIfFailed(result);
            token.ThrowIfCancellationRequested();
            if (accessible is null || child is not int initialChild || initialChild < 0)
            {
                return "";
            }

            IAccessibleNative target = accessible;
            for (int depth = 0; root != 0 && depth < 64; depth++)
            {
                token.ThrowIfCancellationRequested();
                object? hit = WindowsOperationErrors.Provider(
                    () => target.HitTest(screenX, screenY));
                if (hit is not null && Marshal.IsComObject(hit))
                {
                    owned.Add(hit);
                }

                if (hit is IAccessibleNative element)
                {
                    if (ReferenceEquals(element, target))
                    {
                        break;
                    }

                    target = element;
                }
                else if (hit is int childId && childId >= 0)
                {
                    child = childId;
                    break;
                }
                else
                {
                    return "";
                }

                if (depth == 63)
                {
                    throw WindowsOperationErrors.Record(
                        new InvalidOperationException("accessibilityの探索深度が上限を超えました。"));
                }
            }

            string text;
            switch (mode)
            {
                case 3:
                    text = WindowsOperationErrors.Provider(() => target.Name(child)) ?? "";
                    break;
                case 4:
                    text = WindowsOperationErrors.Provider(() => target.Value(child)) ?? "";
                    break;
                case 5:
                    text = RoleText(WindowsOperationErrors.Provider(() => target.Role(child)));
                    break;
                case 6:
                    text = StateText(WindowsOperationErrors.Provider(() => target.State(child)));
                    break;
                case 7:
                    text = WindowsOperationErrors.Provider(() => target.Description(child)) ?? "";
                    break;
                case 8:
                    AccessibleReader.Location(target, out int x, out int y, out int width, out int height, child);
                    if (root != 0)
                    {
                        var position = new Native.Point(x, y);
                        if (!Native.ScreenToClient(root, ref position))
                        {
                            throw WindowsOperationErrors.Record(
                                new Win32Exception(Marshal.GetLastWin32Error()));
                        }

                        x = position.X;
                        y = position.Y;
                    }

                    text = FormattableString.Invariant($"{x},{y},{width},{height}");
                    break;
                default:
                    text = WindowsOperationErrors.Provider(() => target.Name(child)) ?? "";
                    if (text.Length == 0)
                    {
                        text = WindowsOperationErrors.Provider(() => target.Value(child)) ?? "";
                    }

                    break;
            }

            token.ThrowIfCancellationRequested();
            return text;
        }
        catch (Exception failure1)
        {
            cleanupPrimary1 = failure1;
            throw;
        }
        finally
        {
            var cleanup1 = new ProviderCleanup(cleanupPrimary1);
            for (int i = owned.Count - 1; i >= 0; i--)
            {
                cleanup1.Release(owned[i]);
            }

            if (accessible is not null)
            {
                cleanup1.Release(accessible);
            }
            cleanup1.Finish();
        }
    }

    internal static string RoleText(object? role)
    {
        if (role is not int value)
        {
            return Convert.ToString(role, CultureInfo.InvariantCulture) ?? "";
        }

        uint length = GetRoleTextW(unchecked((uint)value), null, 0);
        var text = new StringBuilder(checked((int)length + 1));
        GetRoleTextW(unchecked((uint)value), text, (uint)text.Capacity);
        return text.ToString();
    }

    internal static string StateText(object? state)
    {
        if (state is not int value)
        {
            return Convert.ToString(state, CultureInfo.InvariantCulture) ?? "";
        }

        List<string> states = [];
        for (int index = 0; index < 32; index++)
        {
            uint bit = 1u << index;
            if (value != 0 && (unchecked((uint)value) & bit) == 0)
            {
                continue;
            }

            uint flag = value == 0 ? 0 : bit;
            uint length = GetStateTextW(flag, null, 0);
            var text = new StringBuilder(checked((int)length + 1));
            GetStateTextW(flag, text, (uint)text.Capacity);
            if (text.Length != 0)
            {
                states.Add(text.ToString());
            }

            if (value == 0)
            {
                break;
            }
        }

        return string.Join(' ', states);
    }

    internal static bool InvokeButtons(IReadOnlyList<nint> buttons,
        string name, int occurrence, CancellationToken token)
    {
        string desired = ButtonName.Normalize(name);
        int matched = 0;
        foreach (nint button in buttons)
        {
            token.ThrowIfCancellationRequested();
            IAccessibleNative? accessible = null;
            Exception? cleanupPrimary2 = null;
            try
            {
                Guid iid = typeof(IAccessibleNative).GUID;
                int result = AccessibleObjectFromWindow(button, 0xfffffffcu, ref iid, out accessible);
                if (result == unchecked((int)0x80004002))
                {
                    continue;
                }

                WindowsOperationErrors.ThrowIfFailed(result);
                if (accessible is null || WindowsOperationErrors.Provider(() => accessible.Role(0)) is not int role || role != 43
                    || WindowsOperationErrors.Provider(() => accessible.State(0)) is not int state || (state & 0x18001) != 0
                    || !ButtonName.Normalize(WindowsOperationErrors.Provider(() => accessible.Name(0)) ?? "").Equals(
                        desired, StringComparison.OrdinalIgnoreCase)
                    || ++matched != occurrence)
                {
                    continue;
                }

                token.ThrowIfCancellationRequested();
                WindowsOperationErrors.Provider(() => accessible.DoDefaultAction(0));
                token.ThrowIfCancellationRequested();
                return true;
            }
            catch (Exception failure2)
            {
                cleanupPrimary2 = failure2;
                throw;
            }
            finally
            {
                var cleanup2 = new ProviderCleanup(cleanupPrimary2);
                if (accessible is not null)
                {
                    cleanup2.Release(accessible);
                }
                cleanup2.Finish();
            }
        }

        token.ThrowIfCancellationRequested();
        return false;
    }
}

// oleacc.hのIAccessible順序を維持し、dual interfaceのHRESULTを変換します。
[ComImport]
[Guid("618736e0-3c3d-11cf-810c-00aa00389b71")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IAccessibleNative
{
    [return: MarshalAs(UnmanagedType.IDispatch)]
    object? Parent();

    int ChildCount();

    [return: MarshalAs(UnmanagedType.IDispatch)]
    object? Child([MarshalAs(UnmanagedType.Struct)] object child);

    [return: MarshalAs(UnmanagedType.BStr)]
    string? Name([MarshalAs(UnmanagedType.Struct)] object child);

    [return: MarshalAs(UnmanagedType.BStr)]
    string? Value([MarshalAs(UnmanagedType.Struct)] object child);

    [return: MarshalAs(UnmanagedType.BStr)]
    string? Description([MarshalAs(UnmanagedType.Struct)] object child);

    [return: MarshalAs(UnmanagedType.Struct)]
    object? Role([MarshalAs(UnmanagedType.Struct)] object child);

    [return: MarshalAs(UnmanagedType.Struct)]
    object? State([MarshalAs(UnmanagedType.Struct)] object child);

    [return: MarshalAs(UnmanagedType.BStr)]
    string? Help([MarshalAs(UnmanagedType.Struct)] object child);

    int HelpTopic([MarshalAs(UnmanagedType.BStr)] out string? file,
        [MarshalAs(UnmanagedType.Struct)] object child);

    [return: MarshalAs(UnmanagedType.BStr)]
    string? KeyboardShortcut([MarshalAs(UnmanagedType.Struct)] object child);

    [return: MarshalAs(UnmanagedType.Struct)]
    object? Focus();

    [return: MarshalAs(UnmanagedType.Struct)]
    object? Selection();

    [return: MarshalAs(UnmanagedType.BStr)]
    string? DefaultAction([MarshalAs(UnmanagedType.Struct)] object child);

    void Select(int flags, [MarshalAs(UnmanagedType.Struct)] object child);

    void Location(out int x, out int y, out int width, out int height,
        [MarshalAs(UnmanagedType.Struct)] object child);

    [return: MarshalAs(UnmanagedType.Struct)]
    object? Navigate(int direction, [MarshalAs(UnmanagedType.Struct)] object child);

    [return: MarshalAs(UnmanagedType.Struct)]
    object? HitTest(int x, int y);

    void DoDefaultAction([MarshalAs(UnmanagedType.Struct)] object child);

    void SetName([MarshalAs(UnmanagedType.Struct)] object child,
        [MarshalAs(UnmanagedType.BStr)] string name);

    void SetValue([MarshalAs(UnmanagedType.Struct)] object child,
        [MarshalAs(UnmanagedType.BStr)] string value);
}
