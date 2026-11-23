using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.IO.Tests;

public sealed class FinallyDiagnosticTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void CancellationReportingRecognizesBothAdditionalErrorKinds(
        bool finallyFailure, bool cleanupFailure, bool expected)
    {
        var primary = new OperationCanceledException("primary");
        if (finallyFailure)
        {
            primary.Data["UWSCSharp.FinallyErrors"] =
                new AggregateException(new InvalidOperationException("finally"));
        }

        if (cleanupFailure)
        {
            primary.Data["UWSCSharp.CleanupErrors"] =
                new AggregateException(new IOException("cleanup"));
        }

        Assert.Equal(expected, ScriptLogHost.HasAdditionalDiagnostics(primary));
    }

    [Fact]
    public void FinallyFailureKeepsSourceLocationInStopDiagnostic()
    {
        var primary = new OperationCanceledException("primary");
        var failure = new ScriptException(7, "後処理失敗");
        failure.Data["UWSCSharp.SourcePath"] = "child.uws";
        var errors = new AggregateException(failure);
        primary.Data["UWSCSharp.FinallyErrors"] = errors;
        string diagnostic = ScriptLogHost.FormatDiagnostic(primary, "停止");
        Assert.StartsWith("停止" + Environment.NewLine, diagnostic);
        Assert.Contains("FINALLY:", diagnostic);
        Assert.Contains("child.uws: 7行:", diagnostic);
        Assert.Contains("後処理失敗", diagnostic);
        Assert.Same(errors, primary.Data["UWSCSharp.FinallyErrors"]);
        Assert.Same(failure, errors.InnerExceptions[0]);
    }

    [Fact]
    public void FinallyAndSessionFailuresAreBothVisibleInOriginalOrder()
    {
        var primary = new OperationCanceledException("primary");
        primary.Data["UWSCSharp.FinallyErrors"] = new AggregateException(
            new InvalidOperationException("inner failure"),
            new InvalidOperationException("outer failure"));
        primary.Data["UWSCSharp.CleanupErrors"] = new AggregateException(
            new IOException("session failure"));
        string diagnostic = ScriptLogHost.FormatDiagnostic(primary, "停止");
        Assert.Contains("FINALLY: inner failure" + Environment.NewLine
            + "FINALLY: outer failure", diagnostic);
        Assert.Contains("終了処理:", diagnostic);
        Assert.Contains("session failure", diagnostic);
        Assert.True(diagnostic.IndexOf("FINALLY:", StringComparison.Ordinal)
            < diagnostic.IndexOf("終了処理:", StringComparison.Ordinal));
    }
}
