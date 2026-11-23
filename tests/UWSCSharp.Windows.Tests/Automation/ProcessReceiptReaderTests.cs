using System.Text.Json;
using Xunit;

namespace UWSCSharp.Windows.Tests;

/// <summary>
/// 自分で作った一時ファイルと偽の読取りだけを使い、画面・入力・プロセスを使用しません。
/// </summary>
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ProcessReceiptReaderTests
{
    [Fact]
    public async Task CompatiblePublisherHandleDoesNotPreventReading()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "123");
            using var publisher = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete);
            await Assert.ThrowsAsync<IOException>(() => File.ReadAllTextAsync(path));
            Assert.Equal(123, await ProcessReceiptReader.ReadPidAsync(path, CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExclusiveHandleIsRetriedUntilReleased()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "456");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var publisher = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Task<int> reading = ProcessReceiptReader.ReadPidAsync(path, timeout.Token);
            Assert.False(reading.IsCompleted);
            publisher.Dispose();
            Assert.Equal(456, await reading);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(unchecked((int)0x80070020))]
    [InlineData(unchecked((int)0x80070021))]
    public async Task OnlyKnownTransientFailureIsRetried(int hresult)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        int attempts = 0;
        int pid = await ProcessReceiptReader.ReadPidAsync("same-marker", timeout.Token, (path, token) =>
        {
            Assert.Equal("same-marker", path);
            Assert.Equal(timeout.Token, token);
            return ++attempts == 1
                ? Task.FromException<string>(new IOException("偽の共有競合", hresult))
                : Task.FromResult("789");
        });
        Assert.Equal(789, pid);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task OtherIOExceptionIsNotRetried()
    {
        var failure = new IOException("偽の恒久的な読取り失敗");
        int attempts = 0;
        IOException actual = await Assert.ThrowsAsync<IOException>(() =>
            ProcessReceiptReader.ReadPidAsync("marker", CancellationToken.None, (_, _) =>
            {
                attempts++;
                return Task.FromException<string>(failure);
            }));
        Assert.Same(failure, actual);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task CancellationDuringRetryPreservesToken()
    {
        using var cancellation = new CancellationTokenSource();
        OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProcessReceiptReader.ReadPidAsync("marker", cancellation.Token, (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromException<string>(new IOException("偽の共有競合", unchecked((int)0x80070020)));
            }));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
    }

    [Fact]
    public async Task MalformedPidIsNotTreatedAsTransientSharingFailure()
    {
        int attempts = 0;
        await Assert.ThrowsAsync<FormatException>(() => ProcessReceiptReader.ReadPidAsync(
            "marker", CancellationToken.None, (_, _) =>
            {
                attempts++;
                return Task.FromResult("not-a-pid");
            }));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task JsonNotificationWaitsForExclusiveHandleRelease()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "{\"pid\":456}");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var publisher = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Task<JsonDocument> reading = ProcessReceiptReader.ReadJsonAsync(path, timeout.Token);
            Assert.False(reading.IsCompleted);
            publisher.Dispose();
            using JsonDocument result = await reading;
            Assert.Equal(456, result.RootElement.GetProperty("pid").GetInt32());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MalformedJsonIsNotRetried()
    {
        int attempts = 0;
        await Assert.ThrowsAnyAsync<JsonException>(() => ProcessReceiptReader.ReadJsonAsync(
            "marker", CancellationToken.None, (_, _) =>
            {
                attempts++;
                return Task.FromResult("{invalid-json");
            }));
        Assert.Equal(1, attempts);
    }
}
