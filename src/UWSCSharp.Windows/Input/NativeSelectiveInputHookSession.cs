using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

/// <summary>
/// 所有スレッド上で入力フックの開始・通知処理・待機・解除を行います。
/// </summary>
internal sealed class NativeSelectiveInputHookSession : ISelectiveInputHookSession
{
    private readonly Thread owner = Thread.CurrentThread;
    private readonly SelectiveInputHookCallbacks callbacks;
    private readonly SelectiveInputHookRegistration registration;
    private readonly SelectiveInputDevices devices;
    private readonly NativeSelectiveInputHookContext? nativeContext;
    private readonly SelectiveInputTarget? targetSnapshot;
    private bool started;
    private bool stopping;
    private int stopAttempts = 2;
    private bool monitorsEmergency;
    private TimeProvider stopClock = TimeProvider.System;
    private long? stopStartedAt;

    public void PrepareStop(TimeProvider clock, long startedAt)
    {
        CheckAccess();
        stopClock = clock;
        stopStartedAt = startedAt;
    }

    public void ConfigureEmergency(SelectiveInputEmergencyStop emergency)
    {
        CheckAccess();
        if (started || stopping)
        {
            throw new InvalidOperationException("緊急監視は開始前に設定してください。");
        }

        callbacks.ConfigureEmergency(emergency);
        monitorsEmergency = true;
    }

    internal NativeSelectiveInputHookSession(nint target, SelectiveInputDevices devices,
        ISelectiveInputHookBackend? backend = null, ISelectiveInputHookContext? context = null)
        : this(target, devices, backend, context, null)
    {
    }

    internal NativeSelectiveInputHookSession(SelectiveInputTarget target,
        SelectiveInputDevices devices, ISelectiveInputHookBackend? backend = null,
        ISelectiveInputHookContext? context = null)
        : this((target ?? throw new ArgumentNullException(nameof(target))).Handle,
            devices, backend, context, target)
    {
    }

    private NativeSelectiveInputHookSession(nint target, SelectiveInputDevices devices,
        ISelectiveInputHookBackend? backend, ISelectiveInputHookContext? context,
        SelectiveInputTarget? targetSnapshot)
    {
        this.devices = devices;
        this.targetSnapshot = targetSnapshot;
        targetSnapshot?.Validate();
        Native.PeekMessageW(out _, 0, 0, 0, 0);
        context ??= new NativeSelectiveInputHookContext(NativeSelectiveInputWindowTree.Capture());
        nativeContext = context as NativeSelectiveInputHookContext;
        callbacks = new SelectiveInputHookCallbacks(target, devices, context);
        registration = new SelectiveInputHookRegistration(backend ?? new NativeSelectiveInputHookBackend(),
            callbacks.Keyboard, callbacks.Mouse);
    }

    /// <summary>
    /// 回収が必要な登録を保持しているかを返します。
    /// </summary>
    public bool IsRegistered => registration.IsCallbackRooted;

    /// <summary>
    /// 所有スレッド上で入力フックを登録します。
    /// </summary>
    public void Start()
    {
        CheckAccess();
        if (started || stopping)
        {
            throw new InvalidOperationException("入力フックのセッションは再開始できません。");
        }

        targetSnapshot?.Validate();
        started = true;
        try
        {
            registration.Register(monitorsEmergency
                ? devices | SelectiveInputDevices.Keyboard : devices,
                monitorsEmergency ? callbacks.ResetEmergency : null);
        }
        catch
        {
            // 登録失敗時だけ、Registerが済ませた初回回収を差し引きます。
            Suspend();
            stopAttempts = 1;
            MaintainEmergencyMonitor();
            throw;
        }
    }

    /// <summary>
    /// キューから最大64件取り出します。上限到達時は待機せず、要求の確認後に再度処理します。
    /// </summary>
    public bool Pump()
    {
        CheckAccess();
        MaintainEmergencyMonitor();
        for (int count = 0; count < 64; count++)
        {
            CheckCallbackFailure();
            bool received = Native.PeekMessageW(out Native.Message message, 0, 0, 0, 1);
            CheckCallbackFailure();
            if (!received)
            {
                return false;
            }

            if (message.Id == 0x12)
            {
                throw new OperationCanceledException("入力フックの所有スレッドへ終了が通知されました。");
            }

            Native.TranslateMessage(ref message);
            Native.DispatchMessageW(ref message);
            CheckCallbackFailure();
        }

        return true;
    }

    /// <summary>
    /// 要求イベント・入力通知・更新期限を待ちます。イベントは終了まで保持します。
    /// </summary>
    public void Wait(AutoResetEvent work, int milliseconds)
    {
        CheckAccess();
        ArgumentNullException.ThrowIfNull(work);
        ArgumentOutOfRangeException.ThrowIfLessThan(milliseconds, Timeout.Infinite);
        if (monitorsEmergency && registration.HasKeyboard && !callbacks.IsEmergencyAttached)
        {
            milliseconds = milliseconds < 0 ? 100 : Math.Min(milliseconds, 100);
        }
        nint[] handles = [work.SafeWaitHandle.DangerousGetHandle()];
        uint result = Native.MsgWaitForMultipleObjectsEx(1, handles,
            unchecked((uint)milliseconds), 0x04ff, 0x0004);
        GC.KeepAlive(work);
        if (result == uint.MaxValue)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    /// <summary>
    /// 判定を止めて解除します。解除失敗時も通知を処理しながら再試行できます。
    /// </summary>
    public void Stop()
    {
        Suspend();
        if (registration.HasKeyboard)
        {
            DrainEmergencyRelease();
        }
        try
        {
            registration.Unregister(monitorsEmergency);
        }
        finally
        {
            MaintainEmergencyMonitor();
        }
    }

    private void MaintainEmergencyMonitor()
    {
        if (!registration.HasKeyboard)
        {
            callbacks.DetachEmergency();
        }
        else if (monitorsEmergency && !callbacks.IsEmergencyAttached)
        {
            try
            {
                callbacks.ResetEmergency();
            }
            catch
            {
                // 初回の失敗は保持し、残った監視の採取だけを通知処理の外で再試行します。
            }
        }
    }

    private void DrainEmergencyRelease()
    {
        long startedAt = stopStartedAt ?? stopClock.GetTimestamp();
        while (callbacks.IsConsumingEmergency)
        {
            // 解放が既にキューにある場合は、期限の判定より先に消費します。
            bool pending = Pump();
            if (!callbacks.IsConsumingEmergency)
            {
                return;
            }

            TimeSpan remaining = SelectiveInputHookStop.Timeout
                - stopClock.GetElapsedTime(startedAt);
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException("緊急解除のF11解放待ちが停止予算を超えました。");
            }

            if (!pending)
            {
                uint milliseconds = (uint)Math.Clamp(
                    Math.Ceiling(remaining.TotalMilliseconds), 1, 10);
                uint result = Native.MsgWaitForMultipleObjectsEx(0, [], milliseconds, 0x04ff, 0x0004);
                if (result == uint.MaxValue)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
            }
        }
    }

    public void Suspend()
    {
        CheckAccess();
        stopping = true;
        callbacks.Suspend();
    }

    public int TakeStopAttempts()
    {
        CheckAccess();
        int remaining = stopAttempts;
        stopAttempts = 2;
        return remaining;
    }

    /// <summary>
    /// フック外で可視一覧を更新します。呼出し時期は所有側が管理します。
    /// </summary>
    public void RefreshTargets()
    {
        CheckAccess();
        if (nativeContext is null)
        {
            throw new InvalidOperationException("この入力コンテキストは可視一覧を更新できません。");
        }

        targetSnapshot?.Validate();
        nativeContext.RefreshTargets();
    }

    private void CheckCallbackFailure()
    {
        if (!stopping)
        {
            callbacks.ThrowIfFaulted();
        }
    }

    private void CheckAccess()
    {
        if (Thread.CurrentThread != owner)
        {
            throw new InvalidOperationException("入力フックのセッションは所有スレッドから操作してください。");
        }
    }
}
