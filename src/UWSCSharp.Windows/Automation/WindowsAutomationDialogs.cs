using System.Runtime.Versioning;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

public sealed partial class WindowsAutomation
{
    [SupportedOSPlatform("windows")]
    private sealed partial class Session
    {
        private readonly CancellationTokenSource dialogCancellation = new();

        private Task<Value> InvokeBalloonAsync(IReadOnlyList<Value> arguments,
            CancellationToken token)
        {
            var request = BalloonRequest.Read(arguments);
            return InvokeDialogAsync((service, appearance, _, linked) =>
                service.ShowBalloonAsync(this, request, appearance,
                    dialogCancellation.Token, resources.DialogStopToken, linked), token);
        }

        /// <summary>
        /// 主スクリプト名を共有の既定タイトルへ設定します。
        /// </summary>
        public void ConfigureDialogContext(string scriptName) =>
            resources.ConfigureDialogContext(scriptName);

        /// <summary>
        /// 次の表示へ適用する設定を実行内で更新します。
        /// </summary>
        public void ConfigureDialogOption(string name, IReadOnlyList<Value> values,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            resources.ConfigureDialogOption(name, values);
        }

        private Task<Value> InvokeMessageBoxAsync(IReadOnlyList<Value> arguments,
            CancellationToken token)
        {
            var request = MessageBoxRequest.Read(arguments);
            return InvokeDialogAsync(async (service, appearance, owner, linked) =>
            {
                int result = await service.ShowMessageAsync(this, request,
                    appearance, owner, linked).ConfigureAwait(false);
                return Value.Numeric(result) with
                {
                    NativeVariantType = 3,
                    NativeScalar = result
                };
            }, token);
        }

        private Task<Value> InvokeInputBoxAsync(IReadOnlyList<Value> arguments,
            CancellationToken token)
        {
            var request = InputBoxRequest.Read(arguments);
            return InvokeDialogAsync((service, appearance, owner, linked) =>
                service.ShowInputAsync(this, request, appearance, owner, linked), token);
        }

        private Task<Value> InvokeSelectionBoxAsync(IReadOnlyList<Value> arguments,
            CancellationToken token)
        {
            var request = SelectionBoxRequest.Read(arguments, token);
            return InvokeDialogAsync((service, appearance, owner, linked) =>
                service.ShowSelectionAsync(this, request, appearance, owner, linked), token);
        }

        private Task<Value> InvokePopupMenuAsync(IReadOnlyList<Value> arguments,
            CancellationToken token)
        {
            var request = PopupMenuRequest.Read(arguments, token);
            return InvokeDialogAsync((service, appearance, owner, linked) =>
                service.ShowPopupAsync(this, request, appearance.Title, owner, linked), token);
        }

        private async Task<Value> InvokeDialogAsync(
            Func<NativeDialogService, DialogAppearance, nint, CancellationToken, Task<Value>> show,
            CancellationToken token)
        {
            CancellationTokenSource linked;
            lock (disposalGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                linked = CancellationTokenSource.CreateLinkedTokenSource(
                    token, dialogCancellation.Token, resources.DialogStopToken);
            }

            using (linked)
            {
                try
                {
                    DialogAppearance settings = resources.Appearance;
                    nint owner = hostWindowHandle is null ? 0 :
                        await hostWindowHandle(WindowsHostWindow.Main, linked.Token).ConfigureAwait(false);
                    linked.Token.ThrowIfCancellationRequested();
                    return await show(resources.Dialogs, settings, owner, linked.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw new OperationCanceledException(token);
                }
                catch (ObjectDisposedException) when (linked.IsCancellationRequested)
                {
                    throw new OperationCanceledException(token.IsCancellationRequested ? token : linked.Token);
                }
            }
        }
    }
}
