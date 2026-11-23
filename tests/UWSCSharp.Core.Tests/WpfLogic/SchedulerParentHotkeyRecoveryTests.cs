using System.IO.Pipes;
using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

/// <summary>
/// 実ローカルパイプと代替登録先で部品を接続し、画面・実登録・キー入力を使いません。
/// </summary>
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerParentHotkeyRecoveryTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task RealRegistrationLogicPreservesSendFailureAndRecoveryState(
        int sendFailure, bool restoreFailure)
    {
        var identity = new SchedulerWorkerIdentity(Guid.NewGuid(), Guid.NewGuid());
        string name = "uwsc-parent-recovery-" + Guid.NewGuid().ToString("N");
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var serverPipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var clientPipe = new NamedPipeClientStream(".", name,
            PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(serverPipe.WaitForConnectionAsync(lifetime.Token),
            clientPipe.ConnectAsync(lifetime.Token));
        var backend = new RegistrationBackend();
        using var registration = new HotkeyRegistration(backend);
        var settings = new HotkeySettings(Modifiers: 0, PlayKey: 122);
        Assert.True(registration.Apply(settings));
        int scopes = 0;
        int sent = 0;
        Exception? guardFailure = null;
        var server = new SchedulerKeyboardServer(serverPipe, identity, async (key, send, _) =>
        {
            Interlocked.Increment(ref scopes);
            try
            {
                await registration.SendKeyboardInputAsync(key, send);
            }
            catch (Exception error)
            {
                guardFailure = error;
                throw;
            }
        });
        Task listening = server.RunAsync(lifetime.Token);
        using var client = new SchedulerKeyboardClient(clientPipe, identity, lifetime.Token);
        using var caller = new CancellationTokenSource();
        Exception? inputFailure = null;
        try
        {
            Exception? actual = await Record.ExceptionAsync(() => client.SendAsync(122, () =>
            {
                Assert.False(registration.IsRegistered(4));
                Assert.Equal(3, backend.Keys.Count);
                Interlocked.Increment(ref sent);
                backend.FailRestoration = restoreFailure;
                if (sendFailure == 1)
                {
                    inputFailure = new InvalidOperationException("代替入力の失敗");
                }
                else if (sendFailure == 2)
                {
                    caller.Cancel();
                    inputFailure = new OperationCanceledException(caller.Token);
                }
                return inputFailure is null ? Task.CompletedTask
                    : Task.FromException(inputFailure);
            }, caller.Token));
            Assert.Equal(1, sent);
            Assert.Equal(1, scopes);
            Assert.Equal(2, backend.PlayRegistrationAttempts);
            Assert.Equal(1, backend.PlayUnregistrations);
            Assert.Equal(!restoreFailure, server.Recovered);
            Assert.Equal(!restoreFailure, registration.IsRegistered(4));
            Assert.Equal(restoreFailure ? 3 : 4, backend.Keys.Count);
            Assert.Null(server.Failure);
            if (restoreFailure)
            {
                InvalidOperationException restore =
                    Assert.IsType<InvalidOperationException>(guardFailure);
                Assert.Contains("ホットキー再登録", restore.Message);
                if (inputFailure is null)
                {
                    Assert.Equal(restore.Message,
                        Assert.IsType<InvalidOperationException>(actual).Message);
                }
                else
                {
                    AggregateException combined = Assert.IsType<AggregateException>(actual);
                    Assert.Equal(2, combined.InnerExceptions.Count);
                    Assert.Same(inputFailure, combined.InnerExceptions[0]);
                    Assert.Equal(restore.Message,
                        Assert.IsType<InvalidOperationException>(combined.InnerExceptions[1]).Message);
                }
                backend.FailRestoration = false;
                Assert.True(registration.Apply(settings));
                Assert.Equal(4, backend.Keys.Count);
                // 設定の再適用だけでは、通信側が保持する回収未確認を解除しません。
                await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                    client.SendAsync(122, () =>
                    {
                        Interlocked.Increment(ref sent);
                        return Task.CompletedTask;
                    }, CancellationToken.None));
                await listening.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(server.Recovered);
                Assert.Equal(1, scopes);
                Assert.Equal(1, sent);
            }
            else
            {
                Assert.Null(guardFailure);
                Assert.Same(inputFailure, actual);
                await client.SendAsync(122, () =>
                {
                    Assert.False(registration.IsRegistered(4));
                    Interlocked.Increment(ref sent);
                    return Task.CompletedTask;
                }, CancellationToken.None);
                Assert.Equal(2, scopes);
                Assert.Equal(2, sent);
                Assert.True(server.Recovered);
                Assert.True(registration.IsRegistered(4));
            }
        }
        finally
        {
            lifetime.Cancel();
            await listening.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Null(server.Failure);
    }

    private sealed class RegistrationBackend : IHotkeyBackend
    {
        internal Dictionary<int, int> Keys { get; } = [];

        internal bool FailRestoration
        {
            get;
            set;
        }

        internal int PlayRegistrationAttempts
        {
            get;
            private set;
        }

        internal int PlayUnregistrations
        {
            get;
            private set;
        }

        public bool Register(int id, uint modifiers, int key)
        {
            if (id == 4)
            {
                PlayRegistrationAttempts++;
                if (FailRestoration)
                {
                    return false;
                }
            }
            Keys.Add(id, key);
            return true;
        }

        public bool Unregister(int id)
        {
            if (id == 4)
            {
                PlayUnregistrations++;
            }
            return Keys.Remove(id);
        }
    }
}
