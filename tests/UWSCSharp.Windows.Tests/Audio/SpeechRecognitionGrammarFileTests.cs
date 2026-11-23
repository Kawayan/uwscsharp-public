using System.Text;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 文法文字列と一時ファイルだけを確認し、音声エンジン・画面は起動しません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SpeechRecognitionGrammarFileTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("\u3000\u3000")]
    [InlineData("\u0000\u001f\u3000 \t")]
    public void RemovesBlankEntriesWithoutSwitchingToDictation(string blank)
    {
        Assert.Equal("<GRAMMAR>\r\n<RULE NAME=\"request\" TOPLEVEL=\"ACTIVE\">\r\n"
            + "<L PROPNAME=\"participants\">\r\n</L>\r\n</RULE>\r\n</GRAMMAR>\r\n",
            SpeechRecognitionGrammarFile.Render([blank]));
    }

    [Fact]
    public void PreservesOrderDuplicatesWhitespaceAndRawMarkup()
    {
        string[] words = [" 前後　", "重複", "重複", "<x>&\"", "\u00a0", "\u2003", "中\0間"];
        string text = SpeechRecognitionGrammarFile.Render(words);
        Assert.Equal("<GRAMMAR>\r\n<RULE NAME=\"request\" TOPLEVEL=\"ACTIVE\">\r\n"
            + "<L PROPNAME=\"participants\">\r\n"
            + string.Concat(words.Select(word => "<P>" + word + "</P>\r\n"))
            + "</L>\r\n</RULE>\r\n</GRAMMAR>\r\n", text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeepsBomUtf8FileUntilLoaderCompletesAndThenDeletes(bool fail)
    {
        string? savedPath = null;
        var failure = new InvalidOperationException("文法読込み失敗");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task operation = SpeechRecognitionGrammarFile.UseAsync(["日本語😀"], async (path, token) =>
        {
            savedPath = path;
            byte[] bytes = await File.ReadAllBytesAsync(path, token);
            Assert.Equal([0xef, 0xbb, 0xbf], bytes.Take(3).Select(value => (int)value));
            Assert.Equal(SpeechRecognitionGrammarFile.Render(["日本語😀"]),
                Encoding.UTF8.GetString(bytes.AsSpan(3)));
            entered.TrySetResult();
            await release.Task;
            if (fail)
            {
                throw failure;
            }
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(File.Exists(savedPath));
        }
        finally
        {
            release.TrySetResult();
        }

        if (fail)
        {
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => operation));
        }
        else
        {
            await operation;
        }

        Assert.False(File.Exists(savedPath));
    }

    [Fact]
    public async Task ConcurrentLoadsOwnDifferentFiles()
    {
        string? firstPath = null;
        string? secondPath = null;
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task first = SpeechRecognitionGrammarFile.UseAsync(["一"], async (path, _) =>
        {
            firstPath = path;
            firstEntered.TrySetResult();
            await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEqual(firstPath, secondPath);
        });
        Task second = SpeechRecognitionGrammarFile.UseAsync(["二"], async (path, _) =>
        {
            secondPath = path;
            secondEntered.TrySetResult();
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEqual(firstPath, secondPath);
        });
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(File.Exists(firstPath));
        Assert.False(File.Exists(secondPath));
    }

    [Fact]
    public async Task CancellationAfterLoadingStartsRemovesOwnedFile()
    {
        using var cancellation = new CancellationTokenSource();
        string? savedPath = null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SpeechRecognitionGrammarFile.UseAsync(["語"], (path, token) =>
            {
                savedPath = path;
                cancellation.Cancel();
                return Task.CompletedTask;
            }, cancellation.Token));
        Assert.NotNull(savedPath);
        Assert.False(File.Exists(savedPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteFailurePreservesLoaderFailureAndOwnedPath(bool fail)
    {
        FileStream? held = null;
        string? savedPath = null;
        var failure = new InvalidOperationException("読込み失敗");
        Task operation = SpeechRecognitionGrammarFile.UseAsync(["語"], (path, _) =>
        {
            savedPath = path;
            held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fail)
            {
                throw failure;
            }

            return Task.CompletedTask;
        });
        try
        {
            if (fail)
            {
                AggregateException aggregate = await Assert.ThrowsAsync<AggregateException>(() => operation);
                Assert.Same(failure, aggregate.InnerExceptions[0]);
                Assert.IsType<IOException>(aggregate.InnerExceptions[1]);
            }
            else
            {
                await Assert.ThrowsAsync<IOException>(() => operation);
            }

            Assert.True(File.Exists(savedPath));
        }
        finally
        {
            held?.Dispose();
            if (savedPath is not null)
            {
                File.Delete(savedPath);
            }
        }
    }

    [Fact]
    public async Task InvalidInputOrPreCancellationDoesNotCallLoader()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        int calls = 0;
        Task Load(string _, CancellationToken token)
        {
            calls++;
            return Task.CompletedTask;
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SpeechRecognitionGrammarFile.UseAsync(["語"], Load, cancellation.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            SpeechRecognitionGrammarFile.UseAsync(null!, Load));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            SpeechRecognitionGrammarFile.UseAsync([null!], Load));
        Assert.Equal(0, calls);
    }
}
