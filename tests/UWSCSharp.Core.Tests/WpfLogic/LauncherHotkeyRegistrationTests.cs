using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class LauncherHotkeyRegistrationTests : IDisposable
{
    private static readonly ScriptHotkey First = new(65, 2);
    private static readonly ScriptHotkey Second = new(66, 2);
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "uwsc-launcher-hotkey-" + Guid.NewGuid());
    private readonly Backend backend = new();
    private readonly LauncherHotkeyRegistration registration;
    private readonly LauncherSettingsController settings;
    private readonly string path;

    public LauncherHotkeyRegistrationTests()
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "launcher.json");
        settings = new LauncherSettingsController(path);
        settings.Reload();
        registration = new LauncherHotkeyRegistration(backend);
    }

    public void Dispose()
    {
        backend.FailRelease.Clear();
        backend.ThrowRelease.Clear();
        backend.BeforeRelease = null;
        registration.Dispose();
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void ReservesBeforeSavingAndSwitchesAcceptanceBeforeReleasingOldKey()
    {
        Save(First);
        int oldId = backend.Bindings.Single().Key;
        var candidate = new LauncherSettings(shortcut: Second);
        backend.BeforeRelease = id =>
        {
            Assert.Equal(oldId, id);
            Assert.Equal(Second, registration.ActiveShortcut);
            Assert.Equal(Second, LauncherSettingsStore.Load(path).Shortcut);
            Assert.False(registration.Accepts(id));
        };
        registration.Apply(candidate, [], () =>
        {
            Assert.Equal(2, backend.Bindings.Count);
            Assert.Equal(First, registration.ActiveShortcut);
            Assert.True(registration.Accepts(oldId));
            Assert.False(registration.Accepts(backend.Bindings.Keys.Max()));
            settings.Save(candidate);
        });
        Assert.Single(backend.Bindings);
        Assert.Empty(registration.PendingBindings);
        Assert.Empty(registration.CleanupErrors);
        Assert.True(registration.Accepts(backend.Bindings.Single().Key));
        Assert.Equal(["register:4096", "register:4097", "release:4096"], backend.Calls);
    }

    [Fact]
    public void SameKeySavesWithoutReplacingRegistration()
    {
        Save(First);
        int id = backend.Bindings.Single().Key;
        int calls = backend.Calls.Count;
        LauncherSettings candidate = new LauncherSettings(shortcut: First).WithSlot(0,
            new LauncherGroup("登録", []));
        registration.Apply(candidate, [], () => settings.Save(candidate));
        Assert.Equal(calls, backend.Calls.Count);
        Assert.True(registration.Accepts(id));
        Assert.Equal("登録", LauncherSettingsStore.Load(path).Slots[0]!.Title);
    }

    [Fact]
    public void RegistrationFailureDoesNotSaveOrReleasePreviousKey()
    {
        Save(First);
        byte[] before = File.ReadAllBytes(path);
        int id = backend.Bindings.Single().Key;
        backend.FailRegister = true;
        Assert.Throws<InvalidOperationException>(() => Save(Second));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(First, settings.Current!.Shortcut);
        Assert.True(registration.Accepts(id));
        Assert.Empty(registration.PendingBindings);
        Assert.DoesNotContain(backend.Calls, call => call.StartsWith("release:"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SaveFailureKeepsOldFileAndAcceptanceEvenWhenNewKeyCannotBeReleased(
        bool failCleanup)
    {
        Save(First);
        int oldId = backend.Bindings.Single().Key;
        byte[] before = File.ReadAllBytes(path);
        if (failCleanup)
        {
            backend.FailRelease.Add(oldId + 1);
        }

        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(() => Save(Second));
        }

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(First, settings.Current!.Shortcut);
        Assert.True(registration.Accepts(oldId));
        Assert.False(registration.Accepts(oldId + 1));
        Assert.Equal(failCleanup ? 1 : 0, registration.PendingBindings.Length);
        Assert.Equal(failCleanup ? 2 : 1, registration.RegisteredShortcuts.Length);
        if (failCleanup)
        {
            Assert.Contains("回収未完了", registration.Status);
            Assert.Single(registration.CleanupErrors);
            backend.FailRelease.Clear();
            Assert.True(registration.RetryCleanup());
            Assert.True(registration.Accepts(oldId));
            Assert.Equal(First, registration.RegisteredShortcuts.Single());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OldReleaseFailureKeepsSavedNewKeyAndRequiresExplicitCleanup(bool throws)
    {
        Save(First);
        int oldId = backend.Bindings.Single().Key;
        (throws ? backend.ThrowRelease : backend.FailRelease).Add(oldId);
        Save(Second);
        int activeId = backend.Bindings.Keys.Max();
        Assert.Equal(Second, LauncherSettingsStore.Load(path).Shortcut);
        Assert.Equal(Second, settings.Current!.Shortcut);
        Assert.Equal(Second, registration.ActiveShortcut);
        Assert.False(registration.Accepts(oldId));
        Assert.True(registration.Accepts(activeId));
        Assert.Equal(new LauncherHotkeyBinding(oldId, First),
            Assert.Single(registration.PendingBindings));
        Assert.Single(registration.CleanupErrors);
        Assert.Contains(First, registration.RegisteredShortcuts);
        Assert.Contains(Second, registration.RegisteredShortcuts);
        int calls = backend.Calls.Count;
        Assert.Throws<InvalidOperationException>(() => Save(new ScriptHotkey(67, 2)));
        Assert.Equal(calls, backend.Calls.Count);
        Save(Second);
        Assert.Equal(calls, backend.Calls.Count);
        Assert.False(registration.RetryCleanup());
        Assert.True(registration.Accepts(activeId));
        backend.ThrowRelease.Clear();
        backend.FailRelease.Clear();
        Assert.True(registration.RetryCleanup());
        Assert.Empty(registration.CleanupErrors);
        Assert.Equal(Second, Assert.Single(registration.RegisteredShortcuts));
        Save(First);
        Assert.False(registration.Accepts(oldId));
        Assert.False(registration.Accepts(activeId));
        Assert.True(registration.Accepts(backend.Bindings.Single().Key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisablingSavesBeforeStoppingAcceptanceAndRetainsFailedRelease(bool failCleanup)
    {
        Save(First);
        int id = backend.Bindings.Single().Key;
        if (failCleanup)
        {
            backend.FailRelease.Add(id);
        }

        backend.BeforeRelease = _ =>
        {
            Assert.Null(LauncherSettingsStore.Load(path).Shortcut);
            Assert.False(registration.Accepts(id));
        };
        Save(null);
        Assert.Null(registration.ActiveShortcut);
        Assert.Null(settings.Current!.Shortcut);
        Assert.Equal(failCleanup ? 1 : 0, registration.PendingBindings.Length);
        if (failCleanup)
        {
            Assert.Equal(First, Assert.Single(registration.RegisteredShortcuts));
        }
    }

    [Fact]
    public void FailedDisableKeepsOldAcceptance()
    {
        Save(First);
        int id = backend.Bindings.Single().Key;
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(() => Save(null));
        }

        Assert.Equal(First, LauncherSettingsStore.Load(path).Shortcut);
        Assert.True(registration.Accepts(id));
        Assert.DoesNotContain(backend.Calls, call => call.StartsWith("release:"));
    }

    [Fact]
    public void OccupiedKeyIsRejectedBeforeAnySideEffects()
    {
        Save(First);
        int calls = backend.Calls.Count;
        byte[] before = File.ReadAllBytes(path);
        var candidate = new LauncherSettings(shortcut: Second);
        Assert.Throws<InvalidDataException>(() =>
            registration.Apply(candidate, [Second], () => settings.Save(candidate)));
        Assert.Equal(calls, backend.Calls.Count);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(First, registration.ActiveShortcut);
    }

    [Fact]
    public void ReentrantChangeIsRejectedAndFailedSaveRollsBackOnlyNewKey()
    {
        Save(First);
        var candidate = new LauncherSettings(shortcut: Second);
        Assert.Throws<InvalidOperationException>(() =>
            registration.Apply(candidate, [], registration.Dispose));
        Assert.Equal(First, registration.ActiveShortcut);
        Assert.Equal(First, LauncherSettingsStore.Load(path).Shortcut);
        Assert.Empty(registration.PendingBindings);
        Save(Second);
        Assert.Equal(Second, registration.ActiveShortcut);
    }

    [Fact]
    public void ExitStopsAllAcceptanceAndRetriesRemainingRegistrations()
    {
        Save(First);
        int oldId = backend.Bindings.Single().Key;
        backend.FailRelease.Add(oldId);
        Save(Second);
        int activeId = backend.Bindings.Keys.Max();
        backend.ThrowRelease.Add(activeId);
        registration.Dispose();
        Assert.False(registration.Accepts(oldId));
        Assert.False(registration.Accepts(activeId));
        Assert.Null(registration.ActiveShortcut);
        Assert.Equal(2, registration.PendingBindings.Length);
        Assert.Equal(2, registration.CleanupErrors.Length);
        Assert.Throws<ObjectDisposedException>(() => Save(null));
        backend.FailRelease.Clear();
        backend.ThrowRelease.Clear();
        Assert.True(registration.RetryCleanup());
        Assert.Empty(backend.Bindings);
        int calls = backend.Calls.Count;
        registration.Dispose();
        Assert.Equal(calls, backend.Calls.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirstSaveFailureNeverAcceptsAnUncommittedKey(bool failCleanup)
    {
        if (failCleanup)
        {
            backend.FailRelease.Add(0x1000);
        }

        var candidate = new LauncherSettings(shortcut: First);
        var failure = new IOException("保存障害を注入しました。");
        Assert.Same(failure, Assert.Throws<IOException>(() =>
            registration.Apply(candidate, [], () => throw failure)));
        Assert.Null(registration.ActiveShortcut);
        Assert.False(registration.Accepts(0x1000));
        Assert.False(File.Exists(path));
        Assert.Equal(failCleanup ? 1 : 0, registration.PendingBindings.Length);
    }

    [Theory]
    [InlineData(65, true)]
    [InlineData(17, true)]
    [InlineData(162, true)]
    [InlineData(163, true)]
    [InlineData(66, false)]
    [InlineData(18, false)]
    public async Task KeyboardInputTemporarilyReleasesOnlyMatchingActiveKey(
        int key, bool releases)
    {
        Save(First);
        int id = backend.Bindings.Single().Key;
        await registration.SendKeyboardInputAsync(key, async () =>
        {
            Assert.False(registration.Accepts(id));
            Assert.Equal(!releases, backend.Bindings.ContainsKey(id));
            Assert.Throws<InvalidOperationException>(() => Save(Second));
            Assert.Throws<InvalidOperationException>(() => registration.RetryCleanup());
            await Task.Yield();
        });
        Assert.True(registration.Accepts(id));
        Assert.Equal(First, backend.Bindings[id]);
        Assert.Equal(releases ? 3 : 1, backend.Calls.Count);
    }

    [Fact]
    public async Task FailedInputRestoresRegistrationAndKeepsOriginalException()
    {
        Save(First);
        int id = backend.Bindings.Single().Key;
        var failure = new OperationCanceledException();
        Assert.Same(failure, await Assert.ThrowsAsync<OperationCanceledException>(() =>
            registration.SendKeyboardInputAsync(65, () => Task.FromException(failure))));
        Assert.True(registration.Accepts(id));
        Assert.Equal(First, backend.Bindings[id]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreFailureDisablesAcceptanceAndCanBeReapplied(bool failInput)
    {
        Save(First);
        int id = backend.Bindings.Single().Key;
        backend.FailRegister = true;
        var failure = new IOException("入力障害を注入しました。");
        Exception error = await Assert.ThrowsAnyAsync<Exception>(() =>
            registration.SendKeyboardInputAsync(65, () => failInput
                ? Task.FromException(failure) : Task.CompletedTask));
        if (failInput)
        {
            Assert.Contains(failure, Assert.IsType<AggregateException>(error).InnerExceptions);
        }
        else
        {
            Assert.IsType<InvalidOperationException>(error);
        }

        Assert.False(registration.Accepts(id));
        Assert.Empty(registration.RegisteredShortcuts);
        Assert.Null(registration.ActiveShortcut);
        Assert.Equal(First, LauncherSettingsStore.Load(path).Shortcut);
        backend.FailRegister = false;
        Save(First);
        Assert.True(registration.Accepts(backend.Bindings.Single().Key));
        Assert.False(registration.Accepts(id));
    }

    [Fact]
    public async Task PendingKeyBlocksMatchingInputWithoutAutomaticCleanup()
    {
        Save(First);
        int oldId = backend.Bindings.Single().Key;
        backend.FailRelease.Add(oldId);
        Save(Second);
        int calls = backend.Calls.Count;
        bool sent = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registration.SendKeyboardInputAsync(65, () =>
            {
                sent = true;
                return Task.CompletedTask;
            }));
        Assert.False(sent);
        Assert.Equal(calls, backend.Calls.Count);
        Assert.Single(registration.PendingBindings);
        await registration.SendKeyboardInputAsync(67, () => Task.CompletedTask);
        Assert.Equal(calls, backend.Calls.Count);
    }

    [Fact]
    public async Task FailedTemporaryReleasePreventsSendingAndKeepsRegistration()
    {
        Save(First);
        int id = backend.Bindings.Single().Key;
        backend.FailRelease.Add(id);
        bool sent = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registration.SendKeyboardInputAsync(17, () =>
            {
                sent = true;
                return Task.CompletedTask;
            }));
        Assert.False(sent);
        Assert.True(registration.Accepts(id));
        Assert.Equal(First, backend.Bindings[id]);
        Assert.Equal(2, backend.Calls.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedMainAndLauncherGuardsRestoreBothRegistrations(bool failInput)
    {
        using var main = new HotkeyRegistration(backend);
        Assert.True(main.Apply(new HotkeySettings(Modifiers: 2)));
        Save(First);
        int id = backend.Bindings.Keys.Max();
        Func<Task> operation = () => main.SendKeyboardInputAsync(17, () =>
            registration.SendKeyboardInputAsync(17, () =>
            {
                Assert.Empty(backend.Bindings);
                Assert.False(registration.Accepts(id));
                return failInput ? Task.FromException(new IOException("入力障害"))
                    : Task.CompletedTask;
            }));
        if (failInput)
        {
            await Assert.ThrowsAsync<IOException>(operation);
        }
        else
        {
            await operation();
        }

        Assert.Equal(5, backend.Bindings.Count);
        Assert.True(registration.Accepts(id));
        Assert.Equal(4, main.ActiveKeys.Length);
    }

    [Fact]
    public void ReloadRegistrationFailurePreservesPreviousSnapshotAndCanBeRetried()
    {
        Save(First);
        LauncherSettingsStore.Save(path, new LauncherSettings(shortcut: Second));
        backend.FailRegister = true;
        Assert.Throws<InvalidOperationException>(() => settings.ReloadWithHotkey(candidate =>
            registration.Apply(candidate, [], () => { })));
        Assert.Equal(First, settings.Current!.Shortcut);
        Assert.Equal(First, registration.ActiveShortcut);
        Assert.Null(settings.LoadError);
        Assert.Equal(Second, LauncherSettingsStore.Load(path).Shortcut);
        backend.FailRegister = false;
        settings.ReloadWithHotkey(candidate => registration.Apply(candidate, [], () => { }));
        Assert.Equal(Second, settings.Current!.Shortcut);
        Assert.Equal(Second, registration.ActiveShortcut);
    }

    [Fact]
    public void InitialRegistrationFailureKeepsValidFileAvailableForEditing()
    {
        LauncherSettingsStore.Save(path, new LauncherSettings(shortcut: First)
            .WithSlot(0, new LauncherGroup("保持する登録", [])));
        var initial = new LauncherSettingsController(path);
        backend.FailRegister = true;
        Assert.Throws<InvalidOperationException>(() => initial.ReloadWithHotkey(candidate =>
            registration.Apply(candidate, [], () => { })));
        Assert.Null(initial.LoadError);
        Assert.Equal("保持する登録", initial.Current!.Slots[0]!.Title);
        Assert.Null(registration.ActiveShortcut);
        backend.FailRegister = false;
        var changed = new LauncherSettings(initial.Current.Slots, Second);
        registration.Apply(changed, [], () => initial.Save(changed));
        Assert.Equal(Second, LauncherSettingsStore.Load(path).Shortcut);
        Assert.Equal("保持する登録", LauncherSettingsStore.Load(path).Slots[0]!.Title);
    }

    private void Save(ScriptHotkey? shortcut)
    {
        var candidate = new LauncherSettings(shortcut: shortcut);
        registration.Apply(candidate, [], () => settings.Save(candidate));
    }

    private sealed class Backend : IHotkeyBackend
    {
        internal Dictionary<int, ScriptHotkey> Bindings { get; } = [];

        internal List<string> Calls { get; } = [];

        internal HashSet<int> FailRelease { get; } = [];

        internal HashSet<int> ThrowRelease { get; } = [];

        internal bool FailRegister
        {
            get; set;
        }

        internal Action<int>? BeforeRelease
        {
            get; set;
        }

        public bool Register(int id, uint modifiers, int key)
        {
            Calls.Add("register:" + id);
            Assert.True((modifiers & 0x4000) != 0);
            if (FailRegister)
            {
                return false;
            }

            Bindings.Add(id, new ScriptHotkey(key, (int)(modifiers & 15)));
            return true;
        }

        public bool Unregister(int id)
        {
            Calls.Add("release:" + id);
            BeforeRelease?.Invoke(id);
            if (ThrowRelease.Contains(id))
            {
                throw new InvalidOperationException("解除障害を注入しました。");
            }

            return !FailRelease.Contains(id) && Bindings.Remove(id);
        }
    }
}
