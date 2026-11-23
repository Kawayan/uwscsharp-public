using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UWSCSharp.ControlFixture;

internal static class SaveImageClipboardProbe
{
    internal static async Task RunAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess
            || !Environment.UserName.Equals("WDAGUtilityAccount", StringComparison.OrdinalIgnoreCase)
            || args.Length != 5)
        {
            throw new InvalidOperationException("新規Windows Sandbox内で5引数を指定してください。");
        }

        string original = args[0];
        string runtime = args[1];
        string cli = args[2];
        string output = args[4];
        Directory.CreateDirectory(output);
        var results = new List<object>();
        bool adopted = true;
        using var owner = new ClipboardProbeOwner();
        owner.ShowImage([0, 0xffffff, 0x123456, 0xabcdef]);
        foreach (int quality in new[] { 0, -1, -4, -8, -16, -24, -32, 1, 90, 100 })
        {
            foreach (bool current in new[] { false, true })
            {
                owner.Set("empty", 0, []);
                string folder = Path.Combine(output,
                    $"quality-{quality}-{(current ? "current" : "original")}");
                Directory.CreateDirectory(folder);
                string result = Path.Combine(folder, "result.txt");
                string script = Path.Combine(folder, "main.uws");
                string body = $"""
                    f=FOPEN("{result}",F_WRITE8)
                    TRY
                    SAVEIMG(,HNDTOID({owner.Window}),10,10,2,2,TRUE,{quality},IMG_BACK)
                    FPUT(f,"returned")
                    FPUT(f,PEEKCOLOR(0,0,COL_RGB,TRUE))
                    FPUT(f,PEEKCOLOR(1,0,COL_RGB,TRUE))
                    FPUT(f,PEEKCOLOR(0,1,COL_RGB,TRUE))
                    FPUT(f,PEEKCOLOR(1,1,COL_RGB,TRUE))
                    EXCEPT
                    FPUT(f,"error:"+TRY_ERRMSG)
                    ENDTRY
                    FCLOSE(f)
                    EXITEXIT
                    """;
                File.WriteAllText(script, body, new UTF8Encoding(true));
                var start = new ProcessStartInfo(current ? runtime : original)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = folder,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                if (current)
                {
                    start.ArgumentList.Add(cli);
                }

                start.ArgumentList.Add(script);
                using Process process = Process.Start(start)!;
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                bool returned = true;
                try
                {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));
                }
                catch (TimeoutException)
                {
                    returned = false;
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }

                string[] lines = File.Exists(result) ? File.ReadAllLines(result) : [];
                byte[] dib = returned && lines is ["returned", ..] && owner.Available(8)
                    ? owner.ReadDib() : [];
                int depth = dib.Length >= 40
                    ? BinaryPrimitives.ReadInt16LittleEndian(dib.AsSpan(14)) : 0;
                int width = dib.Length >= 40
                    ? BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(4)) : 0;
                int height = dib.Length >= 40
                    ? BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8)) : 0;
                bool passed = returned && process.ExitCode == 0 && lines.Length == 5
                    && lines[0] == "returned" && lines[1] == "0" && lines[2] == "16777215"
                    && width == 2 && height == 2 && depth == (quality < 0 ? -quality : 24);
                if (current)
                {
                    adopted &= passed;
                }

                results.Add(new
                {
                    quality,
                    engine = current ? "current" : "original",
                    returned,
                    exit_code = process.ExitCode,
                    lines,
                    width,
                    height,
                    depth,
                    dib_hex = Convert.ToHexStringLower(dib),
                    passed,
                    stdout = await stdout,
                    stderr = await stderr
                });
                File.WriteAllText(Path.Combine(output, "saveimg-checkpoint.json"),
                    JsonSerializer.Serialize(new
                    {
                        completed = results.Count
                    }));
            }
        }

        File.WriteAllText(Path.Combine(output, "clipboard-result.json"),
            JsonSerializer.Serialize(new
            {
                operation = "SAVEIMG",
                result = adopted ? "PASS" : "FAIL",
                identity = Environment.UserName,
                os_version = Environment.OSVersion.VersionString,
                runtime = Environment.Version.ToString(),
                original_sha256 = Hash(original),
                fixture_sha256 = Hash(typeof(SaveImageClipboardProbe).Assembly.Location),
                assemblies_sha256 = Directory.GetFiles(Path.GetDirectoryName(cli)!, "UWSCSharp.*.dll")
                    .ToDictionary(path => Path.GetFileName(path)!, Hash),
                cases = results
            }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Hash(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
