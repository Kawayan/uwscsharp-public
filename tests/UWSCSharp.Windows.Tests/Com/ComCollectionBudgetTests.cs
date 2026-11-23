using System.Collections;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[SupportedOSPlatform("windows")]
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ComCollectionBudgetTests
{
    private sealed class Runtime : IComRuntime
    {
        internal readonly ArrayList Collection = [7d];
        internal int Invocations;
        internal Func<object?[], object?>? OnInvoke;

        public object Create(string name) => Collection;

        public object GetActive(string name) => Collection;

        public object? Invoke(object target, string name,
            object?[] arguments, bool assign)
        {
            Invocations++;
            return OnInvoke?.Invoke(arguments);
        }

        public void Release(object target)
        {
        }

        public object? Invoke(object target, string name,
            object?[] arguments, bool assign, IReadOnlyList<bool> byReference) =>
            Invoke(target, name, arguments, assign);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task AdapterReturnAndReferenceOutputsShareBudget(int extra)
    {
        int half = UwscArray.MaximumElements / 2;
        var runtime = new Runtime
        {
            OnInvoke = arguments =>
            {
                arguments[0] = new int[half];
                return new int[half + extra];
            }
        };
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(
            _ => { }, comRuntime: runtime)).RunAsync("""
            o=CREATEOLEOBJ("fixture")
            a=7
            TRY
                result=o.Change(VAR a)
                PRINT LENGTH(a)
                PRINT LENGTH(result)
            EXCEPT
                PRINT TRY_ERRMSG
                PRINT a
            ENDTRY
            """, output.Add);
        Assert.Equal(1, runtime.Invocations);
        if (extra == 0)
        {
            Assert.Equal(["500000", "500000"], output);
        }
        else
        {
            Assert.Equal(2, output.Count);
            Assert.Contains("COM値の累積要素数が上限を超えました", output[0]);
            Assert.Equal("7", output[1]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MemberInputsShareBudgetWithAssignedValue(bool byReference)
    {
        var runtime = new Runtime();
        await using var session = new ComSession(runtime);
        Value target = await session.CreateAsync("fixture", false, default);
        int half = UwscArray.MaximumElements / 2;
        var array = Value.FromArray(UwscArray.CreateBounded(
            [0], [half - 1], Enumerable.Repeat(Value.Empty, half)));
        var one = Value.FromArray(UwscArray.CreateBounded(
            [0], [0], [Value.Empty]));

        async Task InvokeAsync(Value? assigned)
        {
            if (byReference)
            {
                await session.InvokeWithReferencesAsync(target, "Item",
                    [new(array, true), new(array, false)], assigned, default);
            }
            else
            {
                await session.InvokeAsync(target, "Item",
                    [array, array], assigned, default);
            }
        }

        await InvokeAsync(null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeAsync(one));
        Assert.Equal(1, runtime.Invocations);
    }

    [Theory]
    [InlineData(999)]
    [InlineData(1000)]
    public async Task NestedEnumerationLimitKeepsPublishedSnapshot(
        int childLength)
    {
        var runtime = new Runtime();
        await using IWindowsAutomationSession session =
            new WindowsAutomation(_ => { }, comRuntime: runtime)
                .CreateSession();
        Value root = await session.InvokeAsync("CREATEOLEOBJ",
            [Value.String("fixture")], default);
        await session.InvokeAsync("GETOLEITEM", [root], default);
        IReadOnlyList<Value> previous = session.AllOleItems;
        runtime.Collection.Clear();
        int[] child = new int[childLength];
        for (int index = 0; index < 1000; index++)
        {
            runtime.Collection.Add(child);
        }

        if (childLength == 1000)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await session.InvokeAsync("GETOLEITEM", [root], default));
            Assert.Same(previous, session.AllOleItems);
        }
        else
        {
            Assert.Equal(Value.Numeric(1000), await session.InvokeAsync(
                "GETOLEITEM", [root], default));
            Assert.Equal(childLength,
                session.AllOleItems[0].AsArray().Values.Count);
        }
        runtime.Collection.Clear();
        runtime.Collection.Add(9d);
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync(
            "GETOLEITEM", [root], default));
        Assert.Equal(Value.Numeric(9), Assert.Single(session.AllOleItems));
    }

    [Theory]
    [InlineData(999)]
    [InlineData(1000)]
    public void NestedNativeArraysShareReadLimit(int childLength)
    {
        object[] source =
            [.. Enumerable.Repeat<object>(new int[childLength], 1000)];
        nint variant = Marshal.AllocCoTaskMem(24);
        Marshal.Copy(new byte[24], 0, variant, 24);
        try
        {
            Marshal.GetNativeVariantForObject(source, variant);
            if (childLength == 1000)
            {
                Assert.Throws<InvalidOperationException>(() =>
                    ComVariantValue.Read(variant));
            }
            else
            {
                ComArrayValue array = Assert.IsType<ComArrayValue>(
                    ComVariantValue.Read(variant).Value);
                Assert.Equal(1000, array.Values.Count);
            }
        }
        finally
        {
            Marshal.ThrowExceptionForHR(VariantClear(variant));
            Marshal.FreeCoTaskMem(variant);
        }
    }

    [DllImport("oleaut32.dll")]
    private static extern int VariantClear(nint variant);
}
