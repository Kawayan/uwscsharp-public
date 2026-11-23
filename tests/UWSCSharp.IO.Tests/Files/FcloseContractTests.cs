using System.Globalization;
using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.IO.Tests;

public sealed class FcloseContractTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), "UWSCSharp-fclose-" + Guid.NewGuid());

    public FcloseContractTests()
    {
        Directory.CreateDirectory(directory);
    }

    public void Dispose()
    {
        // このfixtureが作成した専用ディレクトリだけを削除します。
        Directory.Delete(directory, true);
    }

    private static Value N(double value) => Value.Numeric(value);

    private static Value S(string value) => Value.String(value);

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    public async Task FiniteConversionAndLongStringsUseInvariantRules(
        string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            await using IScriptFileSession session =
                new ScriptFiles().CreateSession();
            string path = Path.Combine(directory, "data.txt");
            Value id = await session.InvokeAsync("FOPEN",
                [S(path), N(16)], default);
            await session.InvokeAsync("FPUT", [id, S("saved")], default);
            Assert.Equal(N(1), await session.InvokeAsync("FCLOSE",
                [S(new string('0', 1000) + "1.9e+0"), S(" 0.9 ")], default));
            Assert.Equal("saved\r\n", File.ReadAllText(path));
            Assert.Equal(N(0), await session.InvokeAsync("FCLOSE",
                [N(id.Number + 0.9), N(1e300)], default));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(-2147483648)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(999)]
    [InlineData(2147483647)]
    public async Task UnknownRepresentableIdsFollowSuppression(int id)
    {
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        Assert.Equal(N(0), await session.InvokeAsync("FCLOSE",
            [N(id), N(-1)], default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.InvokeAsync("FCLOSE", [N(id)], default).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.InvokeAsync("FCLOSE", [N(id), N(-0.9)], default).AsTask());
    }

    [Theory]
    [InlineData(ValueKind.Null)]
    [InlineData(ValueKind.Nothing)]
    [InlineData(ValueKind.Error)]
    [InlineData(ValueKind.Array)]
    [InlineData(ValueKind.HashTable)]
    [InlineData(ValueKind.Object)]
    public async Task InvalidKindsDoNotCloseTheEntry(ValueKind kind)
    {
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        string path = Path.Combine(directory, "data.txt");
        Value id = await session.InvokeAsync("FOPEN",
            [S(path), N(16 | 512)], default);
        await session.InvokeAsync("FPUT", [id, S("saved")], default);
        foreach (Value[] arguments in new[]
        {
            new[] { new Value(kind), N(1) },
            [id, new Value(kind)]
        })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                session.InvokeAsync("FCLOSE", arguments, default).AsTask());
            Assert.Equal(S("saved"), await session.InvokeAsync("FGET",
                [id, N(1)], default));
        }

        await session.InvokeAsync("FCLOSE", [id], default);
        Assert.Equal("saved\r\n", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    [InlineData("$1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("")]
    public async Task InvalidIdsAreNotSuppressed(string value)
    {
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.InvokeAsync("FCLOSE", [S(value), N(1)], default).AsTask());
    }

    [Fact]
    public async Task EmptyUsesTheSharedZeroConversion()
    {
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        Assert.Equal(N(0), await session.InvokeAsync("FCLOSE",
            [Value.Empty, N(1)], default));
        string path = Path.Combine(directory, "data.txt");
        Value id = await session.InvokeAsync("FOPEN",
            [S(path), N(16)], default);
        Assert.Equal(N(1), await session.InvokeAsync("FCLOSE",
            [id, Value.Empty], default));
        Assert.Empty(File.ReadAllBytes(path));
    }

    [Fact]
    public async Task CancellationKeepsIdAndExclusiveLeaseForRetry()
    {
        string path = Path.Combine(directory, "data.txt");
        File.WriteAllText(path, "original");
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN",
            [S(path), N(16 | 512)], default);
        await session.InvokeAsync("FPUT", [id, S("saved"), N(1)], default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.InvokeAsync("FCLOSE", [id, N(1)],
                cancellation.Token).AsTask());
        Assert.Throws<IOException>(() => File.Open(path, FileMode.Open,
            FileAccess.ReadWrite, FileShare.None));
        Assert.Equal(N(1), await session.InvokeAsync("FCLOSE",
            [id, new(ValueKind.Omitted)], default));
        Assert.Equal("saved\r\n", File.ReadAllText(path));
        using FileStream reopened = File.Open(path, FileMode.Open,
            FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveFailureConsumesIdAndDisposalSavesOtherEntries(
        bool suppress)
    {
        string path = Path.Combine(directory, "blocked");
        string goodPath = Path.Combine(directory, "good.txt");
        IScriptFileSession session = new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN",
            [S(path), N(16)], default);
        Value good = await session.InvokeAsync("FOPEN",
            [S(goodPath), N(16)], default);
        await session.InvokeAsync("FPUT", [id, S("lost")], default);
        await session.InvokeAsync("FPUT", [good, S("other")], default);
        Directory.CreateDirectory(path);
        if (suppress)
        {
            Assert.Equal(N(0), await session.InvokeAsync("FCLOSE",
                [id, N(1)], default));
        }
        else
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                session.InvokeAsync("FCLOSE", [id], default).AsTask());
        }

        Directory.Delete(path);
        Assert.Equal(N(0), await session.InvokeAsync("FCLOSE",
            [id, N(1)], default));
        await session.DisposeAsync();
        await session.DisposeAsync();
        Assert.False(File.Exists(path));
        Assert.Equal("other\r\n", File.ReadAllText(goodPath));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonfiniteNumbersAreRejectedForBothArguments(double number)
    {
        Assert.Throws<InvalidOperationException>(() =>
            FileFunctions.Validate("FCLOSE",
                [new(ValueKind.Number, number), N(1)]));
        Assert.Throws<InvalidOperationException>(() =>
            FileFunctions.Validate("FCLOSE",
                [N(1), new(ValueKind.Number, number)]));
    }
}
