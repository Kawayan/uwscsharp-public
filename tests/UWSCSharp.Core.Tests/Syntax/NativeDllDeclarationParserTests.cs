using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class NativeDllDeclarationParserTests
{
    [Fact]
    public void DeclarationRetainsTypesAndOriginalExportSpelling()
    {
        NativeDllDeclaration declaration = NativeDllDeclarationParser.Parse(
            " QueryValue (VAR int[], {byte,{long,double}}):BOOL:fixture.dll ", 4);
        Assert.Equal("QueryValue", declaration.Name);
        Assert.Equal(2, declaration.Parameters.Count);
        Assert.True(declaration.Parameters[0].IsByReference);
        Assert.True(declaration.Parameters[0].IsArray);
        Assert.IsType<NativeDllStructureType>(declaration.Parameters[1]);
        Assert.Equal(NativeDllPrimitive.Bool,
            Assert.IsType<NativeDllScalarType>(declaration.ReturnType).Primitive);
        Assert.Equal("fixture.dll", declaration.LibraryText);
        Assert.False(declaration.Packed);
    }

    [Theory]
    [InlineData("Read():fixture.dll", "fixture.dll", false)]
    [InlineData("Read():fixture.dll PACK", "fixture.dll", true)]
    [InlineData("Read():fixture.dll:pAcK", "fixture.dll", true)]
    [InlineData("Read():C:\\folder\\fixture.dll", "C:\\folder\\fixture.dll", false)]
    [InlineData("Read():C:/folder/fixture.dll PACK", "C:/folder/fixture.dll", true)]
    [InlineData("Read():\"C:\\folder PACK\\fixture.dll\"", "\"C:\\folder PACK\\fixture.dll\"", false)]
    [InlineData("Read():\"fixture PACK\"", "\"fixture PACK\"", false)]
    [InlineData("Read():'fixture:PACK'", "'fixture:PACK'", false)]
    [InlineData("Read():\"fixture\"\" PACK\" :PACK", "\"fixture\"\" PACK\"", true)]
    [InlineData("Read():backpack", "backpack", false)]
    public void OmittedReturnAndLibraryTextArePreserved(
        string text, string library, bool packed)
    {
        NativeDllDeclaration declaration = NativeDllDeclarationParser.Parse(text, 1);
        Assert.Null(declaration.ReturnType);
        Assert.Empty(declaration.Parameters);
        Assert.Equal(library, declaration.LibraryText);
        Assert.Equal(packed, declaration.Packed);
    }

    [Fact]
    public void ExplicitVoidIsDifferentFromOmittedReturn()
    {
        NativeDllDeclaration declaration = NativeDllDeclarationParser.Parse(
            "Read():VOID:C:\\folder\\fixture.dll:PACK", 1);
        Assert.Equal(NativeDllPrimitive.Void,
            Assert.IsType<NativeDllScalarType>(declaration.ReturnType).Primitive);
        Assert.Equal("C:\\folder\\fixture.dll", declaration.LibraryText);
        Assert.True(declaration.Packed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Read:")]
    [InlineData("(int):fixture.dll")]
    [InlineData("Read(int:fixture.dll")]
    [InlineData("Read(int,,byte):fixture.dll")]
    [InlineData("Read()fixture.dll")]
    [InlineData("Read():")]
    [InlineData("Read():int:")]
    [InlineData("Read():int::PACK")]
    [InlineData("Read():unknown:fixture.dll")]
    [InlineData("Read():\"fixture.dll")]
    [InlineData("Read():'fixture.dll")]
    [InlineData("1Read():fixture.dll")]
    [InlineData("IF():fixture.dll")]
    public void InvalidShapeFailsOnDeclarationLine(string text)
    {
        ScriptException error = Assert.Throws<ScriptException>(() =>
            NativeDllDeclarationParser.Parse(text, 29));
        Assert.Equal(29, error.Line);
    }

    [Fact]
    public void AlreadyCancelledDeclarationDoesNotParse()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            NativeDllDeclarationParser.Parse("Read():fixture.dll", 1, source.Token));
    }

    [Fact]
    public void ModelOwnsParameterListAndPackIsNotGloballyReserved()
    {
        NativeDllType[] types = [new NativeDllScalarType(NativeDllPrimitive.Int)];
        var declaration = new NativeDllDeclaration("Read", types, null, "fixture", false);
        types[0] = new NativeDllScalarType(NativeDllPrimitive.Double);
        Assert.Equal(NativeDllPrimitive.Int,
            Assert.IsType<NativeDllScalarType>(declaration.Parameters[0]).Primitive);
        Assert.True(Names.CanDeclare("PACK"));
    }
}
