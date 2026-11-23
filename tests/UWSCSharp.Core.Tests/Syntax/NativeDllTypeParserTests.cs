using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class NativeDllTypeParserTests
{
    [Fact]
    public void NestedFieldsDoNotSplitOuterParameters()
    {
        IReadOnlyList<NativeDllType> types = NativeDllTypeParser.ParseList(
            "VaR int[ ], {BYTE,DOUBLE,{long,Wstring}},pchar", 12);
        Assert.Equal(3, types.Count);
        NativeDllScalarType array = Assert.IsType<NativeDllScalarType>(types[0]);
        Assert.Equal(NativeDllPrimitive.Int, array.Primitive);
        Assert.True(array.IsByReference);
        Assert.True(array.IsArray);
        NativeDllStructureType outer = Assert.IsType<NativeDllStructureType>(types[1]);
        Assert.False(outer.IsByReference);
        Assert.False(outer.IsArray);
        Assert.Equal(3, outer.Fields.Count);
        NativeDllStructureType inner = Assert.IsType<NativeDllStructureType>(outer.Fields[2]);
        Assert.Equal(NativeDllPrimitive.Long, Assert.IsType<NativeDllScalarType>(inner.Fields[0]).Primitive);
        Assert.Equal(NativeDllPrimitive.Wstring, Assert.IsType<NativeDllScalarType>(inner.Fields[1]).Primitive);
        Assert.Equal(NativeDllPrimitive.Pchar, Assert.IsType<NativeDllScalarType>(types[2]).Primitive);
    }

    [Fact]
    public void AliasesRetainTheirOwnIdentityBeforeAbiConversion()
    {
        IReadOnlyList<NativeDllType> types = NativeDllTypeParser.ParseList(
            "INT,LONG,BOOL,DWORD,UINT,HWND,BYTE,BOOLEAN", 1);
        NativeDllPrimitive[] expected =
        [
            NativeDllPrimitive.Int, NativeDllPrimitive.Long, NativeDllPrimitive.Bool,
            NativeDllPrimitive.Dword, NativeDllPrimitive.Uint, NativeDllPrimitive.Hwnd,
            NativeDllPrimitive.Byte, NativeDllPrimitive.Boolean
        ];
        Assert.Equal(expected, types.Select(type => Assert.IsType<NativeDllScalarType>(type).Primitive));
    }

    [Fact]
    public void ArrayAndReferenceFlagsAreIndependentAtEachLevel()
    {
        NativeDllStructureType type = Assert.IsType<NativeDllStructureType>(
            NativeDllTypeParser.Parse("VAR {char, var double[]}[]", 1));
        Assert.True(type.IsArray);
        Assert.True(type.IsByReference);
        NativeDllScalarType first = Assert.IsType<NativeDllScalarType>(type.Fields[0]);
        Assert.False(first.IsArray);
        Assert.False(first.IsByReference);
        NativeDllScalarType second = Assert.IsType<NativeDllScalarType>(type.Fields[1]);
        Assert.True(second.IsArray);
        Assert.True(second.IsByReference);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{int,}")]
    [InlineData("{,int}")]
    [InlineData("{int")]
    [InlineData("int]")]
    [InlineData("int[1]")]
    [InlineData("int[][]")]
    [InlineData("var")]
    [InlineData("VAR VAR INT")]
    [InlineData("int double")]
    [InlineData("{int} tail")]
    [InlineData("[int]")]
    [InlineData("{int,,double}")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("23")]
    [InlineData("+0")]
    [InlineData("INT_PTR")]
    public void InvalidShapeOrUnknownNameIsRejected(string source)
    {
        ScriptException error = Assert.Throws<ScriptException>(() => NativeDllTypeParser.Parse(source, 37));
        Assert.Equal(37, error.Line);
        Assert.Contains("DEF_DLL", error.Message);
    }

    [Theory]
    [InlineData("int,,double")]
    [InlineData(",int")]
    [InlineData("int,")]
    [InlineData("int double")]
    [InlineData("int)")]
    [InlineData("{int},")]
    public void ListDoesNotDropMissingOrTrailingParameters(string source)
    {
        Assert.Throws<ScriptException>(() => NativeDllTypeParser.ParseList(source, 1));
    }

    [Fact]
    public void EmptyParameterListIsDifferentFromEmptyType()
    {
        Assert.Empty(NativeDllTypeParser.ParseList(" \t", 1));
        Assert.Throws<ScriptException>(() => NativeDllTypeParser.Parse(" \t", 1));
    }

    [Fact]
    public void DiagnosticRetainsSourceLineAndTypeOffset()
    {
        ScriptException error = Assert.Throws<ScriptException>(() => NativeDllTypeParser.Parse("int[2]", 37));
        Assert.Equal(37, error.Line);
        Assert.Contains("5文字目", error.Message);
    }

    [Fact]
    public void NestingUsesExistingCompilerLimit()
    {
        int depth = ScriptLimits.MaxExpressionNesting - 1;
        string accepted = new string('{', depth) + "int" + new string('}', depth);
        Assert.IsType<NativeDllStructureType>(NativeDllTypeParser.Parse(accepted, 1));
        Assert.Throws<ScriptException>(() => NativeDllTypeParser.Parse("{" + accepted + "}", 1));
    }

    [Fact]
    public void CancellationIsObservedEvenForEmptyParameterLists()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => NativeDllTypeParser.Parse("int", 1, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => NativeDllTypeParser.ParseList("", 1, cancellation.Token));
    }

    [Fact]
    public void StructureOwnsReadOnlyCopyOfFields()
    {
        NativeDllType[] fields = [new NativeDllScalarType(NativeDllPrimitive.Int)];
        var structure = new NativeDllStructureType(fields);
        fields[0] = new NativeDllScalarType(NativeDllPrimitive.Double);
        Assert.Equal(NativeDllPrimitive.Int, Assert.IsType<NativeDllScalarType>(structure.Fields[0]).Primitive);
        IList<NativeDllType> list = Assert.IsAssignableFrom<IList<NativeDllType>>(structure.Fields);
        Assert.Throws<NotSupportedException>(() => list[0] = fields[0]);
    }
}
