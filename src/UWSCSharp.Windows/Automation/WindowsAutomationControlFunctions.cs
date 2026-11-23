using UWSCSharp.Core;

namespace UWSCSharp.Windows;

public sealed partial class WindowsAutomation
{
    private sealed partial class Session
    {
        private async ValueTask<Value> InvokeClkItemAsync(
            CallArguments args, CancellationToken token)
        {
            nint root = Resolve(args.Int(0), false);
            int flags = args.Int(2);
            // 呼び出し先へは元の整数値を渡し、判定だけを型付きで行う。
            var options = (ClickItemFlags)flags;
            if ((options & ClickItemFlags.Accessible) != 0)
            {
                return Value.Logical(await AccessibleItems.InvokeAsync(root,
                    args.Str(1), flags, args.Int(4, 1), args.Int(3, 1), token,
                    accessibleContinuation, ItemMouse)
                    .ConfigureAwait(false));
            }

            // CONTINUEだけでは既定種別を補わず、明示ACCの汎用探索は維持します。
            if ((options & ClickItemFlags.Continue) != 0
                && (options & ClickItemFlags.Types) == 0)
            {
                return Value.Logical(false);
            }

            string rootClass = root == 0 ? "" : Text(root, true);
            if ((options & ClickItemFlags.Uia) != 0)
            {
                return Value.Logical(await UiaItemActions.InvokeAsync(
                    root, args.Str(1),
                    flags, args.Int(4), args.Int(3, 1), token,
                    PreparationFor(rootClass), ItemMouse)
                    .ConfigureAwait(false));
            }

            return Value.Logical(await ClickItemAsync(root,
                args.Str(1), flags, args.Int(4, 1), args.Int(4), args.Int(3, 1),
                token).ConfigureAwait(false));
        }

        private nint ResolveSliderRoot(int id)
        {
            nint root = id == 0 ? lookup.ForegroundWindow() : Resolve(id, false);
            return Native.IsWindow(root) ? root : 0;
        }

        private async ValueTask<Value> InvokeSetSliderAsync(
            CallArguments args, CancellationToken token)
        {
            int position = args.Int(1);
            bool query = position == WindowsConstants.GetSliderQuery;
            nint root = ResolveSliderRoot(args.Int(0));
            if (root == 0)
            {
                return query ? N(0) : Value.Logical(false);
            }

            int number = args.Int(2, 1);
            string rootClass = Text(root, true);
            List<SliderTarget> sliders = UiaSelection.PrefersUia(rootClass)
                ? [] : Sliders(root, token, includeDisabled: number < 0);
            long occurrence = number == 0 ? 1 : Math.Abs((long)number);
            if (occurrence > sliders.Count)
            {
                double result = await UiaSlider.ExecuteAsync(root, number,
                    query ? null : position, 0, token,
                    PreparationFor(rootClass)).ConfigureAwait(false);
                return query ? N(result) : Value.Logical(result != 0);
            }

            SliderTarget selected = sliders[(int)occurrence - 1];
            return query ? N(SliderPosition(selected, token))
                : Value.Logical(SetSlider(selected, position, token));
        }

        private async ValueTask<Value> InvokeGetSliderAsync(
            CallArguments args, CancellationToken token)
        {
            nint root = ResolveSliderRoot(args.Int(0));
            if (root == 0)
            {
                return N(-999999);
            }

            int number = args.Int(1, 1);
            string rootClass = Text(root, true);
            List<SliderTarget> sliders = UiaSelection.PrefersUia(rootClass)
                ? [] : Sliders(root, token, includeDisabled: number < 0);
            long sliderNumber = number == 0 ? 1 : Math.Abs((long)number);
            if (sliderNumber > sliders.Count)
            {
                return N(await UiaSlider.ExecuteAsync(root, number,
                    null, args.Int(2), token,
                    PreparationFor(rootClass)).ConfigureAwait(false));
            }

            SliderTarget selectedSlider = sliders[(int)sliderNumber - 1];
            nint slider = selectedSlider.Window;
            if (args.Int(2) >= 5)
            {
                using var dpi = new PhysicalDpiScope();
                Native.Rect sliderRect = Rect(slider);
                var corner = new Native.Point(
                    args.Int(2) is 5 or 6 ? sliderRect.Left : sliderRect.Right,
                    args.Int(2) is 5 or 6 ? sliderRect.Top : sliderRect.Bottom);
                Check(Native.ScreenToClient(root, ref corner));
                token.ThrowIfCancellationRequested();
                return N(args.Int(2) is 5 or 7 ? corner.X : corner.Y);
            }

            bool scroll = selectedSlider.Bar >= 0;
            if (args.Int(2) == 4)
            {
                return N(selectedSlider.Vertical ? 1 : 0);
            }

            if (scroll)
            {
                NativeScrollControl.ScrollInfo info = NativeScrollControl.Read(
                    slider, token, selectedSlider.Bar);
                return N(args.Int(2) switch
                {
                    1 => info.Minimum,
                    2 => info.Maximum,
                    3 => info.Page,
                    _ => info.Position
                });
            }

            uint sliderMessage = args.Int(2) switch
            {
                1 => Native.TbmGetRangeMin,
                2 => Native.TbmGetRangeMax,
                3 => 0x416,
                _ => Native.TbmGetPos
            };
            return N(unchecked((int)ControlMessage(
                slider, sliderMessage, token)));
        }

        private async ValueTask<Value> InvokeGetSlctLstAsync(
            CallArguments args, CancellationToken token)
        {
            nint selectionRoot = Resolve(args.Int(0), false);
            string selectionRootClass = selectionRoot == 0 ? "" : Text(selectionRoot, true);
            if (selectionRoot != 0 && UiaSelection.PrefersUia(selectionRootClass))
            {
                return Value.String(await UiaSelection.ReadAsync(selectionRoot,
                    args.Int(1, 1), args.Int(2), token,
                    PreparationFor(selectionRootClass)).ConfigureAwait(false));
            }

            List<nint> selectable = SelectionControls(selectionRoot, token);
            // 原版native経路は0以下の番号を1へ補正します。
            int selectedNumber = Math.Max(1, args.Int(1, 1));
            if (selectedNumber > selectable.Count)
            {
                return Value.String("");
            }

            nint selectionTarget = selectable[selectedNumber - 1];
            NativeSelectionKind selectionKind = NativeSelectionTarget.Kind(
                NativeSelectionTarget.Label(selectionTarget));
            if (selectionKind == NativeSelectionKind.ListView)
            {
                return Value.String(await NativeListViewControl.SelectedAsync(
                    selectionTarget, args.Int(2), token).ConfigureAwait(false));
            }

            if (selectionKind == NativeSelectionKind.Tree)
            {
                return Value.String(await NativeTreeControl.SelectedAsync(
                    selectionTarget, token).ConfigureAwait(false));
            }

            return Value.String(await NativeListControl.SelectedAsync(
                selectionTarget, token).ConfigureAwait(false));
        }

        private async ValueTask<Value> InvokeGetItemAsync(
            CallArguments args, CancellationToken token)
        {
            using var items = new ItemCollector();
            AllItems = items;
            await ReadItemsAsync(Resolve(args.Int(0), false),
                args.Int(1), args.Int(2, 1), args.Int(3), args.Int(4) == 0, args.Int(5), token, items).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return N(items.Count);
        }

        private async ValueTask<Value> InvokeChkBtnAsync(
            CallArguments args, CancellationToken token)
        {
            // 原版入口はACC分岐の前に非正番号を1へ補正します。
            int checkOccurrence = Math.Max(1, args.Int(2, 1));
            if (args.Int(3) != 0)
            {
                return N(await AccessibleItems.ReadCheckAsync(Resolve(args.Int(0), false),
                    args.Str(1), checkOccurrence, token).ConfigureAwait(false));
            }

            nint checkRoot = Resolve(args.Int(0), false);
            string checkRootClass = checkRoot == 0 ? "" : Text(checkRoot, true);
            if (UiaSelection.PrefersUia(checkRootClass))
            {
                return N(await UiaControl.ReadCheckAsync(checkRoot, args.Str(1),
                    checkOccurrence, token,
                    PreparationFor(checkRootClass)).ConfigureAwait(false));
            }

            (bool Found, int Value) nativeState = await ReadNativeCheckAsync(
                checkRoot, args.Str(1), checkOccurrence, token).ConfigureAwait(false);
            if (nativeState.Found)
            {
                return N(nativeState.Value);
            }

            if (KnownControls(checkRoot, [ControlClassNames.Button, ControlClassNames.ListBox,
                ControlClassNames.ComboBox, ControlClassNames.Tab,
                ControlClassNames.TreeView, ControlClassNames.ListView], false, token).Count < 20)
            {
                return N(await UiaControl.ReadCheckAsync(checkRoot, args.Str(1),
                    checkOccurrence, token).ConfigureAwait(false));
            }

            return N(-1);
        }
    }
}
