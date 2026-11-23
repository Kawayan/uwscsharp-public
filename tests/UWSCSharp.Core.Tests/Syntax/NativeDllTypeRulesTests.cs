using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class NativeDllTypeRulesTests
{
    [Theory]
    [InlineData("STRING")]
    [InlineData("PCHAR")]
    [InlineData("PBYTE")]
    [InlineData("WSTRING")]
    [InlineData("PWCHAR")]
    [InlineData("CHAR")]
    [InlineData("WCHAR")]
    [InlineData("VOID")]
    [InlineData("VARIANT")]
    [InlineData("SAFEARRAY")]
    public void UnsupportedParameterArraysFailAtTheField(string name)
    {
        foreach (string prefix in new[] { "", "VAR " })
        {
            ScriptException error = Assert.Throws<ScriptException>(() =>
                NativeDllDeclarationParser.Parse(
                    $"Read(int,{{byte,{prefix}{name}[ ]}}):fixture", 19));
            Assert.Equal(19, error.Line);
            Assert.Contains("引数2のフィールド2", error.Message);
        }
    }

    [Theory]
    [InlineData("INT")]
    [InlineData("LONG")]
    [InlineData("BOOL")]
    [InlineData("UINT")]
    [InlineData("DWORD")]
    [InlineData("HWND")]
    [InlineData("HMENU")]
    [InlineData("FLOAT")]
    [InlineData("DOUBLE")]
    [InlineData("WORD")]
    [InlineData("BYTE")]
    [InlineData("BOOLEAN")]
    [InlineData("LONGLONG")]
    [InlineData("EXTENDED")]
    public void NumericArrayShapeIsNotRejectedByKnownArrayRules(string name)
    {
        NativeDllDeclaration declaration = NativeDllDeclarationParser.Parse(
            $"Read({name}[],VAR {name}[]):fixture", 1);
        Assert.True(declaration.Parameters[0].IsArray);
        Assert.True(declaration.Parameters[1].IsArray);
        Assert.True(declaration.Parameters[1].IsByReference);
    }

    [Theory]
    [InlineData("Read(VAR CHAR):fixture", "引数1")]
    [InlineData("Read(VAR WCHAR):fixture", "引数1")]
    [InlineData("Read(VAR VOID):fixture", "引数1")]
    [InlineData("Read(int, VAR CHAR[]):fixture", "引数2")]
    [InlineData("Read({int,{byte,VAR WCHAR}}):fixture", "引数1のフィールド2のフィールド2")]
    [InlineData("Read():VAR VOID:fixture", "戻り型")]
    [InlineData("Read():VAR CHAR:fixture", "戻り型")]
    [InlineData("Read():VAR WCHAR:fixture", "戻り型")]
    public void KnownInvalidModifiersAreRejectedBeforeExecution(
        string text, string location)
    {
        ScriptException error = Assert.Throws<ScriptException>(() =>
            NativeDllDeclarationParser.Parse(text, 41));
        Assert.Equal(41, error.Line);
        Assert.Contains(location, error.Message);
        Assert.Contains("VAR", error.Message);
    }

    [Theory]
    [InlineData("Read(CHAR,WCHAR):VOID:fixture")]
    [InlineData("Read(VAR BYTE,VAR BOOLEAN):fixture")]
    [InlineData("Read(VAR PCHAR,VAR PWCHAR,VAR STRING):fixture")]
    [InlineData("Read({CHAR,WCHAR}):fixture")]
    public void DistinctAliasesAreNotConflated(string text)
    {
        NativeDllDeclaration declaration = NativeDllDeclarationParser.Parse(text, 1);
        Assert.NotEmpty(declaration.Parameters);
    }

    [Theory]
    [InlineData("Read(VAR {int,int}):fixture", "引数1")]
    [InlineData("Read(VAR {CHAR,WCHAR}):fixture", "引数1")]
    [InlineData("Read(int,VAR {byte,double}):fixture PACK", "引数2")]
    [InlineData("Read({byte,VAR {int,int},byte}):fixture", "引数1のフィールド2")]
    [InlineData("Read({{byte,VAR {int}}}):fixture", "引数1のフィールド1のフィールド2")]
    [InlineData("Read(VAR {int}[]):fixture", "引数1")]
    public void StructureReferenceIsRejectedAtItsOwnLocation(
        string text, string location)
    {
        ScriptException error = Assert.Throws<ScriptException>(() =>
            NativeDllDeclarationParser.Parse(text, 41));
        Assert.Equal(41, error.Line);
        Assert.Contains(location, error.Message);
        Assert.Contains("構造体にVAR", error.Message);
    }

    [Theory]
    [InlineData("Read({int,int}[]):fixture", "引数1")]
    [InlineData("Read({int,int}[ ]):fixture", "引数1")]
    [InlineData("Read({byte,{int,int}[],byte}):fixture", "引数1のフィールド2")]
    [InlineData("Read(int,{byte,double}[]):fixture PACK", "引数2")]
    public void StructureArrayIsRejectedAtItsOwnLocation(
        string text, string location)
    {
        ScriptException error = Assert.Throws<ScriptException>(() =>
            NativeDllDeclarationParser.Parse(text, 41));
        Assert.Equal(41, error.Line);
        Assert.Contains(location, error.Message);
        Assert.Contains("構造体に配列", error.Message);
    }

    [Fact]
    public void StructureArrayFragmentCannotBypassDeclarationRules()
    {
        NativeDllType type = NativeDllTypeParser.Parse("{int,int}[]", 1);
        Assert.True(type.IsArray);
        var declaration = new NativeDllDeclaration("Read", [type], null, "fixture", false);
        Assert.Throws<ScriptException>(() =>
            NativeDllTypeRules.ValidateKnownConstraints(declaration, 1));
    }

    [Fact]
    public void StructureFragmentRetainsModifierButCannotBypassDeclarationRules()
    {
        NativeDllType type = NativeDllTypeParser.Parse("VAR {int,int}", 1);
        Assert.True(type.IsByReference);
        var declaration = new NativeDllDeclaration("Read", [type], null, "fixture", false);
        Assert.Throws<ScriptException>(() =>
            NativeDllTypeRules.ValidateKnownConstraints(declaration, 1));
    }

    [Fact]
    public void FragmentParsingDoesNotClaimSemanticValidity()
    {
        NativeDllType type = NativeDllTypeParser.Parse("VAR CHAR", 1);
        Assert.True(type.IsByReference);
        var declaration = new NativeDllDeclaration("Read", [type], null, "fixture", false);
        Assert.Throws<ScriptException>(() =>
            NativeDllTypeRules.ValidateKnownConstraints(declaration, 1));
    }

    [Fact]
    public void PrecancelledEmptyDeclarationIsRejected()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var declaration = new NativeDllDeclaration("Read", [], null, "fixture", false);
        Assert.Throws<OperationCanceledException>(() =>
            NativeDllTypeRules.ValidateKnownConstraints(declaration, 1, source.Token));
    }

    [Fact]
    public void DirectlyConstructedModelCannotBypassNestingLimit()
    {
        NativeDllType type = new NativeDllScalarType(NativeDllPrimitive.Int);
        for (int index = 0; index < ScriptLimits.MaxExpressionNesting; index++)
        {
            type = new NativeDllStructureType([type]);
        }

        var declaration = new NativeDllDeclaration("Read", [type], null, "fixture", false);
        Assert.Throws<ScriptException>(() =>
            NativeDllTypeRules.ValidateKnownConstraints(declaration, 1));
    }
}
