namespace UWSCSharp.Windows;

internal sealed class NativeMenuClickSearch(nint root, int number, bool on,
    IListSelectionPlatform platform, bool back, bool partial, CancellationToken token,
    Action? onMatch = null)
{
    private readonly HashSet<nint> ancestors = [];

    internal nint Find(nint menu, string name, int mode)
    {
        token.ThrowIfCancellationRequested();
        if (menu == 0)
        {
            return 0;
        }

        if (ancestors.Count == 64 || !ancestors.Add(menu))
        {
            throw WindowsOperationErrors.Record(
                new InvalidOperationException("menu階層が循環しているか深すぎます。"));
        }

        try
        {
            int count = NativeMenuControl.GetMenuItemCount(menu);
            _ = WindowsOperationErrors.Subtract(count, 1);
            if (count > 100000)
            {
                throw WindowsOperationErrors.Record(
                    new InvalidOperationException("menuの項目数が上限を超えました。"));
            }

            int matches = 0;
            string desired = ButtonName.Normalize(name);
            for (int index = 0; index < count; index++)
            {
                token.ThrowIfCancellationRequested();
                if (mode == 0)
                {
                    nint submenu = NativeMenuControl.GetSubMenu(menu, index);
                    if (submenu != 0)
                    {
                        nint result = Find(submenu, name, 0);
                        if (result != 0)
                        {
                            return result;
                        }

                        continue;
                    }
                }

                string candidate = ButtonName.Normalize(NativeListNameMatcher.AsciiUpper(
                    ReadName(menu, index)));
                if (!NativeListNameMatcher.Compare(ref candidate, desired, partial))
                {
                    continue;
                }

                if (mode == 1)
                {
                    return NativeMenuControl.GetSubMenu(menu, index);
                }

                if (WindowsOperationErrors.Increment(ref matches) != number && number > 0)
                {
                    continue;
                }

                onMatch?.Invoke();
                nint? action = NativeMenuControl.InvokeMatch(root, menu, index,
                    token, on, platform, back);
                if (action.HasValue)
                {
                    return action.Value;
                }
            }

            return 0;
        }
        finally
        {
            ancestors.Remove(menu);
        }
    }

    private string ReadName(nint menu, int index)
    {
        char[] text = new char[256];
        _ = NativeMenuControl.GetMenuStringW(menu, (uint)index, text, text.Length, 0x400);
        token.ThrowIfCancellationRequested();
        int end = Array.IndexOf(text, '\0');
        return new string(text, 0, end < 0 ? text.Length : end);
    }
}
