using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using UWSCSharp.Windows;
using static UWSCSharp.Wpf.VerificationAssert;

namespace UWSCSharp.Wpf;

internal static class ScriptDropOleVerification
{
    private static async Task<(string[] Checks, object[] Transfers)> VerifyScriptDropOleAsync(string directory, bool separateProcess = false)
    {
        string first = Path.Combine(directory, "先頭 script.uws");
        string second = Path.Combine(directory, "次の script.uws");
        const string script = "PRINT GET_UWSC_NAME\nPRINT LENGTH(PARAM_STR)";
        await File.WriteAllTextAsync(first, script);
        await File.WriteAllTextAsync(second, script);
        List<string> checks = [];
        List<object> transfers = [];
        List<Exception> errors = [];
        var window = new MainWindow(commandLine: false,
            confirmDiscard: () => true, reportError: errors.Add)
        {
            Left = SystemParameters.WorkArea.Left + 10,
            Top = SystemParameters.WorkArea.Top + 10,
            ShowActivated = false
        };
        var access = new MainWindow.ScriptDropVerificationAccess(window);
        var sender = new Border
        {
            Background = Brushes.LightBlue,
            Child = new TextBlock { Text = "OLE検証用の送信元", Margin = new Thickness(12) }
        };
        var source = new Window
        {
            Title = "UWSC ドロップ送信検証",
            Width = 180,
            Height = 120,
            Left = SystemParameters.WorkArea.Left + 650,
            Top = SystemParameters.WorkArea.Top + 30,
            Content = sender
        };
        try
        {
            window.Show();
            access.ExpandEditor();
            window.UpdateLayout();
            if (!separateProcess)
            {
                source.Show();
                source.UpdateLayout();
            }
            Require(SystemParameters.WorkArea.Width >= 850
                && SystemParameters.WorkArea.Height >= window.ActualHeight + 20,
                "所有画面を配置する作業領域が不足しています。");
            await TransferAsync("ole-single-script-copy",
                new DataObject(DataFormats.FileDrop, new[] { first }), first);
            await TransferAsync("ole-multiple-first-script-copy",
                new DataObject(DataFormats.FileDrop, new[] { first, second }), first);
            await TransferAsync("ole-reversed-first-script-copy",
                new DataObject(DataFormats.FileDrop, new[] { second, first }), second);
            await TransferAsync("ole-text-rejected",
                new DataObject(DataFormats.UnicodeText, "挿入しない文字列"), null);
            await TransferAsync("ole-move-only-rejected",
                new DataObject(DataFormats.FileDrop, new[] { first }), null,
                DragDropEffects.Move);
            Require(errors.Count == 0, "ドロップ処理からエラーが通知されました。");
            Require(await File.ReadAllTextAsync(first) == script
                && await File.ReadAllTextAsync(second) == script, "送信元ファイルが変更されました。");
            return ([.. checks], [.. transfers]);
        }
        finally
        {
            access.CancelPlayback();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (access.IsReceiving)
            {
                await Task.Delay(10, deadline.Token);
            }

            source.Close();
            access.DiscardDraft();
            window.Close();
        }

        async Task TransferAsync(string name, DataObject data, string? expectedPath,
            DragDropEffects allowed = DragDropEffects.Copy | DragDropEffects.Move)
        {
            if (expectedPath is not null)
            {
                access.ClearOutput();
            }

            string beforeText = access.Source;
            string beforeOutput = access.Output;
            string? beforePath = access.ScriptPath;
            if (access.LogWindow is { IsVisible: true } log)
            {
                Point point = access.DropTarget.PointToScreen(new Point(40, 40));
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    phase = "receiver-uncover",
                    window_at_point = ScriptDropOleProcessVerification.WindowAtPoint(point).ToInt64(),
                    log_window = new WindowInteropHelper(log).Handle.ToInt64(),
                    receiver_window = new WindowInteropHelper(window).Handle.ToInt64()
                }));
                // 検証対象の編集欄が、再生結果のログ画面で覆われないようにします。
                log.Hide();
            }

            if (separateProcess)
            {
                window.Activate();
                window.UpdateLayout();
                await Task.Delay(50);
            }

            Point target = access.DropTarget.PointToScreen(new Point(40, 40));
            Point start;
            DragDropEffects effect;
            int sourceProcessId;
            if (separateProcess)
            {
                ScriptDropOleProcessVerification.TransferResult result =
                    await ScriptDropOleProcessVerification.TransferAsync(
                        directory, access.DropTarget, data, allowed, target);
                start = new Point(result.StartX, result.StartY);
                effect = result.Effect;
                sourceProcessId = result.ProcessId;
            }
            else
            {
                source.Activate();
                await Task.Delay(50);
                start = sender.PointToScreen(new Point(sender.ActualWidth / 2, sender.ActualHeight / 2));
                effect = await ScriptDropOleVerification.TransferAsync(
                    sender, access.DropTarget, data, allowed, start, target);
                sourceProcessId = Environment.ProcessId;
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (access.IsReceiving)
            {
                await Task.Delay(10, deadline.Token);
            }

            bool released = ScriptDropOleVerification.LeftButtonReleased;
            transfers.Add(new
            {
                name,
                effect = effect.ToString(),
                start,
                target,
                released,
                source_process_id = sourceProcessId,
                target_process_id = Environment.ProcessId
            });
            Require(released, "左ボタンが解放されていません。");
            if (expectedPath is null)
            {
                Require(effect == DragDropEffects.None && access.Source == beforeText
                    && access.ScriptPath == beforePath && access.Output == beforeOutput,
                    "拒否したドロップが編集内容や実行結果を変更しました。");
            }
            else
            {
                Require(effect == DragDropEffects.Copy && access.ScriptPath == expectedPath,
                    "OLEの効果または先頭ファイルが一致しません。");
                string[] lines = access.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                Require(lines.Length == 2 && lines[0].Contains(Path.GetFileName(expectedPath), StringComparison.Ordinal)
                    && lines[1] == "0", "再生結果または引数数が一致しません。");
            }

            checks.Add(name);
        }
    }

    internal static bool LeftButtonReleased => (Native.GetAsyncKeyState(1) & 0x8000) == 0;

    internal static async Task<int> RunAsync(string receipt, bool separateProcess = false)
    {
        if (!SandboxEnvironment.IsCurrent
            || IntPtr.Size != 8)
        {
            throw new InvalidOperationException("OLEドロップ検証は64bitのWindows Sandbox内で実行してください。");
        }

        try
        {
            (string[] Checks, object[] Transfers) result =
                await VerifyScriptDropOleAsync(Path.GetDirectoryName(receipt)!, separateProcess);
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
            {
                result = "PASS",
                checks = result.Checks,
                transfers = result.Transfers,
                window_shown = true,
                input_injection = true
            }));
            return 0;
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
            {
                result = "FAIL",
                error = error.ToString()
            }));
            return 1;
        }
    }

    internal static async Task<DragDropEffects> TransferAsync(UIElement sender, UIElement target,
        DataObject data, DragDropEffects allowed, Point start, Point destination)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DragEventHandler onEnter = (_, _) => entered.TrySetResult();
        target.AddHandler(DragDrop.PreviewDragEnterEvent, onEnter, handledEventsToo: true);
        target.AddHandler(DragDrop.PreviewDragOverEvent, onEnter, handledEventsToo: true);
        try
        {
            return await TransferToPointAsync(sender, data, allowed, start,
                destination, () => entered.Task.IsCompleted);
        }
        finally
        {
            target.RemoveHandler(DragDrop.PreviewDragEnterEvent, onEnter);
            target.RemoveHandler(DragDrop.PreviewDragOverEvent, onEnter);
        }
    }

    internal static async Task<DragDropEffects> TransferToPointAsync(
        UIElement sender, DataObject data, DragDropEffects allowed,
        Point start, Point destination, Func<bool> entered)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task? driver = null;
        try
        {
            Move(start);
            Button(down: true);
            await Task.Delay(50, deadline.Token);
            driver = Task.Run(async () =>
            {
                try
                {
                    while (!entered())
                    {
                        Move(destination);
                        await Task.Delay(50, deadline.Token);
                    }

                    await Task.Delay(50, deadline.Token);
                }
                finally
                {
                    Button(down: false);
                }
            });
            // OLEのループが受信イベントを生成します。RaiseEventは使いません。
            DragDropEffects result = DragDrop.DoDragDrop(sender, data, allowed);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                phase = "ole-return",
                effect = result.ToString(),
                entered = entered(),
                released = LeftButtonReleased
            }));
            await driver;
            return result;
        }
        finally
        {
            deadline.Cancel();
            Button(down: false);
            if (driver is not null)
            {
                await driver;
            }
        }
    }

    internal static void Move(Point point)
    {
        if (!Native.SetPhysicalCursorPos(checked((int)point.X), checked((int)point.Y)))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        if (SendInput(1, [new MouseInput(1)], 40) != 1)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    internal static void Button(bool down)
    {
        if (SendInput(1, [new MouseInput(down ? 2u : 4u)], 40) != 1)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct MouseInput
    {
        [FieldOffset(0)] private uint type;
        [FieldOffset(20)] private uint flags;

        internal MouseInput(uint flags)
        {
            this = default;
            type = 0;
            this.flags = flags;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, MouseInput[] inputs, int size);
}
