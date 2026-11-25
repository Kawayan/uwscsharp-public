using UWSCSharp.Core;

namespace UWSCSharp.Windows;

// フックと切り離して、開始境界・押下状態・制御キーの除外を管理します。
internal sealed class RecordingBuffer(
    IEnumerable<int> controlKeys, uint controlModifiers, int limit = 50_000,
    IEnumerable<ScriptHotkey>? additionalControls = null)
{
    private readonly HashSet<ScriptHotkey> controls = new(
        controlKeys.Select(key => new ScriptHotkey(key, (int)controlModifiers))
            .Concat(additionalControls ?? []));
    private readonly HashSet<int> initialKeys = [];
    private readonly HashSet<int> initialButtons = [];
    private readonly HashSet<int> physicalKeys = [];
    private readonly HashSet<int> recordedKeys = [];
    private readonly HashSet<int> recordedButtons = [];
    private readonly HashSet<int> ignoredKeys = [];
    private readonly HashSet<int> ignoredButtons = [];
    private readonly Dictionary<int, BasicRecorder.InputEvent> pendingModifiers = [];
    private readonly HashSet<int> controlOnlyModifiers = [];
    private readonly List<BasicRecorder.InputEvent> events = [];
    private readonly CheckboxRecordingCandidate checkbox = new();
    private int? checkboxBefore;
    private int checkboxIndex;
    private long nextSequence;
    private long mouseGestureStart;
    private bool windowMovementGesture;
    private bool windowMoving;
    private volatile bool waitingForRelease;

    internal bool WaitingForInitialRelease => waitingForRelease;

    internal bool BackgroundPlayback
    {
        get; set;
    }

    internal bool RecordButtonClicks
    {
        get; set;
    }

    internal void ObserveCheckbox(nint target, uint eventTime)
    {
        if (!checkbox.CanObserve(target, eventTime))
        {
            return;
        }

        int? state = CheckboxRecordingCandidate.ReadState(target);
        if (checkbox.Observe(target, state) is long sequence
            && checkboxIndex < events.Count
            && events[checkboxIndex].Sequence == sequence
            && RecordedButtonClick.Capture(target, checkbox: true) is { } click)
        {
            int index = checkboxIndex;
            BasicRecorder.InputEvent release = events[index];
            if (!BackgroundPlayback)
            {
                events.RemoveAt(index);
                index--;
            }

            events[index] = events[index] with
            {
                Time = release.Time,
                Sequence = release.Sequence,
                ButtonClick = click with
                {
                    State = state.GetValueOrDefault()
                }
            };
        }
    }

    internal bool OmitIdleTimeAndMouseMoves
    {
        get; set;
    }

    internal IReadOnlyList<BasicRecorder.InputEvent> Events => events;

    internal void WindowMovement(RecordedWindow window, int phase, long time)
    {
        if (waitingForRelease)
        {
            return;
        }
        if (events.Count >= limit)
        {
            Overflowed = true;
            return;
        }

        if (phase == 1)
        {
            windowMoving = true;
            windowMovementGesture = true;
            // 移動開始通知は押下フックより後に来るため、同じ操作の入力を補正します。
            if (recordedButtons.Count != 0)
            {
                for (int i = events.Count - 1; i >= 0
                    && events[i].Sequence >= mouseGestureStart; i--)
                {
                    if (events[i].Kind is 1 or 2)
                    {
                        events[i] = events[i] with
                        {
                            WindowMovement = true
                        };
                    }
                }
            }
        }

        Add(new(5, 0, phase, 0, 0, time, ++nextSequence, window, true));
        if (phase == 2)
        {
            windowMoving = false;
            if (recordedButtons.Count == 0)
            {
                windowMovementGesture = false;
            }
        }
    }

    internal bool Overflowed
    {
        get; private set;
    }

    internal void Complete()
    {
        foreach (BasicRecorder.InputEvent pending in pendingModifiers.Values.OrderBy(input => input.Sequence))
        {
            if (!controlOnlyModifiers.Contains(pending.Code))
            {
                Add(pending);
            }
        }

        pendingModifiers.Clear();
        controlOnlyModifiers.Clear();
    }

    internal void Begin(IEnumerable<int> keys, IEnumerable<int> buttons)
    {
        int[] held = [.. keys.Where(key => !LatchedImeKey(key))];
        initialKeys.UnionWith(held);
        initialButtons.UnionWith(buttons);
        physicalKeys.UnionWith(held);
        UpdateWaiting();
    }

    // IMEの切替用仮想キーはUPが届かず押下状態のまま残ることがあるため、解放待ちに含めません。
    internal static bool LatchedImeKey(int key) => key is
        (>= 0x15 and <= 0x1A) or 0xE5 or (>= 0xF0 and <= 0xF6);

    internal void Recover(long time, IEnumerable<int> keys, IEnumerable<int> buttons)
    {
        foreach (BasicRecorder.InputEvent pending in pendingModifiers.Values)
        {
            if (!controlOnlyModifiers.Contains(pending.Code))
            {
                recordedKeys.Add(pending.Code);
            }
        }
        Complete();
        foreach (int key in recordedKeys.Order())
        {
            Add(new BasicRecorder.InputEvent(0, key, 2, 0, 0, time, ++nextSequence));
        }
        foreach (int button in recordedButtons.Order())
        {
            Add(new BasicRecorder.InputEvent(3, button, 2, 0, 0, time, ++nextSequence));
        }
        // 欠落区間の押下状態は推測せず、復旧時に保持中の入力の解放を待ちます。
        recordedKeys.Clear();
        recordedButtons.Clear();
        windowMovementGesture = false;
        windowMoving = false;
        initialKeys.Clear();
        initialButtons.Clear();
        physicalKeys.Clear();
        ignoredKeys.Clear();
        ignoredButtons.Clear();
        Begin(keys, buttons);
    }

    internal void ReleaseInitialKey(int key)
    {
        if (initialKeys.Remove(key))
        {
            physicalKeys.Remove(key);
            ignoredKeys.Remove(key);
            UpdateWaiting();
        }
    }

    internal void ReleaseInitialButton(int button)
    {
        if (initialButtons.Remove(button))
        {
            ignoredButtons.Remove(button);
            UpdateWaiting();
        }
    }

    internal void Key(int key, int state, long time, bool excluded,
        RecordedWindow? window = null)
    {
        checkbox.Discard();
        long sequence = ++nextSequence;
        if (state == 1)
        {
            physicalKeys.Add(key);
        }
        else
        {
            physicalKeys.Remove(key);
        }

        bool initial = initialKeys.Count != 0 || initialButtons.Count != 0;
        if (state == 2)
        {
            initialKeys.Remove(key);
        }

        if (initial)
        {
            if (state == 1 && !LatchedImeKey(key))
            {
                initialKeys.Add(key);
                ignoredKeys.Add(key);
            }
            else
            {
                ignoredKeys.Remove(key);
            }

            UpdateWaiting();
            return;
        }

        if (state == 2)
        {
            ignoredKeys.Remove(key);
            if (pendingModifiers.ContainsKey(key))
            {
                if (controlOnlyModifiers.Remove(key))
                {
                    pendingModifiers.Remove(key);
                }
                else
                {
                    FlushModifiers();
                    recordedKeys.Remove(key);
                    Add(new(0, key, 2, 0, 0, time, sequence));
                }
            }
            else if (recordedKeys.Remove(key))
            {
                // 外で押したキーは自画面へ移ってもUPを保持します。
                Add(new(0, key, 2, 0, 0, time, sequence));
            }

            return;
        }

        if (excluded || ignoredKeys.Contains(key))
        {
            ignoredKeys.Add(key);
            return;
        }

        if (controls.Contains(new ScriptHotkey(key, (int)Modifiers())))
        {
            ignoredKeys.Add(key);
            controlOnlyModifiers.UnionWith(pendingModifiers.Keys);
            return;
        }

        if (Modifier(key) != 0 && !recordedKeys.Contains(key))
        {
            pendingModifiers.TryAdd(key, new(0, key, 1, 0, 0, time, sequence, window));
            return;
        }

        FlushModifiers();
        recordedKeys.Add(key);
        Add(new(0, key, 1, 0, 0, time, sequence, window));
    }

    internal void Mouse(int button, int state, int x, int y,
        long time, bool excluded,
        RecordedWindow? window = null, nint buttonTarget = 0, uint? inputTime = null)
    {
        if (button >= 0 && state == 1)
        {
            checkbox.Discard();
            checkboxBefore = null;
        }

        long sequence = ++nextSequence;
        bool initial = initialKeys.Count != 0 || initialButtons.Count != 0;
        if (button >= 0 && state == 2)
        {
            initialButtons.Remove(button);
        }

        if (initial)
        {
            if (button >= 0 && state == 1)
            {
                initialButtons.Add(button);
                ignoredButtons.Add(button);
            }
            else if (button >= 0)
            {
                ignoredButtons.Remove(button);
            }

            UpdateWaiting();
            return;
        }

        if (button < 0)
        {
            if (!excluded && (!OmitIdleTimeAndMouseMoves
                || !windowMovementGesture && recordedButtons.Count != 0))
            {
                Add(new(1, 0, 1, x, y, time, sequence, window, windowMovementGesture));
            }

            return;
        }

        if (state == 2)
        {
            ignoredButtons.Remove(button);
            if (recordedButtons.Remove(button))
            {
                RecordedButtonClick? click = null;
                RecordedMenuClick? menuClick = null;
                if (!excluded && buttonTarget != 0 && button == 0
                    && !windowMovementGesture && recordedKeys.Count == 0
                    && recordedButtons.Count == 0 && events.Count > 0
                    && events[^1] is
                    {
                        Kind: 2, Code: 0, State: 1,
                        WindowMovement: false
                    } down
                    && down.ButtonTarget == buttonTarget
                    && down.X == x && down.Y == y
                    && (checkboxBefore is not null
                        || (down.MenuCandidate is { } menu
                            && CaptureMenuClick(buttonTarget, x, y) == menu
                            && (menuClick = menu) is not null)
                        || (click = RecordedButtonClick.Capture(buttonTarget)) is not null))
                {
                    if (!BackgroundPlayback && checkboxBefore is int highLevelBefore)
                    {
                        int count = events.Count;
                        Add(new(2, button, 2, x, y, time, sequence, window));
                        if (events.Count != count)
                        {
                            checkboxIndex = count;
                            checkbox.AwaitState(buttonTarget, highLevelBefore, sequence,
                                inputTime ?? unchecked((uint)time));
                        }
                    }
                    else
                    {
                        events[^1] = down with
                        {
                            Time = time,
                            Sequence = sequence,
                            ButtonClick = click,
                            MenuClick = menuClick
                        };
                        if (checkboxBefore is int before)
                        {
                            checkboxIndex = events.Count - 1;
                            checkbox.AwaitState(buttonTarget, before, sequence,
                                inputTime ?? unchecked((uint)time));
                        }
                    }
                }
                else
                {
                    Add(new(2, button, 2, x, y, time, sequence, window,
                        windowMovementGesture));
                }
                if (recordedButtons.Count == 0 && !windowMoving)
                {
                    windowMovementGesture = false;
                }
            }

            return;
        }

        if (excluded || ignoredButtons.Contains(button))
        {
            ignoredButtons.Add(button);
            return;
        }

        FlushModifiers();
        if (recordedButtons.Count == 0)
        {
            mouseGestureStart = sequence;
        }
        recordedButtons.Add(button);
        if ((BackgroundPlayback || RecordButtonClicks) && button == 0 && buttonTarget != 0
            && recordedButtons.Count == 1 && recordedKeys.Count == 0
            && NativeSelectionTarget.Label(buttonTarget) == ControlClassNames.Uppercase.Button
            && (Native.GetWindowLongW(buttonTarget, Native.GwlStyle) & 15) == 3)
        {
            checkboxBefore = CheckboxRecordingCandidate.ReadState(buttonTarget);
        }
        Add(new(2, button, 1, x, y, time, sequence, window,
            windowMovementGesture, ButtonTarget: recordedKeys.Count == 0
                && recordedButtons.Count == 1 ? buttonTarget : 0,
            MenuCandidate: button == 0 && recordedKeys.Count == 0
                && recordedButtons.Count == 1
                ? CaptureMenuClick(buttonTarget, x, y) : null));
    }

    private RecordedMenuClick? CaptureMenuClick(nint target, int x, int y)
    {
        if (target == 0 || !RecordButtonClicks || BackgroundPlayback)
        {
            return null;
        }

        try
        {
            return RecordedMenuClick.CaptureAtPoint(target, new Native.Point(x, y));
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception
            or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    internal void Wheel(int button, int delta, int x, int y, long time, bool excluded,
        RecordedWindow? window = null)
    {
        if (delta != 0)
        {
            checkbox.Discard();
            checkboxBefore = null;
        }

        long sequence = ++nextSequence;
        if (waitingForRelease || excluded || delta == 0)
        {
            return;
        }

        FlushModifiers();
        Add(new(4, button, delta, x, y, time, sequence, window));
    }

    private uint Modifiers()
    {
        uint result = 0;
        foreach (int key in physicalKeys)
        {
            result |= Modifier(key);
        }
        return result;
    }

    private void UpdateWaiting() => waitingForRelease =
        initialKeys.Count != 0 || initialButtons.Count != 0;

    private static uint Modifier(int key) => key switch
    {
        16 or 160 or 161 => 4,
        17 or 162 or 163 => 2,
        18 or 164 or 165 => 1,
        91 or 92 => 8,
        _ => 0
    };

    private void FlushModifiers()
    {
        if (pendingModifiers.Count == 0)
        {
            controlOnlyModifiers.Clear();
            return;
        }

        if (events.Count >= limit)
        {
            // 上限後も押下状態は更新しますが、保存しない入力を並べ替えません。
            foreach (int key in pendingModifiers.Keys)
            {
                recordedKeys.Add(key);
            }
            Overflowed = true;
            pendingModifiers.Clear();
            controlOnlyModifiers.Clear();
            return;
        }

        foreach (BasicRecorder.InputEvent pending in pendingModifiers.Values.OrderBy(input => input.Sequence))
        {
            Add(pending);
            recordedKeys.Add(pending.Code);
        }

        pendingModifiers.Clear();
        controlOnlyModifiers.Clear();
    }

    private void Add(BasicRecorder.InputEvent input)
    {
        if (events.Count >= limit)
        {
            Overflowed = true;
            return;
        }

        events.Add(input);
    }
}
