using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class NativeButtonAccessibleSourceTests
{
    [Fact]
    public void LoadFailureRetriesButLoadedModuleDoesNotResolveAgain()
    {
        var loader = new Library { MissingModule = true };
        using var source = new NativeButtonAccessibleSource(loader);
        Assert.False(source.Available());
        loader.MissingModule = false;
        Assert.True(source.Available());
        Assert.True(source.Available());
        Assert.Equal(2, loader.Loads);
        Assert.Equal(new[] { "AccessibleObjectFromPoint", "AccessibleObjectFromWindow" },
            loader.Exports);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    public void AvailabilityDependsOnlyOnPointExport(bool missingPoint, bool missingWindow,
        bool available)
    {
        var loader = new Library { MissingPoint = missingPoint, MissingWindow = missingWindow };
        using var source = new NativeButtonAccessibleSource(loader);
        Assert.Equal(available, source.Available());
        Assert.Equal(available, source.Available());
        Assert.Equal(1, loader.Loads);
        Assert.Equal(2, loader.Exports.Count);
        if (missingWindow)
        {
            Assert.Throws<EntryPointNotFoundException>(() => source.FromWindow(123, out _));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(unchecked((int)0x80004005))]
    public void CallsPreserveRawResultsAndClientArguments(int result)
    {
        var loader = new Library { Result = result };
        using var source = new NativeButtonAccessibleSource(loader);
        NativeButtonVariant child = default;
        Assert.Equal(result, source.FromPoint(new Native.Point(7, 9), out nint point, ref child));
        Assert.Equal((nint)11, point);
        Assert.Equal(5, child.Integer);
        Assert.Equal(result, source.FromWindow(123, out nint root));
        Assert.Equal((nint)12, root);
        Assert.Equal((nint)123, loader.Root);
        Assert.Equal(0xfffffffcu, loader.ObjectId);
        Assert.Equal(NativeButtonAccessibleApi.AccessibleId, loader.Id);
    }

    [Fact]
    public void DisposalFreesLoadedModuleOnceAndPreventsCalls()
    {
        var loader = new Library();
        var source = new NativeButtonAccessibleSource(loader);
        Assert.True(source.Available());
        source.Dispose();
        source.Dispose();
        Assert.Equal(1, loader.Frees);
        Assert.Throws<ObjectDisposedException>(() => source.Available());
    }

    [Fact]
    public void BackendUnavailablePreservesRoleAndReturnsMinusOne()
    {
        var loader = new Library { MissingModule = true };
        var source = new NativeButtonAccessibleSource(loader);
        using var backend = new NativeButtonAccessibleBackend(
            new OwnedListSelectionPlatform(),
            new NativeButtonPointWindow(new NativeButtonPointWindowPlatform()), source);
        int role = 91;
        Assert.Equal(-1, backend.Read(1, 2, default, ref role, out bool action));
        Assert.Equal(91, role);
        Assert.False(action);
        Assert.Empty(loader.Exports);
    }

    private sealed class Library : IButtonAccessibleLibrary
    {
        internal bool MissingModule;
        internal bool MissingPoint;
        internal bool MissingWindow;
        internal int Result;
        internal int Loads;
        internal int Frees;
        internal nint Root;
        internal uint ObjectId;
        internal Guid Id;
        internal readonly List<string> Exports = [];
        private readonly List<Delegate> callbacks = [];

        public nint Load()
        {
            Loads++;
            return MissingModule ? 0 : 10;
        }

        public nint Export(nint library, string name)
        {
            Assert.Equal((nint)10, library);
            Exports.Add(name);
            if ((name == "AccessibleObjectFromPoint" && MissingPoint)
                || (name == "AccessibleObjectFromWindow" && MissingWindow))
            {
                return 0;
            }

            Delegate callback = name == "AccessibleObjectFromPoint"
                ? new NativeButtonAccessibleSource.PointCall(Point)
                : new NativeButtonAccessibleSource.WindowCall(Window);
            callbacks.Add(callback);
            return Marshal.GetFunctionPointerForDelegate(callback);
        }

        private int Point(Native.Point point, out nint result, ref NativeButtonVariant child)
        {
            result = 11;
            child = NativeButtonVariant.Child(5);
            return Result;
        }

        private int Window(nint root, uint objectId, ref Guid id, out nint result)
        {
            Root = root;
            ObjectId = objectId;
            Id = id;
            result = 12;
            return Result;
        }

        public void Free(nint library)
        {
            Assert.Equal((nint)10, library);
            Frees++;
            GC.KeepAlive(callbacks);
        }
    }
}
