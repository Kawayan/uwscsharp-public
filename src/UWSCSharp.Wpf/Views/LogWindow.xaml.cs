using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using UWSCSharp.Core;
using UWSCSharp.IO;
using UWSCSharp.Windows;

namespace UWSCSharp.Wpf;

/// <summary>
/// PRINTの表示履歴を、保存用ログとは独立して表示します。
/// </summary>
public partial class LogWindow : Window
{
    private bool ownerClosing;

    internal event EventHandler? HiddenByUser;

    /// <summary>
    /// 閉じる操作を非表示として扱うログウィンドウを作成します。
    /// </summary>
    public LogWindow()
    {
        InitializeComponent();
        SetFont(new ScriptLogFont());
        Closing += HideOnClosing;
        StateChanged += ApplyRequestedRestoredBounds;
    }

    internal void SetOutput(string text)
    {
        History.Text = text;
        History.ScrollToEnd();
    }

    internal void SetFont(ScriptLogFont font)
    {
        var fallback = new ScriptLogFont();
        FontFamily? family = Fonts.SystemFontFamilies.FirstOrDefault(candidate =>
            candidate.Source.Equals(font.Family, StringComparison.OrdinalIgnoreCase)
            || candidate.FamilyNames.Values.Any(name =>
                name.Equals(font.Family, StringComparison.OrdinalIgnoreCase)));
        History.FontFamily = family ?? new FontFamily(fallback.Family);
        double size = font.SizePoints * (96d / 72d);
        History.FontSize = double.IsFinite(size)
            && Control.FontSizeProperty.IsValidValue(size)
            ? size : fallback.SizePoints * (96d / 72d);
    }

    internal void Apply(ScriptLogView view)
    {
        bool changesBounds = view.X is not null || view.Y is not null
            || view.Width is not null || view.Height is not null;
        if (!view.Visible && changesBounds
            && WindowState == WindowState.Minimized)
        {
            ApplyRestoredBounds(view, updatePlacement: true);
            Hide();
            return;
        }

        if (view.Visible)
        {
            if (!IsVisible)
            {
                ShowWithoutActivation();
            }

            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
        }
        else
        {
            Hide();
        }

        ApplyRequestedRestoredBounds();

        if (changesBounds)
        {
            if (WindowState == WindowState.Maximized)
            {
                ApplyRestoredBounds(view, updatePlacement: false);
                return;
            }

            nint handle = new WindowInteropHelper(this).EnsureHandle();
            if (!GetWindowRect(handle, out NativeRect rect))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            SetPhysicalBounds(handle,
                view.X ?? rect.Left, view.Y ?? rect.Top,
                view.Width ?? rect.Right - rect.Left,
                view.Height ?? rect.Bottom - rect.Top);
        }
    }

    private void ShowWithoutActivation()
    {
        if (WindowState != WindowState.Maximized)
        {
            Show();
            return;
        }

        // WPFは最大化とShowActivated=falseを同時に指定した表示を拒否します。
        WindowState = WindowState.Normal;
        Show();
        WindowState = WindowState.Maximized;
    }

    private static void SetPhysicalBounds(nint handle,
        int x, int y, int width, int height)
    {
        uint dpi = GetDpiForWindow(handle);
        SetBounds(handle, x, y, width, height);
        if (GetDpiForWindow(handle) != dpi)
        {
            // WPFのDPI更新に伴うサイズ調整後に、物理ピクセル指定を反映します。
            SetBounds(handle, x, y, width, height);
        }
    }

    private static void SetBounds(nint handle,
        int x, int y, int width, int height)
    {
        if (!Native.SetWindowPos(handle, 0, x, y, width, height,
            0x0004 | 0x0010))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    internal void CloseForOwner()
    {
        ownerClosing = true;
        Close();
    }

    private void HideOnClosing(object? sender, CancelEventArgs e)
    {
        if (!ownerClosing)
        {
            e.Cancel = true;
            Hide();
            HiddenByUser?.Invoke(this, EventArgs.Empty);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);
}
