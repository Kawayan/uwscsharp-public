using System.Text;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

public sealed partial class WindowsAutomation
{
    private sealed partial class Session
    {
        private const int MouseClickHoldMilliseconds = 20;
        private const uint MouseMove = 0x0001;
        private const uint MouseMoveNoCoalesce = 0x2000;
        private const uint MouseVirtualDesk = 0x4000;
        private const uint MouseAbsolute = 0x8000;
        private const int MouseAbsoluteRange = 65_536;

        private Value InvokeMouseOrg(CallArguments args)
        {
            bool direct = args.Int(1) == 2;
            long target = WindowsOperationErrors.Calculate(() => checked((long)Math.Truncate(
                NumericOperations.Number(args.Values[0]))));
            nint window = target is >= int.MinValue and <= int.MaxValue
                ? Resolve((int)target, false) : 0;
            if (window == 0)
            {
                window = WindowsOperationErrors.Calculate(
                    () => checked((nint)target));
            }

            originWindow = window;
            hasMouseOrigin = target != 0;
            clientOrigin = args.Int(1) >= 1;
            directOrigin = hasMouseOrigin && direct;
            directRoot = directOrigin ? window : 0;
            backgroundCapture = hasMouseOrigin && args.Int(2) == 2;
            return Value.Empty;
        }

        private async ValueTask<Value> InvokeMmvAsync(
            CallArguments args, CancellationToken token)
        {
            await DelayArgumentAsync(args, 2, token).ConfigureAwait(false);
            if (directOrigin)
            {
                direct.Move(directRoot, new(args.Int(0), args.Int(1)), token);
                return Value.Empty;
            }

            using var dpi = new PhysicalDpiScope();
            Native.Point target = Position(args.Int(0),
                args.Int(1));
            if (buttons.Count == 0 || !TrySendDragMove(target))
            {
                Check(Native.SetCursorPos(target.X, target.Y));
            }

            return Value.Empty;
        }

        private bool TrySendDragMove(Native.Point target)
        {
            long x = (long)target.X - Native.GetSystemMetrics(76);
            long y = (long)target.Y - Native.GetSystemMetrics(77);
            int width = Native.GetSystemMetrics(78);
            int height = Native.GetSystemMetrics(79);
            if (x < 0 || x >= width || y < 0 || y >= height
                || width > MouseAbsoluteRange || height > MouseAbsoluteRange)
            {
                return false;
            }

            // ピクセルの左端を切り上げ、絶対入力の丸めで隣の座標へずれないようにします。
            Send(new Native.Input
            {
                Data = new Native.InputUnion
                {
                    Mouse = new Native.MouseInput
                    {
                        X = (int)((x * MouseAbsoluteRange + width - 1) / width),
                        Y = (int)((y * MouseAbsoluteRange + height - 1) / height),
                        Flags = MouseMove | MouseMoveNoCoalesce
                            | MouseVirtualDesk | MouseAbsolute,
                        Extra = 1
                    }
                }
            });
            return true;
        }

        private async ValueTask<Value> InvokeBtnAsync(
            CallArguments args, CancellationToken token)
        {
            await DelayArgumentAsync(args, 4, token).ConfigureAwait(false);
            int button = args.Int(0);
            if (button == MouseButtonCodes.Touch)
            {
                if (!directOrigin)
                {
                    Native.Point position;
                    using (var dpi = new PhysicalDpiScope())
                    {
                        if (args.Has(2))
                        {
                            position = Position(args.Int(2), args.Int(3));
                        }
                        else
                        {
                            Check(Native.GetCursorPos(out position));
                        }
                    }

                    await touch.InvokeAsync(args.Int(1), position, token,
                        DelayMillisecondsAsync).ConfigureAwait(false);
                }

                return Value.Empty;
            }

            if (directOrigin)
            {
                var position = new Native.Point(args.Int(2), args.Int(3));
                if (!args.Has(2) && Native.IsWindow(directRoot))
                {
                    // 直接送信と同じ物理座標系で現在位置を相対化します。
                    using var dpi = new PhysicalDpiScope();
                    Check(Native.GetPhysicalCursorPos(out position));
                    var origin = new Native.Point(0, 0);
                    Check(Native.ClientToScreen(directRoot, ref origin));
                    position = new Native.Point(checked(position.X - origin.X),
                        checked(position.Y - origin.Y));
                }

                await direct.MouseAsync(directRoot, args.Int(0), args.Int(0) >= 5 ? 0 : args.Int(1),
                    position, token, args.Int(0) >= 5 ? WheelDelta(args.Values, args.Has(1)) : null).ConfigureAwait(false);
                return Value.Empty;
            }

            if (args.Has(2))
            {
                using var dpi = new PhysicalDpiScope();
                Native.Point target = Position(args.Int(2), args.Int(3));
                Check(Native.SetCursorPos(target.X,
                    target.Y));
            }

            if (swapMouseButtons && button is MouseButtonCodes.Left or MouseButtonCodes.Right)
            {
                button = 1 - button;
            }

            int mode = button >= 5 ? 0 : args.Int(1);
            if (button >= 5)
            {
                Mouse(button, mode, WheelDelta(args.Values, args.Has(1)));
            }
            else
            {
                if (mode != 2)
                {
                    Mouse(button, 1);
                    buttons.Add(button);
                }

                if (mode == 0)
                {
                    await DelayMillisecondsAsync(MouseClickHoldMilliseconds,
                        token).ConfigureAwait(false);
                }

                if (mode != 1)
                {
                    Mouse(button, 2);
                    buttons.Remove(button);
                }
            }

            return Value.Empty;
        }

        private async ValueTask<Value> InvokeKbdAsync(
            CallArguments args, CancellationToken token)
        {
            await DelayArgumentAsync(args, 2, token).ConfigureAwait(false);
            if (directOrigin)
            {
                await direct.KeyAsync(directRoot, args.Int(0), args.Int(1),
                    token).ConfigureAwait(false);
                return Value.Empty;
            }

            int key = args.Int(0);
            int mode = args.Int(1);
            if (mode != 2)
            {
                await KeyAsync(key, false, token).ConfigureAwait(false);
            }

            if (mode != 1)
            {
                await KeyAsync(key, true).ConfigureAwait(false);
            }

            return Value.Empty;
        }

        private void Send(Native.Input input)
        {
            if (resources.TrySendOwnedInput(input))
            {
                return;
            }

            if (inputSender is not null)
            {
                inputSender(input);
                return;
            }

            NativeInputSender.Send(input);
        }

        private async Task KeyAsync(int raw, bool up, CancellationToken token = default)
        {
            var encoding = KeyboardEncoding.Create(
                raw, keyCode => Native.MapVirtualKeyW(keyCode, 0));
            uint scan = encoding.Scan;
            bool extended = encoding.Extended;
            int key = encoding.Key;
            bool unicode = key >= 256;
            var input = new Native.Input
            {
                Type = 1,
                Data = new Native.InputUnion
                {
                    Keyboard = new Native.KeyboardInput
                    {
                        Vk = unicode ? (ushort)0 : (ushort)key,
                        Scan = unicode ? (ushort)key : (ushort)scan,
                        Flags = (up ? 2u : 0)
                            | (unicode ? 4u : extended ? 1u : 0),
                        Extra = 1
                    }
                }
            };

            async Task SendAndTrackAsync()
            {
                token.ThrowIfCancellationRequested();
                Task? owned = resources.SendOwnedInputAsync(input);
                if (owned is null)
                {
                    Send(input);
                }
                else
                {
                    await owned.ConfigureAwait(false);
                }

                // 送信後のホットキー復元が失敗しても、UPを再試行できます。
                if (up)
                {
                    keys.Remove(raw);
                }
                else
                {
                    keys.Add(raw);
                }
            }

            if (!up && !unicode && asyncKeyboardInputGuard is not null)
            {
                await asyncKeyboardInputGuard(key, SendAndTrackAsync).ConfigureAwait(false);
            }
            else if (!up && !unicode && keyboardInputGuard is not null)
            {
                keyboardInputGuard(key, () => SendAndTrackAsync().GetAwaiter().GetResult());
            }
            else
            {
                await SendAndTrackAsync().ConfigureAwait(false);
            }
        }

        private static int WheelDelta(IReadOnlyList<Value> arguments, bool specified) =>
            specified ? WindowsOperationErrors.Calculate(() => checked((int)Math.Round(NumericOperations.Number(arguments[1]) * 120,
                MidpointRounding.AwayFromZero))) : 0;

        private void Mouse(int button, int mode, int? wheelDelta = null)
        {
            uint flags = button switch
            {
                0 => mode == 1 ? 2u : 4u,
                1 => mode == 1 ? 8u : 16u,
                2 => mode == 1 ? 32u : 64u,
                3 or 4 => mode == 1 ? 0x80u : 0x100u,
                6 => 0x1000u,
                _ => 0x800u
            };
            Send(
                new Native.Input
                {
                    Data = new Native.InputUnion
                    {
                        Mouse = new Native.MouseInput
                        {
                            Flags = flags,
                            Data = button is 3 or 4 ? (uint)(button - 2)
                                : button >= 5 ? unchecked((uint)(wheelDelta ?? mode * 120)) : 0,
                            Extra = 1
                        }
                    }
                });
        }
    }
}
