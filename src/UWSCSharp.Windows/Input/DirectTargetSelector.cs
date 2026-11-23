namespace UWSCSharp.Windows;

internal sealed record DirectTargetCandidate(
    nint Window, string ClassName, bool Visible, bool Enabled,
    uint Style, bool HasChildren, Native.Rect Rect,
    bool UnderCursor);

internal static class DirectTargetSelector
{
    internal const int KeyboardMode = 0x20000;
    internal const int MouseMode = 0xc000;

    // 62019Cは端のU+0000〜0020を除去し、ASCII小文字だけを大文字にします。
    internal static string NormalizeLabel(string className)
    {
        int start = 0;
        int end = className.Length;
        while (start < end && className[start] <= '\u0020')
        {
            start++;
        }

        while (end > start && className[end - 1] <= '\u0020')
        {
            end--;
        }

        char[] label = className.ToCharArray(start, end - start);
        for (int index = 0; index < label.Length; index++)
        {
            if (label[index] is >= 'a' and <= 'z')
            {
                label[index] = (char)(label[index] ^ 0x20);
            }
        }

        return new string(label);
    }

    // ClassNameにはNormalizeLabelによる正規化済みラベルを渡します。
    internal static DirectTargetCandidate[] Select(
        IEnumerable<DirectTargetCandidate> candidates,
        bool rootVisible, int mode, bool includeDisabled = false)
    {
        if (mode != KeyboardMode && mode != MouseMode)
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        DirectTargetCandidate[] available = [.. candidates.Where(
            candidate => AcceptEnumeration(candidate,
                rootVisible, mode, includeDisabled))];
        DirectTargetCandidate[] selected = [.. available.Where(
            candidate => mode == KeyboardMode
                ? AcceptKeyboard(candidate.ClassName)
                : AcceptMouse(candidate, available.Length))];
        int[] keys = [.. selected.Select(candidate =>
            checked(candidate.Rect.Top * 10000
                + candidate.Rect.Left))];
        if (selected.Length > 1)
        {
            Sort(selected, keys, 0, selected.Length - 1);
        }

        return selected;
    }

    internal static nint SelectOccurrence(
        IReadOnlyList<DirectTargetCandidate> candidates,
        int occurrence)
    {
        return occurrence > 0 && occurrence <= candidates.Count
            ? candidates[occurrence - 1].Window : 0;
    }

    // 原版の比較に合わせ、右端と下端も含めて逆順に探す。
    internal static nint HitTest(
        IReadOnlyList<DirectTargetCandidate> candidates,
        Native.Point screenPoint)
    {
        for (int index = candidates.Count - 1; index >= 0; index--)
        {
            Native.Rect rect = candidates[index].Rect;
            if (rect.Left <= screenPoint.X
                && screenPoint.X <= rect.Right
                && rect.Top <= screenPoint.Y
                && screenPoint.Y <= rect.Bottom)
            {
                return candidates[index].Window;
            }
        }

        return 0;
    }

    private static bool AcceptEnumeration(
        DirectTargetCandidate candidate, bool rootVisible,
        int mode, bool includeDisabled)
    {
        if (candidate.ClassName.Length == 0)
        {
            return false;
        }

        if (candidate.ClassName == "INTERNET EXPLORER_SERVER")
        {
            return true;
        }

        bool visible = candidate.Visible
            || (!rootVisible && (candidate.Style & 0x10000000) != 0);
        return visible && (includeDisabled
            || (mode & 0x0c208000) != 0 || candidate.Enabled);
    }

    private static bool AcceptKeyboard(string label)
    {
        return label.Contains(ControlClassNames.Uppercase.Edit, StringComparison.Ordinal)
            || label.Contains("DATETIMEPICK", StringComparison.Ordinal)
            || label.Contains("HM32CLIENT", StringComparison.Ordinal)
            || label == "EDTBX"
            || (label.Length == 4
                && label.StartsWith("_WW", StringComparison.Ordinal));
    }

    private static bool AcceptMouse(
        DirectTargetCandidate candidate, int count)
    {
        string label = candidate.ClassName;
        return !candidate.HasChildren || count == 1
            || label == "SHELLDLL_DEFVIEW"
            || label.Contains("PAGECONTROL", StringComparison.Ordinal)
            || label == "WMPAPPHOST"
            || label == "INTERNET EXPLORER_SERVER"
            || label == "MOZILLAWINDOWCLASS"
            || candidate.UnderCursor;
    }

    // 同値の候補も交換する中央ピボットの手順を維持する。
    internal static void Sort<T>(T[] candidates,
        int[] keys, int low, int high)
    {
        int left = low;
        int right = high;
        int pivot = keys[(low + high) / 2];
        do
        {
            while (keys[left] < pivot)
            {
                left++;
            }

            while (keys[right] > pivot)
            {
                right--;
            }

            if (left <= right)
            {
                (keys[left], keys[right]) = (keys[right], keys[left]);
                (candidates[left], candidates[right]) =
                    (candidates[right], candidates[left]);
                left++;
                right--;
            }
        }
        while (left <= right);

        if (low < right)
        {
            Sort(candidates, keys, low, right);
        }

        if (left < high)
        {
            Sort(candidates, keys, left, high);
        }
    }
}
