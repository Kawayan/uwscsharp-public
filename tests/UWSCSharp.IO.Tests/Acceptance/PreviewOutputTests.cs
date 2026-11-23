using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.IO.Tests;

public sealed class PreviewOutputTests
{
    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(18)]
    [InlineData(528)]
    public async Task RejectsOtherWritableModes(int mode)
    {
        string path = Path.Combine(Path.GetTempPath(),
            "UWSCSharp-preview-mode-" + Guid.NewGuid());
        await using IScriptFileSession session = new ScriptFiles
        {
            NewOutputOnly = true
        }.CreateSession();
        Assert.Equal(Value.Numeric(-1), await session.InvokeAsync(
            "FOPEN", [Value.String(path), Value.Numeric(mode)], default));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ConcurrentSessionsCreateExactlyOneOutput()
    {
        string path = Path.Combine(Path.GetTempPath(),
            "UWSCSharp-create-new-" + Guid.NewGuid());
        try
        {
            var files = new ScriptFiles { NewOutputOnly = true };
            await using IScriptFileSession first = files.CreateSession();
            await using IScriptFileSession second = files.CreateSession();
            Value a = await first.InvokeAsync("FOPEN",
                [Value.String(path), Value.Numeric(16)], default);
            Value b = await second.InvokeAsync("FOPEN",
                [Value.String(path), Value.Numeric(16)], default);
            string textA = new('a', 4000);
            string textB = new('b', 4000);
            await first.InvokeAsync("FPUT", [a, Value.String(textA)], default);
            await second.InvokeAsync("FPUT", [b, Value.String(textB)], default);
            using var start = new ManualResetEventSlim();
            Task<Value> Close(IScriptFileSession session, Value id) => Task.Run(async () =>
            {
                start.Wait();
                return await session.InvokeAsync("FCLOSE",
                    [id, Value.Numeric(1)], default);
            });
            Task<Value> closeA = Close(first, a);
            Task<Value> closeB = Close(second, b);
            start.Set();
            Value[] results = await Task.WhenAll(closeA, closeB);
            Assert.Equal(1, results.Sum(value => value.AsNumber()));
            string saved = await File.ReadAllTextAsync(path);
            Assert.True(saved == textA + "\r\n" || saved == textB + "\r\n");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFileCreatedAfterOpenIsPreservedOnCloseOrDispose(bool explicitClose)
    {
        string path = Path.Combine(Path.GetTempPath(),
            "UWSCSharp-create-race-" + Guid.NewGuid());
        try
        {
            IScriptFileSession session = new ScriptFiles
            {
                NewOutputOnly = true
            }.CreateSession();
            Value id = await session.InvokeAsync("FOPEN",
                [Value.String(path), Value.Numeric(16)], default);
            await session.InvokeAsync("FPUT", [id, Value.String("candidate")], default);
            await File.WriteAllTextAsync(path, "external");
            if (explicitClose)
            {
                Assert.Equal(Value.Numeric(0), await session.InvokeAsync(
                    "FCLOSE", [id, Value.Numeric(1)], default));
                await session.DisposeAsync();
            }
            else
            {
                await Assert.ThrowsAsync<AggregateException>(() => session.DisposeAsync().AsTask());
            }

            Assert.Equal("external", await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
