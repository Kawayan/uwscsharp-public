using UWSCSharp.Core;

namespace UWSCSharp.Windows;

internal readonly record struct DirectTarget(
    nint Window, Native.Point Position);

internal interface IDirectInputBackend
{
    bool IsWindow(nint window);

    uint MapVirtualKey(uint key);

    short GetKeyState(int key);

    DirectTarget SelectMouse(nint root, Native.Point position);

    nint SelectKeyboard(nint root, Native.Point? lastClick);

    void Post(nint window, uint message, uint wParam,
        uint lParam);

    void WithModifiers(nint window, DirectKeyboardState state,
        bool includeAlt, Action send);
}

// 送信先と保持入力は通常入力から独立させ、原点変更後も元の対象へ解放します。
internal sealed class DirectInputSession(IDirectInputBackend backend)
{
    private static readonly TimeSpan InputDelay = TimeSpan.FromMilliseconds(20);
    private const int DirectClickHoldMilliseconds = 20;

    private readonly DirectKeyboardState modifiers = new();
    private readonly HashSet<(nint Window, int Key)> keys = [];
    private readonly Dictionary<(nint Window, int Button),
        Native.Point> buttons = [];
    private readonly bool[] mouseDown = new bool[5];
    private nint lastClickRoot;
    private Native.Point lastClick;
    private ScriptPauseControl? pauseControl;
    private CancellationToken runToken;
    private Func<bool>? canPause;

    internal void ConfigurePause(ScriptPauseControl? control,
        CancellationToken cancellationToken, Func<bool> canPause)
    {
        pauseControl = control;
        runToken = cancellationToken;
        this.canPause = canPause;
    }

    private Task DelayAsync(CancellationToken token)
    {
        TimeSpan duration = InputDelay;
        return pauseControl is not null && !runToken.IsCancellationRequested
            ? pauseControl.DelayAsync(duration, token, Task.Delay, canPause)
            : Task.Delay(duration, token);
    }

    internal bool HasHeldInput => keys.Count != 0 || buttons.Count != 0
        || modifiers.Shift != 0 || modifiers.Control != 0
        || modifiers.Alt != 0;

    internal bool IsKeyHeld(int raw) => keys.Any(entry => entry.Key == raw)
        || (raw != 0 && (modifiers.Shift == raw
            || modifiers.Control == raw || modifiers.Alt == raw));

    internal static uint Pack(int x, int y) =>
        unchecked((uint)(ushort)x | ((uint)(ushort)y << 16));

    private uint MouseState() =>
        (mouseDown[0] ? 1u : 0)
        | (mouseDown[1] ? 2u : 0)
        | (mouseDown[2] ? 16u : 0)
        | (mouseDown[3] ? 32u : 0)
        | (mouseDown[4] ? 64u : 0)
        | (modifiers.Shift != 0 ? 4u : 0)
        | (modifiers.Control != 0 ? 8u : 0);

    private async ValueTask WaitModifiersAsync(
        CancellationToken token)
    {
        while ((backend.GetKeyState(16) & 0xfff0) != 0
            || (backend.GetKeyState(17) & 0xfff0) != 0
            || (backend.GetKeyState(18) & 0xfff0) != 0)
        {
            await DelayAsync(token).ConfigureAwait(false);
        }

        token.ThrowIfCancellationRequested();
    }

    internal void Move(nint root, Native.Point position,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (backend.IsWindow(root))
        {
            // MMVはBTNの子選択を通さず、指定rootへ一度だけ送ります。
            backend.Post(root, 0x200, MouseState(),
                Pack(position.X, position.Y));
        }
    }

    internal async ValueTask KeyAsync(nint root, int raw,
        int mode, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!backend.IsWindow(root))
        {
            return;
        }

        var encoding = KeyboardEncoding.Create(
            raw, backend.MapVirtualKey);
        nint target = backend.SelectKeyboard(root,
            root == lastClickRoot ? lastClick : null);
        token.ThrowIfCancellationRequested();
        if (modifiers.ApplyModifier(raw, mode == 2))
        {
            if (mode == 0)
            {
                modifiers.ApplyModifier(raw, true);
            }

            return;
        }

        if (mode != 2)
        {
            await WaitModifiersAsync(token).ConfigureAwait(false);
            backend.WithModifiers(target, modifiers, true, () =>
            {
                backend.Post(target, encoding.DirectMessage(false),
                    (uint)encoding.Key, encoding.DirectParameter(false));
                // 後続の補助状態解除が失敗してもUPを試みられるよう記録します。
                keys.Add((target, raw));
            });
        }

        if (mode != 1)
        {
            token.ThrowIfCancellationRequested();
            backend.Post(target, encoding.DirectMessage(true),
                (uint)encoding.Key, encoding.DirectParameter(true));
            keys.Remove((target, raw));
        }
    }

    internal async ValueTask MouseAsync(nint root, int button,
        int mode, Native.Point position, CancellationToken token, int? wheelDelta = null)
    {
        token.ThrowIfCancellationRequested();
        if (!backend.IsWindow(root))
        {
            return;
        }

        if (button == 0 && mode != 2)
        {
            lastClickRoot = root;
            lastClick = position;
        }

        if (button >= 5)
        {
            await SendMouseAsync(root, button, mode, position,
                token, wheelDelta).ConfigureAwait(false);
            return;
        }

        if (mode != 2)
        {
            await SendMouseAsync(root, button, 1, position,
                token).ConfigureAwait(false);
        }

        if (mode == 0)
        {
            await Task.Delay(DirectClickHoldMilliseconds, token).ConfigureAwait(false);
        }

        if (mode != 1)
        {
            await SendMouseAsync(root, button, 2, position,
                token).ConfigureAwait(false);
        }
    }

    private async ValueTask SendMouseAsync(nint root, int button,
        int mode, Native.Point position, CancellationToken token, int? wheelDelta = null)
    {
        DirectTarget target = backend.SelectMouse(root, position);
        token.ThrowIfCancellationRequested();
        int x = target.Position.X;
        int y = target.Position.Y;
        uint state = MouseState();
        backend.Post(target.Window, 0x200, state,
            Pack(checked(x + 1), checked(y + 1)));
        backend.Post(target.Window, 0x200, state,
            Pack(Math.Max(checked(x - 1), 0),
                Math.Max(checked(y - 1), 0)));
        backend.Post(target.Window, 0x200, state, Pack(x, y));
        await WaitModifiersAsync(token).ConfigureAwait(false);
        uint message = MouseMessage(button, mode);
        uint parameter = button >= 5
            ? state | ((uint)(ushort)(wheelDelta ?? checked(mode * 120)) << 16)
            : button is 3 or 4 ? XParameter(button, mode, state) : state;
        backend.WithModifiers(root, modifiers, false, () =>
        {
            backend.Post(target.Window, message, parameter, Pack(x, y));
            if (button < 5)
            {
                if (mode == 1)
                {
                    buttons[(target.Window, button)] = target.Position;
                }
                else
                {
                    buttons.Remove((target.Window, button));
                }

                // 本イベント送信後にだけ、次のmessage用状態を更新します。
                mouseDown[button] = mode == 1;
            }
        });
    }

    private static uint MouseMessage(int button, int mode) =>
        button >= 5 ? button == 5 ? 0x20au : 0x20eu
            : button is 3 or 4 ? mode == 1 ? 0x20bu : 0x20cu
            : (uint)(0x201 + button * 3 + (mode == 2 ? 1 : 0));

    private static uint XParameter(int button, int mode, uint state)
    {
        uint bit = button == 3 ? 32u : 64u;
        return (mode == 1 ? state | bit : state & ~bit) | ((uint)(button - 2) << 16);
    }

    internal void Release(List<Exception> errors)
    {
        foreach ((nint window, int raw) in keys)
        {
            try
            {
                if (backend.IsWindow(window))
                {
                    var encoding = KeyboardEncoding.Create(
                        raw, backend.MapVirtualKey);
                    backend.Post(window, 0x101, (uint)encoding.Key,
                        encoding.DirectParameter(true));
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }

        foreach (KeyValuePair<(nint Window, int Button),
            Native.Point> held in buttons)
        {
            try
            {
                if (backend.IsWindow(held.Key.Window))
                {
                    backend.Post(held.Key.Window,
                        MouseMessage(held.Key.Button, 2),
                        held.Key.Button is 3 or 4 ? XParameter(held.Key.Button, 2, MouseState()) : MouseState(),
                        Pack(held.Value.X, held.Value.Y));
                    mouseDown[held.Key.Button] = false;
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }

        keys.Clear();
        buttons.Clear();
        Array.Clear(mouseDown);
        modifiers.ApplyModifier(16, true);
        modifiers.ApplyModifier(17, true);
        modifiers.ApplyModifier(18, true);
    }
}
