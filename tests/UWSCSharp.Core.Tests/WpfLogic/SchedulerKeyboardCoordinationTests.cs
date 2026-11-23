using System.IO.Pipes;
using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

/// <summary>
/// メモリーとローカルパイプだけを使い、画面やキー入力を操作しません。
/// </summary>
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerKeyboardCoordinationTests
{
    private static readonly SchedulerWorkerIdentity Identity = new(Guid.NewGuid(), Guid.NewGuid());

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task KeyboardFramesRoundTrip(int phase)
    {
        var message = new SchedulerWorkerMessage.Keyboard(Identity, Guid.NewGuid(), 135,
            (SchedulerKeyboardPhase)phase);
        using var stream = new MemoryStream();
        await SchedulerWorkerProtocol.WriteAsync(stream, message);
        stream.Position = 0;
        Assert.Equal(message, await SchedulerWorkerProtocol.ReadAsync(stream, Identity));
    }

    [Theory]
    [InlineData(-1, 0, true)]
    [InlineData(256, 0, true)]
    [InlineData(135, -1, true)]
    [InlineData(135, 4, true)]
    [InlineData(135, 0, false)]
    [InlineData(135, 2, false)]
    public async Task InvalidKeyboardFieldsAreRejected(int key, int phase, bool succeeded)
    {
        var message = new SchedulerWorkerMessage.Keyboard(Identity, Guid.NewGuid(), key,
            (SchedulerKeyboardPhase)phase, succeeded);
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SchedulerWorkerProtocol.WriteAsync(stream, message));
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task ParallelRequestsWaitForParentScopeAndRestoration()
    {
        await using Pair pair = await Pair.Create();
        int depth = 0;
        int restored = 0;
        var server = new SchedulerKeyboardServer(pair.Server, Identity, async (key, send, _) =>
        {
            Assert.Equal(135, key);
            Assert.Equal(1, Interlocked.Increment(ref depth));
            try
            {
                await send();
            }
            finally
            {
                Interlocked.Decrement(ref depth);
                Interlocked.Increment(ref restored);
            }
        });
        Task listening = server.RunAsync(pair.Lifetime.Token);
        using var client = new SchedulerKeyboardClient(pair.Client, Identity, pair.Lifetime.Token);
        int sent = 0;
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => client.SendAsync(135, () =>
        {
            Assert.Equal(1, Volatile.Read(ref depth));
            Interlocked.Increment(ref sent);
            return Task.CompletedTask;
        }, pair.Lifetime.Token)));
        Assert.Equal(12, sent);
        Assert.Equal(12, restored);
        Assert.Equal(0, depth);
        pair.Lifetime.Cancel();
        await listening;
        Assert.True(server.Recovered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerReleaseCannotRunBeforeParentRestorationReply(
        bool restoreFailure)
    {
        await using Pair pair = await Pair.Create();
        var restoring = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRestoration = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int down = 0;
        int up = 0;
        var server = new SchedulerKeyboardServer(pair.Server, Identity,
            async (_, send, _) =>
            {
                await send();
                Assert.Equal(1, Volatile.Read(ref down));
                Assert.Equal(0, Volatile.Read(ref up));
                restoring.TrySetResult();
                await allowRestoration.Task.WaitAsync(pair.Lifetime.Token);
                if (restoreFailure)
                {
                    throw new IOException("親の復元失敗");
                }
            });
        Task listening = server.RunAsync(pair.Lifetime.Token);
        using var client = new SchedulerKeyboardClient(
            pair.Client, Identity, pair.Lifetime.Token);
        Task<Exception?> completion = Record.ExceptionAsync(ClickAsync);
        try
        {
            await restoring.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, Volatile.Read(ref down));
            Assert.Equal(0, Volatile.Read(ref up));
            Assert.False(completion.IsCompleted);
            allowRestoration.TrySetResult();
            Exception? error = await completion.WaitAsync(TimeSpan.FromSeconds(5));
            if (restoreFailure)
            {
                Assert.Equal("親の復元失敗",
                    Assert.IsType<InvalidOperationException>(error).Message);
            }
            else
            {
                Assert.Null(error);
            }

            Assert.Equal(1, Volatile.Read(ref up));
            Assert.Equal(!restoreFailure, server.Recovered);
            Assert.Null(server.Failure);
        }
        finally
        {
            allowRestoration.TrySetResult();
            pair.Lifetime.Cancel();
            await listening.WaitAsync(TimeSpan.FromSeconds(5));
            await completion.WaitAsync(TimeSpan.FromSeconds(5));
        }

        async Task ClickAsync()
        {
            try
            {
                await client.SendAsync(135, () =>
                {
                    Interlocked.Increment(ref down);
                    return Task.CompletedTask;
                }, pair.Lifetime.Token);
            }
            finally
            {
                // 復元応答後の通常UPまたは失敗時の入力回収を表します。
                Interlocked.Increment(ref up);
            }
        }
    }

    [Fact]
    public async Task SendFailureStillWaitsForParentRestoration()
    {
        await using Pair pair = await Pair.Create();
        bool restored = false;
        var server = new SchedulerKeyboardServer(pair.Server, Identity, async (_, send, _) =>
        {
            try
            {
                await send();
            }
            finally { restored = true; }
        });
        Task listening = server.RunAsync(pair.Lifetime.Token);
        using var client = new SchedulerKeyboardClient(pair.Client, Identity, pair.Lifetime.Token);
        var failure = new InvalidOperationException("send failed");
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SendAsync(135, () => Task.FromException(failure), pair.Lifetime.Token)));
        Assert.True(restored);
        Assert.True(server.Recovered);
        pair.Lifetime.Cancel();
        await listening;
    }

    [Fact]
    public async Task RestorationFailureIsNotAcknowledgedAsRecovered()
    {
        await using Pair pair = await Pair.Create();
        var server = new SchedulerKeyboardServer(pair.Server, Identity, async (_, send, _) =>
        {
            await send();
            throw new IOException("restore failed");
        });
        Task listening = server.RunAsync(pair.Lifetime.Token);
        using var client = new SchedulerKeyboardClient(pair.Client, Identity, pair.Lifetime.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(135,
            () => Task.CompletedTask, pair.Lifetime.Token));
        Assert.False(server.Recovered);
        pair.Lifetime.Cancel();
        await listening;
    }

    [Fact]
    public async Task MissingGuardRejectsBeforeSendingInput()
    {
        await using Pair pair = await Pair.Create();
        var server = new SchedulerKeyboardServer(pair.Server, Identity, null);
        Task listening = server.RunAsync(pair.Lifetime.Token);
        using var client = new SchedulerKeyboardClient(pair.Client, Identity, pair.Lifetime.Token);
        bool sent = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(135, () =>
        {
            sent = true;
            return Task.CompletedTask;
        }, pair.Lifetime.Token));
        Assert.False(sent);
        Assert.True(server.Recovered);
        pair.Lifetime.Cancel();
        await listening;
    }

    [Fact]
    public async Task CancelledWaitDoesNotSendAfterAParentGrant()
    {
        await using Pair pair = await Pair.Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new SchedulerKeyboardServer(pair.Server, Identity, async (_, _, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        Task listening = server.RunAsync(pair.Lifetime.Token);
        using var client = new SchedulerKeyboardClient(pair.Client, Identity, pair.Lifetime.Token);
        using var cancellation = new CancellationTokenSource();
        bool sent = false;
        Task sending = client.SendAsync(135, () =>
        {
            sent = true;
            return Task.CompletedTask;
        }, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        Assert.False(sent);
        pair.Lifetime.Cancel();
        await listening;
    }

    [Fact]
    public async Task ForeignTransactionNeverAuthorizesInput()
    {
        await using Pair pair = await Pair.Create();
        Task responder = Respond();
        using var client = new SchedulerKeyboardClient(pair.Client, Identity, pair.Lifetime.Token);
        bool sent = false;
        await Assert.ThrowsAsync<InvalidDataException>(() => client.SendAsync(135, () =>
        {
            sent = true;
            return Task.CompletedTask;
        }, pair.Lifetime.Token));
        Assert.False(sent);
        await responder;
        Assert.Equal(0, await pair.Server.ReadAsync(new byte[1], pair.Lifetime.Token));

        async Task Respond()
        {
            SchedulerWorkerMessage.Keyboard request = Assert.IsType<SchedulerWorkerMessage.Keyboard>(
                await SchedulerWorkerProtocol.ReadAsync(pair.Server, Identity));
            await SchedulerWorkerProtocol.WriteAsync(pair.Server,
                request with
                {
                    Transaction = Guid.NewGuid(),
                    Phase = SchedulerKeyboardPhase.Granted
                });
        }
    }

    [Fact]
    public async Task PartialFrameIsNotAnIdleDisconnect()
    {
        await using Pair pair = await Pair.Create();
        var server = new SchedulerKeyboardServer(pair.Server, Identity, null);
        Task listening = server.RunAsync(pair.Lifetime.Token);
        await pair.Client.WriteAsync(new byte[] { 8 });
        pair.Client.Dispose();
        await listening.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(server.Recovered);
        Assert.IsAssignableFrom<EndOfStreamException>(server.Failure);
    }

    private sealed class Pair : IAsyncDisposable
    {
        private Pair(NamedPipeServerStream server, NamedPipeClientStream client)
        {
            Server = server;
            Client = client;
        }

        internal NamedPipeServerStream Server
        {
            get;
        }

        internal NamedPipeClientStream Client
        {
            get;
        }

        internal CancellationTokenSource Lifetime { get; } = new(TimeSpan.FromSeconds(15));

        internal static async Task<Pair> Create()
        {
            string name = "uwsc-keyboard-" + Guid.NewGuid().ToString("N");
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync());
            return new Pair(server, client);
        }

        public ValueTask DisposeAsync()
        {
            Lifetime.Cancel();
            Client.Dispose();
            Server.Dispose();
            Lifetime.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
