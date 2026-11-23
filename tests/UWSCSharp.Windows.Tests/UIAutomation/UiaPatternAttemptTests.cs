using System.Runtime.InteropServices;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class UiaPatternAttemptTests
{
    [Fact]
    public void MarshaledNoInterfaceIsUnsupportedOnlyDuringAcquisition()
    {
        Exception failure = Marshal.GetExceptionForHR(unchecked((int)0x80004002))!;
        Assert.IsType<InvalidCastException>(failure);
        Assert.Equal((false, false), UiaPatternAttempt.Run<object>(
            () => throw failure, _ => { }, _ => true, default));
        Assert.Same(failure, Assert.Throws<InvalidCastException>(() =>
            UiaPatternAttempt.Run<object>(() => new object(), _ => { },
                _ => throw failure, default)));
    }

    [Theory]
    [InlineData(unchecked((int)0x80040204))]
    [InlineData(unchecked((int)0x80004002))]
    public void UnsupportedAcquisitionDoesNotStartAnAction(int code)
    {
        (bool Supported, bool Success) result = UiaPatternAttempt.Run<object>(
            () => throw new COMException("未提供", code),
            _ => Assert.Fail("参照を取得していません。"),
            _ => throw new InvalidOperationException("操作を開始できません。"), default);
        Assert.Equal((false, false), result);
    }

    [Fact]
    public void MissingPatternDoesNotStartAnAction()
    {
        Assert.Equal((false, false), UiaPatternAttempt.Run<object>(
            () => null, _ => Assert.Fail("参照を取得していません。"),
            _ => throw new InvalidOperationException("操作を開始できません。"), default));
    }

    [Fact]
    public void UnexpectedInterfaceIsOwnedWithoutStartingAnAction()
    {
        object pattern = new();
        object? owned = null;
        Assert.Equal((false, false), UiaPatternAttempt.Run<IDisposable>(
            () => pattern, value => owned = value,
            _ => throw new InvalidOperationException("操作を開始できません。"), default));
        Assert.Same(pattern, owned);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SupportedActionRetainsItsResult(bool success)
    {
        object pattern = new();
        object? owned = null;
        int actions = 0;
        Assert.Equal((true, success), UiaPatternAttempt.Run<object>(
            () => pattern, value => owned = value, value =>
            {
                Assert.Same(pattern, value);
                Assert.Same(pattern, owned);
                actions++;
                return success;
            }, default));
        Assert.Equal(1, actions);
    }

    [Theory]
    [InlineData(unchecked((int)0x80040204))]
    [InlineData(unchecked((int)0x80004002))]
    [InlineData(unchecked((int)0x80004005))]
    public void ActionFailureIsNotReclassifiedAsMissingPattern(int code)
    {
        var failure = new COMException("操作開始後の失敗", code);
        Assert.Same(failure, Assert.Throws<COMException>(() =>
            UiaPatternAttempt.Run<object>(() => new object(), _ => { },
                _ => throw failure, default)));
    }

    [Fact]
    public void AcquisitionFailureIsNotReclassifiedAsMissingPattern()
    {
        var failure = new COMException("取得の失敗", unchecked((int)0x80004005));
        Assert.Same(failure, Assert.Throws<COMException>(() =>
            UiaPatternAttempt.Run<object>(() => throw failure, _ => { },
                _ => true, default)));
    }

    [Fact]
    public void CancellationBeforeAcquisitionDoesNotAcquire()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            UiaPatternAttempt.Run<object>(
                () => throw new InvalidOperationException("取消済みです。"),
                _ => { }, _ => true, cancellation.Token));
    }

    [Fact]
    public void CancellationAfterAcquisitionRetainsOwnershipWithoutAction()
    {
        using var cancellation = new CancellationTokenSource();
        object pattern = new();
        object? owned = null;
        Assert.Throws<OperationCanceledException>(() =>
            UiaPatternAttempt.Run<object>(() =>
            {
                cancellation.Cancel();
                return pattern;
            }, value => owned = value,
                _ => throw new InvalidOperationException("取消済みです。"),
                cancellation.Token));
        Assert.Same(pattern, owned);
    }
}
