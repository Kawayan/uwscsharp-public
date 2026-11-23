using System.Diagnostics;
using System.Text;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class ByteTextLanguageCpuProcessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CliAndWpfRunByteTextLanguageAndCpuFeaturesWithCallAndUnicodePaths(bool wpf)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null
            && !File.Exists(Path.Combine(root.FullName, "UWSCSharp.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        string configuration = Path.GetFileName(
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        string name = wpf ? "UWSCSharp.Wpf" : "UWSCSharp.Cli";
        string binaryDirectory = Path.Combine(root.FullName,
            "artifacts", "bin", name, configuration);
        string executable = Path.Combine(binaryDirectory, name + ".exe");
        Assert.True(File.Exists(executable), executable);
        string directory = Path.Combine(Path.GetTempPath(),
            "uwsc-b2-process-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            string receipt = Path.Combine(directory, "result.txt");
            string main = Path.Combine(directory, "日本語 main.uws");
            await File.WriteAllTextAsync(Path.Combine(directory, "child.uws"), """
                TEXTBLOCK note
                  CALL // _ ; "raw"
                ENDTEXTBLOCK
                log=log+"|"+note
                """, new UTF8Encoding(true));
            await File.WriteAllTextAsync(main, """
                OPTION SHORTCIRCUIT
                PUBLIC log="", calls=0
                TEXTBLOCK sample
                Aあ
                ENDTEXTBLOCK
                WITH SampleModule
                    log=.value+"|"
                ENDWITH
                IF FALSE AND ProbeSideEffect() THEN PRINT "bad"
                log=log+LENGTHB(sample)+"|"+COPYB(sample,2,2)+"|"+POSB("あ",sample)+"|"
                log=log+ASCB("あ")+"|"+CHRB(65)+"|"+STRCONV("ｶﾞ",SC_FULLWIDTH+SC_HIRAGANA)+"|"
                log=log+DECODE(ENCODE(sample,CODE_UTF8),CODE_UTF8)+"|"
                log=log+DECODE(ENCODE(sample,CODE_BYTEARRAY),CODE_BYTEARRAY)+"|"
                log=log+DECODE(ENCODE(sample,CODE_BYTEARRAYW),CODE_BYTEARRAYW)+"|"
                log=log+ENCODE(sample,CODE_URL)+"|"+DECODE("&lt;",CODE_HTML)+"|"
                log=log+CPUUSERATE()+"|"+calls
                CALL child
                DIM f=FOPEN("result.txt",F_WRITE8)
                FPUT(f,log,0)
                FCLOSE(f)
                FUNCTION ProbeSideEffect()
                    calls=calls+1
                    RESULT=TRUE
                FEND
                MODULE SampleModule
                    PUBLIC value=3
                ENDMODULE
                """, new UTF8Encoding(true));
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = binaryDirectory
            };
            start.ArgumentList.Add(main);
            using Process process = Process.Start(start)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync()
                    .WaitAsync(TimeSpan.FromSeconds(45));
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }

            Assert.True(process.ExitCode == 0,
                $"終了コード: {process.ExitCode}\n{await stdout}\n{await stderr}");
            Assert.Equal("3|3|あ|2|130|A|が|Aあ|Aあ|Aあ|A%82%A0|<|0|0|  CALL // _ ; \"raw\"",
                (await File.ReadAllTextAsync(receipt)).TrimEnd('\r', '\n'));
        }
        finally
        {
            // 所有process終了後に、この試験で作成したファイルだけを削除します。
            Directory.Delete(directory, recursive: true);
        }
    }
}
