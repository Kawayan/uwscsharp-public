using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

// 音声をゲスト内のWAVへ出力し、実スピーカーへは送信しません。
[SupportedOSPlatform("windows")]
[Trait("Category", "SandboxOnly")]
public sealed class SpeechEngineSynthesisTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SynthesizesJapaneseAfterOptionalQueuePurge(bool purge)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        JapaneseSpeechTestData.Prepare();
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        var runtime = new FileRuntime(path, purge);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await using (ComSpeechVoice voice = await ComSpeechVoice.CreateAsync(
                SpeechEngine.Sapi5, runtime, timeout.Token))
            {
                if (purge)
                {
                    await voice.SpeakAsync(new(string.Concat(
                        Enumerable.Repeat("この文章は取り消されます。", 100)), 1), timeout.Token);
                    Assert.False(await voice.WaitUntilDoneAsync(0, timeout.Token));
                    await voice.SpeakAsync(new("", 3), timeout.Token);
                }

                int stream = await voice.SpeakAsync(new("音声合成の確認です。", 1), timeout.Token);
                Assert.True(stream > 0);
                bool complete = false;
                while (!complete)
                {
                    complete = await voice.WaitUntilDoneAsync(100, timeout.Token);
                }
            }

            Assert.True(runtime.Released);
            byte[] wave = await File.ReadAllBytesAsync(path, timeout.Token);
            Assert.True(wave.Length > 44);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wave, 0, 4));
            Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wave, 8, 4));
            int dataLength = 0;
            bool nonzero = false;
            for (int offset = 12; offset + 8 <= wave.Length;)
            {
                int length = BitConverter.ToInt32(wave, offset + 4);
                Assert.InRange(length, 0, wave.Length - offset - 8);
                string name = System.Text.Encoding.ASCII.GetString(wave, offset, 4);
                if (name == "data")
                {
                    dataLength += length;
                    nonzero |= wave.AsSpan(offset + 8, length).ContainsAnyExcept((byte)0);
                }

                offset += 8 + length + (length & 1);
            }

            Assert.InRange(dataLength, 1000, 1000000);
            Assert.True(nonzero);
            string report = JsonSerializer.Serialize(new
            {
                voiceId = FileRuntime.VoiceId,
                purge,
                dataLength,
                sha256 = Convert.ToHexString(SHA256.HashData(wave)),
                released = runtime.Released,
                audibleOutputTested = false
            }, new JsonSerializerOptions { WriteIndented = true });
            output.WriteLine(report);
            await File.WriteAllTextAsync(Path.Combine(@"C:\ui-output",
                $"speech-synthesis-{purge}.json"), report, timeout.Token);
        }
        catch (Exception failure)
        {
            var details = new Dictionary<string, string>
            {
                ["exception"] = failure.ToString(),
                ["hresult"] = $"0x{failure.HResult:X8}",
                ["released"] = runtime.Released.ToString(),
            };
            foreach (System.Collections.DictionaryEntry entry in failure.Data)
            {
                details[entry.Key.ToString()!] = entry.Value?.ToString() ?? "";
            }

            string report = JsonSerializer.Serialize(details,
                new JsonSerializerOptions { WriteIndented = true });
            output.WriteLine(report);
            await File.WriteAllTextAsync(Path.Combine(@"C:\ui-output",
                $"speech-synthesis-error-{purge}.json"), report);
            throw;
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class FileRuntime(string path, bool purge) : IComRuntime
    {
        internal const string VoiceId = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech\Voices\Tokens\TTS_MS_JA-JP_HARUKA_11.0";
        private readonly NativeComRuntime native = new();
        private readonly List<object> owned = [];
        private object? file;
        private int speaks;

        internal bool Released
        {
            get; private set;
        }

        public object Create(string name)
        {
            try
            {
                object voice = Own(native.Create(name));
                object token = Own(native.Create("SAPI.SpObjectToken"));
                native.Invoke(token, "SetId", [VoiceId, "", false], false);
                native.Invoke(voice, "Voice", [token], true);
                file = Own(native.Create("SAPI.SpFileStream"));
                native.Invoke(file, "Open", [path, 3, false], false);
                native.Invoke(voice, "AudioOutputStream", [file], true);
                if (purge)
                {
                    native.Invoke(voice, "Pause", [], false);
                }

                return voice;
            }
            catch
            {
                Cleanup();
                throw;
            }
        }

        public object GetActive(string name) => throw new NotSupportedException();

        public object? Invoke(object target, string name, object?[] arguments, bool assign)
        {
            object? result = native.Invoke(target, name, arguments, assign);
            if (name == "Speak" && purge && ++speaks == 3)
            {
                native.Invoke(target, "Resume", [], false);
            }

            return result;
        }

        public void Release(object target)
        {
            Cleanup();
            Released = true;
        }

        private object Own(object value)
        {
            owned.Add(value);
            return value;
        }

        private void Cleanup()
        {
            var failures = new List<Exception>();
            if (file is not null)
            {
                try
                {
                    native.Invoke(file, "Close", [], false);
                }
                catch (Exception failure)
                {
                    failures.Add(failure);
                }
            }

            // 音声本体を先に解放してから、保持したtokenとstreamを解放します。
            foreach (object value in owned)
            {
                try
                {
                    native.Release(value);
                }
                catch (Exception failure)
                {
                    failures.Add(failure);
                }
            }

            owned.Clear();
            file = null;
            if (failures.Count > 0)
            {
                throw new AggregateException(failures);
            }
        }
    }
}
