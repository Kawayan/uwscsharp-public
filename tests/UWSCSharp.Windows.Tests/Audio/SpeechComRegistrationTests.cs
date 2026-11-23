using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

// COMを作成せず、登録と参照DLLの情報だけを読み取ります。
[SupportedOSPlatform("windows")]
[Trait("Category", "SandboxOnly")]
public sealed class SpeechComRegistrationTests(ITestOutputHelper output)
{
    [Fact]
    public void RecordsHarukaEngineAndProsodyRegistrationWithoutActivation()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string source = Path.Combine(AppContext.BaseDirectory, "SpeechData", "ja-JP");
        string ini = Path.Combine(source, "M1041Haruka.INI");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(source, "manifest.json")));
        string expected = manifest.RootElement.GetProperty("files").EnumerateArray()
            .Single(file => file.GetProperty("name").GetString() == "M1041Haruka.INI")
            .GetProperty("sha256").GetString()!;
        string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ini)));
        Assert.Equal(expected, actual);
        string[] lines = File.ReadAllLines(ini);
        bool modelSection = false;
        string? modelClass = null;
        foreach (string line in lines)
        {
            string text = line.Trim();
            if (text.StartsWith('['))
            {
                modelSection = text.Equals("[ProsodyModelSetting]", StringComparison.OrdinalIgnoreCase);
            }
            else if (modelSection && text.StartsWith("ACT=", StringComparison.OrdinalIgnoreCase))
            {
                modelClass = text[4..].Trim();
            }
        }

        Assert.NotNull(modelClass);
        Assert.True(Guid.TryParse(modelClass, out _));
        const string tokenPath = @"SOFTWARE\Microsoft\Speech\Voices\Tokens\TTS_MS_JA-JP_HARUKA_11.0";
        using RegistryKey? token = Registry.LocalMachine.OpenSubKey(tokenPath);
        Assert.NotNull(token);
        string engineClass = Assert.IsType<string>(token.GetValue("CLSID"));
        string report = JsonSerializer.Serialize(new
        {
            os = Environment.OSVersion.ToString(),
            processBitness = IntPtr.Size * 8,
            speechAttempted = false,
            comActivated = false,
            registrationChanged = false,
            iniSource = "staged host speech data",
            iniSha256 = actual,
            engine = ReadClass(engineClass),
            prosodyModel = ReadClass(modelClass)
        }, new JsonSerializerOptions { WriteIndented = true });
        output.WriteLine(report);
        File.WriteAllText(@"C:\ui-output\speech-com-registration.json", report);
    }

    private static object ReadClass(string classId)
    {
        using RegistryKey? registration = Registry.ClassesRoot.OpenSubKey(@"CLSID\" + classId);
        using RegistryKey? server = registration?.OpenSubKey("InprocServer32");
        string? path = server?.GetValue(null) as string;
        string? expanded = path is null ? null : Environment.ExpandEnvironmentVariables(path).Trim('"');
        bool exists = File.Exists(expanded);
        return new
        {
            classId,
            registered = registration is not null,
            serverRegistered = server is not null,
            path = expanded,
            fileExists = exists,
            version = exists ? FileVersionInfo.GetVersionInfo(expanded!).FileVersion : null
        };
    }
}
