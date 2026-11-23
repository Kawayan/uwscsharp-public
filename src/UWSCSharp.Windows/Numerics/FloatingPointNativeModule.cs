using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

/// <summary>
/// 所有DLLの寿命を維持し、復元済みのnative演算結果を受け取ります。
/// </summary>
internal sealed class FloatingPointNativeModule : IFloatingPointBuiltins
{
    private const uint SupportedAbiVersion = 1;
    private const string LibraryName = "UWSCSharp.FloatingPoint.Native.dll";
    private const string VersionExport = "GetFloatingPointAbiVersion";
    private const string ReadExport = "GetControlState";
    private const string BinaryExport = "EvaluateBinarySafely";
    private const string BuiltinExport = "EvaluateBuiltinSafely";
    private readonly NativeDllModule module;
    private readonly NativeDllModule.Function read;
    private readonly NativeDllModule.Function binary;
    private readonly NativeDllModule.Function builtin;

    private FloatingPointNativeModule(NativeDllModule module)
    {
        this.module = module;
        uint version = ReadAbiVersion(module);
        if (version != SupportedAbiVersion)
        {
            throw new InvalidOperationException(
                $"浮動小数点DLLのABIが一致しません。必要: {SupportedAbiVersion}、実際: {version}。");
        }

        read = module.Resolve(ReadExport, new NativeDllCallSite(typeof(ulong)));
        binary = module.Resolve(BinaryExport, new NativeDllCallSite(typeof(uint),
            typeof(uint), typeof(uint), typeof(double), typeof(double),
            typeof(nint), typeof(nint)));
        builtin = module.Resolve(BuiltinExport, new NativeDllCallSite(typeof(uint),
            typeof(uint), typeof(uint), typeof(double), typeof(double),
            typeof(nint), typeof(nint)));
    }

    private static uint ReadAbiVersion(NativeDllModule module)
    {
        try
        {
            return (uint)module.Resolve(VersionExport,
                new NativeDllCallSite(typeof(uint))).Invoke()!;
        }
        catch (EntryPointNotFoundException error)
        {
            throw new EntryPointNotFoundException(
                "浮動小数点DLLのABIを確認できません。対応する版へ更新してください。",
                error);
        }
    }

    internal static FloatingPointNativeModule Load(string path,
        INativeDllLoader? loader = null)
        => Create(NativeDllModule.Load(path, loader));

    internal static FloatingPointNativeModule LoadInstalled()
        => Create(NativeDllModule.LoadAssemblyLibrary(LibraryName));

    private static FloatingPointNativeModule Create(NativeDllModule module)
    {
        try
        {
            return new FloatingPointNativeModule(module);
        }
        catch (Exception primary)
        {
            FloatingPointCleanup.Release(module, primary);
            throw;
        }
    }

    public ushort ReadControlWord() => (ushort)(ulong)read.Invoke()!;

    public FloatingPointResult Evaluate(ushort control,
        FloatingPointOperation operation, double left, double right)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            (uint)operation, (uint)FloatingPointOperation.Divide);
        return Invoke(binary, control, (uint)operation, left, right);
    }

    public FloatingPointResult EvaluateBuiltin(ushort control,
        FloatingPointBuiltinOperation operation, double left, double right)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            (uint)operation, (uint)FloatingPointBuiltinOperation.Power);
        if (operation is FloatingPointBuiltinOperation.Sin
                or FloatingPointBuiltinOperation.Cos or FloatingPointBuiltinOperation.Tan
            && double.IsFinite(left) && Math.Abs(left) > Math.PI / 4)
        {
            (int quadrant, double high, double low) =
                FloatingPointAngleReduction.Reduce(left);
            ReducedOperation reduced = (operation, quadrant) switch
            {
                (FloatingPointBuiltinOperation.Sin, 0) => ReducedOperation.Sin,
                (FloatingPointBuiltinOperation.Sin, 1) => ReducedOperation.Cos,
                (FloatingPointBuiltinOperation.Sin, 2) => ReducedOperation.NegativeSin,
                (FloatingPointBuiltinOperation.Sin, 3) => ReducedOperation.NegativeCos,
                (FloatingPointBuiltinOperation.Cos, 0) => ReducedOperation.Cos,
                (FloatingPointBuiltinOperation.Cos, 1) => ReducedOperation.NegativeSin,
                (FloatingPointBuiltinOperation.Cos, 2) => ReducedOperation.NegativeCos,
                (FloatingPointBuiltinOperation.Cos, 3) => ReducedOperation.Sin,
                (_, 0 or 2) => ReducedOperation.Tan,
                _ => ReducedOperation.NegativeCotangent
            };
            return Invoke(builtin, control, (uint)reduced, high, low);
        }

        return Invoke(builtin, control, (uint)operation, left, right);
    }

    private enum ReducedOperation : uint
    {
        Sin = 15,
        NegativeSin,
        Cos,
        NegativeCos,
        Tan,
        NegativeCotangent
    }

    private static FloatingPointResult Invoke(NativeDllModule.Function function,
        ushort control, uint operation, double left, double right)
    {
        nint output = Marshal.AllocCoTaskMem(16);
        try
        {
            uint code = (uint)function.Invoke((uint)control, operation,
                left, right, output, output + 8)!;
            return new(code,
                BitConverter.Int64BitsToDouble(Marshal.ReadInt64(output)),
                unchecked((uint)Marshal.ReadInt32(output, 8)));
        }
        finally
        {
            Marshal.FreeCoTaskMem(output);
        }
    }

    /// <summary>
    /// 新規呼出しを止め、開始済みの呼出し後にDLLを解放します。
    /// </summary>
    public void Dispose() => module.Dispose();
}
