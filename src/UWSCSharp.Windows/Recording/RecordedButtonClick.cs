using System.Globalization;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

// 操作時に識別したボタンを、座標に依存しない命令に変換します。
internal sealed record RecordedButtonClick(
    RecordedWindow Window, string Caption, int Occurrence, int State = 1,
    bool Checkbox = false)
{
    internal static RecordedButtonClick? Capture(nint target, bool checkbox = false)
    {
        if (!string.Equals(NativeSelectionTarget.Label(target),
            ControlClassNames.Uppercase.Button, StringComparison.Ordinal)
            || (checkbox
                ? (Native.GetWindowLongW(target, Native.GwlStyle) & 15) != 3
                : (Native.GetWindowLongW(target, Native.GwlStyle) & 15) is not (0 or 1))
            || RecordedWindow.Capture(target) is not RecordedWindow window)
        {
            return null;
        }

        string caption = NativeButtonControl.ReadName(target, default);
        if (string.IsNullOrWhiteSpace(caption))
        {
            return null;
        }

        int occurrence = 0;
        foreach (nint candidate in WindowsAutomation.FindRecordedButtons(
            window.Handle, caption))
        {
            occurrence++;
            if (candidate == target)
            {
                return new(window, caption, occurrence, Checkbox: checkbox);
            }
        }

        return null;
    }

    internal string Command(bool background = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(Caption);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Occurrence);
        string window = Window.Identifier();
        string caption = RecordedWindow.Literal(Caption);
        ClickItemFlags mode = ClickItemFlags.Button;
        if (background || Checkbox)
        {
            mode |= ClickItemFlags.Api;
        }

        if (background)
        {
            mode |= ClickItemFlags.Background;
        }

        string flags = ((int)mode).ToString(CultureInfo.InvariantCulture);
        string number = Occurrence.ToString(CultureInfo.InvariantCulture);
        string state = State.ToString(CultureInfo.InvariantCulture);
        return $"{BuiltinFunctionNames.ClkItem}({window},{caption},{flags},{state},{number})";
    }
}
