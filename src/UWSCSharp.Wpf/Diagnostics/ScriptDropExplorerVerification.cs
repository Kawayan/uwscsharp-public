using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using UWSCSharp.Windows;
using static UWSCSharp.Wpf.VerificationAssert;

namespace UWSCSharp.Wpf;

internal static class ScriptDropExplorerVerification
{
    private static async Task<object> VerifyExplorerDropAsync(string directory, bool shortcuts = false)
    {
        string folder = Path.Combine(directory, "uwsc-explorer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string first = Path.Combine(folder, "先頭 script.uws");
        string second = Path.Combine(folder, "次の script.uws");
        const string script = "PRINT GET_UWSC_NAME\nPRINT LENGTH(PARAM_STR)";
        await File.WriteAllTextAsync(first, script);
        await File.WriteAllTextAsync(second, script);
        string targetDirectory = Path.Combine(folder, "対象 folder");
        string shortcutTarget = Path.Combine(targetDirectory, "リンク先 script.uws");
        string[] linkArguments = ["", "\"日本語 alpha\" beta", "\"\"", "\"a\\\"b\"", "\talpha", "\"日本語 alpha\" beta"];
        string[][] expectedArguments = [[], ["日本語 alpha", "beta"], [""], ["a\"b"], ["alpha"], ["日本語 alpha", "beta"]];
        string[] links = [];
        if (shortcuts)
        {
            Directory.CreateDirectory(targetDirectory);
            await File.WriteAllTextAsync(shortcutTarget, ScriptDropShortcutVerification.ShortcutProbeSource);
            links = [.. Enumerable.Range(0, 6).Select(index => Path.Combine(folder, "起動 link " + index + ".lnk"))];
            for (int index = 0; index < links.Length; index++)
            {
                ScriptDropShortcutVerification.CreateDropShortcut(links[index], shortcutTarget, linkArguments[index], folder);
            }
        }
        List<Exception> errors = [];
        List<object> transfers = [];
        List<string> checks = [];
        var window = new MainWindow(commandLine: false,
            confirmDiscard: () => true, reportError: errors.Add)
        {
            Left = SystemParameters.WorkArea.Left + 10,
            Top = SystemParameters.WorkArea.Top + 10,
            ShowActivated = false
        };
        var access = new MainWindow.ScriptDropVerificationAccess(window);
        nint explorer = 0;
        try
        {
            window.Show();
            access.ExpandEditor();
            window.UpdateLayout();
            Point corner = window.PointToScreen(new Point(window.ActualWidth + 20, 0));
            explorer = await Task.Run(() => ScriptDropExplorerVerification.OpenFolder(folder));
            ScriptDropExplorerVerification.Position(explorer, corner);
            string processDirectory = Environment.CurrentDirectory;
            if (shortcuts)
            {
                for (int index = 0; index < links.Length; index++)
                {
                    await TransferAsync("shortcut-" + index, [links[index]], links[index], shortcutTarget, expectedArguments[index]);
                }
                Require(Environment.CurrentDirectory == processDirectory, "プロセス全体の基準フォルダー保持");
            }
            else
            {
                await TransferAsync("single-first", [first], first);
                await TransferAsync("single-second", [second], second);
                await TransferAsync("forward-start-first", [first, second], first);
                await TransferAsync("forward-start-second", [first, second], second);
                await TransferAsync("reverse-start-first", [second, first], first);
                await TransferAsync("reverse-start-second", [second, first], second);
            }
            Require(errors.Count == 0, "ドロップ処理からエラーが通知されました。");
            Require(await File.ReadAllTextAsync(first) == script
                && await File.ReadAllTextAsync(second) == script,
                "送信元ファイルが変更されました。");
        }
        finally
        {
            try
            {
                if (explorer != 0)
                {
                    await Task.Run(() => ScriptDropExplorerVerification.CloseFolder(folder, explorer));
                }
            }
            finally
            {
                access.CancelPlayback();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (access.IsReceiving)
                {
                    await Task.Delay(10, deadline.Token);
                }

                access.DiscardDraft();
                window.Close();
            }
        }

        return new
        {
            result = "PASS",
            checks,
            transfers,
            window_shown = true,
            input_injection = true
        };

        async Task TransferAsync(string name, string[] selected, string dragged,
            string? target = null, string[]? arguments = null)
        {
            access.ClearOutput();
            access.LogWindow?.Hide();
            window.UpdateLayout();
            Point destination = access.DropTarget.PointToScreen(new Point(40, 40));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var dropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            string[] received = [];
            DragDropEffects effect = DragDropEffects.None;
            DragEventHandler onEnter = (_, _) => entered.TrySetResult();
            DragEventHandler onDrop = (_, e) =>
            {
                received = e.Data.GetData(DataFormats.FileDrop, false) as string[] ?? [];
                effect = e.Effects;
                dropped.TrySetResult();
            };
            access.DropTarget.AddHandler(DragDrop.PreviewDragEnterEvent, onEnter, true);
            access.DropTarget.AddHandler(DragDrop.PreviewDragOverEvent, onEnter, true);
            access.DropTarget.AddHandler(DragDrop.PreviewDropEvent, onDrop, true);
            Point start;
            try
            {
                start = await Task.Run(() => ScriptDropExplorerVerification.SelectFiles(explorer, selected, dragged));
                Require(ScriptDropOleProcessVerification.WindowAtPoint(destination)
                    == new WindowInteropHelper(window).Handle, "編集欄が別の画面に覆われています。");
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    phase = "explorer-drag-start",
                    name,
                    selected,
                    dragged,
                    start,
                    destination
                }));
                await ScriptDropExplorerVerification.DragAsync(start, destination, entered.Task);
                await dropped.Task.WaitAsync(TimeSpan.FromSeconds(10));
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (access.IsReceiving)
                {
                    await Task.Delay(10, deadline.Token);
                }
            }
            finally
            {
                access.DropTarget.RemoveHandler(DragDrop.PreviewDragEnterEvent, onEnter);
                access.DropTarget.RemoveHandler(DragDrop.PreviewDragOverEvent, onEnter);
                access.DropTarget.RemoveHandler(DragDrop.PreviewDropEvent, onDrop);
            }

            Require(received.Length == selected.Length
                && received.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(selected),
                "Explorerから受信したファイル列が選択したファイルと一致しません。");
            Require(effect == DragDropEffects.Copy && access.ScriptPath == (target ?? received[0]),
                "Copyまたは受信した先頭ファイルの再生を確認できませんでした。");
            string[] lines = access.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (arguments is not null)
            {
                Require(lines.Length == 3 + arguments.Length && lines[0] == "NAME=" + Path.GetFileName(target)
                    && lines[1] == "DIR=" + Path.GetDirectoryName(target)
                    && lines[2] == "COUNT=" + arguments.Length, "リンクの対象・基準・引数数");
                for (int index = 0; index < arguments.Length; index++)
                {
                    Require(lines[3 + index] == "ARG[" + index + "]=[" + arguments[index] + "]", "リンク引数の値");
                }
            }
            else
            {
                Require(lines.Length == 2 && lines[0].Contains(Path.GetFileName(received[0]), StringComparison.Ordinal)
                    && lines[1] == "0", "今回の再生結果または引数数が一致しません。");
            }
            Require(ScriptDropOleVerification.LeftButtonReleased, "左ボタンが解放されていません。");
            transfers.Add(new
            {
                name,
                selected,
                dragged,
                received,
                target_drop_effect = effect.ToString(),
                start,
                destination,
                released = ScriptDropOleVerification.LeftButtonReleased
            });
            checks.Add(name);
        }
    }

    internal static async Task<int> RunAsync(string receipt, bool shortcuts = false)
    {
        if (!SandboxEnvironment.IsCurrent
            || IntPtr.Size != 8)
        {
            throw new InvalidOperationException("Explorerのドロップ検証は64bitのWindows Sandbox内で実行してください。");
        }

        try
        {
            object result = await VerifyExplorerDropAsync(Path.GetDirectoryName(receipt)!, shortcuts);
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(result));
            return 0;
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(receipt,
                JsonSerializer.Serialize(new
                {
                    result = "FAIL",
                    error = error.ToString()
                }));
            return 1;
        }
    }

    internal static nint OpenFolder(string folder)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(folder);
        using var launcher = Process.Start(start);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(20))
        {
            nint handle = FindFolder(folder);
            if (handle != 0 && Native.IsWindowVisible(handle))
            {
                Native.GetWindowThreadProcessId(handle, out uint processId);
                using var process = Process.GetProcessById(checked((int)processId));
                if (!string.Equals(process.MainModule?.FileName, start.FileName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("フォルダー画面の所有者がExplorerではありません。");
                }

                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    phase = "explorer-open",
                    process_id = processId,
                    folder
                }));
                return handle;
            }

            Thread.Sleep(100);
        }

        throw new TimeoutException("専用フォルダーのExplorer画面を確認できませんでした。");
    }

    private static nint FindFolder(string folder)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", throwOnError: true)!)!;
        object? windows = null;
        try
        {
            windows = shell.Windows();
            dynamic collection = windows;
            for (int index = 0; index < (int)collection.Count; index++)
            {
                object? item = null;
                try
                {
                    item = collection.Item(index);
                    if (item is null)
                    {
                        continue;
                    }

                    dynamic browser = item;
                    string location = browser.LocationURL;
                    if (Uri.TryCreate(location, UriKind.Absolute, out Uri? uri) && uri.IsFile
                        && string.Equals(Path.TrimEndingDirectorySeparator(uri.LocalPath), folder, StringComparison.OrdinalIgnoreCase))
                    {
                        return new nint((long)browser.HWND);
                    }
                }
                finally
                {
                    Release(item);
                }
            }

            return 0;
        }
        finally
        {
            Release(windows);
            Release(shell);
        }
    }

    internal static void Position(nint handle, Point corner)
    {
        Native.ShowWindow(handle, 9);
        if (!Native.SetWindowPos(handle, 0, checked((int)corner.X), checked((int)corner.Y), 700, 650, 0x40))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        GetWindowRect(handle, out NativeRect rect);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            phase = "explorer-position",
            visible = Native.IsWindowVisible(handle),
            iconic = Native.IsIconic(handle),
            rect.Left,
            rect.Top,
            rect.Right,
            rect.Bottom
        }));
    }

    internal static Point SelectFiles(nint handle, string[] paths, string? dragged = null)
    {
        var root = AutomationElement.FromHandle(handle);
        List<AutomationElement> items = [];
        foreach (string path in paths)
        {
            var condition = new AndCondition(
                new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem)),
                new OrCondition(new PropertyCondition(AutomationElement.NameProperty, Path.GetFileName(path)),
                    new PropertyCondition(AutomationElement.NameProperty, Path.GetFileNameWithoutExtension(path))));
            AutomationElement? item = null;
            var deadline = Stopwatch.StartNew();
            while (item is null && deadline.Elapsed < TimeSpan.FromSeconds(10))
            {
                item = root.FindFirst(TreeScope.Descendants, condition);
                if (item is null)
                {
                    Thread.Sleep(100);
                }
            }

            if (item is null)
            {
                throw new InvalidOperationException("Explorerでファイル項目を見つけられませんでした: " + path);
            }

            if (item.TryGetCurrentPattern(ScrollItemPattern.Pattern, out object scroll))
            {
                ((ScrollItemPattern)scroll).ScrollIntoView();
            }

            items.Add(item);
        }

        if (Native.GetForegroundWindow() != handle && !Native.SetForegroundWindow(handle))
        {
            ActivateView(handle, items[0]);
        }

        var foregroundDeadline = Stopwatch.StartNew();
        while (Native.GetForegroundWindow() != handle && foregroundDeadline.Elapsed < TimeSpan.FromSeconds(2))
        {
            Thread.Sleep(50);
        }

        if (Native.GetForegroundWindow() != handle)
        {
            throw new InvalidOperationException("専用Explorerの前面状態を確認できませんでした。");
        }

        for (int index = 0; index < items.Count; index++)
        {
            var selection = (SelectionItemPattern)items[index].GetCurrentPattern(SelectionItemPattern.Pattern);
            if (index == 0)
            {
                selection.Select();
            }
            else
            {
                selection.AddToSelection();
            }
        }

        int draggedIndex = dragged is null ? 0 : Array.IndexOf(paths, dragged);
        if (items.Count == 0 || draggedIndex < 0 || !items[draggedIndex].TryGetClickablePoint(out Point point)
            || Native.GetAncestor(ScriptDropOleProcessVerification.WindowAtPoint(point), 2) != handle)
        {
            throw new InvalidOperationException("選択したファイルのクリック地点を確認できませんでした。");
        }

        foreach (AutomationElement item in items)
        {
            var selection = (SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern);
            if (!selection.Current.IsSelected)
            {
                throw new InvalidOperationException("送信直前の選択状態が一致しません。");
            }
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            phase = "explorer-selection-ready",
            foreground = Native.GetForegroundWindow().ToInt64(),
            expected_window = handle.ToInt64(),
            selected_count = items.Count,
            point
        }));

        return point;
    }

    private static void ActivateView(nint handle, AutomationElement item)
    {
        AutomationElement? view = TreeWalker.ControlViewWalker.GetParent(item);
        while (view is not null && view.Current.ControlType != ControlType.List
            && view.Current.ControlType != ControlType.DataGrid)
        {
            view = TreeWalker.ControlViewWalker.GetParent(view);
        }

        if (view is null)
        {
            throw new InvalidOperationException("Explorerのファイル一覧を確認できませんでした。");
        }

        Rect bounds = view.Current.BoundingRectangle;
        var deadline = Stopwatch.StartNew();
        // ファイルやボタンではなく、一覧自体と一致する空白部分だけをクリックします。
        for (double y = bounds.Bottom - 20; y > bounds.Top + 20; y -= 40)
        {
            for (double x = bounds.Left + 20; x < bounds.Right - 20; x += 80)
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(5))
                {
                    throw new TimeoutException("Explorerの空白部分を期限内に確認できませんでした。");
                }

                var point = new Point(x, y);
                if (Native.GetAncestor(ScriptDropOleProcessVerification.WindowAtPoint(point), 2) != handle
                    || !Automation.Compare(AutomationElement.FromPoint(point), view))
                {
                    continue;
                }

                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    phase = "explorer-background-activate",
                    point,
                    control_type = view.Current.ControlType.ProgrammaticName
                }));
                try
                {
                    ScriptDropOleVerification.Move(point);
                    ScriptDropOleVerification.Button(down: true);
                    Thread.Sleep(100);
                }
                finally
                {
                    ScriptDropOleVerification.Button(down: false);
                }

                return;
            }
        }

        throw new InvalidOperationException("専用Explorerの一覧の空白部分を確認できませんでした。");
    }

    internal static async Task DragAsync(Point start, Point destination, Task? entered)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            ScriptDropOleVerification.Move(start);
            await Task.Delay(100, deadline.Token);
            ScriptDropOleVerification.Button(down: true);
            await Task.Delay(200, deadline.Token);
            // Explorer内でドラッグ開始の閾値を超え、開始処理を待ってから受信先へ移動します。
            var threshold = new Point(start.X - 16, start.Y);
            ScriptDropOleVerification.Move(threshold);
            await Task.Delay(300, deadline.Token);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                phase = "explorer-drag-threshold",
                foreground = Native.GetForegroundWindow().ToInt64(),
                left_button_down = !ScriptDropOleVerification.LeftButtonReleased,
                threshold
            }));
            for (int step = 1; step <= 20; step++)
            {
                ScriptDropOleVerification.Move(new Point(
                    threshold.X + (destination.X - threshold.X) * step / 20,
                    threshold.Y + (destination.Y - threshold.Y) * step / 20));
                await Task.Delay(30, deadline.Token);
            }

            while (entered is not null && !entered.IsCompleted)
            {
                ScriptDropOleVerification.Move(destination);
                await Task.Delay(50, deadline.Token);
            }

            if (entered is not null)
            {
                await entered;
            }
            await Task.Delay(100, deadline.Token);
        }
        finally
        {
            ScriptDropOleVerification.Button(down: false);
        }
    }

    internal static void CloseFolder(string folder, nint handle)
    {
        if (FindFolder(folder) != handle)
        {
            throw new InvalidOperationException("終了対象のExplorer画面を専用フォルダーと照合できませんでした。");
        }

        if (!Native.PostMessageW(handle, 0x10, 0, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var deadline = Stopwatch.StartNew();
        while (Native.IsWindow(handle) && deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            Thread.Sleep(100);
        }

        if (Native.IsWindow(handle))
        {
            throw new TimeoutException("専用のExplorer画面が終了しませんでした。");
        }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.ReleaseComObject(value);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(nint handle, out NativeRect rect);
}
