using System.Buffers.Binary;
using System.Runtime.InteropServices;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

internal static class NativeDllX86Control
{
    internal const int ExtendedSlotSize = 12;
    internal static object? Invoke(nint address, Type returnType,
        object?[] arguments, ushort? control,
        Action<ushort>? captureControl = null)
    {
        int size = 0;
        foreach (object? argument in arguments)
        {
            int width = argument is NativeDllExtendedArgument
                ? ExtendedSlotSize
                : Math.Max(4, NativeDllScalarCodec.GetSize(argument!.GetType()));
            size = checked(size + width);
        }

        if (size > NativeDllArgumentPlan.MaxArgumentBlockBytes)
        {
            throw new ArgumentException("DLLの外側引数領域が上限を超えています。",
                nameof(arguments));
        }

        byte[] stack = new byte[size];
        int offset = 0;
        foreach (object? argument in arguments)
        {
            if (argument is NativeDllExtendedArgument extended)
            {
                NativeDllExtendedCodec.Write(stack.AsSpan(offset, ExtendedSlotSize), extended.Number);
                offset += ExtendedSlotSize;
                continue;
            }

            // x86のABIでは4バイト未満の整数もDWORDのスロットで渡します。
            object value = argument switch
            {
                sbyte number => (object)(int)number,
                short number => (object)(int)number,
                byte number => (object)(uint)number,
                ushort number => (object)(uint)number,
                _ => argument!
            };
            int width = NativeDllScalarCodec.GetSize(value.GetType());
            NativeDllScalarCodec.Write(stack.AsSpan(offset, width), value);
            offset += width;
        }

        ReturnKind kind = returnType == typeof(float) ? ReturnKind.Single
            : returnType == typeof(double) ? ReturnKind.Double : ReturnKind.Integer;
        uint count = (uint)(stack.Length / 4);
        ushort returnedControl = 0;
        uint error = captureControl is not null
            ? InvokeCapturingControl(address, stack, count, kind,
                control.HasValue ? 1 : 0, control.GetValueOrDefault(),
                out ulong result, out returnedControl)
            : control is ushort word
                ? InvokeControlled(address, stack, count, kind, word, out result)
                : InvokeCurrent(address, stack, count, kind, out result);
        if (error != 0)
        {
            throw new InvalidOperationException(
                $"32ビットDLLの浮動小数点呼出しに失敗しました (0x{error:x8})。");
        }

        captureControl?.Invoke(returnedControl);

        if (returnType == typeof(void))
        {
            return null;
        }

        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, result);
        return NativeDllScalarCodec.Decode(returnType,
            bytes[..NativeDllScalarCodec.GetSize(returnType)]);
    }

    [DllImport("UWSCSharp.X86Call.Native.dll", ExactSpelling = true,
        CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static extern uint InvokeControlled(nint target, byte[] stack,
        uint count, ReturnKind kind, ushort control, out ulong result);

    [DllImport("UWSCSharp.X86Call.Native.dll", ExactSpelling = true,
        CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static extern uint InvokeCurrent(nint target, byte[] stack,
        uint count, ReturnKind kind, out ulong result);

    [DllImport("UWSCSharp.X86Call.Native.dll", ExactSpelling = true,
        CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static extern uint InvokeCapturingControl(nint target, byte[] stack,
        uint count, ReturnKind kind, int applyControl, ushort control,
        out ulong result, out ushort returnedControl);

    private enum ReturnKind : uint
    {
        Integer = 0,
        Single = 1,
        Double = 2
    }
}
