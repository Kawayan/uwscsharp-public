using System.Text;
using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class ScriptFileStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "UWSC保存 日本語-" + Guid.NewGuid().ToString("N"));

    public ScriptFileStoreTests()
    {
        Directory.CreateDirectory(directory);
    }

    [Fact]
    public void NewAndExistingScriptAreSavedWithUtf8Bom()
    {
        string path = Path.Combine(directory, "記録.uws");
        const string source = "PRINT \"日本語 \U0001F600\"";
        ScriptFileStore.Save(path, source);
        Assert.Equal([0xef, 0xbb, 0xbf], File.ReadAllBytes(path).Take(3));
        Assert.Equal(source, Source.ReadFile(path));
        ScriptFileStore.Save(path, "KBD(65,1)\r\nKBD(65,2)\r\n");
        Assert.Equal("KBD(65,1)\r\nKBD(65,2)\r\n", Source.ReadFile(path));
        Assert.Single(Directory.GetFiles(directory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public void InvalidUnicodeFromScrambledScriptCannotReplaceDestination(
        bool existingDestination)
    {
        string input = Path.Combine(directory, "入力.uws");
        string destination = Path.Combine(directory, "保存先.uws");
        const string text = "// \ud800\r\n";
        byte[] encoded = ScrambledScript.EncodeVerified(text, 0);
        File.WriteAllBytes(input, encoded);
        byte[] original = Encoding.UTF8.GetBytes("既存の内容");
        if (existingDestination)
        {
            File.WriteAllBytes(destination, original);
        }

        string decoded = Source.ReadFile(input);
        Assert.Equal(text, decoded);
        IOException error = Assert.Throws<IOException>(
            () => ScriptFileStore.Save(destination, decoded));
        Assert.IsType<EncoderFallbackException>(error.InnerException);
        Assert.Contains(destination, error.Message);
        Assert.Equal(encoded, File.ReadAllBytes(input));
        if (existingDestination)
        {
            Assert.Equal(original, File.ReadAllBytes(destination));
        }
        else
        {
            Assert.False(File.Exists(destination));
        }

        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void LockedDestinationPreservesOriginalBytesAndRemovesTemporaryFile()
    {
        string path = Path.Combine(directory, "記録.uws");
        byte[] original = Encoding.Unicode.GetBytes("元の記録");
        File.WriteAllBytes(path, original);
        using (var locked = new FileStream(path, FileMode.Open,
            FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => ScriptFileStore.Save(path, "置換内容"));
        }

        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(directory));
    }

    [Fact]
    public void DirectoryDestinationIsNotOverwrittenAndTemporaryFileIsRemoved()
    {
        string path = Path.Combine(directory, "保存先.uws");
        Directory.CreateDirectory(path);
        Assert.ThrowsAny<IOException>(() => ScriptFileStore.Save(path, "記録"));
        Assert.True(Directory.Exists(path));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public void MissingParentDoesNotCreateFileOrTemporaryFile()
    {
        string path = Path.Combine(directory, "存在しない", "記録.uws");
        Assert.Throws<DirectoryNotFoundException>(() => ScriptFileStore.Save(path, "内容"));
        Assert.Empty(Directory.GetFileSystemEntries(directory));
    }

    [Fact]
    public void ReadOnlyDestinationPreservesBytesAndRemovesTemporaryFile()
    {
        string path = Path.Combine(directory, "読み取り専用.uws");
        File.WriteAllText(path, "元の内容");
        byte[] original = File.ReadAllBytes(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => ScriptFileStore.Save(path, "変更"));
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    /// <summary>
    /// 試験で作成したファイルとフォルダーを解放します。
    /// </summary>
    public void Dispose() => Directory.Delete(directory, recursive: true);
}
