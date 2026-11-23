using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

/// <summary>
/// 通知の対象照会と次のフックへの転送を提供します。
/// </summary>
internal interface ISelectiveInputHookContext
{
    nint Foreground();

    nint ResolvePoint(int x, int y);

    nint Next(int code, nuint message, nint data);

    /// <summary>
    /// callback外で、監視開始時のキー状態だけを採取します。
    /// </summary>
    IReadOnlyList<int> PressedEmergencyKeys() => [];
}

/// <summary>
/// 所有スレッドの入力通知を解析し、内部の遮断判定へ渡します。
/// </summary>
internal sealed class SelectiveInputHookCallbacks
{
    private readonly SelectiveInputLockFilter filter;
    private readonly ISelectiveInputHookContext context;
    private readonly nint target;
    private readonly SelectiveInputDevices devices;
    private ExceptionDispatchInfo? failure;
    private bool suspended;
    private SelectiveInputEmergencyStop? emergency;
    private bool emergencyAttached;

    internal void ConfigureEmergency(SelectiveInputEmergencyStop value) => emergency = value;

    internal bool IsEmergencyAttached => emergencyAttached;

    internal void ResetEmergency()
    {
        emergency!.Registry.AttachMonitor(context.PressedEmergencyKeys());
        emergencyAttached = true;
    }

    internal void DetachEmergency()
    {
        if (emergencyAttached)
        {
            emergencyAttached = false;
            emergency!.Registry.DetachMonitor();
        }
    }

    internal bool IsConsumingEmergency => emergency?.Registry.Chord.IsConsuming == true;

    internal SelectiveInputHookCallbacks(nint target, SelectiveInputDevices devices,
        ISelectiveInputHookContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        filter = new SelectiveInputLockFilter(target, devices);
        this.context = context;
        this.target = target;
        this.devices = devices;
    }

    /// <summary>
    /// 保存した最初の例外を、ネイティブcallbackの外側で通知します。
    /// </summary>
    internal void ThrowIfFaulted() => failure?.Throw();

    /// <summary>
    /// 解除前に判定を停止し、以後の通知を次のフックへ渡します。
    /// </summary>
    internal void Suspend() => suspended = true;

    internal nint Keyboard(int code, nuint message, nint data)
    {
        if (code == 0 && (emergency is not null
            || (!suspended && failure is null && (devices & SelectiveInputDevices.Keyboard) != 0)))
        {
            try
            {
                KeyboardData key = Marshal.PtrToStructure<KeyboardData>(data);
                if (emergency is not null)
                {
                    (bool request, bool consume) = emergency.Registry.Chord.Apply(
                        key.Key, key.Scan, key.Flags, key.Extra, message);
                    if (request)
                    {
                        emergency.Broadcast();
                    }

                    if (consume)
                    {
                        return 1;
                    }
                }

                if (!suspended && failure is null && emergency?.IsRequested != true
                    && (devices & SelectiveInputDevices.Keyboard) != 0 && key.Extra != 1)
                {
                    nint foreground = target == 0 ? 0 : context.Foreground();
                    if (filter.ShouldBlockKeyboard(code, key.Extra, foreground))
                    {
                        return 1;
                    }
                }
            }
            catch (Exception error)
            {
                failure ??= ExceptionDispatchInfo.Capture(error);
            }
        }

        return Forward(code, message, data);
    }

    internal nint Mouse(int code, nuint message, nint data)
    {
        if (code == 0 && !suspended && failure is null && emergency?.IsRequested != true
            && (devices & SelectiveInputDevices.Mouse) != 0)
        {
            try
            {
                MouseData mouse = Marshal.PtrToStructure<MouseData>(data);
                if (mouse.Extra != 1)
                {
                    uint kind = (uint)message;
                    nint resolved = target == 0 || kind is 0x200 or 0xa0
                        ? 0 : context.ResolvePoint(mouse.Point.X, mouse.Point.Y);
                    if (filter.ShouldBlockMouse(code, kind, mouse.Extra, resolved))
                    {
                        return 1;
                    }
                }
            }
            catch (Exception error)
            {
                failure ??= ExceptionDispatchInfo.Capture(error);
            }
        }

        return Forward(code, message, data);
    }

    private nint Forward(int code, nuint message, nint data)
    {
        try
        {
            return context.Next(code, message, data);
        }
        catch (Exception error)
        {
            // 転送側の例外もネイティブ境界へ漏らさず、最初の失敗を保持します。
            failure ??= ExceptionDispatchInfo.Capture(error);
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardData
    {
        internal uint Key;
        internal uint Scan;
        internal uint Flags;
        internal uint Time;
        internal nuint Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseData
    {
        internal Native.Point Point;
        internal uint Data;
        internal uint Flags;
        internal uint Time;
        internal nuint Extra;
    }
}
