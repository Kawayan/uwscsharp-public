using System.Text;

namespace UWSCSharp.Windows;

internal enum NativeSelectionKind
{
    None,
    Combo,
    List,
    ListView,
    Tree
}

internal static class NativeSelectionTarget
{
    internal static string Label(nint window)
    {
        var buffer = new StringBuilder(256);
        Native.RealGetWindowClassW(window, buffer,
            (uint)buffer.Capacity);
        return DirectTargetSelector.NormalizeLabel(buffer.ToString());
    }

    // 634428の分岐順と62019Cの正規化済みラベルを使います。
    internal static NativeSelectionKind Kind(string label)
    {
        if (label.Contains(ControlClassNames.Uppercase.ComboBox, StringComparison.Ordinal)
            || label.Contains("COLORBOX", StringComparison.Ordinal))
        {
            return NativeSelectionKind.Combo;
        }

        if (label.Contains(ControlClassNames.Uppercase.ListBox, StringComparison.Ordinal))
        {
            return NativeSelectionKind.List;
        }

        if (label.Contains("LISTVIEW", StringComparison.Ordinal))
        {
            return NativeSelectionKind.ListView;
        }

        return label.Contains("TREEVIEW", StringComparison.Ordinal)
            ? NativeSelectionKind.Tree : NativeSelectionKind.None;
    }

    internal static bool IsNestedCombo(string label, nint parent,
        IReadOnlyList<(nint Window, string Label)> previous, bool listOnly = false)
    {
        return label.Contains(ControlClassNames.Uppercase.ComboBox, StringComparison.Ordinal)
            && previous.Any(candidate => candidate.Window == parent
                && candidate.Label.Contains("COMBOBOXEX", StringComparison.Ordinal))
            // 他の有効な型ラベルは、Comboの重複除外後にも受理されます。
            && !label.Contains("COLORBOX", StringComparison.Ordinal)
            && !label.Contains(ControlClassNames.Uppercase.ListBox, StringComparison.Ordinal)
            && (listOnly || !label.Contains("TREEVIEW", StringComparison.Ordinal))
            && (listOnly || !label.Contains("LISTVIEW", StringComparison.Ordinal));
    }
}
