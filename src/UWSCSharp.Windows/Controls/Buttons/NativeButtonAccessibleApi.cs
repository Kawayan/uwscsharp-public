using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UWSCSharp.Windows;

/// <summary>
/// BTN補助経路でVARIANTの型と生の値を保持します。
/// </summary>
[StructLayout(LayoutKind.Explicit)]
internal struct NativeButtonVariant
{
    [FieldOffset(0)]
    internal ushort Type;

    [FieldOffset(8)]
    internal int Integer;

    [FieldOffset(8)]
    internal nint Pointer;

    // BRECORDの二つのポインターによりx86では16、x64では24バイトになります。
    [FieldOffset(8)]
    internal RecordPointers Record;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RecordPointers
    {
        internal nint Value;
        internal nint Information;
    }

    internal static NativeButtonVariant Child(int value)
        => new()
        {
            Type = 3,
            Integer = value
        };
}

/// <summary>
/// HRESULTを変換せず、BTN補助経路のCOM呼出しを行います。
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class NativeButtonAccessibleApi
{
    internal static readonly Guid AccessibleId =
        new("618736e0-3c3d-11cf-810c-00aa00389b71");
    internal static readonly Guid EnumeratorId =
        new("00020404-0000-0000-C000-000000000046");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int QueryCall(nint self, ref Guid id, out nint result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int CountCall(nint self, out int count);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int ChildCall(nint self, NativeButtonVariant child,
        out nint result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int ValueCall(nint self, NativeButtonVariant child,
        ref NativeButtonVariant result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int LocationCall(nint self, out int left, out int top,
        out int width, out int height, NativeButtonVariant child);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int SelectCall(nint self, int flags,
        NativeButtonVariant child);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int ActionCall(nint self, NativeButtonVariant child);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int NextCall(nint self, uint count,
        ref NativeButtonVariant result, out uint fetched);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int ResetCall(nint self);

    internal static int Query(nint self, Guid id, out nint result)
        => Method<QueryCall>(self, 0)(self, ref id, out result);

    internal static int ChildCount(nint self, out int count)
        => Method<CountCall>(self, 8)(self, out count);

    internal static int Child(nint self, NativeButtonVariant child,
        out nint result)
        => Method<ChildCall>(self, 9)(self, child, out result);

    internal static int Role(nint self, NativeButtonVariant child,
        ref NativeButtonVariant result)
        => Method<ValueCall>(self, 13)(self, child, ref result);

    internal static int State(nint self, NativeButtonVariant child,
        ref NativeButtonVariant result)
        => Method<ValueCall>(self, 14)(self, child, ref result);

    internal static int Location(nint self, NativeButtonVariant child,
        out int left, out int top, out int width, out int height)
        => Method<LocationCall>(self, 22)(self, out left, out top,
            out width, out height, child);

    internal static int Select(nint self, int flags, NativeButtonVariant child)
        => Method<SelectCall>(self, 21)(self, flags, child);

    internal static int DefaultAction(nint self, NativeButtonVariant child)
        => Method<ActionCall>(self, 25)(self, child);

    internal static int Next(nint self, ref NativeButtonVariant result,
        out uint fetched)
        => Method<NextCall>(self, 3)(self, 1, ref result, out fetched);

    internal static int Reset(nint self)
        => Method<ResetCall>(self, 5)(self);

    internal static void Assign(ref nint destination, nint source)
    {
        if (source != 0)
        {
            Marshal.AddRef(source);
        }

        nint previous = destination;
        destination = source;
        if (previous != 0)
        {
            Marshal.Release(previous);
        }
    }

    internal static void Release(ref nint value)
    {
        nint previous = value;
        value = 0;
        if (previous != 0)
        {
            Marshal.Release(previous);
        }
    }

    private static T Method<T>(nint self, int slot) where T : Delegate
    {
        if (self == 0)
        {
            throw new InvalidOperationException("MSAAの参照がありません。");
        }

        nint table = Marshal.ReadIntPtr(self);
        return Marshal.GetDelegateForFunctionPointer<T>(
            Marshal.ReadIntPtr(table, slot * IntPtr.Size));
    }
}
