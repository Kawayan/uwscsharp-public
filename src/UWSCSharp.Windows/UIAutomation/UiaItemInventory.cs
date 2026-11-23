using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// UIAルートの項目を種別ごとに逐次収集します。
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class UiaItemInventory
{
    internal static Task ReadAsync(nint root, ItemKinds kinds,
        int occurrence, int column, bool includeDisabled,
        CancellationToken token, Action<string> emit,
        UiaMozillaPreparation? preparation = null,
        Action<ItemKinds>? nativeFallback = null) => Task.Run(() =>
    {
        void Read(Action? prepare)
        {
            token.ThrowIfCancellationRequested();
            if (root == 0 || !Native.IsWindow(root))
            {
                return;
            }

            using var reader = new Reader(root, includeDisabled, token, emit, nativeFallback);
            reader.Run(kinds, occurrence, column, prepare);
        }

        if (preparation is null)
        {
            Read(null);
        }
        else
        {
            preparation.Execute(root, prepare =>
            {
                Read(prepare);
                return true;
            }, token);
        }
    }, token);

    private sealed partial class Reader(nint root, bool includeDisabled,
        CancellationToken token, Action<string> emit,
        Action<ItemKinds>? nativeFallback) : IDisposable
    {
        private readonly List<object> owned = [];
        private IUiaAutomation automation = null!;
        private IUiaElement element = null!;
        private Exception? primary;
        private int candidates;

        internal void Run(ItemKinds kinds, int occurrence, int column,
            Action? prepare)
        {
            try
            {
                automation = WindowsOperationErrors.Provider(
                    () => (IUiaAutomation)new UiaComObject());
                owned.Add(automation);
                try
                {
                    element = WindowsOperationErrors.ProviderReference(
                        () => automation.ElementFromHandle(root));
                }
                catch (COMException failure) when (
                    failure.HResult == unchecked((int)0x80040201))
                {
                    return;
                }

                owned.Add(element);
                if (prepare is not null)
                {
                    prepare();
                    element = WindowsOperationErrors.ProviderReference(
                        () => automation.ElementFromHandle(root));
                    owned.Add(element);
                }

                foreach (ItemKinds kind in new[]
                {
                    ItemKinds.Button, ItemKinds.List, ItemKinds.Tab,
                    ItemKinds.Menu, ItemKinds.TreeView, ItemKinds.ListView,
                    ItemKinds.Edit, ItemKinds.Static, ItemKinds.StatusBar,
                    ItemKinds.Toolbar, ItemKinds.Link
                })
                {
                    if ((kinds & kind) == 0)
                    {
                        continue;
                    }

                    switch (kind)
                    {
                        case ItemKinds.Button:
                            foreach (int type in new[] { 50000, 50002, 50013 })
                            {
                                Visit(element, type, 7, item =>
                                {
                                    if (HasBounds(item))
                                    {
                                        Emit(ButtonName.Normalize(Name(item)));
                                    }

                                    return true;
                                });
                            }

                            break;
                        case ItemKinds.List:
                            ReadLists(occurrence);
                            break;
                        case ItemKinds.Tab:
                            Visit(element, 50019, 7, item =>
                            {
                                Emit(ButtonName.Normalize(ItemText(item)));
                                return true;
                            });
                            break;
                        case ItemKinds.Menu:
                            ReadMenus();
                            break;
                        case ItemKinds.TreeView:
                            ReadTrees(occurrence);
                            break;
                        case ItemKinds.ListView:
                            ReadGrids(occurrence, column);
                            break;
                        case ItemKinds.Edit:
                            Visit(element, 50004, 7, item =>
                            {
                                Emit(StringProperty(item, 30045));
                                return true;
                            });
                            break;
                        case ItemKinds.Static:
                            ReadStaticItems();
                            break;
                        case ItemKinds.StatusBar:
                            Visit(element, 50017, 7, parent =>
                            {
                                Visit(parent, 50020, 2, item =>
                                {
                                    Emit(Name(item));
                                    return true;
                                });
                                return false;
                            });
                            break;
                        case ItemKinds.Toolbar:
                            Visit(element, 50021, 7, item =>
                            {
                                Emit(Name(item));
                                return true;
                            });
                            break;
                        case ItemKinds.Link:
                            Visit(element, 50005, 7, item =>
                            {
                                Emit(Name(item));
                                return true;
                            });
                            break;
                    }
                }

                token.ThrowIfCancellationRequested();
            }
            catch (Exception failure) when (
                ProviderCleanup.PreservePrimary(failure, ref primary))
            {
                throw;
            }
        }

        private void ReadStaticItems()
        {
            int count = 0;
            foreach (int type in new[] { 50032, 50026 })
            {
                Visit(element, type, 7, parent =>
                {
                    Visit(parent, 50020, 2, item =>
                    {
                        Emit(Name(item));
                        count++;
                        return true;
                    });
                    return true;
                }, filterDisabled: false);
            }

            if (count == 0)
            {
                nativeFallback?.Invoke(ItemKinds.Static);
            }
        }

        private void Emit(string text)
        {
            token.ThrowIfCancellationRequested();
            emit(text);
        }

        private string Name(IUiaElement item) => StringProperty(item, 30005);

        private static string StringProperty(IUiaElement item, int property)
        {
            object? value = WindowsOperationErrors.Provider(() => item.Property(property));
            Exception? failure = null;
            try
            {
                string text = value is null ? "" : value as string
                    ?? throw Invalid("UIAの文字列プロパティが不正です。");
                if (text.Length > NativeListControl.MaximumCharacters)
                {
                    throw Invalid("UIAの文字数が取得上限を超えました。");
                }

                return text;
            }
            catch (Exception error) when (
                ProviderCleanup.PreservePrimary(error, ref failure))
            {
                throw;
            }
            finally
            {
                var cleanup = new ProviderCleanup(failure);
                cleanup.ReleaseComOnly(value);
                cleanup.Finish();
            }
        }

        private bool HasBounds(IUiaElement item)
        {
            object? value = WindowsOperationErrors.Provider(() => item.Property(30001));
            Exception? failure = null;
            try
            {
                if (value is not double[] bounds || bounds.Length != 4
                    || bounds.Any(number => !double.IsFinite(number)))
                {
                    throw Invalid("UIAの矩形プロパティが不正です。");
                }

                return bounds[2] > 0 && bounds[3] > 0;
            }
            catch (Exception error) when (
                ProviderCleanup.PreservePrimary(error, ref failure))
            {
                throw;
            }
            finally
            {
                var cleanup = new ProviderCleanup(failure);
                cleanup.ReleaseComOnly(value);
                cleanup.Finish();
            }
        }

        private string ItemText(IUiaElement item)
        {
            string? result = null;
            Visit(item, 50020, 2, child =>
            {
                result = Name(child);
                return false;
            }, filterDisabled: false);
            return result ?? Name(item);
        }

        private bool Visit(IUiaElement parent, int type, int scope,
            Func<IUiaElement, bool> read, bool filterDisabled = true)
        {
            token.ThrowIfCancellationRequested();
            IUiaCondition? condition = null;
            IUiaElements? items = null;
            Exception? failure = null;
            try
            {
                condition = WindowsOperationErrors.ProviderReference(
                    () => automation.CreatePropertyCondition(30003, type));
                items = WindowsOperationErrors.ProviderReference(
                    () => parent.FindAll(scope, condition));
                int count = WindowsOperationErrors.Provider(() => items.Length());
                if (count < 0 || count > 100000 - candidates)
                {
                    throw Invalid("UIA GETITEMの候補数が上限を超えました。");
                }

                candidates += count;
                for (int index = 0; index < count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    IUiaElement item = WindowsOperationErrors.ProviderReference(
                        () => items.GetElement(index));
                    Exception? itemFailure = null;
                    try
                    {
                        if ((!filterDisabled || includeDisabled
                            || WindowsOperationErrors.Provider(() => item.Property(30010)) is true)
                            && !read(item))
                        {
                            return false;
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
                        cleanup.Release(item);
                        cleanup.Finish();
                    }
                }

                return true;
            }
            catch (Exception error) when (
                ProviderCleanup.PreservePrimary(error, ref failure))
            {
                throw;
            }
            finally
            {
                var cleanup = new ProviderCleanup(failure);
                cleanup.ReleaseComOnly(items);
                cleanup.ReleaseComOnly(condition);
                cleanup.Finish();
            }
        }

        private bool WithPattern<T>(IUiaElement item, int id, Action<T> read)
        {
            token.ThrowIfCancellationRequested();
            object? pattern = null;
            Exception? failure = null;
            try
            {
                Guid iid = typeof(T).GUID;
                try
                {
                    pattern = WindowsOperationErrors.Provider(
                        () => item.PatternAs(id, ref iid));
                }
                catch (COMException error) when (error.HResult is
                    unchecked((int)0x80040204) or unchecked((int)0x80004002))
                {
                    token.ThrowIfCancellationRequested();
                    return false;
                }

                if (pattern is not T typed)
                {
                    return false;
                }

                read(typed);
                token.ThrowIfCancellationRequested();
                return true;
            }
            catch (Exception error) when (
                ProviderCleanup.PreservePrimary(error, ref failure))
            {
                throw;
            }
            finally
            {
                var cleanup = new ProviderCleanup(failure);
                cleanup.ReleaseComOnly(pattern);
                cleanup.Finish();
            }
        }

        private static InvalidOperationException Invalid(string message) =>
            WindowsOperationErrors.Record(new InvalidOperationException(message));

        /// <summary>
        /// 取得したUIA参照を主例外を保って解放します。
        /// </summary>
        public void Dispose()
        {
            var cleanup = new ProviderCleanup(primary);
            for (int index = owned.Count - 1; index >= 0; index--)
            {
                cleanup.Release(owned[index]);
            }

            cleanup.Finish();
        }
    }
}
