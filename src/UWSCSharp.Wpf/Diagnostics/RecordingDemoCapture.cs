using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using UWSCSharp.Windows;
using static UWSCSharp.Wpf.VerificationAssert;

namespace UWSCSharp.Wpf;

// README向けに、Sandbox内で実際の記録・再生と画面収録を行います。
internal static class RecordingDemoCapture
{
    private const string DemoText = "Hello UWSC#";
    private const string ShortcutName = "メモ帳";
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(12);

    internal static async Task<int> RunAsync(string tools, string receipt)
    {
        Require(SandboxEnvironment.IsCurrent, "記録デモの撮影はWindows Sandbox専用です。");
        string output = Path.GetDirectoryName(Path.GetFullPath(receipt))!;
        string notepad = Path.Combine(tools, "notepad", "notepad.exe");
        string progress = Path.Combine(output, "capture.progress");
        string video = Path.Combine(output, "capture.mkv");
        var errors = new List<Exception>();
        var main = new MainWindow(false, reportError: errors.Add, verifyRecordingInput: true)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 125,
            Top = 42
        };
        var recording = new MainWindow.RecordingVerificationAccess(main);
        main.Show();
        nint owner = new WindowInteropHelper(main).Handle;
        try
        {
            var origin = new Native.Point(Pixels(95), Pixels(250));
            int noteWidth = Pixels(460);
            int noteHeight = Pixels(285);
            int width = Math.Min(Native.GetSystemMetrics(0), Pixels(1200)) & ~1;
            int height = Math.Min(Native.GetSystemMetrics(1), Pixels(770)) & ~1;
            var destination = new Native.Point(
                Math.Min(Pixels(630), width - noteWidth - Pixels(20)),
                Math.Min(Pixels(350), height - noteHeight - Pixels(20)));
            Require(destination.X - origin.X >= Pixels(200) && destination.Y >= origin.Y,
                "撮影にはデスクトップの有効領域が横775×縦555 DIP以上必要です。");

            await PositionNotepadAsync(notepad, origin, noteWidth, noteHeight);
            Point shortcut = await Task.Run(FindShortcut);
            Require(shortcut.X < width && shortcut.Y < height,
                "メモ帳のショートカットが撮影範囲外にあります。");
            main.Activate();

            using Process capture = StartCapture(tools, video, progress, width, height);
            Task<string> captureErrors = capture.StandardError.ReadToEndAsync();
            double resetAt;
            double replayAt;
            try
            {
                await WaitAsync(() => capture.HasExited || FrameTime(progress) > 0,
                    "FFmpegの録画開始", TimeSpan.FromSeconds(25));
                Require(!capture.HasExited, "FFmpegが録画前に終了しました。 " + Inspect(captureErrors));
                await ClickButtonAsync(recording.RecordButton, owner);
                await WaitAsync(() => recording.IsRunning || errors.Count > 0, "記録開始");
                CheckErrors(errors);
                await Task.Delay(500);

                await ClickAsync(shortcut);
                await Task.Delay(90);
                await ClickAsync(shortcut);
                using Process first = await OpenedNotepadAsync(notepad);
                nint firstWindow = first.MainWindowHandle;
                nint firstEditor = FindEditor(firstWindow);
                Native.Rect initial = Bounds(firstWindow);
                await Task.Delay(650);
                await DragAsync(firstWindow, initial, destination);
                Native.Rect moved = Bounds(firstWindow);
                Require(moved.Left - initial.Left >= Pixels(190), "記録中にメモ帳が右へ移動しませんでした。");
                await Task.Delay(500);
                Require(Native.GetClientRect(firstEditor, out Native.Rect editBounds),
                    "メモ帳の入力欄の取得に失敗しました。");
                var editPoint = new Native.Point(Math.Min(editBounds.Right / 2, Pixels(75)), Pixels(30));
                Require(Native.ClientToScreen(firstEditor, ref editPoint)
                    && Native.WindowFromPoint(editPoint) == firstEditor,
                    "メモ帳の入力欄にマウスが届きません。");
                await ClickAsync(new Point(editPoint.X, editPoint.Y));
                Require(Native.GetForegroundWindow() == firstWindow, "メモ帳が前面にありません。");
                await TypeDemoAsync();
                await WaitAsync(() => ReadEditor(firstEditor) == DemoText, "記録中の文字入力");
                await Task.Delay(850);

                await ClickButtonAsync(recording.RecordButton, owner);
                await WaitAsync(() => !recording.HasRecorder || errors.Count > 0, "記録終了");
                CheckErrors(errors);
                string source = recording.Source;
                File.WriteAllText(Path.Combine(output, "recorded.uws"), source, new UTF8Encoding(true));
                Require(source.Contains("MMV(", StringComparison.Ordinal)
                    && source.Contains("BTN(", StringComparison.Ordinal)
                    && source.Contains("KBD(", StringComparison.Ordinal)
                    && !source.Contains("// 警告:", StringComparison.Ordinal),
                    "マウス移動・ボタン・キー入力を警告なしで記録できませんでした。recorded.uwsを確認してください。");
                await Task.Delay(900);

                resetAt = FrameTime(progress);
                await DragAsync(firstWindow, moved, new Native.Point(initial.Left, initial.Top));
                Require(Native.GetForegroundWindow() == firstWindow, "再生準備中にメモ帳が前面にありません。");
                TapKey('Z', modifier: 0x11);
                await WaitAsync(() => ReadEditor(firstEditor).Length == 0, "メモ帳の入力を取り消す");
                await CloseNotepadAsync(first);
                await Task.Delay(650);

                replayAt = FrameTime(progress);
                await ClickButtonAsync((Button)main.FindName("PlayButton"), owner);
                using Process replayed = await OpenedNotepadAsync(notepad);
                nint replayWindow = replayed.MainWindowHandle;
                nint replayEditor = FindEditor(replayWindow);
                await WaitAsync(() => recording.Status == "実行が完了しました" || errors.Count > 0,
                    "記録したスクリプトの再生完了", TimeSpan.FromSeconds(25));
                CheckErrors(errors);
                Native.Rect final = Bounds(replayWindow);
                Require(ReadEditor(replayEditor) == DemoText
                    && Math.Abs(final.Left - moved.Left) <= 12 && Math.Abs(final.Top - moved.Top) <= 12,
                    "再生後のメモ帳の位置または文字列が記録時と一致しません。");
                await Task.Delay(1900);
            }
            finally
            {
                if (!capture.HasExited)
                {
                    capture.StandardInput.WriteLine("q");
                    try
                    {
                        await capture.WaitForExitAsync().WaitAsync(StepTimeout);
                    }
                    catch (TimeoutException)
                    {
                        capture.Kill();
                        await capture.WaitForExitAsync();
                        throw;
                    }
                }
                File.WriteAllText(Path.Combine(output, "ffmpeg.log"), await captureErrors);
            }
            Require(capture.ExitCode == 0 && new FileInfo(video).Length > 0,
                "FFmpegが録画の保存に失敗しました。ffmpeg.logを確認してください。");
            File.WriteAllText(receipt, JsonSerializer.Serialize(new { resetAt, replayAt }));
            return 0;
        }
        finally
        {
            recording.DiscardDraft();
            main.Close();
        }

        int Pixels(double value) => (int)Math.Round(main.PointToScreen(new Point(value, 0)).X
            - main.PointToScreen(new Point(0, 0)).X);
    }

    private static Point FindShortcut()
    {
        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.NameProperty, ShortcutName),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
        AutomationElement item = AutomationElement.RootElement.FindFirst(TreeScope.Descendants, condition)
            ?? throw new InvalidOperationException("デスクトップのメモ帳ショートカットを取得できませんでした。");
        Rect bounds = item.Current.BoundingRectangle;
        Require(!bounds.IsEmpty && !item.Current.IsOffscreen, "メモ帳のショートカットが見えていません。");
        return new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
    }

    private static async Task PositionNotepadAsync(string path, Native.Point origin, int width, int height)
    {
        using Process started = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false })!;
        using Process notepad = await OpenedNotepadAsync(path);
        nint window = notepad.MainWindowHandle;
        _ = FindEditor(window);
        Require(Native.SetWindowPos(window, 0, origin.X, origin.Y, width, height, 0x14),
            "撮影前にメモ帳の位置を設定できませんでした。");
        await CloseNotepadAsync(notepad);
    }

    private static async Task<Process> OpenedNotepadAsync(string path)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        do
        {
            foreach (Process process in Process.GetProcessesByName("notepad"))
            {
                bool match = false;
                try
                {
                    match = process.MainWindowHandle != 0
                        && string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase);
                    if (match)
                    {
                        return process;
                    }
                }
                finally
                {
                    if (!match)
                    {
                        process.Dispose();
                    }
                }
            }
            await Task.Delay(50);
        } while (elapsed.Elapsed < StepTimeout);
        throw new TimeoutException("撮影用のクラシック メモ帳が起動しませんでした。");
    }

    private static async Task CloseNotepadAsync(Process notepad)
    {
        Require(notepad.CloseMainWindow(), "撮影用のメモ帳を閉じる操作に失敗しました。");
        await notepad.WaitForExitAsync().WaitAsync(StepTimeout);
    }

    private static nint FindEditor(nint window)
    {
        nint editor = 0;
        var name = new StringBuilder(64);
        _ = Native.EnumChildWindows(window, (child, _) =>
        {
            _ = Native.GetClassNameW(child, name, name.Capacity);
            if (name.ToString() != "Edit")
            {
                return true;
            }
            editor = child;
            return false;
        }, 0);
        Require(editor != 0, "クラシック メモ帳の編集欄を取得できませんでした。");
        return editor;
    }

    private static string ReadEditor(nint editor)
    {
        var text = new StringBuilder(128);
        Require(Native.ReadText(editor, 0x000D, text.Capacity, text, 2, 1000, out _) != 0,
            "メモ帳の文字列を取得できませんでした。");
        return text.ToString();
    }

    private static Task ClickButtonAsync(Button button, nint owner)
    {
        Point point = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
        Require(button.IsEnabled && RootAt(point) == owner, "撮影中にUWSC#のボタンが隠れています。");
        return ClickAsync(point);
    }

    private static nint RootAt(Point point) =>
        Native.GetAncestor(Native.WindowFromPoint(new Native.Point((int)point.X, (int)point.Y)), 2);

    private static Native.Rect Bounds(nint window)
    {
        Require(Native.GetWindowRect(window, out Native.Rect bounds), "ウィンドウの位置を取得できませんでした。");
        return bounds;
    }

    private static async Task DragAsync(nint window, Native.Rect from, Native.Point destination)
    {
        // ウィンドウ幅の中ほどにある、タイトルバーの空いた部分をつかみます。
        int offsetX = (from.Right - from.Left) / 2;
        int offsetY = Math.Max(12, (from.Right - from.Left) / 28);
        var start = new Point(from.Left + offsetX, from.Top + offsetY);
        Require(RootAt(start) == window, "メモ帳のタイトルバーが隠れています。");
        Move(start.X, start.Y);
        await Task.Delay(150);
        SendMouse(2);
        try
        {
            for (int step = 1; step <= 24; step++)
            {
                Move(start.X + (destination.X - from.Left) * step / 24.0,
                    start.Y + (destination.Y - from.Top) * step / 24.0);
                await Task.Delay(30);
            }
        }
        finally
        {
            SendMouse(4);
        }
        await Task.Delay(180);
    }

    private static async Task ClickAsync(Point point)
    {
        Move(point.X, point.Y);
        SendMouse(2);
        try { await Task.Delay(45); }
        finally { SendMouse(4); }
    }

    private static void Move(double x, double y)
    {
        Require(NativeInputSender.TryCreateAbsoluteMouseMove(
            new Native.Point((int)x, (int)y), out Native.Input move), "マウスの移動先が画面外です。");
        NativeInputSender.Send(move);
    }

    private static void SendMouse(uint flags) => NativeInputSender.Send(new Native.Input
    {
        Type = 0,
        Data = new Native.InputUnion { Mouse = new Native.MouseInput { Flags = flags } }
    });

    private static async Task TypeDemoAsync()
    {
        foreach (char character in DemoText)
        {
            char key = character == '#' ? '3' : char.ToUpperInvariant(character);
            bool shift = character == '#' || char.IsUpper(character);
            TapKey(key, shift ? (ushort)0x10 : (ushort)0);
            await Task.Delay(95);
        }
    }

    private static void TapKey(ushort key, ushort modifier)
    {
        if (modifier != 0)
        {
            SendKey(modifier, 0);
        }
        try
        {
            SendKey(key, 0);
            SendKey(key, 2);
        }
        finally
        {
            if (modifier != 0)
            {
                SendKey(modifier, 2);
            }
        }
    }

    private static void SendKey(ushort key, uint flags) => NativeInputSender.Send(new Native.Input
    {
        Type = 1,
        Data = new Native.InputUnion { Keyboard = new Native.KeyboardInput { Vk = key, Flags = flags } }
    });

    private static Process StartCapture(string tools, string video, string progress, int width, int height)
    {
        var start = new ProcessStartInfo(Path.Combine(tools, "ffmpeg.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true
        };
        string[] args = ["-y", "-hide_banner", "-loglevel", "warning", "-stats_period", "0.15",
            "-progress", progress, "-f", "gdigrab", "-framerate", "12", "-draw_mouse", "1",
            "-video_size", $"{width}x{height}", "-i", "desktop", "-t", "70",
            "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-crf", "15", video];
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        return Process.Start(start)!;
    }

    private static double FrameTime(string progress)
    {
        const string prefix = "out_time_us=";
        try
        {
            using var input = new FileStream(progress, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(input);
            double seconds = 0;
            while (reader.ReadLine() is { } line)
            {
                if (line.StartsWith(prefix, StringComparison.Ordinal)
                    && double.TryParse(line.AsSpan(prefix.Length), CultureInfo.InvariantCulture, out double time))
                {
                    seconds = time / 1_000_000;
                }
            }
            return seconds;
        }
        catch (FileNotFoundException)
        {
            return 0;
        }
    }

    private static string Inspect(Task<string> errors) => errors.IsCompletedSuccessfully ? errors.Result : "";

    private static async Task WaitAsync(Func<bool> complete, string step, TimeSpan? timeout = null)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!complete())
        {
            if (elapsed.Elapsed > (timeout ?? StepTimeout))
            {
                throw new TimeoutException(step + "を確認できませんでした。");
            }
            await Task.Delay(50);
        }
    }

    private static void CheckErrors(List<Exception> errors)
    {
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("UWSC#の操作中にエラーが発生しました。", errors[0]);
        }
    }
}
