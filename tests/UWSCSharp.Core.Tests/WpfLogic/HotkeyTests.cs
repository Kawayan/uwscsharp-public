using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class HotkeyTests
{
    [Theory]
    [InlineData("{}", 1u)]
    [InlineData("{\"Modifiers\":0}", 0u)]
    [InlineData("{\"Modifiers\":3}", 3u)]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public void DefaultAltPreservesSavedModifierChoices(string json, uint expected)
    {
        string directory = Path.Combine(Path.GetTempPath(),
            Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "hotkeys.json");
        try
        {
            Assert.Equal(1u, new HotkeySettings().Modifiers);
            Assert.Equal(1u, HotkeySettings.LoadForStartup(path).Modifiers);
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, json);
            var settings = HotkeySettings.LoadForStartup(path);
            Assert.Equal(expected, settings.Modifiers);
            settings.Save(path);
            Assert.Equal(settings, HotkeySettings.Load(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(2, false)]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public void ReservedSettingsKeepPreviousBindings(int role, bool enabled)
    {
        var backend = new Backend();
        using var registration = new HotkeyRegistration(backend);
        Assert.True(registration.Apply(new()));
        int calls = backend.Threads.Count;
        int[] keys = [119, 120, 121, 118];
        keys[role] = 122;
        var candidate = new HotkeySettings(enabled, 7,
            keys[0], keys[1], keys[2], keys[3]);
        Assert.Throws<InvalidDataException>(candidate.Validate);
        Assert.False(registration.Apply(candidate));
        Assert.Equal([119, 120, 121, 118], registration.ActiveKeys);
        Assert.Equal(calls, backend.Threads.Count);
        Assert.Contains("Ctrl+Alt+Shift+F11", registration.Status);
        Assert.Contains("設定を変更", registration.Status);
    }

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public void StartupPreservesReservedSettingsWithoutRewritingTheFile()
    {
        string path = Path.GetTempFileName();
        const string original = """{"Modifiers":7,"StopKey":122}""";
        try
        {
            File.WriteAllText(path, original);
            Assert.Throws<InvalidDataException>(() => HotkeySettings.Load(path));
            var settings = HotkeySettings.LoadForStartup(path);
            Assert.Equal(new HotkeySettings(Modifiers: 7, StopKey: 122), settings);
            var backend = new Backend();
            using var registration = new HotkeyRegistration(backend);
            Assert.False(registration.Apply(settings));
            Assert.Empty(backend.Keys);
            Assert.Empty(backend.Threads);
            Assert.Throws<InvalidDataException>(() => settings.Save(path));
            Assert.Equal(original, File.ReadAllText(path));
            Assert.True(registration.Apply(settings with
            {
                Modifiers = 15
            }));
            Assert.Equal(4, backend.Keys.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public void AsyncInputRestoresOnTheCallingContext()
    {
        var backend = new Backend();
        using var registration = new HotkeyRegistration(backend);
        Assert.True(registration.Apply(new()));
        using var context = new PumpContext();
        SynchronizationContext? previous = SynchronizationContext.Current;
        int ownerThread = Environment.CurrentManagedThreadId;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Task sending = registration.SendKeyboardInputAsync(120, () => completion.Task);
            Assert.False(sending.IsCompleted);
            completion.SetResult();
            context.RunOne();
            Assert.True(sending.IsCompletedSuccessfully);
            Assert.Equal(4, backend.Keys.Count);
            Assert.All(backend.Threads, thread => Assert.Equal(ownerThread, thread));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task AsyncInputKeepsBindingsSuspendedUntilSendCompletes()
    {
        var backend = new Backend();
        using var registration = new HotkeyRegistration(backend);
        Assert.True(registration.Apply(new()));
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int sends = 0;
        Task sending = registration.SendKeyboardInputAsync(120, () =>
        {
            sends++;
            return completion.Task;
        });
        Assert.False(sending.IsCompleted);
        Assert.False(registration.IsRegistered(2));
        Assert.Equal(3, backend.Keys.Count);
        Assert.Throws<InvalidOperationException>(() => registration.Apply(new()));
        Assert.Throws<InvalidOperationException>(registration.Dispose);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registration.SendKeyboardInputAsync(119, () => Task.CompletedTask));
        completion.SetResult();
        await sending;
        Assert.Equal(1, sends);
        Assert.Equal(4, backend.Keys.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task AsyncInputPreservesSendAndRestoreFailures(bool failRestore)
    {
        var backend = new Backend();
        using var registration = new HotkeyRegistration(backend);
        Assert.True(registration.Apply(new()));
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task sending = registration.SendKeyboardInputAsync(120, () => completion.Task);
        var failure = new IOException("非同期送信失敗");
        backend.ConflictKey = failRestore ? 120 : 0;
        completion.SetException(failure);
        if (failRestore)
        {
            AggregateException error = await Assert.ThrowsAsync<AggregateException>(() => sending);
            Assert.Equal(2, error.InnerExceptions.Count);
            Assert.Same(failure, error.InnerExceptions[0]);
            Assert.False(registration.IsRegistered(2));
            backend.ConflictKey = 0;
            Assert.True(registration.Apply(new()));
        }
        else
        {
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => sending));
        }

        Assert.Equal(4, backend.Keys.Count);
    }

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public void StopSuppressionSurvivesInputAndSettingChanges()
    {
        var backend = new Backend();
        using var registration = new HotkeyRegistration(backend);
        Assert.True(registration.Apply(new(Modifiers: 1)));
        registration.SetStopSuppressed(true);
        registration.SetStopSuppressed(true);
        Assert.Equal([1, 2, 4], backend.Keys.Keys.Order());
        registration.SendKeyboardInput(121, () => Assert.False(registration.IsRegistered(3)));
        registration.SendKeyboardInput(18, () => Assert.Empty(backend.Keys));
        Assert.Equal([1, 2, 4], backend.Keys.Keys.Order());
        Assert.True(registration.Apply(new(StopKey: 114)));
        Assert.False(registration.IsRegistered(3));
        registration.SetStopSuppressed(false);
        Assert.Equal(114, backend.Keys[3]);
        registration.SetStopSuppressed(false);
        Assert.Equal(4, backend.Keys.Count);
    }

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public void FailedStopReleaseKeepsItsRegistrationAndCanRetry()
    {
        var backend = new Backend();
        using var registration = new HotkeyRegistration(backend);
        Assert.True(registration.Apply(new()));
        backend.FailReleaseKey = 121;
        Assert.Throws<InvalidOperationException>(() => registration.SetStopSuppressed(true));
        Assert.False(registration.StopSuppressed);
        Assert.True(registration.IsRegistered(3));
        backend.FailReleaseKey = 0;
        registration.SetStopSuppressed(true);
        Assert.False(registration.IsRegistered(3));
    }

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public void FailedStopRestorationCanRecoverByApplyingSettings()
    {
        var backend = new Backend();
        using var registration = new HotkeyRegistration(backend);
        Assert.True(registration.Apply(new()));
        registration.SetStopSuppressed(true);
        backend.ConflictKey = 121;
        Assert.Throws<InvalidOperationException>(() => registration.SetStopSuppressed(false));
        Assert.False(registration.StopSuppressed);
        Assert.False(registration.IsRegistered(3));
        Assert.Equal([1, 2, 4], backend.Keys.Keys.Order());
        backend.ConflictKey = 0;
        Assert.True(registration.Apply(new()));
        Assert.True(registration.IsRegistered(3));
    }

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public void DisabledSettingsRemainDisabledAndDisposedReleaseCanRetry()
    {
        var backend = new Backend();
        var registration = new HotkeyRegistration(backend);
        Assert.True(registration.Apply(new(Enabled: false)));
        registration.SetStopSuppressed(true);
        registration.SetStopSuppressed(false);
        Assert.Empty(backend.Keys);
        Assert.True(registration.Apply(new()));
        backend.FailReleaseKey = 121;
        registration.Dispose();
        Assert.Equal(121, Assert.Single(backend.Keys).Value);
        Assert.Throws<ObjectDisposedException>(() => registration.SetStopSuppressed(false));
        backend.FailReleaseKey = 0;
        registration.Dispose();
        Assert.Empty(backend.Keys);
    }

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public void StopSuppressionRejectsReentryDuringInput()
    {
        var backend = new Backend();
        using var registration = new HotkeyRegistration(backend);
        Assert.True(registration.Apply(new()));
        registration.SendKeyboardInput(121, () => Assert.Throws<InvalidOperationException>(
            () => registration.SetStopSuppressed(true)));
        Assert.True(registration.IsRegistered(3));
    }

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public void DisposedRegistrationRejectsRebinding()
    {
        var backend = new Backend();
        var registration = new HotkeyRegistration(backend);
        Assert.True(registration.Apply(new()));
        registration.Dispose();
        Assert.Throws<ObjectDisposedException>(() => registration.Apply(new()));
        Assert.Empty(backend.Keys);
    }

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public void DisposedRegistrationRejectsInput()
    {
        var backend = new Backend();
        var registration = new HotkeyRegistration(backend);
        Assert.True(registration.Apply(new()));
        registration.Dispose();
        bool sent = false;
        Assert.Throws<ObjectDisposedException>(() => registration.SendKeyboardInput(119, () => sent = true));
        Assert.False(sent);
    }

    [Fact]
    public void KeyboardInputTemporarilyReleasesOnlyTheMatchingBinding()
    {
        var backend = new Backend();
        using var registration = new HotkeyRegistration(backend);
        Assert.True(registration.Apply(new()));
        registration.SendKeyboardInput(120, () =>
        {
            Assert.False(registration.IsRegistered(2));
            Assert.True(registration.IsRegistered(1));
            Assert.True(registration.IsRegistered(3));
            Assert.True(registration.IsRegistered(4));
            Assert.Equal([119, 121, 118], registration.ActiveKeys);
        });
        Assert.Equal([119, 120, 121, 118], registration.ActiveKeys);
    }

    [Theory]
    [InlineData(16, 4u)]
    [InlineData(160, 4u)]
    [InlineData(161, 4u)]
    [InlineData(17, 2u)]
    [InlineData(162, 2u)]
    [InlineData(163, 2u)]
    [InlineData(18, 1u)]
    [InlineData(164, 1u)]
    [InlineData(165, 1u)]
    [InlineData(91, 8u)]
    [InlineData(92, 8u)]
    public void KeyboardModifiersTemporarilyReleaseAllUsingThatModifier(int key, uint modifier)
    {
        var backend = new Backend();
        using var registration = new HotkeyRegistration(backend);
        Assert.True(registration.Apply(new(Modifiers: modifier)));
        registration.SendKeyboardInput(key, () => Assert.Empty(backend.Keys));
        Assert.Equal(4, backend.Keys.Count);
        registration.SendKeyboardInput(65, () => Assert.Equal(4, backend.Keys.Count));
    }

    [Fact]
    public void FailedUnregisterPreventsInputAndRestoresTheReleasedBindings()
    {
        var backend = new Backend();
        using var registration = new HotkeyRegistration(backend);
        Assert.True(registration.Apply(new(Modifiers: 2)));
        backend.FailReleaseKey = 120;
        bool sent = false;
        Assert.Throws<InvalidOperationException>(() =>
            registration.SendKeyboardInput(17, () => sent = true));
        Assert.False(sent);
        Assert.Equal([119, 120, 121, 118], registration.ActiveKeys);
        backend.FailReleaseKey = 0;
    }

    [Fact]
    public void FailedInputIsRethrownAfterRestoringTheBinding()
    {
        var backend = new Backend();
        using var registration = new HotkeyRegistration(backend);
        Assert.True(registration.Apply(new()));
        using var cancellation = new CancellationTokenSource();
        var failure = new OperationCanceledException(cancellation.Token);
        Exception error = Assert.Throws<OperationCanceledException>(() =>
            registration.SendKeyboardInput(120, () => throw failure));
        Assert.Same(failure, error);
        Assert.Equal(4, backend.Keys.Count);
    }

    [Fact]
    public void FailedReregistrationIsVisibleAndCanBeRepaired()
    {
        var backend = new Backend();
        using var registration = new HotkeyRegistration(backend);
        Assert.True(registration.Apply(new()));
        Assert.Throws<InvalidOperationException>(() =>
            registration.SendKeyboardInput(120, () => backend.ConflictKey = 120));
        Assert.False(registration.IsRegistered(2));
        Assert.Equal(3, registration.ActiveKeys.Length);
        Assert.Contains("画面ボタン", registration.Status);
        backend.ConflictKey = 0;
        Assert.True(registration.Apply(new()));
        Assert.Equal(4, registration.ActiveKeys.Length);
    }

    [Fact]
    public void InputAndRestoreFailuresAreBothPreserved()
    {
        var backend = new Backend();
        using var registration = new HotkeyRegistration(backend);
        Assert.True(registration.Apply(new()));
        var failure = new IOException("入力送信失敗");
        AggregateException error = Assert.Throws<AggregateException>(() =>
            registration.SendKeyboardInput(120, () =>
            {
                backend.ConflictKey = 120;
                throw failure;
            }));
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Same(failure, error.InnerExceptions[0]);
        Assert.Equal(3, backend.Keys.Count);
    }

    [Fact]
    public void RegistrationCannotChangeDuringKeyboardInput()
    {
        var backend = new Backend();
        using var registration = new HotkeyRegistration(backend);
        Assert.True(registration.Apply(new()));
        registration.SendKeyboardInput(120, () =>
        {
            Assert.Throws<InvalidOperationException>(() => registration.Apply(new()));
            Assert.Throws<InvalidOperationException>(registration.Dispose);
            Assert.Throws<InvalidOperationException>(() =>
                registration.SendKeyboardInput(119, () => { }));
        });
        Assert.Equal(4, backend.Keys.Count);
    }

    [Fact]
    public void OrphanedRegistrationPreventsInputUntilItCanBeReleased()
    {
        var backend = new Backend();
        using var registration = new HotkeyRegistration(backend);
        Assert.True(registration.Apply(new()));
        backend.ConflictKey = 113;
        backend.FailReleaseKey = 112;
        Assert.False(registration.Apply(new(RecordKey: 112, PauseKey: 113, StopKey: 114)));
        Assert.Single(backend.Keys);
        bool sent = false;
        Assert.Throws<InvalidOperationException>(() =>
            registration.SendKeyboardInput(65, () => sent = true));
        Assert.False(sent);
        backend.FailReleaseKey = 0;
        Assert.True(registration.Apply(new(Enabled: false)));
        registration.SendKeyboardInput(65, () => sent = true);
        Assert.True(sent);
    }

    [Fact]
    public void PlaybackConflictRestoresAllFourBindings()
    {
        var backend = new Backend();
        using var registration = new HotkeyRegistration(backend);
        Assert.True(registration.Apply(new()));
        backend.ConflictKey = 112;
        Assert.False(registration.Apply(new(PlayKey: 112)));
        Assert.Equal([119, 120, 121, 118], registration.ActiveKeys);
        Assert.True(registration.IsRegistered(4));
        Assert.Contains("再生 Alt+F7", new HotkeySettings().Describe());
    }

    [Fact]
    public void LegacySettingsPreserveExistingKeysWhenPlaybackDefaultConflicts()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{"RecordKey":118,"PauseKey":120,"StopKey":121}""");
            var settings = HotkeySettings.Load(path);
            Assert.Equal([118, 120, 121, 112], settings.Keys);
            settings.Save(path);
            Assert.Equal(settings, HotkeySettings.Load(path));
            File.WriteAllText(path, """{"RecordKey":118,"PlayKey":118}""");
            Assert.Throws<InvalidDataException>(() => HotkeySettings.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FailedReleaseKeepsOnlySurvivingBindingAndCanRetry()
    {
        var backend = new Backend();
        using var registration = new HotkeyRegistration(backend);
        Assert.True(registration.Apply(new()));
        backend.FailReleaseKey = 120;
        Assert.False(registration.Apply(new(Modifiers: 3)));
        Assert.Equal([120], registration.ActiveKeys);
        Assert.True(registration.IsRegistered(2));
        Assert.False(registration.IsRegistered(1));
        Assert.False(registration.IsRegistered(3));
        Assert.Contains("解放", registration.Status);
        backend.FailReleaseKey = 0;
        Assert.True(registration.Apply(new(Modifiers: 3)));
        Assert.Equal([119, 120, 121, 118], registration.ActiveKeys);
    }

    [Fact]
    public void FailedPartialReleaseIgnoresStaleMessagesAndCanDisable()
    {
        var backend = new Backend();
        using var registration = new HotkeyRegistration(backend);
        Assert.True(registration.Apply(new()));
        backend.ConflictKey = 113;
        backend.FailReleaseKey = 112;
        Assert.False(registration.Apply(new(RecordKey: 112, PauseKey: 113, StopKey: 114)));
        Assert.Equal(112, Assert.Single(backend.Keys).Value);
        Assert.Empty(registration.ActiveKeys);
        Assert.False(registration.IsRegistered(1));
        backend.FailReleaseKey = 0;
        Assert.True(registration.Apply(new(Enabled: false)));
        Assert.Empty(backend.Keys);
    }

    [Fact]
    public void ConflictRollsBackWithoutLeavingPartialNewBindings()
    {
        var backend = new Backend();
        using var registration = new HotkeyRegistration(backend);
        Assert.True(registration.Apply(new()));
        backend.ConflictKey = 113;
        Assert.False(registration.Apply(new(RecordKey: 112, PauseKey: 113, StopKey: 114)));
        Assert.Equal([119, 120, 121, 118], backend.Keys.OrderBy(pair => pair.Key).Select(pair => pair.Value));
        Assert.Equal([119, 120, 121, 118], registration.ActiveKeys);
        Assert.Contains("復元", registration.Status);
    }

    [Fact]
    public void DisabledAndDisposedBindingsAreReleased()
    {
        var backend = new Backend();
        var registration = new HotkeyRegistration(backend);
        Assert.True(registration.Apply(new()));
        Assert.True(registration.Apply(new(Enabled: false)));
        Assert.Empty(backend.Keys);
        Assert.True(registration.Apply(new(Modifiers: 3)));
        registration.Dispose();
        Assert.Empty(backend.Keys);
    }

    [Fact]
    public void FailedRollbackDisablesEveryBinding()
    {
        var backend = new Backend();
        using var registration = new HotkeyRegistration(backend);
        Assert.True(registration.Apply(new()));
        backend.FailAll = true;
        Assert.False(registration.Apply(new(Modifiers: 3)));
        Assert.Empty(backend.Keys);
        Assert.Empty(registration.ActiveKeys);
    }

    [Fact]
    public void SettingsRoundTripAndInvalidFileIsNotOverwritten()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "settings.json");
        try
        {
            var settings = new HotkeySettings(Modifiers: 3, RecordKey: 112);
            settings.Save(path);
            Assert.Equal(settings, HotkeySettings.Load(path));
            File.WriteAllText(path, "{\"RecordKey\":123}");
            Assert.Throws<InvalidDataException>(() => HotkeySettings.Load(path));
            Assert.Equal("{\"RecordKey\":123}", File.ReadAllText(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root);
            }
        }
    }

    [Theory]
    [InlineData(119, 119, 121, 0u)]
    [InlineData(123, 120, 121, 0u)]
    [InlineData(119, 120, 121, 16u)]
    public void InvalidSettingsDoNotTouchExistingRegistration(
        int record, int pause, int stop, uint modifiers)
    {
        var backend = new Backend();
        using var registration = new HotkeyRegistration(backend);
        Assert.True(registration.Apply(new()));
        Assert.Throws<InvalidDataException>(() => registration.Apply(
            new(RecordKey: record, PauseKey: pause, StopKey: stop, Modifiers: modifiers)));
        Assert.Equal(4, backend.Keys.Count);
    }

    private sealed class Backend : IHotkeyBackend
    {
        internal Dictionary<int, int> Keys { get; } = [];

        internal List<int> Threads { get; } = [];

        internal int ConflictKey
        {
            get; set;
        }

        internal bool FailAll
        {
            get; set;
        }

        internal int FailReleaseKey
        {
            get; set;
        }

        public bool Register(int id, uint modifiers, int key)
        {
            Threads.Add(Environment.CurrentManagedThreadId);
            if (FailAll || key == ConflictKey)
            {
                return false;
            }

            Keys.Add(id, key);
            return true;
        }

        public bool Unregister(int id)
        {
            Threads.Add(Environment.CurrentManagedThreadId);
            return Keys.GetValueOrDefault(id) != FailReleaseKey && Keys.Remove(id);
        }
    }

    private sealed class PumpContext : SynchronizationContext, IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<Action> callbacks = [];

        public override void Post(SendOrPostCallback callback, object? state) =>
            callbacks.Add(() => callback(state));

        internal void RunOne()
        {
            Assert.True(callbacks.TryTake(out Action? callback, TimeSpan.FromSeconds(5)));
            callback();
        }

        public void Dispose() => callbacks.Dispose();
    }
}
