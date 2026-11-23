using System.Text;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ScrambledScriptTests : IDisposable
{
    private readonly string directory =
        Directory.CreateTempSubdirectory("uwsc-scramble-read-").FullName;

    private const string OriginalSample =
        "FFFE550057005300300035000D000A00320039003700320038000D000A00"
        + "39556C552055225506551A552555835581552F553355415538554755265528557F"
        + "552D553555285582557D551955135506550E551F550E55165584550D000A0015"
        + "5510550C550455895536557E5581551C65785587550D000A001C551A5524551E"
        + "550D5516558955335588550D000A00";

    [Fact]
    public void ReadsAnOriginalUnicodeSample()
    {
        string path = Write(Convert.FromHexString(OriginalSample));

        Assert.Equal("f=FOPEN(\"receipt.txt\",F_WRITE)\r\n"
            + "FPUT(f,\"あ\")\r\nFCLOSE(f)\r\n", Source.ReadFile(path));
    }

    [Fact]
    public void RejectsAnOriginalSampleWithAnAlteredBody()
    {
        byte[] bytes = Convert.FromHexString(OriginalSample);
        bytes[30] ^= 1;
        string path = Write(bytes);

        IOException error = Assert.Throws<IOException>(() => Source.ReadFile(path));
        Assert.Contains("検査値", error.Message);
    }

    [Fact]
    public void RejectsAnIncompleteCodeUnit()
    {
        byte[] bytes = Convert.FromHexString(OriginalSample);
        string path = Write(bytes[..^1]);

        Assert.Throws<IOException>(() => Source.ReadFile(path));
    }

    [Theory]
    [InlineData("0", "")]
    [InlineData("-01", "")]
    [InlineData("655360", "")]
    [InlineData("0\00", "")]
    [InlineData("00", "\0\r\n")]
    [InlineData("100", "\n\r\n")]
    [InlineData("130", "\r\r\n")]
    [InlineData("260", "\x1a\r\n")]
    [InlineData("00", "途中で終わる")]
    public void RejectsInvalidMetadataOrLineEnding(string metadata, string body)
    {
        string path = Write(Encoding.Unicode.GetBytes(
            "\ufeffUWS05\r\n" + metadata + "\r\n" + body));

        Assert.Throws<IOException>(() => Source.ReadFile(path));
    }

    [Fact]
    public void ReadsEscapesAndUnpairedEncodedCodeUnits()
    {
        const string sample = "FFFE550057005300300035000D000A00310037003800350030000D"
            + "000A005F003C002300300030003E005F005F003C00230031003000"
            + "3E005F005F003C002300310033003E005F005F003C002300320036"
            + "003E005F0000D80D000A00";
        string path = Write(Convert.FromHexString(sample));

        Assert.Equal("\uaaae\u5557\u555c\u5559\u82ab\r\n", Source.ReadFile(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunsScrambledCallTarget(bool dynamicCall)
    {
        const string body = "\ufeffUWS05\r\n5120\r\n"
            + "\u550a\u550f\u5520\u5529\u5503\u557e\u557c\u6521\u5580\r\n";
        string child = Write(Encoding.Unicode.GetBytes(body));
        string main = Path.Combine(directory, "main.uws");
        File.WriteAllText(main, dynamicCall
            ? "EVAL(\"CALL child.uws\")"
            : "CALL \"" + Path.GetFileName(child) + "\"");
        ScriptProgram program = ScriptCompiler.CompileFile(main);
        var output = new List<string>();

        await new Interpreter().RunAsync(program, new CallbackScriptHost(output.Add));

        Assert.Equal(["あ"], output);
    }

    [Fact]
    public void TriesAnotherSeedWhenBodyLooksLikeAnEscape()
    {
        const string source = "\u550f\u5561\u554a\u5577\u557b\u5560\u550f";
        Assert.NotEqual(source + "\r\n",
            ScrambledScript.Decode(ScrambledScript.Encode(source, 0)));

        byte[] encoded = ScrambledScript.EncodeVerified(source, 0);

        Assert.Equal(source + "\r\n", ScrambledScript.Decode(encoded));
    }

    [Fact]
    public void RejectsBodyThatCollidesAtEverySeed()
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

        Assert.Throws<IOException>(() => ScrambledScript.EncodeVerified(source, 0));
    }

    private string Write(byte[] bytes)
    {
        string path = Path.Combine(directory, "child.uws");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
