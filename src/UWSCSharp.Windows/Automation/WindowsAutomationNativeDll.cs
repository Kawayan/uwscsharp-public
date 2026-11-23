using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

public sealed partial class WindowsAutomation
{
    private sealed partial class Session : INativeDllScalarInvoker
    {
        private static long nextNativeDllWorkerId;
        private readonly long nativeDllWorkerId = Interlocked.Increment(ref nextNativeDllWorkerId);
        private NativeDllRelayClient? nativeDllWorkerClient;

        object? INativeDllScalarInvoker.InvokeNativeDll(
            NativeDllDeclaration declaration, string workingDirectory,
            object?[] arguments, CancellationToken token, ushort? controlWord)
        {
            token.ThrowIfCancellationRequested();
            NativeDllArgumentPlan.CheckArgumentBlockSize(declaration.Parameters);
            string path = ResolveNativeDllPath(declaration, workingDirectory);
            bool extendedReturn = declaration.ReturnType is NativeDllScalarType
            { Primitive: NativeDllPrimitive.Extended };
            bool extendedArgument = declaration.Parameters.Any(
                NativeDllScalarInput.IsExtendedParameter);
            if (extendedReturn || extendedArgument)
            {
                if (!NativeDllImage.IsNativeX86Dll(path))
                {
                    throw new PlatformNotSupportedException(
                        extendedReturn ? "EXTENDEDの戻り型には32ビットDLLが必要です。"
                            : "EXTENDEDの引数には32ビットDLLが必要です。");
                }

                return InvokeRelayedDll(path, declaration, arguments, token, controlWord);
            }

            NativeDllCallSite callSite = resources.NativeDllCallSites.GetValue(
                declaration, static definition => new NativeDllCallSite(
                    definition.ReturnType is NativeDllScalarType type
                        ? NativeDllScalarInput.AbiType(type.Primitive) ?? typeof(nint)
                        : typeof(void),
                    [.. definition.Parameters.Select(parameter => parameter is
                        NativeDllScalarType { IsByReference: false, IsArray: false }
                            scalar && NativeDllScalarInput.AbiType(scalar.Primitive)
                                is Type scalarType ? scalarType : typeof(nint))]));
            NativeDllModule.Function function;
            try
            {
                function = ResolveNativeDll(path, declaration.Name, callSite);
            }
            catch (BadImageFormatException) when (NativeDllImage.IsNativeX86Dll(path)
                && NativeDllRelayHost.Find() is not null)
            {
                return InvokeRelayedDll(path, declaration, arguments, token, controlWord);
            }

            token.ThrowIfCancellationRequested();
            return NativeDllArgumentPlan.Create(declaration, arguments, token)
                .InvokeDirect(function, token);
        }

        private object? InvokeRelayedDll(string path, NativeDllDeclaration declaration,
            object?[] arguments, CancellationToken token, ushort? controlWord)
        {
            (NativeDllRelayCall call, Func<NativeDllRelayResult, object?> apply) =
                NativeDllArgumentPlan.PrepareRelay(declaration, arguments,
                    CallingConvention.StdCall, token, controlWord);
            Task<NativeDllRelayClient> connection;
            lock (disposalGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                connection = resources.GetNativeDllRelay();
            }

            NativeDllRelayClient client = connection.WaitAsync(token).GetAwaiter().GetResult();
            nativeDllWorkerClient = client;
            NativeDllRelayReply reply = client.Invoke(
                new(path, call, WorkerId: nativeDllWorkerId), token);
            if (reply.Result is not { } result)
            {
                throw new InvalidOperationException(reply.ErrorMessage
                    ?? "32ビットDLLの中継が応答を返しませんでした。");
            }

            return apply(result);
        }

        private void CloseNativeDllWorker()
        {
            NativeDllRelayClient? client = nativeDllWorkerClient;
            if (client is not null)
            {
                client.CloseWorker(nativeDllWorkerId);
            }
        }

        private static string ResolveNativeDllPath(NativeDllDeclaration declaration,
            string workingDirectory) => NativeDllLibraryPath.Resolve(declaration.LibraryText,
                workingDirectory, AppContext.BaseDirectory, Environment.SystemDirectory);

        /// <summary>
        /// 宣言のDLL名を検索して実行内の所有参照から関数を解決します。
        /// </summary>
        public NativeDllModule.Function ResolveNativeDll(
            NativeDllDeclaration declaration, string workingDirectory,
            NativeDllCallSite callSite)
        {
            string path = ResolveNativeDllPath(declaration, workingDirectory);
            return ResolveNativeDll(path, declaration.Name, callSite);
        }

        /// <summary>
        /// この実行が所有するDLLから関数を解決します。
        /// </summary>
        public NativeDllModule.Function ResolveNativeDll(string path,
            string export, NativeDllCallSite callSite)
        {
            NativeDllModuleCache cache;
            lock (disposalGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                cache = resources.NativeDlls;
            }

            // ロードやexport解決中の再入を妨げないよう、実行管理のロック外で呼びます。
            return cache.Resolve(path, export, callSite);
        }
    }

    private sealed partial class RunResources
    {
        internal readonly ConditionalWeakTable<NativeDllDeclaration,
            NativeDllCallSite> NativeDllCallSites = new();

        internal Task<NativeDllRelayClient> GetNativeDllRelay()
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return nativeDllRelay ??= NativeDllRelayHost.StartAsync();
            }
        }

        internal NativeDllModuleCache NativeDlls
        {
            get
            {
                lock (gate)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    return nativeDlls ??= new NativeDllModuleCache(nativeDllLoader);
                }
            }
        }
    }
}
