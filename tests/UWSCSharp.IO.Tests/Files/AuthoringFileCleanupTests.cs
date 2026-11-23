using System.Security.Cryptography;
using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.IO.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class AuthoringFileCleanupTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalScriptClosesItsFileIdBeforeSessionDisposal(
        bool injectFailure)
    {
        string tempRoot = Path.GetFullPath(Path.GetTempPath());
        string directory = Path.GetFullPath(Path.Combine(tempRoot,
            "uwsc-authoring-cleanup-" + Guid.NewGuid().ToString("N")));
        Assert.StartsWith(Path.TrimEndingDirectorySeparator(tempRoot)
            + Path.DirectorySeparatorChar + "uwsc-authoring-cleanup-",
            directory, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(directory);
        try
        {
            string input = Path.Combine(directory, "input.txt");
            await File.WriteAllTextAsync(input, "10\n20\n30\n");
            string fixture = Path.Combine(AppContext.BaseDirectory,
                "authoring", "read-and-sum-lines.uws");
            byte[] script = await File.ReadAllBytesAsync(fixture);
            var files = new ObservedFiles(injectFailure);
            var runner = new Interpreter(files: files);
            var options = new ScriptRunOptions([input]);
            List<string> messages = [];
            Task Run() => runner.RunWithExitCodeAsync(
                ScriptCompiler.CompileFile(fixture),
                new CallbackScriptHost(messages.Add), default, options);
            if (injectFailure)
            {
                Exception error = await Assert.ThrowsAnyAsync<Exception>(Run);
                Assert.Contains("authoring-probe-error", error.ToString());
            }
            else
            {
                await Run();
                Assert.Equal(["合計: 60", "件数: 3"], messages);
            }

            output.WriteLine("script_sha256={0} events={1}",
                Convert.ToHexString(SHA256.HashData(script)),
                string.Join(",", files.Events));
            Assert.Equal(injectFailure
                ? ["open", "line-count", "injected-error", "close:1", "closed-id:0", "dispose"]
                : ["open", "line-count", "row", "row", "row", "close:1", "closed-id:0", "dispose"],
                files.Events);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class ObservedFiles(bool injectFailure) : IScriptFiles
    {
        private readonly bool failOnRead = injectFailure;

        internal List<string> Events { get; } = [];

        public IScriptFileSession CreateSession()
        {
            return new Session(new ScriptFiles().CreateSession(), this);
        }

        private sealed class Session(
            IScriptFileSession inner, ObservedFiles owner) : IScriptFileSession
        {
            private int reads;

            public async ValueTask<Value> InvokeAsync(string name,
                IReadOnlyList<Value> arguments, CancellationToken cancellationToken)
            {
                if (string.Equals(name, "FGET", StringComparison.OrdinalIgnoreCase))
                {
                    reads++;
                    if (owner.failOnRead && reads == 2)
                    {
                        owner.Events.Add("injected-error");
                        throw new InvalidOperationException("authoring-probe-error");
                    }

                    owner.Events.Add(reads == 1 ? "line-count" : "row");
                }

                Value value = await inner.InvokeAsync(name, arguments, cancellationToken);
                if (string.Equals(name, "FOPEN", StringComparison.OrdinalIgnoreCase))
                {
                    owner.Events.Add("open");
                }
                else if (string.Equals(name, "FCLOSE", StringComparison.OrdinalIgnoreCase))
                {
                    owner.Events.Add("close:" + value.Display());
                    Value again = await inner.InvokeAsync("FCLOSE",
                        [arguments[0], Value.Numeric(1)], cancellationToken);
                    owner.Events.Add("closed-id:" + again.Display());
                }

                return value;
            }

            public async ValueTask DisposeAsync()
            {
                owner.Events.Add("dispose");
                await inner.DisposeAsync();
            }
        }
    }
}
