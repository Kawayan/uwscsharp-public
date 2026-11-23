using System.ComponentModel;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SelectiveInputHookRegistrationTests
{
    [Fact]
    public void EmergencyMonitorRemainsUntilTheMouseHookIsRecovered()
    {
        var backend = new Backend();
        SelectiveInputHookRegistration registration = Create(backend);
        bool initialized = false;
        registration.Register(SelectiveInputDevices.All, () =>
        {
            Assert.True(registration.HasKeyboard);
            Assert.DoesNotContain("install:14", backend.Calls);
            initialized = true;
        });
        Assert.True(initialized);
        backend.FailedRemoves.Add(14);
        Assert.Throws<AggregateException>(() => registration.Unregister(true));
        Assert.True(registration.HasKeyboard);
        Assert.DoesNotContain("remove:13", backend.Calls);
        backend.FailedRemoves.Clear();
        registration.Unregister(true);
        Assert.False(registration.IsCallbackRooted);
    }

    [Theory]
    [InlineData(1, "install:13,remove:13")]
    [InlineData(2, "install:14,remove:14")]
    [InlineData(3, "install:13,install:14,remove:14,remove:13")]
    public void SelectedHooksAreReleasedInReverseOrder(int devices, string expected)
    {
        var backend = new Backend();
        SelectiveInputHookRegistration registration = Create(backend);
        registration.Register((SelectiveInputDevices)devices);
        Assert.True(registration.IsCallbackRooted);
        registration.Unregister();
        registration.Unregister();
        Assert.False(registration.IsCallbackRooted);
        Assert.Equal(expected, string.Join(',', backend.Calls));
    }

    [Theory]
    [InlineData(13, "install:13")]
    [InlineData(14, "install:13,install:14,remove:13")]
    public void FailedInstallRollsBackEarlierRegistration(int type, string expected)
    {
        var backend = new Backend { FailedInstall = type };
        SelectiveInputHookRegistration registration = Create(backend);
        Win32Exception error = Assert.Throws<Win32Exception>(() =>
            registration.Register(SelectiveInputDevices.All));
        Assert.Equal(5, error.NativeErrorCode);
        Assert.False(registration.IsCallbackRooted);
        Assert.Equal(expected, string.Join(',', backend.Calls));
    }

    [Fact]
    public void InvalidHookIsReleasedButItsErrorIsReported()
    {
        var backend = new Backend { RemoveError = 1404 };
        SelectiveInputHookRegistration registration = Create(backend);
        registration.Register(SelectiveInputDevices.All);
        backend.FailedRemoves.Add(14);
        AggregateException error = Assert.Throws<AggregateException>(registration.Unregister);
        Assert.Equal(1404, Assert.IsType<Win32Exception>(Assert.Single(error.InnerExceptions)).NativeErrorCode);
        Assert.False(registration.IsCallbackRooted);
        backend.FailedRemoves.Clear();
        registration.Register(SelectiveInputDevices.Keyboard);
        registration.Unregister();
        Assert.False(registration.IsCallbackRooted);
    }

    [Fact]
    public void FailedRollbackKeepsCallbackUntilRetry()
    {
        var backend = new Backend { FailedInstall = 14 };
        backend.FailedRemoves.Add(13);
        SelectiveInputHookRegistration registration = Create(backend);
        AggregateException error = Assert.Throws<AggregateException>(() =>
            registration.Register(SelectiveInputDevices.All));
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.True(registration.IsCallbackRooted);
        Assert.Throws<InvalidOperationException>(() =>
            registration.Register(SelectiveInputDevices.Mouse));
        backend.FailedRemoves.Clear();
        registration.Unregister();
        Assert.False(registration.IsCallbackRooted);
        Assert.Equal("install:13,install:14,remove:13,remove:13",
            string.Join(',', backend.Calls));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReleaseAttemptsBothHooksAndRetriesOnlyFailures(bool throws)
    {
        var backend = new Backend { ThrowOnRemove = throws };
        SelectiveInputHookRegistration registration = Create(backend);
        registration.Register(SelectiveInputDevices.All);
        backend.FailedRemoves.Add(14);
        Assert.Throws<AggregateException>(registration.Unregister);
        Assert.True(registration.IsCallbackRooted);
        Assert.Equal("install:13,install:14,remove:14,remove:13",
            string.Join(',', backend.Calls));
        backend.FailedRemoves.Clear();
        registration.Unregister();
        Assert.Equal("remove:14", backend.Calls.Last());
        Assert.False(registration.IsCallbackRooted);
    }

    [Fact]
    public void InstallExceptionAlsoRollsBack()
    {
        var backend = new Backend { ThrowOnInstall = 14 };
        SelectiveInputHookRegistration registration = Create(backend);
        Assert.Throws<InvalidOperationException>(() =>
            registration.Register(SelectiveInputDevices.All));
        Assert.False(registration.IsCallbackRooted);
        Assert.Equal("install:13,install:14,remove:13", string.Join(',', backend.Calls));
    }

    [Fact]
    public void DuplicateRegistrationDoesNotReplaceOwnedHandles()
    {
        var backend = new Backend();
        SelectiveInputHookRegistration registration = Create(backend);
        registration.Register(SelectiveInputDevices.Keyboard);
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                registration.Register(SelectiveInputDevices.Mouse));
            Assert.Single(backend.Calls);
        }
        finally
        {
            registration.Unregister();
        }
    }

    [Fact]
    public void AnotherThreadCannotChangeRegistration()
    {
        var backend = new Backend();
        SelectiveInputHookRegistration registration = Create(backend);
        Exception? registerError = null;
        Exception? removeError = null;
        var thread = new Thread(() =>
        {
            registerError = Record.Exception(() => registration.Register(SelectiveInputDevices.All));
            removeError = Record.Exception(registration.Unregister);
        });
        thread.Start();
        Assert.True(thread.Join(5000));
        Assert.IsType<InvalidOperationException>(registerError);
        Assert.IsType<InvalidOperationException>(removeError);
        Assert.Empty(backend.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-1)]
    public void InvalidInternalSelectionNeverCallsBackend(int devices)
    {
        var backend = new Backend();
        SelectiveInputHookRegistration registration = Create(backend);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            registration.Register((SelectiveInputDevices)devices));
        Assert.Empty(backend.Calls);
        Assert.False(registration.IsCallbackRooted);
    }

    private static SelectiveInputHookRegistration Create(Backend backend) =>
        new(backend, (_, _, _) => 0, (_, _, _) => 0);

    private sealed class Backend : ISelectiveInputHookBackend
    {
        internal List<string> Calls { get; } = [];

        internal HashSet<nint> FailedRemoves { get; } = [];

        internal int FailedInstall
        {
            get; init;
        }

        internal int ThrowOnInstall
        {
            get; init;
        }

        internal bool ThrowOnRemove
        {
            get; init;
        }

        internal int RemoveError { get; init; } = 6;

        public (nint Handle, int Error) Install(int type, SelectiveInputHookProcedure procedure)
        {
            Calls.Add("install:" + type);
            if (type == ThrowOnInstall)
            {
                throw new InvalidOperationException("登録時の例外");
            }

            return type == FailedInstall ? (0, 5) : (type, 0);
        }

        public (bool Success, int Error) Remove(nint hook)
        {
            Calls.Add("remove:" + hook);
            if (FailedRemoves.Contains(hook) && ThrowOnRemove)
            {
                throw new InvalidOperationException("解除時の例外");
            }

            return FailedRemoves.Contains(hook) ? (false, RemoveError) : (true, 0);
        }
    }
}
