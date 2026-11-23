using System.Runtime.InteropServices;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

internal enum NativeDllRelayType
{
    Void, Int8, UInt8, Int16, UInt16, Int32, UInt32, Int64, UInt64,
    Float32, Float64, BufferPointer, AnsiText, Utf16Text, Extended
}

internal sealed record NativeDllRelayArgument(NativeDllRelayType Type,
    byte[]? Value = null, int? Buffer = null, int Offset = 0);

internal enum NativeDllRelayOutputKind
{
    Bytes, AnsiText, Utf16Text, Handle
}

internal sealed record NativeDllRelayOutput(int Buffer, int Offset, int Length,
    NativeDllRelayOutputKind Kind = NativeDllRelayOutputKind.Bytes);

internal sealed record NativeDllRelayCall(string Export, CallingConvention Convention,
    NativeDllRelayType ReturnType, NativeDllRelayArgument[] Arguments,
    byte[][] Buffers, NativeDllBufferLink[] Links, NativeDllRelayOutput[] Outputs,
    ushort? ControlWord = null, bool CaptureControlWord = false);

internal sealed record NativeDllRelayResult(byte[]? ReturnValue, byte[][] Outputs,
    ushort? ReturnedControlWord = null);

/// <summary>
/// 親から受け取ったバイト列と参照関係を、x86の子で呼出しに変換します。
/// </summary>
internal static class NativeDllRelayInvocation
{
    internal static NativeDllRelayResult Invoke(NativeDllModule module,
        NativeDllRelayCall call, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(call.Arguments);
        ArgumentNullException.ThrowIfNull(call.Outputs);
        token.ThrowIfCancellationRequested();
        bool textResult = call.ReturnType is NativeDllRelayType.AnsiText
            or NativeDllRelayType.Utf16Text;
        Type returnType = textResult ? typeof(nint) : ResolveType(call.ReturnType);
        if (!textResult && returnType == typeof(nint))
        {
            throw new ArgumentException("戻り値にはポインターを指定できません。", nameof(call));
        }

        var graph = new NativeDllBufferGraph(call.Buffers, call.Links);
        foreach (NativeDllRelayOutput output in call.Outputs)
        {
            ArgumentNullException.ThrowIfNull(output);
            byte[] buffer = GetBuffer(call, output.Buffer);
            int length = output.Kind switch
            {
                NativeDllRelayOutputKind.Bytes => output.Length,
                NativeDllRelayOutputKind.AnsiText or NativeDllRelayOutputKind.Utf16Text
                    when output.Length == 0 => nint.Size,
                NativeDllRelayOutputKind.Handle when output.Length == 0
                    && call.Links.Any(link => link.SourceBuffer == output.Buffer
                        && link.SourceOffset == output.Offset) => nint.Size,
                _ => throw new ArgumentException("書戻しの形式と長さが一致しません。", nameof(call))
            };
            if (length < 0 || output.Offset < 0
                || output.Offset > buffer.Length - length
                || call.Links.Any(link => output.Buffer == link.SourceBuffer
                    && output.Offset < link.SourceOffset + nint.Size
                    && link.SourceOffset < output.Offset + length
                    && (output.Kind == NativeDllRelayOutputKind.Bytes
                        || output.Offset != link.SourceOffset)))
            {
                throw new ArgumentException("書戻し範囲が領域外か参照スロットと重複しています。", nameof(call));
            }
        }

        var types = new Type[call.Arguments.Length];
        object?[] values = new object?[types.Length];
        for (int index = 0; index < types.Length; index++)
        {
            NativeDllRelayArgument argument = call.Arguments[index];
            ArgumentNullException.ThrowIfNull(argument);
            Type type = ResolveType(argument.Type);
            types[index] = type;
            if (type == typeof(nint))
            {
                if (argument.Value is not null || argument.Offset < 0
                    || (argument.Buffer is int buffer
                        ? argument.Offset >= GetBuffer(call, buffer).Length
                        : argument.Offset != 0))
                {
                    throw new ArgumentException("ポインターには領域内の位置かNULLを指定してください。", nameof(call));
                }
            }
            else
            {
                if (type == typeof(void) || argument.Value is null
                    || argument.Buffer is not null || argument.Offset != 0)
                {
                    throw new ArgumentException("数値引数には宣言した型のバイト列を指定してください。", nameof(call));
                }

                values[index] = type == typeof(NativeDllExtendedArgument)
                    ? new NativeDllExtendedArgument((double)NativeDllScalarCodec.Decode(
                        typeof(double), argument.Value))
                    : NativeDllScalarCodec.Decode(type, argument.Value);
            }
        }

        ushort? returnedControl = null;
        var site = NativeDllCallSite.CreateX86(call.Convention,
            returnType, types, call.ControlWord,
            call.CaptureControlWord ? word => returnedControl = word : null);
        NativeDllModule.Function function = module.Resolve(call.Export, site);
        return graph.Invoke(function, pointers =>
        {
            for (int index = 0; index < types.Length; index++)
            {
                if (types[index] == typeof(nint))
                {
                    NativeDllRelayArgument argument = call.Arguments[index];
                    values[index] = argument.Buffer is int buffer
                        ? pointers[buffer] + argument.Offset : (nint)0;
                }
            }

            token.ThrowIfCancellationRequested();
            return values;
        }, (result, buffers, pointers) =>
        {
            byte[]? returned = result is null ? null : textResult
                ? NativeDllTextContents.Read((nint)result,
                    call.ReturnType == NativeDllRelayType.Utf16Text,
                    token, buffers, pointers).ToArray()
                : NativeDllScalarCodec.Encode(result);
            return new NativeDllRelayResult(returned,
                [.. call.Outputs.Select(output => ReadOutput(output, buffers, pointers, token))],
                returnedControl);
        });
    }

    private static byte[] ReadOutput(NativeDllRelayOutput output,
        IReadOnlyList<byte[]> buffers, IReadOnlyList<nint> pointers,
        CancellationToken token)
    {
        byte[] buffer = buffers[output.Buffer];
        if (output.Kind == NativeDllRelayOutputKind.Bytes)
        {
            return buffer.AsSpan(output.Offset, output.Length).ToArray();
        }

        nint address = (nint)NativeDllScalarCodec.Decode(typeof(nint),
            buffer.AsSpan(output.Offset, nint.Size));
        if (output.Kind == NativeDllRelayOutputKind.Handle)
        {
            // 子が確保した領域を指す値は、親側のハンドル値に変換しません。
            for (int index = 0; index < buffers.Count; index++)
            {
                nuint offset = unchecked((nuint)address - (nuint)pointers[index]);
                if (offset < (nuint)buffers[index].Length)
                {
                    return [];
                }
            }

            return buffer.AsSpan(output.Offset, nint.Size).ToArray();
        }

        return NativeDllTextContents.Read(address,
            output.Kind == NativeDllRelayOutputKind.Utf16Text,
            token, buffers, pointers).ToArray();
    }

    private static byte[] GetBuffer(NativeDllRelayCall call, int index)
    {
        if ((uint)index >= (uint)call.Buffers.Length)
        {
            throw new ArgumentException("存在する領域を指定してください。", nameof(call));
        }

        return call.Buffers[index];
    }

    private static Type ResolveType(NativeDllRelayType type) => type switch
    {
        NativeDllRelayType.Void => typeof(void),
        NativeDllRelayType.Int8 => typeof(sbyte),
        NativeDllRelayType.UInt8 => typeof(byte),
        NativeDllRelayType.Int16 => typeof(short),
        NativeDllRelayType.UInt16 => typeof(ushort),
        NativeDllRelayType.Int32 => typeof(int),
        NativeDllRelayType.UInt32 => typeof(uint),
        NativeDllRelayType.Int64 => typeof(long),
        NativeDllRelayType.UInt64 => typeof(ulong),
        NativeDllRelayType.Float32 => typeof(float),
        NativeDllRelayType.Float64 => typeof(double),
        NativeDllRelayType.BufferPointer => typeof(nint),
        NativeDllRelayType.Extended => typeof(NativeDllExtendedArgument),
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
}
