using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[SupportedOSPlatform("windows")]
[Collection("UIA desktop")]
public sealed partial class NativeButtonAccessibleApiTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(unchecked((int)0x80004005))]
    public void CallsPreserveHResultAndVariantTags(int result)
    {
        using var fixture = new Fixture { Result = result };
        var child = NativeButtonVariant.Child(-123);
        NativeButtonVariant value = default;
        Assert.Equal(result, NativeButtonAccessibleApi.Role(
            fixture.Pointer, child, ref value));
        Assert.Equal((ushort)3, value.Type);
        Assert.Equal(44, value.Integer);
        Assert.Equal(result, NativeButtonAccessibleApi.State(
            fixture.Pointer, child, ref value));
        Assert.Equal((ushort)10, value.Type);
        Assert.Equal(-42, value.Integer);
        Assert.Equal(result, NativeButtonAccessibleApi.Location(
            fixture.Pointer, child, out int left, out int top,
            out int width, out int height));
        Assert.Equal((-10, -20, 30, 40), (left, top, width, height));
        Assert.Equal(result, NativeButtonAccessibleApi.Select(
            fixture.Pointer, 1, child));
        Assert.Equal(result, NativeButtonAccessibleApi.DefaultAction(
            fixture.Pointer, child));
        Assert.Equal(new[] { "role:-123", "state:-123", "location:-123",
            "select:1:-123", "action:-123" }, fixture.Events);
        Assert.Equal(0, NativeButtonAccessibleApi.Clear(ref value));
        Assert.Equal((ushort)0, value.Type);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(unchecked((int)0x80004005))]
    public void EnumerationDoesNotDiscardValuesOrInterpretFetched(int result)
    {
        using var fixture = new Fixture { Result = result };
        var value = NativeButtonVariant.Child(77);
        Assert.Equal(result, NativeButtonAccessibleApi.Reset(fixture.Pointer));
        Assert.Equal(result, NativeButtonAccessibleApi.Next(
            fixture.Pointer, ref value, out uint fetched));
        Assert.Equal(0u, fetched);
        Assert.Equal((ushort)3, value.Type);
        Assert.Equal(77, value.Integer);
        fixture.WriteNext = true;
        Assert.Equal(result, NativeButtonAccessibleApi.Next(
            fixture.Pointer, ref value, out fetched));
        Assert.Equal(2u, fetched);
        Assert.Equal(-8, value.Integer);
        Assert.Equal(new[] { "reset", "next:1", "next:1" }, fixture.Events);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(unchecked((int)0x80004005))]
    public void OutPointersRemainOwnedEvenWithFailureResult(int result)
    {
        using var fixture = new Fixture { Result = result };
        Assert.Equal(result, NativeButtonAccessibleApi.ChildCount(
            fixture.Pointer, out int count));
        Assert.Equal(7, count);
        nint child = 0;
        nint queried = 0;
        try
        {
            Assert.Equal(result, NativeButtonAccessibleApi.Child(
                fixture.Pointer, NativeButtonVariant.Child(2), out child));
            Assert.Equal(fixture.Pointer, child);
            Assert.Equal(result, NativeButtonAccessibleApi.Query(
                child, NativeButtonAccessibleApi.EnumeratorId, out queried));
            Assert.Equal(NativeButtonAccessibleApi.EnumeratorId, fixture.QueryId);
            Assert.Equal(3, fixture.References);
        }
        finally
        {
            NativeButtonAccessibleApi.Release(ref queried);
            NativeButtonAccessibleApi.Release(ref child);
        }

        Assert.Equal(0, child);
        Assert.Equal(0, queried);
        Assert.Equal(1, fixture.References);
    }

    [Fact]
    public void AssignmentAddsReferenceBeforeReplacingAndReleasing()
    {
        using var first = new Fixture();
        using var second = new Fixture();
        nint slot = 0;
        try
        {
            NativeButtonAccessibleApi.Assign(ref slot, first.Pointer);
            Assert.Equal(2, first.References);
            NativeButtonAccessibleApi.Assign(ref slot, first.Pointer);
            Assert.Equal(new[] { "add", "add", "release" }, first.Events);
            Assert.Equal(2, first.References);
            first.OnRelease = () => Assert.Equal(second.Pointer, slot);
            NativeButtonAccessibleApi.Assign(ref slot, second.Pointer);
            Assert.Equal(1, first.References);
            Assert.Equal(2, second.References);
            second.OnRelease = () => Assert.Equal(0, slot);
            NativeButtonAccessibleApi.Release(ref slot);
            NativeButtonAccessibleApi.Release(ref slot);
            Assert.Equal(1, second.References);
        }
        finally
        {
            first.OnRelease = null;
            second.OnRelease = null;
            NativeButtonAccessibleApi.Release(ref slot);
        }

        Assert.Null(first.Failure);
        Assert.Null(second.Failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VariantClearOwnsDispatchButNotByReferenceDispatch(bool byReference)
    {
        using var fixture = new Fixture();
        nint indirect = Marshal.AllocCoTaskMem(IntPtr.Size);
        var value = new NativeButtonVariant
        {
            Type = (ushort)(byReference ? 0x4009 : 9),
            Pointer = byReference ? indirect : fixture.Pointer
        };
        Marshal.WriteIntPtr(indirect, fixture.Pointer);
        if (!byReference)
        {
            Marshal.AddRef(fixture.Pointer);
        }

        try
        {
            Assert.Equal(0, NativeButtonAccessibleApi.Clear(ref value));
            Assert.Equal((ushort)0, value.Type);
            Assert.Equal(1, fixture.References);
            Assert.Equal(byReference ? Array.Empty<string>() : ["add", "release"],
                fixture.Events);
        }
        finally
        {
            NativeButtonAccessibleApi.Clear(ref value);
            Marshal.FreeCoTaskMem(indirect);
        }
    }

    [Fact]
    public void ReadsOwnedNativeButtonWithoutClrVariantConversion()
    {
        Assert.Equal(IntPtr.Size == 8 ? 24 : 16,
            Marshal.SizeOf<NativeButtonVariant>());
        using var fixture = new ButtonControlTests.Fixture();
        nint button = fixture.Child("BUTTON", "MSAA", 3, 10);
        Guid id = NativeButtonAccessibleApi.AccessibleId;
        nint accessible = 0;
        NativeButtonVariant role = default;
        NativeButtonVariant state = default;
        try
        {
            Assert.Equal(0, NativeButtonAccessibleApi.FromWindow(
                button, 0xfffffffcu, ref id, out accessible));
            Assert.NotEqual(0, accessible);
            Assert.Equal(0, NativeButtonAccessibleApi.Role(accessible,
                NativeButtonVariant.Child(0), ref role));
            Assert.Equal((ushort)3, role.Type);
            Assert.Equal(44, role.Integer);
            Assert.Equal(0, NativeButtonAccessibleApi.State(accessible,
                NativeButtonVariant.Child(0), ref state));
            Assert.Equal((ushort)3, state.Type);
            Assert.Equal(0, NativeButtonAccessibleApi.Location(accessible,
                NativeButtonVariant.Child(0), out _, out _,
                out int width, out int height));
            Assert.True(width > 0);
            Assert.True(height > 0);
        }
        finally
        {
            NativeButtonAccessibleApi.Clear(ref state);
            NativeButtonAccessibleApi.Clear(ref role);
            NativeButtonAccessibleApi.Release(ref accessible);
        }
    }

    private sealed class Fixture : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint Reference(nint self);

        private readonly nint table;
        private readonly List<Delegate> callbacks = [];

        internal nint Pointer
        {
            get;
        }

        internal int Result
        {
            get; init;
        }

        internal int? RoleResult
        {
            get; init;
        }

        internal int? StateResult
        {
            get; init;
        }

        internal int? LocationResult
        {
            get; init;
        }

        internal NativeButtonVariant RoleValue
        {
            get; set;
        } = NativeButtonVariant.Child(44);

        internal NativeButtonVariant StateValue
        {
            get; set;
        } = new()
        {
            Type = 10,
            Integer = -42
        };

        internal (int Left, int Top, int Width, int Height) Bounds
        {
            get; init;
        } = (-10, -20, 30, 40);

        internal Action? Selected
        {
            get; init;
        }

        internal ushort SeenChildType
        {
            get; private set;
        }

        internal int References
        {
            get; private set;
        } = 1;

        internal bool WriteNext
        {
            get; set;
        }

        internal Guid QueryId
        {
            get; private set;
        }

        internal nint? Enumerator
        {
            get; init;
        }

        internal int CountValue
        {
            get; init;
        } = 7;

        internal Func<int, (int Result, nint Pointer)>? ChildProvider
        {
            get; init;
        }

        internal NativeButtonAccessibleApi.NextCall? NextProvider
        {
            get; init;
        }

        internal Action? OnRelease
        {
            get; set;
        }

        internal Exception? Failure
        {
            get; private set;
        }

        internal List<string> Events
        {
            get;
        } = [];

        internal Fixture()
        {
            table = Marshal.AllocCoTaskMem(26 * IntPtr.Size);
            Marshal.Copy(new byte[26 * IntPtr.Size], 0, table, 26 * IntPtr.Size);
            Pointer = Marshal.AllocCoTaskMem(IntPtr.Size);
            Marshal.WriteIntPtr(Pointer, table);
            Set(0, new NativeButtonAccessibleApi.QueryCall(Query));
            Set(1, new Reference(AddRef));
            Set(2, new Reference(Release));
            Set(3, new NativeButtonAccessibleApi.NextCall(Next));
            Set(5, new NativeButtonAccessibleApi.ResetCall(Reset));
            Set(8, new NativeButtonAccessibleApi.CountCall(Count));
            Set(9, new NativeButtonAccessibleApi.ChildCall(Child));
            Set(13, new NativeButtonAccessibleApi.ValueCall(Role));
            Set(14, new NativeButtonAccessibleApi.ValueCall(State));
            Set(21, new NativeButtonAccessibleApi.SelectCall(Select));
            Set(22, new NativeButtonAccessibleApi.LocationCall(Location));
            Set(25, new NativeButtonAccessibleApi.ActionCall(Action));
        }

        private void Set(int slot, Delegate callback)
        {
            callbacks.Add(callback);
            Marshal.WriteIntPtr(table, slot * IntPtr.Size,
                Marshal.GetFunctionPointerForDelegate(callback));
        }

        private uint AddRef(nint self)
        {
            Events.Add("add");
            return (uint)++References;
        }

        private uint Release(nint self)
        {
            Events.Add("release");
            try
            {
                OnRelease?.Invoke();
            }
            catch (Exception ex)
            {
                Failure = ex;
            }

            return (uint)--References;
        }

        private int Query(nint self, ref Guid id, out nint result)
        {
            QueryId = id;
            result = id == NativeButtonAccessibleApi.EnumeratorId
                ? Enumerator ?? self : self;
            if (result != 0)
            {
                Marshal.AddRef(result);
            }

            return Result;
        }

        private int Count(nint self, out int count)
        {
            count = CountValue;
            return Result;
        }

        private int Child(nint self, NativeButtonVariant child, out nint result)
        {
            if (ChildProvider is not null)
            {
                Events.Add("child:" + child.Integer);
                (int status, nint pointer) = ChildProvider(child.Integer);
                result = pointer;
                if (pointer != 0)
                {
                    Marshal.AddRef(pointer);
                }

                return status;
            }

            result = self;
            AddRef(self);
            return Result;
        }

        private int Role(nint self, NativeButtonVariant child,
            ref NativeButtonVariant result)
        {
            Events.Add("role:" + child.Integer);
            SeenChildType = child.Type;
            result = RoleValue;
            if (result.Type == 9 && result.Pointer != 0)
            {
                Marshal.AddRef(result.Pointer);
            }

            return RoleResult ?? Result;
        }

        private int State(nint self, NativeButtonVariant child,
            ref NativeButtonVariant result)
        {
            Events.Add("state:" + child.Integer);
            result = StateValue;
            return StateResult ?? Result;
        }

        private int Location(nint self, out int left, out int top,
            out int width, out int height, NativeButtonVariant child)
        {
            Events.Add("location:" + child.Integer);
            (left, top, width, height) = Bounds;
            return LocationResult ?? Result;
        }

        private int Select(nint self, int flags, NativeButtonVariant child)
        {
            Events.Add($"select:{flags}:{child.Integer}");
            Selected?.Invoke();
            return Result;
        }

        private int Action(nint self, NativeButtonVariant child)
        {
            Events.Add("action:" + child.Integer);
            return Result;
        }

        private int Next(nint self, uint count,
            ref NativeButtonVariant result, out uint fetched)
        {
            Events.Add("next:" + count);
            if (NextProvider is not null)
            {
                return NextProvider(self, count, ref result, out fetched);
            }

            fetched = WriteNext ? 2u : 0u;
            if (WriteNext)
            {
                result = NativeButtonVariant.Child(-8);
            }

            return Result;
        }

        private int Reset(nint self)
        {
            Events.Add("reset");
            return Result;
        }

        public void Dispose()
        {
            int remaining = References;
            Marshal.FreeCoTaskMem(Pointer);
            Marshal.FreeCoTaskMem(table);
            GC.KeepAlive(callbacks);
            Assert.Equal(1, remaining);
        }
    }
}
