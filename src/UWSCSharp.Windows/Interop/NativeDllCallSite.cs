using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// 変換済みの数値・ポインターを指定したABIで呼び出します。
/// </summary>
internal sealed class NativeDllCallSite
{
    private readonly Type[] parameterTypes;
    private readonly Func<nint, object?[], object?> invoke;

    internal NativeDllCallSite(Type returnType, params Type[] parameterTypes)
        : this(Architecture.X64, CallingConvention.Winapi, returnType, parameterTypes)
    {
    }

    internal static NativeDllCallSite CreateX86(CallingConvention convention,
        Type returnType, params Type[] parameterTypes) =>
        CreateX86(convention, returnType, parameterTypes, null);

    internal static NativeDllCallSite CreateX86(CallingConvention convention,
        Type returnType, Type[] parameterTypes, ushort? controlWord,
        Action<ushort>? captureControl = null)
    {
        if (convention is not (CallingConvention.Cdecl or CallingConvention.StdCall))
        {
            throw new ArgumentOutOfRangeException(nameof(convention));
        }

        return new NativeDllCallSite(Architecture.X86, convention, returnType,
            parameterTypes, controlWord, captureControl);
    }

    private NativeDllCallSite(Architecture architecture, CallingConvention convention,
        Type returnType, Type[] parameterTypes, ushort? controlWord = null,
        Action<ushort>? captureControl = null)
    {
        ArgumentNullException.ThrowIfNull(returnType);
        ArgumentNullException.ThrowIfNull(parameterTypes);
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != architecture)
        {
            throw new PlatformNotSupportedException(
                architecture == Architecture.X64
                    ? "ネイティブDLLの直接呼出しはWindows x64で実装しています。"
                    : "ネイティブDLLのx86呼出しにはWindows x86プロセスが必要です。");
        }

        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            throw new PlatformNotSupportedException("ネイティブDLLの呼出しには動的コード生成が必要です。");
        }

        this.parameterTypes = [.. parameterTypes];
        if (returnType != typeof(void) && !NativeDllScalarCodec.IsSupported(returnType))
        {
            throw new ArgumentException("戻り型はABIの数値・ポインター型かvoidで指定してください。", nameof(returnType));
        }

        bool hasExtendedArgument = false;
        foreach (Type type in this.parameterTypes)
        {
            if (architecture == Architecture.X86
                && type == typeof(NativeDllExtendedArgument))
            {
                hasExtendedArgument = true;
                continue;
            }

            if (!NativeDllScalarCodec.IsSupported(type))
            {
                throw new ArgumentException("引数型はABIの数値・ポインター型で指定してください。", nameof(parameterTypes));
            }
        }

        if (controlWord is not null || hasExtendedArgument || captureControl is not null)
        {
            invoke = (address, arguments) => NativeDllX86Control.Invoke(
                address, returnType, arguments, controlWord, captureControl);
            return;
        }

        // calliの署名とcookieを、同じcollectible moduleの寿命で保持します。
        // DynamicMethodの署名領域が回収された後のcookie再利用を避けます。
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"UWSCSharp.NativeDllCall.{Guid.NewGuid():N}"),
            AssemblyBuilderAccess.RunAndCollect);
        ModuleBuilder module = assembly.DefineDynamicModule("NativeDllCall");
        TypeBuilder caller = module.DefineType("NativeDllCaller",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        MethodBuilder method = caller.DefineMethod("Invoke",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(object), [typeof(nint), typeof(object[])]);
        ILGenerator il = method.GetILGenerator();
        for (int index = 0; index < this.parameterTypes.Length; index++)
        {
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldc_I4, index);
            il.Emit(OpCodes.Ldelem_Ref);
            il.Emit(OpCodes.Unbox_Any, this.parameterTypes[index]);
        }

        il.Emit(OpCodes.Ldarg_0);
        il.EmitCalli(OpCodes.Calli, convention,
            returnType, this.parameterTypes);
        if (returnType == typeof(void))
        {
            il.Emit(OpCodes.Ldnull);
        }
        else
        {
            il.Emit(OpCodes.Box, returnType);
        }

        il.Emit(OpCodes.Ret);
        Type callerType = caller.CreateType()!;
        invoke = callerType.GetMethod("Invoke")!
            .CreateDelegate<Func<nint, object?[], object?>>();
    }

    // DLLと参照先バッファは呼出し元が所有し、この処理が戻るまで解放しません。
    internal object? Invoke(nint address, params object?[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (address == 0)
        {
            throw new ArgumentException("関数のアドレスがNULLです。", nameof(address));
        }

        if (arguments.Length != parameterTypes.Length)
        {
            throw new ArgumentException("宣言と引数の数が一致しません。", nameof(arguments));
        }

        // 検査後に呼出し元が配列を変更しても、引数の型と値を入れ替えないようにします。
        object?[] snapshot = [.. arguments];
        for (int index = 0; index < snapshot.Length; index++)
        {
            if (snapshot[index]?.GetType() != parameterTypes[index])
            {
                throw new ArgumentException($"引数{index + 1}は宣言したABI型へ変換してください。", nameof(arguments));
            }
        }

        return invoke(address, snapshot);
    }

}
