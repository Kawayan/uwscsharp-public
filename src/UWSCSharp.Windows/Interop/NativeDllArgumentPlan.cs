using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// 宣言と評価済み引数から、呼出し用の領域と書戻し先を組み立てます。
/// </summary>
internal sealed partial class NativeDllArgumentPlan
{
    private const int InlineEmptySlotSize = 4;
    private const int MaxStructureCount = 7;
    private const int MaxStructureBytes = 2048;
    internal const int MaxArgumentBlockBytes = 2048;
    private const int MaxArrayStoragePosition = 20;
    private const int MaxReferenceStoragePosition = 40;

    private sealed record StructureText(NativeDllPrimitive Type,
        byte[]? Buffer, int? ReferencedBuffer = null);

    private sealed record StructureNumericBuffer(Type ElementType,
        int Buffer, bool Scalar = false, bool PreserveUnsignedType = false);

    private readonly object?[] arguments;
    private readonly IReadOnlyList<(int Buffer, int Offset)> pointerSlots;
    private readonly NativeDllPrimitive returnType;
    private readonly List<(int Index, NativeDllPrimitive Type)>? nullTextTargets;

    private NativeDllArgumentPlan(object?[] arguments,
        NativeDllPrimitive returnType,
        List<(int Index, NativeDllPrimitive Type)>? nullTextTargets,
        IReadOnlyList<byte[]> initial, IReadOnlyList<NativeDllBufferLink> links,
        IReadOnlyList<(int Buffer, int Offset)> pointerSlots,
        IReadOnlyList<(int Parameter, int Buffer, NativeDllScalarType? Scalar)> roots,
        IReadOnlyList<(int Index, NativeDllStructureLayout Layout, object?[] Values,
            StructureText?[]? Text, StructureNumericBuffer?[]? Numbers)> structures)
    {
        this.arguments = arguments;
        this.pointerSlots = pointerSlots;
        this.returnType = returnType;
        this.nullTextTargets = nullTextTargets;
        Initial = initial;
        Links = links;
        Roots = roots;
        Structures = structures;
    }

    private IReadOnlyList<byte[]> Initial
    {
        get;
    }

    private IReadOnlyList<NativeDllBufferLink> Links
    {
        get;
    }

    private IReadOnlyList<(int Parameter, int Buffer, NativeDllScalarType? Scalar)> Roots
    {
        get;
    }

    private IReadOnlyList<(int Index, NativeDllStructureLayout Layout, object?[] Values,
        StructureText?[]? Text, StructureNumericBuffer?[]? Numbers)> Structures
    {
        get;
    }

    internal static NativeDllArgumentPlan Create(NativeDllDeclaration declaration,
        object?[] arguments, CancellationToken token, int? pointerSize = null)
    {
        token.ThrowIfCancellationRequested();
        IReadOnlyList<NativeDllType> parameterTypes = declaration.Parameters;
        bool packed = declaration.Packed;
        List<(int Index, NativeDllType Type)> buffered = [];
        List<(int Index, NativeDllPrimitive Type)>? nullTextTargets = null;
        int[]? positions = null;
        for (int index = 0; index < arguments.Length; index++)
        {
            NativeDllType parameter = parameterTypes[index];
            if (arguments[index] is null)
            {
                arguments[index] = (nint)0;
            }
            else if (arguments[index] is Value { Kind: ValueKind.Null })
            {
                if (parameter is NativeDllScalarType { IsByReference: true } nullText
                    && NativeDllScalarInput.IsText(nullText.Primitive))
                {
                    positions ??= ScriptPositions(parameterTypes, 0);
                    CheckStoragePosition(nullText, positions[index]);
                }

                arguments[index] = (nint)0;
                if (parameter is NativeDllScalarType { IsByReference: true } text)
                {
                    nullTextTargets ??= [];
                    nullTextTargets.Add((index, text.Primitive));
                }
            }
            else if (parameter is NativeDllStructureType
                || parameter.IsByReference || parameter.IsArray
                || arguments[index] is Value)
            {
                buffered.Add((index, parameter));
            }
        }

        NativeDllPrimitive returnType =
            (declaration.ReturnType as NativeDllScalarType)?.Primitive
            ?? NativeDllPrimitive.Void;
        if (buffered.Count == 0)
        {
            return new NativeDllArgumentPlan(arguments, returnType,
                nullTextTargets, [], [], [], [], []);
        }

        positions ??= ScriptPositions(parameterTypes, 0);
        List<byte[]> initial = [];
        List<NativeDllBufferLink> links = [];
        List<(int Buffer, int Offset)> pointerSlots = [];
        List<(int Index, NativeDllStructureLayout Layout, object?[] Values,
            StructureText?[]? Text, StructureNumericBuffer?[]? Numbers)>
            structures = [];
        List<(int Parameter, int Buffer, NativeDllScalarType? Scalar)> roots = [];
        foreach ((int parameter, NativeDllType type) in buffered)
        {
            object value = arguments[parameter]!;
            if (type is NativeDllStructureType structure)
            {
                int index = AddStructure(structure, (object?[])value, positions[parameter]);
                roots.Add((parameter, index, null));
            }
            else
            {
                var scalar = (NativeDllScalarType)type;
                CheckStoragePosition(scalar, positions[parameter]);
                roots.Add((parameter, initial.Count, scalar));
                initial.Add(value switch
                {
                    Value text => NativeDllTextCodec.EncodeArgument(
                        scalar.Primitive, text),
                    NativeDllExtendedArgument extended =>
                        NativeDllExtendedCodec.Encode(extended.Number),
                    object?[] array when scalar.Primitive == NativeDllPrimitive.Extended =>
                        NativeDllExtendedCodec.EncodeArray(array),
                    object?[] array => NativeDllScalarCodec.EncodeArray(
                        NativeDllScalarInput.AbiType(scalar.Primitive)!, array, pointerSize),
                    _ => NativeDllScalarCodec.Encode(value, pointerSize)
                });
            }
        }

        return new NativeDllArgumentPlan(arguments, returnType, nullTextTargets,
            initial, links, pointerSlots, roots, structures);

        int AddStructure(NativeDllStructureType structure, object?[] values,
            int position)
        {
            token.ThrowIfCancellationRequested();
            if (structures.Count >= MaxStructureCount)
            {
                throw new InvalidOperationException(
                    $"DLLの構造体は1回の呼出しで{MaxStructureCount}個までです。");
            }
            int[] fieldPositions = ScriptPositions(structure.Fields, position);
            Type[] types = [.. structure.Fields.Select(field => field is
                NativeDllScalarType { IsByReference: false, IsArray: false } scalar
                    ? StructureFieldAbiType(scalar.Primitive)
                    : typeof(nint))];
            object?[] fields = [.. values];
            int[]? sizes = null;
            StructureText?[]? text = null;
            StructureNumericBuffer?[]? numbers = null;
            bool hasEmpty = false;
            List<(int Field, int Buffer)>? references = null;
            var alignedPointers = new HashSet<int>();
            for (int field = 0; field < types.Length; field++)
            {
                if (structure.Fields[field] is NativeDllScalarType fieldType
                    && ((values[field] is not null
                            && values[field] is not Value { Kind: ValueKind.Null })
                        || (fieldType.IsByReference
                            && NativeDllScalarInput.IsText(fieldType.Primitive))))
                {
                    CheckStoragePosition(fieldType, fieldPositions[field]);
                }

                if (types[field] == typeof(nint))
                {
                    fields[field] = (nint)0;
                    if (structure.Fields[field] is NativeDllStructureType)
                    {
                        alignedPointers.Add(field);
                    }
                    else
                    {
                        var scalar = (NativeDllScalarType)structure.Fields[field];
                        if (scalar.IsArray
                            || !NativeDllScalarInput.IsText(scalar.Primitive))
                        {
                            if (values[field] is { } arrayValue)
                            {
                                Type element = StructureFieldAbiType(scalar.Primitive);
                                int buffer = initial.Count;
                                bool single = arrayValue is not object?[];
                                initial.Add(arrayValue switch
                                {
                                    NativeDllExtendedArgument extended =>
                                        NativeDllExtendedCodec.Encode(extended.Number),
                                    object?[] array when element == typeof(NativeDllExtendedArgument) =>
                                        NativeDllExtendedCodec.EncodeArray(array),
                                    object?[] array => NativeDllScalarCodec.EncodeArray(
                                        element, array, pointerSize),
                                    _ => NativeDllScalarCodec.Encode(arrayValue, pointerSize)
                                });
                                references ??= [];
                                references.Add((field, buffer));
                                numbers ??= new StructureNumericBuffer?[fields.Length];
                                numbers[field] = new(element, buffer, single, scalar.IsArray);
                            }

                            continue;
                        }

                        int? target = null;
                        if (values[field] is Value { Kind: not ValueKind.Null } value)
                        {
                            byte[] buffer = NativeDllTextCodec.EncodeArgument(
                                scalar.Primitive, value);
                            target = initial.Count;
                            initial.Add(buffer);
                            references ??= [];
                            references.Add((field, target.Value));
                        }

                        text ??= new StructureText?[fields.Length];
                        text[field] = new(scalar.Primitive, null, target);
                    }
                }
                else if (fields[field] is Value value)
                {
                    var scalar = (NativeDllScalarType)structure.Fields[field];
                    if (NativeDllScalarInput.IsHandleByValue(scalar))
                    {
                        fields[field] = (nuint)0;
                        references ??= [];
                        references.Add((field, initial.Count));
                        initial.Add(NativeDllTextCodec.EncodeArgument(
                            scalar.Primitive, value));
                        continue;
                    }

                    sizes ??= new int[fields.Length];
                    if (value.Kind == ValueKind.Null)
                    {
                        fields[field] = new byte[InlineEmptySlotSize];
                        sizes[field] = InlineEmptySlotSize;
                        continue;
                    }

                    byte[] buffer = NativeDllTextCodec.EncodeArgument(
                        scalar.Primitive, value);
                    // 原版はUTF-16のコード単位数をバイト数としてコピーします。
                    int unit = NativeDllTextCodec.IsWide(scalar.Primitive) ? 2 : 1;
                    int length = buffer.Length / unit - 1;
                    fields[field] = buffer[..length];
                    sizes[field] = length;
                    if (length == 0)
                    {
                        hasEmpty = true;
                        continue;
                    }

                    text ??= new StructureText?[fields.Length];
                    text[field] = new(scalar.Primitive, buffer);
                }
            }

            var layout = new NativeDllStructureLayout(types, packed,
                alignedPackedPointerFields: alignedPointers,
                inlineFieldSizes: sizes, pointerSize: pointerSize);
            if (layout.DataSize > MaxStructureBytes)
            {
                throw new InvalidOperationException(
                    $"DLLの構造体の大きさは{MaxStructureBytes}バイトまでです。");
            }

            byte[] payload = layout.Encode(fields);
            NativeDllStructureLayout readLayout = layout;
            if (hasEmpty)
            {
                // 原版は空文字を書き戻さず、読取り時だけ4バイト進めます。
                for (int field = 0; field < fields.Length; field++)
                {
                    if (fields[field] is byte[] { Length: 0 })
                    {
                        sizes![field] = InlineEmptySlotSize;
                    }
                }

                readLayout = new(types, packed,
                    alignedPackedPointerFields: alignedPointers,
                    inlineFieldSizes: sizes, pointerSize: pointerSize);
                if (readLayout.Size > payload.Length)
                {
                    Array.Resize(ref payload, readLayout.Size);
                }
            }

            int index = initial.Count;
            initial.Add(payload);
            structures.Add((index, readLayout, values, text, numbers));
            if (pointerSize is not null)
            {
                for (int field = 0; field < types.Length; field++)
                {
                    if (types[field] == typeof(nint))
                    {
                        pointerSlots.Add((index, layout.Offsets[field]));
                    }
                }
            }
            if (references is not null)
            {
                foreach ((int field, int target) in references)
                {
                    links.Add(new(index, layout.Offsets[field], target));
                }
            }

            for (int field = 0; field < types.Length; field++)
            {
                if (structure.Fields[field] is NativeDllStructureType child)
                {
                    int target = AddStructure(child, (object?[])values[field]!,
                        fieldPositions[field]);
                    links.Add(new(index, layout.Offsets[field], target));
                }
            }

            return index;
        }
    }

    internal object? InvokeDirect(NativeDllModule.Function function,
        CancellationToken token)
    {
        object? result = Roots.Count != 0
            ? InvokeBuffers(function, token)
            : NativeDllScalarInput.IsText(returnType)
                ? function.Invoke(arguments, address =>
                    NativeDllTextReturn.Read(returnType, (nint)address!, token))
                : function.Invoke(arguments);
        ApplyNullTextTargets();
        return result;
    }

    private void ApplyNullTextTargets()
    {
        if (nullTextTargets is not null)
        {
            foreach ((int index, NativeDllPrimitive type) in nullTextTargets)
            {
                arguments[index] = NativeDllTextCodec.DecodeArgument(type, null);
            }
        }
    }

    private object? InvokeBuffers(NativeDllModule.Function function,
        CancellationToken token)
    {
        var buffers = new NativeDllBufferGraph(Initial, Links);
        return buffers.Invoke(function, pointers =>
        {
            object?[] nativeArguments = [.. arguments];
            foreach ((int parameter, int index, NativeDllScalarType? scalar) in Roots)
            {
                nativeArguments[parameter] = scalar is not null
                    && NativeDllScalarInput.IsHandleByValue(scalar)
                    ? (object)unchecked((nuint)pointers[index]) : pointers[index];
            }

            token.ThrowIfCancellationRequested();
            return nativeArguments;
        }, (result, bytes, pointers) =>
        {
            if (NativeDllScalarInput.IsText(returnType))
            {
                result = NativeDllTextReturn.Read(returnType, (nint)result!,
                    token, bytes, pointers);
            }

            ApplyBuffers(bytes, (type, _, _, address) =>
                NativeDllTextReturn.Read(type, address, token, bytes, pointers));
            return result;
        });

    }

    private void ApplyBuffers(IReadOnlyList<byte[]> bytes,
        Func<NativeDllPrimitive, int, int, nint, Value> readText,
        int? pointerSize = null,
        IReadOnlySet<(int Buffer, int Offset)>? unchangedHandles = null,
        ushort controlWord = 0)
    {
        foreach ((int parameter, int index, NativeDllScalarType? scalar)
            in Roots)
        {
            if (scalar is null
                || NativeDllScalarInput.IsHandleByValue(scalar))
            {
                continue;
            }

            if (NativeDllScalarInput.IsText(scalar.Primitive))
            {
                if (scalar.IsByReference)
                {
                    arguments[parameter] = NativeDllTextCodec.DecodeArgument(
                        scalar.Primitive, bytes[index]);
                }

                continue;
            }

            bool array = scalar.IsArray && arguments[parameter] is object?[];
            if (scalar.Primitive == NativeDllPrimitive.Extended)
            {
                arguments[parameter] = array
                    ? NativeDllExtendedCodec.DecodeArray(bytes[index], controlWord)
                    : NativeDllExtendedCodec.Decode(bytes[index], controlWord);
                continue;
            }

            Type element = NativeDllScalarInput.AbiType(scalar.Primitive)!;
            arguments[parameter] = array
                ? NativeDllScalarCodec.DecodeArray(element, bytes[index], pointerSize)
                : NativeDllScalarCodec.Decode(element, bytes[index], pointerSize);
        }

        foreach ((int index, NativeDllStructureLayout layout,
            object?[] values, StructureText?[]? text,
            StructureNumericBuffer?[]? numbers) in Structures)
        {
            object[] decoded = layout.Decode(
                bytes[index].AsSpan(0, layout.Size), controlWord);
            for (int field = 0; field < values.Length; field++)
            {
                if (numbers?[field] is { } numberBuffer)
                {
                    if (numberBuffer.Scalar)
                    {
                        byte[] fieldBytes = bytes[numberBuffer.Buffer];
                        object value = numberBuffer.ElementType == typeof(NativeDllExtendedArgument)
                            ? NativeDllExtendedCodec.Decode(fieldBytes, controlWord)
                            : NativeDllScalarCodec.Decode(numberBuffer.ElementType,
                                fieldBytes, pointerSize);
                        values[field] = value is uint unsigned && numberBuffer.PreserveUnsignedType
                            ? NativeDllScalarResult.UnsignedArrayValue(unsigned)
                            : value;
                    }
                    else
                    {
                        byte[] fieldBytes = bytes[numberBuffer.Buffer];
                        object[] elements = numberBuffer.ElementType == typeof(NativeDllExtendedArgument)
                            ? NativeDllExtendedCodec.DecodeArray(fieldBytes, controlWord)
                            : NativeDllScalarCodec.DecodeArray(numberBuffer.ElementType,
                                fieldBytes, pointerSize);
                        for (int element = 0; element < elements.Length; element++)
                        {
                            if (numberBuffer.PreserveUnsignedType
                                && elements[element] is uint unsignedElement)
                            {
                                elements[element] = NativeDllScalarResult
                                    .UnsignedArrayValue(unsignedElement);
                            }
                        }

                        values[field] = elements;
                    }
                    continue;
                }

                if (text?[field] is { } fieldText)
                {
                    if (fieldText.Buffer is null)
                    {
                        // STRINGは差替え後のポインター、固定長型は元の領域を読みます。
                        values[field] = NativeDllTextCodec.IsNulTerminated(fieldText.Type)
                            ? readText(fieldText.Type, index, layout.Offsets[field], (nint)decoded[field])
                            : NativeDllTextCodec.DecodeArgument(fieldText.Type,
                                fieldText.ReferencedBuffer is int referenced
                                    ? bytes[referenced] : null);
                        continue;
                    }

                    if (NativeDllTextCodec.IsNulTerminated(fieldText.Type))
                    {
                        ((byte[])decoded[field]).CopyTo(fieldText.Buffer, 0);
                    }

                    values[field] = NativeDllTextCodec.DecodeArgument(
                        fieldText.Type, fieldText.Buffer);
                    continue;
                }

                // 子構造体は所有領域から読み、ハンドルはフィールドの値を書き戻します。
                if (unchangedHandles?.Contains((index, layout.Offsets[field])) != true
                    && (values[field] is not (object?[] or Value)
                        || decoded[field] is nuint))
                {
                    values[field] = decoded[field];
                }
            }
        }
    }

    internal static void CheckArgumentBlockSize(IReadOnlyList<NativeDllType> types)
    {
        int size = 0;
        foreach (NativeDllType type in types)
        {
            // 外側は原版x86の4バイトスロットで判定し、x64の実際の幅と分けます。
            size += type switch
            {
                NativeDllScalarType
                {
                    IsByReference: false,
                    IsArray: false,
                    Primitive: NativeDllPrimitive.Extended
                } => NativeDllX86Control.ExtendedSlotSize,
                NativeDllScalarType
                {
                    IsByReference: false,
                    IsArray: false,
                    Primitive: NativeDllPrimitive.Double or NativeDllPrimitive.Longlong
                } => 8,
                _ => 4
            };
            if (size > MaxArgumentBlockBytes)
            {
                throw new InvalidOperationException(
                    $"DLLの外側引数領域は{MaxArgumentBlockBytes}バイトまでです。");
            }
        }
    }

    private static Type StructureFieldAbiType(NativeDllPrimitive primitive) =>
        primitive == NativeDllPrimitive.Extended ? typeof(NativeDllExtendedArgument)
            : NativeDllScalarInput.AbiType(primitive) ?? typeof(byte[]);

    private static int[] ScriptPositions(IReadOnlyList<NativeDllType> types,
        int position)
    {
        int[] positions = new int[types.Count];
        for (int index = 0; index < types.Count; index++)
        {
            positions[index] = position;
            position = checked(position + ScriptParameterCount(types[index]));
        }
        return positions;
    }

    private static int ScriptParameterCount(NativeDllType type) =>
        type is NativeDllStructureType structure
            ? structure.Fields.Sum(ScriptParameterCount) : 1;

    private static void CheckStoragePosition(NativeDllScalarType type,
        int position)
    {
        if (type.IsArray && position >= MaxArrayStoragePosition)
        {
            throw new InvalidOperationException(
                $"DLLの配列は構造体を展開した引数の{MaxArrayStoragePosition}番目までに指定してください。");
        }
        if ((type.IsByReference || NativeDllScalarInput.IsText(type.Primitive))
            && position >= MaxReferenceStoragePosition)
        {
            throw new InvalidOperationException(
                $"DLLの参照値と文字列は構造体を展開した引数の{MaxReferenceStoragePosition}番目までに指定してください。");
        }
    }

}
