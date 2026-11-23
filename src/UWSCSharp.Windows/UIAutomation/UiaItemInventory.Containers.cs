using System.Runtime.InteropServices;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

internal static partial class UiaItemInventory
{
    internal static void ReadExpandedPreservingState(
        IUiaExpandCollapsePattern pattern, Action read, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        int state = WindowsOperationErrors.Provider(pattern.State);
        token.ThrowIfCancellationRequested();
        if (state is < 0 or > 3)
        {
            throw WindowsOperationErrors.Record(
                new InvalidOperationException("UIAの展開状態が不正です。"));
        }

        if (state == 2)
        {
            throw WindowsOperationErrors.Record(new NotSupportedException(
                "UIAの部分展開を元の状態へ復元する操作はありません。"));
        }

        if (state != 0)
        {
            read();
            token.ThrowIfCancellationRequested();
            return;
        }

        Exception? primary = null;
        try
        {
            WindowsOperationErrors.Provider(pattern.Expand);
            token.ThrowIfCancellationRequested();
            read();
            token.ThrowIfCancellationRequested();
        }
        catch (Exception failure)
        {
            primary = failure;
            throw;
        }
        finally
        {
            try
            {
                WindowsOperationErrors.Provider(pattern.Collapse);
            }
            catch (Exception cleanup) when (primary is not null)
            {
                primary.Data["UWSCSharp.UiaItemExpansionRestoreError"] = cleanup;
            }
        }
    }

    private sealed partial class Reader
    {
        private bool NativeTarget(IUiaElement item, NativeSelectionKind kind,
            out nint window)
        {
            window = 0;
            if (WindowsOperationErrors.Provider(() => item.Property(30020)) is not int handle
                || handle == 0)
            {
                return false;
            }

            window = unchecked((nint)(uint)handle);
            return (window == root || Native.IsChild(root, window))
                && NativeSelectionTarget.Kind(NativeSelectionTarget.Label(window)) == kind;
        }

        private void ReadLists(int occurrence)
        {
            bool all = occurrence < 0;
            int remaining = Math.Max(1, occurrence);
            foreach (int type in new[] { 50008, 50003 })
            {
                if (!Visit(element, type, 7, item =>
                {
                    if (!all && --remaining != 0)
                    {
                        return true;
                    }

                    if (type == 50003 && NativeTarget(item, NativeSelectionKind.Combo,
                        out nint window))
                    {
                        NativeListControl.ReadAsync(window, token, Emit)
                            .GetAwaiter().GetResult();
                    }
                    else
                    {
                        if (type == 50008)
                        {
                            _ = WithPattern<IUiaScrollPattern>(item, 10004,
                                pattern => UiaSelection.PrepareScroll(pattern, token));
                        }

                        void ReadItems()
                        {
                            int found = 0;
                            bool Add(IUiaElement child)
                            {
                                found++;
                                Emit(ItemText(child));
                                return true;
                            }

                            Visit(item, 50007, 2, Add);
                            if (found == 0)
                            {
                                Visit(item, 50007, 4, Add);
                            }
                        }

                        if (type == 50003)
                        {
                            Expanded(item, ReadItems);
                        }
                        else
                        {
                            ReadItems();
                        }
                    }

                    return all;
                }))
                {
                    return;
                }
            }
        }

        private void Expanded(IUiaElement item, Action read)
        {
            if (!WithPattern<IUiaExpandCollapsePattern>(item, 10005,
                pattern => ReadExpandedPreservingState(pattern, read, token)))
            {
                read();
            }
        }

        private void ReadTrees(int occurrence)
        {
            int remaining = Math.Max(1, occurrence);
            Visit(element, 50023, 7, item =>
            {
                if (occurrence >= 0 && --remaining != 0)
                {
                    return true;
                }

                Visit(item, 50024, 2, child =>
                {
                    ReadLeaves(child, 50024, "", 0, normalize: false);
                    return true;
                });
                return occurrence < 0;
            });
        }

        private void ReadLeaves(IUiaElement item, int type, string path,
            int depth, bool normalize)
        {
            token.ThrowIfCancellationRequested();
            if (depth >= 128)
            {
                throw Invalid("UIA項目の探索深度が上限を超えました。");
            }

            string name = ItemText(item);
            if (normalize)
            {
                name = ButtonName.Normalize(name);
            }

            if (name.Length >= NativeListControl.MaximumCharacters - path.Length)
            {
                throw Invalid("UIA項目のパスが取得上限を超えました。");
            }

            string current = path + "\\" + name;
            Expanded(item, () =>
            {
                int children = 0;
                Visit(item, type, 2, child =>
                {
                    children++;
                    ReadLeaves(child, type, current, depth + 1, normalize);
                    return true;
                });
                if (children == 0)
                {
                    Emit(current);
                }
            });
        }

        private void ReadGrids(int occurrence, int column)
        {
            int remaining = Math.Max(1, occurrence);
            Visit(element, 50028, 7, item =>
            {
                if (occurrence >= 0 && --remaining != 0)
                {
                    return true;
                }

                if (column < 0)
                {
                    if (!WithPattern<IUiaTablePattern>(item, 10012, ReadColumnHeaders))
                    {
                        Visit(item, 50034, 2, headers =>
                        {
                            Visit(headers, 50035, 2, header =>
                            {
                                Emit(Name(header));
                                return true;
                            });
                            return true;
                        });
                    }
                }
                else
                {
                    _ = WithPattern<IUiaGridPattern>(item, 10006, grid =>
                    {
                        int rows = WindowsOperationErrors.Provider(grid.RowCount);
                        int columns = WindowsOperationErrors.Provider(grid.ColumnCount);
                        if (rows < 0 || rows > UwscArray.MaximumElements
                            || columns < 0 || columns > 100000)
                        {
                            throw Invalid("UIA Gridの行数または列数が上限を超えました。");
                        }

                        int selected = Math.Max(1, column) - 1;
                        if (selected >= columns)
                        {
                            return;
                        }

                        for (int row = 0; row < rows; row++)
                        {
                            token.ThrowIfCancellationRequested();
                            IUiaElement cell = WindowsOperationErrors.ProviderReference(
                                () => grid.GetItem(row, selected));
                            Exception? failure = null;
                            try
                            {
                                Emit(ItemText(cell));
                            }
                            catch (Exception error) when (
                                ProviderCleanup.PreservePrimary(error, ref failure))
                            {
                                throw;
                            }
                            finally
                            {
                                var cleanup = new ProviderCleanup(failure);
                                cleanup.Release(cell);
                                cleanup.Finish();
                            }
                        }
                    });
                }

                return occurrence < 0;
            });
        }

        private void ReadColumnHeaders(IUiaTablePattern table)
        {
            IUiaElements? headers = WindowsOperationErrors.Provider(table.ColumnHeaders);
            Exception? failure = null;
            try
            {
                if (headers is null)
                {
                    return;
                }

                int count = WindowsOperationErrors.Provider(headers.Length);
                if (count < 0 || count > 100000 - candidates)
                {
                    throw Invalid("UIA列見出し数が上限を超えました。");
                }

                candidates += count;
                for (int index = 0; index < count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    IUiaElement header = WindowsOperationErrors.ProviderReference(
                        () => headers.GetElement(index));
                    Exception? itemFailure = null;
                    try
                    {
                        if (includeDisabled || WindowsOperationErrors.Provider(
                            () => header.Property(30010)) is true)
                        {
                            Emit(Name(header));
                        }
                    }
                    catch (Exception error) when (
                        ProviderCleanup.PreservePrimary(error, ref itemFailure))
                    {
                        throw;
                    }
                    finally
                    {
                        var cleanup = new ProviderCleanup(itemFailure);
                        cleanup.Release(header);
                        cleanup.Finish();
                    }
                }
            }
            catch (Exception error) when (
                ProviderCleanup.PreservePrimary(error, ref failure))
            {
                throw;
            }
            finally
            {
                var cleanup = new ProviderCleanup(failure);
                cleanup.ReleaseComOnly(headers);
                cleanup.Finish();
            }
        }

        private void ReadMenus()
        {
            Visit(element, 50011, 7, item =>
            {
                if (HasMenuParent(item)
                    || StringProperty(item, 30007).Contains("SPACE",
                        StringComparison.OrdinalIgnoreCase) || !HasBounds(item))
                {
                    return true;
                }

                ReadLeaves(item, 50011, "", 0, normalize: true);
                return true;
            });
        }

        private bool HasMenuParent(IUiaElement item)
        {
            IUiaTreeWalker walker = WindowsOperationErrors.ProviderReference(
                () => automation.RawViewWalker());
            List<object> parents = [walker];
            Exception? failure = null;
            try
            {
                IUiaElement current = item;
                for (int depth = 0; depth < 128; depth++)
                {
                    token.ThrowIfCancellationRequested();
                    IUiaElement? parent = WindowsOperationErrors.Provider(
                        () => walker.Parent(current));
                    if (parent is null)
                    {
                        return false;
                    }

                    parents.Add(parent);
                    if (WindowsOperationErrors.Provider(() => parent.Property(30003)) is 50011)
                    {
                        return true;
                    }

                    int same = 0;
                    WindowsOperationErrors.Provider(
                        () => automation.CompareElements(parent, element, out same));
                    if (same != 0)
                    {
                        return false;
                    }

                    current = parent;
                }

                throw Invalid("UIAメニューの祖先探索深度が上限を超えました。");
            }
            catch (Exception error) when (
                ProviderCleanup.PreservePrimary(error, ref failure))
            {
                throw;
            }
            finally
            {
                var cleanup = new ProviderCleanup(failure);
                for (int index = parents.Count - 1; index >= 0; index--)
                {
                    cleanup.Release(parents[index]);
                }

                cleanup.Finish();
            }
        }
    }
}

[ComImport]
[Guid("414c3cdc-856b-4f5b-8538-3131c6302550")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUiaGridPattern
{
    IUiaElement GetItem(int row, int column);

    int RowCount();

    int ColumnCount();
}

[ComImport]
[Guid("620e691c-ea96-4710-a850-754b24ce2417")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUiaTablePattern
{
    IUiaElements? RowHeaders();

    IUiaElements? ColumnHeaders();
}
