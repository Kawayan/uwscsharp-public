using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class HashTableTests
{
    [Theory]
    [InlineData("HASH_KEY", -101)]
    [InlineData("HASH_VAL", -102)]
    [InlineData("HASH_EXISTS", -103)]
    [InlineData("HASH_REMOVE", -104)]
    [InlineData("HASH_REMOVEALL", -109)]
    [InlineData("HASH_CASECARE", 4096)]
    [InlineData("HASH_SORT", 8192)]
    public void HashConstantsMatchStoredCatalog(string name, int value) =>
        Assert.Equal(Value.Numeric(value), ExpressionEvaluator.Evaluate(name));

    [Fact]
    public async Task DefaultKeysIgnoreCaseAndKeepInsertionOrder()
    {
        Assert.Equal(["2", "3", "First", "second", "", "1", "1", "0", "0", "second"], await Run("""
            HASHTBL h
            h["First"]=1
            h["second"]=2
            h["FIRST"]=3
            PRINT LENGTH(h)
            PRINT h["first"]
            PRINT h[0,HASH_KEY]
            PRINT h[1,HASH_KEY]
            PRINT h["missing"]
            PRINT h["FIRST",HASH_EXISTS]
            DIM removed=h["first",HASH_REMOVE]
            PRINT removed
            PRINT h["First",HASH_EXISTS]
            removed=h["First",HASH_REMOVE]
            PRINT removed
            PRINT h[0,HASH_KEY]
            """));
    }

    [Fact]
    public async Task CaseSensitiveSortedKeysSupportCombinedFlagsAndClear()
    {
        Assert.Equal(["A", "B", "a", "3", "0", "2", "B", "b"], await Run("""
            HASHTBL h=HASH_CASECARE OR HASH_SORT
            h["a"]=1
            h["B"]=2
            h["A"]=3
            FOR key IN h
                PRINT key
            NEXT
            PRINT h[0,HASH_VAL]
            h=HASH_REMOVEALL
            PRINT LENGTH(h)
            h["b"]=1
            h["B"]=2
            PRINT LENGTH(h)
            PRINT h[0,HASH_KEY]
            PRINT h[1,HASH_KEY]
            """));
    }

    [Fact]
    public async Task HelpHashTableExampleProducesDocumentedOutput()
    {
        Assert.Equal(["'う' の値は 3", "あ = 1", "い = 2", "う = 3", "お = 9", "1"], await Run("""
            PUBLIC HASHTBL B
            HASHTBL A=HASH_CASECARE OR HASH_SORT
            A["あ"]=1
            A["う"]=3
            A["お"]=5
            A["え"]=4
            A["い"]=2
            A["お"]=9
            PRINT "'う' の値は "+A["う"]
            IFB A["え",HASH_EXISTS]
                flg=A["え",HASH_REMOVE]
            ENDIF
            FOR n=0 TO LENGTH(A)-1
                PRINT A[n,HASH_KEY]+" = "+A[n,HASH_VAL]
            NEXT
            testproc(A)
            PROCEDURE testproc(X[])
                PRINT X["あ"]
            FEND
            """));
    }

    [Fact]
    public async Task PublicHashAndHelpExampleProcedureWork()
    {
        Assert.Equal(["1", "2", "3", "9", "4", "12"], await Run("""
            PUBLIC HASHTBL h=HASH_SORT
            HASHTBL a=HASH_CASECARE OR HASH_SORT
            a["あ"]=1
            a["う"]=3
            a["お"]=5
            a["え"]=4
            a["い"]=2
            a["お"]=9
            DIM removed=a["え",HASH_REMOVE]
            FOR n=0 TO LENGTH(a)-1
                PRINT a[n,HASH_VAL]
            NEXT
            testproc(a)
            PRINT LENGTH(a)
            PRINT h["result"]
            PROCEDURE testproc(VAR x[])
                x["あ"]=4
                h["result"]=12
            FEND
            """));
    }

    [Fact]
    public async Task AssignmentCopiesContentsAndFlagsWithoutSharingUpdates()
    {
        Assert.Equal(["1", "2", "1", "A", "a"], await Run("""
            HASHTBL source=HASH_CASECARE OR HASH_SORT
            source["a"]=1
            HASHTBL target
            target["old"]=8
            target=source
            target["A"]=2
            target["a"]=3
            source=source
            PRINT LENGTH(source)
            PRINT LENGTH(target)
            PRINT source["a"]
            PRINT target[0,HASH_KEY]
            PRINT target[1,HASH_KEY]
            """));
    }

    [Fact]
    public async Task HashParametersCopyInAndCopyOutInParameterOrder()
    {
        Assert.Equal(["0", "0", "0", "2"], await Run("""
            HASHTBL h
            h["x"]=0
            h["y"]=0
            edit(h,h)
            PRINT h["x"]
            PRINT h["y"]
            PROCEDURE edit(VAR first[],VAR second[])
                first["x"]=1
                PRINT second["x"]
                second["y"]=2
                PRINT first["y"]
            FEND
            """));
    }

    [Fact]
    public async Task HashParameterFailureDoesNotCopyBack()
    {
        Assert.Equal(["caught", "1"], await Run("""
            HASHTBL h
            h["x"]=1
            TRY
                fail(h)
            EXCEPT
                PRINT "caught"
            ENDTRY
            PRINT h["x"]
            PROCEDURE fail(VAR x[])
                x["x"]=9
                PRINT 1/0
            FEND
            """));
    }

    [Fact]
    public async Task ForInCollectsKeysOnceAndClearsIteratorAtNormalEnd()
    {
        Assert.Equal(["b", "a", "1", "new"], await Run("""
            HASHTBL h
            h["b"]=1
            h["a"]=2
            FOR key IN h[]
                PRINT key
                h=HASH_REMOVEALL
                h["new"]=9
            NEXT
            PRINT key=EMPTY
            PRINT h[0,HASH_KEY]
            """));
    }

    [Fact]
    public async Task NestedArraysAndHashValuesRemainIndependentWhileObjectsShareIdentity()
    {
        object instance = new();
        UwscHashTable first = UwscHashTable.Create().Set("ref", Value.FromObject(instance))
            .Set("array", Value.FromArray(UwscArray.Create([0], [Value.Numeric(1)])));
        UwscHashTable second = first.Set("array", Value.FromArray(first.Get("array").AsArray()
            .Set([0], Value.Numeric(2))));
        Assert.Same(instance, second.Get("ref").AsObject());
        Assert.Equal(Value.Numeric(1), first.Get("array").AsArray().Get([0]));
        Assert.Equal(["1", "2"], await Run("""
            HASHTBL child
            child["x"]=1
            HASHTBL parent
            parent["child"]=child
            child["x"]=2
            DIM copied=parent["child"]
            PRINT copied["x"]
            PRINT child["x"]
            """));
    }

    [Fact]
    public async Task NumericAndEmptyKeysUseStringRepresentation()
    {
        Assert.Equal(["one", "empty", "2", "3.5"], await Run("""
            HASHTBL h
            h[1]="one"
            h[EMPTY]="empty"
            PRINT h["1"]
            PRINT h[""]
            PRINT LENGTH(h)
            h[3.5]=4
            PRINT h[2,HASH_KEY]
            """));
    }

    [Fact]
    public async Task LookupIndexSideEffectsUseCurrentHashContents()
    {
        Assert.Equal(["2", "3"], await Run("""
            PUBLIC HASHTBL h
            h["x"]=1
            PRINT h[key()]
            h[key()]=3
            PRINT h["x"]
            FUNCTION key()
                h["x"]=2
                RESULT="x"
            FEND
            """));
    }

    [Theory]
    [InlineData("HASHTBL h=1")]
    [InlineData("HASHTBL h\nh[\"x\",HASH_EXISTS]=2")]
    [InlineData("HASHTBL h\nPRINT h[0,HASH_KEY]")]
    [InlineData("HASHTBL h\nPRINT h[-1,HASH_VAL]")]
    [InlineData("HASHTBL h\nPRINT h[\"x\",0]")]
    [InlineData("HASHTBL h\nPRINT h[NULL]")]
    [InlineData("HASHTBL h\nh=1")]
    [InlineData("HASHTBL h\nPRINT h[1,2,3]")]
    public async Task InvalidOperationsReportSourceLine(string source) =>
        await Assert.ThrowsAsync<ScriptException>(() => Run(source));


    [Fact]
    public async Task CancellationDoesNotCopyBackHashArguments()
    {
        using var cancellation = new CancellationTokenSource();
        var output = new List<string>();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("""
                OPTION OPTFINALLY
                HASHTBL h
                h["x"]=1
                TRY
                    edit(h)
                FINALLY
                    PRINT h["x"]
                ENDTRY
                PROCEDURE edit(VAR x[])
                    x["x"]=9
                    PRINT "cancel"
                FEND
                """, text =>
                {
                    output.Add(text);
                    if (text == "cancel")
                    {
                        cancellation.Cancel();
                    }
                }, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(["cancel", "1"], output);
    }

    [Fact]
    public async Task ReturnedHashesAndExplicitVarParametersKeepCopySemantics()
    {
        Assert.Equal(["4", "8", "4"], await Run("""
            HASHTBL h
            h["x"]=1
            DIM returned=edit(h[])
            returned["x"]=8
            PRINT h["x"]
            PRINT returned["x"]
            HASHTBL parent
            parent["child"]=h
            edit(parent["child"])
            DIM nested=parent["child"]
            PRINT nested["x"]
            FUNCTION edit(VAR x)
                x["x"]=4
                RESULT=x
            FEND
            """));
    }

    [Fact]
    public async Task CompiledProgramDoesNotSharePublicHashBetweenRuns()
    {
        ScriptProgram program = ScriptCompiler.Compile("""
            PUBLIC HASHTBL h
            PRINT LENGTH(h)
            h["x"]=1
            PRINT LENGTH(h)
            """);
        var first = new List<string>();
        var second = new List<string>();
        var interpreter = new Interpreter();
        await Task.WhenAll(interpreter.RunAsync(program, new CallbackScriptHost(first.Add)),
            interpreter.RunAsync(program, new CallbackScriptHost(second.Add)));
        Assert.Equal(["0", "1"], first);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task StaticCallSharesPublicHashWithChild()
    {
        string directory = Path.Combine(Path.GetTempPath(), "uwsc-hash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "child.uws"), "h[\"child\"]=7");
            var output = new List<string>();
            await new Interpreter().RunAsync("""
                PUBLIC HASHTBL h
                CALL "child.uws"
                PRINT h["child"]
                """, output.Add, default,
                new ScriptRunOptions([]) { SourcePath = Path.Combine(directory, "main.uws") });
            Assert.Equal(["7"], output);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void PureEvaluatorReadsSnapshotsButRejectsRemoval()
    {
        var value = Value.FromHashTable(UwscHashTable.Create().Set("key", Value.Numeric(7)));
        var variables = new Dictionary<string, Value> { ["h"] = value };
        Assert.Equal(Value.Numeric(7), ExpressionEvaluator.Evaluate("h[\"key\"]", variables));
        Assert.Equal(Value.Numeric(1), ExpressionEvaluator.Evaluate("LENGTH(h)", variables));
        Assert.Throws<InvalidOperationException>(() => ExpressionEvaluator.Evaluate("h[\"key\",HASH_REMOVE]", variables));
        Assert.True(value.AsHashTable().Contains("key"));
    }
}
