using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using UWSCSharp.Windows;
using static UWSCSharp.Wpf.VerificationAssert;

namespace UWSCSharp.Wpf;

internal static class ConfirmationDialogVerification
{
    internal static async Task ChooseAsync(nint owner,
        MessageBoxResult choice, string diagnostic,
        MessageBoxResult? expectedDefault = null)
    {
        const uint getOwner = 4;
        nint dialog = 0;
        nint button = 0;
        var name = new StringBuilder(32);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (dialog == 0)
        {
            Native.EnumWindows((candidate, _) =>
            {
                if (Native.GetWindow(candidate, getOwner) != owner
                    || !Native.IsWindowVisible(candidate))
                {
                    return true;
                }

                if (Native.GetClassNameW(candidate, name, name.Capacity) == 0
                    || !string.Equals(name.ToString(), "#32770", StringComparison.Ordinal))
                {
                    return true;
                }

                button = GetDlgItem(candidate, (int)choice);
                if (button == 0)
                {
                    return true;
                }

                dialog = candidate;
                return false;
            }, 0);
            if (dialog == 0)
            {
                await Task.Delay(10, deadline.Token).ConfigureAwait(false);
            }
        }

        try
        {
            Require(Native.IsWindowEnabled(button), "確認画面のボタンが有効");
            var text = new StringBuilder(512);
            bool foundText = false;
            Native.EnumChildWindows(dialog, (child, _) =>
            {
                Native.GetWindowTextW(child, text, text.Capacity);
                foundText = text.ToString().Contains(diagnostic,
                    StringComparison.Ordinal);
                return !foundText;
            }, 0);
            Require(foundText, "確認画面の文面が一致する");
            if (expectedDefault is MessageBoxResult expected)
            {
                Require(Native.SendMessageTimeoutW(dialog, 0x0400, 0, 0,
                    2, 1000, out nuint defaultButton) != 0
                    && (defaultButton & 0xffff) == (nuint)expected,
                    "確認画面の既定ボタンが一致する");
            }
        }
        finally
        {
            // 非アクティブな画面でもボタン通知を親へ届けます。
            const uint wmCommand = 0x0111;
            Require(Native.PostMessageW(dialog, wmCommand, (nint)choice, button),
                "確認画面の選択ボタンへ通知");
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetDlgItem(nint window, int id);
}
