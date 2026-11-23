using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

// 入力フックより後に届く移動通知も、同じ記録スレッドで収集します。
internal sealed partial class WindowMovementMonitor : IDisposable
{
    private readonly RecordingBuffer buffer;
    private readonly Func<nint, bool> excluded;
    private readonly Action<Exception> failure;
    private readonly Func<nint> foreground;
    private readonly Func<nint, RecordedWindow?> capture;
    private readonly WinEventCallback callback;
    private nint movementHook;
    private nint locationHook;
    private nint stateHook;
    private nint movingWindow;
    private nint lastMovedWindow;
    private RecordedWindow? lastSnapshot;

    internal WindowMovementMonitor(RecordingBuffer buffer,
        Func<nint, bool> excluded, Action<Exception> failure,
        Func<nint>? foreground = null,
        Func<nint, RecordedWindow?>? capture = null)
    {
        this.buffer = buffer;
        this.excluded = excluded;
        this.failure = failure;
        this.foreground = foreground ?? Native.GetForegroundWindow;
        this.capture = capture ?? RecordedWindow.Capture;
        callback = OnEvent;
    }

    internal void Start()
    {
        movementHook = SetWinEventHook(0x000a, 0x000b, 0, callback, 0, 0, 2);
        if (movementHook == 0)
        {
            throw new Win32Exception("ウインドウ移動の監視を開始できません。");
        }

        locationHook = SetWinEventHook(0x800b, 0x800b, 0, callback, 0, 0, 2);
        if (locationHook == 0)
        {
            throw new Win32Exception("ウインドウ位置の監視を開始できません。");
        }

        if (buffer.BackgroundPlayback || buffer.RecordButtonClicks)
        {
            stateHook = SetWinEventHook(0x800a, 0x800a, 0, callback, 0, 0, 0);
            if (stateHook == 0)
            {
                throw new Win32Exception("チェック状態の監視を開始できません。");
            }
        }
    }

    private void OnEvent(nint hook, uint eventId, nint window,
        int objectId, int childId, uint thread, uint time)
    {
        try
        {
            if (eventId == 0x800a)
            {
                if (objectId == -4 && childId == 0)
                {
                    buffer.ObserveCheckbox(window, time);
                }

                return;
            }

            ProcessEvent(eventId, window, objectId, childId, Environment.TickCount64);
        }
        catch (Exception error)
        {
            failure(error);
        }
    }

    internal void ProcessEvent(uint eventId, nint window,
        int objectId, int childId, long time)
    {
        if (window == 0 || objectId != 0 || childId != 0 || excluded(window))
        {
            return;
        }

        if (eventId == 0x000a)
        {
            if (window != foreground() || buffer.WaitingForInitialRelease)
            {
                return;
            }

            movingWindow = window;
            lastMovedWindow = window;
            lastSnapshot = null;
        }
        else if (eventId == 0x000b)
        {
            if (window != movingWindow)
            {
                return;
            }
        }
        else if (eventId != 0x800b
            || window != movingWindow && (window != lastMovedWindow || window != foreground()))
        {
            return;
        }

        RecordedWindow? snapshot = capture(window);
        if (snapshot is null && eventId == 0x000a)
        {
            movingWindow = 0;
            lastMovedWindow = 0;
            return;
        }
        if (snapshot is null && eventId == 0x000b)
        {
            snapshot = lastSnapshot;
        }
        if (snapshot is not null)
        {
            int phase = eventId == 0x000a ? 1 : eventId == 0x000b ? 2 : 0;
            if (phase != 0 || snapshot != lastSnapshot)
            {
                buffer.WindowMovement(snapshot, phase, time);
                lastSnapshot = snapshot;
            }
        }
        if (eventId == 0x000b)
        {
            movingWindow = 0;
        }
    }

    /// <summary>
    /// 記録スレッドで登録した移動通知のフックを解除します。
    /// </summary>
    public void Dispose()
    {
        bool released = true;
        if (movementHook != 0)
        {
            released &= UnhookWinEvent(movementHook);
            movementHook = 0;
        }
        if (locationHook != 0)
        {
            released &= UnhookWinEvent(locationHook);
            locationHook = 0;
        }
        if (stateHook != 0)
        {
            released &= UnhookWinEvent(stateHook);
            stateHook = 0;
        }
        GC.KeepAlive(callback);
        if (!released)
        {
            throw new Win32Exception("ウインドウ移動の監視を解除できません。");
        }
    }

    private delegate void WinEventCallback(nint hook, uint eventId,
        nint window, int objectId, int childId, uint thread, uint time);
}
