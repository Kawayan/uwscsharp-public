using Xunit;

namespace UWSCSharp.Windows.Tests;

// 偽の演算部品だけを使い、物理threadのCWやGUIを変更しません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class FloatingPointEnvironmentTests
{
    [Fact]
    public void OwnersKeepTheirControlWordsAndZeroRestoresTheirOwnInitialWord()
    {
        var firstApi = new Arithmetic(0x27f);
        var secondApi = new Arithmetic(0x37f);
        using var first = new FloatingPointEnvironment(firstApi);
        using var second = new FloatingPointEnvironment(secondApi);
        first.SetControlWord(0x87f);
        _ = first.Evaluate(FloatingPointOperation.Add, 1, 2);
        _ = second.Evaluate(FloatingPointOperation.Multiply, 3, 4);
        Assert.Equal((ushort)0x87f, firstApi.LastControl);
        Assert.Equal((ushort)0x37f, secondApi.LastControl);
        first.SetControlWord(0);
        Assert.Equal((ushort)0x27f, first.CurrentControlWord);
        Assert.Equal((ushort)0x37f, second.CurrentControlWord);
        Assert.Equal(1, firstApi.Reads);
        Assert.Equal(1, secondApi.Reads);
    }

    [Fact]
    public void NativeStatusAndNonfiniteValuesAreCopiedWithoutScriptConversion()
    {
        var api = new Arithmetic(0x27f)
        {
            Result = new(0xc000008e, double.PositiveInfinity, 0xb084027b)
        };
        using var owner = new FloatingPointEnvironment(api);
        FloatingPointResult result = owner.Evaluate(FloatingPointOperation.Divide, 1, 0);
        Assert.False(result.Succeeded);
        Assert.Equal(0xc000008eu, result.Code);
        Assert.True(double.IsPositiveInfinity(result.Value));
        Assert.Equal((ushort)0x27b, result.FaultControlWord);
        Assert.Equal((ushort)0xb084, result.FaultStatusWord);
    }

    [Fact]
    public void FailedInitializationReleasesTheOwnedBackendAndKeepsBothErrors()
    {
        var primary = new InvalidOperationException("初期CW取得の失敗");
        var cleanup = new InvalidOperationException("演算部品解放の失敗");
        var api = new Arithmetic(0x27f)
        {
            ReadFailure = primary,
            DisposeFailure = cleanup
        };
        Assert.Same(primary, Assert.Throws<InvalidOperationException>(
            () => new FloatingPointEnvironment(api)));
        Assert.Same(cleanup, primary.Data["UWSCSharp.FloatingPointCleanup"]);
        Assert.Equal(1, api.Disposals);
    }

    [Fact]
    public void DisposalRejectsEveryNewOperationAndIsIdempotent()
    {
        var api = new Arithmetic(0x27f);
        var owner = new FloatingPointEnvironment(api);
        owner.Dispose();
        owner.Dispose();
        Assert.Throws<ObjectDisposedException>(() => owner.SetControlWord(0));
        Assert.Throws<ObjectDisposedException>(() => owner.CurrentControlWord);
        Assert.Throws<ObjectDisposedException>(() => owner.Evaluate(FloatingPointOperation.Add, 1, 2));
        Assert.Equal(1, api.Disposals);
    }

    [Fact]
    public void CurrentWordsRemainIndependentWhileFutureForksUseTheLatestDefault()
    {
        var api = new Arithmetic(0x27f);
        using var root = new FloatingPointEnvironment(api);
        root.SetControlWord(0x87f);
        using FloatingPointEnvironment first = root.ForkForThread();
        Assert.Equal((ushort)0x87f, first.CurrentControlWord);
        first.SetControlWord(0x47f);
        Assert.Equal((ushort)0x87f, root.CurrentControlWord);
        using FloatingPointEnvironment second = root.ForkForThread();
        Assert.Equal((ushort)0x47f, second.CurrentControlWord);
        first.SetControlWord(0);
        Assert.Equal((ushort)0x27f, first.CurrentControlWord);
        Assert.Equal((ushort)0x47f, second.CurrentControlWord);
        using FloatingPointEnvironment third = second.ForkForThread();
        Assert.Equal((ushort)0x27f, third.CurrentControlWord);
        Assert.Equal(1, api.Reads);
    }

    [Fact]
    public void ClosingTheRootKeepsExistingSubjectsAliveUntilTheirLastRelease()
    {
        var api = new Arithmetic(0x27f);
        var root = new FloatingPointEnvironment(api);
        FloatingPointEnvironment first = root.ForkForThread();
        FloatingPointEnvironment second = root.ForkForThread();
        root.Dispose();
        Assert.Equal(0, api.Disposals);
        Assert.Throws<ObjectDisposedException>(() => second.ForkForThread());
        first.SetControlWord(0x87f);
        _ = first.Evaluate(FloatingPointOperation.Add, 1, 2);
        Assert.Equal((ushort)0x87f, api.LastControl);
        first.Dispose();
        Assert.Equal(0, api.Disposals);
        second.Dispose();
        second.Dispose();
        Assert.Equal(1, api.Disposals);
    }

    [Fact]
    public void DeferredOwnerRetriesFailedLoadAndInheritsOnlyTheLatestActivation()
    {
        var api = new Arithmetic(0x27f);
        int loads = 0;
        using var root = new FloatingPointEnvironment(() =>
        {
            if (++loads == 1)
            {
                throw new DllNotFoundException("テスト用DLLがありません。");
            }

            return api;
        });
        using FloatingPointEnvironment before = root.ForkForThread();
        Assert.Throws<DllNotFoundException>(() => before.SetControlWord(0x87f));
        Assert.False(before.IsActive);
        Assert.False(root.IsActive);

        before.SetControlWord(0x87f);
        using FloatingPointEnvironment after = root.ForkForThread();
        Assert.Equal((ushort)0x87f, after.CurrentControlWord);
        root.SetControlWord(0);
        using FloatingPointEnvironment reset = root.ForkForThread();
        Assert.False(reset.IsActive);
        Assert.True(after.IsActive);
        Assert.Equal(2, loads);
        root.Dispose();
        before.Dispose();
        after.Dispose();
        Assert.Equal(0, api.Disposals);
        reset.Dispose();
        Assert.Equal(1, api.Disposals);
    }

    [Fact]
    public void ReleasingAChildDoesNotCloseTheRootOrReleaseTheSharedBackend()
    {
        var api = new Arithmetic(0x27f);
        using var root = new FloatingPointEnvironment(api);
        FloatingPointEnvironment first = root.ForkForThread();
        first.Dispose();
        Assert.Equal(0, api.Disposals);
        Assert.Throws<ObjectDisposedException>(() => first.ForkForThread());
        using FloatingPointEnvironment second = root.ForkForThread();
        _ = root.Evaluate(FloatingPointOperation.Subtract, 1, 2);
        Assert.Equal((ushort)0x27f, api.LastControl);
    }

    private sealed class Arithmetic(ushort initial) : IFloatingPointArithmetic
    {
        internal int Reads;
        internal int Disposals;
        internal ushort LastControl;
        internal Exception? ReadFailure;
        internal Exception? DisposeFailure;
        internal FloatingPointResult Result;

        public ushort ReadControlWord()
        {
            Reads++;
            if (ReadFailure is not null)
            {
                throw ReadFailure;
            }

            return initial;
        }

        public FloatingPointResult Evaluate(ushort control,
            FloatingPointOperation operation, double left, double right)
        {
            LastControl = control;
            return Result;
        }

        public void Dispose()
        {
            Disposals++;
            if (DisposeFailure is not null)
            {
                throw DisposeFailure;
            }
        }
    }
}
