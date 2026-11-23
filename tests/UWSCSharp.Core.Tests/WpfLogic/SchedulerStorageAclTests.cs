using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

/// <summary>
/// 専用一時フォルダーのACLだけを変更し、画面や入力を操作しません。
/// </summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerStorageAclTests : IDisposable
{
    private static readonly DateTimeOffset Accepted =
        new(2027, 1, 2, 3, 4, 0, TimeSpan.Zero);
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "uwsc-storage-acl-" + Guid.NewGuid().ToString("N"));

    public SchedulerStorageAclTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()),
            Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase);
        Assert.False((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0);
        Directory.Delete(directory, true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoryDenialPreservesPendingResultUntilPermissionsRestored(
        bool denyWholeManagedTree)
    {
        var storage = new SchedulerManagedStorage(directory);
        var journal = new SchedulerHistoryJournal(storage.SaveHistoryAsync);
        SchedulerHistoryRecord accepted = Capture();
        await storage.SaveHistoryAsync(accepted);
        string history = Path.Combine(directory, "managed", "history");
        string path = Path.Combine(history,
            accepted.OccurrenceId.ToString("N") + ".json");
        byte[] previous = await File.ReadAllBytesAsync(path);
        SchedulerHistoryRecord completed = accepted.MarkStarted(Accepted.AddSeconds(1))
            .Complete(SchedulerExecutionResult.Completed(7, true, false, true),
                Accepted.AddSeconds(2), endedConfirmed: true);
        string denied = denyWholeManagedTree
            ? Path.Combine(directory, "managed") : history;
        Exception recordedError;
        using (var permission = new FolderWriteDenial(denied,
            denyWholeManagedTree))
        {
            permission.Apply();
            AssertCreateDenied(denied);
            if (!denyWholeManagedTree)
            {
                // 既存の排他ファイルは開けるため、一時ファイル作成の拒否を確認できます。
                using var existing = new FileStream(Path.Combine(history,
                    ".history.lock"), FileMode.Open, FileAccess.ReadWrite,
                    FileShare.None);
            }
            else
            {
                UnauthorizedAccessException lockError = Assert.Throws<UnauthorizedAccessException>(() =>
                {
                    using var existing = new FileStream(Path.Combine(denied,
                        ".storage.lock"), FileMode.Open, FileAccess.ReadWrite,
                        FileShare.None);
                });
                Assert.Equal(5, lockError.HResult & 0xffff);
            }
            journal.Enqueue(completed);
            await journal.FlushAsync();
            UnauthorizedAccessException error =
                Assert.IsType<UnauthorizedAccessException>(journal.LastError);
            recordedError = error;
            Assert.Equal(5, error.HResult & 0xffff);
            Assert.True(journal.HasPending);
            Assert.Equal(previous, await File.ReadAllBytesAsync(path));
            Assert.Equal(accepted.Revision,
                storage.ReadHistoryRecord(accepted.OccurrenceId).Revision);
            Assert.Empty(Directory.EnumerateFiles(history, ".history-*.tmp"));
        }
        await journal.FlushAsync();
        Assert.False(journal.HasPending);
        Assert.Same(recordedError, journal.LastError);
        SchedulerHistoryRecord saved = storage.ReadHistoryRecord(accepted.OccurrenceId);
        Assert.Equal(completed.Revision, saved.Revision);
        Assert.Equal(7, saved.ExitCode);
        Assert.Equal(SchedulerExecutionOutcome.Failed, saved.Outcome);
        Assert.True(saved.Started && saved.ResourcesRecovered && !saved.IsProtected);
        Assert.Equal(completed.Arguments, saved.Arguments);
        Assert.Equal(completed.EndedAt, saved.EndedAt);
        // 別の保存操作でも、拒否後の管理領域の排他を取得できることを確認します。
        await WriteLog(storage, accepted.OccurrenceId, [1, 2, 3]);
    }

    [Fact]
    public async Task LogFolderDenialPreservesExistingLogsAndReleasesLease()
    {
        var storage = new SchedulerManagedStorage(directory);
        SchedulerHistoryRecord running = Capture();
        await storage.SaveHistoryAsync(running);
        await WriteLog(storage, running.OccurrenceId, [1, 2, 3]);
        string path = storage.LogPath(running.OccurrenceId);
        string logs = Path.GetDirectoryName(path)!;
        var next = Guid.NewGuid();
        using (var permission = new FolderWriteDenial(logs, inherit: true))
        {
            permission.Apply();
            AssertCreateDenied(logs);
            UnauthorizedAccessException error = await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => WriteLog(storage, next, [4, 5, 6]));
            Assert.Equal(5, error.HResult & 0xffff);
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
            Assert.False(File.Exists(storage.LogPath(next)));
            Assert.True(storage.ReadHistoryRecord(running.OccurrenceId).IsProtected);
            // ログへの書込み拒否が、別フォルダーの履歴更新を妨げないことを確認します。
            await storage.SaveHistoryAsync(running.MarkStarted(Accepted.AddSeconds(1)));
        }
        await WriteLog(storage, next, [4, 5, 6]);
        Assert.Equal(new byte[] { 4, 5, 6 },
            await File.ReadAllBytesAsync(storage.LogPath(next)));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
        Assert.True(storage.ReadHistoryRecord(running.OccurrenceId).IsProtected);
    }

    private SchedulerHistoryRecord Capture()
    {
        var entry = new SchedulerEntry(Guid.NewGuid(), "ACL保存拒否",
            Path.Combine(directory, "main.uws"), ["", "a b", "日本語"],
            SchedulerExecutionMode.SeparateProcess,
            new SchedulerStartupCondition(), directory);
        return SchedulerHistoryRecord.Capture(new SchedulerDispatch(Guid.NewGuid(),
            new SchedulerExecutionRequest(Guid.NewGuid(), entry, Accepted)));
    }

    private static void AssertCreateDenied(string folder)
    {
        string probe = Path.Combine(folder, "write-probe.tmp");
        UnauthorizedAccessException error = Assert.Throws<UnauthorizedAccessException>(() =>
        {
            using var stream = new FileStream(probe, FileMode.CreateNew,
                FileAccess.Write, FileShare.None);
        });
        Assert.Equal(5, error.HResult & 0xffff);
        Assert.False(File.Exists(probe));
    }

    private static async Task WriteLog(SchedulerManagedStorage storage,
        Guid id, byte[] content)
    {
        using IDisposable lease = await storage.AcquireDefaultSaveAsync(
            storage.LogPath(id), content.Length, default);
        await File.WriteAllBytesAsync(storage.LogPath(id), content);
    }

    private sealed class FolderWriteDenial : IDisposable
    {
        private readonly DirectoryInfo folder;
        private readonly byte[] original;
        private readonly FileSystemAccessRule rule;

        internal FolderWriteDenial(string path, bool inherit)
        {
            folder = new DirectoryInfo(path);
            original = folder.GetAccessControl(AccessControlSections.Access)
                .GetSecurityDescriptorBinaryForm();
            using var identity = WindowsIdentity.GetCurrent();
            SecurityIdentifier user = identity.User
                ?? throw new InvalidOperationException("実行ユーザーのSIDを取得できません。");
            rule = new FileSystemAccessRule(user,
                inherit ? FileSystemRights.Write : FileSystemRights.CreateFiles,
                inherit ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
                    : InheritanceFlags.None,
                PropagationFlags.None, AccessControlType.Deny);
        }

        internal void Apply()
        {
            Assert.False((folder.Attributes & FileAttributes.ReparsePoint) != 0);
            DirectorySecurity security = folder.GetAccessControl(AccessControlSections.Access);
            security.AddAccessRule(rule);
            folder.SetAccessControl(security);
            Assert.Contains(folder.GetAccessControl(AccessControlSections.Access)
                .GetAccessRules(true, false, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>(), actual =>
                    actual.IdentityReference == rule.IdentityReference
                    && actual.AccessControlType == AccessControlType.Deny
                    && (actual.FileSystemRights & rule.FileSystemRights) == rule.FileSystemRights
                    && actual.InheritanceFlags == rule.InheritanceFlags);
        }

        public void Dispose()
        {
            var restored = new DirectorySecurity();
            restored.SetSecurityDescriptorBinaryForm(original, AccessControlSections.Access);
            folder.SetAccessControl(restored);
            Assert.Equal(ComparableAccessDescriptor(restored),
                ComparableAccessDescriptor(
                    folder.GetAccessControl(AccessControlSections.Access)));
        }

        private static string ComparableAccessDescriptor(
            DirectorySecurity security)
        {
            var descriptor = new RawSecurityDescriptor(
                security.GetSecurityDescriptorBinaryForm(), 0);
            // Windowsが設定時に更新する自動継承済みフラグだけを比較から除きます。
            // 権限エントリー、継承指定、保護状態はそのまま比較します。
            descriptor.SetFlags(descriptor.ControlFlags
                & ~ControlFlags.DiscretionaryAclAutoInherited);
            return descriptor.GetSddlForm(AccessControlSections.Access);
        }
    }
}
