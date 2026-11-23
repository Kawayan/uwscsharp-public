using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed partial class NativeDllThreadLifetimeTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RealLibraryRemainsLoadedUntilScriptCallsReturn(bool cancel,
        bool externalCall)
    {
        using var loader = new RealLoader();
        using var cancellation = new CancellationTokenSource();
        var output = new ConcurrentQueue<string>();
        var interpreter = new Interpreter(windows: RealAutomation(loader));
        Task run = RunRealAsync(interpreter, loader, output, cancellation.Token,
            externalCall: externalCall);
        try
        {
            await loader.WaitForCallsAsync();
            loader.AssertLoaded();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            loader.AssertLoaded();
            if (cancel)
            {
                cancellation.Cancel();
                loader.ReleaseAll();
            }
            else
            {
                loader.ReleaseChild.Set();
                await loader.WorkerPrinted.Task.WaitAsync(Timeout);
                loader.AssertLoaded();
                loader.ReleaseParent.Set();
            }

            await ObserveAsync(run, cancel);
            Assert.Equal(ExpectedRealOutput(externalCall, cancel), output.ToArray());
            loader.AssertUnloaded();
        }
        finally
        {
            loader.ReleaseAll();
            await ObserveAsync(run, cancellation.IsCancellationRequested);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealLibraryClosingOwnerRetainsParentNativeCall(
        bool externalCall)
    {
        using var loader = new RealLoader();
        var factory = new CapturingAutomation(RealAutomation(loader));
        var interpreter = new Interpreter(windows: factory);
        var output = new ConcurrentQueue<string>();
        Task run = RunRealAsync(interpreter, loader, output,
            externalCall: externalCall);
        Task? closing = null;
        try
        {
            await loader.WaitForCallsAsync();
            IWindowsAutomationSession root = Assert.IsAssignableFrom<
                IWindowsAutomationSession>(factory.Session);
            INativeDllModuleSource source =
                Assert.IsAssignableFrom<INativeDllModuleSource>(root);
            var signature = new NativeDllCallSite(typeof(int),
                typeof(nint), typeof(nint), typeof(nint), typeof(nint));
            NativeDllModule.Function retained = source.ResolveNativeDll(
                loader.Path, "Gate", signature);

            closing = root.DisposeAsync().AsTask();
            Assert.False(closing.IsCompleted);
            Assert.False(run.IsCompleted);
            loader.AssertLoaded();
            Assert.Throws<ObjectDisposedException>(() => root.Fork());
            Assert.Throws<ObjectDisposedException>(() => source.ResolveNativeDll(
                loader.Path, "Gate", signature));

            loader.ReleaseChild.Set();
            await closing.WaitAsync(Timeout);
            await loader.WorkerPrinted.Task.WaitAsync(Timeout);
            Assert.False(run.IsCompleted);
            loader.AssertLoaded();
            Assert.Throws<ObjectDisposedException>(() => retained.Invoke(
                (nint)0, (nint)0, (nint)0, (nint)0));
            GC.Collect();
            GC.WaitForPendingFinalizers();
            loader.AssertLoaded();

            loader.ReleaseParent.Set();
            await run.WaitAsync(Timeout);
            Assert.Equal(ExpectedRealOutput(externalCall), output.ToArray());
            loader.AssertUnloaded();
            await root.DisposeAsync();
            loader.AssertUnloaded();
        }
        finally
        {
            loader.ReleaseAll();
            await run.WaitAsync(Timeout);
            if (closing is not null)
            {
                await closing.WaitAsync(Timeout);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealLibraryMainCompletionWaitsAndPreservesWriteback(
        bool externalCall)
    {
        using var loader = new RealLoader();
        var stopping = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new CapturingAutomation(RealAutomation(loader), stopping);
        var interpreter = new Interpreter(windows: factory);
        var output = new ConcurrentQueue<string>();
        Task run = RunRealAsync(interpreter, loader, output, shutdown: true,
            externalCall: externalCall);
        try
        {
            await loader.WaitForCallsAsync();
            loader.AssertLoaded();
            loader.ReleaseParent.Set();
            await stopping.Task.WaitAsync(Timeout);
            Assert.False(run.IsCompleted);
            loader.AssertLoaded();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            loader.AssertLoaded();

            loader.ReleaseChild.Set();
            await run.WaitAsync(Timeout);
            Assert.Equal(ExpectedRealOutput(externalCall, shutdown: true),
                output.ToArray());
            loader.AssertUnloaded();
        }
        finally
        {
            loader.ReleaseAll();
            await run.WaitAsync(Timeout);
        }
    }

    private static WindowsAutomation RealAutomation(RealLoader loader) =>
        new(_ => throw new InvalidOperationException(
            "この検査では入力を操作しません。"))
        {
            NativeDllLoader = loader
        };

    private static async Task RunRealAsync(Interpreter interpreter, RealLoader loader,
        ConcurrentQueue<string> output, CancellationToken token = default,
        bool shutdown = false, bool externalCall = false)
    {
        using var input = new RealInput(
            RealScript(loader.Path, loader.Prefix, shutdown, externalCall),
            externalCall, RealParentScript(loader.Prefix));
        await Task.Run(() => interpreter.RunAsync(input.Program,
            new CallbackScriptHost(message =>
            {
                output.Enqueue(message);
                if (message.StartsWith("worker-value=", StringComparison.Ordinal))
                {
                    loader.WorkerPrinted.TrySetResult();
                }
            }), token));
    }

    private const string ExternalScript = "CALL child\nPRINT \"call-returned\"\n";

    private static string[] ExpectedRealOutput(bool externalCall,
        bool cancel = false, bool shutdown = false)
    {
        string[] output = cancel ? [] : shutdown
            ? ["parent=11", "worker-finally=101,3"] : ExpectedOutput();
        return externalCall ? ["call-returned", .. output] : output;
    }

    private sealed class RealInput : IDisposable
    {
        private readonly string? folder;

        internal RealInput(string source, bool externalCall, string parent)
        {
            if (!externalCall)
            {
                Program = ScriptCompiler.Compile(source);
                return;
            }

            folder = Directory.CreateTempSubdirectory(
                "uwsc-dll-lifetime-").FullName;
            try
            {
                var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
                string main = Path.Combine(folder, "main.uws");
                File.WriteAllText(main, ExternalScript + parent, encoding);
                File.WriteAllText(Path.Combine(folder, "child.uws"), source, encoding);
                Program = ScriptCompiler.CompileFile(main);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal ScriptProgram Program
        {
            get;
        }

        public void Dispose()
        {
            if (folder is null)
            {
                return;
            }
            File.Delete(Path.Combine(folder, "main.uws"));
            File.Delete(Path.Combine(folder, "child.uws"));
            Directory.Delete(folder);
        }
    }

    private static string RealParentScript(string prefix) => $$"""
        PRINT "parent="+Gate("{{prefix}}-child-entered","{{prefix}}-parent-entered","{{prefix}}-parent-release","{{prefix}}-parent-verified")
        """ + "\n";

    private static string RealScript(string path, string prefix, bool shutdown,
        bool externalCall = false)
    {
        string declaration = $$"""
            DEF_DLL Hold(VAR int,wstring,wstring,wstring):int:{{path}}
            DEF_DLL Gate(wstring,wstring,wstring,wstring):int:{{path}}
            THREAD Worker()
            """ + "\n" + (externalCall ? "" : RealParentScript(prefix)) + """
            PROCEDURE Worker()
                DIM value=7
            """ + "\n";
        string call = $$"""
                PRINT "worker-result="+Hold(value,"{{prefix}}-child-entered","{{prefix}}-child-release","{{prefix}}-child-verified")
            """ + "\n";
        return shutdown
            ? "OPTION OPTFINALLY\n" + declaration + "    TRY\n" + call
                + "    FINALLY\n"
                + "        PRINT \"worker-finally=\"+value+\",\"+VARTYPE(value)\n"
                + "    ENDTRY\nFEND\n"
            : declaration + call + "    PRINT \"worker-value=\"+value\nFEND\n";
    }

    private sealed class RealLoader : INativeDllLoader, IDisposable
    {
        private readonly INativeDllLoader loader = NativeDllLoader.ScriptDependencies;
        private readonly ConcurrentQueue<nint> deferred = new();
        private readonly EventWaitHandle childEntered;
        private readonly EventWaitHandle parentEntered;
        private readonly EventWaitHandle childVerified;
        private readonly EventWaitHandle parentVerified;
        internal readonly EventWaitHandle ReleaseChild;
        internal readonly EventWaitHandle ReleaseParent;
        internal readonly TaskCompletionSource WorkerPrinted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int loads;
        private int frees;
        private int prematureFrees;

        internal RealLoader(bool buffered = false)
        {
            Path = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(AppContext.BaseDirectory,
                    buffered ? "native-dll-buffer-lifetime" : "native-dll-lifetime",
                    buffered ? "UwscOwnedBufferLifetimeProbe.dll" : "UwscOwnedLifetimeProbe.dll"));
            Assert.True(File.Exists(Path));
            Assert.Equal((nint)0, GetModuleHandleW(Path));
            childEntered = CreateEvent("child-entered");
            parentEntered = CreateEvent("parent-entered");
            childVerified = CreateEvent("child-verified");
            parentVerified = CreateEvent("parent-verified");
            ReleaseChild = CreateEvent("child-release");
            ReleaseParent = CreateEvent("parent-release");
        }

        internal string Path
        {
            get;
        }

        internal string Prefix
        {
            get;
        } =
            "Local\\UWSCSharpLifetime-" + Guid.NewGuid().ToString("N");

        private EventWaitHandle CreateEvent(string suffix) =>
            new(false, EventResetMode.ManualReset, Prefix + "-" + suffix);

        internal async Task WaitForCallsAsync()
        {
            bool[] entered = await Task.WhenAll(
                Task.Run(() => childEntered.WaitOne(Timeout)),
                Task.Run(() => parentEntered.WaitOne(Timeout))).WaitAsync(Timeout);
            Assert.All(entered, Assert.True);
        }

        public nint Load(string path)
        {
            Assert.Equal(Path, path);
            Interlocked.Increment(ref loads);
            return loader.Load(path);
        }

        public nint GetExport(nint library, string export) =>
            loader.GetExport(library, export);

        public void Free(nint library)
        {
            Interlocked.Increment(ref frees);
            if ((childEntered.WaitOne(0) && !ReleaseChild.WaitOne(0))
                || (parentEntered.WaitOne(0) && !ReleaseParent.WaitOne(0)))
            {
                // 回帰時も実行中のコードをアンマップせず、違反を失敗として残します。
                Interlocked.Increment(ref prematureFrees);
                deferred.Enqueue(library);
                return;
            }
            loader.Free(library);
        }

        internal void AssertLoaded()
        {
            Assert.Equal(1, Volatile.Read(ref loads));
            Assert.Equal(0, Volatile.Read(ref frees));
            Assert.NotEqual((nint)0, GetModuleHandleW(Path));
        }

        internal void AssertUnloaded()
        {
            Assert.True(childVerified.WaitOne(0));
            Assert.True(parentVerified.WaitOne(0));
            Assert.Equal(1, Volatile.Read(ref loads));
            Assert.Equal(1, Volatile.Read(ref frees));
            Assert.Equal(0, Volatile.Read(ref prematureFrees));
            Assert.Equal((nint)0, GetModuleHandleW(Path));
        }

        internal void ReleaseAll()
        {
            ReleaseChild.Set();
            ReleaseParent.Set();
        }

        public void Dispose()
        {
            while (deferred.TryDequeue(out nint library))
            {
                loader.Free(library);
            }
            childEntered.Dispose();
            parentEntered.Dispose();
            childVerified.Dispose();
            parentVerified.Dispose();
            ReleaseChild.Dispose();
            ReleaseParent.Dispose();
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
            ExactSpelling = true)]
        private static extern nint GetModuleHandleW(string name);
    }
}
