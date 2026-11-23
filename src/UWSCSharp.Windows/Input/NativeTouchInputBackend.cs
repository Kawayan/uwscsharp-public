using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UWSCSharp.Windows;

/// <summary>
/// 他の実行とデバイスを共有せず、タッチフレームを送信します。
/// </summary>
internal sealed partial class NativeTouchInputBackend : ITouchInputBackend
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct PointerInfo
    {
        internal uint Type;
        internal uint Id;
        internal uint Frame;
        internal uint Flags;
        internal nint Source;
        internal nint Target;
        internal Native.Point Pixel;
        internal Native.Point Himetric;
        internal Native.Point RawPixel;
        internal Native.Point RawHimetric;
        internal uint Time;
        internal uint History;
        internal int Data;
        internal uint Keys;
        internal ulong Counter;
        internal uint ButtonChange;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TouchInfo
    {
        internal PointerInfo Pointer;
        internal uint Flags;
        internal uint Mask;
        internal Native.Rect Contact;
        internal Native.Rect RawContact;
        internal uint Orientation;
        internal uint Pressure;
    }

    // unionの最大要素はTouchInfoで、先頭のtypeとの配置も維持します。
    [StructLayout(LayoutKind.Sequential)]
    internal struct PointerTypeInfo
    {
        internal uint Type;
        internal TouchInfo Touch;
    }

    private sealed class DeviceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal DeviceHandle(nint value) : base(true)
        {
            SetHandle(value);
        }

        protected override bool ReleaseHandle()
        {
            DestroySyntheticPointerDevice(handle);
            return true;
        }
    }

    private DeviceHandle? device;
    private bool disposed;

    /// <summary>
    /// 補間を始める前に、要求された終点も検査します。
    /// </summary>
    public void ValidatePosition(Native.Point position)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var dpi = new PhysicalDpiScope();
        ValidateContacts([new(1, TouchInputSession.Down, position)],
            new(Native.GetSystemMetrics(76), Native.GetSystemMetrics(77)),
            Native.GetSystemMetrics(78), Native.GetSystemMetrics(79));
    }

    /// <summary>
    /// 成功したフレームだけを呼出し元へ返します。
    /// </summary>
    public void Send(IReadOnlyList<TouchContact> contacts)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var dpi = new PhysicalDpiScope();
        var virtualOrigin = new Native.Point(
            Native.GetSystemMetrics(76), Native.GetSystemMetrics(77));
        int width = Native.GetSystemMetrics(78);
        int height = Native.GetSystemMetrics(79);
        ValidateContacts(contacts, virtualOrigin, width, height);
        if (device is null)
        {
            nint handle = CreateSyntheticPointerDevice(2, 2, 1);
            if (handle == 0)
            {
                throw Failure("タッチ入力デバイスを作成できません。");
            }

            device = new DeviceHandle(handle);
        }

        var frame = new PointerTypeInfo[contacts.Count];
        for (int index = 0; index < frame.Length; index++)
        {
            frame[index] = Encode(contacts[index], virtualOrigin);
        }

        if (!InjectSyntheticPointerInput(device, frame, (uint)frame.Length))
        {
            throw Failure("タッチ入力の送信に失敗しました。");
        }
    }

    internal static void ValidateContacts(IReadOnlyList<TouchContact> contacts,
        Native.Point origin, int width, int height)
    {
        if (contacts.Count is < 1 or > 2 || width <= 0 || height <= 0)
        {
            throw WindowsOperationErrors.Record(new ArgumentException(
                "タッチ接点数または仮想画面の範囲が無効です。"));
        }

        foreach (TouchContact contact in contacts)
        {
            long x = (long)contact.Position.X - origin.X;
            long y = (long)contact.Position.Y - origin.Y;
            if (x < 0 || x >= width || y < 0 || y >= height)
            {
                throw WindowsOperationErrors.Record(new ArgumentOutOfRangeException(
                    nameof(contacts), "タッチ座標が仮想画面の範囲外です。"));
            }
        }
    }

    internal static PointerTypeInfo Encode(TouchContact contact,
        Native.Point virtualOrigin = default)
    {
        Native.Point point = WindowsOperationErrors.Calculate(() => new Native.Point(
            checked(contact.Position.X - virtualOrigin.X),
            checked(contact.Position.Y - virtualOrigin.Y)));
        return WindowsOperationErrors.Calculate(() => new PointerTypeInfo
        {
            Type = 2,
            Touch = new TouchInfo
            {
                Pointer = new PointerInfo
                {
                    Type = 2,
                    Id = contact.Id,
                    Flags = contact.Flags,
                    Pixel = point
                },
                Mask = 7,
                Contact = new Native.Rect
                {
                    Left = checked(point.X - 2),
                    Top = checked(point.Y - 2),
                    Right = checked(point.X + 2),
                    Bottom = checked(point.Y + 2)
                },
                Orientation = 90,
                Pressure = 1024
            }
        });
    }

    private static Win32Exception Failure(string message)
    {
        int error = Marshal.GetLastWin32Error();
        return WindowsOperationErrors.Record(new Win32Exception(error,
            message + " OSエラー: " + error));
    }

    /// <summary>
    /// 保持接点の取消に失敗した場合も、所有デバイスを破棄します。
    /// </summary>
    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            device?.Dispose();
        }
    }
}
