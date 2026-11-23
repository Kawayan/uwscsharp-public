using System.Globalization;
using System.Runtime.InteropServices;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed partial class ComReferenceMarshallingTests
{
    public static IEnumerable<object[]> VariantTypeNativeScalars()
    {
        foreach ((ushort type, object? value) in new (ushort, object?)[]
        {
            (0, null), (1, DBNull.Value), (2, (short)-123), (3, -123),
            (4, 1.25f), (5, 1.25d), (6, 1.2345m),
            (7, new DateTime(2026, 11, 3)), (8, "a\0日本語😀"),
            (8, null), (8, ""),
            (10, new ErrorWrapper(7)), (11, true), (14, 1.234567890123456789m),
            (16, (sbyte)-123), (17, (byte)123), (18, (ushort)65535),
            (19, uint.MaxValue), (20, 9007199254740993L),
            (21, ulong.MaxValue), (22, -123), (23, uint.MaxValue)
        })
        {
            foreach (string route in new[] { "direct", "variant-byref", "scalar-byref", "out" })
            {
                if (route == "scalar-byref" && type is 0 or 1)
                {
                    continue;
                }

                yield return [type, value!, route];
            }
        }
    }

    // 所有するインプロセスIDispatchだけを使い、画面・入力・外部サーバーは使いません。
    [Theory]
    [MemberData(nameof(VariantTypeNativeScalars))]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task VariantTypeRetainsScalarTagsAcrossNativeRoutes(
        ushort type, object? scalar, string route)
    {
        nint source = Marshal.AllocCoTaskMem(24);
        Marshal.Copy(new byte[24], 0, source, 24);
        Exception? primary = null;
        try
        {
            var typed = new ComVariantValue(type, scalar);
            ComVariantValue.Write(typed, source);
            using var fixture = new DispatchFixture();
            int calls = 0;
            fixture.Operation = (arguments, count, result) =>
            {
                if (calls++ > 0)
                {
                    Assert.Equal(1u, count);
                    Assert.Equal(type, unchecked((ushort)Marshal.ReadInt16(arguments)));
                    object? received = ComVariantValue.Read(arguments).Value;
                    if (scalar is DateTime date)
                    {
                        Assert.Equal(date.ToOADate(), received);
                    }
                    else if (scalar is ErrorWrapper error)
                    {
                        Assert.Equal(error.ErrorCode, Assert.IsType<ErrorWrapper>(received).ErrorCode);
                    }
                    else
                    {
                        Assert.Equal(scalar, received);
                    }
                    return;
                }

                if (route == "out")
                {
                    Assert.Equal(1u, count);
                    Assert.Equal(0x400c, Marshal.ReadInt16(arguments));
                    nint destination = Marshal.ReadIntPtr(arguments, 8);
                    ComVariantValue.Write(typed, destination);
                }
                else if (route == "variant-byref")
                {
                    Marshal.WriteIntPtr(result, 8, source);
                    Marshal.WriteInt16(result, 0x400c);
                }
                else if (route == "scalar-byref")
                {
                    Marshal.WriteIntPtr(result, 8, type == 14 ? source : source + 8);
                    Marshal.WriteInt16(result, unchecked((short)(type | 0x4000)));
                }
                else
                {
                    ComVariantValue.Write(typed, result);
                }
            };
            string receive = route == "out"
                ? "DIM value\no.Change(VAR value)" : "DIM value=o.Change()";
            List<string> output = [];
            Exception? failure = await Record.ExceptionAsync(() =>
                new Interpreter(windows: new WindowsAutomation(_ => { },
                    comRuntime: new Runtime(fixture))).RunAsync(
                        "DIM o=CREATEOLEOBJ(\"fixture\")\n" + receive
                        + "\nPRINT VARTYPE(value)\nDIM alias=value"
                        + "\no.Change(alias)\nPRINT VARTYPE(value)\nPRINT VARTYPE(alias)"
                        + "\nconverted=VARTYPE(value,VARTYPE(value))"
                        + "\nPRINT VARTYPE(converted)\no.Change(converted)", output.Add));
            Assert.Null(fixture.Failure);
            Assert.Null(failure);
            Assert.Equal(3, calls);
            int reportedType = route == "variant-byref" ? 0x400c
                : route == "scalar-byref" ? 0x4000 | type
                : type == 8 ? 258 : type;
            string expected = reportedType.ToString(CultureInfo.InvariantCulture);
            Assert.Equal([expected, expected, expected, expected], output);
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            ClearVariantTypeSource(source, primary);
        }
    }

    [DllImport("oleaut32.dll", EntryPoint = "VariantClear")]
    private static extern int VariantClearForTypeTest(nint variant);

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task LinkedSortPreservesDistinctNativeArrayReferenceTypes()
    {
        nint storage = Marshal.AllocCoTaskMem(48);
        Marshal.Copy(new byte[48], 0, storage, 48);
        Exception? primary = null;
        try
        {
            ComVariantValue.Write(new ComArrayValue(3, [0], [2],
                [3d, 1d, 2d]), storage);
            ComVariantValue.Write(new ComArrayValue(8, [0], [2],
                ["c", "a", "b"]), storage + 24);
            using var fixture = new DispatchFixture();
            int calls = 0;
            fixture.Operation = (arguments, count, result) =>
            {
                int call = calls++;
                if (call == 2)
                {
                    Assert.Equal(2u, count);
                    nint keyArgument = DispatchFixture.Argument(arguments, 0, count);
                    nint labelArgument = DispatchFixture.Argument(arguments, 1, count);
                    Assert.Equal(0x2003, Marshal.ReadInt16(keyArgument));
                    Assert.Equal(0x2008, Marshal.ReadInt16(labelArgument));
                    Assert.Equal([1, 2, 3], Assert.IsType<int[]>(
                        Marshal.GetObjectForNativeVariant(keyArgument)));
                    Assert.Equal(new[] { "a", "b", "c" }, Assert.IsType<string[]>(
                        Marshal.GetObjectForNativeVariant(labelArgument)));
                    Assert.Equal([3, 1, 2], Assert.IsType<int[]>(
                        Marshal.GetObjectForNativeVariant(storage)));
                    Assert.Equal(new[] { "c", "a", "b" }, Assert.IsType<string[]>(
                        Marshal.GetObjectForNativeVariant(storage + 24)));
                    return;
                }

                bool keys = call == 0;
                Marshal.WriteIntPtr(result, 8, keys ? storage + 8 : storage + 24);
                Marshal.WriteInt16(result, keys ? (short)0x6003 : (short)0x400c);
            };
            List<string> output = [];
            await new Interpreter(windows: new WindowsAutomation(_ => { },
                comRuntime: new Runtime(fixture))).RunAsync("""
                o=CREATEOLEOBJ("fixture")
                keys=o.Change()
                labels=o.Change()
                originalKeys=keys
                originalLabels=labels
                QSORT(keys,0,labels)
                PRINT VARTYPE(keys)
                PRINT VARTYPE(labels)
                PRINT JOIN(keys,",")
                PRINT JOIN(labels,",")
                PRINT JOIN(originalKeys,",")
                PRINT JOIN(originalLabels,",")
                o.Change(keys,labels)
                """, output.Add);
            Assert.Null(fixture.Failure);
            Assert.Equal(3, calls);
            Assert.Equal(["24579", "16396", "1,2,3", "a,b,c",
                "3,1,2", "c,a,b"], output);
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            try
            {
                Marshal.ThrowExceptionForHR(VariantClearForTypeTest(storage + 24));
            }
            finally
            {
                ClearVariantTypeSource(storage, primary);
            }
        }
    }

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task ResizeResendsTypedArrayWithOriginalNegativeLowerBound()
    {
        nint source = Marshal.AllocCoTaskMem(24);
        Marshal.Copy(new byte[24], 0, source, 24);
        Exception? primary = null;
        try
        {
            ComVariantValue.Write(new ComArrayValue(3, [-2], [-1],
                [7d, 9d]), source);
            using var fixture = new DispatchFixture();
            int calls = 0;
            fixture.Operation = (arguments, count, result) =>
            {
                if (calls++ == 0)
                {
                    Marshal.WriteIntPtr(result, 8, source + 8);
                    Marshal.WriteInt16(result, 0x6003);
                    return;
                }

                Assert.Equal(1u, count);
                Assert.Equal(0x2003, Marshal.ReadInt16(arguments));
                Array resized = Assert.IsAssignableFrom<Array>(
                    Marshal.GetObjectForNativeVariant(arguments));
                Assert.Equal(-2, resized.GetLowerBound(0));
                Assert.Equal(1, resized.GetUpperBound(0));
                Assert.Equal([7, 9, 0, 0], resized.Cast<int>());
                Array original = Assert.IsAssignableFrom<Array>(
                    Marshal.GetObjectForNativeVariant(source));
                Assert.Equal(-2, original.GetLowerBound(0));
                Assert.Equal(-1, original.GetUpperBound(0));
                Assert.Equal([7, 9], original.Cast<int>());
            };
            List<string> output = [];
            await new Interpreter(windows: new WindowsAutomation(_ => { },
                comRuntime: new Runtime(fixture))).RunAsync("""
                o=CREATEOLEOBJ("fixture")
                value=o.Change()
                saved=value
                RESIZE(value,1)
                PRINT VARTYPE(value)
                PRINT LENGTH(value)
                PRINT LENGTH(saved)
                o.Change(value)
                """, output.Add);
            Assert.Null(fixture.Failure);
            Assert.Equal(2, calls);
            Assert.Equal(["24579", "4", "2"], output);
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            ClearVariantTypeSource(source, primary);
        }
    }

    private static void ClearVariantTypeSource(nint source, Exception? primary)
    {
        int status = VariantClearForTypeTest(source);
        Marshal.FreeCoTaskMem(source);
        if (status >= 0)
        {
            return;
        }

        Exception cleanup = Marshal.GetExceptionForHR(status)!;
        if (primary is not null)
        {
            primary.Data["VariantTypeTestCleanup"] = cleanup;
        }
        else
        {
            throw cleanup;
        }
    }

    // 配列の入力とNativeの読取りだけを扱い、画面や外部サーバーには接続しません。
    [Theory]
    [InlineData(3, "direct")]
    [InlineData(3, "variant-byref")]
    [InlineData(3, "array-byref")]
    [InlineData(3, "out")]
    [InlineData(8, "direct")]
    [InlineData(8, "variant-byref")]
    [InlineData(8, "array-byref")]
    [InlineData(8, "out")]
    [InlineData(12, "direct")]
    [InlineData(12, "variant-byref")]
    [InlineData(12, "array-byref")]
    [InlineData(12, "out")]
    [InlineData(17, "direct")]
    [InlineData(17, "variant-byref")]
    [InlineData(17, "array-byref")]
    [InlineData(17, "out")]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task VariantTypeRetainsArrayTagsAndBoundsAcrossNativeRoutes(
        int type, string route)
    {
        object? element = type == 8 ? "日本語" : 123d;
        var array = new ComArrayValue((ushort)type, [-2, 5], [-1, 6],
            [element, element, element, element]);
        nint source = Marshal.AllocCoTaskMem(24);
        Marshal.Copy(new byte[24], 0, source, 24);
        Exception? primary = null;
        try
        {
            ComVariantValue.Write(array, source);
            using var fixture = new DispatchFixture();
            int calls = 0;
            fixture.Operation = (arguments, count, result) =>
            {
                if (calls++ > 0)
                {
                    Assert.Equal(1u, count);
                    Assert.Equal(8192 | type, Marshal.ReadInt16(arguments));
                    Array received = Assert.IsAssignableFrom<Array>(Marshal.GetObjectForNativeVariant(arguments));
                    Assert.Equal(2, received.Rank);
                    Assert.Equal(-2, received.GetLowerBound(0));
                    Assert.Equal(5, received.GetLowerBound(1));
                    Assert.Equal(-1, received.GetUpperBound(0));
                    Assert.Equal(6, received.GetUpperBound(1));
                    Assert.Equal(type == 8 ? "日本語" : "123",
                        Convert.ToString(received.GetValue(-2, 5), CultureInfo.InvariantCulture));
                    return;
                }

                if (route == "out")
                {
                    Assert.Equal(0x400c, Marshal.ReadInt16(arguments));
                    ComVariantValue.Write(array, Marshal.ReadIntPtr(arguments, 8));
                }
                else if (route is "variant-byref" or "array-byref")
                {
                    Marshal.WriteIntPtr(result, 8, route == "variant-byref" ? source : source + 8);
                    Marshal.WriteInt16(result, route == "variant-byref"
                        ? (short)0x400c : unchecked((short)(0x6000 | type)));
                }
                else
                {
                    ComVariantValue.Write(array, result);
                }
            };
            string receive = route == "out"
                ? "DIM value\no.Change(VAR value)" : "DIM value=o.Change()";
            List<string> output = [];
            Exception? failure = await Record.ExceptionAsync(() =>
                new Interpreter(windows: new WindowsAutomation(_ => { },
                    comRuntime: new Runtime(fixture))).RunAsync(
                        "DIM o=CREATEOLEOBJ(\"fixture\")\n" + receive
                        + "\nPRINT VARTYPE(value)\nPRINT VARTYPE(value[-2,5])"
                        + "\nDIM alias=value\no.Change(alias)\nPRINT VARTYPE(alias)"
                        + "\nconverted=VARTYPE(value,VARTYPE(value))"
                        + "\nPRINT VARTYPE(converted)\no.Change(converted)", output.Add));
            Assert.Null(fixture.Failure);
            Assert.Null(failure);
            Assert.Equal(3, calls);
            int reportedType = route == "variant-byref" ? 0x400c
                : route == "array-byref" && type != 12 ? 0x6000 | type : 8192 | type;
            string expected = reportedType.ToString(CultureInfo.InvariantCulture);
            string elementType = (type == 8 ? 258 : type == 12 ? 5 : type)
                .ToString(CultureInfo.InvariantCulture);
            Assert.Equal([expected, elementType, expected, expected], output);
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            ClearVariantTypeSource(source, primary);
        }
    }

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task VariantTypeCoercesDefaultMemberWithoutChangingTheObject()
    {
        using var fixture = new DispatchFixture { MemberId = 0 };
        int calls = 0;
        fixture.Operation = (_, count, result) =>
        {
            calls++;
            Assert.Equal(0u, count);
            ComVariantValue.Write(new ComVariantValue(3, 42), result);
        };
        List<string> output = [];
        Exception? failure = await Record.ExceptionAsync(() =>
            new Interpreter(windows: new WindowsAutomation(_ => { },
                comRuntime: new Runtime(fixture))).RunAsync("""
                    source=CREATEOLEOBJ("fixture")
                    converted=VARTYPE(source,VAR_INTEGER)
                    PRINT converted
                    PRINT VARTYPE(converted)
                    PRINT VARTYPE(source)
                    """, output.Add));
        Assert.Null(fixture.Failure);
        Assert.Null(failure);
        Assert.Equal(1, calls);
        Assert.Equal(["42", "3", "9"], output);
    }

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task VariantTypeDefaultMemberFailureIsCatchableAndKeepsTheObject()
    {
        using var fixture = new DispatchFixture
        {
            MemberId = 0,
            HResult = unchecked((int)0x80020009)
        };
        fixture.Operation = (_, _, _) => { };
        List<string> output = [];
        Exception? failure = await Record.ExceptionAsync(() =>
            new Interpreter(windows: new WindowsAutomation(_ => { },
                comRuntime: new Runtime(fixture))).RunAsync("""
                    source=CREATEOLEOBJ("fixture")
                    TRY
                        VARTYPE(source,VAR_INTEGER)
                    EXCEPT
                        PRINT "caught"
                    ENDTRY
                    PRINT VARTYPE(source)
                    """, output.Add));
        Assert.Null(fixture.Failure);
        Assert.Null(failure);
        Assert.Equal(["caught", "9"], output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task VariantTypeReferenceTagsSurviveAliasesAndExplicitConversion(bool variant)
    {
        nint source = Marshal.AllocCoTaskMem(24);
        Marshal.Copy(new byte[24], 0, source, 24);
        Exception? primary = null;
        try
        {
            ComVariantValue.Write(new ComVariantValue(3, 123), source);
            using var fixture = new DispatchFixture();
            fixture.Operation = (_, _, result) =>
            {
                Marshal.WriteIntPtr(result, 8, variant ? source : source + 8);
                Marshal.WriteInt16(result, variant ? (short)0x400c : (short)0x4003);
            };
            List<string> output = [];
            Exception? failure = await Record.ExceptionAsync(() =>
                new Interpreter(windows: new WindowsAutomation(_ => { },
                    comRuntime: new Runtime(fixture))).RunAsync("""
                        o=CREATEOLEOBJ("fixture")
                        value=o.Change()
                        alias=value
                        PRINT VARTYPE(value)
                        PRINT VARTYPE(alias)
                        Report(value)
                        converted=VARTYPE(value,VAR_INTEGER)
                        PRINT VARTYPE(converted)
                        PRINT converted
                        value=value+1
                        PRINT VARTYPE(value)
                        PRINT VARTYPE(alias)
                        PROCEDURE Report(argument)
                            PRINT VARTYPE(argument)
                        FEND
                        """, output.Add));
            Assert.Null(fixture.Failure);
            Assert.Null(failure);
            string expected = (variant ? 0x400c : 0x4003).ToString(CultureInfo.InvariantCulture);
            Assert.Equal([expected, expected, expected, "3", "123", "5", expected], output);
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            ClearVariantTypeSource(source, primary);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task VariantTypeReferenceTagsSurviveArrayElementAndModuleUpdates(bool variant)
    {
        nint source = Marshal.AllocCoTaskMem(24);
        Marshal.Copy(new byte[24], 0, source, 24);
        Exception? primary = null;
        try
        {
            ComVariantValue.Write(new ComArrayValue(3, [-2, 5], [-1, 6],
                [123d, 123d, 123d, 123d]), source);
            using var fixture = new DispatchFixture();
            fixture.Operation = (_, _, result) =>
            {
                Marshal.WriteIntPtr(result, 8, variant ? source : source + 8);
                Marshal.WriteInt16(result, variant ? (short)0x400c : (short)0x6003);
            };
            List<string> output = [];
            Exception? failure = await Record.ExceptionAsync(() =>
                new Interpreter(windows: new WindowsAutomation(_ => { },
                    comRuntime: new Runtime(fixture))).RunAsync("""
                        o=CREATEOLEOBJ("fixture")
                        value=o.Change()
                        Box.data=value
                        value[-2,5]=7
                        Box.data[-1,6]=9
                        PRINT VARTYPE(value)
                        PRINT VARTYPE(Box.data)
                        PRINT value[-2,5]
                        PRINT Box.data[-2,5]
                        PRINT Box.data[-1,6]
                        PRINT value[-1,6]
                        MODULE Box
                            PUBLIC data
                        ENDMODULE
                        """, output.Add));
            Assert.Null(fixture.Failure);
            Assert.Null(failure);
            string expected = (variant ? 0x400c : 0x6003).ToString(CultureInfo.InvariantCulture);
            Assert.Equal([expected, expected, "7", "123", "9", "123"], output);
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            ClearVariantTypeSource(source, primary);
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(8195)]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task VariantTypeReferenceMetadataDoesNotBorrowTheNativeStorage(int type)
    {
        nint source = Marshal.AllocCoTaskMem(24);
        Marshal.Copy(new byte[24], 0, source, 24);
        Exception? primary = null;
        try
        {
            if (type == 8195)
            {
                ComVariantValue.Write(new ComArrayValue(3, [-2], [-1], [123d, 456d]), source);
            }
            else
            {
                ComVariantValue.Write(new ComVariantValue((ushort)type,
                    type == 8 ? "日本語" : 123), source);
            }

            using var fixture = new DispatchFixture();
            fixture.Operation = (_, _, result) =>
            {
                Marshal.WriteIntPtr(result, 8, source);
                Marshal.WriteInt16(result, 0x400c);
            };
            await using var session = new ComSession(new Runtime(fixture));
            Value target = await session.CreateAsync("fixture", false, default);
            Value received = await session.InvokeAsync(target, "Change", [], null, default);
            nint owned = source;
            source = 0;
            ClearVariantTypeSource(owned, null);
            Assert.Equal(0x400c, VariantTypeRequest.Query(received).Number);
            if (type == 8195)
            {
                Assert.Equal(123, received.AsArray().Get(-2).Number);
            }
            else
            {
                Value converted = await session.ConvertVariantAsync(received,
                    type == 8 ? (ushort)258 : (ushort)3, default);
                Assert.Equal(type == 8 ? "日本語" : "123", new PrintFormatter().Format(converted));
                Assert.Equal(type == 8 ? 258 : 3, VariantTypeRequest.Query(converted).Number);
            }

            Assert.Null(fixture.Failure);
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            if (source != 0)
            {
                ClearVariantTypeSource(source, primary);
            }
        }
    }

    [Theory]
    [InlineData(9, "direct")]
    [InlineData(9, "variant-byref")]
    [InlineData(9, "interface-byref")]
    [InlineData(13, "direct")]
    [InlineData(13, "variant-byref")]
    [InlineData(13, "interface-byref")]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task VariantTypeReferenceMetadataKeepsOwnedInterfaceIdentity(int type, string route)
    {
        nint source = Marshal.AllocCoTaskMem(24);
        Marshal.Copy(new byte[24], 0, source, 24);
        Exception? primary = null;
        try
        {
            using var fixture = new DispatchFixture();
            await using var session = new ComSession(new Runtime(fixture));
            Value root = await session.CreateAsync("fixture", false, default);
            Marshal.AddRef(fixture.Pointer);
            Marshal.WriteIntPtr(source, 8, fixture.Pointer);
            Marshal.WriteInt16(source, (short)type);

            fixture.Operation = (_, _, result) =>
            {
                if (route == "direct")
                {
                    Marshal.AddRef(Marshal.ReadIntPtr(source, 8));
                    Marshal.WriteIntPtr(result, 8, Marshal.ReadIntPtr(source, 8));
                    Marshal.WriteInt16(result, (short)type);
                }
                else
                {
                    Marshal.WriteIntPtr(result, 8, route == "variant-byref" ? source : source + 8);
                    Marshal.WriteInt16(result, route == "variant-byref"
                        ? (short)0x400c : unchecked((short)(0x4000 | type)));
                }
            };
            Value received = await session.InvokeAsync(root, "Change", [], null, default);
            nint owned = source;
            source = 0;
            ClearVariantTypeSource(owned, null);
            int expected = route == "variant-byref" ? 0x400c
                : route == "interface-byref" ? 0x4000 | type : type;
            Assert.Equal(expected, VariantTypeRequest.Query(received).Number);
            Assert.Equal(root, received);
            Value converted = await session.ConvertVariantAsync(received, 9, default);
            Assert.Equal(9, VariantTypeRequest.Query(converted).Number);
            Assert.Equal(root, converted);
            Assert.Equal(expected, VariantTypeRequest.Query(received).Number);
            Assert.Null(fixture.Failure);
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            if (source != 0)
            {
                ClearVariantTypeSource(source, primary);
            }
        }
    }
}
