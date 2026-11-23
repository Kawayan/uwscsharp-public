using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

public sealed partial class WindowsAutomation : IWindowsAutomation,
    INumericArithmeticProvider
{
    INumericArithmeticScope INumericArithmeticProvider.CreateNumericArithmetic() =>
        new FloatingPointArithmeticBridge(
            new FloatingPointEnvironment(FloatingPointNativeModule.LoadInstalled));

    /// <summary>
    /// 実行終了後の入力禁止フックを回収するために、ホストが保持する一覧です。
    /// </summary>
    public WindowsSelectiveInputCleanup SelectiveInputCleanup { get; init; } = new();

    /// <summary>
    /// 実行ごとのコマンド出力と表示起動に使うホスト設定です。
    /// </summary>
    public WindowsCommandOptions Commands { get; init; } = new();

    // 検証時も同じ実行経路を使い、通知元の所有と解放を観測します。
    internal Func<IScriptHotkeySource>? ScriptHotkeySourceFactory
    {
        get; init;
    }

    internal Func<SensorSession>? SensorSessionFactory
    {
        get; init;
    }

    internal INativeDllLoader? NativeDllLoader
    {
        get; init;
    }

    internal SelectiveInputEmergencyRegistry EmergencyRegistry
    {
        get; init;
    } =
        SelectiveInputEmergencyRegistry.Shared;

    private readonly Action<Native.Input>? inputSender;
    private readonly Func<IInputOwnershipBackend>? inputOwnershipFactory;
    private readonly Func<SelectiveInputTarget, SelectiveInputDevices, ISelectiveInputHookSession>? selectiveInputFactory;
    private readonly IWindowIdentityPlatform? selectiveInputPlatform;
    private readonly TimeProvider? selectiveInputClock;
    private readonly IDirectInputBackend? directBackend;
    private readonly IImageCapture? imageCapture;
    private readonly IClipboardBackend? clipboardBackend;
    private readonly IComRuntime? comRuntime;
    private readonly IAccessibleActivationBackend? accessibleActivation;
    private readonly IListSelectionPlatform? listSelectionPlatform;
    private readonly Func<IListSelectionPlatform, IButtonAccessibleOperations>? buttonAccessibleFactory;
    private readonly IWindowActivation? windowActivationBackend;
    private readonly IWindowLookupEnvironment? windowLookup;
    private readonly ITouchInputBackend? touchBackend;
    private readonly bool? swappedMouseButtons;
    private readonly IKeyStateBackend? keyStateBackend;
    private readonly IDisplayPlatform? displayPlatform;
    private readonly Func<OperatingSystemInformation>? operatingSystemInformation;
    private readonly Func<CpuTimes>? cpuTimes;
    private readonly TimeProvider? cpuClock;
    private readonly Func<AudioHostPlayRequest, CancellationToken, IAudioPlaybackOperation>? audioPlaybackFactory;
    private Func<WindowsHostWindow, CancellationToken, ValueTask<nint>>? HostWindowHandle
    {
        get;
        init;
    }

    private Action<int, Action>? KeyboardInputGuard
    {
        get;
        init;
    }

    private WindowsCapturedWindow? CapturedScheduleWindow
    {
        get; init;
    }

    private Func<int, Func<Task>, Task>? AsyncKeyboardInputGuard
    {
        get; init;
    }

    /// <summary>
    /// Windowsのウィンドウ操作と入力を提供します。
    /// </summary>
    public WindowsAutomation()
    {
        inputOwnershipFactory = static () => new NativeInputOwnershipBackend();
    }

    /// <summary>
    /// 実行元が所有するログウィンドウの取得方法を指定します。
    /// </summary>
    public static WindowsAutomation WithLogWindow(
        Func<CancellationToken, ValueTask<nint>> logWindowHandle)
    {
        ArgumentNullException.ThrowIfNull(logWindowHandle);
        return WithHostWindows((kind, token) => kind == WindowsHostWindow.Log
            ? logWindowHandle(token) : ValueTask.FromResult((nint)0));
    }

    /// <summary>
    /// 実行元の特殊ウィンドウを取得します。対象がなければ0を返します。
    /// </summary>
    public static WindowsAutomation WithHostWindows(
        Func<WindowsHostWindow, CancellationToken, ValueTask<nint>> hostWindowHandle)
        => WithHostWindows(hostWindowHandle, null);

    /// <summary>
    /// 実行元のウィンドウ取得と物理キー送信中のホットキー制御を指定します。
    /// </summary>
    /// <param name="hostWindowHandle">実行元が所有する特殊ウィンドウを取得します。</param>
    /// <param name="keyboardInputGuard">
    /// 正規化した仮想キーと送信処理を受け、送信処理を同期的に1回実行します。
    /// 物理DOWNだけを対象とし、UP・Unicode・直接入力では呼びません。
    /// </param>
    public static WindowsAutomation WithHostWindows(
        Func<WindowsHostWindow, CancellationToken, ValueTask<nint>> hostWindowHandle,
        Action<int, Action>? keyboardInputGuard)
        => WithHostWindows(hostWindowHandle, keyboardInputGuard, new());

    /// <summary>
    /// 実行元の画面・キー制御とコマンド設定を指定します。
    /// </summary>
    public static WindowsAutomation WithHostWindows(
        Func<WindowsHostWindow, CancellationToken, ValueTask<nint>> hostWindowHandle,
        Action<int, Action>? keyboardInputGuard, WindowsCommandOptions commands)
        => WithHostWindows(hostWindowHandle, keyboardInputGuard, commands, new());

    /// <summary>
    /// 画面・キー制御・コマンド設定と、ホストが保持する回収一覧を指定します。
    /// </summary>
    public static WindowsAutomation WithHostWindows(
        Func<WindowsHostWindow, CancellationToken, ValueTask<nint>> hostWindowHandle,
        Action<int, Action>? keyboardInputGuard, WindowsCommandOptions commands,
        WindowsSelectiveInputCleanup selectiveInputCleanup)
    {
        ArgumentNullException.ThrowIfNull(hostWindowHandle);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(selectiveInputCleanup);
        return new WindowsAutomation
        {
            HostWindowHandle = hostWindowHandle,
            KeyboardInputGuard = keyboardInputGuard,
            Commands = commands,
            SelectiveInputCleanup = selectiveInputCleanup
        };
    }

    /// <summary>
    /// 実行元の画面取得と非同期のキー送信制御を指定します。
    /// </summary>
    /// <param name="hostWindowHandle">実行元が所有する特殊ウィンドウを取得します。</param>
    /// <param name="keyboardInputGuard">物理DOWNの送信を1回呼び、その完了と復元を待ちます。</param>
    /// <param name="commands">コマンド起動の設定です。</param>
    public static WindowsAutomation WithAsyncHostWindows(
        Func<WindowsHostWindow, CancellationToken, ValueTask<nint>> hostWindowHandle,
        Func<int, Func<Task>, Task> keyboardInputGuard, WindowsCommandOptions commands)
        => WithAsyncHostWindows(hostWindowHandle, keyboardInputGuard, commands, new());

    /// <summary>
    /// 非同期のキー制御と、ホストが保持する回収一覧を指定します。
    /// </summary>
    public static WindowsAutomation WithAsyncHostWindows(
        Func<WindowsHostWindow, CancellationToken, ValueTask<nint>> hostWindowHandle,
        Func<int, Func<Task>, Task> keyboardInputGuard, WindowsCommandOptions commands,
        WindowsSelectiveInputCleanup selectiveInputCleanup)
        => WithAsyncHostWindows(hostWindowHandle, keyboardInputGuard, commands,
            selectiveInputCleanup, null);

    /// <summary>
    /// 非同期のキー制御と、実行ごとに捕捉した予約窓を指定します。
    /// </summary>
    public static WindowsAutomation WithAsyncHostWindows(
        Func<WindowsHostWindow, CancellationToken, ValueTask<nint>> hostWindowHandle,
        Func<int, Func<Task>, Task> keyboardInputGuard, WindowsCommandOptions commands,
        WindowsSelectiveInputCleanup selectiveInputCleanup,
        WindowsCapturedWindow? capturedScheduleWindow)
    {
        ArgumentNullException.ThrowIfNull(hostWindowHandle);
        ArgumentNullException.ThrowIfNull(keyboardInputGuard);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(selectiveInputCleanup);
        return new WindowsAutomation
        {
            HostWindowHandle = hostWindowHandle,
            AsyncKeyboardInputGuard = keyboardInputGuard,
            CapturedScheduleWindow = capturedScheduleWindow,
            Commands = commands,
            SelectiveInputCleanup = selectiveInputCleanup
        };
    }

    internal WindowsAutomation(
        Action<Native.Input> inputSender,
        IDirectInputBackend? directBackend = null, IImageCapture? imageCapture = null,
        IClipboardBackend? clipboardBackend = null, IComRuntime? comRuntime = null,
        IAccessibleActivationBackend? accessibleActivation = null,
        IListSelectionPlatform? listSelectionPlatform = null,
        Func<IListSelectionPlatform, IButtonAccessibleOperations>? buttonAccessibleFactory = null,
        IWindowActivation? windowActivationBackend = null,
        IWindowLookupEnvironment? windowLookup = null,
        Action<int, Action>? keyboardInputGuard = null,
        ITouchInputBackend? touchBackend = null,
        bool? swappedMouseButtons = null,
        IKeyStateBackend? keyStateBackend = null,
        IDisplayPlatform? displayPlatform = null,
        Func<OperatingSystemInformation>? operatingSystemInformation = null,
        Func<CpuTimes>? cpuTimes = null, TimeProvider? cpuClock = null,
        Func<AudioHostPlayRequest, CancellationToken, IAudioPlaybackOperation>? audioPlaybackFactory = null,
        Func<IInputOwnershipBackend>? inputOwnershipFactory = null,
        Func<int, Func<Task>, Task>? asyncKeyboardInputGuard = null,
        Func<SelectiveInputTarget, SelectiveInputDevices, ISelectiveInputHookSession>? selectiveInputFactory = null,
        IWindowIdentityPlatform? selectiveInputPlatform = null,
        TimeProvider? selectiveInputClock = null,
        Func<WindowsHostWindow, CancellationToken, ValueTask<nint>>? hostWindowHandle = null,
        WindowsCapturedWindow? capturedScheduleWindow = null)
    {
        this.inputSender = inputSender;
        this.inputOwnershipFactory = inputOwnershipFactory;
        this.selectiveInputFactory = selectiveInputFactory;
        this.selectiveInputPlatform = selectiveInputPlatform;
        this.selectiveInputClock = selectiveInputClock;
        HostWindowHandle = hostWindowHandle;
        CapturedScheduleWindow = capturedScheduleWindow;
        this.directBackend = directBackend;
        this.imageCapture = imageCapture;
        this.clipboardBackend = clipboardBackend;
        this.comRuntime = comRuntime;
        this.accessibleActivation = accessibleActivation;
        this.listSelectionPlatform = listSelectionPlatform;
        this.buttonAccessibleFactory = buttonAccessibleFactory;
        this.windowActivationBackend = windowActivationBackend;
        this.windowLookup = windowLookup;
        KeyboardInputGuard = keyboardInputGuard;
        AsyncKeyboardInputGuard = asyncKeyboardInputGuard;
        this.touchBackend = touchBackend;
        this.swappedMouseButtons = swappedMouseButtons;
        this.keyStateBackend = keyStateBackend;
        this.displayPlatform = displayPlatform;
        this.operatingSystemInformation = operatingSystemInformation;
        this.cpuTimes = cpuTimes;
        this.cpuClock = cpuClock;
        this.audioPlaybackFactory = audioPlaybackFactory;
    }

    /// <summary>
    /// 実行ごとに独立した自動操作セッションを作成します。
    /// </summary>
    public IWindowsAutomationSession CreateSession()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Windows操作にはWindowsが必要です。");
        }

        ArgumentNullException.ThrowIfNull(Commands);
        ArgumentNullException.ThrowIfNull(SelectiveInputCleanup);
        Commands.Validate();
        return new Session(inputSender, directBackend, imageCapture, clipboardBackend, comRuntime, accessibleActivation,
            listSelectionPlatform ?? new NativeListSelectionPlatform(), buttonAccessibleFactory,
            HostWindowHandle, windowActivationBackend, windowLookup, KeyboardInputGuard,
            touchBackend, swappedMouseButtons, keyStateBackend, displayPlatform,
            operatingSystemInformation, cpuTimes, cpuClock, commandOptions: Commands,
            audioPlaybackFactory: audioPlaybackFactory,
            inputOwnershipFactory: inputOwnershipFactory,
            asyncKeyboardInputGuard: AsyncKeyboardInputGuard,
            selectiveInputFactory: selectiveInputFactory,
            selectiveInputPlatform: selectiveInputPlatform,
            selectiveInputClock: selectiveInputClock,
            selectiveInputCleanup: SelectiveInputCleanup.Registry,
            selectiveEmergencyRegistry: EmergencyRegistry,
            scriptHotkeySourceFactory: ScriptHotkeySourceFactory,
            capturedScheduleWindow: CapturedScheduleWindow,
            nativeDllLoader: NativeDllLoader, sensorSessionFactory: SensorSessionFactory);
    }

    [SupportedOSPlatform("windows")]
    internal static IEnumerable<nint> FindRecordedButtons(nint root, string caption) =>
        Session.FindButtons(root, caption, 0, default);

    private sealed partial class Session(
        Action<Native.Input>? inputSender,
        IDirectInputBackend? directBackend, IImageCapture? imageCapture,
        IClipboardBackend? clipboardBackend, IComRuntime? comRuntime,
        IAccessibleActivationBackend? accessibleActivation,
        IListSelectionPlatform listSelectionPlatform,
        Func<IListSelectionPlatform, IButtonAccessibleOperations>? buttonAccessibleFactory,
        Func<WindowsHostWindow, CancellationToken, ValueTask<nint>>? hostWindowHandle,
        IWindowActivation? windowActivationBackend,
        IWindowLookupEnvironment? windowLookup,
        Action<int, Action>? keyboardInputGuard,
        ITouchInputBackend? touchBackend,
        bool? swappedMouseButtons,
        IKeyStateBackend? keyStateBackend,
        IDisplayPlatform? displayPlatform,
        Func<OperatingSystemInformation>? operatingSystemInformation,
        Func<CpuTimes>? cpuTimes, TimeProvider? cpuClock,
        RunResources? sharedResources = null,
        bool ownsResources = true,
        WindowsCommandOptions? commandOptions = null,
        Func<AudioHostPlayRequest, CancellationToken, IAudioPlaybackOperation>? audioPlaybackFactory = null,
        Func<IInputOwnershipBackend>? inputOwnershipFactory = null,
        Func<int, Func<Task>, Task>? asyncKeyboardInputGuard = null,
        Func<SelectiveInputTarget, SelectiveInputDevices, ISelectiveInputHookSession>? selectiveInputFactory = null,
        IWindowIdentityPlatform? selectiveInputPlatform = null,
        TimeProvider? selectiveInputClock = null,
        SelectiveInputCleanupRegistry? selectiveInputCleanup = null,
        SelectiveInputEmergencyRegistry? selectiveEmergencyRegistry = null,
        Func<IScriptHotkeySource>? scriptHotkeySourceFactory = null,
        WindowsCapturedWindow? capturedScheduleWindow = null,
        INativeDllLoader? nativeDllLoader = null,
        Func<SensorSession>? sensorSessionFactory = null) : IWindowsAutomationSession, IControlledComMemberInvoker,
        IShortcutKeyBackend, INativeDllModuleSource
    {
        private const int MaximumNumberedItemControls = 99;
        private readonly WindowsCommandOptions commands = commandOptions ?? new();
        private readonly DisplayEnvironment display = new(
            displayPlatform ?? new NativeDisplayPlatform());

        private readonly IKeyStateBackend keyState =
            keyStateBackend ?? new NativeKeyStateBackend();
        private readonly MouseKeyHistory mouseKeyHistory = new();
        private readonly object mousePollGate = new();
        private long nextMousePoll;

        private readonly DirectInputSession direct = new(
            directBackend ?? new NativeDirectInputBackend());
        private readonly IImageCapture capture = imageCapture ?? new NativeImageCapture();
        private readonly IClipboardBackend clipboard = clipboardBackend ?? new ClipboardBackend();
        private readonly IWindowActivation windowActivation = windowActivationBackend ?? new NativeWindowActivation();
        private readonly CpuUsageSampler cpu = new(
            cpuTimes ?? CpuTimes.Read, cpuClock ?? TimeProvider.System);
        private readonly IWindowLookupEnvironment lookup = windowLookup ?? new NativeWindowLookupEnvironment();
        private readonly RunResources resources =
            sharedResources ?? new RunResources(comRuntime, touchBackend,
                audioPlaybackFactory, commandOptions?.CommandHost, inputOwnershipFactory,
                selectiveInputFactory, selectiveInputPlatform, selectiveInputClock,
                selectiveInputCleanup, selectiveEmergencyRegistry, nativeDllLoader, sensorSessionFactory);
        private readonly object disposalGate = new();
        private Task? disposal;
        private readonly HashSet<int> keys = [];
        private readonly HashSet<int> buttons = [];
        private readonly TouchInputSession touch = new(
            touchBackend is null ? new NativeTouchInputBackend()
                : new BorrowedTouchBackend(touchBackend));
        // 実行中にOS設定が変わっても、DOWNとUPの対応を変えません。
        private readonly bool swapMouseButtons =
            swappedMouseButtons ?? Native.GetSystemMetrics(23) != 0;
        private readonly ConditionalWeakTable<Exception, string> scriptErrors = new();
        private nint originWindow;
        private bool hasMouseOrigin;
        private bool backgroundCapture;
        private bool clientOrigin;
        private bool directOrigin;
        private nint directRoot;
        private int lastWindow;
        private bool disposed;
        private readonly UiaMozillaPreparation mozillaPreparation = new();
        private readonly Lazy<IButtonAccessibleOperations> buttonAccessible = new(() =>
        {
            IListSelectionPlatform platform = listSelectionPlatform;
            return buttonAccessibleFactory?.Invoke(platform)
                ?? new NativeButtonAccessibleBackend(platform,
                    new NativeButtonPointWindow(new NativeButtonPointWindowPlatform()));
        });

        private readonly AccessibleActionContinuation accessibleContinuation = new();

        private ProviderItemMouse ItemMouse =>
            new(Send, swapMouseButtons, listSelectionPlatform);

        private ComSession Com
        {
            get
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return resources.Com;
            }
        }

        /// <summary>
        /// 直前のCOM列挙結果を保持します。
        /// </summary>
        public IReadOnlyList<Value> AllOleItems { get; private set; } = [];

        public ValueTask<Value> InvokeMemberAsync(Value target, string name,
            IReadOnlyList<Value> arguments, Value? assigned, CancellationToken cancellationToken) =>
            Com.InvokeAsync(target, name, arguments, assigned, cancellationToken);

        ValueTask<Value> IControlledComMemberInvoker.InvokeControlledMemberAsync(
            Value target, string name, IReadOnlyList<Value> arguments,
            Value? assigned, ushort controlWord, CancellationToken token) =>
            Com.InvokeAsync(target, name, arguments, assigned, token, controlWord);

        ValueTask<WindowsMemberResult>
            IControlledComMemberInvoker.InvokeControlledMemberWithReferencesAsync(
                Value target, string name, IReadOnlyList<WindowsMemberArgument> arguments,
                Value? assigned, ushort controlWord, CancellationToken token) =>
            Com.InvokeWithReferencesAsync(target, name, arguments, assigned,
                token, controlWord);

        private bool hasComEvents;

        public ValueTask SetComEventAsync(Value target, string? interfaceName,
            string? eventName, Func<IReadOnlyList<Value>, CancellationToken,
                ValueTask<IReadOnlyList<Value>>>? callback,
            CancellationToken cancellationToken)
        {
            hasComEvents = true;
            return Com.SetScriptEventAsync(this, target, interfaceName, eventName,
                callback, runToken, cancellationToken);
        }

        public ValueTask<WindowsMemberResult> InvokeMemberWithReferencesAsync(Value target,
            string name, IReadOnlyList<WindowsMemberArgument> arguments, Value? assigned,
            CancellationToken cancellationToken) => Com.InvokeWithReferencesAsync(
                target, name, arguments, assigned, cancellationToken);

        public ValueTask<IReadOnlyList<Value>> EnumerateObjectsAsync(Value target,
            CancellationToken cancellationToken) => Com.EnumerateAsync(target, cancellationToken);
        private ScriptPauseControl? pauseControl;
        private CancellationToken runToken;

        /// <summary>
        /// 押下中の入力がない場合に一時停止を許可します。
        /// </summary>
        public bool CanPause => keys.Count == 0 && buttons.Count == 0
            && !direct.HasHeldInput && !touch.HasHeldInput;

        /// <summary>
        /// 待機処理へ実行単位の一時停止制御を接続します。
        /// </summary>
        public void ConfigurePause(ScriptPauseControl? control,
            CancellationToken cancellationToken)
        {
            pauseControl = control;
            runToken = cancellationToken;
            if (ownsResources)
            {
                resources.ConfigureAudioCancellation(cancellationToken);
                resources.ConfigureSpeechCancellation(cancellationToken);
                resources.ConfigureRecognitionCancellation(cancellationToken);
                resources.ConfigureSensorCancellation(cancellationToken);
                resources.ConfigureInputCancellation(cancellationToken);
                resources.ConfigureSelectiveCancellation(cancellationToken);
            }

            direct.ConfigurePause(control, cancellationToken, () => CanPause);
        }

        private Task DelayMillisecondsAsync(int milliseconds,
            CancellationToken token)
        {
            var duration = TimeSpan.FromMilliseconds(milliseconds);
            return pauseControl is not null && !runToken.IsCancellationRequested
                ? pauseControl.DelayAsync(duration, token, Task.Delay, () => CanPause)
                : Task.Delay(duration, token);
        }

        private async ValueTask WaitForPauseAsync(
            Stopwatch watch, CancellationToken token)
        {
            if (pauseControl is null || runToken.IsCancellationRequested
                || !CanPause || !pauseControl.IsPauseRequested)
            {
                return;
            }

            watch.Stop();
            try
            {
                await pauseControl.WaitAsync(token).ConfigureAwait(false);
            }
            finally
            {
                watch.Start();
            }
        }

        /// <summary>
        /// 直前のGETITEMで取得した文字列を保持します。
        /// </summary>
        public IReadOnlyList<string> AllItems
        {
            get;
            private set;
        } = [];

        /// <summary>
        /// 直前の画像探索結果を保持します。
        /// </summary>
        public WindowsImageResult ImageResult { get; private set; } = WindowsImageResult.Empty;

        private int Register(nint handle)
            => resources.Register(handle);

        private nint Resolve(int id,
            bool foreground = true) => id == 0 && foreground
            ? Native.GetForegroundWindow() : ResolveRegistered(id);

        private nint ResolveRegistered(int id)
        {
            nint handle = resources.Resolve(id);
            return handle != 0 && Native.IsWindow(handle) ? handle : 0;
        }

        /// <summary>
        /// Mozilla系の親ウィンドウに限り、UIA操作前の準備処理を返します。
        /// </summary>
        private UiaMozillaPreparation? PreparationFor(string rootClass) =>
            UiaSelection.IsMozilla(rootClass) ? mozillaPreparation : null;

        private static string Text(nint handle,
            bool className = false)
        {
            var buffer = new StringBuilder(className ? 256
                : Math.Max(256, Native.GetWindowTextLengthW(handle) + 1));
            if (className)
            {
                Native.GetClassNameW(handle, buffer,
                    buffer.Capacity);
            }
            else
            {
                Native.GetWindowTextW(handle, buffer,
                    buffer.Capacity);
            }

            return buffer.ToString();
        }

        private static Native.Rect Rect(nint handle,
            bool client = false)
        {
            bool ok = client ? Native.GetClientRect(handle,
                out Native.Rect rect) : Native.GetWindowRect(handle, out rect);
            if (!ok)
            {
                throw WindowsOperationErrors.Record(
                    new Win32Exception(
                    Marshal.GetLastWin32Error()));
            }

            return rect;
        }

        private Native.Point Position(int x, int y)
        {
            using var dpi = new PhysicalDpiScope();
            if (!hasMouseOrigin)
            {
                return new(x, y);
            }

            nint handle = RequireMouseOrigin();

            if (clientOrigin)
            {
                var point = new Native.Point(x, y);
                Check(Native.ClientToScreen(handle, ref point));
                return point;
            }

            Native.Rect rect = Rect(handle);
            return new(WindowsOperationErrors.Add(x, rect.Left), WindowsOperationErrors.Add(y, rect.Top));
        }

        /// <summary>
        /// 確認済みの操作エラーをscriptへ公開する本文へ分類します。
        /// </summary>
        public string? GetScriptErrorMessage(string name, Exception exception)
        {
            if (name.Equals(BuiltinFunctionNames.RecoState, StringComparison.OrdinalIgnoreCase)
                || name.Equals(BuiltinFunctionNames.Dictate, StringComparison.OrdinalIgnoreCase))
            {
                string? message = resources.GetRecognitionScriptErrorMessage(exception);
                if (message is not null)
                {
                    return message;
                }
            }

            if (name.Equals(BuiltinFunctionNames.CreateOleObj, StringComparison.OrdinalIgnoreCase)
                || name.Equals(BuiltinFunctionNames.GetActiveOleObj, StringComparison.OrdinalIgnoreCase)
                || name.Equals(BuiltinFunctionNames.IeGetData, StringComparison.OrdinalIgnoreCase)
                || name.Equals(BuiltinFunctionNames.IeSetData, StringComparison.OrdinalIgnoreCase)
                || name.Equals(BuiltinFunctionNames.IeLink, StringComparison.OrdinalIgnoreCase)
                || name.Equals(BuiltinFunctionNames.IeGetSrc, StringComparison.OrdinalIgnoreCase)
                || name.Equals(BuiltinFunctionNames.IeGetFrame, StringComparison.OrdinalIgnoreCase)
                || name.Equals(BuiltinFunctionNames.IeSetSrc, StringComparison.OrdinalIgnoreCase)
                || name.Equals(BuiltinFunctionNames.GetOleItem, StringComparison.OrdinalIgnoreCase)
                || name.Equals(BuiltinFunctionNames.SafeArray, StringComparison.OrdinalIgnoreCase)
                || name.Equals(BuiltinFunctionNames.VarType, StringComparison.OrdinalIgnoreCase))
            {
                return GetObjectScriptErrorMessage(exception);
            }

            if (scriptErrors.TryGetValue(exception, out string? function)
                && name.Equals(function, StringComparison.OrdinalIgnoreCase))
            {
                if (name.Equals(BuiltinFunctionNames.ClkItem, StringComparison.OrdinalIgnoreCase)
                    || name.Equals(BuiltinFunctionNames.ChkBtn, StringComparison.OrdinalIgnoreCase))
                {
                    // 識別済みの失敗だけに、共有wrapperの報告接頭辞を付けます。
                    return "FucCLKITEM:" + exception.Message;
                }

                return exception.Message;
            }

            return null;
        }

        /// <summary>
        /// このCOM sessionで確認した操作失敗だけをscriptへ公開します。
        /// </summary>
        public string? GetObjectScriptErrorMessage(Exception exception) =>
            resources.GetComErrorMessage(exception);

        /// <summary>
        /// 共有COMセッションで識別した失敗だけを抑止対象にします。
        /// </summary>
        public bool IsComError(Exception exception) =>
            resources.GetComErrorMessage(exception) is not null;

        private T ScriptError<T>(string name, T exception)
            where T : Exception
        {
            // 例外の型や本文ではなく、検証箇所で生成した実体を識別します。
            // 弱参照にして、捕捉済みの例外をsessionが保持し続けないようにします。
            scriptErrors.Add(exception, name);
            return exception;
        }

        private static List<nint> SelectionControls(nint root,
            CancellationToken token, bool listOnly = false,
            bool includeDisabled = false)
        {
            var candidates = new List<(nint Window, string Label)>();
            var result = new List<nint>();
            if (root == 0)
            {
                return result;
            }

            bool rootVisible = Native.IsWindowVisible(root);
            Native.EnumChildWindows(root, (child, _) =>
            {
                string label = NativeSelectionTarget.Label(child);
                bool visible = Native.IsWindowVisible(child)
                    || (!rootVisible && (Native.GetWindowLongW(child, Native.GwlStyle) & Native.WsVisible) != 0);
                if (label.Length != 0 && visible
                    && (includeDisabled || Native.IsWindowEnabled(child)))
                {
                    // selectorは先に列挙した親ComboBoxExに属するComboを除外します。
                    NativeSelectionKind kind = NativeSelectionTarget.Kind(label);
                    bool matches = listOnly
                        ? kind is NativeSelectionKind.Combo or NativeSelectionKind.List
                        : kind != NativeSelectionKind.None;
                    if (matches
                        && !NativeSelectionTarget.IsNestedCombo(label,
                            Native.GetParent(child), candidates, listOnly))
                    {
                        result.Add(child);
                    }

                    candidates.Add((child, label));
                }

                return true;
            }, 0);
            token.ThrowIfCancellationRequested();
            nint[] ordered = SortByPosition(result,
                window => window);

            token.ThrowIfCancellationRequested();
            return [.. ordered];
        }

        private static T[] SortByPosition<T>(IReadOnlyList<T> candidates,
            Func<T, nint> window)
        {
            T[] ordered = [.. candidates];
            int[] keys = [.. ordered.Select(candidate =>
            {
                Native.Rect rect = Rect(window(candidate));
                return WindowsOperationErrors.Add(
                    WindowsOperationErrors.Int32((long)rect.Top * 10000), rect.Left);
            })];
            if (ordered.Length > 1)
            {
                DirectTargetSelector.Sort(ordered, keys, 0, ordered.Length - 1);
            }

            return ordered;
        }

        private static List<nint> Controls(nint root,
            IReadOnlyList<string> classNames,
            bool includeDisabled, CancellationToken token, bool allowHiddenRoot = false,
            Func<nint, bool>? accepts = null)
        {
            var result = new List<nint>();
            if (root == 0)
            {
                return result;
            }

            bool rootVisible = !allowHiddenRoot || Native.IsWindowVisible(root);
            Native.EnumChildWindows(root, (child, _) =>
            {
                if ((accepts?.Invoke(child) ?? classNames.Contains(Text(child, true),
                    StringComparer.OrdinalIgnoreCase))
                    && (Native.IsWindowVisible(child)
                    || (!rootVisible && (Native.GetWindowLongW(child, Native.GwlStyle) & Native.WsVisible) != 0))
                    && (includeDisabled || Native.IsWindowEnabled(child)))
                {
                    result.Add(child);
                }

                return true;
            }, 0);
            token.ThrowIfCancellationRequested();
            nint[] ordered = SortByPosition(result,
                window => window);

            token.ThrowIfCancellationRequested();
            return [.. ordered];
        }

        internal static IEnumerable<nint> FindButtons(nint root, string label,
            int occurrence, CancellationToken token, bool allowHiddenRoot = false,
            bool partial = false, bool includeDisabled = false)
        {
            int number = occurrence < 0 ? WindowsOperationErrors.Int32(-(long)occurrence) : occurrence;
            int matched = 0;
            string desired = ButtonName.Normalize(NativeListNameMatcher.AsciiUpper(
                partial ? label : NativeListControl.Trim(label)));
            foreach (nint child in Controls(root, [],
                includeDisabled || occurrence < 0, token, allowHiddenRoot,
                child => NativeButtonControl.IsClass(NativeSelectionTarget.Label(child))))
            {
                token.ThrowIfCancellationRequested();
                string candidate = ButtonName.Normalize(
                    NativeListNameMatcher.AsciiUpper(NativeButtonControl.ReadName(child, token)));
                if (NativeListNameMatcher.Compare(ref candidate, desired, partial)
                    && (WindowsOperationErrors.Increment(ref matched) == number || number == 0))
                {
                    yield return child;
                }
            }
        }

        private Value CheckImage(string path, int transparent,
            int? x1, int? y1, int? x2, int? y2, int number,
            int tolerance, CancellationToken token)
        {
            PixelImage template = path.Length == 0 ? clipboard.ReadImage(token)
                : ImageTemplate.Load(path, token);
            token.ThrowIfCancellationRequested();
            ImageSource source = GetImageSource();
            token.ThrowIfCancellationRequested();
            ImageArea bounds = source.Bounds;
            Native.Point origin = source.Origin;
            if ((x1.HasValue && x2.HasValue && x1.Value > x2.Value)
                || (y1.HasValue && y2.HasValue && y1.Value > y2.Value))
            {
                throw ScriptError(BuiltinFunctionNames.ChkImg,
                    new ArgumentException("画像探索範囲が逆転しています。"));
            }

            if (hasMouseOrigin && (bounds.Width < 1 || bounds.Height < 1))
            {
                token.ThrowIfCancellationRequested();
                ImageResult = WindowsImageResult.Empty;
                return number < 0 ? N(0) : Value.Logical(false);
            }

            long left = Math.Max(bounds.X, x1.HasValue ? (long)origin.X + x1.Value : bounds.X);
            long top = Math.Max(bounds.Y, y1.HasValue ? (long)origin.Y + y1.Value : bounds.Y);
            long right = Math.Min((long)bounds.X + bounds.Width - 1,
                x2.HasValue ? (long)origin.X + x2.Value : (long)bounds.X + bounds.Width - 1);
            long bottom = Math.Min((long)bounds.Y + bounds.Height - 1,
                y2.HasValue ? (long)origin.Y + y2.Value : (long)bounds.Y + bounds.Height - 1);
            if (left > right || top > bottom)
            {
                throw ScriptError(BuiltinFunctionNames.ChkImg,
                    new ArgumentException("画像探索範囲が空または逆転しています。"));
            }

            var area = new ImageArea(
                WindowsOperationErrors.Int32(left),
                WindowsOperationErrors.Int32(top),
                WindowsOperationErrors.Int32(right - left + 1),
                WindowsOperationErrors.Int32(bottom - top + 1));
            PixelImage image = CaptureImage(source, area, token);
            if (transparent == -1 && template.Width == 1 && template.Height == 1)
            {
                // 原版の専用出口は番号や範囲原点を適用せず、成功と座標0を返します。
                token.ThrowIfCancellationRequested();
                ImageResult = new(0, 0, [], []);
                return Value.Logical(true);
            }

            int selectedNumber = number == 0 ? 1 : number;
            IReadOnlyList<(int X, int Y)> hits = ImageMatcher.Match(
                image, template, transparent, selectedNumber, tolerance, token);
            var allX = new List<int>();
            var allY = new List<int>();
            if (number < 0)
            {
                foreach ((int x, int y) in hits)
                {
                    allX.Add(WindowsOperationErrors.Subtract(
                        WindowsOperationErrors.Add(x, area.X), origin.X));
                    allY.Add(WindowsOperationErrors.Subtract(
                        WindowsOperationErrors.Add(y, area.Y), origin.Y));
                }
            }

            WindowsImageResult result = hits.Count == 0 ? WindowsImageResult.Empty
                : new(
                    WindowsOperationErrors.Subtract(
                        WindowsOperationErrors.Add(hits[^1].X, area.X), origin.X),
                    WindowsOperationErrors.Subtract(
                        WindowsOperationErrors.Add(hits[^1].Y, area.Y), origin.Y),
                    allX.AsReadOnly(), allY.AsReadOnly());
            token.ThrowIfCancellationRequested();
            ImageResult = result;
            return number < 0
                ? N(hits.Count)
                : Value.Logical(hits.Count == selectedNumber);
        }

        private readonly record struct SliderTarget(nint Window, int Bar, bool Vertical);

        private static List<SliderTarget> Sliders(nint root, CancellationToken token,
            bool includeDisabled = false)
        {
            using var dpi = new PhysicalDpiScope();
            var candidates = new List<SliderTarget>();
            if (!Native.IsWindow(root))
            {
                return candidates;
            }

            void Add(nint window, bool includeControl)
            {
                token.ThrowIfCancellationRequested();
                if (!Native.IsWindowVisible(window)
                    || (!includeDisabled && !Native.IsWindowEnabled(window)))
                {
                    return;
                }

                int style = Native.GetWindowLongW(window, Native.GwlStyle);
                NativeSliderKind kind = NativeSliderDiscovery.Kind(Text(window, true));
                if (includeControl && kind == NativeSliderKind.Trackbar)
                {
                    candidates.Add(new(window, -1, (style & 2) != 0));
                }
                else if (includeControl && kind == NativeSliderKind.ScrollBar)
                {
                    candidates.Add(new(window, 2, (style & 1) != 0));
                }

                if ((style & 0x100000) != 0)
                {
                    candidates.Add(new(window, 0, false));
                }

                if ((style & 0x200000) != 0)
                {
                    candidates.Add(new(window, 1, true));
                }
            }

            Add(root, false);
            NativeSliderDiscovery.VisitChildren(root,
                child => Add(child, true), token);
            token.ThrowIfCancellationRequested();
            SliderTarget[] ordered = SortByPosition(candidates, candidate =>
            {
                token.ThrowIfCancellationRequested();
                return candidate.Window;
            });

            token.ThrowIfCancellationRequested();
            return [.. ordered];
        }

        private static bool SetSlider(SliderTarget selected, int requested,
            CancellationToken token)
        {
            nint target = selected.Window;
            bool scroll = selected.Bar >= 0;
            int minimum;
            int maximum;
            if (scroll)
            {
                NativeScrollControl.ScrollInfo info = NativeScrollControl.Read(target, token, selected.Bar);
                minimum = info.Minimum;
                maximum = WindowsOperationErrors.Calculate(() => checked((int)Math.Max(minimum,
                    (long)info.Maximum - Math.Max(0, (long)info.Page - 1))));
            }
            else
            {
                minimum = unchecked((int)ControlMessage(target, Native.TbmGetRangeMin, token));
                maximum = unchecked((int)ControlMessage(target, Native.TbmGetRangeMax, token));
            }
            if (minimum > maximum)
            {
                return false;
            }

            int desired = Math.Clamp(requested, minimum, maximum);
            int current = SliderPosition(selected, token);
            if (current == desired)
            {
                return true;
            }

            nint parent = selected.Bar is 0 or 1 ? target : Native.GetParent(target);
            if (!Native.IsWindow(parent))
            {
                return false;
            }

            if (scroll)
            {
                NativeScrollControl.Write(target, desired, token, selected.Bar);
            }
            else
            {
                ControlMessage(target, Native.TbmSetPos, token, 1, desired);
            }

            if (!Native.IsWindow(target)
                || SliderPosition(selected, token) != desired)
            {
                return false;
            }

            // 位置設定だけでは通知されないため、親または所有windowへ一度だけ通知します。
            uint notification = selected.Vertical
                ? Native.WmVScroll : Native.WmHScroll;
            uint positionCode = ((uint)desired & 0xffff) << 16 | 4;
            ControlMessage(parent, notification, token,
                unchecked((nint)positionCode), selected.Bar is 0 or 1 ? 0 : target);
            return Native.IsWindow(target)
                && SliderPosition(selected, token) == desired;
        }

        private static int SliderPosition(SliderTarget selected, CancellationToken token) =>
            selected.Bar >= 0 ? NativeScrollControl.Read(selected.Window, token, selected.Bar).Position
                : unchecked((int)ControlMessage(selected.Window, Native.TbmGetPos, token));

        private static nuint ControlMessage(nint target, uint message,
            CancellationToken token, nint wParam = 0, nint lParam = 0)
        {
            token.ThrowIfCancellationRequested();
            if (Native.SendMessageTimeoutW(target, message, wParam, lParam,
                Native.SmtoAbortIfHung, Native.MessageTimeoutMilliseconds, out nuint result) == 0)
            {
                throw WindowsOperationErrors.Record(
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }

            token.ThrowIfCancellationRequested();
            return result;
        }

        // native照会の同期処理をまとめ、資源解放後に共通復元を行います。
        private Task<(bool Found, int Value)> ReadNativeCheckAsync(nint root,
            string label, int occurrence, CancellationToken token)
        {
            if (root == 0)
            {
                return Task.FromResult((false, -1));
            }

            IListSelectionPlatform platform = listSelectionPlatform;
            return NativeControlOperation.RunAsync(root, platform, active =>
            {
                using NativeControlBufferOwner owner = platform.CreateControlBufferOwner(root);
                nint button = FindButtons(root, label, occurrence, active,
                    allowHiddenRoot: true).FirstOrDefault();
                if (button != 0)
                {
                    IButtonAccessibleOperations accessible = buttonAccessible.Value;
                    int state;
                    if (accessible.Matches(root, active))
                    {
                        int role = 0;
                        state = accessible.Read(root, button, active, ref role, out _);
                    }
                    else
                    {
                        state = (int)ControlMessage(button, Native.BmGetCheck, active);
                    }

                    active.ThrowIfCancellationRequested();
                    // 原本queryはgateを再評価しますが、どちらも取得値を保存します。
                    accessible.Matches(root, active);
                    active.ThrowIfCancellationRequested();
                    bool enabled = Native.IsWindowEnabled(button);
                    active.ThrowIfCancellationRequested();
                    return (true, enabled ? state : -1);
                }

                var treeBuffer = new NativeTreeClickBuffer(owner);
                foreach (nint tree in KnownControls(root, ControlClassNames.TreeView, false, active,
                    allowHiddenRoot: true))
                {
                    (bool Found, int Value) state = NativeTreeControl.Check(
                        tree, label, active, treeBuffer, platform, occurrence);
                    if (state.Found)
                    {
                        return (true, state.Value);
                    }
                }

                var buffer = new NativeListViewClickBuffer(owner);
                int remaining = occurrence;
                foreach (nint view in KnownControls(root, ControlClassNames.ListView, false, active,
                    allowHiddenRoot: true))
                {
                    (int Matches, bool Found, int Value) state = NativeListViewControl.Check(
                        view, label, remaining, active, buffer, platform);
                    if (state.Found)
                    {
                        return (true, state.Value);
                    }

                    remaining -= state.Matches;
                }

                return (false, -1);
            }, token);
        }

        private static int ItemNumber(ClickItemFlags types,
            ClickItemFlags type, int number) => (types & type) != 0 && number < 0
                ? WindowsOperationErrors.Calculate(() => checked(-number)) : number;

        private async Task<bool> ClickItemAsync(nint root, string label, int kind,
            int occurrence, int listOccurrence, int request, CancellationToken token)
        {
            bool on = request != 0;
            if (root == 0)
            {
                return false;
            }

            // 未定義のビットも保持したまま、スクリプトの指定を一度だけ型付けする。
            var flags = (ClickItemFlags)kind;

            const ClickItemFlags nativeOnly = ClickItemFlags.Api;
            bool providerAllowed = (flags & nativeOnly) == 0
                && Native.IsWindowVisible(root);
            string rootClass = Text(root, true);
            UiaMozillaPreparation? preparation = PreparationFor(rootClass);
            bool uiaSearched = providerAllowed
                && (flags & ClickItemFlags.OfThis) == 0
                && UiaSelection.PrefersUia(rootClass);
            if (uiaSearched)
            {
                (bool Handled, bool Success) preferred = await UiaItemActions.TryInvokeAsync(
                    root, label, kind, listOccurrence, request, token, preparation, ItemMouse)
                    .ConfigureAwait(false);
                if (preferred.Handled)
                {
                    return preferred.Success;
                }
            }

            ClickItemFlags types = flags & ClickItemFlags.Types;
            if (types == 0)
            {
                types = ClickItemFlags.Types;
            }

            int listNumber = ItemNumber(types, ClickItemFlags.List, listOccurrence);
            int menuNumber = ItemNumber(types, ClickItemFlags.Menu, listOccurrence);
            int tabNumber = ItemNumber(types, ClickItemFlags.Tab, listOccurrence);
            int treeNumber = ItemNumber(types, ClickItemFlags.TreeView, listOccurrence);
            int toolbarNumber = ItemNumber(types, ClickItemFlags.Toolbar, listOccurrence);
            IListSelectionPlatform platform = listSelectionPlatform;
            var click = new NativeClickRequest(root, label, kind, occurrence,
                listOccurrence, request, listNumber, tabNumber,
                treeNumber, toolbarNumber);
            ItemActionResult ClickNative(CancellationToken active)
            {
                bool matched = false;
                using NativeControlBufferOwner owner = platform.CreateControlBufferOwner(root);
                if ((types & ClickItemFlags.Button) != 0)
                {
                    ItemActionResult result =
                        ClickNativeButtons(click, active);
                    if (result.Handled)
                    {
                        return result;
                    }
                }

                if ((types & ClickItemFlags.List) != 0)
                {
                    ItemActionResult result =
                        ClickNativeLists(click, active, () => matched = true);
                    if (result.Handled)
                    {
                        return result;
                    }
                }

                if ((types & ClickItemFlags.Tab) != 0)
                {
                    ItemActionResult result =
                        ClickNativeTabs(click, owner, active, () => matched = true);
                    if (result.Handled)
                    {
                        return result;
                    }
                }

                if ((types & ClickItemFlags.Menu) != 0)
                {
                    bool invoked = NativeMenuControl.Invoke(root, label, menuNumber,
                        active, on, platform, (flags & ClickItemFlags.Background) != 0,
                        (flags & ClickItemFlags.Partial) != 0, () => matched = true);
                    if (invoked)
                    {
                        return ItemActionResult.Done(true);
                    }
                }

                if ((types & ClickItemFlags.TreeView) != 0)
                {
                    ItemActionResult result =
                        ClickNativeTrees(click, owner, active, () => matched = true);
                    if (result.Handled)
                    {
                        return result;
                    }
                }

                if ((types & ClickItemFlags.ListView) != 0)
                {
                    ItemActionResult result =
                        ClickNativeListViews(click, owner, active, () => matched = true);
                    if (result.Handled)
                    {
                        return result;
                    }
                }

                if ((types & ClickItemFlags.Toolbar) != 0)
                {
                    ItemActionResult result =
                        ClickNativeToolbars(click, owner, active);
                    if (result.Handled)
                    {
                        return result;
                    }
                }

                return new ItemActionResult(matched, false);
            }

            if ((types & ClickItemFlags.NativeTypes) != 0)
            {
                ItemActionResult result = await NativeControlOperation.RunAsync(
                    root, platform, ClickNative, token).ConfigureAwait(false);
                if (result.Handled)
                {
                    return result.Success;
                }
            }

            if (!providerAllowed)
            {
                return false;
            }

            if (!uiaSearched)
            {
                (bool Handled, bool Success) uia = await UiaItemActions.TryInvokeAsync(
                    root, label, kind, listOccurrence, request, token, preparation, ItemMouse)
                    .ConfigureAwait(false);
                if (uia.Handled)
                {
                    return uia.Success;
                }
            }

            if (types == ClickItemFlags.Link
                || (flags & ClickItemFlags.OfThis) != 0)
            {
                return false;
            }

            return await AccessibleItems.InvokeAsync(root, label, kind,
                occurrence, request, token, accessibleContinuation, ItemMouse).ConfigureAwait(false);
        }

        private async Task ReadItemsAsync(nint root, int kinds,
            int occurrence, int column, bool includeDisabled, int maximum,
            CancellationToken token, ItemCollector result)
        {
            occurrence = occurrence < 0 ? -1 : Math.Max(1, occurrence);
            void Add(string text)
            {
                token.ThrowIfCancellationRequested();
                result.Add(text);
            }

            ItemKinds ordinary = (ItemKinds)kinds & ~(ItemKinds.AccessibleTypes
                | ItemKinds.FromLast | ItemKinds.Background | ItemKinds.ExtendedAccessible);
            string rootClass = root == 0 ? "" : Text(root, true);
            if (ordinary != 0 && UiaSelection.PrefersUia(rootClass))
            {
                await UiaItemInventory.ReadAsync(root, ordinary, occurrence,
                    column, includeDisabled, token, Add,
                    PreparationFor(rootClass),
                    kind =>
                    {
                        foreach (string text in ReadCaptions(root, kind, includeDisabled, token))
                        {
                            Add(text);
                        }
                    }).ConfigureAwait(false);
            }
            else
            {
                foreach (ItemKinds kind in new[]
                {
                    ItemKinds.Button, ItemKinds.List, ItemKinds.Tab,
                    ItemKinds.Menu, ItemKinds.TreeView, ItemKinds.ListView,
                    ItemKinds.Edit, ItemKinds.Static, ItemKinds.StatusBar,
                    ItemKinds.Toolbar, ItemKinds.Link
                })
                {
                    if (((ItemKinds)kinds & kind) == 0)
                    {
                        continue;
                    }

                    if (kind == ItemKinds.List)
                    {
                        await ReadListItemsAsync(root, occurrence, includeDisabled, token, Add).ConfigureAwait(false);
                    }
                    else if (kind == ItemKinds.Tab)
                    {
                        foreach (nint tab in KnownControls(root, ControlClassNames.Tab, includeDisabled, token))
                        {
                            await NativeTabControl.ReadAsync(tab, token, Add).ConfigureAwait(false);
                        }
                    }
                    else if (kind is ItemKinds.TreeView or ItemKinds.ListView)
                    {
                        List<nint> controls = KnownControls(root,
                            kind == ItemKinds.TreeView ? ControlClassNames.TreeView : ControlClassNames.ListView, includeDisabled, token);
                        for (int index = 0; index < Math.Min(controls.Count, MaximumNumberedItemControls); index++)
                        {
                            if (occurrence != -1 && index != occurrence - 1)
                            {
                                continue;
                            }

                            if (kind == ItemKinds.TreeView)
                            {
                                await NativeTreeControl.ReadAsync(controls[index], token, Add).ConfigureAwait(false);
                            }
                            else
                            {
                                await NativeListViewControl.ReadAsync(controls[index], column, token, onItem: Add).ConfigureAwait(false);
                            }
                        }
                    }
                    else if (kind == ItemKinds.Menu)
                    {
                        foreach (NativeMenuControl.Item item in NativeMenuControl.Read(root, includeDisabled, token))
                        {
                            Add(item.Path);
                        }
                    }
                    else if (kind == ItemKinds.StatusBar)
                    {
                        foreach (nint status in KnownControls(root, ControlClassNames.StatusBar, includeDisabled, token))
                        {
                            await NativeStatusControl.ReadAsync(status, token, onItem: Add).ConfigureAwait(false);
                        }
                    }
                    else if (kind == ItemKinds.Toolbar)
                    {
                        foreach (nint toolbar in KnownControls(root, ControlClassNames.Toolbar, includeDisabled, token))
                        {
                            await NativeToolbarControl.ReadAsync(toolbar, includeDisabled, token, Add).ConfigureAwait(false);
                        }

                        NativeMenuControl.ReadTopLevel(root, includeDisabled, token, Add);
                    }
                    else if (kind == ItemKinds.Link)
                    {
                        await UiaControl.ReadLinksAsync(root, includeDisabled, token, Add).ConfigureAwait(false);
                    }
                    else
                    {
                        foreach (string text in ReadCaptions(root, kind, includeDisabled, token))
                        {
                            Add(text);
                        }
                    }
                }
            }

            if ((kinds & (int)ItemKinds.AccessibleTypes) != 0)
            {
                // 初回ACC追加後から配列全体の件数で上限を判定します。
                int accessibleMaximum = maximum > 0
                    ? Math.Max(1, maximum - result.Count) : 0;
                await AccessibleItems.ReadAsync(root, kinds,
                    (kinds & (int)ItemKinds.FromLast) != 0, includeDisabled, accessibleMaximum,
                    token, accessibleActivation, Add).ConfigureAwait(false);
            }
        }

        private static void AppendControlItems(List<string> destination,
            List<string> source)
        {
            if (source.Count > UwscArray.MaximumElements - destination.Count
                || source.Sum(text => text.Length)
                    > NativeListControl.MaximumCharacters - destination.Sum(text => text.Length))
            {
                throw WindowsOperationErrors.Record(
                    new InvalidOperationException("GETITEMの取得上限を超えました。"));
            }

            destination.AddRange(source);
        }

        private static List<nint> KnownControls(nint root, string className,
            bool includeDisabled, CancellationToken token, bool allowHiddenRoot = false) =>
            KnownControls(root, [className], includeDisabled, token, allowHiddenRoot);

        private static List<nint> KnownControls(nint root, IReadOnlyList<string> classNames,
            bool includeDisabled, CancellationToken token, bool allowHiddenRoot = false) =>
            Controls(root, classNames, includeDisabled, token, allowHiddenRoot,
                accepts: window =>
                {
                    string actual = Text(window, true);
                    return classNames.Any(standard => NativeTextControlNames.Matches(actual, standard));
                });

        private static IEnumerable<string> ReadCaptions(nint root, ItemKinds kind,
            bool includeDisabled, CancellationToken token)
        {
            string className = kind == ItemKinds.Button ? ControlClassNames.Button
                : kind == ItemKinds.Edit ? ControlClassNames.Edit : ControlClassNames.Static;
            foreach (nint control in KnownControls(root, className, includeDisabled, token))
            {
                token.ThrowIfCancellationRequested();
                string text = ReadControlText(control);
                token.ThrowIfCancellationRequested();
                yield return kind == ItemKinds.Button ? ButtonName.Normalize(text)
                    : NativeListControl.Trim(text);
            }
        }

        private static async Task ReadListItemsAsync(nint root, int occurrence,
            bool includeDisabled, CancellationToken token, Action<string> onItem)
        {
            var enumerationBuffer = new ListEnumerationBuffer();
            List<nint> lists = SelectionControls(root, token,
                listOnly: true, includeDisabled);
            for (int index = 0; index < Math.Min(lists.Count, MaximumNumberedItemControls); index++)
            {
                if (occurrence != -1 && index != occurrence - 1)
                {
                    continue;
                }

                await NativeListControl.ReadAsync(lists[index], token, text =>
                {
                    string trimmed = NativeListControl.Trim(text);
                    if (trimmed.Length > 0)
                    {
                        onItem(trimmed);
                    }
                }, enumerationBuffer).ConfigureAwait(false);
            }
        }

        private static void Check(bool success)
        {
            if (!success)
            {
                throw WindowsOperationErrors.Record(
                    new Win32Exception(
                    Marshal.GetLastWin32Error()));
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (disposalGate)
            {
                if (disposed)
                {
                    return disposal is { IsCompleted: false }
                        ? new ValueTask(disposal) : ValueTask.CompletedTask;
                }

                disposed = true;
                disposal = DisposeCoreAsync();
                return new ValueTask(disposal);
            }
        }

        private async Task DisposeCoreAsync()
        {
            List<Exception> errors = [];
            if (hasComEvents)
            {
                try
                {
                    await resources.Com.DisconnectEventOwnerAsync(this).ConfigureAwait(false);
                }
                catch (Exception failure)
                {
                    errors.Add(failure);
                }
            }
            if (ownsResources)
            {
                await resources.StopSelectiveInputAsync(errors).ConfigureAwait(false);
                await resources.StopInputBlockingAsync(errors).ConfigureAwait(false);
            }

            try
            {
                await dialogCancellation.CancelAsync().ConfigureAwait(false);
                await resources.CloseDialogOwnerAsync(this, dialogCancellation.Token).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
            finally
            {
                dialogCancellation.Dispose();
            }

            lock (mousePollGate)
            {
                mouseKeyHistory.Clear();
            }
            direct.Release(errors);
            await touch.ReleaseAsync(errors).ConfigureAwait(false);
            foreach (int key in keys.ToArray())
            {
                try
                {
                    await KeyAsync(key, true).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }

            foreach (int button in buttons)
            {
                try
                {
                    Mouse(button, 2);
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }

            keys.Clear();
            buttons.Clear();
            accessibleContinuation.Clear();
            if (buttonAccessible.IsValueCreated)
            {
                try
                {
                    buttonAccessible.Value.Dispose();
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }

            try
            {
                if (ownsResources)
                {
                    errors.AddRange(await resources.DisposeRootAsync()
                        .ConfigureAwait(false));
                }
                else
                {
                    try
                    {
                        CloseNativeDllWorker();
                    }
                    finally
                    {
                        resources.ReleaseFork();
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }

            AllItems = [];
            AllOleItems = [];
            AllWindowIds = [];
            ImageResult = WindowsImageResult.Empty;
            if (errors.Count > 0)
            {
                throw new AggregateException(
                    "自動操作sessionの解放に失敗しました。", errors);
            }

        }
    }
}
