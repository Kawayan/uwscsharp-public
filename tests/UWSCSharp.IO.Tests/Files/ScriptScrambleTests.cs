using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.IO.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ScriptScrambleTests : IDisposable
{
    private readonly string directory =
        Directory.CreateTempSubdirectory("uwsc-scramble-save-").FullName;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void WritesUws05AndPreservesOriginalBytes(bool inPlace, bool bigEndian)
    {
        string input = Path.Combine(directory, "input.uws");
        string output = Path.Combine(directory, "output.uws");
        Encoding encoding = bigEndian ? Encoding.BigEndianUnicode : new UTF8Encoding(false);
        File.WriteAllText(input, "PRINT \"日本語😀\"\n\nPRINT 42", encoding);
        byte[] original = File.ReadAllBytes(input);

        ScriptScramble.ConvertFile(input, inPlace ? null : output);

        string originalPath = inPlace ? Path.ChangeExtension(input, ".org") : input;
        Assert.Equal(original, File.ReadAllBytes(originalPath));
        Assert.Equal("PRINT \"日本語😀\"\r\n\r\nPRINT 42\r\n",
            Source.ReadFile(inPlace ? input : output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KeepsInputAndExistingDestinationOnCollision(bool inPlace)
    {
        string input = Path.Combine(directory, "input.uws");
        string existing = inPlace ? Path.ChangeExtension(input, ".org")
            : Path.Combine(directory, "output.uws");
        File.WriteAllText(input, "PRINT 42");
        File.WriteAllText(existing, "既存ファイル");

        IOException failure = Assert.ThrowsAny<IOException>(() =>
            ScriptScramble.ConvertFile(input, inPlace ? null : existing));
        Assert.Contains(existing, failure.Message);

        Assert.Equal("PRINT 42", File.ReadAllText(input));
        Assert.Equal("既存ファイル", File.ReadAllText(existing));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void KeepsReadOnlyInputAndBackupWhenReplacementFails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string input = Path.Combine(directory, "input.uws");
        string backup = Path.ChangeExtension(input, ".org");
        byte[] original = Encoding.UTF8.GetBytes("PRINT 42");
        File.WriteAllBytes(input, original);
        File.SetAttributes(input, FileAttributes.ReadOnly);
        try
        {
            Exception? failure = Record.Exception(() =>
                ScriptScramble.ConvertFile(input));
            Assert.True(failure is IOException or UnauthorizedAccessException);
            Assert.NotNull(failure);
            Assert.Contains("対象: " + input, failure.Message);
            Assert.Contains("退避先: " + backup, failure.Message);
            Assert.True(failure.InnerException is IOException
                or UnauthorizedAccessException);
            Assert.Equal(original, File.ReadAllBytes(input));
            Assert.Equal(original, File.ReadAllBytes(backup));
            Assert.True(File.GetAttributes(input).HasFlag(FileAttributes.ReadOnly));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            File.SetAttributes(input, FileAttributes.Normal);
        }
    }

    [Fact]
    public void KeepsInputWhenAccessControlDeniesCreatingTheOutput()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string input = Path.Combine(directory, "input.uws");
        DirectoryInfo output = Directory.CreateDirectory(Path.Combine(directory, "output"));
        File.WriteAllText(input, "PRINT 42");
        using var identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = identity.User
            ?? throw new InvalidOperationException("ユーザーのSIDを取得できません。");
        DirectorySecurity restricted = output.GetAccessControl(AccessControlSections.Access);
        var rule = new FileSystemAccessRule(
            user, FileSystemRights.CreateFiles, AccessControlType.Deny);
        restricted.AddAccessRule(rule);
        output.SetAccessControl(restricted);
        try
        {
            string destination = Path.Combine(output.FullName, "出力.uws");
            UnauthorizedAccessException failure =
                Assert.Throws<UnauthorizedAccessException>(() =>
                    ScriptScramble.ConvertFile(input, destination));
            Assert.Contains("保存先: " + destination, failure.Message);
            Assert.IsType<UnauthorizedAccessException>(failure.InnerException);
            Assert.Equal("PRINT 42", File.ReadAllText(input));
            Assert.Empty(output.EnumerateFileSystemInfos());
        }
        finally
        {
            restricted.RemoveAccessRuleSpecific(rule);
            output.SetAccessControl(restricted);
        }
    }

    [Fact]
    public void KeepsInputAndBackupWhenAccessControlDeniesReplacement()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var input = new FileInfo(Path.Combine(directory, "input.uws"));
        string backup = Path.ChangeExtension(input.FullName, ".org");
        byte[] original = "PRINT 42"u8.ToArray();
        File.WriteAllBytes(input.FullName, original);
        using var identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = identity.User
            ?? throw new InvalidOperationException("ユーザーのSIDを取得できません。");
        FileSecurity security = input.GetAccessControl(AccessControlSections.Access);
        var rule = new FileSystemAccessRule(
            user, FileSystemRights.Delete, AccessControlType.Deny);
        var parent = new DirectoryInfo(directory);
        DirectorySecurity parentSecurity = parent.GetAccessControl(AccessControlSections.Access);
        var parentRule = new FileSystemAccessRule(
            user, FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Deny);
        parentSecurity.AddAccessRule(parentRule);
        security.AddAccessRule(rule);
        input.SetAccessControl(security);
        try
        {
            parent.SetAccessControl(parentSecurity);
            UnauthorizedAccessException failure =
                Assert.Throws<UnauthorizedAccessException>(() =>
                ScriptScramble.ConvertFile(input.FullName));
            Assert.Contains("対象: " + input.FullName, failure.Message);
            Assert.Contains("退避先: " + backup, failure.Message);
            Assert.Equal(original, File.ReadAllBytes(input.FullName));
            Assert.Equal(original, File.ReadAllBytes(backup));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            parentSecurity.RemoveAccessRuleSpecific(parentRule);
            parent.SetAccessControl(parentSecurity);
            security.RemoveAccessRuleSpecific(rule);
            input.SetAccessControl(security);
        }
    }

    [Fact]
    public async Task ConcurrentConversionsKeepOneBackupAndOneResult()
    {
        string input = Path.Combine(directory, "input.uws");
        byte[] original = "PRINT 42"u8.ToArray();
        File.WriteAllBytes(input, original);
        var start = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Exception?> first = ConvertAfterStartAsync();
        Task<Exception?> second = ConvertAfterStartAsync();
        start.SetResult();

        Exception?[] results = await Task.WhenAll(first, second);
        Assert.Single(results, error => error is null);
        Assert.IsAssignableFrom<IOException>(
            Assert.Single(results, error => error is not null));
        Assert.Equal(original,
            File.ReadAllBytes(Path.ChangeExtension(input, ".org")));
        Assert.Equal("PRINT 42\r\n", Source.ReadFile(input));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));

        async Task<Exception?> ConvertAfterStartAsync()
        {
            await start.Task;
            return Record.Exception(() => ScriptScramble.ConvertFile(input));
        }
    }

    [Fact]
    public void CanRetryToSeparateOutputAfterInPlaceReplacementFails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string input = Path.Combine(directory, "input.uws");
        string backup = Path.ChangeExtension(input, ".org");
        string output = Path.Combine(directory, "converted.uws");
        byte[] original = "PRINT 42"u8.ToArray();
        File.WriteAllBytes(input, original);
        using (FileStream locked = File.Open(input, FileMode.Open,
            FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(() => ScriptScramble.ConvertFile(input));
        }

        Assert.ThrowsAny<IOException>(() => ScriptScramble.ConvertFile(input));
        ScriptScramble.ConvertFile(input, output);

        Assert.Equal(original, File.ReadAllBytes(input));
        Assert.Equal(original, File.ReadAllBytes(backup));
        Assert.Equal("PRINT 42\r\n", Source.ReadFile(output));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void CanRecoverWhenBackupAndTemporaryOutputRemain()
    {
        string input = Path.Combine(directory, "input.uws");
        string backup = Path.ChangeExtension(input, ".org");
        string temporary = Path.Combine(directory,
            ".uwsc-scramble-" + Guid.NewGuid().ToString("N") + ".tmp");
        string output = Path.Combine(directory, "converted.uws");
        byte[] original = "PRINT 42\r\n"u8.ToArray();
        File.WriteAllBytes(input, original);
        File.WriteAllBytes(backup, original);
        ScriptScramble.ConvertFile(input, temporary);
        byte[] staged = File.ReadAllBytes(temporary);

        Assert.ThrowsAny<IOException>(() => ScriptScramble.ConvertFile(input));
        ScriptScramble.ConvertFile(input, output);

        Assert.Equal(original, File.ReadAllBytes(input));
        Assert.Equal(original, File.ReadAllBytes(backup));
        Assert.Equal(staged, File.ReadAllBytes(temporary));
        Assert.Equal("PRINT 42\r\n", Source.ReadFile(output));
        Assert.Equal([temporary], Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void KeepsInputWhenOutputDirectoryIsMissing()
    {
        string input = Path.Combine(directory, "input.uws");
        string output = Path.Combine(directory, "missing", "output.uws");
        File.WriteAllText(input, "PRINT 42");

        IOException failure = Assert.Throws<IOException>(() =>
            ScriptScramble.ConvertFile(input, output));
        Assert.Contains(output, failure.Message, StringComparison.Ordinal);
        Assert.IsType<DirectoryNotFoundException>(failure.InnerException);

        Assert.Equal("PRINT 42", File.ReadAllText(input));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KeepsInputWhenEveryScrambleSeedCollides(bool inPlace)
    {
        const string source = "\u550f\u5561\u554a\u5577\u557b\u5560\u550f\n"
            + "\u550e\u5561\u5543\u557a\u5578\u5565\u5502\n"
            + "\u5501\u5567\u5576\u5571\u557e\u5563\u550d\n"
            + "\u5501\u556b\u5546\u557f\u557b\u5560\u5504\n"
            + "\u5502\u556c\u5579\u557a\u5573\u556f\u5502\n"
            + "\u550c\u556d\u5543\u557d\u557a\u5561\u5500\n"
            + "\u5501\u5567\u554b\u5571\u557c\u5563\u550d\n"
            + "\u550d\u5578\u5548\u557f\u5573\u556e\u550f\n"
            + "\u550f\u556e\u5579\u557c\u5573\u556a\u5504";
        string input = Path.Combine(directory, "input.uws");
        string output = Path.Combine(directory, "output.uws");
        File.WriteAllText(input, source, Encoding.Unicode);
        byte[] original = File.ReadAllBytes(input);

        IOException failure = Assert.Throws<IOException>(() =>
            ScriptScramble.ConvertFile(input, inPlace ? null : output));

        Assert.Contains("すべての初期位置で衝突", failure.Message);
        Assert.Equal(original, File.ReadAllBytes(input));
        Assert.Equal([input], Directory.GetFiles(directory));
    }

    [Theory]
    [InlineData(false, "82", "文字コード")]
    [InlineData(true, "82", "文字コード")]
    [InlineData(false, "EFBBBFC3", "文字コード")]
    [InlineData(true, "EFBBBFC3", "文字コード")]
    [InlineData(false, "FEFF00", "UTF-16BE")]
    [InlineData(true, "FEFFD800", "UTF-16BE")]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public void ReportsInputPathWhenDecodingFails(
        bool inPlace, string inputHex, string encoding)
    {
        string input = Path.Combine(directory, "不正な入力.uws");
        string output = Path.Combine(directory, "output.uws");
        byte[] original = Convert.FromHexString(inputHex);
        File.WriteAllBytes(input, original);

        IOException failure = Assert.Throws<IOException>(() =>
            ScriptScramble.ConvertFile(input, inPlace ? null : output));

        Assert.Contains(encoding, failure.Message);
        Assert.Contains(input, failure.Message, StringComparison.Ordinal);
        Assert.IsType<DecoderFallbackException>(failure.InnerException);
        Assert.Equal(original, File.ReadAllBytes(input));
        Assert.Equal([input], Directory.GetFiles(directory));
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
