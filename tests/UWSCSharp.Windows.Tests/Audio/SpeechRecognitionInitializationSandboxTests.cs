using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using UWSCSharp.Core;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

// 音声入力を無効にしたゲストで初期化を調べ、認識開始は呼びません。
[SupportedOSPlatform("windows")]
[Trait("Category", "SandboxOnly")]
public sealed class SpeechRecognitionInitializationSandboxTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ProbeContextWithFixedWaveInput()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.wav");
        const int sampleRate = 16000;
        const int dataSize = sampleRate * sizeof(short);
        string stage = "CreateRecognizer";
        try
        {
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write("RIFF"u8);
                writer.Write(36 + dataSize);
                writer.Write("WAVEfmt "u8);
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(sampleRate);
                writer.Write(sampleRate * sizeof(short));
                writer.Write((short)sizeof(short));
                writer.Write((short)16);
                writer.Write("data"u8);
                writer.Write(dataSize);
                writer.Write(new byte[dataSize]);
            }

            await using var session = new ComSession();
            Value recognizer = await session.CreateAsync(
                "SAPI.SpInprocRecognizer", false, timeout.Token);
            stage = "OpenWave";
            Value stream = await session.CreateAsync("SAPI.SpFileStream",
                false, timeout.Token);
            await session.InvokeAsync(stream, "Open",
                [Value.String(path)], null, timeout.Token);
            stage = "AudioInputStream";
            await session.InvokeAsync(recognizer, "AudioInputStream", [],
                stream, timeout.Token);
            stage = "CreateRecoContext";
            Value context = await session.InvokeAsync(recognizer,
                "CreateRecoContext", [], null, timeout.Token);
            Assert.Equal(ValueKind.Object, context.Kind);
            output.WriteLine("Fixed WAV: recognition context created");
        }
        catch (COMException failure)
        {
            output.WriteLine($"Fixed WAV: {stage} 0x{failure.HResult:x8}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ProbeInProcessRecognizerWithoutOpeningAudioInput()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var session = new ComSession();
        Exception? failure = await Record.ExceptionAsync(async () =>
        {
            Value recognizer = await session.CreateAsync(
                "SAPI.SpInprocRecognizer", false, timeout.Token);
            Assert.Equal(ValueKind.Object, recognizer.Kind);
        });
        if (failure is null)
        {
            output.WriteLine("SAPI.SpInprocRecognizer: created");
        }
        else
        {
            COMException error = Assert.IsType<COMException>(failure);
            output.WriteLine($"SAPI.SpInprocRecognizer: 0x{error.HResult:x8}");
        }
    }

    [Fact]
    public async Task UnavailableContextKeepsInitializationCauseWithoutStartingRecognition()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var report = new Dictionary<string, object?>
        {
            ["recognitionAttempted"] = false,
            ["os"] = RuntimeInformation.OSDescription,
            ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString()
        };
        ComSpeechRecognition? unexpected = null;
        try
        {
            await using (var session = new ComSession())
            {
                Value category = await session.CreateAsync("SAPI.SpObjectTokenCategory", false, timeout.Token);
                Value create = Value.Numeric(0) with
                {
                    NativeVariantType = 11,
                    NativeScalar = false
                };
                await session.InvokeAsync(category, "SetId",
                    [Value.String(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech\AudioInput"), create],
                    null, timeout.Token);
                Value tokens = await session.InvokeAsync(category, "EnumerateTokens", [], null, timeout.Token);
                Value count = await session.InvokeAsync(tokens, "Count", [], null, timeout.Token);
                Assert.Equal(ValueKind.Number, count.Kind);
                report["inputTokens"] = count.Number;
                Assert.Equal(0, count.Number);
            }

            Exception observed = await Record.ExceptionAsync(async () =>
                unexpected = await ComSpeechRecognition.CreateAsync(_ => { }, token: timeout.Token));
            report["failure"] = observed?.ToString();
            SpeechRecognitionInitializationException failure =
                Assert.IsType<SpeechRecognitionInitializationException>(observed);
            Assert.Equal("SAPIが存在しないか or 初期化エラー", failure.Message);
            COMException cause = Assert.IsType<COMException>(failure.InnerException);
            report["hresult"] = $"0x{failure.HResult:x8}";
            Assert.Equal(cause.HResult, failure.HResult);
            // 入力取得より前のcontext作成で観測した、このゲストの失敗条件です。
            Assert.Equal(unchecked((int)0x80050022), cause.HResult);
            Assert.Contains("73AD6842-ACE0-45E8-A4DD-8795881A2C2A", cause.Message,
                StringComparison.OrdinalIgnoreCase);
            var lines = new List<string>();
            var automation = new WindowsAutomation(
                _ => throw new InvalidOperationException("物理入力は禁止です。"),
                swappedMouseButtons: false);
            await new Interpreter(windows: automation).RunAsync(
                "TRY\nRECOSTATE(TRUE)\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT \"after\"",
                lines.Add, timeout.Token);
            Assert.Equal(["caught", "after"], lines);
            report["publicCall"] = "caught-and-exited-without-duplicate-error";
            report["result"] = "expected-initialization-error";
        }
        finally
        {
            if (unexpected is not null)
            {
                await unexpected.DisposeAsync();
            }

            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            output.WriteLine(json);
            await File.WriteAllTextAsync(Path.Combine(@"C:\ui-output",
                "speech-recognition-initialization-error.json"), json);
        }
    }
}
