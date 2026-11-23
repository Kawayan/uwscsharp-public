using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class NativeDllModuleTests
{
    private static readonly string FixturePath = Path.Combine(Path.GetTempPath(), "owned-dll-fixture.dll");

    [Fact]
    public void ScriptLoaderDiagnosesNativeX86WithoutMisclassifyingAnyCpu()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "floating-point-boundary");
        string x86 = Path.Combine(directory, "x86", "DefDllAbiFixture.dll");
        string x64 = Path.Combine(directory, "x64", "DefDllAbiFixture.dll");

        Assert.True(NativeDllImage.IsNativeX86Dll(x86));
        Assert.False(NativeDllImage.IsNativeX86Dll(x64));
        Assert.False(NativeDllImage.IsNativeX86Dll(typeof(NativeDllModuleTests).Assembly.Location));

        BadImageFormatException error = Assert.Throws<BadImageFormatException>(() =>
            NativeDllModule.Load(x86, NativeDllLoader.ScriptDependencies));
        Assert.Contains("32ビットのネイティブDLL", error.Message);
        using var native = NativeDllModule.Load(x64, NativeDllLoader.ScriptDependencies);
        var callSite = new NativeDllCallSite(typeof(int), typeof(int));
        Assert.Equal(-7, native.Resolve("EchoSigned", callSite).Invoke(-7));
    }

    [Fact]
    public void DisposeReleasesOnceAndRejectsRetainedFunctions()
    {
        var loader = new Loader(() => 42);
        var module = NativeDllModule.Load(FixturePath, loader);
        var callSite = new NativeDllCallSite(typeof(int));
        NativeDllModule.Function function = module.Resolve("Value", callSite);
        Assert.Equal(42, function.Invoke());
        Assert.Equal(0, loader.Frees);
        module.Dispose();
        module.Dispose();
        Assert.Equal(1, loader.Frees);
        Assert.Throws<ObjectDisposedException>(() => function.Invoke());
        Assert.Throws<ObjectDisposedException>(() => module.Resolve("Value", callSite));
    }

    [Fact]
    public async Task InFlightCallRetainsModuleAfterDispose()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var loader = new Loader(() =>
        {
            entered.TrySetResult();
            return release.Wait(TimeSpan.FromSeconds(10)) ? 42 : -1;
        });
        using var module = NativeDllModule.Load(FixturePath, loader);
        NativeDllModule.Function function = module.Resolve("Value", new NativeDllCallSite(typeof(int)));
        Task<object?> pending = Task.Run(() => function.Invoke());
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            module.Dispose();
            Assert.Equal(0, loader.Frees);
            Assert.Throws<ObjectDisposedException>(() => function.Invoke());
        }
        finally
        {
            release.Set();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(42, await pending);
        Assert.Equal(1, loader.Frees);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LastOfTwoConcurrentCallsReleasesModule(bool collect)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var firstRelease = new ManualResetEventSlim();
        using var secondRelease = new ManualResetEventSlim();
        int count = 0;
        int[] callbackResults = [int.MinValue, int.MinValue];
        int[] callbackThreads = [0, 0];
        var loader = new Loader(() =>
        {
            int index = Interlocked.Increment(ref count);
            if (index == 2)
            {
                entered.TrySetResult();
            }

            ManualResetEventSlim release = index == 1 ? firstRelease : secondRelease;
            int result = release.Wait(TimeSpan.FromSeconds(10)) ? index : -1;
            if (index is 1 or 2)
            {
                Volatile.Write(ref callbackThreads[index - 1], Environment.CurrentManagedThreadId);
                Volatile.Write(ref callbackResults[index - 1], result);
            }

            return result;
        });
        using var module = NativeDllModule.Load(FixturePath, loader);
        NativeDllModule.Function function = module.Resolve("Value", new NativeDllCallSite(typeof(int)));
        Task<object?> first = Task.Run(() => function.Invoke());
        Task<object?> second = Task.Run(() => function.Invoke());
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            module.Dispose();
            if (collect)
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
            }

            firstRelease.Set();
            Task<object?> completed = await Task.WhenAny(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            object? result = await completed;
            string diagnostic = $"戻り値={result}, 型={result?.GetType().FullName}, "
                + $"callback=[{Volatile.Read(ref callbackResults[0])}, {Volatile.Read(ref callbackResults[1])}], "
                + $"thread=[{Volatile.Read(ref callbackThreads[0])}, {Volatile.Read(ref callbackThreads[1])}], "
                + $"呼出数={Volatile.Read(ref count)}, 解放数={loader.Frees}, 強制GC={collect}";
            Assert.True(result is int value && value == 1, diagnostic);
            Assert.Equal(0, loader.Frees);
        }
        finally
        {
            firstRelease.Set();
            secondRelease.Set();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal([1, 2], (await Task.WhenAll(first, second)).Cast<int>().Order());
        Assert.Equal([1, 2], callbackResults);
        Assert.Equal(1, loader.Frees);
    }

    [Fact]
    public void ReentrantDisposeDoesNotWaitForItsOwnCall()
    {
        NativeDllModule? module = null;
        Loader? loader = null;
        int freesDuringCall = -1;
        loader = new Loader(() =>
        {
            module!.Dispose();
            freesDuringCall = loader!.Frees;
            return 42;
        });
        using (module = NativeDllModule.Load(FixturePath, loader))
        {
            NativeDllModule.Function function = module.Resolve("Value", new NativeDllCallSite(typeof(int)));
            Assert.Equal(42, function.Invoke());
            Assert.Equal(0, freesDuringCall);
            Assert.Equal(1, loader.Frees);
        }
    }

    [Fact]
    public async Task InFlightResolutionRetainsModuleButCannotStartAfterDispose()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var loader = new Loader(() => 42);
        loader.Resolving = () =>
        {
            entered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(10));
        };
        using var module = NativeDllModule.Load(FixturePath, loader);
        Task<NativeDllModule.Function> pending = Task.Run(() => module.Resolve("Value", new NativeDllCallSite(typeof(int))));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            module.Dispose();
            Assert.Equal(0, loader.Frees);
        }
        finally
        {
            release.Set();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(1, loader.Frees);
        NativeDllModule.Function function = await pending;
        Assert.Throws<ObjectDisposedException>(() => function.Invoke());
    }

    [Fact]
    public void MissingExportAndArgumentErrorsReleaseTemporaryReferences()
    {
        var loader = new Loader(() => 42);
        var module = NativeDllModule.Load(FixturePath, loader);
        var callSite = new NativeDllCallSite(typeof(int));
        Assert.Throws<EntryPointNotFoundException>(() => module.Resolve("Missing", callSite));
        NativeDllModule.Function function = module.Resolve("Value", callSite);
        Assert.Throws<ArgumentException>(() => function.Invoke(1));
        Assert.Equal(42, function.Invoke());
        module.Dispose();
        Assert.Equal(1, loader.Frees);
    }

    [Fact]
    public void InvalidNamesDoNotReachLoader()
    {
        var loader = new Loader(() => 42);
        Assert.Throws<ArgumentException>(() => NativeDllModule.Load("relative.dll", loader));
        Assert.Throws<ArgumentException>(() => NativeDllModule.Load(FixturePath + "\0extra", loader));
        Assert.Equal(0, loader.Loads);
        using var module = NativeDllModule.Load(FixturePath, loader);
        var callSite = new NativeDllCallSite(typeof(int));
        Assert.Throws<ArgumentException>(() => module.Resolve("Value\0extra", callSite));
        Assert.Throws<ArgumentException>(() => module.Resolve("", callSite));
        Assert.Equal(0, loader.Resolutions);
    }

    [Fact]
    public void FailedLoadDoesNotFreeAnUnownedHandle()
    {
        var loader = new Loader(() => 42) { FailLoad = true };
        Assert.Throws<DllNotFoundException>(() => NativeDllModule.Load(FixturePath, loader));
        Assert.Equal(0, loader.Frees);
        loader.FailLoad = false;
        using (NativeDllModule.Load(FixturePath, loader))
        {
            Assert.Equal(0, loader.Frees);
        }

        Assert.Equal(1, loader.Frees);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Callback();

    private sealed class Loader(Callback callback) : INativeDllLoader
    {
        private int frees;

        internal int Frees => Volatile.Read(ref frees);

        internal int Loads
        {
            get; private set;
        }

        internal int Resolutions
        {
            get; private set;
        }

        internal bool FailLoad
        {
            get; set;
        }

        internal Action? Resolving
        {
            get; set;
        }

        public nint Load(string path)
        {
            Loads++;
            return FailLoad ? throw new DllNotFoundException(path) : 123;
        }

        public nint GetExport(nint library, string export)
        {
            Resolutions++;
            Resolving?.Invoke();
            return export == "Value"
                ? Marshal.GetFunctionPointerForDelegate(callback)
                : throw new EntryPointNotFoundException(export);
        }

        public void Free(nint library) => Interlocked.Increment(ref frees);
    }
}
