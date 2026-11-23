using UWSCSharp.Core;

namespace UWSCSharp.Windows;

public sealed partial class WindowsAutomation
{
    private sealed partial class Session
    {
        /// <summary>
        /// 指定した自動操作関数を非同期に実行します。
        /// </summary>
        public async ValueTask<Value> InvokeAsync(
            string name, IReadOnlyList<Value> arguments,
            CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            token.ThrowIfCancellationRequested();
            WindowsFunctions.Validate(name, arguments, token);
            try
            {
                return await InvokeValidatedAsync(name, arguments, token)
                    .ConfigureAwait(false);
            }
            catch (Exception failure) when (WindowsOperationErrors.Contains(failure))
            {
                scriptErrors.GetValue(failure, _ => name);
                throw;
            }
        }

        private async ValueTask<Value> InvokeValidatedAsync(
            string name, IReadOnlyList<Value> arguments, CancellationToken token)
        {
            var args = new CallArguments(arguments);
            switch (name)
            {
                case BuiltinFunctionNames.Balloon:
                case BuiltinFunctionNames.Fukidasi:
                    return await InvokeBalloonAsync(arguments, token).ConfigureAwait(false);
                case BuiltinFunctionNames.SlctBox:
                    return await InvokeSelectionBoxAsync(arguments, token).ConfigureAwait(false);
                case BuiltinFunctionNames.PopupMenu:
                    return await InvokePopupMenuAsync(arguments, token).ConfigureAwait(false);
                case BuiltinFunctionNames.Input:
                    return await InvokeInputBoxAsync(arguments, token).ConfigureAwait(false);
                case BuiltinFunctionNames.MsgBox:
                    return await InvokeMessageBoxAsync(arguments, token).ConfigureAwait(false);
                case BuiltinFunctionNames.Sensor:
                    return await InvokeSensorAsync(args.Int(0), token).ConfigureAwait(false);
                case BuiltinFunctionNames.CpuUserate:
                    return N(cpu.Query(token));
                case BuiltinFunctionNames.KindOfOs:
                    return InvokeKindOfOs(args, token);
                case BuiltinFunctionNames.Monitor:
                    return display.Query(MonitorRequest.Read(arguments), token);
                case BuiltinFunctionNames.CreateOleObj:
                case BuiltinFunctionNames.GetActiveOleObj:
                    return await Com.CreateAsync(args.Str(0),
                        name == BuiltinFunctionNames.GetActiveOleObj,
                        token).ConfigureAwait(false);
                case BuiltinFunctionNames.IeGetFrame:
                    return await Com.GetHtmlFrameAsync(args, token)
                        .ConfigureAwait(false);
                case BuiltinFunctionNames.IeGetSrc:
                    return await Com.ReadHtmlSourceAsync(args, token)
                        .ConfigureAwait(false);
                case BuiltinFunctionNames.IeSetSrc:
                    return await Com.SetHtmlSourceAsync(args, token)
                        .ConfigureAwait(false);
                case BuiltinFunctionNames.IeLink:
                    return await Com.ActivateHtmlLinkAsync(args, token)
                        .ConfigureAwait(false);
                case BuiltinFunctionNames.IeSetData:
                    return await Com.SetHtmlDataAsync(args, token)
                        .ConfigureAwait(false);
                case BuiltinFunctionNames.IeGetData:
                    return await Com.ReadHtmlDataAsync(args, token)
                        .ConfigureAwait(false);
                case BuiltinFunctionNames.GetOleItem:
                    return await InvokeGetOleItemAsync(args, token)
                        .ConfigureAwait(false);
                case BuiltinFunctionNames.SafeArray:
                    return await Com.CreateSafeArrayAsync(
                        SafeArrayRequest.Read(arguments), token).ConfigureAwait(false);
                case BuiltinFunctionNames.VarType:
                    var variant = VariantTypeRequest.Read(arguments);
                    return variant.Target is null ? VariantTypeRequest.Query(variant.Source)
                        : await Com.ConvertVariantAsync(variant.Source, variant.Target.Value, token)
                            .ConfigureAwait(false);
                case BuiltinFunctionNames.IdToHnd:
                    return N((long)Resolve(args.Int(0)));
                case BuiltinFunctionNames.HndToId:
                    return InvokeHndToId(args);
                case BuiltinFunctionNames.GetId:
                    return await InvokeGetIdAsync(args, token)
                        .ConfigureAwait(false);
                case BuiltinFunctionNames.GetAllWin:
                    return InvokeGetAllWin(args, token);
                case BuiltinFunctionNames.GetCtlHnd:
                    return await InvokeGetCtlHndAsync(args, token)
                        .ConfigureAwait(false);
                case BuiltinFunctionNames.MusCur:
                    return N(CursorReader.Read());
                case BuiltinFunctionNames.ScKey:
                    return await InvokeScKeyAsync(arguments, token)
                        .ConfigureAwait(false);
                case BuiltinFunctionNames.GetKeyState:
                    return await InvokeGetKeyStateAsync(args, token)
                        .ConfigureAwait(false);
                case BuiltinFunctionNames.Exec:
                    return await InvokeExecAsync(arguments, token)
                        .ConfigureAwait(false);
                case BuiltinFunctionNames.DosCmd:
                    return await InvokeDosCommandAsync(arguments, token)
                        .ConfigureAwait(false);
                case BuiltinFunctionNames.PowerShell:
                    return await InvokePowerShellAsync(arguments, token)
                        .ConfigureAwait(false);
                case BuiltinFunctionNames.LockHard:
                    return await InvokeLockHardAsync(arguments, token).ConfigureAwait(false);
                case BuiltinFunctionNames.LockHardEx:
                    return await InvokeSelectiveInputAsync(arguments, token)
                        .ConfigureAwait(false);
                case BuiltinFunctionNames.DropFile:
                    return await InvokeDropFileAsync(arguments, token)
                        .ConfigureAwait(false);
                case BuiltinFunctionNames.Status:
                    return InvokeStatus(args);
                case BuiltinFunctionNames.CtrlWin:
                    return InvokeCtrlWin(args);
                case BuiltinFunctionNames.ClkItem:
                    return await InvokeClkItemAsync(args, token)
                        .ConfigureAwait(false);
                case BuiltinFunctionNames.PosAcc:
                    return InvokePosAcc(args, token);
                case BuiltinFunctionNames.PeekColor:
                    return InvokePeekColor(args, token);
                case BuiltinFunctionNames.ChkImg:
                    return InvokeCheckImage(args, token);
                case BuiltinFunctionNames.SaveImg:
                    return InvokeSaveImage(args, token);
                case BuiltinFunctionNames.SetSlider:
                    return await InvokeSetSliderAsync(args, token)
                        .ConfigureAwait(false);
                case BuiltinFunctionNames.GetSlider:
                    return await InvokeGetSliderAsync(args, token)
                        .ConfigureAwait(false);
                case BuiltinFunctionNames.GetSlctLst:
                    return await InvokeGetSlctLstAsync(args, token)
                        .ConfigureAwait(false);
                case BuiltinFunctionNames.GetItem:
                    return await InvokeGetItemAsync(args, token)
                        .ConfigureAwait(false);
                case BuiltinFunctionNames.ChkBtn:
                    return await InvokeChkBtnAsync(args, token)
                        .ConfigureAwait(false);
                case BuiltinFunctionNames.GetStr:
                    return await InvokeGetStrAsync(args, token)
                        .ConfigureAwait(false);
                case BuiltinFunctionNames.SendStr:
                    return await InvokeSendStrAsync(args, token)
                        .ConfigureAwait(false);
                case BuiltinFunctionNames.MouseOrg:
                    return InvokeMouseOrg(args);
                case BuiltinFunctionNames.Mmv:
                    return await InvokeMmvAsync(args, token)
                        .ConfigureAwait(false);
                case BuiltinFunctionNames.Btn:
                    return await InvokeBtnAsync(args, token)
                        .ConfigureAwait(false);
                case BuiltinFunctionNames.Kbd:
                    return await InvokeKbdAsync(args, token)
                        .ConfigureAwait(false);
                case BuiltinFunctionNames.Acw:
                    return await InvokeAcwAsync(args, token)
                        .ConfigureAwait(false);
                default:
                    throw new NotSupportedException(name);
            }
        }

        private Value InvokeKindOfOs(CallArguments args,
            CancellationToken token)
        {
            OperatingSystemInformation system = operatingSystemInformation is null
                ? OperatingSystemInformation.Read() : operatingSystemInformation();
            token.ThrowIfCancellationRequested();
            return NumericOperations.RuntimeNumber(args.Get(0)) != 0
                ? Value.Logical(system.Is64Bit) : N(system.Kind);
        }

        private Value InvokeCheckImage(CallArguments args,
            CancellationToken token)
        {
            return CheckImage(args.Str(0), args.Int(1),
                args.Has(2) ? args.Int(2) : null, args.Has(3) ? args.Int(3) : null,
                args.Has(4) ? args.Int(4) : null, args.Has(5) ? args.Int(5) : null,
                args.Int(6, 1), args.Int(7), token);
        }

        private Value InvokeSaveImage(CallArguments args,
            CancellationToken token)
        {
            SaveImage(args.Str(0), args.Int(1),
                args.Has(2) ? args.Int(2) : null,
                args.Has(3) ? args.Int(3) : null,
                args.Int(4), args.Int(5), args.Int(6) != 0,
                args.Int(7), args.Int(8), token);
            return Value.Empty;
        }
    }
}
