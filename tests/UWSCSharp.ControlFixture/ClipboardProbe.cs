using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UWSCSharp.ControlFixture;

internal static class ClipboardProbe
{
    private sealed record Case(string Name, string Kind,
        uint Format, string Data, int[]? Expected);

    private sealed record Expression(string Name, string Source,
        string Expected, string Setup = "");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    internal static async Task RunAsync(string[] args, bool checkImage = false)
    {
        if (!OperatingSystem.IsWindows()
            || !Environment.Is64BitProcess
            || !Environment.UserName.Equals("WDAGUtilityAccount", StringComparison.OrdinalIgnoreCase)
            || args.Length != 5)
        {
            throw new InvalidOperationException("新規Windows Sandbox内で5引数を指定してください。");
        }

        string original = args[0];
        Console.OutputEncoding = Encoding.UTF8;
        string runtime = args[1];
        string cli = args[2];
        string source = args[3];
        string output = args[4];
        Directory.CreateDirectory(output);
        Case[] cases = JsonSerializer.Deserialize<Case[]>(File.ReadAllText(source), Json)!;
        var results = new List<object>();
        bool adopted = true;
        using var owner = new ClipboardProbeOwner();
        foreach (Case item in cases)
        {
            Expression[] expressions = checkImage
                ? ImageExpressions(item, owner.Window) : Expressions(item);
            var engines = new List<object>();
            foreach (bool current in new[] { false, true })
            {
                owner.Set(item.Kind, item.Format, Convert.FromBase64String(item.Data));
                if (checkImage)
                {
                    owner.ShowImage(item.Expected ?? [0x123456, 0xabcdef, 0xfedcba, 0x102030]);
                }
                int[] available = [.. new[] { 2, 8, 17 }.Where(value => owner.Available((uint)value))];
                string folder = Path.Combine(output, item.Name + (current ? "-current" : "-original"));
                Directory.CreateDirectory(folder);
                string result = Path.Combine(folder, "result.txt");
                string script = Path.Combine(folder, "main.uws");
                var body = new StringBuilder($"f=FOPEN(\"{result}\",F_WRITE8)\nd=FOPEN(\"{Path.Combine(folder, "errors.txt")}\",F_WRITE8)\n");
                foreach (Expression expression in expressions)
                {
                    body.Append("TRY\nMOUSEORG(0)\n");
                    body.AppendLine(expression.Setup);
                    body.AppendLine($"FPUT(f,{expression.Source})");
                    body.Append("EXCEPT\nFPUT(f,\"error\")\nFPUT(d,TRY_ERRMSG)\nENDTRY\n");
                }

                body.Append("FCLOSE(d)\nFCLOSE(f)\nEXITEXIT\n");
                File.WriteAllText(script, body.ToString(), new UTF8Encoding(true));
                var start = new ProcessStartInfo(current ? runtime : original)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = folder,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                if (current)
                {
                    start.ArgumentList.Add(cli);
                }

                start.ArgumentList.Add(script);
                bool locked = item.Kind == "locked";
                if (locked)
                {
                    owner.Lock();
                }

                try
                {
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
                    string[] expected = [.. expressions.Select(expression => expression.Expected)];
                    string error = await stderr;
                    bool passed = returned && process.ExitCode == 0
                        && lines.SequenceEqual(expected) && error.Length == 0
                        && owner.Failure is null
                        && (item.Kind is not ("delayed" or "slow-delayed") || owner.Renders > 0);
                    if (current)
                    {
                        adopted &= passed;
                    }

                    engines.Add(new
                    {
                        engine = current ? "current" : "original",
                        status = returned ? "returned" : "timeout",
                        exit_code = process.ExitCode,
                        stdout = await stdout,
                        stderr = error,
                        lines,
                        expected,
                        available,
                        renders = owner.Renders,
                        provider_failure = owner.Failure,
                        adopted_passed = passed
                    });
                }
                finally
                {
                    if (locked)
                    {
                        owner.Unlock();
                    }
                }
            }

            results.Add(new
            {
                name = item.Name,
                item.Kind,
                item.Format,
                expressions,
                engines
            });
            SaveJson(Path.Combine(output, "clipboard-partial.json"),
                new
                {
                    result = "RUNNING",
                    cases = results
                });
            SaveJson(Path.Combine(output, "progress.json"),
                new
                {
                    completed = results.Count,
                    total = cases.Length,
                    last = item.Name
                });
        }

        var report = new
        {
            operation = checkImage ? "CHKIMG" : "PEEKCOLOR",
            result = adopted ? "PASS" : "FAIL",
            checked_at = DateTimeOffset.Now,
            identity = Environment.UserName,
            os_version = Environment.OSVersion.VersionString,
            runtime = Environment.Version.ToString(),
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            original_sha256 = Hash(original),
            fixture_sha256 = Hash(typeof(ClipboardProbe).Assembly.Location),
            cases_sha256 = Hash(source),
            lock_events = owner.LockEvents,
            assemblies_sha256 = Directory.GetFiles(Path.GetDirectoryName(cli)!, "UWSCSharp.*.dll")
                .ToDictionary(path => Path.GetFileName(path)!, Hash),
            cases = results
        };
        SaveJson(Path.Combine(output, "clipboard-result.json"), report);
    }

    private static void SaveJson(string path, object value)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
        File.Move(temporary, path, overwrite: true);
    }

    private static string Hash(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static Expression[] ImageExpressions(Case item, nint window)
    {
        string setup = $"MOUSEORG(HNDTOID({window}),1,MORG_BACK)";
        if (item.Kind == "locked")
        {
            return [new("clipboard-locked", "CHKIMG()", "error", setup)];
        }

        var rows = new List<Expression>();
        foreach (int number in new[] { 1, 2, 4, -1, -2 })
        {
            bool valid = item.Expected is not null;
            string value = number < 0 ? "3" : number == 4 ? "0" : "1";
            string x = !valid ? "-999999" : number == 2 ? "14" : "10";
            string y = !valid ? "-999999" : number is 1 or 2 ? "10" : "14";
            string prefix = $"number-{number}";
            rows.Add(new(prefix, $"CHKIMG(\"\",0,0,0,95,63,{number})",
                valid ? value : "error", setup));
            rows.Add(new(prefix + "-x", "G_IMG_X", x));
            rows.Add(new(prefix + "-y", "G_IMG_Y", y));
            rows.Add(new(prefix + "-all-x", "JOIN(ALL_IMG_X,\",\")",
                valid && number < 0 ? "10,14,10" : ""));
            rows.Add(new(prefix + "-all-y", "JOIN(ALL_IMG_Y,\",\")",
                valid && number < 0 ? "10,10,14" : ""));
        }

        rows.Add(new("omitted-name", "CHKIMG()",
            item.Expected is null ? "error" : "1", setup));
        return [.. rows];
    }

    private static Expression[] Expressions(Case item)
    {
        if (item.Kind == "locked")
        {
            return [new("clipboard-locked", "PEEKCOLOR(0,0,COL_RGB,TRUE)", "error")];
        }

        var rows = new List<Expression>();
        foreach (int index in Enumerable.Range(0, 4))
        {
            foreach (int mode in new[] { 0, 3, 4, 5, 6 })
            {
                string expected = "error";
                if (item.Expected is int[] colors)
                {
                    int value = colors[index];
                    int converted = mode switch
                    {
                        3 => value,
                        4 => value >> 16 & 255,
                        5 => value >> 8 & 255,
                        6 => value & 255,
                        _ => (value & 255) << 16 | (value & 0xff00) | (value >> 16 & 255)
                    };
                    expected = converted.ToString(CultureInfo.InvariantCulture);
                }

                rows.Add(new($"pixel-{index}-{mode}",
                    $"PEEKCOLOR({index % 2},{index / 2},{mode},TRUE)", expected));
            }
        }

        string first = item.Expected is null ? "error"
            : item.Expected[0].ToString(CultureInfo.InvariantCulture);
        string bgr = item.Expected is null ? "error" :
            ((item.Expected[0] & 255) << 16 | (item.Expected[0] & 0xff00)
                | (item.Expected[0] >> 16 & 255)).ToString(CultureInfo.InvariantCulture);
        foreach ((string name, string expression) in new[]
        {
            ("negative-x", "PEEKCOLOR(-1,0,COL_RGB,TRUE)"),
            ("negative-y", "PEEKCOLOR(0,-1,COL_RGB,TRUE)"),
            ("right-edge", "PEEKCOLOR(2,0,COL_RGB,TRUE)"),
            ("bottom-edge", "PEEKCOLOR(0,2,COL_RGB,TRUE)"),
            ("huge-coordinate", "PEEKCOLOR(2147483647,0,COL_RGB,TRUE)")
        })
        {
            rows.Add(new(name, expression, item.Expected is null ? "error" : "-1"));
        }

        rows.Add(new("mode-omitted", "PEEKCOLOR(0,0,,TRUE)", bgr));
        rows.Add(new("mode-empty", "PEEKCOLOR(0,0,EMPTY,TRUE)", bgr));
        rows.Add(new("fraction", "PEEKCOLOR(0.9,0.9,COL_RGB,TRUE)", first));
        rows.Add(new("clipboard-nonzero", "PEEKCOLOR(0,0,COL_RGB,2)", first));
        rows.Add(new("clipboard-negative", "PEEKCOLOR(0,0,COL_RGB,-1)", first));
        rows.Add(new("invalid-mode", "PEEKCOLOR(0,0,1,TRUE)", "error"));
        rows.Add(new("invalid-negative-mode", "PEEKCOLOR(0,0,-1,TRUE)", "error"));
        rows.Add(new("extra-argument", "PEEKCOLOR(0,0,COL_RGB,TRUE,99)", "error"));
        rows.Add(new("ignore-mouse-origin", "PEEKCOLOR(0,0,COL_RGB,TRUE)", first,
            "MOUSEORG(999999,1,2)"));
        return [.. rows];
    }
}
