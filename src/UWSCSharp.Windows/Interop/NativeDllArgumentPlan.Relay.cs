using System.Runtime.InteropServices;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

internal sealed partial class NativeDllArgumentPlan
{
    private const int X86PointerSize = 4;

    /// <summary>
    /// x86向けの領域と書戻し範囲を、プロセス間で渡せる形式へ変換します。
    /// </summary>
    internal static (NativeDllRelayCall Call, Func<NativeDllRelayResult, object?> Apply)
        PrepareRelay(NativeDllDeclaration declaration,
        object?[] arguments, CallingConvention convention, CancellationToken token,
        ushort? controlWord = null)
    {
        CheckArgumentBlockSize(declaration.Parameters);
        NativeDllArgumentPlan plan = Create(declaration, arguments, token, X86PointerSize);
        NativeDllRelayCall call = plan.ToRelayCall(declaration.Name, convention)
            with
        {
            ControlWord = controlWord,
            CaptureControlWord = declaration.Parameters.Any(
                NativeDllScalarInput.IsExtendedParameter)
        };
        return (call, result => plan.ApplyRelayResult(call, result));
    }

    private NativeDllRelayCall ToRelayCall(string export, CallingConvention convention)
    {
        var nativeArguments = new NativeDllRelayArgument[arguments.Length];
        foreach ((int parameter, int buffer, _) in Roots)
        {
            nativeArguments[parameter] = new(NativeDllRelayType.BufferPointer, Buffer: buffer);
        }

        for (int index = 0; index < nativeArguments.Length; index++)
        {
            nativeArguments[index] ??= arguments[index] switch
            {
                nint pointer when pointer == 0 => new(NativeDllRelayType.BufferPointer),
                nuint number => new(NativeDllRelayType.UInt32,
                    NativeDllScalarCodec.Encode(number, X86PointerSize)),
                NativeDllExtendedArgument extended => new(NativeDllRelayType.Extended,
                    NativeDllScalarCodec.Encode(extended.Number)),
                object number => new(RelayType(number.GetType()),
                    NativeDllScalarCodec.Encode(number)),
                null => throw new InvalidOperationException("DLL引数が変換されていません。")
            };
        }

        List<NativeDllRelayOutput> outputs = [];
        ILookup<int, int> slots = pointerSlots
            .Concat(Links.Select(link => (link.SourceBuffer, link.SourceOffset)))
            .ToLookup(slot => slot.Item1, slot => slot.Item2);
        for (int index = 0; index < Initial.Count; index++)
        {
            int offset = 0;
            foreach (int slot in slots[index].Order())
            {
                if (slot > offset)
                {
                    outputs.Add(new(index, offset, slot - offset));
                }

                offset = slot + X86PointerSize;
            }

            if (offset < Initial[index].Length)
            {
                outputs.Add(new(index, offset, Initial[index].Length - offset));
            }
        }

        foreach ((int Index, NativeDllStructureLayout Layout, object?[] Values, StructureText?[]? Text, StructureNumericBuffer?[]? Numbers) structure in Structures)
        {
            for (int field = 0; field < structure.Values.Length; field++)
            {
                int offset = structure.Layout.Offsets[field];
                if (structure.Layout.IsHandleField(field)
                    && structure.Values[field] is Value
                    && slots[structure.Index].Contains(offset))
                {
                    outputs.Add(new(structure.Index, offset, 0,
                        NativeDllRelayOutputKind.Handle));
                }

                if (structure.Text?[field] is { Buffer: null } value
                    && NativeDllTextCodec.IsNulTerminated(value.Type))
                {
                    NativeDllRelayOutputKind kind = NativeDllTextCodec.IsWide(value.Type)
                        ? NativeDllRelayOutputKind.Utf16Text
                        : NativeDllRelayOutputKind.AnsiText;
                    outputs.Add(new(structure.Index, structure.Layout.Offsets[field], 0, kind));
                }
            }
        }

        NativeDllRelayType resultType = NativeDllScalarInput.IsText(returnType)
            ? NativeDllTextCodec.IsWide(returnType)
                ? NativeDllRelayType.Utf16Text : NativeDllRelayType.AnsiText
            : RelayType(RelayReturnAbiType(returnType) ?? typeof(void));
        return new NativeDllRelayCall(export, convention, resultType, nativeArguments,
            [.. Initial], [.. Links], [.. outputs]);
    }

    private object? ApplyRelayResult(NativeDllRelayCall call, NativeDllRelayResult result)
    {
        if (result.Outputs.Length != call.Outputs.Length)
        {
            throw new InvalidOperationException("DLL中継の書戻し範囲の数が一致しません。");
        }

        object? returned = result.ReturnValue is not { } returnedBytes
            ? call.ReturnType == NativeDllRelayType.Void ? null
                : throw new InvalidOperationException("DLL中継の戻り値がありません。")
            : NativeDllScalarInput.IsText(returnType)
                ? NativeDllTextCodec.DecodeContents(returnType, returnedBytes)
                : NativeDllScalarCodec.Decode(RelayReturnAbiType(returnType)!,
                    returnedBytes, X86PointerSize);
        byte[][] buffers = [.. Initial.Select(buffer => (byte[])buffer.Clone())];
        Dictionary<(int Buffer, int Offset, NativeDllRelayOutputKind Kind), byte[]> text = [];
        HashSet<(int Buffer, int Offset)> unchangedHandles = [];
        for (int index = 0; index < call.Outputs.Length; index++)
        {
            NativeDllRelayOutput output = call.Outputs[index];
            byte[] bytes = result.Outputs[index];
            if (output.Kind == NativeDllRelayOutputKind.Handle && bytes.Length == 0)
            {
                unchangedHandles.Add((output.Buffer, output.Offset));
                continue;
            }

            if (output.Kind is NativeDllRelayOutputKind.Bytes or NativeDllRelayOutputKind.Handle)
            {
                int length = output.Kind == NativeDllRelayOutputKind.Handle
                    ? X86PointerSize : output.Length;
                if (bytes.Length != length)
                {
                    throw new InvalidOperationException("DLL中継の書戻し範囲の長さが一致しません。");
                }

                bytes.CopyTo(buffers[output.Buffer], output.Offset);
            }
            else
            {
                text[(output.Buffer, output.Offset, output.Kind)] = bytes;
            }
        }

        ApplyBuffers(buffers, (type, buffer, offset, _) =>
            NativeDllTextCodec.DecodeContents(type, text[(buffer, offset,
                NativeDllTextCodec.IsWide(type) ? NativeDllRelayOutputKind.Utf16Text
                    : NativeDllRelayOutputKind.AnsiText)]), X86PointerSize, unchangedHandles,
                result.ReturnedControlWord ?? call.ControlWord ?? 0);
        ApplyNullTextTargets();
        return returned;
    }

    // x86のST(0)をdoubleへ格納してから中継します。
    private static Type? RelayReturnAbiType(NativeDllPrimitive primitive) =>
        primitive == NativeDllPrimitive.Extended ? typeof(double)
            : NativeDllScalarInput.AbiType(primitive);

    private static NativeDllRelayType RelayType(Type type)
    {
        if (type == typeof(void))
        {
            return NativeDllRelayType.Void;
        }

        if (type == typeof(byte))
        {
            return NativeDllRelayType.UInt8;
        }

        if (type == typeof(ushort))
        {
            return NativeDllRelayType.UInt16;
        }

        if (type == typeof(int))
        {
            return NativeDllRelayType.Int32;
        }

        if (type == typeof(uint) || type == typeof(nuint))
        {
            return NativeDllRelayType.UInt32;
        }

        if (type == typeof(long))
        {
            return NativeDllRelayType.Int64;
        }

        if (type == typeof(float))
        {
            return NativeDllRelayType.Float32;
        }

        if (type == typeof(double))
        {
            return NativeDllRelayType.Float64;
        }

        throw new ArgumentException("中継に対応するABI型を指定してください。", nameof(type));
    }
}
