using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SessionCleanupTests
{
    private sealed class Services : IWindowsAutomation, IScriptFiles
    {
        internal List<string> Events { get; } = [];

        internal Exception? FileCreationFailure
        {
            get; init;
        }

        internal Exception? CallFailure
        {
            get; init;
        }

        internal Action? OnFileCreation
        {
            get; init;
        }

        internal Exception? FileCleanupFailure
        {
            get; init;
        }

        internal Exception? WindowCleanupFailure
        {
            get; init;
        }

        IWindowsAutomationSession IWindowsAutomation.CreateSession()
        {
            Events.Add("create-window");
            return new Session(this, "window", WindowCleanupFailure);
        }

        IScriptFileSession IScriptFiles.CreateSession()
        {
            Events.Add("create-file");
            OnFileCreation?.Invoke();
            if (FileCreationFailure is not null)
            {
                throw FileCreationFailure;
            }

            return new Session(this, "file", FileCleanupFailure);
        }

        private sealed class Session(Services owner, string name, Exception? failure)
            : IWindowsAutomationSession, IScriptFileSession
        {
            public ValueTask<Value> InvokeAsync(string operation,
                IReadOnlyList<Value> arguments, CancellationToken token)
            {
                if (owner.CallFailure is not null)
                {
                    throw owner.CallFailure;
                }

                return ValueTask.FromResult(Value.Numeric(1));
            }

            public async ValueTask DisposeAsync()
            {
                await Task.Yield();
                owner.Events.Add("dispose-" + name);
                if (failure is not null)
                {
                    throw failure;
                }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrimaryAdapterFailureSurvivesBothCleanupFailures(bool cancelled)
    {
        using var cancellation = new CancellationTokenSource();
        Exception primary = cancelled ? new OperationCanceledException(cancellation.Token)
            : new IOException("primary");
        var fileFailure = new IOException("file cleanup");
        var windowFailure = new IOException("window cleanup");
        var services = new Services
        {
            CallFailure = primary,
            FileCleanupFailure = fileFailure,
            WindowCleanupFailure = windowFailure
        };
        Exception actual = await Record.ExceptionAsync(() => new Interpreter(
            windows: services, files: services).RunAsync("CHKBTN(1, \"target\")", _ => { }));
        Assert.Same(primary, actual);
        AggregateException cleanup = Assert.IsType<AggregateException>(actual.Data["UWSCSharp.CleanupErrors"]);
        Assert.Equal([fileFailure, windowFailure], cleanup.InnerExceptions);
        Assert.Equal(["create-window", "create-file", "dispose-file", "dispose-window"], services.Events);
    }

    [Theory]
    [InlineData("")]
    [InlineData("EXIT")]
    [InlineData("EXITEXIT")]
    [InlineData("EXITEXIT 7")]
    public async Task BothCleanupErrorsRemainObservableAfterNormalExit(string script)
    {
        var fileFailure = new IOException("file cleanup");
        var windowFailure = new IOException("window cleanup");
        var services = new Services
        {
            FileCleanupFailure = fileFailure,
            WindowCleanupFailure = windowFailure
        };
        AggregateException error = await Assert.ThrowsAsync<AggregateException>(() =>
            new Interpreter(windows: services, files: services).RunAsync(script, _ => { }));
        Assert.Equal([fileFailure, windowFailure], error.InnerExceptions);
        Assert.Equal(["create-window", "create-file", "dispose-file", "dispose-window"], services.Events);
    }

    [Fact]
    public async Task ScriptLineRemainsVisibleWhenCleanupAlsoFails()
    {
        var cleanup = new IOException("cleanup");
        var services = new Services { FileCleanupFailure = cleanup };
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() => new Interpreter(
            windows: services, files: services).RunAsync("// heading\nPRINT 1/0", _ => { }));
        Assert.Equal(2, error.Line);
        Assert.Equal([cleanup], Assert.IsType<AggregateException>(
            error.Data["UWSCSharp.CleanupErrors"]).InnerExceptions);
        Assert.Equal("dispose-window", services.Events[^1]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SingleCleanupFailureIsRethrownUnwrapped(bool file)
    {
        var failure = new IOException("cleanup");
        var services = new Services
        {
            FileCleanupFailure = file ? failure : null,
            WindowCleanupFailure = file ? null : failure
        };
        IOException error = await Assert.ThrowsAsync<IOException>(() => new Interpreter(
            windows: services, files: services).RunAsync("", _ => { }));
        Assert.Same(failure, error);
        Assert.Equal("dispose-window", services.Events[^1]);
    }

    [Fact]
    public async Task ConstantInitializationFailureReleasesBothSessions()
    {
        var cleanup = new IOException("cleanup");
        var services = new Services { FileCleanupFailure = cleanup };
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: services, files: services).RunAsync(
                "// heading\nCONST c=ABS(\"bad\")\nPRINT c", _ => { }));
        Assert.Equal(2, error.Line);
        Assert.Equal([cleanup], Assert.IsType<AggregateException>(
            error.Data["UWSCSharp.CleanupErrors"]).InnerExceptions);
        Assert.Equal(["create-window", "create-file", "dispose-file", "dispose-window"],
            services.Events);
    }

    [Fact]
    public async Task CancellationBeforeConstantInitializationReleasesBothSessions()
    {
        using var cancellation = new CancellationTokenSource();
        var services = new Services { OnFileCreation = cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter(windows: services, files: services).RunAsync(
                "CONST c=ABS(-7)\nPRINT c", _ => { }, cancellation.Token));
        Assert.Equal(["create-window", "create-file", "dispose-file", "dispose-window"],
            services.Events);
    }

    [Fact]
    public async Task CreationFailureStillReleasesWindowWithoutHidingPrimaryError()
    {
        var primary = new IOException("create file");
        var cleanup = new IOException("window cleanup");
        var services = new Services
        {
            FileCreationFailure = primary,
            WindowCleanupFailure = cleanup
        };
        IOException actual = await Assert.ThrowsAsync<IOException>(() => new Interpreter(
            windows: services, files: services).RunAsync("", _ => { }));
        Assert.Same(primary, actual);
        Assert.Equal([cleanup], Assert.IsType<AggregateException>(
            actual.Data["UWSCSharp.CleanupErrors"]).InnerExceptions);
        Assert.Equal(["create-window", "create-file", "dispose-window"], services.Events);
    }
}
