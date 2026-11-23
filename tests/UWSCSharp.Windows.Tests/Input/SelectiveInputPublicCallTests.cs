using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SelectiveInputPublicCallTests
{
    [Fact]
    public async Task FaultedOwnerRejectsRegistrationAsAScriptErrorUntilExplicitRelease()
    {
        var hooks = new List<Hook>();
        List<string> output = [];
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await new Interpreter(windows: Create(hooks, configure: hook =>
            hook.StopFailures = hooks.Count == 0 ? 2 : 0))
            .RunAsync("""
                LOCKHARDEX(0)
                TRY
                    PRINT "arm"
                    SLEEP(99)
                EXCEPT
                    PRINT "background"
                ENDTRY
                TRY
                    LOCKHARDEX(0)
                EXCEPT
                    PRINT "blocked"
                ENDTRY
                TRY
                    LOCKHARDEX()
                EXCEPT
                    PRINT "diagnostic"
                ENDTRY
                LOCKHARDEX(0)
                LOCKHARDEX()
                PRINT "recovered"
                """, value =>
                {
                    output.Add(value);
                    if (value == "arm")
                    {
                        Hook hook = Assert.Single(hooks);
                        hook.PumpFailure = new System.ComponentModel.Win32Exception(5);
                        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref hook.Stops) >= 2, 3000));
                    }
                    else if (value == "diagnostic")
                    {
                        Assert.False(Assert.Single(hooks).IsRegistered);
                    }
                }, cancel.Token);
        Assert.Equal(["arm", "background", "blocked", "diagnostic", "recovered"], output);
        Assert.Equal(2, hooks.Count);
        Assert.All(hooks, hook => Assert.False(hook.IsRegistered));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RegistrationFailureCanBeHandledByExcept(int kind)
    {
        var hooks = new List<Hook>();
        List<string> output = [];
        Exception failure = kind switch
        {
            0 => new System.ComponentModel.Win32Exception(5),
            1 => new TimeoutException("検証用の待機期限"),
            _ => new AggregateException(new System.ComponentModel.Win32Exception(5),
                new TimeoutException("検証用の待機期限"))
        };
        await new Interpreter(windows: Create(hooks, configure: hook =>
            hook.StartFailure = failure)).RunAsync("""
            TRY
                LOCKHARDEX(0)
            EXCEPT
                PRINT "caught"
            ENDTRY
            PRINT "continued"
            """, output.Add);
        Assert.Equal(["caught", "continued"], output);
        Assert.False(Assert.Single(hooks).IsRegistered);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ReleaseFailureCanBeHandledAndExplicitlyRetried(int failures)
    {
        var hooks = new List<Hook>();
        List<string> output = [];
        await new Interpreter(windows: Create(hooks, configure: hook => hook.StopFailures = failures))
            .RunAsync("""
                LOCKHARDEX(0)
                TRY
                    LOCKHARDEX()
                EXCEPT
                    PRINT "caught"
                ENDTRY
                LOCKHARDEX()
                PRINT "continued"
                """, output.Add);
        Assert.Equal(["caught", "continued"], output);
        Assert.False(Assert.Single(hooks).IsRegistered);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task InternalFailuresAndCancellationAreNotCaughtAsScriptErrors(int kind)
    {
        Exception original = kind switch
        {
            0 => new NullReferenceException("検証用の内部障害"),
            1 => new OutOfMemoryException("検証用の資源不足"),
            2 => new System.ComponentModel.Win32Exception(8),
            3 => new AggregateException(new System.ComponentModel.Win32Exception(5),
                new NullReferenceException("検証用の複合内部障害")),
            4 => new OperationCanceledException("検証用の取消"),
            _ => new InvalidOperationException("検証用の未分類内部障害")
        };
        var hooks = new List<Hook>();
        List<string> output = [];
        Exception observed = await Assert.ThrowsAnyAsync<Exception>(() =>
            new Interpreter(windows: Create(hooks, configure: hook => hook.StartFailure = original))
                .RunAsync("""
                    TRY
                        LOCKHARDEX(0)
                    EXCEPT
                        PRINT "caught"
                    ENDTRY
                    """, output.Add));
        Assert.Same(original, observed);
        Assert.Empty(output);
        Assert.False(Assert.Single(hooks).IsRegistered);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PublicRegistrationIsCancelledAndRecoveredByTheEmergencyRequest(int mode)
    {
        var hooks = new List<Hook>();
        var registry = new SelectiveInputEmergencyRegistry();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter(windows: Create(hooks, registry)).RunAsync($"""
                LOCKHARDEX(0,{mode})
                PRINT "ready"
                SLEEP(99)
                """, _ => registry.RequestAll(), cancel.Token));
        Assert.False(cancel.IsCancellationRequested);
        Assert.False(Assert.Single(hooks).IsRegistered);
    }

    [Fact]
    public async Task PublicInvocationPreservesTheCurrentSettingOnInvalidArguments()
    {
        var hooks = new List<Hook>();
        await using IWindowsAutomationSession session = Create(hooks).CreateSession();
        Value released = await session.InvokeAsync(BuiltinFunctionNames.LockHardEx, [], default);
        Assert.Equal(ValueKind.Empty, released.Kind);
        Assert.Empty(hooks);
        Value held = await session.InvokeAsync(BuiltinFunctionNames.LockHardEx,
            [Value.Numeric(0), Value.Numeric(1)], default);
        Assert.Equal(ValueKind.Empty, held.Kind);
        Hook first = Assert.Single(hooks);
        Assert.True(first.IsRegistered);
        foreach (Value[] arguments in new[]
        {
            new[] { Value.Numeric(0), Value.Numeric(3) },
            [Value.Numeric(int.MaxValue), Value.Numeric(2)],
            [Value.Empty]
        })
        {
            await Assert.ThrowsAnyAsync<Exception>(async () =>
                await session.InvokeAsync(BuiltinFunctionNames.LockHardEx, arguments, default));
            Assert.True(first.IsRegistered);
            Assert.Equal(0, first.Stops);
            Assert.Single(hooks);
        }
        await session.InvokeAsync(BuiltinFunctionNames.LockHardEx, [], default);
        Assert.False(first.IsRegistered);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public async Task InterpreterUsesThePublicEntryAndReleasesAtCompletion(int mode, int devices)
    {
        var hooks = new List<Hook>();
        var observed = new List<bool>();
        await new Interpreter(windows: Create(hooks)).RunAsync($"""
            LOCKHARDEX(0,{mode})
            PRINT "held"
            LOCKHARDEX()
            PRINT "released"
            LOCKHARDEX(0,{mode})
            """, value => observed.Add(hooks.Last().IsRegistered));
        Assert.Equal([true, false], observed);
        Assert.Equal(2, hooks.Count);
        Assert.All(hooks, hook =>
        {
            Assert.Equal((SelectiveInputDevices)devices, hook.Devices);
            Assert.False(hook.IsRegistered);
        });
    }

    [Theory]
    [InlineData("0,3")]
    [InlineData("2147483647,2")]
    public async Task HandledArgumentFailureDoesNotReleaseTheValidSetting(string arguments)
    {
        var hooks = new List<Hook>();
        await new Interpreter(windows: Create(hooks)).RunAsync($"""
            LOCKHARDEX(0,LOCK_MOUSE)
            TRY
                LOCKHARDEX({arguments})
            EXCEPT
                PRINT "caught"
            ENDTRY
            """, _ => Assert.True(Assert.Single(hooks).IsRegistered));
        Assert.False(Assert.Single(hooks).IsRegistered);
    }

    [Fact]
    public async Task RootAndThreadShareThePublicSetting()
    {
        var hooks = new List<Hook>();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await new Interpreter(windows: Create(hooks)).RunAsync("""
            PUBLIC finished=0
            LOCKHARDEX(0,LOCK_KEYBOARD)
            THREAD Change()
            WHILE finished=0
                SLEEP(0.001)
            WEND
            PRINT "child-ended"
            LOCKHARDEX()
            PROCEDURE Change()
                LOCKHARDEX(0,LOCK_MOUSE)
                finished=1
            FEND
            """, _ =>
            {
                Assert.Equal(2, hooks.Count);
                Assert.False(hooks[0].IsRegistered);
                Assert.True(hooks[1].IsRegistered);
            }, cancel.Token);
        Assert.All(hooks, hook => Assert.False(hook.IsRegistered));
    }

    private static WindowsAutomation Create(List<Hook> hooks,
        SelectiveInputEmergencyRegistry? registry = null, Action<Hook>? configure = null) => new(
        _ => throw new InvalidOperationException("入力送信は使用しません。"),
        swappedMouseButtons: false, keyStateBackend: new Keys(),
        selectiveInputFactory: (_, devices) =>
        {
            var hook = new Hook(devices);
            configure?.Invoke(hook);
            hooks.Add(hook);
            return hook;
        })
        {
            EmergencyRegistry = registry ?? new()
        };

    private sealed class Hook(SelectiveInputDevices devices) : ISelectiveInputHookSession
    {
        internal SelectiveInputDevices Devices => devices;
        public bool IsRegistered
        {
            get; private set;
        }
        internal int Stops;
        internal int StopFailures;
        internal Exception? StartFailure;
        internal Exception? PumpFailure;
        public void Start()
        {
            IsRegistered = true;
            if (StartFailure is not null)
            {
                throw StartFailure;
            }
        }
        public void Stop()
        {
            Stops++;
            if (StopFailures-- > 0)
            {
                throw new System.ComponentModel.Win32Exception(5);
            }
            IsRegistered = false;
        }
        public void Suspend()
        {
        }
        public bool Pump()
        {
            Exception? failure = Interlocked.Exchange(ref PumpFailure, null);
            if (failure is not null)
            {
                throw failure;
            }
            return false;
        }
        public void Wait(AutoResetEvent work, int milliseconds) => work.WaitOne(milliseconds);
        public void RefreshTargets()
        {
        }
    }

    private sealed class Keys : IKeyStateBackend
    {
        public short ReadAsync(int key) => 0;
        public short ReadToggle(int key) => 0;
        public bool ReadIme(CancellationToken token) => false;
    }
}
