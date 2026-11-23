using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed partial class NativeDllThreadLifetimeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private const string Script = """
        DEF_DLL Hold(VAR int):int:kernel32.dll
        DEF_DLL Gate():int:kernel32.dll
        THREAD Worker()
        PRINT "parent="+Gate()
        PROCEDURE Worker()
            DIM value=7
            PRINT "worker-result="+Hold(value)
            PRINT "worker-value="+value
        FEND
        """ + "\n";

    private const string ShutdownScript = """
        OPTION OPTFINALLY
        DEF_DLL Hold(VAR int):int:kernel32.dll
        DEF_DLL Gate():int:kernel32.dll
        THREAD Worker()
        PRINT "parent="+Gate()
        PROCEDURE Worker()
            DIM value=7
            TRY
                PRINT "worker-result="+Hold(value)
            FINALLY
                PRINT "worker-finally="+value+","+VARTYPE(value)
            ENDTRY
        FEND
        """ + "\n";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScriptThreadsShareModuleAndObserveCancellation(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        using var loader = new Loader(cancel ? Protocol.Cancel : Protocol.Normal,
            cancellation);
        var output = new ConcurrentQueue<string>();
        var interpreter = new Interpreter(windows: Automation(loader));
        Task run = RunAsync(interpreter, loader, output, cancellation.Token);
        try
        {
            await ObserveAsync(run, cancel);
            Assert.Equal(cancel ? [] : ExpectedOutput(), output.ToArray());
            VerifyLoader(loader);
        }
        finally
        {
            Release(loader);
            await ObserveAsync(run, cancel);
        }
    }

    [Fact]
    public async Task ClosingOwnerWaitsForScriptWorkerAndRetainsActiveCall()
    {
        using var cancellation = new CancellationTokenSource();
        using var loader = new Loader(Protocol.CloseOwner, cancellation);
        var factory = new CapturingAutomation(Automation(loader));
        var interpreter = new Interpreter(windows: factory);
        var output = new ConcurrentQueue<string>();
        Task run = RunAsync(interpreter, loader, output, cancellation.Token);
        Task? closing = null;
        try
        {
            await Task.WhenAll(loader.ChildEntered, loader.ParentEntered)
                .WaitAsync(Timeout);
            IWindowsAutomationSession root = Assert.IsAssignableFrom<
                IWindowsAutomationSession>(factory.Session);
            INativeDllModuleSource source =
                Assert.IsAssignableFrom<INativeDllModuleSource>(root);
            NativeDllModule.Function retained = source.ResolveNativeDll(
                loader.LoadedPath, "Gate", new NativeDllCallSite(typeof(int)));

            closing = root.DisposeAsync().AsTask();
            Assert.False(closing.IsCompleted);
            Assert.False(run.IsCompleted);
            Assert.Equal(0, loader.Frees);
            Assert.Throws<ObjectDisposedException>(() => root.Fork());
            Assert.Throws<ObjectDisposedException>(() => source.ResolveNativeDll(
                loader.LoadedPath, "Gate", new NativeDllCallSite(typeof(int))));

            loader.ReleaseChild.Set();
            await closing.WaitAsync(Timeout);
            Assert.False(run.IsCompleted);
            Assert.Equal(0, loader.Frees);
            Assert.Throws<ObjectDisposedException>(() => retained.Invoke());
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Assert.Equal(0, loader.Frees);

            loader.ReleaseParent.Set();
            await run.WaitAsync(Timeout);
            Assert.Equal(ExpectedOutput(), output.ToArray());
            VerifyLoader(loader);
            await root.DisposeAsync();
            Assert.Equal(1, loader.Frees);
        }
        finally
        {
            Release(loader);
            await run.WaitAsync(Timeout);
            if (closing is not null)
            {
                await closing.WaitAsync(Timeout);
            }
        }
    }

    [Fact]
    public async Task MainCompletionWaitsForNativeWorkerAndPreservesWriteback()
    {
        using var cancellation = new CancellationTokenSource();
        using var loader = new Loader(Protocol.EndMain, cancellation);
        var stopping = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new CapturingAutomation(Automation(loader), stopping);
        var interpreter = new Interpreter(windows: factory);
        var output = new ConcurrentQueue<string>();
        var run = Task.Run(() => interpreter.RunAsync(
            ShutdownScript, output.Enqueue));
        try
        {
            await stopping.Task.WaitAsync(Timeout);
            Assert.True(loader.ChildEntered.IsCompletedSuccessfully);
            Assert.True(loader.ParentEntered.IsCompletedSuccessfully);
            Assert.False(run.IsCompleted);
            Assert.Equal(0, loader.Frees);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Assert.Equal(0, loader.Frees);

            loader.ReleaseChild.Set();
            await run.WaitAsync(Timeout);
            Assert.Equal<string>(["parent=11", "worker-finally=101,3"], output);
            VerifyLoader(loader);
        }
        finally
        {
            Release(loader);
            await run.WaitAsync(Timeout);
        }
    }

    private static WindowsAutomation Automation(Loader loader) =>
        new(_ => throw new InvalidOperationException(
            "この検査では入力を操作しません。"))
        {
            NativeDllLoader = loader
        };

    private static Task RunAsync(Interpreter interpreter, Loader loader,
        ConcurrentQueue<string> output, CancellationToken token) =>
        Task.Run(() => interpreter.RunAsync(Script, message =>
        {
            output.Enqueue(message);
            if (message.StartsWith("worker-value=", StringComparison.Ordinal))
            {
                loader.WorkerPrinted.Set();
            }
        }, token));

    private static async Task ObserveAsync(Task run, bool cancelled)
    {
        if (cancelled)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => run.WaitAsync(Timeout));
        }
        else
        {
            await run.WaitAsync(Timeout);
        }
    }

    private static string[] ExpectedOutput() =>
        ["worker-result=42", "worker-value=101", "parent=11"];

    private static void VerifyLoader(Loader loader)
    {
        Assert.Empty(loader.Errors);
        Assert.Equal(2, loader.Calls);
        Assert.Equal(1, loader.Loads);
        Assert.Equal(1, loader.Frees);
    }

    private static void Release(Loader loader)
    {
        loader.ReleaseChild.Set();
        loader.ReleaseParent.Set();
        loader.WorkerPrinted.Set();
    }

    private sealed class CapturingAutomation(WindowsAutomation automation,
        TaskCompletionSource? stopping = null)
        : IWindowsAutomation, INumericArithmeticProvider
    {
        internal IWindowsAutomationSession? Session
        {
            get; private set;
        }

        public IWindowsAutomationSession CreateSession()
        {
            Session = automation.CreateSession();
            return stopping is null ? Session
                : new CancellationRecordingSession(Session, stopping);
        }

        public INumericArithmeticScope CreateNumericArithmetic() =>
            ((INumericArithmeticProvider)automation).CreateNumericArithmetic();
    }

    private sealed class CancellationRecordingSession(
        IWindowsAutomationSession session, TaskCompletionSource stopping)
        : IWindowsAutomationSession, INativeDllScalarInvoker
    {
        public CancellationToken ExecutionStopToken => session.ExecutionStopToken;

        public IWindowsAutomationSession Fork() =>
            new CancellationRecordingSession(session.Fork(), stopping);

        public IWorkerThreadPriority? CreateWorkerThreadPriority() =>
            session.CreateWorkerThreadPriority();

        public void ConfigureDialogContext(string scriptName) =>
            session.ConfigureDialogContext(scriptName);

        public void ConfigurePause(ScriptPauseControl? pause,
            CancellationToken cancellationToken) =>
            session.ConfigurePause(pause, cancellationToken);

        public ValueTask DisposeAsync() => session.DisposeAsync();

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken cancellationToken) =>
            session.InvokeAsync(name, arguments, cancellationToken);

        public object? InvokeNativeDll(NativeDllDeclaration declaration,
            string workingDirectory, object?[] arguments, CancellationToken token,
            ushort? controlWord)
        {
            using CancellationTokenRegistration registration =
                declaration.Name == "Hold"
                    ? token.Register(() => stopping.TrySetResult()) : default;
            return ((INativeDllScalarInvoker)session).InvokeNativeDll(
                declaration, workingDirectory, arguments, token, controlWord);
        }
    }

    private enum Protocol
    {
        Normal,
        Cancel,
        CloseOwner,
        EndMain
    }

    private sealed class Loader : INativeDllLoader, IDisposable
    {
        private readonly Protocol protocol;
        private readonly CancellationTokenSource cancellation;
        private readonly ReferenceCall hold;
        private readonly GateCall gate;
        private readonly TaskCompletionSource childEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource parentEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim childExited = new();
        internal readonly ManualResetEventSlim ReleaseChild = new();
        internal readonly ManualResetEventSlim ReleaseParent = new();
        internal readonly ManualResetEventSlim WorkerPrinted = new();
        internal readonly ConcurrentQueue<string> Errors = new();
        internal int Loads;
        internal int Frees;
        internal int Calls;

        internal Loader(Protocol protocol, CancellationTokenSource cancellation)
        {
            this.protocol = protocol;
            this.cancellation = cancellation;
            hold = Hold;
            gate = Gate;
        }

        internal Task ChildEntered => childEntered.Task;

        internal Task ParentEntered => parentEntered.Task;

        internal string LoadedPath { get; private set; } = "";

        public nint Load(string path)
        {
            LoadedPath = path;
            Interlocked.Increment(ref Loads);
            return 1;
        }

        public nint GetExport(nint library, string export) => export switch
        {
            "Hold" => Marshal.GetFunctionPointerForDelegate(hold),
            "Gate" => Marshal.GetFunctionPointerForDelegate(gate),
            _ => throw new EntryPointNotFoundException(export)
        };

        public void Free(nint library) => Interlocked.Increment(ref Frees);

        private int Hold(nint pointer)
        {
            Interlocked.Increment(ref Calls);
            childEntered.TrySetResult();
            try
            {
                if (!ReleaseChild.Wait(Timeout))
                {
                    Errors.Enqueue("子処理の再開待ちが期限を超えました。");
                    return -1;
                }
                if (Frees != 0 || pointer == 0)
                {
                    Errors.Enqueue("子処理の復帰前に資源が解放されました。");
                    return -2;
                }
                if (Marshal.ReadInt32(pointer) != 7)
                {
                    Errors.Enqueue("参照引数の入力が一致しません。");
                    return -3;
                }
                Marshal.WriteInt32(pointer, 101);
                return 42;
            }
            catch (Exception error)
            {
                Errors.Enqueue("子処理のcallback: " + error.GetType().Name);
                return -4;
            }
            finally
            {
                childExited.Set();
            }
        }

        private int Gate()
        {
            Interlocked.Increment(ref Calls);
            parentEntered.TrySetResult();
            try
            {
                if (!ChildEntered.Wait(Timeout))
                {
                    Errors.Enqueue("子処理の開始待ちが期限を超えました。");
                    return -5;
                }
                if (protocol == Protocol.CloseOwner)
                {
                    if (!ReleaseParent.Wait(Timeout))
                    {
                        Errors.Enqueue("親処理の再開待ちが期限を超えました。");
                        return -6;
                    }
                    return 11;
                }
                if (protocol == Protocol.EndMain)
                {
                    return 11;
                }
                GC.Collect();
                GC.WaitForPendingFinalizers();
                if (Frees != 0)
                {
                    Errors.Enqueue("呼出し中にDLLが解放されました。");
                }
                if (protocol == Protocol.Cancel)
                {
                    cancellation.Cancel();
                }
                ReleaseChild.Set();
                bool completed = protocol == Protocol.Cancel
                    ? childExited.Wait(Timeout) : WorkerPrinted.Wait(Timeout);
                if (!completed)
                {
                    Errors.Enqueue("子処理の完了待ちが期限を超えました。");
                    return -7;
                }
                return 11;
            }
            catch (Exception error)
            {
                Errors.Enqueue("親処理のcallback: " + error.GetType().Name);
                ReleaseChild.Set();
                return -8;
            }
        }

        public void Dispose()
        {
            GC.KeepAlive(hold);
            GC.KeepAlive(gate);
            childExited.Dispose();
            ReleaseChild.Dispose();
            ReleaseParent.Dispose();
            WorkerPrinted.Dispose();
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int ReferenceCall(nint pointer);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int GateCall();
    }
}
