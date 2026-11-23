using System.Runtime.Versioning;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 偽COMの値と参照回収だけを使い、認識器・音声入力を起動しません。
[SupportedOSPlatform("windows")]
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ComSpeechRecognitionTextTests
{
    private sealed class Runtime : IComRuntime
    {
        internal readonly object Result = new();
        internal readonly object Phrase = new();
        internal readonly List<object> Released = [];
        internal readonly List<string> Calls = [];
        internal object? Text = new ComVariantValue(8, "認識😀\0結果");
        internal Exception? InvokeFailure;
        internal string? FailMember;
        internal bool FailRelease;
        internal bool SameObject;
        internal bool Untagged;

        public object Create(string name) => throw new NotSupportedException();

        public object GetActive(string name) => throw new NotSupportedException();

        public object? Invoke(object target, string name, object?[] arguments, bool assign)
        {
            Assert.False(assign);
            Calls.Add(name);
            if (name == FailMember)
            {
                throw InvokeFailure!;
            }

            if (name == "PhraseInfo")
            {
                Assert.Same(Result, target);
                Assert.Empty(arguments);
                object phrase = SameObject ? Result : Phrase;
                return Untagged ? phrase : Acquired(phrase);
            }

            Assert.Equal("GetText", name);
            Assert.Same(SameObject ? Result : Phrase, target);
            Assert.Equal([new ComVariantValue(3, 0), new ComVariantValue(3, -1),
                new ComVariantValue(11, true)], arguments.Cast<ComVariantValue>());
            return Text;
        }

        public void Release(object target)
        {
            Released.Add(target);
            if (FailRelease)
            {
                throw new InvalidOperationException("release");
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadsTextAndReleasesEveryAcquisitionInReverseOrder(bool untagged)
    {
        var runtime = new Runtime { Untagged = untagged };
        Assert.Equal("認識😀\0結果", ComSpeechRecognitionText.Read(
            runtime, Acquired(runtime.Result)));
        Assert.Equal(["PhraseInfo", "GetText"], runtime.Calls);
        Assert.Equal([runtime.Phrase, runtime.Result], runtime.Released);
    }

    [Fact]
    public void DoesNotDeduplicateRepeatedInterfaceAcquisitions()
    {
        var runtime = new Runtime { SameObject = true };
        _ = ComSpeechRecognitionText.Read(runtime, Acquired(runtime.Result));
        Assert.Equal([runtime.Result, runtime.Result], runtime.Released);
    }

    [Theory]
    [InlineData("PhraseInfo", 1)]
    [InlineData("GetText", 2)]
    public void FailedReadStillReleasesAcquiredObjects(string member, int expected)
    {
        var primary = new InvalidOperationException("invoke");
        var runtime = new Runtime { FailMember = member, InvokeFailure = primary };
        Exception actual = Assert.Throws<InvalidOperationException>(() =>
            ComSpeechRecognitionText.Read(runtime, Acquired(runtime.Result)));
        Assert.Same(primary, actual);
        Assert.Equal(expected, runtime.Released.Count);
        Assert.Same(runtime.Result, runtime.Released[^1]);
    }

    [Fact]
    public void RetainsReadAndAllCleanupFailures()
    {
        var primary = new InvalidOperationException("invoke");
        var runtime = new Runtime
        {
            FailMember = "GetText",
            InvokeFailure = primary,
            FailRelease = true
        };
        AggregateException failure = Assert.Throws<AggregateException>(() =>
            ComSpeechRecognitionText.Read(runtime, Acquired(runtime.Result)));
        Assert.Equal(3, failure.InnerExceptions.Count);
        Assert.Same(primary, failure.InnerExceptions[0]);
        Assert.Equal([runtime.Phrase, runtime.Result], runtime.Released);
    }

    [Fact]
    public void CleanupFailureDoesNotReturnSuccessfulText()
    {
        var runtime = new Runtime { FailRelease = true };
        AggregateException failure = Assert.Throws<AggregateException>(() =>
            ComSpeechRecognitionText.Read(runtime, Acquired(runtime.Result)));
        Assert.Equal(2, failure.InnerExceptions.Count);
    }

    [Fact]
    public void InvalidReturnTypeReleasesItsAcquisitionsToo()
    {
        object unexpected = new();
        var runtime = new Runtime { Text = Acquired(unexpected) };
        Assert.Throws<InvalidDataException>(() =>
            ComSpeechRecognitionText.Read(runtime, Acquired(runtime.Result)));
        Assert.Equal([unexpected, runtime.Phrase, runtime.Result], runtime.Released);
    }

    [Fact]
    public void InvalidResultTypeReleasesOwnedReferencesBeforeRejecting()
    {
        var runtime = new Runtime();
        var malformed = new ComVariantValue(8, "invalid")
        {
            Acquisitions = [runtime.Result]
        };
        Assert.Throws<InvalidDataException>(() =>
            ComSpeechRecognitionText.Read(runtime, malformed));
        Assert.Equal([runtime.Result], runtime.Released);
        Assert.Empty(runtime.Calls);
    }

    [Fact]
    public void NullBstrReturnsEmptyText()
    {
        var runtime = new Runtime { Text = new ComVariantValue(8, null) };
        Assert.Equal(string.Empty, ComSpeechRecognitionText.Read(
            runtime, Acquired(runtime.Result)));
        Assert.Equal(2, runtime.Released.Count);
    }

    private static ComVariantValue Acquired(object instance) =>
        new(9, instance)
        {
            Acquisitions = [instance]
        };
}
