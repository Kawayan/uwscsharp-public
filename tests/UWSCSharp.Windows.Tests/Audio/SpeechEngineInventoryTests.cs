using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;
using UWSCSharp.Core;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

// 導入状況だけを採取し、Speakや出力先の変更は行いません。
[SupportedOSPlatform("windows")]
[Trait("Category", "SandboxOnly")]
public sealed class SpeechEngineInventoryTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("Speech.SpVoice")]
    [InlineData("SAPI.SpVoice")]
    public async Task RecordsInstalledVoicesWithoutSpeaking(string programId)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var report = new Dictionary<string, object?>
        {
            ["programId"] = programId,
            ["os"] = RuntimeInformation.OSDescription,
            ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["speechAttempted"] = false,
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var session = new ComSession();
        try
        {
            Value voice;
            try
            {
                voice = await session.CreateAsync(programId, false, timeout.Token);
            }
            catch (COMException failure) when (
                failure.HResult == unchecked((int)0x80040154)
                || failure.HResult == unchecked((int)0x800401F3))
            {
                report["availability"] = "unregistered";
                report["creationError"] = failure.ToString();
                return;
            }

            report["availability"] = "created";
            Value collection = await Invoke(voice, "GetVoices");
            Value count = await Invoke(collection, "Count");
            Assert.Equal(ValueKind.Number, count.Kind);
            Assert.InRange(count.Number, 0, 1000);
            Assert.Equal(Math.Truncate(count.Number), count.Number);
            var voices = new List<Dictionary<string, string>>();
            report["voices"] = voices;
            var registrations = new List<object>();
            report["registrations"] = registrations;
            for (int index = 0; index < (int)count.Number; index++)
            {
                Value item = await Invoke(collection, "Item", Value.Numeric(index) with
                {
                    NativeVariantType = (ushort)VarEnum.VT_I4,
                    NativeScalar = index
                });
                string id = await Text(item, "Id");
                voices.Add(new()
                {
                    ["id"] = id,
                    ["description"] = await Text(item, "GetDescription"),
                    ["language"] = await Text(item, "GetAttribute", Value.String("Language")),
                });
                registrations.Add(ReadRegistration(id));
            }

            if (voices.Count > 0)
            {
                Value current = await Invoke(voice, "Voice");
                report["defaultVoiceId"] = await Text(current, "Id");
            }

            // 出力先がない環境でも音声一覧とエラーを残します。
            try
            {
                Value device = await Invoke(voice, "AudioOutput");
                report["defaultOutputId"] = await Text(device, "Id");
                report["defaultOutputDescription"] = await Text(device, "GetDescription");
            }
            catch (COMException failure)
            {
                report["outputError"] = failure.ToString();
            }
        }
        catch (Exception failure)
        {
            report["probeError"] = failure.ToString();
            throw;
        }
        finally
        {
            try
            {
                await session.DisposeAsync();
                report["released"] = true;
            }
            catch (Exception failure)
            {
                report["releaseError"] = failure.ToString();
                throw;
            }
            finally
            {
                string json = JsonSerializer.Serialize(report,
                    new JsonSerializerOptions { WriteIndented = true });
                output.WriteLine(json);
                await File.WriteAllTextAsync(
                    Path.Combine(@"C:\ui-output", programId + "-inventory.json"), json);
            }
        }

        ValueTask<Value> Invoke(Value target, string member, params Value[] arguments)
            => session.InvokeAsync(target, member, arguments, null, timeout.Token);

        async Task<string> Text(Value target, string member, params Value[] arguments)
        {
            Value value = await Invoke(target, member, arguments);
            Assert.Equal(ValueKind.String, value.Kind);
            return value.Text;
        }
    }

    private static object ReadRegistration(string id)
    {
        const string prefix = @"HKEY_LOCAL_MACHINE\";
        Assert.StartsWith(prefix, id);
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(id[prefix.Length..]);
        Assert.NotNull(key);
        var values = new Dictionary<string, object?>();
        var paths = new Dictionary<string, object?>();
        foreach (string name in key.GetValueNames())
        {
            object? value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            values[name] = value;
            if (value is string text && name.EndsWith("Path", StringComparison.OrdinalIgnoreCase))
            {
                string expanded = Environment.ExpandEnvironmentVariables(text);
                string? directory = Path.GetDirectoryName(expanded);
                string filePrefix = Path.GetFileName(expanded);
                string[] matches = directory is not null && Directory.Exists(directory)
                    ? Directory.GetFiles(directory, filePrefix + "*") : [];
                paths[name] = new
                {
                    expanded,
                    fileExists = File.Exists(expanded),
                    directoryExists = Directory.Exists(directory),
                    prefixMatchCount = matches.Length,
                    prefixMatches = matches.Take(32).Select(Path.GetFileName).ToArray()
                };
            }
        }

        string? classId = key.GetValue("CLSID") as string;
        using RegistryKey? server = classId is null ? null
            : Registry.ClassesRoot.OpenSubKey(@"CLSID\" + classId + @"\InprocServer32");
        string? serverPath = server?.GetValue(null) as string;
        string? expandedServer = serverPath is null ? null
            : Environment.ExpandEnvironmentVariables(serverPath).Trim('"');
        bool serverExists = File.Exists(expandedServer);
        return new
        {
            id,
            values,
            paths,
            serverPath = expandedServer,
            serverExists,
            serverVersion = serverExists
                ? FileVersionInfo.GetVersionInfo(expandedServer!).FileVersion : null
        };
    }
}
