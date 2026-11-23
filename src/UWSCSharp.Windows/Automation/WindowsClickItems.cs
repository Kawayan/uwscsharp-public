using UWSCSharp.Core;

namespace UWSCSharp.Windows;

public sealed partial class WindowsAutomation
{
    // クリック対象ごとのネイティブ処理を分離します。
    private sealed partial class Session
    {
        /// <summary>
        /// 項目操作を処理したかと、処理した場合の成否です。
        /// </summary>
        private readonly record struct ItemActionResult(bool Handled, bool Success)
        {
            /// <summary>
            /// 対象が見つからず、次の手段へ進む結果です。
            /// </summary>
            internal static ItemActionResult Unhandled => default;

            /// <summary>
            /// 対象を処理した結果です。
            /// </summary>
            internal static ItemActionResult Done(bool success) => new(true, success);
        }

        private readonly record struct NativeClickRequest(
            nint Root, string Label, int Kind, int Occurrence,
            int ListOccurrence, int Request, int ListNumber,
            int TabNumber, int TreeNumber,
            int ToolbarNumber)
        {
            internal bool On => Request != 0;

            internal bool Has(ClickItemFlags flag) =>
                ((ClickItemFlags)Kind & flag) != 0;
        }

        private ItemActionResult ClickNativeButtons(
            NativeClickRequest click, CancellationToken active)
        {
            bool found = false;
            bool success = true;
            foreach (nint button in FindButtons(click.Root, click.Label,
                click.Occurrence, active,
                allowHiddenRoot: true, partial: click.Has(ClickItemFlags.Partial)))
            {
                string className = Text(button, true);
                bool managed = className.StartsWith("WindowsForms10.",
                    StringComparison.OrdinalIgnoreCase);
                // 型を確認できるWinFormsの意味操作はBM_CLICKで通知します。
                bool pointer = ((ClickItemFlags)click.Kind & ProviderItemMouse.Buttons) != 0;
                bool managedPush = managed && NativeButtonApi.IsManagedPush(button, active);
                if (click.Has(ClickItemFlags.Api) || (managedPush && !pointer))
                {
                    bool clicked = NativeButtonApi.Click(click.Root, button,
                        click.Request, click.Kind, managed,
                        listSelectionPlatform, active);
                    success &= clicked;
                    found = true;
                    continue;
                }

                if (managed && (managedPush || (click.Request is >= 0 and <= 2
                    && NativeButtonApi.IsManagedCheck(button, active))))
                {
                    // 同じHWNDのpatternまたは矩形を使い、所有描画部品の端をクリックしません。
                    (bool Handled, bool Success) result = UiaItemActions.TryInvokeButtonAsync(
                        click.Root, button, click.Label, click.Kind, click.Request,
                        active, ItemMouse).GetAwaiter().GetResult();
                    if (result.Handled)
                    {
                        success &= result.Success;
                        found = true;
                        continue;
                    }
                }

                NativeButtonControl.Click(click.Root, button, click.Request,
                    click.Kind, listSelectionPlatform, active, buttonAccessible.Value);
                found = true;
            }

            if (found)
            {
                return ItemActionResult.Done(success);
            }

            return ItemActionResult.Unhandled;
        }

        private ItemActionResult ClickNativeLists(
            NativeClickRequest click, CancellationToken active, Action onMatch)
        {
            List<nint> lists = SelectionControls(click.Root, active, listOnly: true,
                includeDisabled: click.ListOccurrence < 0);
            bool ofThis = click.Has(ClickItemFlags.OfThis);
            int itemMatches = 0;
            IEnumerable<nint> candidates = ofThis || click.ListNumber == 0
                ? lists : lists.Skip(click.ListNumber - 1).Take(1);
            foreach (nint target in candidates)
            {
                bool multipleMatched = false;
                bool selected = NativeListControl.Select(target,
                    click.Label, active, click.Root,
                    click.Has(ClickItemFlags.Background), listSelectionPlatform,
                    click.Has(ClickItemFlags.MouseMove),
                    () =>
                    {
                        multipleMatched = true;
                        onMatch();
                    },
                    click.Has(ClickItemFlags.Partial),
                    ofThis ? () => checked(++itemMatches) == click.ListNumber
                        || click.ListNumber == 0 : null,
                    onMatch, (ClickItemFlags)click.Kind);
                if (selected || multipleMatched)
                {
                    return ItemActionResult.Done(selected);
                }
            }

            return ItemActionResult.Unhandled;
        }

        private ItemActionResult ClickNativeTabs(
            NativeClickRequest click, NativeControlBufferOwner owner,
            CancellationToken active, Action onMatch)
        {
            var tabBuffer = new NativeTabClickBuffer(owner);
            int matched = 0;
            bool partial = click.Has(ClickItemFlags.Partial);
            string desired = ButtonName.Normalize(NativeListNameMatcher.AsciiUpper(
                partial ? click.Label : NativeListControl.Trim(click.Label)));
            foreach (nint tab in KnownControls(click.Root, [ControlClassNames.Tab],
                click.ListOccurrence < 0, active, allowHiddenRoot: true))
            {
                foreach ((int Index, string Name) item in tabBuffer.ReadNames(tab, active))
                {
                    string candidate = NativeListNameMatcher.AsciiUpper(item.Name);
                    if (NativeListNameMatcher.Compare(ref candidate, desired, partial)
                        && (checked(++matched) == click.TabNumber || click.TabNumber == 0))
                    {
                        onMatch();
                        if (NativeTabControl.Click(tab, item.Index, active,
                            tabBuffer, click.Root, listSelectionPlatform, click.Kind))
                        {
                            return ItemActionResult.Done(true);
                        }

                        // 選択不一致では同じTABの残り項目を読まず、次controlへ進みます。
                        break;
                    }
                }
            }

            return ItemActionResult.Unhandled;
        }

        private ItemActionResult ClickNativeTrees(
            NativeClickRequest click, NativeControlBufferOwner owner,
            CancellationToken active, Action onAttempt)
        {
            var treeBuffer = new NativeTreeClickBuffer(owner);
            foreach (nint tree in KnownControls(click.Root,
                [ControlClassNames.TreeView], click.ListOccurrence < 0,
                active, allowHiddenRoot: true))
            {
                (bool Found, bool Success) clicked = NativeTreeControl.Click(
                    tree, click.Label, click.On, active, treeBuffer,
                    click.Kind, listSelectionPlatform, number: click.TreeNumber,
                    onAttempt: onAttempt);
                if (clicked.Found)
                {
                    return ItemActionResult.Done(clicked.Success);
                }
            }

            return ItemActionResult.Unhandled;
        }

        private ItemActionResult ClickNativeListViews(
            NativeClickRequest click, NativeControlBufferOwner owner,
            CancellationToken active, Action onAttempt)
        {
            var viewBuffer = new NativeListViewClickBuffer(owner);
            int remaining = click.Occurrence;
            foreach (nint view in KnownControls(click.Root,
                [ControlClassNames.ListView], false, active,
                allowHiddenRoot: true))
            {
                (int Matches, bool Found, bool Success) clicked =
                    NativeListViewControl.Click(view, click.Label,
                        remaining, click.On, active, viewBuffer,
                        listSelectionPlatform,
                        click.Has(ClickItemFlags.Background), click.Kind,
                        onAttempt: onAttempt);
                if (clicked.Found)
                {
                    return ItemActionResult.Done(clicked.Success);
                }

                remaining -= clicked.Matches;
            }

            return ItemActionResult.Unhandled;
        }

        private ItemActionResult ClickNativeToolbars(
            NativeClickRequest click, NativeControlBufferOwner owner,
            CancellationToken active)
        {
            var toolbarBuffer = new NativeToolbarClickBuffer(owner);
            int remaining = click.ToolbarNumber;
            foreach (nint toolbar in KnownControls(click.Root,
                [ControlClassNames.Toolbar], click.ListOccurrence < 0,
                active, allowHiddenRoot: true))
            {
                (int Matches, bool Found, bool Success) clicked =
                    NativeToolbarControl.Click(toolbar, click.Label,
                        remaining, click.On, active, toolbarBuffer,
                        click.Has(ClickItemFlags.Partial), listSelectionPlatform,
                        click.Has(ClickItemFlags.Background), click.Kind);
                if (clicked.Found)
                {
                    return ItemActionResult.Done(clicked.Success);
                }

                remaining -= clicked.Matches;
            }

            return ItemActionResult.Unhandled;
        }
    }
}
