using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;
using UWSCSharp.Core;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

// カテゴリと登録だけを読み、認識器の作成・入力の設定・認識開始は行いません。
[SupportedOSPlatform("windows")]
[Trait("Category", "SandboxOnly")]
public sealed class SpeechRecognitionInventoryTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("recognizers", @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech\Recognizers")]
    [InlineData("inputs", @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech\AudioInput")]
    public async Task RecordsRecognitionTokensWithoutStartingRecognition(string kind, string categoryId)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var report = new Dictionary<string, object?>
        {
            ["category"] = categoryId,
            ["os"] = RuntimeInformation.OSDescription,
            ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["recognitionAttempted"] = false,
            ["inputConfigured"] = false,
            ["contextRegistration"] = ReadContextRegistration()
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var session = new ComSession();
        try
        {
            Value category = await session.CreateAsync("SAPI.SpObjectTokenCategory", false, timeout.Token);
            // SDKのCreateIfNotExist=FALSEを明示し、既存カテゴリだけへ関連付けます。
            Value create = Value.Numeric(0) with
            {
                NativeVariantType = 11,
                NativeScalar = false
            };
            await Invoke(category, "SetId", Value.String(categoryId), create);
            Value tokens = await Invoke(category, "EnumerateTokens", Value.String(""), Value.String(""));
            Value count = await Invoke(tokens, "Count");
            Assert.Equal(ValueKind.Number, count.Kind);
            Assert.InRange(count.Number, 0, 1000);
            Assert.Equal(Math.Truncate(count.Number), count.Number);
            report["tokenCount"] = (int)count.Number;
            var inventory = new List<Dictionary<string, object?>>();
            report["tokens"] = inventory;
            for (int index = 0; index < (int)count.Number; index++)
            {
                Value item = await Invoke(tokens, "Item", Value.Numeric(index) with
                {
                    NativeVariantType = 3,
                    NativeScalar = index
                });
                var token = new Dictionary<string, object?>
                {
                    ["id"] = await Text(item, "Id"),
                    ["description"] = await Text(item, "GetDescription")
                };
                inventory.Add(token);
                if (kind == "recognizers")
                {
                    try
                    {
                        token["language"] = await Text(item, "GetAttribute", Value.String("Language"));
                    }
                    catch (COMException failure)
                    {
                        token["languageError"] = Error(failure);
                    }
                }
            }

            try
            {
                report["defaultId"] = await Text(category, "Default");
            }
            catch (COMException failure)
            {
                report["defaultError"] = Error(failure);
            }

            report["inventoryStatus"] = "enumerated";
        }
        catch (COMException failure)
        {
            // 導入状況の採取結果であり、認識機能の受入成功とは区別します。
            report["inventoryStatus"] = "com-error";
            report["inventoryError"] = Error(failure);
        }
        catch (Exception failure)
        {
            report["inventoryStatus"] = "host-error";
            report["inventoryError"] = failure.ToString();
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
                report["released"] = false;
                report["releaseError"] = failure.ToString();
                throw;
            }
            finally
            {
                string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
                output.WriteLine(json);
                await File.WriteAllTextAsync(Path.Combine(@"C:\ui-output",
                    "speech-recognition-" + kind + "-inventory.json"), json);
            }
        }

        ValueTask<Value> Invoke(Value target, string member, params Value[] arguments) =>
            session.InvokeAsync(target, member, arguments, null, timeout.Token);

        async Task<string> Text(Value target, string member, params Value[] arguments)
        {
            Value value = await Invoke(target, member, arguments);
            Assert.Equal(ValueKind.String, value.Kind);
            return value.Text;
        }
    }

    private static object Error(COMException failure) => new
    {
        hresult = $"0x{failure.HResult:x8}",
        message = failure.Message
    };

    private static object ReadContextRegistration()
    {
        using RegistryKey? program = Registry.ClassesRoot.OpenSubKey(@"SAPI.SpInProcRecoContext\CLSID");
        string? classId = program?.GetValue(null) as string;
        using RegistryKey? server = classId is null ? null
            : Registry.ClassesRoot.OpenSubKey(@"CLSID\" + classId + @"\InprocServer32");
        string? path = server?.GetValue(null) as string;
        string? expanded = path is null ? null : Environment.ExpandEnvironmentVariables(path).Trim('"');
        bool exists = File.Exists(expanded);
        return new
        {
            classId,
            server = expanded,
            exists,
            version = exists ? FileVersionInfo.GetVersionInfo(expanded!).FileVersion : null
        };
    }
}
