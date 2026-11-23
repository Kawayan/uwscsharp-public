using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class AccessibleProviderTests
{
    private static List<string> Read(IAccessibleNative provider,
        int kinds, bool reverse, bool includeDisabled, int maximum,
        CancellationToken token)
    {
        return OperatingSystem.IsWindows()
            ? AccessibleItems.ReadProvider(provider, kinds, reverse,
                includeDisabled, maximum, token) : [];
    }

    [Fact]
    public void NativeEnumerationKeepsNonconsecutiveSimpleChildIds()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        AccessibleTestProvider[] children =
        [
            new(42, 0x200000, "First...", "value1"),
            new(42, 0x200000, "Second...", "value2"),
            new(42, 0x200000, "Third...", "value3")
        ];
        var root = new AccessibleSimpleProvider([7, 29, 103], children);
        Assert.Equal(["First", "Second", "Third"], Read(root,
            272629760, false, true, 0, default));
        var reverse = new AccessibleSimpleProvider([7, 29, 103], children);
        Assert.Equal(["Third", "Second"], Read(reverse,
            272629760, true, true, 2, default));
    }

    [Theory]
    [InlineData(0x200000, " selected... ", "Selected", true)]
    [InlineData(0x200000, "", "value", false)]
    [InlineData(0x200000, "   ", "value", false)]
    [InlineData(0x200040, " selected... ", "value", true)]
    [InlineData(0, " selected... ", "value", false)]
    [InlineData(0x100000, " selected... ", "value", false)]
    [InlineData(0x200000, " &... ", "value", false)]
    public void Acc2UsesSelectableNameBeforeValue(int state,
        string name, string value, bool included)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var provider = new AccessibleTestProvider(42, state, name, value);
        List<string> result = Read(provider,
            272629760, false, true, 0, default);
        Assert.Equal(included ? 1 : 0, result.Count);
        if (included)
        {
            Assert.Equal(["selected"], result);
        }

        Assert.Empty(Read(provider,
            4194304, false, true, 0, default));
        Assert.Equal(Read(provider,
            8388608, false, true, 0, default),
            Read(provider,
                276824064, false, true, 0, default));
    }

    [Fact]
    public void TraversalRejectsCyclesAndExcessDepthWithoutKeepingAncestors()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var children = new AccessibleTestProvider[1];
        var cycle = new AccessibleTestProvider(10, 0, "", "", children);
        children[0] = cycle;
        InvalidOperationException cycleError = Assert.Throws<InvalidOperationException>(() => Read(
            cycle, 272629760, false, true, 0, default));
        Assert.Contains("循環", cycleError.Message);
        var leaf = new AccessibleTestProvider(42, 0x200000, "leaf", "value");
        AccessibleTestProvider root = leaf;
        for (int depth = 0; depth < 127; depth++)
        {
            root = new AccessibleTestProvider(10, 0, "", "", root);
        }

        Assert.Equal(["leaf"], Read(root,
            272629760, false, true, 0, default));
        var tooDeep = new AccessibleTestProvider(10, 0, "", "", root);
        Assert.Throws<InvalidOperationException>(() => Read(
            tooDeep, 272629760, false, true, 0, default));
        Assert.Equal(["leaf"], Read(leaf,
            272629760, false, true, 0, default));
    }

    [Fact]
    public void MaximumSkipsLaterPropertiesAndCancellationAfterValueThrows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var first = new AccessibleTestProvider(42, 0, "", "first");
        var second = new AccessibleTestProvider(42, 0, "", "second");
        var root = new AccessibleTestProvider(10, 0, "", "", first, second);
        Assert.Equal(["first"], Read(root,
            16777216, false, true, 1, default));
        Assert.Equal(1, first.ValueReads);
        Assert.Equal(0, second.ValueReads);
        using var cancellation = new CancellationTokenSource();
        first.ReadingValue = cancellation.Cancel;
        Assert.ThrowsAny<OperationCanceledException>(() => Read(
            root, 16777216, false, true, 0, cancellation.Token));
        first.ReadingValue = null;
        Assert.Equal(["first", "second"], Read(root,
            16777216, false, true, 0, default));
    }

    [Fact]
    public void Acc2AddsSelectableTextThroughProviderTraversal()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var child = new AccessibleTestProvider(42, 0x200000, " &Selected... ", "value");
        var root = new AccessibleTestProvider(10, 0, "root", "", child);
        Assert.Empty(Read(root, 4194304, false, true, 0, default));
        Assert.Equal(["Selected"], Read(root,
            272629760, false, true, 0, default));
    }
}

/// <summary>
/// 所有テストのMSAA判定を制御するproviderです。
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public class AccessibleTestProvider : IAccessibleNative
{
    private readonly int role;
    private readonly int state;
    private readonly string name;
    private string value;
    private readonly AccessibleTestProvider[] children;

    internal AccessibleTestProvider(int role, int state, string name,
        string value, params AccessibleTestProvider[] children)
    {
        this.role = role;
        this.state = state;
        this.name = name;
        this.value = value;
        this.children = children;
    }

    internal int ValueReads
    {
        get;
        private set;
    }

    internal Action? ReadingValue
    {
        get;
        set;
    }

    internal Action? ReadingDescription
    {
        get;
        set;
    }

    internal Action<int>? TakingFocus
    {
        get; set;
    }

    internal Action? PerformingAction
    {
        get; set;
    }

    internal string ActionName
    {
        get;
        set;
    } = "";

    internal bool SimpleChildren
    {
        get;
        set;
    }

    internal int[]? ChildIds
    {
        get;
        set;
    }

    internal IReadOnlyList<AccessibleTestProvider> Children => children;

    private AccessibleTestProvider Element(object child)
    {
        int id = (int)child;
        return id == 0 ? this : children[ChildIds is null
            ? id - 1 : Array.IndexOf(ChildIds, id)];
    }

    object? IAccessibleNative.Parent() => null;

    int IAccessibleNative.ChildCount() => children.Length;

    object? IAccessibleNative.Child(object child) => SimpleChildren ? null : Element(child);

    string? IAccessibleNative.Name(object child) => Element(child).name;

    string? IAccessibleNative.Value(object child)
    {
        AccessibleTestProvider element = Element(child);
        element.ValueReads++;
        element.ReadingValue?.Invoke();
        return element.value;
    }

    string? IAccessibleNative.Description(object child)
    {
        Element(child).ReadingDescription?.Invoke();
        return "";
    }

    object? IAccessibleNative.Role(object child) => Element(child).role;

    object? IAccessibleNative.State(object child) => Element(child).state;

    string? IAccessibleNative.Help(object child) => "";

    int IAccessibleNative.HelpTopic(out string? file, object child)
    {
        file = null;
        return 0;
    }

    string? IAccessibleNative.KeyboardShortcut(object child) => "";

    object? IAccessibleNative.Focus() => null;

    object? IAccessibleNative.Selection() => null;

    string? IAccessibleNative.DefaultAction(object child) => Element(child).ActionName;

    void IAccessibleNative.Select(int flags, object child)
    {
        if (Element(child).TakingFocus is not Action<int> action)
        {
            throw new InvalidOperationException("操作は禁止です。");
        }

        action(flags);
    }

    void IAccessibleNative.Location(out int x, out int y,
        out int width, out int height, object child)
    {
        x = 10;
        y = 10;
        width = 100;
        height = 20;
    }

    object? IAccessibleNative.Navigate(int direction, object child) => null;

    object? IAccessibleNative.HitTest(int x, int y) => null;

    void IAccessibleNative.DoDefaultAction(object child)
    {
        if (Element(child).PerformingAction is not Action action)
        {
            throw new InvalidOperationException("操作は禁止です。");
        }

        action();
    }

    void IAccessibleNative.SetName(object child, string name) =>
        throw new InvalidOperationException("名前の変更は禁止です。");

    void IAccessibleNative.SetValue(object child, string replacement) =>
        Element(child).value = replacement;
}


/// <summary>
/// 非連続の単純子IDをnative列挙へ返すテストproviderです。
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class AccessibleSimpleProvider : AccessibleTestProvider, IEnumVARIANT
{
    private int position;

    internal object?[]? Variants
    {
        get;
        set;
    }

    internal AccessibleSimpleProvider(int[] ids, AccessibleTestProvider[] children)
        : base(10, 0, "", "", children)
    {
        ChildIds = ids;
        SimpleChildren = true;
    }

    int IEnumVARIANT.Next(int count, object?[] values, nint fetched)
    {
        int obtained = Math.Min(count, ChildIds!.Length - position);
        for (int i = 0; i < obtained; i++)
        {
            values[i] = Variants is null ? ChildIds[position++] : Variants[position++];
        }

        if (fetched != 0)
        {
            Marshal.WriteInt32(fetched, obtained);
        }

        return obtained == count ? 0 : 1;
    }

    int IEnumVARIANT.Skip(int count)
    {
        int skipped = Math.Min(count, ChildIds!.Length - position);
        position += skipped;
        return skipped == count ? 0 : 1;
    }

    int IEnumVARIANT.Reset()
    {
        position = 0;
        return 0;
    }

    IEnumVARIANT IEnumVARIANT.Clone()
    {
        return new AccessibleSimpleProvider(ChildIds!, [.. Children])
        {
            position = position
        };
    }
}
