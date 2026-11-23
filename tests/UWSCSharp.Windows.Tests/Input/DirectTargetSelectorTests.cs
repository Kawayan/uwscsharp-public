using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
public sealed class DirectTargetSelectorTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("\u0000\t\r\n\u001f ", "")]
    [InlineData(" \tEdit\r\n", "EDIT")]
    [InlineData("a\tb c", "A\tB C")]
    [InlineData("\u00a0edit\u00a0", "\u00a0EDIT\u00a0")]
    [InlineData("\u3000edit\u3000", "\u3000EDIT\u3000")]
    [InlineData("éıſａｚ", "éıſａｚ")]
    public void LabelTrimsControlsAndUppercasesOnlyAscii(
        string raw, string expected)
    {
        Assert.Equal(expected, DirectTargetSelector.NormalizeLabel(raw));
    }

    [Fact]
    public void LabelPreservesUnpairedSurrogateCodeUnits()
    {
        // 属性文字列のUTF-8変換を避け、実行時に不正UTF-16を構築します。
        string raw = new(['\ud800', 'a', '\udfff']);
        string label = DirectTargetSelector.NormalizeLabel(raw);
        Assert.Equal(3, label.Length);
        Assert.Equal('\ud800', label[0]);
        Assert.Equal('A', label[1]);
        Assert.Equal('\udfff', label[2]);
    }

    [Fact]
    public void UnicodeCaseMappingDoesNotCreateEditCandidate()
    {
        string label = DirectTargetSelector.NormalizeLabel("edıt");
        Assert.Empty(DirectTargetSelector.Select(
            [Candidate(1) with { ClassName = label }], true,
            DirectTargetSelector.KeyboardMode));
    }

    [Fact]
    public void GeometryCollisionsExchangeEqualCandidates()
    {
        DirectTargetCandidate[] candidates =
        [
            Candidate(1, left: 10000),
            Candidate(2, top: 1),
            Candidate(3, left: 10000)
        ];
        DirectTargetCandidate[] selected = DirectTargetSelector.Select(
            candidates, true, DirectTargetSelector.KeyboardMode);
        Assert.Equal([3, 2, 1],
            selected.Select(candidate => candidate.Window));
        Assert.Equal((nint)3,
            DirectTargetSelector.SelectOccurrence(selected, 1));
        Assert.Equal((nint)0,
            DirectTargetSelector.SelectOccurrence(selected, 0));
        Assert.Equal((nint)0,
            DirectTargetSelector.SelectOccurrence(selected, 4));
    }

    [Fact]
    public void GeometryKeyUsesNumericOrderAndChecksOverflow()
    {
        DirectTargetCandidate[] selected = DirectTargetSelector.Select(
            [Candidate(1, left: 20000), Candidate(2, top: 1)],
            true, DirectTargetSelector.KeyboardMode);
        Assert.Equal((nint)2, selected[0].Window);
        Assert.Throws<OverflowException>(() => DirectTargetSelector.Select(
            [Candidate(1, top: int.MaxValue)], true,
            DirectTargetSelector.KeyboardMode));
    }

    [Fact]
    public void HiddenRootUsesChildStyleAndKeyboardRejectsDisabled()
    {
        DirectTargetCandidate hidden = Candidate(1) with
        {
            Visible = false,
            Style = 0x10000000
        };
        Assert.Single(DirectTargetSelector.Select([hidden], false,
            DirectTargetSelector.KeyboardMode));
        Assert.Empty(DirectTargetSelector.Select([hidden], true,
            DirectTargetSelector.KeyboardMode));
        DirectTargetCandidate disabled = Candidate(2) with
        {
            Enabled = false
        };
        Assert.Empty(DirectTargetSelector.Select([disabled], true,
            DirectTargetSelector.KeyboardMode));
        Assert.Single(DirectTargetSelector.Select([disabled], true,
            DirectTargetSelector.KeyboardMode, true));
        Assert.Single(DirectTargetSelector.Select([disabled], true,
            DirectTargetSelector.MouseMode));
    }

    [Theory]
    [InlineData("MYEDIT", true)]
    [InlineData("SYSDATETIMEPICK32", true)]
    [InlineData("HM32CLIENT", true)]
    [InlineData("EDTBX", true)]
    [InlineData("_WWS", true)]
    [InlineData("_WWLONG", false)]
    [InlineData("BUTTON", false)]
    public void KeyboardSelectsKnownLabelPatterns(
        string label, bool expected)
    {
        DirectTargetCandidate[] selected = DirectTargetSelector.Select(
            [Candidate(1) with { ClassName = label }], true,
            DirectTargetSelector.KeyboardMode);
        Assert.Equal(expected, selected.Length == 1);
    }

    [Fact]
    public void MouseFiltersParentsAndUsesCursorAndLabelExceptions()
    {
        DirectTargetCandidate parent = Candidate(1) with
        {
            HasChildren = true
        };
        DirectTargetCandidate cursor = parent with
        {
            Window = 2,
            UnderCursor = true
        };
        DirectTargetCandidate browser = parent with
        {
            Window = 3,
            ClassName = "INTERNET EXPLORER_SERVER",
            Visible = false,
            Enabled = false
        };
        DirectTargetCandidate activeX = Candidate(4) with
        {
            ClassName = "ACTIVEX"
        };
        DirectTargetCandidate[] selected = DirectTargetSelector.Select(
            [parent, cursor, browser, activeX], true,
            DirectTargetSelector.MouseMode);
        Assert.DoesNotContain(selected,
            candidate => candidate.Window == 1);
        Assert.Equal(3, selected.Length);
        Assert.Single(DirectTargetSelector.Select([parent], true,
            DirectTargetSelector.MouseMode));
    }

    [Fact]
    public void HitTestScansBackwardsAndIncludesRightBottomEdges()
    {
        DirectTargetCandidate[] candidates = [Candidate(1), Candidate(2)];
        Assert.Equal((nint)2, DirectTargetSelector.HitTest(
            candidates, new Native.Point(20, 20)));
        Assert.Equal((nint)0, DirectTargetSelector.HitTest(
            candidates, new Native.Point(21, 20)));
    }

    private static DirectTargetCandidate Candidate(
        nint window, int left = 0, int top = 0)
    {
        return new DirectTargetCandidate(window, "EDIT", true, true,
            0, false, new Native.Rect
            {
                Left = left,
                Top = top,
                Right = left + 20,
                Bottom = top + 20
            }, false);
    }
}
