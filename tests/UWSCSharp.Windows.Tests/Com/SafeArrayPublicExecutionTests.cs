using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

/// <summary>
/// 画面・入力・COMサーバーなしで公開SAFEARRAYのNative生成を検査します。
/// </summary>
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SafeArrayPublicExecutionTests
{
    private sealed class NativeEchoRuntime : IComRuntime
    {
        private readonly object root = new();

        public object Create(string name) => root;

        public object GetActive(string name) => root;

        public object? Invoke(object target, string name, object?[] arguments, bool assign)
        {
            Assert.Same(root, target);
            Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
            nint variant = Marshal.AllocCoTaskMem(24);
            Marshal.Copy(new byte[24], 0, variant, 24);
            try
            {
                ComVariantValue.Write(Assert.Single(arguments), variant);
                return ComVariantValue.Read(variant);
            }
            finally
            {
                Marshal.ThrowExceptionForHR(VariantClear(variant));
                Marshal.FreeCoTaskMem(variant);
            }
        }

        public void Release(object target)
        {
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    public async Task PublicSessionCreatesAllSupportedTypesWithBounds(int type)
    {
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        Value value = await session.InvokeAsync(BuiltinFunctionNames.SafeArray,
            [Value.Numeric(-2), Value.Numeric(-1), Value.Numeric(5),
                Value.Numeric(7), Value.Numeric(type)], default);
        Assert.Equal(ValueKind.Array, value.Kind);
        Assert.Equal((ushort)(0x2000 | type), value.NativeVariantType);
        Assert.Equal((ushort)type, value.AsArray().NativeElementType);
        Assert.Equal([-2, 5], value.AsArray().LowerBounds);
        Assert.Equal([-1, 7], value.AsArray().UpperBounds);
        Assert.Equal(6, value.AsArray().Count);
        Assert.Equal(type switch
        {
            8 => ValueKind.String,
            9 or 13 => ValueKind.Nothing,
            10 => ValueKind.Error,
            12 => ValueKind.Empty,
            _ => ValueKind.Number
        }, value.AsArray().Get(-2, 5).Kind);
    }

    [Theory]
    [InlineData("SAFEARRAY()", "0")]
    [InlineData("SAFEARRAY(1)", "0")]
    [InlineData("SAFEARRAY(3,2,3)", "0")]
    [InlineData("SAFEARRAY(0,,3)", "1")]
    [InlineData("SAFEARRAY(-2,-1,5,7,8)", "2")]
    [InlineData("SAFEARRAY(0,1,3,2,3)", "2")]
    [InlineData("SAFEARRAY(3,2,0,1,3)", "0")]
    public async Task TypedLengthUsesTheSameFirstAxisRule(string call, string expected)
    {
        var output = new List<string>();
        await new Interpreter(windows: new WindowsAutomation()).RunAsync(
            $"a={call}\nPRINT LENGTH(a)", output.Add);
        Assert.Equal([expected], output);
    }

    [Fact]
    public async Task PublicScriptKeepsTypedMutationAndFailedAssignmentAtomic()
    {
        var output = new List<string>();
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            a=SAFEARRAY(-2,-1,17)
            a[-2]="7"
            TRY
                a[-2]=256
            EXCEPT
                PRINT "rejected"
            ENDTRY
            Change(a[-1])
            PRINT a[-2]
            PRINT a[-1]
            PRINT LENGTH(a)
            M.a[0]="9"
            PRINT M.a[0]
            PROCEDURE Change(VAR x)
                x="8"
            FEND
            MODULE M
                PUBLIC a=SAFEARRAY(0,0,3)
            ENDMODULE
            """, output.Add);
        Assert.Equal(["rejected", "7", "8", "2", "9"], output);
    }

    [Fact]
    public async Task PublicCreationAcceptsTheMillionBoundaryAndRejectsOverflow()
    {
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        Value value = await session.InvokeAsync(BuiltinFunctionNames.SafeArray,
            [Value.Numeric(0), Value.Numeric(999999), Value.Numeric(17)], default);
        Assert.Equal(1000000, value.AsArray().Count);
        Assert.Equal(0, value.AsArray().Get(999999).Number);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.InvokeAsync(BuiltinFunctionNames.SafeArray,
                [Value.Numeric(0), Value.Numeric(1000000)], default));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(12)]
    public async Task PublicArrayRoundtripKeepsNativeTypeBoundsAndValues(int type)
    {
        var automation = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"),
            comRuntime: new NativeEchoRuntime());
        await using IWindowsAutomationSession session = automation.CreateSession();
        Value root = await session.InvokeAsync(BuiltinFunctionNames.CreateOleObj,
            [Value.String("owned-adapter")], default);
        Value array = await session.InvokeAsync(BuiltinFunctionNames.SafeArray,
            [Value.Numeric(-2), Value.Numeric(-1), Value.Numeric(5),
                Value.Numeric(7), Value.Numeric(type)], default);
        Value replacement = type == 3 ? Value.String("7") : Value.String("日本語");
        Value changed = array with
        {
            Array = array.AsArray().Set([-2, 5], replacement)
        };
        Value copied = await session.InvokeMemberAsync(root, "Echo", [changed], null, default);
        Assert.Equal(changed.NativeVariantType, copied.NativeVariantType);
        Assert.Equal(changed.AsArray().LowerBounds, copied.AsArray().LowerBounds);
        Assert.Equal(changed.AsArray().UpperBounds, copied.AsArray().UpperBounds);
        Assert.Equal(changed.AsArray().Values, copied.AsArray().Values);
    }

    [Fact]
    public async Task PublicSessionRejectsCancellationAndDisposedUse()
    {
        IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.InvokeAsync(BuiltinFunctionNames.SafeArray, [], cancellation.Token));
        }
        finally
        {
            await session.DisposeAsync();
        }
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await session.InvokeAsync(BuiltinFunctionNames.SafeArray, [], default));
    }

    [DllImport("oleaut32.dll")]
    private static extern int VariantClear(nint variant);
}
