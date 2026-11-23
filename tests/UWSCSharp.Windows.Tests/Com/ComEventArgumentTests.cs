using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ComEventArgumentTests
{
    /// <summary>
    /// 参照所有の検証だけに使うCOM公開可能な値です。
    /// </summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.AutoDispatch)]
    public sealed class InterfaceValue
    {
        /// <summary>
        /// ディスパッチへ公開する検証用の数値です。
        /// </summary>
        public int Number => 1;
    }

    internal sealed class Frame(int count) : IDisposable
    {
        internal static readonly int Stride = IntPtr.Size == 8 ? 24 : 16;
        internal readonly nint Arguments = Allocate(count * Stride);
        internal readonly nint Storage = Allocate(count * Stride);

        internal nint Argument(int index) => Arguments + (count - index - 1) * Stride;

        internal nint Stored(int index) => Storage + index * Stride;

        internal DISPPARAMS Parameters => new()
        {
            cArgs = count,
            rgvarg = Arguments
        };

        internal NativeComEventArguments Open()
        {
            DISPPARAMS parameters = Parameters;
            return new NativeComEventArguments(in parameters);
        }

        internal void Set(int index, object? value,
            bool byReference = true, bool variantReference = false)
        {
            ComVariantValue.Write(value, Stored(index));
            if (!byReference)
            {
                ComVariantValue.Write(value, Argument(index));
                return;
            }

            ushort type = variantReference ? (ushort)12
                : unchecked((ushort)Marshal.ReadInt16(Stored(index)));
            Marshal.WriteInt16(Argument(index), unchecked((short)(type | 0x4000)));
            Marshal.WriteIntPtr(Argument(index), 8,
                Stored(index) + (type is 12 or 14 ? 0 : 8));
        }

        public void Dispose()
        {
            for (int index = 0; index < count; index++)
            {
                Marshal.ThrowExceptionForHR(VariantClear(Argument(index)));
                Marshal.ThrowExceptionForHR(VariantClear(Stored(index)));
            }

            Marshal.FreeCoTaskMem(Arguments);
            Marshal.FreeCoTaskMem(Storage);
        }

        private static nint Allocate(int size)
        {
            nint result = Marshal.AllocCoTaskMem(Math.Max(size, 1));
            Marshal.Copy(new byte[size], 0, result, size);
            return result;
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(14)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    public void TypedScalarReferenceKeepsItsTypeAndUpdatesValue(int type)
    {
        using var memory = new Frame(1);
        memory.Set(0, Scalar((ushort)type, 0));
        NativeComEventArguments frame = memory.Open();
        frame.WriteBack([Scalar((ushort)type, 7)]);
        ComVariantValue result = frame.Read(0);
        Assert.Equal(type, result.Type);
        Assert.Equal((ushort)(type | 0x4000), result.ReferenceType!.Value);
        object? expected = Scalar((ushort)type, 7).Value;
        if (expected is ErrorWrapper error)
        {
            Assert.Equal(error.ErrorCode, Assert.IsType<ErrorWrapper>(result.Value).ErrorCode);
        }
        else
        {
            Assert.Equal(expected, result.Value);
        }

        if (type == 14)
        {
            Assert.Equal(0, Marshal.ReadInt16(memory.Stored(0)));
        }
    }

    [Theory]
    [InlineData(9)]
    [InlineData(13)]
    public void NullInterfaceReferenceRetainsItsInterfaceKind(int type)
    {
        using var memory = new Frame(1);
        memory.Set(0, new ComVariantValue((ushort)type, null));
        NativeComEventArguments frame = memory.Open();
        frame.WriteBack([new ComVariantValue((ushort)type, null)]);
        Assert.Equal(type, frame.Read(0).Type);
        Assert.Null(frame.Read(0).Value);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(13)]
    public void InterfaceReferenceTransfersOwnershipAndReleasesPreviousValue(int type)
    {
        var original = new InterfaceValue();
        var replacement = new InterfaceValue();
        nint originalPointer = Marshal.GetIUnknownForObject(original);
        nint replacementPointer = Marshal.GetIUnknownForObject(replacement);
        try
        {
            int originalBaseline = ReferenceCount(originalPointer);
            int replacementBaseline = ReferenceCount(replacementPointer);
            using (var memory = new Frame(1))
            {
                memory.Set(0, new ComVariantValue((ushort)type, original));
                Assert.Equal(originalBaseline + 1, ReferenceCount(originalPointer));
                memory.Open().WriteBack([new ComVariantValue((ushort)type, replacement)]);
                Assert.Equal(originalBaseline, ReferenceCount(originalPointer));
                Assert.Equal(replacementBaseline + 1, ReferenceCount(replacementPointer));
            }

            Assert.Equal(replacementBaseline, ReferenceCount(replacementPointer));
        }
        finally
        {
            Marshal.Release(originalPointer);
            Marshal.Release(replacementPointer);
            GC.KeepAlive(original);
            GC.KeepAlive(replacement);
        }

        static int ReferenceCount(nint pointer)
        {
            Marshal.AddRef(pointer);
            return Marshal.Release(pointer);
        }
    }

    [Fact]
    public void ArgumentOrderAndOnlyReferenceWritebackArePreserved()
    {
        using var memory = new Frame(3);
        memory.Set(0, 42, byReference: false);
        memory.Set(1, false);
        memory.Set(2, "before");
        NativeComEventArguments frame = memory.Open();
        Assert.Equal(42, frame.Read(0).Value);
        Assert.False(frame.IsByReference(0));
        Assert.True(frame.IsByReference(1));
        frame.WriteBack([new object(), true, "日本語\0終端"]);
        Assert.Equal(42, frame.Read(0).Value);
        Assert.Equal(true, frame.Read(1).Value);
        Assert.Equal("日本語\0終端", frame.Read(2).Value);
    }

    [Fact]
    public void VariantReferenceCanChangeTypeWithoutChangingBorrowedWrapper()
    {
        using var memory = new Frame(1);
        memory.Set(0, "before", variantReference: true);
        NativeComEventArguments frame = memory.Open();
        frame.WriteBack([7.5]);
        Assert.Equal(5, frame.Read(0).Type);
        Assert.Equal(7.5, frame.Read(0).Value);
        Assert.Equal(0x400c, unchecked((ushort)Marshal.ReadInt16(memory.Argument(0))));
    }

    [Fact]
    public void AliasedStringReferencesReleaseIntermediateValueOnce()
    {
        using var memory = new Frame(2);
        memory.Set(0, "before");
        memory.Set(1, "unused");
        Marshal.WriteIntPtr(memory.Argument(1), 8, memory.Stored(0) + 8);
        NativeComEventArguments frame = memory.Open();
        frame.WriteBack(["first", "last"]);
        Assert.Equal("last", frame.Read(0).Value);
        Assert.Equal("last", frame.Read(1).Value);
    }

    [Fact]
    public void ConversionFailureLeavesAllReferenceArgumentsUnchanged()
    {
        using var memory = new Frame(2);
        memory.Set(0, 1);
        memory.Set(1, 2);
        NativeComEventArguments frame = memory.Open();
        Exception failure = Assert.ThrowsAny<Exception>(
            () => frame.WriteBack(["7", "数値ではない"]));
        Assert.True(ComOperationErrors.Contains(failure));
        Assert.Equal(1, frame.Read(0).Value);
        Assert.Equal(2, frame.Read(1).Value);
        frame.WriteBack(["7", "8"]);
        Assert.Equal(7, frame.Read(0).Value);
        Assert.Equal(8, frame.Read(1).Value);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(12)]
    public void ArrayReferencePreservesElementKindAndAcceptsNewBounds(int type)
    {
        using var memory = new Frame(1);
        memory.Set(0, new ComArrayValue((ushort)type, [-2], [-1], [1, 2]));
        NativeComEventArguments frame = memory.Open();
        frame.WriteBack([new ComArrayValue(12, [5], [6], ["7", "8"])]);
        ComVariantValue result = frame.Read(0);
        Assert.Equal(0x2000 | type, result.Type);
        ComArrayValue array = Assert.IsType<ComArrayValue>(result.Value);
        Assert.Equal(type, array.ElementType);
        Assert.Equal([5], array.LowerBounds);
        Assert.Equal([6], array.UpperBounds);
        ComVariantValue first = Assert.IsType<ComVariantValue>(array.Values[0]);
        Assert.Equal(type == 3 ? 3 : 8, first.Type);
        Assert.Equal(type == 3 ? (object)7 : "7", first.Value);
    }

    [Fact]
    public void LockedArrayFailurePreservesItsBorrowedPointer()
    {
        using var memory = new Frame(1);
        memory.Set(0, new ComArrayValue(3, [0], [0], [1]));
        NativeComEventArguments frame = memory.Open();
        nint pointer = Marshal.ReadIntPtr(memory.Stored(0), 8);
        Marshal.ThrowExceptionForHR(SafeArrayLock(pointer));
        try
        {
            Exception failure = Assert.ThrowsAny<Exception>(() =>
                frame.WriteBack([new ComArrayValue(3, [0], [0], [7])]));
            Assert.True(ComOperationErrors.Contains(failure));
            Assert.Equal(pointer, Marshal.ReadIntPtr(memory.Stored(0), 8));
        }
        finally
        {
            Marshal.ThrowExceptionForHR(SafeArrayUnlock(pointer));
        }

        ComArrayValue original = Assert.IsType<ComArrayValue>(frame.Read(0).Value);
        Assert.Equal(1, Assert.IsType<ComVariantValue>(original.Values[0]).Value);
    }

    [Fact]
    public void WrongResultCountAndCancellationDoNotMutateReferences()
    {
        using var memory = new Frame(1);
        memory.Set(0, 1);
        NativeComEventArguments frame = memory.Open();
        Assert.Throws<InvalidOperationException>(() => frame.WriteBack([]));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        OperationCanceledException failure = Assert.ThrowsAny<OperationCanceledException>(
            () => frame.WriteBack([7], cancellation.Token));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal(1, frame.Read(0).Value);
    }

    [Fact]
    public void EmptyEventHasNoArgumentsOrWriteback()
    {
        var parameters = new DISPPARAMS();
        var frame = new NativeComEventArguments(in parameters);
        frame.WriteBack([]);
        Assert.Equal(0, frame.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => frame.Read(0));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(1, 0)]
    [InlineData(1_000_001, 0)]
    [InlineData(0, 1)]
    public void InvalidArgumentLayoutIsRejectedBeforeReading(int count, int named)
    {
        var parameters = new DISPPARAMS { cArgs = count, cNamedArgs = named };
        Exception failure = Assert.Throws<InvalidOperationException>(
            () => new NativeComEventArguments(in parameters));
        Assert.True(ComOperationErrors.Contains(failure));
    }

    [Theory]
    [InlineData(0x4003)]
    [InlineData(0x4024)]
    public void NullOrUnsupportedReferenceIsRejected(int type)
    {
        using var memory = new Frame(1);
        Marshal.WriteInt16(memory.Argument(0), unchecked((short)type));
        Assert.ThrowsAny<Exception>(() => memory.Open());
    }

    private static ComVariantValue Scalar(ushort type, int value) => new(type,
        type switch
        {
            2 => (short)value,
            3 or 22 => value,
            4 => (float)value,
            5 or 7 => (double)value,
            6 or 14 => (decimal)value,
            10 => new ErrorWrapper(value),
            11 => value != 0,
            16 => (sbyte)value,
            17 => (byte)value,
            18 => (ushort)value,
            19 or 23 => (uint)value,
            20 => (long)value,
            21 => (ulong)value,
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        });

    [DllImport("oleaut32.dll")]
    private static extern int VariantClear(nint variant);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayLock(nint array);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayUnlock(nint array);
}
