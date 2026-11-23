using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class NativeDllRelayClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidReplyFailsPendingCallsAndClosesConnection(
        bool unexpectedId)
    {
        using var deadline =
            new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CancellationToken token = deadline.Token;
        string name = $"uwsc-dll-relay-test-{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(name,
            PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var peer = new NamedPipeClientStream(".", name,
            PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(token),
            peer.ConnectAsync(token));

        var start = new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("exit");
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("検証用の子プロセスを起動できません。");
        using var client = new NativeDllRelayClient(server, process);
        using var requests = new StreamReader(peer, leaveOpen: true);
        var request = new NativeDllRelayRequest("", null);
        Task<NativeDllRelayReply> first = Task.Run(
            () => client.Invoke(request, token), token);
        Task<NativeDllRelayReply> second = Task.Run(
            () => client.Invoke(request, token), token);
        Assert.NotNull(await requests.ReadLineAsync(token));
        Assert.NotNull(await requests.ReadLineAsync(token));

        string reply = unexpectedId
            ? JsonSerializer.Serialize(new NativeDllRelayReply(null, Id: -1))
            : "null";
        await peer.WriteAsync(Encoding.UTF8.GetBytes(reply + "\n"), token);

        Task<InvalidOperationException> firstFailure =
            Assert.ThrowsAsync<InvalidOperationException>(() => first);
        Task<InvalidOperationException> secondFailure =
            Assert.ThrowsAsync<InvalidOperationException>(() => second);
        InvalidOperationException[] failures =
            await Task.WhenAll(firstFailure, secondFailure).WaitAsync(token);
        Assert.All(failures,
            failure => Assert.IsType<InvalidDataException>(failure.InnerException));
        InvalidOperationException retry =
            Assert.Throws<InvalidOperationException>(
                () => client.Invoke(request, token));
        Assert.Same(failures[0].InnerException, retry.InnerException);
    }
}
