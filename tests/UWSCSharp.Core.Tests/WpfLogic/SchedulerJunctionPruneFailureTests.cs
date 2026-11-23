using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

/// <summary>
/// 専用一時領域の実ジャンクションと削除拒否を使い、画面や入力を操作しません。
/// </summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerJunctionPruneFailureTests : IDisposable
{
    private static readonly DateTimeOffset Accepted = new(2027, 1, 2, 3, 4, 0, TimeSpan.Zero);
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "uwsc-junction-prune-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(directory, "owned");
    private string Logs => Path.Combine(Root, "managed", "logs");
    private string Backup => Path.Combine(directory, "saved-logs");
    private string Target => Path.Combine(directory, "outside");

    public SchedulerJunctionPruneFailureTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        RestoreLogs();
        Directory.Delete(directory, true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteFailureThenJunctionCannotPruneOutsideLogs(bool historyPrune)
    {
        var storage = new SchedulerManagedStorage(Root, 10);
        SchedulerHistoryRecord oldest = Capture(0, completed: true);
        SchedulerHistoryRecord running = Capture(1, completed: false);
        SchedulerHistoryRecord next = Capture(200, completed: true);
        await storage.SaveHistoryAsync(oldest);
        await storage.SaveHistoryAsync(running);
        if (historyPrune)
        {
            for (int index = 1; index < 100; index++)
            {
                await storage.SaveHistoryAsync(Capture(index, completed: true));
            }
        }
        await WriteLog(storage, oldest.OccurrenceId, [1, 2, 3, 4, 5, 6]);
        await WriteLog(storage, running.OccurrenceId, [7, 8, 9, 10]);
        string ownedLog = storage.LogPath(oldest.OccurrenceId);
        Directory.CreateDirectory(Target);
        string outsideOldest = Path.Combine(Target, Path.GetFileName(ownedLog));
        string outsideRunning = Path.Combine(Target, Path.GetFileName(storage.LogPath(running.OccurrenceId)));
        await File.WriteAllBytesAsync(outsideOldest, [11, 12, 13, 14, 15, 16]);
        await File.WriteAllBytesAsync(outsideRunning, [17, 18, 19, 20]);
        File.SetAttributes(ownedLog, File.GetAttributes(ownedLog) | FileAttributes.ReadOnly);
        try
        {
            UnauthorizedAccessException denied = await Assert.ThrowsAsync<UnauthorizedAccessException>(RequestSave);
            Assert.Equal(5, denied.HResult & 0xffff);
            Assert.Equal(historyPrune ? 102 : 2, storage.LoadHistory().Count);
            Assert.True(storage.ReadHistoryRecord(running.OccurrenceId).IsProtected);
            AssertPrivatePath(Logs);
            AssertPrivatePath(Backup);
            Directory.Move(Logs, Backup);
            CreateJunction(Logs, Target);
            IOException blocked = await Assert.ThrowsAsync<IOException>(RequestSave);
            Assert.Contains("リンクされた予約管理領域", blocked.Message);
            Assert.Equal(new byte[] { 11, 12, 13, 14, 15, 16 }, await File.ReadAllBytesAsync(outsideOldest));
            Assert.Equal(new byte[] { 17, 18, 19, 20 }, await File.ReadAllBytesAsync(outsideRunning));
            Assert.False(File.Exists(Path.Combine(Target, next.OccurrenceId.ToString("N") + ".log")));
            Assert.Equal(historyPrune ? 102 : 2, storage.LoadHistory().Count);
        }
        finally
        {
            RestoreLogs();
            File.SetAttributes(ownedLog, File.GetAttributes(ownedLog) & ~FileAttributes.ReadOnly);
        }
        await RequestSave();
        Assert.False(File.Exists(ownedLog));
        Assert.Equal(new byte[] { 7, 8, 9, 10 }, await File.ReadAllBytesAsync(storage.LogPath(running.OccurrenceId)));
        Assert.True(storage.ReadHistoryRecord(running.OccurrenceId).IsProtected);
        Assert.Equal(historyPrune ? 101 : 2, storage.LoadHistory().Count);
        Assert.Equal(new byte[] { 11, 12, 13, 14, 15, 16 }, await File.ReadAllBytesAsync(outsideOldest));
        Assert.Equal(new byte[] { 17, 18, 19, 20 }, await File.ReadAllBytesAsync(outsideRunning));
        if (historyPrune)
        {
            Assert.DoesNotContain(storage.LoadHistory(), record => record.OccurrenceId == oldest.OccurrenceId);
            Assert.Equal(2, Directory.GetFiles(Target).Length);
        }
        else
        {
            await WriteLog(storage, next.OccurrenceId, [21, 22, 23, 24]);
            Assert.Equal(4, new FileInfo(storage.LogPath(next.OccurrenceId)).Length);
        }

        async Task RequestSave()
        {
            if (historyPrune)
            {
                await storage.SaveHistoryAsync(next);
            }
            else
            {
                using IDisposable ownership = await storage.AcquireDefaultSaveAsync(storage.LogPath(next.OccurrenceId), 4, default);
            }
        }
    }

    private SchedulerHistoryRecord Capture(int seconds, bool completed)
    {
        var entry = new SchedulerEntry(Guid.NewGuid(), "整理先の保護", Path.Combine(directory, "main.uws"), [],
            SchedulerExecutionMode.SeparateProcess, new SchedulerStartupCondition(), directory);
        var record = SchedulerHistoryRecord.Capture(new SchedulerDispatch(Guid.NewGuid(),
            new SchedulerExecutionRequest(Guid.NewGuid(), entry, Accepted.AddSeconds(seconds))));
        return completed ? record.Complete(SchedulerExecutionResult.Completed(0, true, false, true),
            Accepted.AddSeconds(seconds + 1), endedConfirmed: true) : record;
    }

    private static async Task WriteLog(SchedulerManagedStorage storage, Guid id, byte[] bytes)
    {
        using IDisposable ownership = await storage.AcquireDefaultSaveAsync(storage.LogPath(id), bytes.Length, default);
        await File.WriteAllBytesAsync(storage.LogPath(id), bytes);
    }

    private void RestoreLogs()
    {
        AssertPrivatePath(Logs);
        AssertPrivatePath(Backup);
        if (Directory.Exists(Backup))
        {
            if (Directory.Exists(Logs))
            {
                // 再帰削除せず、ジャンクション自身か作成途中の空フォルダーだけを除きます。
                Directory.Delete(Logs);
            }
            Directory.Move(Backup, Logs);
        }
    }

    private void AssertPrivatePath(string path) => Assert.StartsWith(
        Path.GetFullPath(directory) + Path.DirectorySeparatorChar, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);

    private void CreateJunction(string link, string target)
    {
        AssertPrivatePath(link);
        AssertPrivatePath(target);
        Directory.CreateDirectory(link);
        byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
        byte[] print = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        byte[] buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xA0000003);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), checked((ushort)(buffer.Length - 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), checked((ushort)substitute.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), checked((ushort)(substitute.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), checked((ushort)print.Length));
        substitute.CopyTo(buffer, 16);
        print.CopyTo(buffer, 16 + substitute.Length + 2);
        using SafeFileHandle handle = CreateFileW(link, 0x40000000, 7, 0, 3, 0x02200000, 0);
        if (handle.IsInvalid || !DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, null, 0, out _, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        byte[] actual = new byte[16 * 1024];
        Assert.True(DeviceIoControl(handle, 0x000900A8, null, 0, actual, actual.Length, out _, 0));
        Assert.Equal(0xA0000003u, BinaryPrimitives.ReadUInt32LittleEndian(actual));
        Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint));
        Assert.Equal(Path.GetFullPath(target), Directory.ResolveLinkTarget(link, false)!.FullName, ignoreCase: true);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing,
        nint security, uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code,
        [In] byte[]? input, int inputSize, [Out] byte[]? output, int outputSize, out uint returned, nint overlapped);
}
