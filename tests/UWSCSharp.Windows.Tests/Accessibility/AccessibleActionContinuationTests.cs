using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class AccessibleActionContinuationTests
{
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class FailingNameProvider()
        : AccessibleTestProvider(43, 0, "Same", ""), IAccessibleNative
    {
        string? IAccessibleNative.Name(object child) =>
            throw new COMException("再開位置より前の候補", unchecked((int)0x80004005));
    }

    [Theory]
    [InlineData(false, 0, 2, "&Same")]
    [InlineData(false, 1, 1, "&Same")]
    [InlineData(false, 1, 2, null)]
    [InlineData(true, 1, 2, "Same")]
    [InlineData(true, 0, 1, "Same")]
    [InlineData(true, 0, 2, null)]
    public void ResumeCountsFromSavedPositionInclusively(bool reverse,
        int position, int number, string? expected)
    {
        var provider = new AccessibleTestProvider(10, 0, "", "",
            new(43, 0, "Same", ""), new(43, 0, "&Same", ""));
        string? action = null;
        List<string> result = AccessibleItems.ReadProvider(provider, 32768,
            reverse, true, 1, default, 7, number, "Same",
            write: (target, child, _) => action = target.Name(child),
            resumePath: [position]);
        Assert.Equal(expected, action);
        Assert.Equal(expected is null ? [] : new[] { "1" }, result);
    }

    [Fact]
    public void SelectedPathCanResumeThroughAncestorsAndLaterSiblings()
    {
        var provider = new AccessibleTestProvider(10, 0, "", "",
            new(10, 0, "", "", new(43, 0, "Same", ""), new(43, 0, "&Same", "")),
            new(10, 0, "", "", new AccessibleTestProvider(43, 0, "Sa&me", "")));
        int[]? selected = null;
        AccessibleItems.ReadProvider(provider, 32768, false, true, 1,
            default, 7, 2, "Same", selectedPath: path => selected = path);
        Assert.NotNull(selected);
        Assert.Equal([0, 1], selected);
        string? action = null;
        int[]? next = null;
        AccessibleItems.ReadProvider(provider, 32768, false, true, 1,
            default, 7, 2, "Same",
            write: (target, child, _) => action = target.Name(child),
            resumePath: selected, selectedPath: path => next = path);
        Assert.Equal("Sa&me", action);
        Assert.NotNull(next);
        Assert.Equal([1, 0], next);
    }

    [Fact]
    public void EarlierSubtreeIsNotQueriedAfterResume()
    {
        var provider = new AccessibleTestProvider(10, 0, "", "",
            new FailingNameProvider(), new(43, 0, "Same", ""));
        Assert.Throws<COMException>(() => AccessibleItems.ReadProvider(provider,
            32768, false, true, 1, default, 7, 1, "Same"));
        Assert.Equal(new[] { "1" }, AccessibleItems.ReadProvider(provider,
            32768, false, true, 1, default, 7, 1, "Same", resumePath: [1]));
    }

    [Fact]
    public void CancellationOrActionFailureDoesNotCommitPosition()
    {
        var provider = new AccessibleTestProvider(43, 0, "Same", "");
        int commits = 0;
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => AccessibleItems.ReadProvider(
            provider, 32768, false, true, 1, cancellation.Token, 7, 1, "Same",
            write: (_, _, _) => cancellation.Cancel(), selectedPath: _ => commits++));
        Assert.Throws<InvalidOperationException>(() => AccessibleItems.ReadProvider(
            provider, 32768, false, true, 1, default, 7, 1, "Same",
            write: (_, _, _) => throw new InvalidOperationException("操作失敗"),
            selectedPath: _ => commits++));
        Assert.Equal(0, commits);
    }

    [Fact]
    public void StateIsBoundedToOneRootAndDoesNotRetainCallerArrays()
    {
        using var fixture = new ButtonControlTests.Fixture();
        nint child = fixture.Child("STATIC", "child", 0, 10);
        var state = new AccessibleActionContinuation();
        AccessibleActionContinuation.Snapshot first = state.Begin(fixture.Root, 0);
        int[] path = [1, 2];
        state.Save(first, path);
        path[0] = 9;
        AccessibleActionContinuation.Snapshot resumed = state.Begin(fixture.Root,
            (int)ClickItemFlags.Continue);
        Assert.NotNull(resumed.Path);
        Assert.Equal([1, 2], resumed.Path);
        resumed.Path![0] = 8;
        AccessibleActionContinuation.Snapshot copy = state.Begin(fixture.Root,
            (int)ClickItemFlags.Continue);
        Assert.NotNull(copy.Path);
        Assert.Equal([1, 2], copy.Path);
        Assert.Null(state.Begin(child, (int)ClickItemFlags.Continue).Path);
    }

    [Fact]
    public void FreshSearchClearAndLaterCallsInvalidateOldWorkerCommits()
    {
        using var fixture = new ButtonControlTests.Fixture();
        var state = new AccessibleActionContinuation();
        AccessibleActionContinuation.Snapshot old = state.Begin(fixture.Root, 0);
        state.Save(old, [1]);
        AccessibleActionContinuation.Snapshot fresh = state.Begin(fixture.Root, 0);
        Assert.Null(fresh.Path);
        state.Save(old, [2]);
        Assert.Null(state.Begin(fixture.Root, (int)ClickItemFlags.Continue).Path);
        state.Clear();
        state.Save(fresh, [3]);
        Assert.Null(state.Begin(fixture.Root, (int)ClickItemFlags.Continue).Path);
    }
}
