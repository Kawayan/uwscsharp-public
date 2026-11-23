using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// DLLの代わりに所有delegateを使い、ロード寿命とABIの転送を確認します。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class FloatingPointNativeModuleTests
{
    private static string FixturePath => Path.GetFullPath("floating-point-owned.dll");

    [Fact]
    public void ModuleCopiesTheWordOperandsResultAndFaultState()
    {
        var loader = new Loader();
        using var module = FloatingPointNativeModule.Load(FixturePath, loader);
        Assert.Equal((ushort)0x27f, module.ReadControlWord());
        FloatingPointResult result = module.Evaluate(0x87f, FloatingPointOperation.Subtract, 17, 5);
        Assert.Equal(0xc0000090u, result.Code);
        Assert.Equal(12, result.Value);
        Assert.Equal(0xb881027eu, result.FaultState);
        Assert.Equal(0x87fu, loader.Control);
        Assert.Equal(1u, loader.Operation);
    }

    [Fact]
    public void BuiltinExportUsesTheSameOperandAndResultBoundary()
    {
        var loader = new Loader();
        using var module = FloatingPointNativeModule.Load(FixturePath, loader);
        FloatingPointResult result = module.EvaluateBuiltin(
            0x87f, FloatingPointBuiltinOperation.Ln, 17, 5);
        Assert.Equal(0x87fu, loader.Control);
        Assert.Equal(5u, loader.Operation);
        Assert.Equal(12, result.Value);
        Assert.Equal(0xc0000090u, result.Code);
        Assert.Equal(0xb881027eu, result.FaultState);
    }

    [Fact]
    public void MissingBuiltinExportAlsoReleasesThePartialModule()
    {
        var loader = new Loader { MissingBuiltin = true };
        Assert.Throws<EntryPointNotFoundException>(() =>
            FloatingPointNativeModule.Load(FixturePath, loader));
        Assert.Equal(1, loader.Frees);
    }

    [Fact]
    public void MissingAbiVersionRejectsAnOlderDllBeforeResolvingOperations()
    {
        var loader = new Loader { MissingVersion = true };
        EntryPointNotFoundException error = Assert.Throws<EntryPointNotFoundException>(
            () => FloatingPointNativeModule.Load(FixturePath, loader));

        Assert.Contains("ABI", error.Message);
        Assert.Equal(0, loader.OperationLookups);
        Assert.Equal(1, loader.Frees);
    }

    [Fact]
    public void UnexpectedAbiVersionReleasesTheDllWithoutResolvingOperations()
    {
        var loader = new Loader { AbiVersion = 2 };
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => FloatingPointNativeModule.Load(FixturePath, loader));

        Assert.Contains("ABI", error.Message);
        Assert.Equal(0, loader.OperationLookups);
        Assert.Equal(1, loader.Frees);
    }

    [Fact]
    public void MissingRequiredExportReleasesThePartialModule()
    {
        var loader = new Loader { MissingBinary = true };
        Assert.Throws<EntryPointNotFoundException>(
            () => FloatingPointNativeModule.Load(FixturePath, loader));
        Assert.Equal(1, loader.Frees);
    }

    [Fact]
    public void ClosedModuleRejectsReadsAndEvaluations()
    {
        var loader = new Loader();
        var module = FloatingPointNativeModule.Load(FixturePath, loader);
        module.Dispose();
        module.Dispose();
        Assert.Throws<ObjectDisposedException>(() => module.ReadControlWord());
        Assert.Throws<ObjectDisposedException>(
            () => module.Evaluate(0x27f, FloatingPointOperation.Add, 1, 2));
        Assert.Throws<ObjectDisposedException>(() => module.EvaluateBuiltin(
            0x27f, FloatingPointBuiltinOperation.Sqrt, 4, 0));
        Assert.Equal(1, loader.Frees);
    }

    [Fact]
    public async Task ActiveEvaluationKeepsTheDllUntilTheNativeCallReturns()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var loader = new Loader
        {
            DuringCall = () =>
            {
                entered.TrySetResult();
                return release.Wait(TimeSpan.FromSeconds(10));
            }
        };
        using var module = FloatingPointNativeModule.Load(FixturePath, loader);
        Task<FloatingPointResult> pending = Task.Run(() =>
            module.Evaluate(0x27f, FloatingPointOperation.Add, 17, 5));
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

        Assert.Equal(12, (await pending).Value);
        Assert.Equal(1, loader.Frees);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate ulong ReadWord();

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint ReadVersion();

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint Binary(uint control, uint operation,
        double left, double right, nint output, nint fault);

    private sealed class Loader : INativeDllLoader
    {
        private readonly ReadWord read = () => 0x00001fa00000027f;
        private readonly ReadVersion version;
        private readonly Binary binary;
        private int frees;

        internal Loader()
        {
            binary = Evaluate;
            version = () => AbiVersion;
        }

        internal int Frees => Volatile.Read(ref frees);

        internal bool MissingBinary;
        internal bool MissingBuiltin;
        internal bool MissingVersion;
        internal uint AbiVersion = 1;
        internal int OperationLookups;
        internal Func<bool>? DuringCall;
        internal uint Control;
        internal uint Operation;

        public nint Load(string path) => 123;

        public nint GetExport(nint library, string export)
        {
            if (export != "GetFloatingPointAbiVersion")
            {
                OperationLookups++;
            }

            return export switch
            {
                "GetFloatingPointAbiVersion" when !MissingVersion => Marshal.GetFunctionPointerForDelegate(version),
                "GetControlState" => Marshal.GetFunctionPointerForDelegate(read),
                "EvaluateBinarySafely" when !MissingBinary => Marshal.GetFunctionPointerForDelegate(binary),
                "EvaluateBuiltinSafely" when !MissingBuiltin => Marshal.GetFunctionPointerForDelegate(binary),
                _ => throw new EntryPointNotFoundException(export)
            };
        }

        private uint Evaluate(uint control, uint operation,
            double left, double right, nint output, nint fault)
        {
            Control = control;
            Operation = operation;
            bool completed = DuringCall?.Invoke() ?? true;
            Marshal.WriteInt64(output, BitConverter.DoubleToInt64Bits(left - right));
            Marshal.WriteInt32(fault, unchecked((int)0xb881027e));
            return completed ? 0xc0000090 : 0xc0000001;
        }

        public void Free(nint library) => Interlocked.Increment(ref frees);
    }
}
