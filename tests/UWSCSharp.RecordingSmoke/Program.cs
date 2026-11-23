using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;

namespace UWSCSharp.RecordingSmoke;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        // 実入力を送る検証は破棄可能なSandboxゲストだけで実行します。
        if (Environment.UserName != "WDAGUtilityAccount")
        {
            Console.Error.WriteLine("Windows Sandbox内で実行してください。");
            return 2;
        }

        ApplicationConfiguration.Initialize();
        using var form = new Form
        {
            Text = "記録再生fixture",
            StartPosition = FormStartPosition.CenterScreen,
            Size = new Size(400, 200),
            TopMost = true
        };
        using var text = new TextBox { Left = 20, Top = 20, Width = 250 };
        using var button = new Button
        {
            Text = "検証クリック",
            Left = 20,
            Top = 70,
            Width = 150
        };
        int clicks = 0;
        int result = 1;
        button.Click += (_, _) => clicks++;
        form.Controls.Add(text);
        form.Controls.Add(button);
        form.Shown += async (_, _) =>
        {
            try
            {
                ShowWindow(form.Handle, 5);
                form.Show();
                form.Activate();
                text.Focus();
                uint foregroundThread = GetWindowThreadProcessId(
                    GetForegroundWindow(), out _);
                uint currentThread = GetCurrentThreadId();
                bool attached = foregroundThread != currentThread
                    && AttachThreadInput(currentThread, foregroundThread, true);
                SetForegroundWindow(form.Handle);
                text.Focus();
                if (attached)
                {
                    AttachThreadInput(currentThread, foregroundThread, false);
                }

                await Task.Delay(200);
                if (GetForegroundWindow() != form.Handle)
                {
                    throw new InvalidOperationException("fixtureを前景にできません。");
                }
                using var recorder = new BasicRecorder(-1, includeInjected: true);
                await recorder.StartAsync();
                await using (IWindowsAutomationSession session =
                    new WindowsAutomation().CreateSession())
                {
                    await session.InvokeAsync("KBD", [Value.Numeric(65)], default);
                    await Task.Delay(80);
                    Point point = button.PointToScreen(new Point(30, 15));
                    await session.InvokeAsync("MMV",
                        [Value.Numeric(point.X), Value.Numeric(point.Y)], default);
                    await session.InvokeAsync("BTN", [Value.Numeric(0)], default);
                }

                await Task.Delay(200);
                string source = recorder.Stop();
                if (text.Text != "a" || clicks != 1
                    || !source.Contains("KBD(65,1)")
                    || !source.Contains("BTN(0,1,"))
                {
                    throw new InvalidOperationException(
                        $"収集/fixture入力が一致しません: text={text.Text}, clicks={clicks}, foreground={GetForegroundWindow()}, fixture={form.Handle}, " + source);
                }

                text.Clear();
                clicks = 0;
                text.Focus();
                await new Interpreter(windows: new WindowsAutomation())
                    .RunAsync(source, _ => { });
                await Task.Delay(100);
                if (text.Text != "a" || clicks != 1)
                {
                    throw new InvalidOperationException("生成スクリプトの再生結果が一致しません。");
                }

                await CheckDragAsync(form, outsidePanel: false);
                await CheckDragAsync(form, outsidePanel: true);
                text.Focus();

                using var filtered = new BasicRecorder(-1);
                await filtered.StartAsync();
                await using (IWindowsAutomationSession session =
                    new WindowsAutomation().CreateSession())
                {
                    await session.InvokeAsync("KBD", [Value.Numeric(65)], default);
                }

                await Task.Delay(100);
                if (filtered.Stop().Contains("KBD(65,"))
                {
                    throw new InvalidOperationException("注入キーが除外されていません。");
                }

                await using (IWindowsAutomationSession session = new WindowsAutomation().CreateSession())
                {
                    await session.InvokeAsync("KBD", [Value.Numeric(16), Value.Numeric(1)], default);
                    await Task.Delay(80);
                    using var initial = new BasicRecorder(-1, includeInjected: true);
                    await initial.StartAsync();
                    if (!initial.WaitingForInitialRelease)
                    {
                        throw new InvalidOperationException("開始前のSHIFT保持が検出されていません。");
                    }
                    await session.InvokeAsync("KBD", [Value.Numeric(65)], default);
                    await session.InvokeAsync("KBD", [Value.Numeric(16), Value.Numeric(2)], default);
                    await session.InvokeAsync("KBD", [Value.Numeric(66)], default);
                    await Task.Delay(100);
                    string recorded = initial.Stop();
                    if (initial.WaitingForInitialRelease || recorded.Contains("KBD(65,") || !recorded.Contains("KBD(66,1)"))
                    {
                        throw new InvalidOperationException("開始前のSHIFT保持の境界が一致しません: " + recorded);
                    }
                }

                using (var chord = new BasicRecorder(-1, includeInjected: true,
                    excludedKeys: [121], excludedModifiers: 3))
                {
                    await chord.StartAsync();
                    await new Interpreter(windows: new WindowsAutomation()).RunAsync(
                        "KBD(17,DOWN)\nKBD(18,DOWN)\nKBD(121)\nKBD(18,UP)\nKBD(17,UP)\nKBD(121)", _ => { });
                    await Task.Delay(100);
                    string recorded = chord.Stop();
                    if (recorded.Contains("KBD(162,") || recorded.Contains("KBD(164,")
                        || recorded.Split("KBD(121,1)").Length != 2)
                    {
                        throw new InvalidOperationException("制御chordの除外が一致しません: " + recorded);
                    }
                }

                for (int iteration = 0; iteration < 5; iteration++)
                {
                    using var rapid = new BasicRecorder(-1);
                    Task starting = rapid.StartAsync();
                    rapid.Dispose();
                    try
                    {
                        await starting;
                    }
                    catch (ObjectDisposedException)
                    {
                    }

                    if (rapid.IsRunning)
                    {
                        throw new InvalidOperationException("開始直後の停止でスレッドが残っています。");
                    }
                }

                using (var concurrent = new BasicRecorder(-1, includeInjected: true))
                {
                    await concurrent.StartAsync();
                    await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
                    await session.InvokeAsync("KBD", [Value.Numeric(16), Value.Numeric(1)], default);
                    await Task.Delay(100);
                    Task<string>[] stops = [.. Enumerable.Range(0, 8)
                        .Select(_ => Task.Run(concurrent.Stop))];
                    string[] results = await Task.WhenAll(stops);
                    if (concurrent.IsRunning || results.Any(source => source != results[0])
                        || !results[0].Contains("KBD(160,1)") || !results[0].Contains("KBD(160,2)"))
                    {
                        throw new InvalidOperationException("同時停止で記録結果またはmodifier解放が一致しません。");
                    }
                }

                string sustained = args.Contains("--sustained")
                    ? await CheckSustainedRecordingAsync(button) : "";
                Console.WriteLine("PASS: native-hooks, generated-script-replay, keyboard-text, mouse-click, drag-inside, drag-capture-outside, injected-filter, initial-held, control-chord, immediate-stop, concurrent-stop" + sustained);
                result = 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
            }
            finally
            {
                form.Close();
            }
        };
        Application.Run(form);
        return result;
    }

    private static async Task CheckDragAsync(Form form, bool outsidePanel)
    {
        using var panel = new Panel
        {
            Left = 200,
            Top = 70,
            Width = 120,
            Height = 60,
            BackColor = Color.LightBlue
        };
        form.Controls.Add(panel);
        int downs = 0;
        int ups = 0;
        int heldMoves = 0;
        Point start = default;
        Point end = default;
        panel.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                downs++;
                start = e.Location;
                panel.Capture = true;
            }
        };
        panel.MouseMove += (_, e) =>
        {
            if ((e.Button & MouseButtons.Left) != 0)
            {
                heldMoves++;
            }
        };
        panel.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ups++;
                end = e.Location;
                panel.Capture = false;
            }
        };
        var expectedStart = new Point(10, 10);
        var expectedEnd = new Point(outsidePanel ? 160 : 80, 35);
        Point from = panel.PointToScreen(expectedStart);
        Point middle = panel.PointToScreen(new Point(40, 20));
        Point to = panel.PointToScreen(expectedEnd);
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        await session.InvokeAsync("MMV", [Value.Numeric(from.X), Value.Numeric(from.Y)], default);
        await Task.Delay(50);
        using var recorder = new BasicRecorder(-1, includeInjected: true);
        await recorder.StartAsync();
        await session.InvokeAsync("BTN", [Value.Numeric(0), Value.Numeric(1)], default);
        await Task.Delay(50);
        await session.InvokeAsync("MMV", [Value.Numeric(middle.X), Value.Numeric(middle.Y)], default);
        await Task.Delay(50);
        await session.InvokeAsync("MMV", [Value.Numeric(to.X), Value.Numeric(to.Y)], default);
        await Task.Delay(50);
        await session.InvokeAsync("BTN", [Value.Numeric(0), Value.Numeric(2)], default);
        await Task.Delay(100);
        string source = recorder.Stop();
        void VerifyDrag(string phase)
        {
            if (downs != 1 || ups != 1 || heldMoves == 0
                || start != expectedStart || end != expectedEnd
                || (GetAsyncKeyState(1) & 0x8000) != 0 || panel.Capture)
            {
                throw new InvalidOperationException(
                    $"ドラッグ{phase}が不正です: outside={outsidePanel}, down={downs}, up={ups}, moves={heldMoves}, start={start}, end={end}");
            }
        }

        VerifyDrag("収集");
        string down = $"BTN(0,1,{from.X},{from.Y})";
        string movement = $"MMV({to.X},{to.Y})";
        string up = $"BTN(0,2,{to.X},{to.Y})";
        int downIndex = source.IndexOf(down, StringComparison.Ordinal);
        int moveIndex = source.IndexOf(movement, StringComparison.Ordinal);
        int upIndex = source.IndexOf(up, StringComparison.Ordinal);
        if (downIndex < 0 || moveIndex <= downIndex || upIndex <= moveIndex)
        {
            throw new InvalidOperationException("ドラッグ生成の順序・座標が不正です: " + source);
        }

        downs = 0;
        ups = 0;
        heldMoves = 0;
        start = default;
        end = default;
        await new Interpreter(windows: new WindowsAutomation()).RunAsync(source, _ => { });
        await Task.Delay(100);
        VerifyDrag("再生");
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    private static async Task<string> CheckSustainedRecordingAsync(Button button)
    {
        using var recorder = new BasicRecorder(-1, includeInjected: true);
        await recorder.StartAsync();
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        Point origin = button.PointToScreen(new Point(10, 10));
        await session.InvokeAsync("MMV", [Value.Numeric(origin.X), Value.Numeric(origin.Y)], default);
        // 移動の統合を無効にして、フックへ継続的な試験入力を流します。
        MouseInputPacket[] packets = [.. Enumerable.Range(0, 256).Select(index =>
            new MouseInputPacket
            {
                Mouse = new MouseInput { X = index % 2 == 0 ? 1 : -1, Flags = 0x2001 }
            })];
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        long moves = 0;
        long? saturatedHeap = null;
        long maximumHeap = 0;
        long nextSample = 0;
        while (elapsed.Elapsed < TimeSpan.FromSeconds(120))
        {
            if (SendInput((uint)packets.Length, packets, Marshal.SizeOf<MouseInputPacket>())
                != packets.Length)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }

            moves += packets.Length;
            await Task.Delay(10);
            if (!recorder.IsRunning)
            {
                throw new InvalidOperationException("継続負荷中に記録スレッドが終了しました。");
            }

            if (elapsed.ElapsedMilliseconds >= nextSample && recorder.Overflowed)
            {
                long heap = GC.GetTotalMemory(forceFullCollection: true);
                saturatedHeap ??= heap;
                maximumHeap = Math.Max(maximumHeap, heap);
                nextSample = elapsed.ElapsedMilliseconds + 5_000;
            }
        }

        if (!recorder.Overflowed || saturatedHeap is null)
        {
            throw new InvalidOperationException($"継続負荷で記録上限に到達しませんでした: sent={moves}");
        }

        long growth = maximumHeap - saturatedHeap.Value;
        if (growth > 4 * 1024 * 1024)
        {
            throw new InvalidOperationException($"上限後の保持ヒープ増加が4MiBを超えました: {growth}");
        }

        var stopping = System.Diagnostics.Stopwatch.StartNew();
        string source = recorder.Stop();
        stopping.Stop();
        int recordedMoves = source.Split('\n').Count(line => line.StartsWith("MMV("));
        if (recorder.IsRunning || recordedMoves != 50_000)
        {
            throw new InvalidOperationException($"継続負荷の停止/件数が不正です: {recordedMoves}");
        }

        return $", sustained: seconds={elapsed.Elapsed.TotalSeconds:F1}, sent_moves={moves}, recorded_moves={recordedMoves}, retained_heap_growth={growth}, stop_ms={stopping.ElapsedMilliseconds}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        internal int X;
        internal int Y;
        internal uint Data;
        internal uint Flags;
        internal uint Time;
        internal nuint Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputPacket
    {
        internal uint Type;
        internal MouseInput Mouse;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, MouseInputPacket[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint current, uint target, bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
