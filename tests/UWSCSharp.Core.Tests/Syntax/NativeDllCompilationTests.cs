using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class NativeDllCompilationTests
{
    [Theory]
    [InlineData("int[]", true)]
    [InlineData("string[]", true)]
    [InlineData("VAR int[]", false)]
    [InlineData("{int}", false)]
    [InlineData("{int}[]", false)]
    public void ReturnArrayMarkerUsesScalarSupport(string type, bool supported)
    {
        string source = $"DEF_DLL ReadValue():{type}:fixture.dll\nPRINT ReadValue()";
        ScriptProgram program = ScriptCompiler.CompileWithoutExternalFiles(source);
        NativeDllDeclaration declaration = Assert.Single(
            program.NativeDllDeclarations).Declaration;

        Assert.Equal(supported, NativeDllScalarInput.Supports(declaration));
        Assert.Equal(supported, ScriptCallAnalyzer.Analyze(source).Diagnostics.Count == 0);
    }

    [Fact]
    public void CompilerRetainsDeclarationShapeAndSourceLocation()
    {
        ScriptProgram program = ScriptCompiler.CompileWithoutExternalFiles("""
            PRINT 1
            dEf_DlL ReadValue(VAR int[], {byte,double}):"C:\work\fixture.dll" PACK // 注釈
            """);
        NativeDllDeclarationStatement statement =
            Assert.IsType<NativeDllDeclarationStatement>(program.Main[1]);
        Assert.Equal(2, statement.Line);
        Assert.Equal("ReadValue", statement.Declaration.Name);
        Assert.Equal(2, statement.Declaration.Parameters.Count);
        Assert.Null(statement.Declaration.ReturnType);
        Assert.Equal(@"""C:\work\fixture.dll""", statement.Declaration.LibraryText);
        Assert.True(statement.Declaration.Packed);
        Assert.NotNull(statement.SourcePath);
    }

    [Fact]
    public void CompilerKeepsRedeclarationsInTheirExecutionOrder()
    {
        ScriptProgram program = ScriptCompiler.Compile("""
            DEF_DLL ReadValue():long:first.dll
            IFB TRUE
                DEF_DLL ReadValue():long:second.dll
            ENDIF
            PROCEDURE Prepare()
                DEF_DLL ReadValue():long:third.dll
            FEND
            """);

        Assert.Equal("first.dll", Assert.IsType<NativeDllDeclarationStatement>(
            program.Main[0]).Declaration.LibraryText);
        IfStatement block = Assert.IsType<IfStatement>(program.Main[1]);
        Assert.Equal("second.dll", Assert.IsType<NativeDllDeclarationStatement>(
            Assert.Single(Assert.Single(block.Branches).Body)).Declaration.LibraryText);
        Assert.Equal("third.dll", Assert.IsType<NativeDllDeclarationStatement>(
            Assert.Single(program.Routines["Prepare"].Body)).Declaration.LibraryText);
        Assert.Equal(["first.dll", "second.dll", "third.dll"],
            program.NativeDllDeclarations.Select(item => item.Declaration.LibraryText));
    }

    [Theory]
    [InlineData("DEF_DLL ReadValue(VAR char):int:fixture.dll", "DEF_DLL")]
    [InlineData("DEF_DLL ReadValue():int:", "DEF_DLL")]
    [InlineData("DEF_DLL ReadValue():\"fixture.dll", "文字列が閉じていません")]
    public void CompilerReportsInvalidDeclarationsAtTheirOwnLine(
        string declaration, string message)
    {
        ScriptException error = Assert.Throws<ScriptException>(() =>
            ScriptCompiler.Compile("PRINT 1\n" + declaration));
        Assert.Equal(2, error.Line);
        Assert.Contains(message, error.Message);
    }

    [Theory]
    [InlineData("VAR {int,int}", "構造体にVAR")]
    [InlineData("{byte,VAR {int,int},byte}", "構造体にVAR")]
    [InlineData("{int,int}[]", "構造体に配列")]
    [InlineData("{byte,{int,int}[],byte}", "構造体に配列")]
    public async Task InvalidStructureModifierFailsBeforeExecutionAndCallAnalysis(
        string type, string message)
    {
        string source = $"PRINT 99\nDEF_DLL ReadValue({type}):int:fixture.dll";
        var output = new List<string>();
        ScriptException runtime = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync(source, output.Add));
        ScriptException analysis = Assert.Throws<ScriptException>(() =>
            ScriptCallAnalyzer.Analyze(source));
        Assert.Equal(2, runtime.Line);
        Assert.Equal(2, analysis.Line);
        Assert.Contains(message, runtime.Message);
        Assert.Empty(output);
    }

    [Fact]
    public void InvalidModuleLevelDeclarationReportsItsOwnLine()
    {
        ScriptException error = Assert.Throws<ScriptException>(() =>
            ScriptCompiler.Compile("""
                MODULE Storage
                    DEF_DLL ReadValue():int:
                ENDMODULE
                """));
        Assert.Equal(2, error.Line);
        Assert.Contains("DEF_DLL", error.Message);
    }

    [Theory]
    [InlineData("MODULE", "ENDMODULE")]
    [InlineData("CLASS", "ENDCLASS")]
    public async Task ModuleDeclarationsUseTheirOwnNamespace(
        string opening, string closing)
    {
        string source = $$"""
            DEF_DLL ReadValue():global.dll
            TRY
                PRINT Storage.readvalue()
            EXCEPT
                PRINT TRY_ERRMSG
            ENDTRY
            PRINT Storage.ReplaceAndRead()
            TRY
                PRINT ReadValue()
            EXCEPT
                PRINT TRY_ERRMSG
            ENDTRY
            TRY
                PRINT PrivateExport()
            EXCEPT
                PRINT TRY_ERRMSG
            ENDTRY
            {{opening}} Storage
                DEF_DLL READVALUE():module.dll
                DEF_DLL PrivateExport():module.dll
                FUNCTION ReplaceAndRead()
                    DEF_DLL readValue():replacement.dll
                    TRY
                        RESULT=ReadValue()
                    EXCEPT
                        RESULT=TRY_ERRMSG
                    ENDTRY
                FEND
            {{closing}}
            """;
        ScriptProgram program = ScriptCompiler.Compile(source);
        Assert.Single(program.NativeDllDeclarations);
        Assert.Equal(3, Assert.Single(program.Modules).NativeDllDeclarations.Count);
        var output = new List<string>();
        await new Interpreter().RunAsync(program, new CallbackScriptHost(output.Add));

        Assert.Collection(output,
            initial => Assert.EndsWith(": READVALUE", initial),
            updated => Assert.EndsWith(": readValue", updated),
            global => Assert.EndsWith(": ReadValue", global),
            hidden => Assert.Contains("未定義の関数: PrivateExport", hidden));
        Assert.Equal(["unknown-function"],
            ScriptCallAnalyzer.Analyze(source).Diagnostics.Select(item => item.Code));
    }

    [Fact]
    public async Task FirstDeclarationIsVisibleBeforeExecutionAndOnlyReachedUpdatesApply()
    {
        var output = new List<string>();
        await new Interpreter().RunAsync("""
            TRY
                PRINT readvalue()
            EXCEPT
                PRINT TRY_ERRMSG
            ENDTRY
            IF FALSE THEN DEF_DLL ReadValue():missing.dll
            IF FALSE THEN DEF_DLL readValue():skipped.dll
            TRY
                PRINT readvalue()
            EXCEPT
                PRINT TRY_ERRMSG
            ENDTRY
            DEF_DLL READVALUE():replacement.dll
            TRY
                PRINT ReadValue()
            EXCEPT
                PRINT TRY_ERRMSG
            ENDTRY
            """, output.Add);

        Assert.Collection(output,
            first => Assert.EndsWith(": ReadValue", first, StringComparison.Ordinal),
            skipped => Assert.EndsWith(": ReadValue", skipped, StringComparison.Ordinal),
            replaced => Assert.EndsWith(": READVALUE", replaced, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeclarationRegistersMetadataAndUserRoutineStillTakesPriority()
    {
        var output = new List<string>();
        await new Interpreter().RunAsync("""
            DEF_DLL ReadValue():missing.dll
            PRINT readvalue()
            FUNCTION ReadValue()
                RESULT=77
            FEND
            """, output.Add);

        Assert.Equal(["77"], output);
    }

    [Fact]
    public async Task NewRunDoesNotInheritPreviousDeclarations()
    {
        var interpreter = new Interpreter();
        await interpreter.RunAsync("DEF_DLL ReadValue():missing.dll", _ => { });
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            interpreter.RunAsync("PRINT ReadValue()", _ => { }));
        Assert.Contains("未定義の関数", error.Message);
    }

    [Fact]
    public void AnalyzerIdentifiesDeclarationWithoutClaimingItsCallIsAvailable()
    {
        ScriptCallAnalysis analysis = ScriptCallAnalyzer.Analyze("""
            DEF_DLL ReadValue():variant:fixture.dll
            PRINT ReadValue()
            """);

        Assert.Collection(analysis.Diagnostics,
            declaration =>
            {
                Assert.Equal(1, declaration.Line);
                Assert.Equal("native-dll-unavailable", declaration.Code);
                Assert.Equal("ReadValue", declaration.Function);
            },
            call =>
            {
                Assert.Equal(2, call.Line);
                Assert.Equal("native-dll-unavailable", call.Code);
                Assert.Equal("ReadValue", call.Function);
            });
    }

    [Fact]
    public void AnalyzerChecksSupportedNativeCallShape()
    {
        ScriptCallAnalysis analysis = ScriptCallAnalyzer.Analyze("""
            DEF_DLL ReadValue(int):long:fixture.dll
            PRINT ReadValue(2)
            PRINT ReadValue()
            """);

        ScriptCallDiagnostic diagnostic = Assert.Single(analysis.Diagnostics);
        Assert.Equal(3, diagnostic.Line);
        Assert.Equal("argument-shape", diagnostic.Code);
    }

    [Theory]
    [InlineData("string")]
    [InlineData("pchar")]
    [InlineData("pbyte")]
    [InlineData("wstring")]
    [InlineData("pwchar")]
    public void AnalyzerChecksReferencedTextFieldsWithoutEnablingUnsupportedFields(string type)
    {
        ScriptCallAnalysis analysis = ScriptCallAnalyzer.Analyze($$"""
            DEF_DLL ReadValue({byte,VAR {{type}},byte}):long:fixture.dll PACK
            DIM text="abc"
            PRINT ReadValue(7,text,42)
            PRINT ReadValue(7,"literal",42)
            PRINT ReadValue(7,text)
            DEF_DLL NumericField({VAR variant}):long:fixture.dll
            DEF_DLL UnsupportedStructure({variant}):long:fixture.dll
            """);

        Assert.Equal([(5, "argument-shape"), (6, "native-dll-unavailable"),
            (7, "native-dll-unavailable")],
            analysis.Diagnostics.Select(diagnostic =>
                (diagnostic.Line, diagnostic.Code)));
    }

    [Theory]
    [InlineData("byte")]
    [InlineData("boolean")]
    [InlineData("word")]
    [InlineData("int")]
    [InlineData("long")]
    [InlineData("bool")]
    [InlineData("uint")]
    [InlineData("dword")]
    [InlineData("longlong")]
    [InlineData("float")]
    [InlineData("double")]
    public void AnalyzerAcceptsArrayFieldsWithOrWithoutVar(string type)
    {
        ScriptCallAnalysis analysis = ScriptCallAnalyzer.Analyze($$"""
            DEF_DLL ReadArray({byte,{{type}}[],byte}):int:fixture.dll
            DEF_DLL ReadReference({VAR {{type}}[]}):int:fixture.dll PACK
            DIM values[2]=11,22,33
            PRINT ReadArray(7,values,42)
            PRINT ReadReference(values)
            PRINT ReadArray(7,values)
            DEF_DLL UnsupportedStructure({variant}):int:fixture.dll
            """);

        Assert.Equal([(6, "argument-shape"), (7, "native-dll-unavailable")],
            analysis.Diagnostics.Select(diagnostic =>
                (diagnostic.Line, diagnostic.Code)));
    }

    [Theory]
    [InlineData("byte")]
    [InlineData("boolean")]
    [InlineData("word")]
    [InlineData("int")]
    [InlineData("long")]
    [InlineData("bool")]
    [InlineData("uint")]
    [InlineData("dword")]
    [InlineData("longlong")]
    [InlineData("float")]
    [InlineData("double")]
    public void AnalyzerAcceptsNumericReferenceFields(string type)
    {
        ScriptCallAnalysis analysis = ScriptCallAnalyzer.Analyze($$"""
            DEF_DLL ReadReference({byte,VAR {{type}},byte}):int:fixture.dll PACK
            DIM value=11.25
            PRINT ReadReference(7,value,42)
            PRINT ReadReference(7,value)
            """);

        ScriptCallDiagnostic error = Assert.Single(analysis.Diagnostics);
        Assert.Equal(4, error.Line);
        Assert.Equal("argument-shape", error.Code);
    }

    [Fact]
    public void AnalyzerCountsStructureFieldsAsScriptArguments()
    {
        ScriptCallAnalysis analysis = ScriptCallAnalyzer.Analyze("""
            DEF_DLL ReadValue(int,{byte,double},VAR long):long:fixture.dll PACK
            DEF_DLL Nested({long,{long,{long}},long}):long:fixture.dll
            DEF_DLL VariantField({long,{variant}}):long:fixture.dll
            DIM output=0
            PRINT ReadValue(1,2,3.5,output)
            PRINT ReadValue(1,2,output)
            PRINT ReadValue(1,2,,output)
            PRINT Nested(1,2,3,4)
            PRINT Nested(1,2,3)
            PRINT VariantField(1,"text")
            """);

        Assert.Equal([(3, "native-dll-unavailable"), (6, "argument-shape"),
            (7, "argument-shape"), (9, "argument-shape"),
            (10, "native-dll-unavailable")],
            analysis.Diagnostics.Select(diagnostic =>
                (diagnostic.Line, diagnostic.Code)));
    }
}
