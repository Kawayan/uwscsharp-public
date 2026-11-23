using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// 選択入力禁止の実行共有と終了後の再回収を提供します。
/// </summary>
internal interface ISelectiveInputOwnershipSession
{
    Task SetSelectiveInputAsync(SelectiveInputLockRequest request, CancellationToken token);

    Exception? TakeSelectiveInputFailure();

    Task RetrySelectiveInputCleanupAsync();
}

public sealed partial class WindowsAutomation
{
    private sealed partial class Session : ISelectiveInputOwnershipSession
    {
        private async ValueTask<Value> InvokeSelectiveInputAsync(
            IReadOnlyList<Value> arguments, CancellationToken token)
        {
            try
            {
                await SetSelectiveInputAsync(SelectiveInputLockRequest.Read(arguments), token)
                    .ConfigureAwait(false);
                return Value.Empty;
            }
            catch (SelectiveInputSupersededException error)
            {
                // 実際の実行取消を優先し、要求だけの失効を操作エラーにします。
                token.ThrowIfCancellationRequested();
                ExecutionStopToken.ThrowIfCancellationRequested();
                throw ScriptError(BuiltinFunctionNames.LockHardEx,
                    new InvalidOperationException(error.Message, error));
            }
            catch (Exception error) when (SelectiveInputOperationErrors.IsExpected(error))
            {
                throw ScriptError(BuiltinFunctionNames.LockHardEx, error);
            }
        }

        public CancellationToken ExecutionStopToken => resources.selectiveEmergency.Token;

        public Task SetSelectiveInputAsync(SelectiveInputLockRequest request, CancellationToken token)
        {
            lock (disposalGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return resources.SetSelectiveInputAsync(request, token);
            }
        }

        public Exception? TakeSelectiveInputFailure() => resources.TakeSelectiveInputFailure();

        public bool RequiresAutomationFailurePolling => true;

        public WindowsAutomationFailure? TakeAutomationFailure()
        {
            Exception? error = resources.TakeSelectiveInputFailure();
            if (error is not null)
            {
                return new(BuiltinFunctionNames.LockHardEx, error);
            }

            error = resources.TakeSpeechFailure();
            return error is null ? null : new(BuiltinFunctionNames.Speak, error);
        }

        public Task RetrySelectiveInputCleanupAsync()
        {
            if (!ownsResources)
            {
                throw new InvalidOperationException("共有資源の再回収は実行元だけが要求できます。");
            }

            return resources.RetrySelectiveInputCleanupAsync();
        }
    }

    private sealed partial class RunResources
    {
        private SelectiveInputRunOwner? selectiveInput;
        internal readonly SelectiveInputEmergencyStop selectiveEmergency = new(selectiveEmergencyRegistry);
        private Task? selectiveStop;
        private bool selectiveStopping;
        private readonly HashSet<CancellationToken> selectiveRootTokens = [];
        private readonly List<CancellationTokenRegistration> selectiveRegistrations = [];

        internal Task SetSelectiveInputAsync(SelectiveInputLockRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request.IsRelease && selectiveEmergency.IsRequested)
            {
                return RequestSelectiveStop();
            }

            if (!request.IsRelease)
            {
                selectiveEmergency.ThrowIfRequested();
            }
            SelectiveInputDevices devices = request.Mode switch
            {
                0 => SelectiveInputDevices.All,
                1 => SelectiveInputDevices.Keyboard,
                2 => SelectiveInputDevices.Mouse,
                _ => throw new InvalidOperationException("入力禁止モードが範囲外です。")
            };
            SelectiveInputTarget? target = request.IsRelease ? null
                : SelectiveInputTarget.CaptureRegistered(request.WindowId!.Value, windows);
            lock (gate)
            {
                if (!request.IsRelease)
                {
                    selectiveEmergency.ThrowIfRequested();
                }

                ObjectDisposedException.ThrowIf(closing || selectiveStopping, this);
                foreach (CancellationToken root in selectiveRootTokens)
                {
                    root.ThrowIfCancellationRequested();
                }

                selectiveInput ??= new SelectiveInputRunOwner(selectiveInputFactory
                    ?? (static (selectionTarget, selectionDevices) =>
                        new NativeSelectiveInputHookSession(selectionTarget, selectionDevices)),
                    selectiveInputClock, selectiveEmergency);
                return selectiveInput.ChangeAsync(target, devices, token);
            }
        }

        internal Exception? TakeSelectiveInputFailure()
        {
            lock (gate)
            {
                return SelectiveInputHookStop.Combine(selectiveInput?.TakeFailure(),
                    selectiveEmergency.TakeFailure());
            }
        }

        internal void ConfigureSelectiveCancellation(CancellationToken token)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(closing, this);
                if (token.CanBeCanceled && selectiveRootTokens.Add(token))
                {
                    selectiveRegistrations.Add(token.UnsafeRegister(
                        static state => ((RunResources)state!).RequestSelectiveStop(), this));
                }
            }
        }

        private Task RequestSelectiveStop()
        {
            lock (gate)
            {
                selectiveStopping = true;
                return selectiveStop ??= selectiveInput?.DisposeAsync().AsTask() ?? Task.CompletedTask;
            }
        }

        internal async Task StopSelectiveInputAsync(List<Exception> errors)
        {
            selectiveEmergency.MarkExecutionEnded();
            try
            {
                await RequestSelectiveStop().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
            finally
            {
                if (selectiveInput is not null)
                {
                    selectiveInputCleanup?.Retain(selectiveInput);
                    selectiveInput.RetainEmergencyTarget();
                }
            }
        }

        internal Task RetrySelectiveInputCleanupAsync()
        {
            lock (gate)
            {
                if (!selectiveStopping)
                {
                    throw new InvalidOperationException("実行終了前の再回収は明示解除を使用してください。");
                }

                return selectiveInput?.RetryCleanupAsync() ?? Task.CompletedTask;
            }
        }

        private async Task DisposeSelectiveRegistrationsAsync()
        {
            foreach (CancellationTokenRegistration registration in selectiveRegistrations)
            {
                await registration.DisposeAsync().ConfigureAwait(false);
            }

            selectiveRegistrations.Clear();
        }
    }
}
