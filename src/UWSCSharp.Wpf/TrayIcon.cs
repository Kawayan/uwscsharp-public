using System.Drawing;
using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;

namespace UWSCSharp.Wpf;

/// <summary>
/// 通知領域のアイコンと操作メニューを管理します。
/// </summary>
internal sealed partial class TrayIcon : IDisposable
{
    private readonly Icon icon;
    private readonly Forms.ContextMenuStrip menu;
    private readonly Forms.NotifyIcon notification;
    private readonly Forms.Timer clickTimer = new();
    private readonly TrayLauncherClickState clickState = new();
    private bool disposed;

    internal TrayIcon(Action open, Action exit, Action? launch = null)
    {
        using Stream stream = Application.GetResourceStream(new Uri(
            "/UWSCSharp.Wpf;component/Assets/UWSCSharp.ico",
            UriKind.Relative))!.Stream;
        icon = new Icon(stream);
        menu = new Forms.ContextMenuStrip();
        menu.Items.Add("開く", null, (_, _) => open());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => exit());
        notification = new Forms.NotifyIcon
        {
            Icon = icon,
            Text = launch is null ? "UWSC# — ダブルクリックで開く"
                : "UWSC# — 左クリックでランチ、ダブルクリックで開く",
            ContextMenuStrip = menu
        };
        clickTimer.Tick += (_, _) =>
        {
            if (disposed || !notification.Visible)
            {
                clickTimer.Stop();
                clickState.Cancel();
                return;
            }

            if (!disposed && clickState.TakePending(Environment.TickCount64))
            {
                clickTimer.Stop();
                launch?.Invoke();
            }
        };
        notification.MouseClick += (_, e) =>
        {
            if (!disposed && notification.Visible && launch is not null
                && e.Button == Forms.MouseButtons.Left
                && clickState.Click(Environment.TickCount64, Forms.SystemInformation.DoubleClickTime))
            {
                clickTimer.Interval = Forms.SystemInformation.DoubleClickTime;
                clickTimer.Start();
            }
        };
        notification.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                clickTimer.Stop();
                clickState.DoubleClick(Environment.TickCount64, Forms.SystemInformation.DoubleClickTime);
                open();
            }
        };
    }

    internal bool Visible
    {
        get => notification.Visible;
        set
        {
            if (!value)
            {
                clickTimer.Stop();
                clickState.Cancel();
            }

            notification.Visible = value;
        }
    }

    /// <summary>
    /// 通知領域のアイコンとメニューを解放します。
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        clickTimer.Stop();
        clickState.Cancel();
        clickTimer.Dispose();
        notification.Dispose();
        menu.Dispose();
        icon.Dispose();
    }
}
