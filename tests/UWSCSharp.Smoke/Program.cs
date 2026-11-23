using System.Text;
using UWSCSharp.Core;

int passed = 0;
async Task Check(string name, string script,
    params string[] expected)
{
    var actual = new List<string>();
    await new Interpreter().RunAsync(script, actual.Add);
    if (!actual.SequenceEqual(expected))
    {
        throw new Exception($"{name}: [{string.Join(",",
            actual)}]");
    }

    passed++;
}

async Task Error(string script, int line)
{
    try
    {
        await new Interpreter().RunAsync(script, _ =>
        {
        });
    }
    catch (ScriptException ex) when (ex.Line == line)
    {
        passed++;
        return;
    }

    throw new Exception(
        $"Expected error at line {line}: {script}");
}

await Check("example", Source.ReadFile(
    "examples/first-stage.uws"), "30", "開始\t待機", "完了");
await Check("precedence",
    "PRINT 2+3*4; PRINT (2+3)*4; PRINT 10 MOD 3", "14", "20", "1");
await Check("names", "DIM Total\ntotal=4\nPRINT TOTAL", "4");
await Check("empty", "DIM x\nPRINT x\nPRINT x+2", "", "2");
await Check("hex/unary",
    "PRINT $FF; PRINT -2*3; PRINT !FALSE", "255", "-6", "1");
await Check("comparisons",
    "PRINT 2 < 3; PRINT 2>=3; PRINT 2<>3; PRINT \"Ab\"=\"aB\"",
    "1", "0", "1", "1");
await Check("bits",
    "PRINT 6 AND 3; PRINT 4 OR 1; PRINT 7 XOR 3", "2", "5", "4");
await Check("quoted delimiters",
    "PRINT \"http://host;a\" // comment\nPRINT \"a\"+\"b<#DBL><#TAB>\"",
    "http://host;a", "ab\"\t");
await Check("continuation",
    "x = 1 + _ // join\n 2\nPRINT x", "3");
await Check("exit", "PRINT 1; EXITEXIT\nPRINT 2", "1");
await Check("sleep", "SLEEP(0)\nPRINT 1", "1");
await Error("PRINT unknown", 1);
await Error("DIM x\nPRINT 1/0", 2);
await Error("PRINT 1 + _\n (2 / 0)", 1);
await Error("PRINT \"unterminated", 1);
await Error("PRINT 1 _", 1);
await Error("IF TRUE THEN\nPRINT 1", 1);
await Check("null output", "PRINT NULL", "NULL");
await Check("mixed concatenation", "PRINT \"1\"+1", "11");
await Error("PRINT 0 AND (1/0)", 1);
await Error("TRUE=2", 1);
await Error("IF=2", 1);
await Error("PRINT 1e999", 1);
await Error("PRINT (1+2", 1);
await Error("SLEEP(-1)", 1);
await Error("PRINT 1; PRINT @", 1);
using (var cancel = new CancellationTokenSource())
{
    cancel.CancelAfter(50);
    var lines = new List<string>();
    try
    {
        await new Interpreter().RunAsync(
            "SLEEP(30)\nPRINT 1", lines.Add, cancel.Token);
        throw new Exception("Not cancelled");
    }
    catch (OperationCanceledException) when (cancel.IsCancellationRequested)
    {
        if (lines.Count != 0)
        {
            throw new Exception("Executed after cancellation");
        }

        passed++;
    }
}

// 同じインタープリターでも、実行間で変数を保持してはいけません。
var interpreter = new Interpreter();
await interpreter.RunAsync("x=1", _ =>
{
});
try
{
    await interpreter.RunAsync("PRINT x", _ =>
    {
    });
    throw new Exception("Leaked state");
}
catch (ScriptException)
{
    passed++;
}

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
string directory = Path.Combine(Path.GetTempPath(),
    "uwsc-smoke-" + Guid.NewGuid());
Directory.CreateDirectory(directory);
try
{
    foreach (Encoding encoding in new Encoding[]
    {
        new UTF8Encoding(false),
        new UTF8Encoding(true),
        Encoding.GetEncoding(932)
    }

    )
    {
        string path = Path.Combine(directory, "encoding.uws");
        File.WriteAllText(path, "PRINT \"日本語\"", encoding);
        await Check("encoding " + encoding.WebName,
            Source.ReadFile(path), "日本語");
    }
}
finally
{
    Directory.Delete(directory, true);
}

Console.WriteLine(
    $"PASS: {passed} checks (original UWSC not executed)");
