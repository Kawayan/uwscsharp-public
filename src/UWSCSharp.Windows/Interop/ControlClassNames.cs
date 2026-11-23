namespace UWSCSharp.Windows;

/// <summary>
/// 標準コントロールのクラス名です。
/// </summary>
internal static class ControlClassNames
{
    internal const string Button = "Button";
    internal const string Edit = "Edit";
    internal const string Static = "Static";
    internal const string ListBox = "ListBox";
    internal const string ComboBox = "ComboBox";
    internal const string RichEdit50 = "RICHEDIT50W";
    internal const string ProgressBar = "msctls_progress32";
    internal const string Link = "SysLink";
    internal const string TreeView = "SysTreeView32";
    internal const string ListView = "SysListView32";
    internal const string Toolbar = "ToolbarWindow32";
    internal const string Tab = "SysTabControl32";
    internal const string StatusBar = "msctls_statusbar32";

    internal static class Uppercase
    {
        internal const string Button = "BUTTON";
        internal const string Edit = "EDIT";
        internal const string Static = "STATIC";
        internal const string ListBox = "LISTBOX";
        internal const string ComboBox = "COMBOBOX";
    }

    internal static bool Matches(string actual, string standard) =>
        actual.Equals(standard, StringComparison.OrdinalIgnoreCase)
        || actual.StartsWith("WindowsForms10." + standard + ".",
            StringComparison.OrdinalIgnoreCase);
}
