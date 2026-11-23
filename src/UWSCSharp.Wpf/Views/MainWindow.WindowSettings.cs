using System.IO;
using System.Windows;
using System.Windows.Media;
using UWSCSharp.IO;

namespace UWSCSharp.Wpf;

public partial class MainWindow
{
    private ApplicationWindowSize? requestedMainSize;
    private bool mainSizeEventsConnected;

    private void ApplyMainSize(ApplicationWindowSize? size)
    {
        requestedMainSize = size;
        if (!mainSizeEventsConnected)
        {
            SourceInitialized += (_, _) => UpdateMainSize();
            DpiChanged += (_, _) => UpdateMainSize();
            mainSizeEventsConnected = true;
        }

        UpdateMainSize();
    }

    private void UpdateMainSize()
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        // 既存の操作幅を下限とし、高さは編集欄などの内容に追従させます。
        MinWidth = 620;
        Width = requestedMainSize is { } size
            ? Math.Max(MinWidth, size.WidthPixels / dpi.DpiScaleX) : MinWidth;
        MinHeight = requestedMainSize is { } height
            ? height.HeightPixels / dpi.DpiScaleY : 0;
        SizeToContent = SizeToContent.Height;
    }

    private void WindowSettingsClick(object sender, RoutedEventArgs e)
    {
        if (cancellation is not null || recording.HasRecorder
            || recording.IsStarting || recording.IsStopping)
        {
            return;
        }

        var dialog = new WindowSettingsWindow { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is { } settings)
        {
            Topmost = settings.StayOnTop;
            ApplyMainSize(settings.MainSize);
        }
    }

    internal async Task ApplyStartupWindowSettingsAsync(string? path = null)
    {
        try
        {
            ApplicationWindowSettings settings = await ApplicationWindowSettings.LoadAsync(path);
            Topmost = settings.StayOnTop;
            ApplyMainSize(settings.MainSize);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Topmost = false;
            StatusLabel.Text = "本体設定を読み込めないため最前面固定を無効にします。" + error.Message;
        }
    }
}
