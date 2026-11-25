using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// 絶対座標と仮想キーを収集する試作記録器です。
/// </summary>
public sealed partial class BasicRecorder : IDisposable
{
    private static readonly TimeSpan WorkerJoinTimeout = TimeSpan.FromSeconds(3);

    private const int InvalidHookHandle = 1404;
    private readonly RecordingBuffer buffer;
    private readonly object lifecycle = new();
    private readonly TaskCompletionSource ready = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly nint excludedWindow;
    private readonly bool includeInjected;
    private readonly IRecordingHooks hooks;
    private readonly Thread worker;
    private HookCallback? keyboardCallback;
    private HookCallback? mouseCallback;
    private uint threadId;
    private int stopRequested;
    private bool started;
    private uint excludedProcess;
    private bool stopped;
    private Exception? failure;
    private string? recordedSource;
    private bool omittedBackgroundInput;
    private readonly Func<IRecordingHealthMonitor>? healthFactory;
    private IRecordingHealthMonitor? health;
    private int recoveryCount;
    private int removedHookCount;

    /// <summary>
    /// 応答途絶を検出してフックを再登録した回数です。
    /// </summary>
    public int RecoveryCount => Volatile.Read(ref recoveryCount);

    /// <summary>
    /// フックの復旧や、背景再生用の記録で省略した操作を通知します。
    /// </summary>
    public string RecordingWarning
    {
        get
        {
            int recovered = RecoveryCount;
            int removed = Volatile.Read(ref removedHookCount);
            string warning = recovered == 0 ? ""
                : $"フック応答途絶から{recovered}回復旧しました。";
            if (removed != 0)
            {
                warning += $"停止時に解除済みのフックを{removed}件検出しました。";
            }

            if (warning.Length != 0)
            {
                warning += "入力が欠落した可能性があります。再生前に内容を確認してください。";
            }

            if (Volatile.Read(ref omittedBackgroundInput))
            {
                warning += "背景再生に変換できない操作を省略しました。";
            }

            return warning;
        }
    }

    /// <summary>
    /// 収集上限による欠落があったかを取得します。
    /// </summary>
    public bool Overflowed => buffer.Overflowed;

    internal bool BackgroundPlayback
    {
        get; init;
    }

    internal bool RecordButtonClicks
    {
        get; init;
    }

    /// <summary>
    /// 記録スレッドが動作中かを取得します。
    /// </summary>
    public bool IsRunning => worker.IsAlive;

    /// <summary>
    /// 開始前からの押下入力の解放を待っているかを取得します。
    /// </summary>
    public bool WaitingForInitialRelease => buffer.WaitingForInitialRelease;

    /// <summary>
    /// マウス座標をアクティブウインドウの左上からの相対値で記録します。
    /// </summary>
    public bool RelativeMouseCoordinates
    {
        get; init;
    }

    /// <summary>
    /// 待ち時間とボタンを押していないマウス移動を省略します。
    /// </summary>
    public bool OmitIdleTimeAndMouseMoves
    {
        get => buffer.OmitIdleTimeAndMouseMoves;
        init => buffer.OmitIdleTimeAndMouseMoves = value;
    }

    /// <summary>
    /// 自画面と注入入力を除外する記録器を作成します。
    /// </summary>
    public BasicRecorder(nint excludedWindow,
        bool includeInjected = false,
        IEnumerable<int>? excludedKeys = null, uint excludedModifiers = 0)
        : this(excludedWindow, includeInjected, excludedKeys, excludedModifiers, false)
    {
    }

    /// <summary>
    /// Raw Inputによるフック監視の有効・無効を指定して記録器を作成します。
    /// </summary>
    public BasicRecorder(nint excludedWindow, bool includeInjected,
        IEnumerable<int>? excludedKeys, uint excludedModifiers, bool monitorHookHealth)
        : this(excludedWindow, includeInjected, excludedKeys,
            excludedModifiers, new NativeRecordingHooks(),
            monitorHookHealth ? () => new RawInputHookMonitor() : null)
    {
    }

    /// <summary>
    /// 修飾キーの異なる追加ショートカットも記録対象から除外します。
    /// </summary>
    public BasicRecorder(nint excludedWindow, bool includeInjected,
        IEnumerable<int>? excludedKeys, uint excludedModifiers,
        bool monitorHookHealth, IEnumerable<ScriptHotkey> additionalControls)
        : this(excludedWindow, includeInjected, excludedKeys,
            excludedModifiers, new NativeRecordingHooks(),
            monitorHookHealth ? () => new RawInputHookMonitor() : null,
            additionalControls)
    {
        ArgumentNullException.ThrowIfNull(additionalControls);
    }

    internal BasicRecorder(IRecordingHooks hooks, bool includeInjected = false)
        : this(-1, includeInjected, [], 0, hooks, null)
    {
    }

    internal BasicRecorder(IRecordingHooks hooks, IRecordingHealthMonitor health,
        bool includeInjected = false)
        : this(-1, includeInjected, [], 0, hooks, () => health)
    {
    }

    private BasicRecorder(nint excludedWindow, bool includeInjected,
        IEnumerable<int>? excludedKeys, uint excludedModifiers,
        IRecordingHooks hooks, Func<IRecordingHealthMonitor>? healthFactory,
        IEnumerable<ScriptHotkey>? additionalControls = null)
    {
        this.excludedWindow = excludedWindow;
        this.includeInjected = includeInjected;
        this.hooks = hooks;
        this.healthFactory = healthFactory;
        buffer = new RecordingBuffer(excludedKeys ?? [119, 120, 121, 118],
            excludedModifiers, additionalControls: additionalControls);
        worker = new Thread(Collect) { IsBackground = true };
    }

    /// <summary>
    /// フック登録の完了まで非同期に待機します。
    /// </summary>
    public Task StartAsync()
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(stopped, this);
            if (started)
            {
                throw new InvalidOperationException("同じ記録器は再開始できません。");
            }

            started = true;
            worker.Start();
            return ready.Task;
        }
    }

    private void Collect()
    {
        nint keyboard = 0;
        nint mouse = 0;
        WindowMovementMonitor? movement = null;
        try
        {
            threadId = Native.GetCurrentThreadId();
            Native.PeekMessageW(out _, 0, 0, 0, 0);
            Native.GetWindowThreadProcessId(excludedWindow, out excludedProcess);
            if (Volatile.Read(ref stopRequested) != 0)
            {
                throw new ObjectDisposedException(nameof(BasicRecorder));
            }
            keyboardCallback = Keyboard;
            mouseCallback = Mouse;
            health = healthFactory?.Invoke();
            health?.Start();
            keyboard = hooks.Register(Native.WhKeyboardLl, keyboardCallback);
            if (keyboard == 0)
            {
                throw new Win32Exception(hooks.LastError);
            }

            mouse = hooks.Register(Native.WhMouseLl, mouseCallback);
            if (mouse == 0)
            {
                throw new Win32Exception(hooks.LastError);
            }

            buffer.BackgroundPlayback = BackgroundPlayback;
            buffer.RecordButtonClicks = RecordButtonClicks;
            buffer.Begin(hooks.PressedKeys(), hooks.PressedButtons());
            if (Volatile.Read(ref stopRequested) != 0)
            {
                throw new ObjectDisposedException(nameof(BasicRecorder));
            }

            if (excludedWindow != -1)
            {
                movement = new WindowMovementMonitor(buffer, IsExcluded, FailCallback);
                movement.Start();
            }
            ready.TrySetResult();
            int status;
            while ((status = Native.GetMessageW(out Native.Message message, 0, 0, 0)) > 0)
            {
                try
                {
                    if (Volatile.Read(ref stopRequested) == 0)
                    {
                        int missing = health?.ProcessMessage(message.Window,
                            message.Id, message.WParam, message.LParam, message.Time) ?? 0;
                        if (missing != 0)
                        {
                            RecoverHooks(missing, ref keyboard, ref mouse);
                        }
                    }
                }
                finally
                {
                    // WM_INPUTのOS側バッファも既定ウィンドウ処理で解放します。
                    Native.TranslateMessage(ref message);
                    Native.DispatchMessageW(ref message);
                }
            }

            if (status < 0)
            {
                throw new Win32Exception();
            }

            if (Volatile.Read(ref stopRequested) == 0)
            {
                failure ??= new InvalidOperationException("記録スレッドが予期せず終了しました。");
            }
        }
        catch (Exception error)
        {
            failure ??= error;
        }
        finally
        {
            var cleanupErrors = new List<Exception>();
            ReleaseHook(keyboard, cleanupErrors);
            ReleaseHook(mouse, cleanupErrors);
            try
            {
                movement?.Dispose();
            }
            catch (Exception error)
            {
                cleanupErrors.Add(error);
            }
            try
            {
                health?.Dispose();
            }
            catch (Exception error)
            {
                cleanupErrors.Add(error);
            }
            if (cleanupErrors.Count != 0)
            {
                if (failure is not null)
                {
                    cleanupErrors.Insert(0, failure);
                }

                failure = cleanupErrors.Count == 1 ? cleanupErrors[0]
                    : new AggregateException("記録処理またはフックの解放に失敗しました。", cleanupErrors);
            }

            if (failure is not null)
            {
                // 開始失敗も解放結果を含めて通知し、未完了の待機を残しません。
                ready.TrySetException(failure);
            }
        }
    }

    private void ReleaseHook(nint hook, List<Exception> errors)
    {
        if (hook == 0)
        {
            return;
        }

        try
        {
            if (!hooks.Unregister(hook))
            {
                int error = hooks.LastError;
                if (error == InvalidHookHandle)
                {
                    // 所有フックは既に無効です。欠落の警告を残して結果生成へ進みます。
                    Interlocked.Increment(ref removedHookCount);
                }
                else
                {
                    errors.Add(new Win32Exception(error));
                }
            }
        }
        catch (Exception error)
        {
            // 一方の解放失敗で、もう一方の解放を省略しません。
            errors.Add(error);
        }
    }

    private void RecoverHooks(int missing, ref nint keyboard, ref nint mouse)
    {
        if (RecoveryCount >= 3)
        {
            throw new InvalidOperationException("フックの応答途絶が繰り返されたため記録を停止しました。");
        }
        // 再登録中に入力が届いても、途絶区間を通常の記録へ混ぜません。
        buffer.Recover(Environment.TickCount64,
            hooks.PressedKeys(), hooks.PressedButtons());
        Interlocked.Increment(ref recoveryCount);
        if ((missing & 2) != 0)
        {
            ReplaceHook(ref keyboard, Native.WhKeyboardLl, keyboardCallback!);
        }
        if ((missing & 1) != 0)
        {
            ReplaceHook(ref mouse, Native.WhMouseLl, mouseCallback!);
        }
        health?.Reset();
    }

    private void ReplaceHook(ref nint handle, int type, HookCallback callback)
    {
        if (!hooks.Unregister(handle) && hooks.LastError != InvalidHookHandle)
        {
            throw new Win32Exception(hooks.LastError, "応答が途絶したフックを解放できません。");
        }
        // 無通知解除済みのハンドル(ERROR_INVALID_HOOK_HANDLE)だけは再登録へ進みます。
        handle = 0;
        if (Volatile.Read(ref stopRequested) != 0)
        {
            return;
        }
        handle = hooks.Register(type, callback);
        if (handle == 0)
        {
            throw new Win32Exception(hooks.LastError, "フックを再登録できません。");
        }
    }

    private bool IsExcluded(nint window)
    {
        if (excludedProcess == 0)
        {
            return false;
        }

        Native.GetWindowThreadProcessId(window, out uint process);
        return process == excludedProcess;
    }

    private nint Keyboard(int code, nint message, nint data)
    {
        try
        {
            if (code >= 0 && failure is null)
            {
                KeyboardData key = Marshal.PtrToStructure<KeyboardData>(data);
                if ((key.Flags & Native.LlkhfInjected) == 0)
                {
                    health?.Hook(1, key.Time);
                }
                int virtualKey = key.Key switch
                {
                    16 => key.Scan == Native.RightShiftScanCode ? 161 : 160,
                    17 => (key.Flags & Native.LlkhfExtended) != 0 ? 163 : 162,
                    18 => (key.Flags & Native.LlkhfExtended) != 0 ? 165 : 164,
                    _ => (int)key.Key
                };
                if (includeInjected || (key.Flags & Native.LlkhfInjected) == 0)
                {
                    nint window = Native.GetForegroundWindow();
                    bool excluded = IsExcluded(window);
                    buffer.Key(virtualKey, message is Native.WmKeyUp or Native.WmSysKeyUp ? 2 : 1,
                        Environment.TickCount64, excluded,
                        !excluded && message is Native.WmKeyDown or Native.WmSysKeyDown
                            ? RecordedWindow.Capture(window) : null);
                }
                else if (message is Native.WmKeyUp or Native.WmSysKeyUp)
                {
                    // 注入UPは記録せず、開始時の押下待ちだけ解除します。
                    buffer.ReleaseInitialKey(virtualKey);
                }
            }
        }
        catch (Exception error)
        {
            FailCallback(error);
        }

        return CallNextHookEx(0, code, message, data);
    }

    private nint Mouse(int code, nint message, nint data)
    {
        try
        {
            if (code >= 0 && failure is null)
            {
                MouseData mouse = Marshal.PtrToStructure<MouseData>(data);
                if ((mouse.Flags & Native.LlmhfInjected) == 0)
                {
                    health?.Hook(0, mouse.Time);
                }
                int button = message switch
                {
                    Native.WmLButtonDown or Native.WmLButtonUp => 0,
                    Native.WmRButtonDown or Native.WmRButtonUp => 1,
                    Native.WmMButtonDown or Native.WmMButtonUp => 2,
                    Native.WmXButtonDown or Native.WmXButtonUp when (mouse.Data >> 16) is >= 1 and <= 3 =>
                        (mouse.Data >> 16 & 1) != 0 ? 3 : 4,
                    _ => -1
                };
                if ((includeInjected || (mouse.Flags & Native.LlmhfInjected) == 0)
                    && (message == Native.WmMouseMove || button >= 0
                        || message is Native.WmMouseWheel or Native.WmMouseHWheel))
                {
                    using PhysicalDpiScope? dpi = (RecordButtonClicks || BackgroundPlayback)
                        && message is Native.WmLButtonDown or Native.WmLButtonUp
                        ? new PhysicalDpiScope() : null;
                    nint window = Native.WindowFromPoint(mouse.Point);
                    bool excluded = IsExcluded(window);
                    nint originWindow = RelativeMouseCoordinates
                        ? Native.GetForegroundWindow() : window;
                    RecordMouse(buffer, message, mouse.Data, mouse.Point.X, mouse.Point.Y,
                        Environment.TickCount64, excluded,
                        (!excluded || RelativeMouseCoordinates)
                            && !IsExcluded(originWindow)
                            && (RelativeMouseCoordinates
                                || message is Native.WmLButtonDown or Native.WmRButtonDown
                                    or Native.WmMButtonDown or Native.WmXButtonDown
                                    or Native.WmMouseWheel or Native.WmMouseHWheel)
                            ? RecordedWindow.Capture(originWindow) : null,
                        (RecordButtonClicks || BackgroundPlayback) && !excluded
                            && message is Native.WmLButtonDown or Native.WmLButtonUp
                            ? RecordingClickTarget(window, mouse.Point, BackgroundPlayback)
                            : 0, mouse.Time);
                }
                else if (button >= 0 && (message is Native.WmLButtonUp or Native.WmRButtonUp
                    or Native.WmMButtonUp or Native.WmXButtonUp))
                {
                    buffer.ReleaseInitialButton(button);
                    if (message == Native.WmXButtonUp && (mouse.Data >> 16) == 3)
                    {
                        buffer.ReleaseInitialButton(4);
                    }
                }
            }
        }
        catch (Exception error)
        {
            FailCallback(error);
        }

        return CallNextHookEx(0, code, message, data);
    }

    internal static nint RecordingClickTarget(
        nint window, Native.Point point, bool background)
    {
        if (!background || window == 0)
        {
            return window;
        }

        return Native.ScreenToClient(window, ref point)
            && Native.GetClientRect(window, out Native.Rect bounds)
            && point.X >= bounds.Left && point.X < bounds.Right
            && point.Y >= bounds.Top && point.Y < bounds.Bottom
            ? window : 0;
    }

    private void FailCallback(Exception error)
    {
        // コールバックから例外をネイティブ側へ伝播させません。
        failure ??= error;
        Native.PostThreadMessageW(threadId, Native.WmQuit, 0, 0);
    }

    /// <summary>
    /// フックを解除し、保持入力の解放を付けたスクリプトを返します。
    /// </summary>
    public string Stop()
    {
        lock (lifecycle)
        {
            Dispose();
            if (failure is not null)
            {
                throw new InvalidOperationException("記録処理が失敗しました。", failure);
            }

            if (recordedSource is null)
            {
                // 停止要求が重なっても末尾確定と生成を一度だけ行います。
                buffer.Complete();
                Volatile.Write(ref omittedBackgroundInput, BackgroundPlayback
                    && buffer.Events.Any(input => input.Kind != RecordedInputKind.MouseMove
                        && input.ButtonClick is null));
                recordedSource = Serialize(
                    buffer.Events.OrderBy(input => input.Sequence),
                    OmitIdleTimeAndMouseMoves, RelativeMouseCoordinates,
                    BackgroundPlayback);
                string warning = RecordingWarning;
                if (warning.Length != 0)
                {
                    recordedSource = "// 警告: " + warning
                        + Environment.NewLine + recordedSource;
                }
            }

            return recordedSource;
        }
    }

    internal static void RecordMouse(RecordingBuffer buffer, nint message,
        uint data, int x, int y, long time, bool excluded,
        RecordedWindow? window = null, nint buttonTarget = 0, uint? inputTime = null)
    {
        if (message is Native.WmMouseWheel or Native.WmMouseHWheel)
        {
            buffer.Wheel(message == Native.WmMouseWheel ? 5 : 6, unchecked((short)(data >> 16)),
                x, y, time, excluded, window);
        }
        else if (message is Native.WmXButtonDown or Native.WmXButtonUp)
        {
            for (int button = 3; button <= 4; button++)
            {
                if (((data >> 16) & (1u << (button - 3))) != 0)
                {
                    buffer.Mouse(button, message == Native.WmXButtonUp ? 2 : 1,
                        x, y, time, excluded, window);
                }
            }
        }
        else
        {
            int button = message switch
            {
                Native.WmLButtonDown or Native.WmLButtonUp => 0,
                Native.WmRButtonDown or Native.WmRButtonUp => 1,
                Native.WmMButtonDown or Native.WmMButtonUp => 2,
                _ => -1
            };
            bool released = message is Native.WmLButtonUp or Native.WmRButtonUp
                or Native.WmMButtonUp;
            buffer.Mouse(button, released ? 2 : 1,
                x, y, time, excluded, window, buttonTarget, inputTime);
        }
    }

    internal static string Serialize(IEnumerable<InputEvent> inputs,
        bool omitIdleTimeAndMouseMoves = false,
        bool relativeMouseCoordinates = false,
        bool backgroundPlayback = false)
    {
        var text = new StringBuilder();
        var keys = new HashSet<int>();
        var buttons = new HashSet<int>();
        long? previous = null;
        RecordedWindow? previousWindow = null;
        bool previousWindowPositioned = false;
        var windowIds = new Dictionary<(nint, string, string), string>();
        foreach (InputEvent input in inputs)
        {
            if (backgroundPlayback && input.ButtonClick is null)
            {
                continue;
            }

            if (omitIdleTimeAndMouseMoves && input.Kind == RecordedInputKind.MouseMove
                && (buttons.Count == 0 || input.WindowMovement))
            {
                continue;
            }

            if (!omitIdleTimeAndMouseMoves
                && previous is long last && input.Time > last)
            {
                string seconds = ((input.Time - last) / 1000d)
                    .ToString("0.###", CultureInfo.InvariantCulture);
                text.AppendLine($"{BuiltinFunctionNames.Sleep}({seconds})");
            }

            previous = input.Time;
            if (input.MenuClick is { } menuClick)
            {
                text.AppendLine(menuClick.Command());
                previousWindow = null;
                previousWindowPositioned = false;
                continue;
            }

            if (input.ButtonClick is RecordedButtonClick click)
            {
                text.AppendLine(click.Command(backgroundPlayback));
                previousWindow = null;
                previousWindowPositioned = false;
                continue;
            }

            if (input.Kind == RecordedInputKind.WindowMovement && input.Window is RecordedWindow moved)
            {
                if (previousWindow?.Handle != moved.Handle || !previousWindowPositioned)
                {
                    text.AppendLine(moved.MovementCommand());
                }
                else if (previousWindow.X != moved.X || previousWindow.Y != moved.Y
                    || previousWindow.Width != moved.Width || previousWindow.Height != moved.Height)
                {
                    int width = previousWindow.Width == moved.Width ? 0 : moved.Width;
                    int height = previousWindow.Height == moved.Height ? 0 : moved.Height;
                    text.AppendLine($"{BuiltinFunctionNames.Acw}(0,{moved.X},{moved.Y},{width},{height},0)");
                }

                previousWindow = moved;
                previousWindowPositioned = true;
                continue;
            }
            string? windowId = null;
            if (relativeMouseCoordinates && !input.WindowMovement
                && input.Window is RecordedWindow reference)
            {
                (nint, string, string) identity =
                    (reference.Handle, reference.Title, reference.ClassName);
                if (!windowIds.TryGetValue(identity, out windowId))
                {
                    windowId = "UWSC_RECORD_WINDOW_" + (windowIds.Count + 1)
                        .ToString(CultureInfo.InvariantCulture);
                    windowIds.Add(identity, windowId);
                    text.AppendLine($"{Keywords.Dim} {windowId} = {reference.Identifier()}");
                }
            }

            bool pointerInput = input.Kind is RecordedInputKind.MouseMove
                or RecordedInputKind.MouseButton or RecordedInputKind.Wheel;
            if (windowId is not null && pointerInput)
            {
                text.AppendLine(
                    $"{Keywords.If} !{BuiltinFunctionNames.Status}({windowId},ST_ISID) "
                    + $"{Keywords.Then} {Keywords.ExitExit} 1");
            }

            // 保持入力中はACWを挟みませんが、相対値の基準は各入力時の対象を使います。
            if (input.Window is RecordedWindow window
                && (window != previousWindow || input.WindowMovement) && keys.Count == 0
                && buttons.Count == 0
                && (input.Kind == RecordedInputKind.Key || input.Kind == RecordedInputKind.Wheel
                    || relativeMouseCoordinates && input.Kind == RecordedInputKind.MouseMove
                    || input.Kind == RecordedInputKind.MouseButton && input.State == 1))
            {
                text.AppendLine(input.WindowMovement ? window.MovementCommand()
                    : relativeMouseCoordinates
                    ? $"{BuiltinFunctionNames.Acw}({windowId})"
                    : window.Command());
                previousWindow = window;
                previousWindowPositioned = input.WindowMovement || !relativeMouseCoordinates;
            }

            string x = input.X.ToString(CultureInfo.InvariantCulture);
            string y = input.Y.ToString(CultureInfo.InvariantCulture);
            if (relativeMouseCoordinates && !input.WindowMovement && pointerInput
                && input.Window is RecordedWindow origin)
            {
                string offsetX = checked(input.X - origin.X).ToString(CultureInfo.InvariantCulture);
                string offsetY = checked(input.Y - origin.Y).ToString(CultureInfo.InvariantCulture);
                x = $"{BuiltinFunctionNames.Status}({windowId},ST_X)+({offsetX})";
                y = $"{BuiltinFunctionNames.Status}({windowId},ST_Y)+({offsetY})";
            }
            if (input.Kind == RecordedInputKind.Key)
            {
                text.AppendLine($"{BuiltinFunctionNames.Kbd}({input.Code},{input.State})");
                if (input.State == 1)
                {
                    keys.Add(input.Code);
                }
                else
                {
                    keys.Remove(input.Code);
                }
            }
            else if (input.Kind == RecordedInputKind.MouseMove)
            {
                text.AppendLine($"{BuiltinFunctionNames.Mmv}({x},{y})");
            }
            else if (input.Kind == RecordedInputKind.Wheel)
            {
                string notches = (input.State / 120d).ToString("R", CultureInfo.InvariantCulture);
                text.AppendLine($"{BuiltinFunctionNames.Btn}({input.Code},{notches},{x},{y})");
            }
            else
            {
                text.AppendLine(input.Kind == RecordedInputKind.ButtonRelease
                    ? $"{BuiltinFunctionNames.Btn}({input.Code},{input.State})"
                    : $"{BuiltinFunctionNames.Btn}({input.Code},{input.State},{x},{y})");
                if (input.State == 1)
                {
                    buttons.Add(input.Code);
                }
                else
                {
                    buttons.Remove(input.Code);
                }
            }
        }

        foreach (int key in keys.Order())
        {
            text.AppendLine($"{BuiltinFunctionNames.Kbd}({key},2)");
        }

        foreach (int button in buttons.Order())
        {
            text.AppendLine($"{BuiltinFunctionNames.Btn}({button},2)");
        }

        return text.ToString();
    }

    /// <summary>
    /// 専用スレッドで収集を終了してからフックの解放を待ちます。
    /// </summary>
    public void Dispose()
    {
        lock (lifecycle)
        {
            if (stopped)
            {
                return;
            }

            Interlocked.Exchange(ref stopRequested, 1);
            if (worker.IsAlive)
            {
                if (threadId != 0)
                {
                    Native.PostThreadMessageW(threadId, Native.WmQuit, 0, 0);
                }

                if (!worker.Join(WorkerJoinTimeout))
                {
                    throw new TimeoutException("記録スレッドの終了を確認できません。");
                }
            }

            stopped = true;
        }
    }

    // Stateはキー・ボタンでは生成するKBD/BTNの引数（1=押下、2=解放）、
    // ホイールでは回転量、ウインドウ移動では段階（1=開始、2=終了、0=途中）です。
    internal readonly record struct InputEvent(
        RecordedInputKind Kind, int Code, int State, int X, int Y, long Time, long Sequence = 0,
        RecordedWindow? Window = null, bool WindowMovement = false,
        RecordedButtonClick? ButtonClick = null, nint ButtonTarget = 0,
        RecordedMenuClick? MenuCandidate = null, RecordedMenuClick? MenuClick = null);

    internal delegate nint HookCallback(int code, nint message, nint data);

    internal interface IRecordingHooks
    {
        int LastError
        {
            get;
        }

        nint Register(int type, HookCallback callback);

        bool Unregister(nint hook);

        // 既定では実入力を読み取り、試験では開始・復旧時の状態を固定します。
        IReadOnlyList<int> PressedKeys() =>
            [.. Enumerable.Range(8, 248).Where(key => key is not (16 or 17 or 18)
                && (Native.GetAsyncKeyState(key) & 0x8000) != 0)];

        IReadOnlyList<int> PressedButtons() =>
            [.. new[] { 0, 1, 2, 3, 4 }.Where(button =>
                (Native.GetAsyncKeyState(button switch
                {
                    0 => 1,
                    1 => 2,
                    2 => 4,
                    3 => 5,
                    _ => 6
                }) & 0x8000) != 0)];
    }

    internal sealed class NativeRecordingHooks : IRecordingHooks
    {
        public int LastError => Marshal.GetLastWin32Error();

        public nint Register(int type, HookCallback callback) =>
            SetWindowsHookExW(type, callback, Native.GetModuleHandleW(null), 0);

        public bool Unregister(nint hook) => Native.UnhookWindowsHookEx(hook);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardData
    {
        public uint Key;
        public uint Scan;
        public uint Flags;
        public uint Time;
        public nuint Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseData
    {
        public Native.Point Point;
        public uint Data;
        public uint Flags;
        public uint Time;
        public nuint Extra;
    }
}
