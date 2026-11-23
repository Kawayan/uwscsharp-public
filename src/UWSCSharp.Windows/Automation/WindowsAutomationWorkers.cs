using System.Runtime.Versioning;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

public sealed partial class WindowsAutomation
{
    [SupportedOSPlatform("windows")]
    private sealed partial class Session
    {
        /// <summary>
        /// ID・COM・DLLを共有し、入力保持と履歴を分離します。
        /// </summary>
        public IWindowsAutomationSession Fork()
        {
            lock (disposalGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                resources.AcquireFork();
                try
                {
                    return new Session(inputSender, directBackend,
                        imageCapture, clipboardBackend, comRuntime,
                        accessibleActivation, listSelectionPlatform,
                        buttonAccessibleFactory, hostWindowHandle,
                        windowActivationBackend, windowLookup,
                        keyboardInputGuard, touchBackend,
                        swapMouseButtons, keyStateBackend, displayPlatform,
                        operatingSystemInformation, cpuTimes, cpuClock,
                        resources, ownsResources: false, commandOptions: commands,
                        asyncKeyboardInputGuard: asyncKeyboardInputGuard,
                        scriptHotkeySourceFactory: scriptHotkeySourceFactory,
                        capturedScheduleWindow: capturedScheduleWindow)
                    {
                        originWindow = originWindow,
                        hasMouseOrigin = hasMouseOrigin,
                        backgroundCapture = backgroundCapture,
                        clientOrigin = clientOrigin,
                        directOrigin = directOrigin,
                        directRoot = directRoot,
                        lastWindow = lastWindow,
                        AllItems = Array.AsReadOnly(AllItems.ToArray()),
                        AllOleItems = Array.AsReadOnly(AllOleItems.ToArray()),
                        AllWindowIds = Array.AsReadOnly(AllWindowIds.ToArray()),
                        ImageResult = new WindowsImageResult(
                            ImageResult.X, ImageResult.Y,
                            Array.AsReadOnly(ImageResult.AllX.ToArray()),
                            Array.AsReadOnly(ImageResult.AllY.ToArray()))
                    };
                }
                catch
                {
                    resources.ReleaseFork();
                    throw;
                }
            }
        }

        /// <summary>
        /// このworkerの所有Threadへ接続するnative優先度を作成します。
        /// </summary>
        public IWorkerThreadPriority CreateWorkerThreadPriority()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return new WindowsWorkerThreadPriority();
        }
    }

    /// <summary>
    /// 実行全体のID・COM・DLLを持ち、rootだけが共有資源を解放します。
    /// </summary>
    [SupportedOSPlatform("windows")]
    private sealed partial class RunResources(
        IComRuntime? comRuntime, ITouchInputBackend? injectedTouch,
        Func<AudioHostPlayRequest, CancellationToken, IAudioPlaybackOperation>? audioPlaybackFactory,
        WindowsCommandHost? audioHost,
        Func<IInputOwnershipBackend>? inputOwnershipFactory,
        Func<SelectiveInputTarget, SelectiveInputDevices, ISelectiveInputHookSession>? selectiveInputFactory,
        IWindowIdentityPlatform? selectiveInputPlatform,
        TimeProvider? selectiveInputClock,
        SelectiveInputCleanupRegistry? selectiveInputCleanup,
        SelectiveInputEmergencyRegistry? selectiveEmergencyRegistry,
        INativeDllLoader? nativeDllLoader,
        Func<SensorSession>? sensorSessionFactory)
    {
        private readonly object gate = new();
        private readonly WindowRegistrationTable windows = new(
            selectiveInputPlatform ?? new NativeWindowIdentityPlatform());
        private ComSession? com;
        private NativeDllModuleCache? nativeDlls;
        private Task<NativeDllRelayClient>? nativeDllRelay;
        private NativeDialogService? dialogs;
        private DialogAppearance appearance = new("UWSC#");
        private readonly CancellationTokenSource dialogStop = new();
        internal CancellationToken DialogStopToken => dialogStop.Token;

        internal DialogAppearance Appearance
        {
            get
            {
                lock (gate)
                {
                    return appearance;
                }
            }
        }

        internal void ConfigureDialogContext(string scriptName)
        {
            lock (gate)
            {
                appearance = appearance with
                {
                    Title = string.IsNullOrEmpty(scriptName) ? "UWSC#" : "UWSC# - " + scriptName
                };
            }
        }

        internal void ConfigureDialogOption(string name, IReadOnlyList<Value> values)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(closing, this);
                appearance = appearance.Apply(name, values);
            }
        }

        internal NativeDialogService Dialogs
        {
            get
            {
                lock (gate)
                {
                    ObjectDisposedException.ThrowIf(closing, this);
                    return dialogs ??= new NativeDialogService(windowDestroyed: Invalidate);
                }
            }
        }

        internal Task CloseDialogOwnerAsync(object owner, CancellationToken token)
        {
            lock (gate)
            {
                return dialogs?.CloseOwnerAsync(owner, token) ?? Task.CompletedTask;
            }
        }

        internal Task<int> GetBalloonIdAsync(object owner, CancellationToken token)
        {
            lock (gate)
            {
                return dialogs?.GetBalloonIdAsync(owner, Register, token)
                    ?? Task.FromResult(-1);
            }
        }

        private void Invalidate(nint handle)
        {
            lock (gate)
            {
                windows.Invalidate(handle);
            }
        }

        private TaskCompletionSource? forksReleased;
        private int forks;
        private bool closing;
        private bool disposed;

        internal ComSession Com
        {
            get
            {
                lock (gate)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    // 起動を予約するだけで、COMの待機はこのlock外です。
                    return com ??= new ComSession(comRuntime);
                }
            }
        }

        internal string? GetComErrorMessage(Exception error)
        {
            lock (gate)
            {
                return com?.GetScriptErrorMessage(error);
            }
        }

        internal int Register(nint handle)
        {
            if (handle == 0)
            {
                return -1;
            }

            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
            }

            return windows.Register(handle);
        }

        internal int RegisterCaptured(WindowsCapturedWindow captured)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
            }

            return windows.RegisterExpected(captured.Handle,
                new(captured.ProcessId, captured.ThreadId));
        }

        internal nint Resolve(int id)
        {
            lock (gate)
            {
                // native IsWindowは呼出元がlock外で確認します。
                return windows.Resolve(id);
            }
        }

        internal void AcquireFork()
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(closing, this);
                forks = checked(forks + 1);
            }
        }

        internal void ReleaseFork()
        {
            lock (gate)
            {
                if (forks <= 0)
                {
                    throw new InvalidOperationException("workerの所有数が不正です。");
                }

                forks--;
                if (forks == 0)
                {
                    forksReleased?.TrySetResult();
                }
            }
        }

        internal async Task<IReadOnlyList<Exception>> DisposeRootAsync()
        {
            selectiveEmergency.MarkExecutionEnded();
            Task wait;
            lock (gate)
            {
                closing = true;
                wait = forks == 0 ? Task.CompletedTask
                    : (forksReleased ??= new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }

            List<Exception> errors = [];
            try
            {
                await dialogStop.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }

            await DisposeAudioAsync(errors).ConfigureAwait(false);
            await DisposeSpeechAsync(errors).ConfigureAwait(false);
            await DisposeRecognitionAsync(errors).ConfigureAwait(false);
            await DisposeSensorAsync(errors).ConfigureAwait(false);
            await wait.ConfigureAwait(false);
            await DisposeInputAsync(errors).ConfigureAwait(false);
            await DisposeSelectiveRegistrationsAsync().ConfigureAwait(false);
            try
            {
                await selectiveEmergency.DisposeExecutionAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }

            if (selectiveEmergency.TakeFailure() is { } emergencyFailure)
            {
                errors.Add(emergencyFailure);
            }
            ComSession? ownedCom;
            NativeDllModuleCache? ownedDlls;
            Task<NativeDllRelayClient>? ownedRelay;
            lock (gate)
            {
                disposed = true;
                windows.Dispose();
                ownedCom = com;
                ownedDlls = nativeDlls;
                ownedRelay = nativeDllRelay;
            }

            try
            {
                if (dialogs is not null)
                {
                    await dialogs.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
            finally
            {
                dialogStop.Dispose();
            }

            try
            {
                if (ownedCom is not null)
                {
                    // 原版workerのMTAとは異なり、既存の専用STAを共有します。
                    await ownedCom.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception error)
            {
                errors.Add(error);
            }

            try
            {
                ownedDlls?.Dispose();
            }
            catch (Exception error)
            {
                errors.Add(error);
            }

            try
            {
                if (ownedRelay is not null)
                {
                    await ((Task)ownedRelay).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    if (ownedRelay.IsCompletedSuccessfully)
                    {
                        ownedRelay.Result.Dispose();
                    }
                }
            }
            catch (Exception error)
            {
                errors.Add(error);
            }

            try
            {
                injectedTouch?.Dispose();
            }
            catch (Exception error)
            {
                errors.Add(error);
            }

            return errors;
        }
    }

    /// <summary>
    /// 注入backendの所有はrootに残し、workerは接点だけを解放します。
    /// </summary>
    private sealed class BorrowedTouchBackend(ITouchInputBackend backend)
        : ITouchInputBackend
    {
        public void ValidatePosition(Native.Point position)
            => backend.ValidatePosition(position);

        public void Send(IReadOnlyList<TouchContact> contacts)
            => backend.Send(contacts);

        public void Dispose()
        {
            // run共有資源の所有rootが最後に解放します。
        }
    }
}
