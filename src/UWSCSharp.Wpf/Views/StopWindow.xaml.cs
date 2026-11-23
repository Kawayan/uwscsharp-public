using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using UWSCSharp.Core;
using UWSCSharp.IO;
using UWSCSharp.Windows;

namespace UWSCSharp.Wpf;

/// <summary>
/// 本体を最小化しても操作できる再生コントロールです。
/// </summary>
public partial class StopWindow : Window
{
    private bool ownerClosing;
    private ApplicationWindowSize? requestedSize;
    private ScriptWindowPosition? pendingPosition;
    private PlaybackControlView view =
        PlaybackControlView.Create(false, false, false, false);

    internal event EventHandler? StopRequested;

    internal event EventHandler? PauseRequested;

    /// <summary>
    /// 実行中だけ表示する操作画面を初期化します。
    /// </summary>
    public StopWindow()
    {
        InitializeComponent();
        Title = ApplicationInfo.PlaybackWindowTitle;
        // 初回だけ中央へ配置し、その後は利用者が移動した位置を保ちます。
        Loaded += (_, _) => WindowStartupLocation = WindowStartupLocation.Manual;
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() => SetSize(requestedSize));
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Normal && pendingPosition is { } position)
            {
                SetPosition(position);
            }
        };
    }

    internal void ShowForPlayback(string path, PlaybackControlView state)
    {
        PlaybackFile.Text = Path.GetFileName(path);
        PlaybackFile.ToolTip = path;
        Apply(state);
        Show();
    }

    internal void SetSize(ApplicationWindowSize? size)
    {
        requestedSize = size;
        Width = 320;
        MinHeight = 0;
        UpdateLayout();
        if (size is null)
        {
            return;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        nint handle = new WindowInteropHelper(this).EnsureHandle();
        int minimumHeight = checked((int)Math.Ceiling(ActualHeight * dpi.DpiScaleY));
        (int width, int height) = WindowsStopFormLayout.ConstrainSize(handle,
            size.WidthPixels, size.HeightPixels,
            checked((int)Math.Ceiling(MinWidth * dpi.DpiScaleX)), Math.Max(1, minimumHeight));
        Width = width / dpi.DpiScaleX;
        MinHeight = height / dpi.DpiScaleY;
        UpdateLayout();
        WindowsStopFormLayout.Apply(handle, null, null);
    }

    internal void Apply(PlaybackControlView state)
    {
        view = state;
        PlaybackStatus.Text = state.Status;
        PausePlaybackButton.Content = state.PauseCaption;
        PausePlaybackButton.IsEnabled = state.CanPause;
        StopPlaybackButton.IsEnabled = state.CanStop;
    }

    internal void FinishPlayback()
    {
        Apply(PlaybackControlView.Create(false, false, false, false));
        Hide();
    }

    internal void ApplyRequest(ScriptStopForm request, PlaybackControlView state)
    {
        Apply(state);
        if (!request.Visible)
        {
            Hide();
            return;
        }

        Show();
        WindowState = WindowState.Normal;
        UpdateLayout();
        WindowsStopFormLayout.Apply(new WindowInteropHelper(this).EnsureHandle(), request.X, request.Y);
        if (!Activate())
        {
            throw new InvalidOperationException("Windowsが停止画面の前面化を許可しませんでした。");
        }

        StopPlaybackButton.Focus();
    }

    internal bool IsTopMost() =>
        WindowsStopFormLayout.IsTopMost(new WindowInteropHelper(this).EnsureHandle());

    internal (int X, int Y, int Width, int Height) SetPosition(ScriptWindowPosition request)
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        nint handle = new WindowInteropHelper(this).EnsureHandle();
        if (WindowState != WindowState.Normal)
        {
            pendingPosition = request;
            return WindowsScriptWindowLayout.StopBounds(handle, request);
        }

        pendingPosition = null;
        UpdateLayout();
        WindowsStopFormLayout.Apply(handle, request.X, request.Y);
        return WindowsScriptWindowLayout.ReadBounds(handle);
    }

    internal void SetTopMost(bool value)
    {
        Topmost = value;
        WindowsStopFormLayout.SetTopMost(new WindowInteropHelper(this).EnsureHandle(), value);
    }

    internal void CloseForOwner()
    {
        ownerClosing = true;
        Close();
    }

    private void StopPlaybackClick(object sender, RoutedEventArgs e)
    {
        if (view.CanStop)
        {
            StopRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void PausePlaybackClick(object sender, RoutedEventArgs e)
    {
        if (view.CanPause)
        {
            PauseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void StopWindowClosing(object? sender, CancelEventArgs e)
    {
        if (ownerClosing)
        {
            return;
        }

        e.Cancel = true;
        if (view.CanStop)
        {
            StopRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (!view.IsActive)
        {
            Hide();
        }
    }
}
