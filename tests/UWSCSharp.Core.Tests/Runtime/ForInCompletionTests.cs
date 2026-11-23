using System.Collections;
using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class ForInCompletionTests
{
    [Theory]
    [InlineData("AB", "A,B,")]
    [InlineData("日本", "日,本,")]
    [InlineData("😀Z", "😀,Z,")]
    [InlineData("a\u0301", "a,\u0301,")]
    [InlineData("", "")]
    public async Task StringsUseUnicodeScalarsAndClearIterator(string text, string expected)
    {
        string[] output = await Run("item=\"old\"\nFOR item IN \""
            + text + "\"\nPRINT item\nNEXT\nPRINT item");
        Assert.Equal(expected, string.Join(",", output));
    }

    [Fact]
    public async Task InvalidUtf16UsesReplacementScalar()
    {
        string text = new(['\uD800', 'Z']);
        Assert.Equal(["\uFFFD", "Z", ""], await Run(
            "FOR item IN \"" + text + "\"\nPRINT item\nNEXT\nPRINT item"));
    }

    [Theory]
    [InlineData("DIM a[]=1,2,3", "a", "1,2,3,")]
    [InlineData("DIM a[-1]", "a", "")]
    [InlineData("DIM a[2][2]=1,2,3,4,5,6,7,8,9", "a", "1,2,3,4,5,6,7,8,9,")]
    [InlineData("DIM a[2][2]=1,2,3,4,5,6,7,8,9", "a[1]", "4,5,6,")]
    public async Task ArraysUseRowMajorValuesAndPartialRegions(
        string declaration, string collection, string expected)
    {
        Assert.Equal(expected, string.Join(",", await Run(declaration
            + "\nFOR item IN " + collection + "\nPRINT item\nNEXT\nPRINT item")));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("b")]
    public async Task ImmutableArrayViewSurvivesMutationAndAliasReassignment(string collection)
    {
        Assert.Equal(["1", "2", "3", "9"], await Run(
            "DIM a[]=1,2,3\nb=a\nFOR item IN " + collection
            + "\nPRINT item\nSETCLEAR(a,9)\nb=SPLIT(\"x\",\",\")\nNEXT\nPRINT a[0]"));
    }

    [Fact]
    public async Task IteratorCanReplaceItsOwnArraySource()
    {
        Assert.Equal(["1", "2", "3", ""], await Run(
            "DIM a[]=1,2,3\nFOR a IN a\nPRINT a\nNEXT\nPRINT a"));
    }

    [Fact]
    public async Task CollectionExpressionRunsOnceAndBodyCanReplaceSource()
    {
        Assert.Equal(["A", "B", "1"], await Run("""
            PUBLIC calls=0
            FOR item IN MakeText()
                PRINT item
            NEXT
            PRINT calls
            FUNCTION MakeText()
                calls=calls+1
                RESULT="AB"
            FEND
            """));
        Assert.Equal(["A", "B", "C"], await Run(
            "text=\"ABC\"\nFOR item IN text\nPRINT item\ntext=\"X\"\nNEXT"));
    }

    [Theory]
    [InlineData("", "b,a,")]
    [InlineData("=HASH_SORT", "a,b,")]
    public async Task HashKeySnapshotKeepsConfiguredOrderAfterClear(
        string flags, string expected)
    {
        Assert.Equal(expected, string.Join(",", await Run(
            "HASHTBL h" + flags + "\nh[\"b\"]=1\nh[\"a\"]=2\n"
            + "FOR item IN h[]\nPRINT item\nh=HASH_REMOVEALL\nh[\"new\"]=9\n"
            + "NEXT\nPRINT item")));
    }

    [Theory]
    [InlineData("BREAK", "A", "1")]
    [InlineData("CONTINUE", "", "2")]
    public async Task FlowKeepsOrClearsIteratorAccordingToCompletion(
        string instruction, string iterator, string iterations)
    {
        Assert.Equal([iterator, iterations], await Run(
            "n=0\nFOR item IN \"AB\"\nn=n+1\n" + instruction
            + "\nNEXT\nPRINT item\nPRINT n"));
    }

    [Theory]
    [InlineData("BREAK 2", "A", "C", "1")]
    [InlineData("CONTINUE 2", "", "C", "2")]
    public async Task MultiLevelFlowClearsOnlyNormallyFinishedIterator(
        string instruction, string outer, string inner, string count)
    {
        Assert.Equal([outer, inner, count], await Run(
            "n=0\nFOR s IN \"AB\"\nFOR t IN \"CD\"\nn=n+1\n"
            + instruction + "\nNEXT\nNEXT\nPRINT s\nPRINT t\nPRINT n"));
    }

    [Fact]
    public async Task FinallyRunsBeforeBindingTheNextElement()
    {
        Assert.Equal(["A", "B", ""], await Run(
            "FOR item IN \"AB\"\nTRY\nCONTINUE\nFINALLY\nPRINT item\n"
            + "ENDTRY\nNEXT\nPRINT item"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("EMPTY")]
    [InlineData("NULL")]
    [InlineData("NOTHING")]
    public async Task InvalidCollectionIsReportedAtHeaderWithoutChangingIterator(string collection)
    {
        Assert.Equal(["3", "old"], await Run(
            "item=\"old\"\nTRY\nFOR item IN " + collection
            + "\nPRINT 9\nNEXT\nEXCEPT\nPRINT TRY_ERRLINE\nENDTRY\nPRINT item"));
    }

    [Theory]
    [InlineData("CONST item=1\nFOR item IN \"AB\"\nNEXT", 2)]
    [InlineData("CONST item=1\nFOR item IN \"\"\nNEXT", 2)]
    [InlineData("OPTION EXPLICIT\nFOR item IN \"A\"\nNEXT", 2)]
    public async Task FirstAndTerminalAssignmentsUseNormalWriteGuards(string source, int line)
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() => Run(source));
        Assert.Equal(line, error.Line);
    }

    [Fact]
    public async Task TerminalAssignmentFailureDoesNotRollbackBody()
    {
        var output = new List<string>();
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync(
                "FOR item IN \"A\"\nHASHTBL item\nPRINT \"body\"\nNEXT", output.Add));
        Assert.Equal(1, error.Line);
        Assert.Equal(["body"], output);
    }

    [Theory]
    [InlineData("FOR item IN\nNEXT", 1)]
    [InlineData("FOR IN \"A\"\nNEXT", 1)]
    [InlineData("FOR item IN \"A\"\nNEXT item", 2)]
    [InlineData("FOR item IN \"A\"", 1)]
    [InlineData("FOR item IN \"A\"\nWEND", 2)]
    [InlineData("IF FALSE\nFOR item IN\nNEXT\nENDIF", 2)]
    public void InvalidStructureFailsBeforeExecution(string source, int line)
    {
        ScriptException error = Assert.Throws<ScriptException>(() => ScriptCompiler.Compile(source));
        Assert.Equal(line, error.Line);
    }

    [Fact]
    public async Task NativeReturnedListIsCopiedBeforeBodyAndEnumeratedOnce()
    {
        var values = new List<Value> { Value.Numeric(1), Value.Numeric(2) };
        var adapter = new Adapter(values);
        var output = new List<string>();
        await new Interpreter(windows: adapter).RunAsync(
            "FOR item IN CREATEOLEOBJ(\"fixture\")\nPRINT item\nNEXT\nPRINT item",
            text =>
            {
                output.Add(text);
                values[1] = Value.Numeric(99);
            });
        Assert.Equal(["1", "2", ""], output);
        Assert.Equal(1, adapter.Enumerations);
        Assert.Equal(1, adapter.Disposals);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SnapshotCancellationStopsFurtherReadsAndPreservesCleanupFailure(
        bool onCurrent, bool cleanupFails)
    {
        using var cancellation = new CancellationTokenSource();
        var cleanup = new InvalidOperationException("enumerator cleanup");
        var values = new CancellingValues(cancellation, onCurrent)
        {
            CleanupFailure = cleanupFails ? cleanup : null
        };
        var adapter = new Adapter(values);
        var output = new List<string>();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter(windows: adapter).RunAsync(
                "FOR item IN CREATEOLEOBJ(\"fixture\")\nPRINT item\nNEXT",
                output.Add, cancellation.Token));
        Assert.Equal(1, values.Moves);
        Assert.Equal(onCurrent ? 1 : 0, values.Reads);
        Assert.Equal(1, values.Disposals);
        Assert.Equal(1, adapter.Disposals);
        Assert.Empty(output);
        if (cleanupFails)
        {
            Assert.Same(cleanup, error.Data["UWSCSharp.ForInCleanupErrors"]);
        }
    }

    [Fact]
    public async Task SnapshotFailureDisposesEnumeratorAndDoesNotEnterBody()
    {
        var failure = new IOException("collection failed");
        var values = new FailingValues(failure);
        var adapter = new Adapter(values);
        var output = new List<string>();
        IOException error = await Assert.ThrowsAsync<IOException>(() =>
            new Interpreter(windows: adapter).RunAsync(
                "FOR item IN CREATEOLEOBJ(\"fixture\")\nPRINT item\nNEXT", output.Add));
        Assert.Same(failure, error);
        Assert.Equal(1, values.Disposals);
        Assert.Equal(1, adapter.Disposals);
        Assert.Empty(output);
    }

    [Fact]
    public async Task SnapshotFailureSurvivesEnumeratorDisposeFailure()
    {
        var primary = new IOException("collection failed");
        var cleanup = new InvalidOperationException("enumerator cleanup");
        var values = new FailingValues(primary) { CleanupFailure = cleanup };
        var adapter = new Adapter(values);
        IOException error = await Assert.ThrowsAsync<IOException>(() =>
            new Interpreter(windows: adapter).RunAsync(
                "FOR item IN CREATEOLEOBJ(\"fixture\")\nPRINT item\nNEXT", _ => { }));
        Assert.Same(primary, error);
        Assert.Same(cleanup, error.Data["UWSCSharp.ForInCleanupErrors"]);
        Assert.Equal(1, values.Disposals);
        Assert.Equal(1, adapter.Disposals);
    }

    [Fact]
    public async Task DisposeFailureAfterCollectionPreventsBodyAndReleasesSession()
    {
        var cleanup = new IOException("enumerator cleanup");
        var values = new FailingValues(new InvalidOperationException("unused"))
        {
            EndNormally = true,
            CleanupFailure = cleanup
        };
        var adapter = new Adapter(values);
        var output = new List<string>();
        IOException error = await Assert.ThrowsAsync<IOException>(() =>
            new Interpreter(windows: adapter).RunAsync(
                "FOR item IN CREATEOLEOBJ(\"fixture\")\nPRINT item\nNEXT", output.Add));
        Assert.Same(cleanup, error);
        Assert.Equal(1, values.Disposals);
        Assert.Equal(1, adapter.Disposals);
        Assert.Empty(output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdapterSnapshotPreservesMillionElementBoundary(bool overflow)
    {
        var values = new GeneratedValues(UwscArray.MaximumElements + (overflow ? 1 : 0));
        var adapter = new Adapter(values);
        var output = new List<string>();
        Task run = new Interpreter(windows: adapter).RunAsync(
            "FOR item IN CREATEOLEOBJ(\"fixture\")\nPRINT \"body\"\nBREAK\nNEXT",
            output.Add);
        if (overflow)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => run);
            Assert.Empty(output);
        }
        else
        {
            await run;
            Assert.Equal(["body"], output);
        }

        Assert.Equal(values.Count, values.Produced);
        Assert.Equal(1, adapter.Disposals);
    }

    private sealed class GeneratedValues(int count) : IReadOnlyList<Value>
    {
        internal int Produced;

        public int Count => count;

        public Value this[int index] => Value.Empty;

        public IEnumerator<Value> GetEnumerator()
        {
            for (int i = 0; i < count; i++)
            {
                Produced++;
                yield return Value.Empty;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class FailingValues(Exception failure) : IReadOnlyList<Value>
    {
        internal Exception? CleanupFailure
        {
            get; init;
        }

        internal int Disposals;

        internal bool EndNormally;

        public int Count => 2;

        public Value this[int index] => throw failure;

        public IEnumerator<Value> GetEnumerator()
        {
            return new FailingEnumerator(this, failure);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class FailingEnumerator(FailingValues owner, Exception failure)
            : IEnumerator<Value>
        {
            private int moves;

            public Value Current => Value.Numeric(1);

            object IEnumerator.Current => Current;

            public bool MoveNext() => ++moves == 1 ? true
                : owner.EndNormally ? false : throw failure;

            public void Reset() => throw new NotSupportedException();

            public void Dispose()
            {
                owner.Disposals++;
                if (owner.CleanupFailure is not null)
                {
                    throw owner.CleanupFailure;
                }
            }
        }
    }

    private sealed class Adapter(IReadOnlyList<Value> values)
        : IWindowsAutomation, IWindowsAutomationSession
    {
        internal int Disposals
        {
            get; private set;
        }

        internal int Enumerations;

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token) =>
            ValueTask.FromResult(Value.FromObject(this));

        public ValueTask<IReadOnlyList<Value>> EnumerateObjectsAsync(
            Value target, CancellationToken token)
        {
            Enumerations++;
            return ValueTask.FromResult(values);
        }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancellingValues(CancellationTokenSource cancellation,
        bool onCurrent)
        : IReadOnlyList<Value>
    {
        internal int Moves;
        internal int Reads;
        internal int Disposals;

        internal Exception? CleanupFailure
        {
            get; init;
        }

        public int Count => 3;

        public Value this[int index] => Value.Numeric(index);

        public IEnumerator<Value> GetEnumerator() => new Enumerator(this, cancellation, onCurrent);

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class Enumerator(CancellingValues owner,
            CancellationTokenSource cancellation, bool onCurrent) : IEnumerator<Value>
        {
            private int index = -1;

            public Value Current
            {
                get
                {
                    owner.Reads++;
                    if (onCurrent)
                    {
                        cancellation.Cancel();
                    }
                    return owner[index];
                }
            }

            object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                owner.Moves++;
                if (owner.Moves == 1 && !onCurrent)
                {
                    cancellation.Cancel();
                }

                return ++index < owner.Count;
            }

            public void Reset() => throw new NotSupportedException();

            public void Dispose()
            {
                owner.Disposals++;
                if (owner.CleanupFailure is not null)
                {
                    throw owner.CleanupFailure;
                }
            }
        }
    }
}
