using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using UWSCSharp.IO;

namespace UWSCSharp.Wpf;

internal sealed class WindowSettingsWindow : Window
{
    private readonly CheckBox stayOnTop = new()
    {
        Content = "本体を最前面に固定する",
        IsEnabled = false
    };
    private readonly Button save = new()
    {
        Content = "保存",
        IsDefault = true,
        IsEnabled = false,
        Padding = new Thickness(8)
    };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox family = new() { IsEnabled = false };
    private readonly TextBox size = new() { IsEnabled = false };
    private readonly TextBox widthPixels = new() { IsEnabled = false };
    private readonly TextBox heightPixels = new() { IsEnabled = false };
    private readonly TextBox stopWidthPixels = new() { IsEnabled = false };
    private readonly TextBox stopHeightPixels = new() { IsEnabled = false };
    private readonly GroupBox recordingOptions = new()
    {
        Header = "記録設定",
        IsEnabled = false,
        Margin = new Thickness(0, 0, 0, 8)
    };
    private readonly CheckBox omitIdle;
    private readonly CheckBox relativeMouse;
    private readonly CheckBox highLevel;
    private readonly CheckBox background;
    private readonly CheckBox copyRecording;
    private readonly RecordingSettings initialRecordingSettings;
    private readonly string recordingSettingsPath;
    private ApplicationWindowSettings? savedWindowSettings;
    private bool saving;

    internal ApplicationWindowSettings? Result
    {
        get; private set;
    }

    internal RecordingSettings? RecordingResult { get; private set; }

    internal WindowSettingsWindow(RecordingSettings recordingSettings, string recordingSettingsPath)
    {
        initialRecordingSettings = recordingSettings;
        this.recordingSettingsPath = recordingSettingsPath;
        Title = "本体設定";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        NameScope.SetNameScope(this, new NameScope());
        var panel = new StackPanel { Margin = new Thickness(16) };
        MaxHeight = SystemParameters.WorkArea.Height;
        Content = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        RegisterName("StayOnTop", stayOnTop);
        panel.Children.Add(stayOnTop);
        panel.Children.Add(new TextBlock
        {
            Text = "保存後に本体へ反映し、次回起動時にも適用します。停止画面の最前面設定とは独立しています。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 8)
        });
        AddField(panel, "ダイアログのフォント名", "DialogFontFamily", family);
        AddField(panel, "文字サイズ（整数pt）", "DialogFontSize", size);
        panel.Children.Add(new TextBlock
        {
            Text = "フォントは次の実行からMSGBOX・INPUT・SLCTBOXに適用します。実行内のOPTIONを優先します。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
        AddField(panel, "本体の幅（物理ピクセル）", "MainWidthPixels", widthPixels);
        AddField(panel, "本体の高さ（物理ピクセル）", "MainHeightPixels", heightPixels);
        panel.Children.Add(new TextBlock
        {
            Text = "操作に必要な最小サイズより小さい場合は、表示時に補正します。両欄が空の場合は現在の寸法設定を保持します。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
        AddField(panel, "停止画面の幅（物理ピクセル）", "StopWidthPixels", stopWidthPixels);
        AddField(panel, "停止画面の高さ（物理ピクセル）", "StopHeightPixels", stopHeightPixels);
        panel.Children.Add(new TextBlock
        {
            Text = "停止画面の寸法は次の実行から適用します。操作に必要な寸法と画面の作業領域に合わせて補正し、両欄が空なら現在の設定を保持します。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
        var recordingPanel = new StackPanel { Margin = new Thickness(8, 4, 8, 4) };
        omitIdle = AddRecordingOption(recordingPanel, "OmitIdleRecordingCheckBox",
            "余分な時間、マウス移動は記録しない", recordingSettings.OmitIdleTimeAndMouseMoves,
            "SLEEPと、ボタンを押していない間のMMVを省略します。クリックの座標とドラッグ中の移動は記録します。記録開始時の設定を使用します。");
        relativeMouse = AddRecordingOption(recordingPanel, "RelativeMouseRecordingCheckBox",
            "マウス座標を相対座標で記録する", recordingSettings.RelativeMouseCoordinates,
            "アクティブウインドウの左上（枠を含む）を基準に、ACWとSTATUSの座標式で記録します。記録開始時の設定を使用します。");
        highLevel = AddRecordingOption(recordingPanel, "HighLevelRecordingCheckBox",
            "高レベル記録（Win32ボタン）", recordingSettings.RecordButtonClicks,
            "対象の左クリックをCLKITEMで記録します。記録開始時の設定を使用します。");
        background = AddRecordingOption(recordingPanel, "BackgroundRecordingCheckBox",
            "背景再生用に記録する（Win32ボタン）", recordingSettings.BackgroundPlayback,
            "対象を前面に出さずに再生できるCLKITEMを生成します。");
        copyRecording = AddRecordingOption(recordingPanel, "CopyRecordingCheckBox",
            "記録後クリップボードへコピーする", recordingSettings.CopyAfterRecording,
            "正常に開始した記録を停止操作で終了した場合だけコピーします。警告・上限超過・画面終了時の回収は除外します。");
        recordingOptions.Content = recordingPanel;
        panel.Children.Add(recordingOptions);
        RegisterName("SettingsError", error);
        AutomationProperties.SetName(error, "本体設定のエラー");
        panel.Children.Add(error);
        RegisterName("SaveSettings", save);
        panel.Children.Add(save);
        panel.Children.Add(new Button
        {
            Content = "キャンセル",
            IsCancel = true,
            Padding = new Thickness(8),
            Margin = new Thickness(0, 4, 0, 0)
        });
        Loaded += async (_, _) => await LoadAsync();
        save.Click += async (_, _) => await SaveAsync();
        Closing += (_, args) => args.Cancel = saving;
    }

    private void AddField(Panel panel, string label, string name, TextBox box)
    {
        panel.Children.Add(new TextBlock { Text = label });
        box.Margin = new Thickness(0, 4, 0, 8);
        AutomationProperties.SetName(box, label);
        RegisterName(name, box);
        panel.Children.Add(box);
    }

    private CheckBox AddRecordingOption(Panel panel, string name, string label,
        bool isChecked, string toolTip)
    {
        var option = new CheckBox
        {
            Content = label,
            IsChecked = isChecked,
            ToolTip = toolTip,
            Margin = new Thickness(0, 3, 0, 3)
        };
        RegisterName(name, option);
        panel.Children.Add(option);
        return option;
    }

    private async Task LoadAsync()
    {
        try
        {
            ApplicationWindowSettings settings = await ApplicationWindowSettings.LoadAsync();
            stayOnTop.IsChecked = settings.StayOnTop;
            ScriptDialogFont font = settings.DialogFont ?? new ScriptDialogFont();
            savedWindowSettings = new(settings.StayOnTop, font, settings.MainSize, settings.StopSize);
            family.Text = font.Family;
            size.Text = font.SizePoints.ToString(CultureInfo.InvariantCulture);
            widthPixels.Text = settings.MainSize?.WidthPixels.ToString(CultureInfo.InvariantCulture) ?? "";
            heightPixels.Text = settings.MainSize?.HeightPixels.ToString(CultureInfo.InvariantCulture) ?? "";
            stopWidthPixels.Text = settings.StopSize?.WidthPixels.ToString(CultureInfo.InvariantCulture) ?? "";
            stopHeightPixels.Text = settings.StopSize?.HeightPixels.ToString(CultureInfo.InvariantCulture) ?? "";
            SetEditorEnabled(true);
            save.IsEnabled = true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            error.Text = "本体設定を読み込めませんでした。" + failure.Message;
        }
    }

    private async Task SaveAsync()
    {
        if (saving || savedWindowSettings is not { } previous)
        {
            return;
        }

        error.Text = "";
        bool savingRecording = false;
        try
        {
            if (!int.TryParse(size.Text, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int points))
            {
                throw new ArgumentException("文字サイズは正の整数で指定してください。");
            }

            ApplicationWindowSize? mainSize = ReadSize(widthPixels, heightPixels,
                previous.MainSize, "本体");
            ApplicationWindowSize? stopSize = ReadSize(stopWidthPixels, stopHeightPixels,
                previous.StopSize, "停止画面");
            bool topmost = stayOnTop.IsChecked == true;
            var font = new ScriptDialogFont(family.Text, points);
            var recordingSettings = new RecordingSettings(
                OmitIdleTimeAndMouseMoves: omitIdle.IsChecked == true,
                RelativeMouseCoordinates: relativeMouse.IsChecked == true,
                CopyAfterRecording: copyRecording.IsChecked == true,
                RecordButtonClicks: highLevel.IsChecked == true,
                BackgroundPlayback: background.IsChecked == true);
            bool recordingChanged = recordingSettings != initialRecordingSettings;
            saving = true;
            SetEditorEnabled(false);
            save.IsEnabled = false;
            if (!recordingChanged || topmost != previous.StayOnTop || font.Family != previous.DialogFont?.Family
                || font.SizePoints != previous.DialogFont?.SizePoints
                || Dimensions(mainSize) != Dimensions(previous.MainSize)
                || Dimensions(stopSize) != Dimensions(previous.StopSize))
            {
                var settings = new ApplicationWindowSettings(topmost, font, mainSize, stopSize);
                await settings.SaveAsync();
                Result = savedWindowSettings = settings;
            }

            if (recordingChanged)
            {
                savingRecording = true;
                recordingSettings.Save(recordingSettingsPath);
                RecordingResult = recordingSettings;
            }

            saving = false;
            DialogResult = true;
        }
        catch (Exception failure) when (failure is IOException
            or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException)
        {
            error.Text = (savingRecording ? "記録設定を保存できませんでした。"
                : "本体設定を保存できませんでした。") + failure.Message;
            if (Result is not null)
            {
                error.Text += " 画面の設定は保存済みです。";
            }
        }
        finally
        {
            saving = false;
            SetEditorEnabled(true);
            save.IsEnabled = true;
        }
    }

    private static (int? Width, int? Height) Dimensions(ApplicationWindowSize? size) =>
        (size?.WidthPixels, size?.HeightPixels);

    private static ApplicationWindowSize? ReadSize(TextBox widthBox, TextBox heightBox,
        ApplicationWindowSize? previous, string target)
    {
        if (string.IsNullOrWhiteSpace(widthBox.Text) && string.IsNullOrWhiteSpace(heightBox.Text))
        {
            return previous;
        }

        if (!int.TryParse(widthBox.Text, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int width)
            || !int.TryParse(heightBox.Text, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int height)
            || width <= 0 || height <= 0)
        {
            throw new ArgumentException(target + "の幅と高さは両方を正の整数で指定してください。");
        }

        return new ApplicationWindowSize(width, height);
    }

    private void SetEditorEnabled(bool enabled)
    {
        stayOnTop.IsEnabled = enabled;
        family.IsEnabled = enabled;
        size.IsEnabled = enabled;
        widthPixels.IsEnabled = enabled;
        heightPixels.IsEnabled = enabled;
        stopWidthPixels.IsEnabled = enabled;
        stopHeightPixels.IsEnabled = enabled;
        recordingOptions.IsEnabled = enabled;
    }
}
