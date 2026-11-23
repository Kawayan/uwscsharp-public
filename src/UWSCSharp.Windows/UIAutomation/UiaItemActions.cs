using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// UIAの項目操作を、対象ルートと有限の探索に限定します。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class UiaItemActions
{
    internal static Task<(bool Handled, bool Success)> TryInvokeButtonAsync(
        nint root, nint control, string name, int flags, int request,
        CancellationToken cancellationToken, ProviderItemMouse? mouse = null) =>
        ProviderActionOperation.RunAsync(root, token =>
        {
            var action = new ActionSearch(root, name,
                ((ClickItemFlags)flags & ~ClickItemFlags.Types) | ClickItemFlags.Button,
                1, request, token, mouse: mouse, control: control);
            bool success = action.Run(null);
            return (action.Handled, success);
        }, cancellationToken);

    internal static Task<bool> SelectTabAsync(nint target, int index,
        bool background, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        if (target == 0 || !Native.IsWindow(target))
        {
            return false;
        }

        ClickItemFlags flags = ClickItemFlags.Tab
            | (background ? ClickItemFlags.Background : 0);
        var action = new ActionSearch(target, "", flags, 1, 1, token, index);
        return action.Run(null);
    }, token);

    internal static async Task<bool> InvokeAsync(nint root, string name,
        int flags, int occurrence, int request, CancellationToken token,
        UiaMozillaPreparation? preparation = null, ProviderItemMouse? mouse = null) =>
        (await TryInvokeAsync(root, name, flags, occurrence, request,
            token, preparation, mouse).ConfigureAwait(false)).Success;

    internal static Task<(bool Handled, bool Success)> TryInvokeAsync(
        nint root, string name, int flags, int occurrence, int request,
        CancellationToken cancellationToken,
        UiaMozillaPreparation? preparation = null,
        ProviderItemMouse? mouse = null) => ProviderActionOperation.RunAsync(root, token =>
    {
        (bool Handled, bool Success) Run(Action? prepare)
        {
            token.ThrowIfCancellationRequested();
            if (root == 0 || !Native.IsWindow(root))
            {
                return (false, false);
            }

            var action = new ActionSearch(root, name,
                (ClickItemFlags)flags, occurrence, request, token, mouse: mouse);
            bool success = action.Run(prepare);
            return (action.Handled, success);
        }

        return preparation is null ? Run(null)
            : preparation.Execute(root, Run, token);
    }, cancellationToken);

    internal static bool SetToggle(IUiaToggle pattern, int request,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ValidateToggleRequest(request);
        // 三状態でも操作は最大2回に限定し、同じ状態なら操作しません。
        int desired = request;
        for (int attempt = 0; attempt <= 2; attempt++)
        {
            token.ThrowIfCancellationRequested();
            int current = WindowsOperationErrors.Provider(pattern.CurrentState);
            if (current is < 0 or > 2)
            {
                throw Invalid("UIA Toggle状態が範囲外です。");
            }

            if (current == desired)
            {
                return true;
            }

            if (attempt < 2)
            {
                WindowsOperationErrors.Provider(pattern.Toggle);
                token.ThrowIfCancellationRequested();
                // UIAの通知を処理する時間を設け、取消を待機中にも確認します。
                if (token.WaitHandle.WaitOne(20))
                {
                    token.ThrowIfCancellationRequested();
                }
            }
        }

        return false;
    }

    private static void ValidateToggleRequest(int request)
    {
        if (request is < 0 or > 2)
        {
            throw WindowsOperationErrors.Record(new ArgumentOutOfRangeException(
                nameof(request), request,
                "UIA Toggleの要求状態は0・1・2のいずれかを指定してください。"));
        }
    }

    private static InvalidOperationException Invalid(string message) =>
        WindowsOperationErrors.Record(new InvalidOperationException(message));

    private sealed class ActionSearch(nint root, string name,
        ClickItemFlags flags, int occurrence, int request,
        CancellationToken token, int? tabIndex = null, ProviderItemMouse? mouse = null,
        nint control = 0)
    {
        private readonly List<object> owned = [];
        private IUiaAutomation automation = null!;
        private IUiaElement element = null!;
        private IUiaTreeWalker? walker;
        private int candidates;
        private int characters;
        private int matched;
        private bool found;
        private bool recognized;
        private bool missingPattern;
        private bool actionStarted;
        private bool success;
        internal bool Handled => found ? !missingPattern || actionStarted : recognized;

        private bool On => request != 0;
        private bool Partial => (flags & ClickItemFlags.Partial) != 0;
        private bool PointerClick => (flags & ProviderItemMouse.Buttons) != 0;
        private ProviderItemMouse Mouse => mouse ??= new ProviderItemMouse(
            input => NativeInputSender.Send(input), Native.GetSystemMetrics(23) != 0,
            new NativeListSelectionPlatform());

        internal bool Run(Action? prepare)
        {
            Exception? primary = null;
            try
            {
                automation = Own(WindowsOperationErrors.Provider(
                    () => (IUiaAutomation)new UiaComObject()));
                element = Own(WindowsOperationErrors.ProviderReference(
                    () => automation.ElementFromHandle(control == 0 ? root : control)));
                if (prepare is not null)
                {
                    prepare();
                    element = Own(WindowsOperationErrors.ProviderReference(
                        () => automation.ElementFromHandle(root)));
                }

                ClickItemFlags types = flags & ClickItemFlags.Types;
                if (types == 0)
                {
                    types = ClickItemFlags.Types;
                }

                foreach (ClickItemFlags kind in new[]
                {
                    ClickItemFlags.Button, ClickItemFlags.List,
                    ClickItemFlags.Tab, ClickItemFlags.Menu,
                    ClickItemFlags.TreeView, ClickItemFlags.ListView,
                    ClickItemFlags.Toolbar, ClickItemFlags.Link
                })
                {
                    if ((types & kind) == 0)
                    {
                        continue;
                    }

                    matched = 0;
                    switch (kind)
                    {
                        case ClickItemFlags.Button:
                            foreach (int type in new[] { 50000, 50002, 50013 })
                            {
                                Visit(element, type, 7, item =>
                                {
                                    if (!Boolean(item, 30022))
                                    {
                                        Match(item, Name(item), () => Button(item, type));
                                    }
                                });
                                if (found)
                                {
                                    break;
                                }
                            }

                            break;
                        case ClickItemFlags.List:
                            Lists();
                            break;
                        case ClickItemFlags.Tab:
                            int index = 0;
                            Visit(element, 50019, 7, item =>
                            {
                                if (tabIndex.HasValue)
                                {
                                    if (index++ != tabIndex.Value || !Boolean(item, 30010))
                                    {
                                        return;
                                    }

                                    found = true;
                                    success = Select(item) && Boolean(item, 30079);
                                }
                                else
                                {
                                    Match(item, Name(item), () =>
                                    {
                                        return Select(item);
                                    });
                                }
                            });
                            break;
                        case ClickItemFlags.Menu:
                            Paths(element, 50011, "", 0, menu: true);
                            break;
                        case ClickItemFlags.TreeView:
                            Visit(element, 50023, 7, tree =>
                                Paths(tree, 50024, "", 0, menu: false));
                            break;
                        case ClickItemFlags.ListView:
                            Visit(element, 50028, 7, grid =>
                            {
                                Visit(grid, 50029, 4, item =>
                                {
                                    string caption = CellText(item);
                                    Match(item, caption, () =>
                                    {
                                        return !On ? NoOp(item) : Select(item);
                                    });
                                });
                            });
                            break;
                        case ClickItemFlags.Toolbar:
                            Visit(element, 50021, 7, toolbar =>
                                Visit(toolbar, 50000, 4, item =>
                                    Match(item, Name(item), () => Button(item, 50000))));
                            break;
                        case ClickItemFlags.Link:
                            // LINK固有の祖先・可視条件とOFFの規約を維持します。
                            success = UiaControl.InvokeAsync(root, name,
                                Math.Max(1, occurrence), token, 128,
                                (flags & ClickItemFlags.Background) == 0
                                    && (flags & ProviderItemMouse.Options) == 0, On,
                                () => found = true, () => recognized = true,
                                (flags & ProviderItemMouse.Options) == 0 ? null : item =>
                                {
                                    actionStarted = true;
                                    if (PointerClick && On)
                                    {
                                        return Physical(item);
                                    }

                                    Prepare(item);
                                    return null;
                                }, partial: Partial)
                                .GetAwaiter().GetResult();
                            break;
                    }

                    if (found)
                    {
                        token.ThrowIfCancellationRequested();
                        return success;
                    }
                }

                token.ThrowIfCancellationRequested();
                return false;
            }
            catch (COMException error) when (error.HResult is
                unchecked((int)0x80040200) or unchecked((int)0x80040201))
            {
                token.ThrowIfCancellationRequested();
                return false;
            }
            catch (Exception failure) when (
                ProviderCleanup.PreservePrimary(failure, ref primary))
            {
                throw;
            }
            finally
            {
                var cleanup = new ProviderCleanup(primary);
                for (int index = owned.Count - 1; index >= 0; index--)
                {
                    cleanup.Release(owned[index]);
                }

                cleanup.Finish();
            }
        }

        private T Own<T>(T value) where T : class
        {
            owned.Add(value);
            return value;
        }

        private void Visit(IUiaElement parent, int type, int scope,
            Action<IUiaElement> visit)
        {
            if (found)
            {
                return;
            }

            token.ThrowIfCancellationRequested();
            IUiaCondition condition = Own(WindowsOperationErrors.ProviderReference(
                () => automation.CreatePropertyCondition(30003, type)));
            IUiaElements children = Own(WindowsOperationErrors.ProviderReference(
                () => parent.FindAll(scope, condition)));
            int count = WindowsOperationErrors.Provider(children.Length);
            if (count < 0 || count > 100000 - candidates)
            {
                throw Invalid("UIA操作候補数が上限を超えました。");
            }

            candidates += count;
            for (int index = 0; index < count && !found; index++)
            {
                token.ThrowIfCancellationRequested();
                IUiaElement item = Own(WindowsOperationErrors.ProviderReference(
                    () => children.GetElement(index)));
                if (Available(item))
                {
                    visit(item);
                }
            }
        }

        private bool Available(IUiaElement item)
        {
            List<IUiaElement> parents = [];
            Exception? primary = null;
            try
            {
                IUiaElement current = item;
                for (int depth = 0; depth < 128; depth++)
                {
                    token.ThrowIfCancellationRequested();
                    if (Property(current, 30020) is int handle && handle != 0)
                    {
                        nint window = unchecked((nint)(uint)handle);
                        if (!Native.IsWindow(window) || !Native.IsWindowEnabled(window)
                            || (window != root
                                && (Native.GetWindowLongW(window, Native.GwlStyle) & 0x10000000) == 0))
                        {
                            return false;
                        }
                        if (window == root)
                        {
                            return true;
                        }
                    }

                    walker ??= Own(WindowsOperationErrors.ProviderReference(
                        automation.RawViewWalker));
                    IUiaElement? parent = WindowsOperationErrors.Provider(
                        () => walker.Parent(current));
                    if (parent is null)
                    {
                        return false;
                    }
                    parents.Add(parent);
                    current = parent;
                }

                throw Invalid("UIA操作の祖先探索深度が上限を超えました。");
            }
            catch (Exception failure) when (
                ProviderCleanup.PreservePrimary(failure, ref primary))
            {
                throw;
            }
            finally
            {
                var cleanup = new ProviderCleanup(primary);
                for (int index = parents.Count - 1; index >= 0; index--)
                {
                    cleanup.Release(parents[index]);
                }
                cleanup.Finish();
            }
        }

        private string Name(IUiaElement item)
        {
            string text = Property(item, 30005) as string ?? "";
            if (text.Length > NativeListControl.MaximumCharacters - characters)
            {
                throw Invalid("UIA操作の判定文字数が上限を超えました。");
            }

            characters += text.Length;
            return text;
        }

        private object? Property(IUiaElement item, int property)
        {
            object? value = WindowsOperationErrors.Provider(() => item.Property(property));
            if (value is not null && Marshal.IsComObject(value))
            {
                Own(value);
            }

            return value;
        }

        private bool Boolean(IUiaElement item, int property) =>
            Property(item, property) is true;

        private void Match(IUiaElement item, string caption, Func<bool> act,
            bool numbered = true)
        {
            if (found || !Boolean(item, 30010))
            {
                return;
            }

            string candidate = ButtonName.Normalize(caption);
            string desired = ButtonName.Normalize(Partial ? name : NativeListControl.Trim(name));
            bool match = Partial
                ? candidate.Contains(desired, StringComparison.OrdinalIgnoreCase)
                : AccessibleItems.MatchesName(candidate, desired);
            if (!match)
            {
                return;
            }

            recognized = true;
            if (numbered && ++matched != Math.Max(1, Math.Abs((long)occurrence)))
            {
                return;
            }

            found = true;
            missingPattern = false;
            actionStarted = false;
            token.ThrowIfCancellationRequested();
            success = Boolean(item, 30010) && act();
            token.ThrowIfCancellationRequested();
        }

        private Native.Point? Prepare(IUiaElement item)
        {
            token.ThrowIfCancellationRequested();
            if ((flags & ClickItemFlags.Background) == 0)
            {
                ItemActionPreparation.Foreground(root, token);
                if (Boolean(item, 30009))
                {
                    WindowsOperationErrors.Provider(item.SetFocus);
                }
                token.ThrowIfCancellationRequested();
            }

            if ((flags & ProviderItemMouse.Options) == 0)
            {
                return null;
            }

            using var dpi = new PhysicalDpiScope();
            if (Property(item, 30001) is not double[] bounds || bounds.Length != 4)
            {
                throw Invalid("UIAの項目矩形が不正です。");
            }

            return Mouse.Move(bounds[0], bounds[1], bounds[2], bounds[3], flags, token);
        }

        private bool Physical(IUiaElement item, bool scroll = false)
        {
            actionStarted = true;
            using var dpi = new PhysicalDpiScope();
            if (scroll)
            {
                _ = UiaPatternAttempt.Run<IUiaScrollItemPattern>(() =>
                {
                    Guid iid = typeof(IUiaScrollItemPattern).GUID;
                    return item.PatternAs(10017, ref iid);
                }, value => Own(value), pattern =>
                {
                    WindowsOperationErrors.Provider(pattern.ScrollIntoView);
                    return true;
                }, token);
            }

            for (int attempt = 0; Boolean(item, 30022); attempt++)
            {
                if (!scroll || attempt == 5)
                {
                    return false;
                }

                // 一度のScrollIntoViewの後、表示更新を最大100ms待ちます。
                if (token.WaitHandle.WaitOne(20))
                {
                    token.ThrowIfCancellationRequested();
                }
            }

            Native.Point? point = Prepare(item);
            if (!point.HasValue)
            {
                return false;
            }

            return Mouse.Click(root, point.Value, flags, token);
        }

        private bool Pattern<T>(IUiaElement item, int id, Func<T, bool> action,
            bool prepare = false)
            where T : class
        {
            (bool Supported, bool Success) result = UiaPatternAttempt.Run<T>(() =>
            {
                Guid iid = typeof(T).GUID;
                return item.PatternAs(id, ref iid);
            }, value => Own(value), typed =>
            {
                if (prepare)
                {
                    actionStarted = true;
                    Prepare(item);
                }
                return action(typed);
            }, token);
            missingPattern |= !result.Supported;
            return result.Success;
        }

        private bool NoOp(IUiaElement item)
        {
            actionStarted = true;
            Prepare(item);
            return true;
        }

        private bool Button(IUiaElement item, int type)
        {
            if (type == 50013)
            {
                return !On ? NoOp(item) : Select(item);
            }

            if (type == 50002 && PointerClick)
            {
                return Physical(item);
            }

            if (Boolean(item, 30041))
            {
                // 不正な状態はフォーカス準備やToggleを行う前に拒否します。
                ValidateToggleRequest(request);
                return Pattern<IUiaToggle>(item, 10015,
                    pattern => SetToggle(pattern, request, token), prepare: true);
            }

            if (type == 50000)
            {
                return !On ? NoOp(item) : Invoke(item);
            }

            missingPattern = true;
            return false;
        }

        private bool Invoke(IUiaElement item) => PointerClick ? Physical(item)
            : Pattern<IUiaInvoke>(item, 10000, pattern =>
            {
                WindowsOperationErrors.Provider(pattern.Invoke);
                return true;
            }, prepare: true);

        private bool Select(IUiaElement item) => PointerClick ? Physical(item, scroll: true)
            : Pattern<IUiaSelectionItemPattern>(item, 10010, pattern =>
            {
                WindowsOperationErrors.Provider(pattern.Select);
                return true;
            }, prepare: true);

        private void Expanded(IUiaElement item, Action action)
        {
            if (!Pattern<IUiaExpandCollapsePattern>(item, 10005, pattern =>
            {
                UiaItemInventory.ReadExpandedPreservingState(pattern, action, token);
                return true;
            }))
            {
                action();
            }
        }

        private void Lists()
        {
            int container = 0;
            bool ofThis = (flags & ClickItemFlags.OfThis) != 0;
            long number = Math.Abs((long)occurrence);
            foreach (int type in new[] { 50008, 50003 })
            {
                Visit(element, type, 7, list =>
                {
                    // 通常の番号0は全コンテナ、OFTHISは一致項目を横断して数えます。
                    if (!ofThis && ++container != number && number != 0)
                    {
                        return;
                    }

                    void Items() => Visit(list, 50007, 4, item =>
                    {
                        Match(item, Name(item), () =>
                        {
                            return !On ? NoOp(item) : Select(item);
                        }, numbered: ofThis);
                    });

                    if (name.Contains('\t'))
                    {
                        MultipleListItems(list, ofThis);
                        return;
                    }

                    if (type == 50003)
                    {
                        Expanded(list, Items);
                    }
                    else
                    {
                        Items();
                    }
                });
            }
        }

        private void MultipleListItems(IUiaElement list, bool ofThis)
        {
            // 複数名を単一選択や物理クリックへ縮めず、選択patternで扱います。
            if (!Boolean(list, 30060))
            {
                return;
            }

            string[] terms = [.. name.Split('\t')
                .Select(term => ButtonName.Normalize(Partial
                    ? term : NativeListControl.Trim(term)))
                .Where(term => term.Length != 0)];
            List<IUiaElement> items = [];
            Visit(list, 50007, 4, item =>
            {
                string caption = ButtonName.Normalize(Name(item));
                if (Boolean(item, 30010) && terms.Any(term => Partial
                    ? caption.Contains(term, StringComparison.OrdinalIgnoreCase)
                    : AccessibleItems.MatchesName(caption, term)))
                {
                    items.Add(item);
                }
            });
            if (items.Count == 0)
            {
                return;
            }

            recognized = true;
            if (ofThis)
            {
                long index = Math.Max(1, Math.Abs((long)occurrence)) - matched - 1;
                matched += items.Count;
                if (index < 0 || index >= items.Count)
                {
                    return;
                }

                items = [items[(int)index]];
            }

            found = true;
            if (PointerClick)
            {
                // 修飾キーなしのクリックでは選択集合を保持できません。
                success = false;
                return;
            }

            bool first = true;
            success = true;
            foreach (IUiaElement item in items)
            {
                token.ThrowIfCancellationRequested();
                bool selected = !On ? NoOp(item)
                    : Pattern<IUiaSelectionItemPattern>(item, 10010, pattern =>
                    {
                        WindowsOperationErrors.Provider(first
                            ? pattern.Select : pattern.AddToSelection);
                        return true;
                    }, prepare: true);
                success &= selected;
                if (!selected)
                {
                    break;
                }

                first = false;
            }
        }

        private void Paths(IUiaElement parent, int type, string prefix,
            int depth, bool menu)
        {
            if (depth >= 128)
            {
                throw Invalid("UIA操作の探索深度が上限を超えました。");
            }

            Visit(parent, type, menu && depth == 0 ? 4 : 2, item =>
            {
                if (menu && !ApplicationMenu(item))
                {
                    return;
                }

                string caption = Name(item);
                if (caption.Length >= NativeListControl.MaximumCharacters - prefix.Length)
                {
                    throw Invalid("UIA操作のパス長が上限を超えました。");
                }

                string path = prefix + "\\" + caption;
                Match(item, name.StartsWith('\\') ? path : caption, () =>
                {
                    if (menu)
                    {
                        if (PointerClick)
                        {
                            return Physical(item);
                        }

                        return Boolean(item, 30041)
                            ? Pattern<IUiaToggle>(item, 10015,
                                pattern => SetToggle(pattern, On ? 1 : 0, token), prepare: true)
                            : !On ? NoOp(item) : Invoke(item);
                    }

                    return !On ? NoOp(item) : Select(item);
                });
                if (!found)
                {
                    Expanded(item, () => Paths(item, type, path, depth + 1, menu));
                }
            });
        }

        private bool ApplicationMenu(IUiaElement item)
        {
            List<IUiaElement> parents = [];
            Exception? primary = null;
            try
            {
                IUiaElement current = item;
                for (int depth = 0; depth < 128; depth++)
                {
                    token.ThrowIfCancellationRequested();
                    // タイトルバーのシステムメニューを探索のために展開しません。
                    if (Property(current, 30003) is 50037)
                    {
                        return false;
                    }

                    int same = 0;
                    WindowsOperationErrors.Provider(() =>
                        automation.CompareElements(current, element, out same));
                    if (same != 0)
                    {
                        return true;
                    }

                    walker ??= Own(WindowsOperationErrors.ProviderReference(
                        automation.RawViewWalker));
                    IUiaElement? ancestor = WindowsOperationErrors.Provider(
                        () => walker.Parent(current));
                    if (ancestor is null)
                    {
                        return true;
                    }

                    parents.Add(ancestor);
                    current = ancestor;
                }

                throw Invalid("UIAメニューの祖先探索深度が上限を超えました。");
            }
            catch (Exception failure) when (
                ProviderCleanup.PreservePrimary(failure, ref primary))
            {
                throw;
            }
            finally
            {
                var cleanup = new ProviderCleanup(primary);
                for (int index = parents.Count - 1; index >= 0; index--)
                {
                    cleanup.Release(parents[index]);
                }
                cleanup.Finish();
            }
        }

        private string CellText(IUiaElement item)
        {
            string caption = Name(item);
            bool first = true;
            Visit(item, 50020, 4, text =>
            {
                if (first)
                {
                    caption = Name(text);
                    first = false;
                }
            });
            return caption;
        }
    }
}

[ComImport]
[Guid("a8efa66a-0fda-421a-9194-38021f3578ea")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUiaSelectionItemPattern
{
    void Select();

    void AddToSelection();

    void RemoveFromSelection();
}

[ComImport]
[Guid("b488300f-d015-4f19-9c29-bb595e3645ef")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUiaScrollItemPattern
{
    void ScrollIntoView();
}
