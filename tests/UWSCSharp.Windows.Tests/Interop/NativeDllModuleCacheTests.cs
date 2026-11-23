using System.Runtime.InteropServices;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// fakeローダーと入力送信を使い、画面・入力・実DLLを操作しません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class NativeDllModuleCacheTests
{
    private static readonly string Library = Path.Combine(Path.GetTempPath(), "cached-native.dll");

    [Fact]
    public void NormalizedPathSharesLoadButResolvesEachExport()
    {
        using var loader = new Loader();
        using var cache = new NativeDllModuleCache(loader);
        NativeDllModule.Function first = cache.Resolve(Library, "First", new(typeof(int)));
        NativeDllModule.Function second = cache.Resolve(
            Path.Combine(Path.GetDirectoryName(Library)!, ".", Path.GetFileName(Library)),
            "Second", new(typeof(int)));
        Assert.Equal(7, first.Invoke());
        Assert.Equal(7, second.Invoke());
        Assert.Equal(1, loader.Loads);
        Assert.Equal(2, loader.Exports);
        cache.Dispose();
        cache.Dispose();
        Assert.Equal(1, loader.Frees);
        Assert.Throws<ObjectDisposedException>(() => first.Invoke());
        Assert.Throws<ObjectDisposedException>(() => cache.Resolve(Library, "First", new(typeof(int))));
    }

    [Fact]
    public async Task ConcurrentRequestsLoadOnce()
    {
        using var loader = new Loader();
        using var cache = new NativeDllModuleCache(loader);
        Task<NativeDllModule.Function>[] requests = [.. Enumerable.Range(0, 16).Select(_ =>
            Task.Run(() => cache.Resolve(Library, "Call", new(typeof(int)))))];
        NativeDllModule.Function[] functions = await Task.WhenAll(requests);
        Assert.Equal(1, loader.Loads);
        Assert.All(functions, function => Assert.Equal(7, function.Invoke()));
    }

    [Fact]
    public void LoadFailureCanRetryAndMissingExportKeepsLoadedModule()
    {
        using var loader = new Loader { FailLoad = true };
        using var cache = new NativeDllModuleCache(loader);
        Assert.Throws<DllNotFoundException>(() => cache.Resolve(Library, "Call", new(typeof(int))));
        loader.FailLoad = false;
        Assert.Throws<EntryPointNotFoundException>(() => cache.Resolve(Library, "Missing", new(typeof(int))));
        Assert.Equal(7, cache.Resolve(Library, "Call", new(typeof(int))).Invoke());
        Assert.Equal(2, loader.Loads);
        cache.Dispose();
        Assert.Equal(1, loader.Frees);
    }

    [Fact]
    public void ReentrantDisposalDuringLoadReleasesLateModule()
    {
        using var loader = new Loader();
        using var cache = new NativeDllModuleCache(loader);
        loader.OnLoad = cache.Dispose;
        Assert.Throws<ObjectDisposedException>(() => cache.Resolve(Library, "Call", new(typeof(int))));
        Assert.Equal(1, loader.Loads);
        Assert.Equal(0, loader.Exports);
        Assert.Equal(1, loader.Frees);
    }

    [Fact]
    public async Task DisposalDoesNotWaitForAnInProgressLoad()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var loader = new Loader();
        using var cache = new NativeDllModuleCache(loader);
        loader.OnLoad = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException();
            }
        };
        Task pending = Task.Run(() =>
            Assert.Throws<ObjectDisposedException>(() => cache.Resolve(Library, "Call", new(typeof(int)))));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            cache.Dispose();
            Assert.Equal(0, loader.Frees);
        }
        finally
        {
            release.Set();
        }

        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, loader.Frees);
    }

    [Fact]
    public void ActiveFunctionRetainsModuleAfterCacheDisposal()
    {
        using var loader = new Loader();
        using var cache = new NativeDllModuleCache(loader);
        loader.Callback = () =>
        {
            cache.Dispose();
            return loader.Frees;
        };
        Assert.Equal(0, cache.Resolve(Library, "Call", new(typeof(int))).Invoke());
        Assert.Equal(1, loader.Frees);
    }

    [Fact]
    public void InvalidRequestsDoNotLoad()
    {
        using var loader = new Loader();
        using var cache = new NativeDllModuleCache(loader);
        Assert.Throws<ArgumentException>(() => cache.Resolve("relative.dll", "Call", new(typeof(int))));
        Assert.Throws<ArgumentException>(() => cache.Resolve(Library, "Bad\0Name", new(typeof(int))));
        Assert.Throws<ArgumentNullException>(() => cache.Resolve(Library, "Call", null!));
        Assert.Equal(0, loader.Loads);
    }

    [Fact]
    public async Task WorkersShareModulesAndRootWaitsForTheirExit()
    {
        using var loader = new Loader();
        var automation = new WindowsAutomation(_ => { }) { NativeDllLoader = loader };
        IWindowsAutomationSession root = automation.CreateSession();
        IWindowsAutomationSession worker = root.Fork();
        Task? closing = null;
        try
        {
            NativeDllModule.Function first = ((INativeDllModuleSource)root)
                .ResolveNativeDll(Library, "Call", new(typeof(int)));
            NativeDllModule.Function second = ((INativeDllModuleSource)worker)
                .ResolveNativeDll(Library, "Call", new(typeof(int)));
            Assert.Equal(1, loader.Loads);
            closing = root.DisposeAsync().AsTask();
            Assert.False(closing.IsCompleted);
            Assert.Equal(0, loader.Frees);
            Assert.Equal(7, second.Invoke());
            await worker.DisposeAsync();
            await closing.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, loader.Frees);
            Assert.Throws<ObjectDisposedException>(() => first.Invoke());
            Assert.Throws<ObjectDisposedException>(() => ((INativeDllModuleSource)worker)
                .ResolveNativeDll(Library, "Call", new(typeof(int))));
        }
        finally
        {
            await worker.DisposeAsync();
            await root.DisposeAsync();
        }
    }

    [Fact]
    public async Task SeparateRunsOwnSeparateReferences()
    {
        using var loader = new Loader();
        var automation = new WindowsAutomation(_ => { }) { NativeDllLoader = loader };
        await using IWindowsAutomationSession first = automation.CreateSession();
        await using IWindowsAutomationSession second = automation.CreateSession();
        _ = ((INativeDllModuleSource)first).ResolveNativeDll(Library, "Call", new(typeof(int)));
        NativeDllModule.Function remaining = ((INativeDllModuleSource)second)
            .ResolveNativeDll(Library, "Call", new(typeof(int)));
        Assert.Equal(2, loader.Loads);
        await first.DisposeAsync();
        Assert.Equal(1, loader.Frees);
        Assert.Equal(7, remaining.Invoke());
        await second.DisposeAsync();
        Assert.Equal(2, loader.Frees);
    }

    private sealed class Loader : INativeDllLoader, IDisposable
    {
        private readonly NativeCall native;
        internal int Loads;
        internal int Exports;
        internal int Frees;
        internal bool FailLoad;
        internal Action? OnLoad;
        internal Func<int> Callback = () => 7;

        internal Loader()
        {
            native = () => Callback();
        }

        public nint Load(string path)
        {
            Interlocked.Increment(ref Loads);
            OnLoad?.Invoke();
            return FailLoad ? throw new DllNotFoundException(path) : (nint)1;
        }

        public nint GetExport(nint library, string export)
        {
            Interlocked.Increment(ref Exports);
            return export == "Missing" ? throw new EntryPointNotFoundException(export)
                : Marshal.GetFunctionPointerForDelegate(native);
        }

        public void Free(nint library) => Interlocked.Increment(ref Frees);

        public void Dispose() => GC.KeepAlive(native);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int NativeCall();
    }
}
