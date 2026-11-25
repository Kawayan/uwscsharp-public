using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using UWSCSharp.Windows;

namespace UWSCSharp.Wpf;

internal static class ScriptDropOleProcessVerification
{
    internal sealed record TransferRequest(string[]? Files, string? Text,
        DragDropEffects Allowed, double TargetX, double TargetY,
        string ArrivedMarker, long ReceiverWindow);

    internal sealed record TransferResult(DragDropEffects Effect,
        int ProcessId, double StartX, double StartY, bool Released);

    internal static async Task<TransferResult> TransferAsync(string directory,
        UIElement target, DataObject data, DragDropEffects allowed,
        Point destination)
    {
        RequireSandbox();
        string prefix = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        string requestPath = prefix + "-request.json";
        string receiptPath = prefix + "-receipt.json";
        string marker = prefix + "-arrived";
        var request = new TransferRequest(
            data.GetData(DataFormats.FileDrop, false) as string[],
            data.GetData(DataFormats.UnicodeText, false) as string,
            allowed, destination.X, destination.Y, marker,
            new WindowInteropHelper(Window.GetWindow(target)).Handle.ToInt64());
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request));
        bool arrived = false;
        DragEventHandler onEnter = (_, _) =>
        {
            if (!arrived)
            {
                File.WriteAllText(marker, "arrived");
                arrived = true;
            }
        };
        target.AddHandler(DragDrop.PreviewDragEnterEvent, onEnter, handledEventsToo: true);
        target.AddHandler(DragDrop.PreviewDragOverEvent, onEnter, handledEventsToo: true);
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("--verify-script-drop-source");
        start.ArgumentList.Add(requestPath);
        start.ArgumentList.Add(receiptPath);
        using var process = new Process { StartInfo = start };
        bool started = false;
        try
        {
            started = process.Start();
            if (!started)
            {
                throw new InvalidOperationException("送信元プロセスを起動できませんでした。");
            }

            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Console.WriteLine(await stdout);
            Console.Error.WriteLine(await stderr);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("送信元プロセスが異常終了しました。");
            }

            TransferResult result = JsonSerializer.Deserialize<TransferResult>(
                await File.ReadAllTextAsync(receiptPath))!;
            if (!arrived || !result.Released || result.ProcessId != process.Id
                || result.ProcessId == Environment.ProcessId)
            {
                throw new InvalidOperationException("別プロセスからの到達または入力解放を確認できませんでした。");
            }

            return result;
        }
        finally
        {
            target.RemoveHandler(DragDrop.PreviewDragEnterEvent, onEnter);
            target.RemoveHandler(DragDrop.PreviewDragOverEvent, onEnter);
            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    internal static async Task<int> RunSourceAsync(string requestPath, string receiptPath)
    {
        RequireSandbox();
        TransferRequest request = JsonSerializer.Deserialize<TransferRequest>(
            await File.ReadAllTextAsync(requestPath))!;
        var sender = new Border
        {
            Background = Brushes.LightBlue,
            Child = new TextBlock { Text = "別プロセスのOLE送信元", Margin = new Thickness(12) }
        };
        var source = new Window
        {
            Title = "UWSC 別プロセス送信検証",
            Width = 180,
            Height = 120,
            Left = SystemParameters.WorkArea.Left + 650,
            Top = SystemParameters.WorkArea.Top + 30,
            Content = sender
        };
        try
        {
            source.Show();
            source.UpdateLayout();
            source.Activate();
            await Task.Delay(50);
            Point start = sender.PointToScreen(
                new Point(sender.ActualWidth / 2, sender.ActualHeight / 2));
            nint targetWindow = WindowFromPhysicalPoint(new NativePoint(
                checked((int)request.TargetX), checked((int)request.TargetY)));
            Native.GetWindowThreadProcessId(targetWindow, out uint targetProcessId);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                phase = "process-source-ready",
                source_process_id = Environment.ProcessId,
                target_at_point_process_id = targetProcessId,
                request.TargetX,
                request.TargetY,
                start
            }));
            if (targetWindow.ToInt64() != request.ReceiverWindow)
            {
                throw new InvalidOperationException("送信地点が受信画面で覆われていません。");
            }

            DataObject data = request.Files is not null
                ? new DataObject(DataFormats.FileDrop, request.Files)
                : new DataObject(DataFormats.UnicodeText, request.Text!);
            DragDropEffects effect = await ScriptDropOleVerification.TransferToPointAsync(
                sender, data, request.Allowed, start,
                new Point(request.TargetX, request.TargetY),
                () => File.Exists(request.ArrivedMarker));
            var result = new TransferResult(effect, Environment.ProcessId,
                start.X, start.Y, ScriptDropOleVerification.LeftButtonReleased);
            await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(result));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            source.Close();
        }
    }

    private static void RequireSandbox()
    {
        if (!SandboxEnvironment.IsCurrent
            || IntPtr.Size != 8)
        {
            throw new InvalidOperationException("別プロセスのOLE検証は64bitのWindows Sandbox内で実行してください。");
        }
    }

    internal static nint WindowAtPoint(Point point) =>
        WindowFromPhysicalPoint(new NativePoint(
            checked((int)point.X), checked((int)point.Y)));

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint(int x, int y)
    {
        private readonly int x = x;
        private readonly int y = y;
    }

    [DllImport("user32.dll")]
    private static extern nint WindowFromPhysicalPoint(NativePoint point);
}
