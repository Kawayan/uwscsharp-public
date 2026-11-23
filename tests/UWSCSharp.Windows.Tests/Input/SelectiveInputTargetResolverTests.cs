using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SelectiveInputTargetResolverTests
{
    [Fact]
    public void MissingPointWindowDoesNotQueryTree()
    {
        var tree = new Tree();
        Assert.Equal(0, SelectiveInputTargetResolver.Resolve(0, tree));
        Assert.Empty(tree.Queries);
    }

    [Fact]
    public void ChildResolvesToVisibleTopLevelWithoutFollowingItsOwner()
    {
        var tree = new Tree();
        tree.Parents[3] = 2;
        tree.Parents[2] = 1;
        tree.Parents[1] = 9;
        tree.TopLevels.Add(1);
        Assert.Equal(1, SelectiveInputTargetResolver.Resolve(3, tree));
        Assert.DoesNotContain("parent:1", tree.Queries);
        Assert.DoesNotContain("area:9", tree.Queries);
    }

    [Fact]
    public void ParentlessWindowIsKept()
    {
        var tree = new Tree();
        Assert.Equal(5, SelectiveInputTargetResolver.Resolve(5, tree));
        Assert.Equal(["top:5", "parent:5"], tree.Queries);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(0, 0)]
    public void ZeroExtentParentDoesNotReplaceCurrentWindow(int width, int height)
    {
        var tree = new Tree();
        tree.Parents[2] = 1;
        tree.Sizes[1] = (width, height);
        Assert.Equal(2, SelectiveInputTargetResolver.Resolve(2, tree));
        Assert.DoesNotContain("top:1", tree.Queries);
    }

    [Fact]
    public void NonzeroExtentsAllowTraversal()
    {
        var tree = new Tree();
        tree.Parents[2] = 1;
        tree.TopLevels.Add(1);
        Assert.Equal(1, SelectiveInputTargetResolver.Resolve(2, tree));
        Assert.Contains("area:1", tree.Queries);
    }

    [Fact]
    public void DeepTreeStopsAfterOneHundredParentTransitions()
    {
        var tree = new Tree();
        for (int window = 1; window <= 120; window++)
        {
            tree.Parents[window] = window + 1;
        }

        Assert.Equal(101, SelectiveInputTargetResolver.Resolve(1, tree));
        Assert.Equal(100, tree.Queries.Count(q => q.StartsWith("parent:", StringComparison.Ordinal)));
        Assert.DoesNotContain("top:101", tree.Queries);
    }

    [Fact]
    public void CyclicParentDataRemainsBounded()
    {
        var tree = new Tree();
        tree.Parents[1] = 2;
        tree.Parents[2] = 1;
        Assert.Equal(1, SelectiveInputTargetResolver.Resolve(1, tree));
        Assert.Equal(300, tree.Queries.Count);
    }

    [Fact]
    public void VisibilityMembershipIsProvidedSeparatelyFromParentEdges()
    {
        var tree = new Tree();
        tree.Parents[2] = 1;
        Assert.Equal(1, SelectiveInputTargetResolver.Resolve(2, tree));
        tree.TopLevels.Add(2);
        Assert.Equal(2, SelectiveInputTargetResolver.Resolve(2, tree));
    }

    private sealed class Tree : ISelectiveInputWindowTree
    {
        internal Dictionary<nint, nint> Parents { get; } = [];

        internal HashSet<nint> TopLevels { get; } = [];

        internal Dictionary<nint, (int Width, int Height)> Sizes { get; } = [];

        internal List<string> Queries { get; } = [];

        public bool IsVisibleTopLevel(nint window)
        {
            Queries.Add("top:" + window);
            return TopLevels.Contains(window);
        }

        public nint GetParent(nint window)
        {
            Queries.Add("parent:" + window);
            return Parents.GetValueOrDefault(window);
        }

        public bool HasArea(nint window)
        {
            Queries.Add("area:" + window);
            (int width, int height) = Sizes.GetValueOrDefault(window, (10, 10));
            return width != 0 && height != 0;
        }
    }
}
