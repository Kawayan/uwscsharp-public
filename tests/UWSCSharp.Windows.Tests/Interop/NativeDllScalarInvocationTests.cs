using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class NativeDllScalarInvocationTests
{
    [Fact]
    public async Task WideTextWritesBackTerminatedOrFixedLength()
    {
        var output = new List<string>();
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            DEF_DLL CharUpperBuffW(VAR PWCHAR,dword):dword:user32.dll
            DIM text="a"+CHR(0)+"b"
            PRINT CharUpperBuffW(text,3)
            PRINT text
            DEF_DLL CharUpperBuffW(VAR WSTRING,dword):dword:user32.dll
            text="a"+CHR(0)+"b"
            PRINT CharUpperBuffW(text,3)
            PRINT text
            """, output.Add);

        Assert.Equal(["3", "A\0B", "3", "A"], output);
    }

    [Fact]
    public async Task NumericStructuresGroupFieldsAndWriteBackInArgumentOrder()
    {
        var output = new List<string>();
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            DEF_DLL SystemTimeToFileTime({word,word,word,word,word,word,word,word},{dword,dword}):bool:kernel32.dll
            DIM words[1]=0,0
            IF SystemTimeToFileTime(1970,1,0,1,0,0,0,0,words[0],words[1])<>0 THEN PRINT words[0]
            PRINT words[1]
            IF SystemTimeToFileTime(1970,1,0,1,0,0,0,0,words[0],words[0])<>0 THEN PRINT words[0]
            """, output.Add);

        Assert.Equal(["3577643008", "27111902", "27111902"], output);
    }

    [Fact]
    public async Task NumericReferenceReevaluatesHashTableKeyDuringWriteback()
    {
        var output = new List<string>();
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            DEF_DLL SystemTimeToFileTime({word,word,word,word,word,word,word,word},{dword,dword}):bool:kernel32.dll
            PUBLIC reads=0
            HASHTBL values
            values["before"]=11
            values["after"]=22
            DIM high=0
            IF SystemTimeToFileTime(1970,1,0,1,0,0,0,0,values[ChooseKey()],high)<>0 THEN PRINT "converted"
            PRINT values["before"]+"|"+values["after"]+"|"+high+"|"+reads
            FUNCTION ChooseKey()
                reads=reads+1
                IFB reads=1
                    RESULT="before"
                ELSE
                    RESULT="after"
                ENDIF
            FEND
            """, output.Add);

        Assert.Equal(["converted", "11|3577643008|27111902|2"], output);
    }

    [Fact]
    public async Task NumericReferenceReevaluatesBothArrayCoordinatesInOrder()
    {
        var output = new List<string>();
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            DEF_DLL SystemTimeToFileTime({word,word,word,word,word,word,word,word},{dword,dword}):bool:kernel32.dll
            PUBLIC rowReads=0,columnReads=0,trace=""
            DIM values[1,1]=11,22,33,44
            DIM high=0
            IF SystemTimeToFileTime(1970,1,0,1,0,0,0,0,values[ChooseRow()][ChooseColumn()],high)<>0 THEN PRINT "converted"
            PRINT values[0][0]+"|"+values[0][1]+"|"+values[1][0]+"|"+values[1][1]+"|"+high
            PRINT trace
            FUNCTION ChooseRow()
                rowReads=rowReads+1
                trace=trace+"R"+rowReads
                RESULT=rowReads-1
            FEND
            FUNCTION ChooseColumn()
                columnReads=columnReads+1
                trace=trace+"C"+columnReads
                RESULT=columnReads-1
            FEND
            """, output.Add);

        Assert.Equal(["converted", "11|22|33|3577643008|27111902", "R1C1R2C2"],
            output);
    }

    [Fact]
    public async Task NumericArraysUseDeclaredElementWidthAndWriteBackWithoutVar()
    {
        var output = new List<string>();
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            DEF_DLL SystemTimeToFileTime(word[],dword[]):bool:kernel32.dll
            DIM systemTime[7]=1970,1,0,1,0,0,0,0
            DIM fileTime[1]=0,0
            IF SystemTimeToFileTime(systemTime,fileTime)<>0 THEN PRINT fileTime[0]
            PRINT fileTime[1]
            """, output.Add);

        Assert.Equal(["3577643008", "27111902"], output);
    }

    [Fact]
    public async Task NumericReferenceWritesBackAndLoadFailureLeavesValueIntact()
    {
        var output = new List<string>();
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            DEF_DLL QueryPerformanceFrequency(VAR longlong):bool:kernel32.dll
            DEF_DLL MissingReferenceExport(VAR longlong):bool:kernel32.dll
            DIM frequency=0, retained=23
            IF QueryPerformanceFrequency(frequency)<>0 AND frequency>0 THEN PRINT VARTYPE(frequency)
            TRY
                MissingReferenceExport(retained)
            EXCEPT
                PRINT retained
            ENDTRY
            """, output.Add);

        Assert.Equal(["20", "23"], output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectNumericReferenceReevaluatesDestination(bool hash)
    {
        string setup = hash
            ? """
                HASHTBL values
                values["initial"]=11
                values["updated"]=22
                """
            : "DIM values[1]=11,22";
        string initial = hash ? "\"initial\"" : "0";
        string updated = hash ? "\"updated\"" : "1";
        var output = new List<string>();
        await new Interpreter(windows: new WindowsAutomation()).RunAsync($$"""
            DEF_DLL QueryPerformanceFrequency(VAR longlong):bool:kernel32.dll
            PUBLIC reads=0
            DIM frequency=0
            IF QueryPerformanceFrequency(frequency)<>0 AND frequency>0 THEN PRINT "frequency"
            {{setup}}
            IF QueryPerformanceFrequency(values[Choose()])<>0 THEN PRINT "converted"
            PRINT values[{{initial}}]+"|"+reads+"|"+VARTYPE(values[{{updated}}])
            IF values[{{updated}}]=frequency THEN PRINT "matches"
            FUNCTION Choose()
                reads=reads+1
                IFB reads=1
                    RESULT={{initial}}
                ELSE
                    RESULT={{updated}}
                ENDIF
            FEND
            """, output.Add);

        Assert.Equal(["frequency", "converted", "11|2|20", "matches"], output);
    }

    [Fact]
    public async Task SystemFunctionsUseNormalInterpreterDispatch()
    {
        var output = new List<string>();
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            DEF_DLL GetCurrentProcessId():uint:kernel32
            DEF_DLL MulDiv(int,int,int):long:kernel32
            PRINT getcurrentprocessid()
            PRINT MulDiv(2.5,1,1)
            PRINT MulDiv("3.5",1,1)
            PRINT MulDiv(TRUE,1,1)
            PRINT Box.GetCurrentProcessId()
            PRINT GLOBAL.GetCurrentProcessId()
            MODULE Box
                DEF_DLL GetCurrentProcessId():uint:kernel32
            ENDMODULE
            """, output.Add);

        string pid = Environment.ProcessId.ToString();
        Assert.Equal([pid, "2", "4", "1", pid, pid], output);
    }

    [Theory]
    [InlineData("MissingExport():int:kernel32", "MissingExport()", "MissingExport")]
    [InlineData("ReadValue():int:UwscMissingProbe.dll", "ReadValue()", "UwscMissingProbe.dll")]
    [InlineData("MulDiv(int,int,int):int:kernel32", "MulDiv(1)", "引数")]
    [InlineData("MulDiv(int,int,int):int:kernel32", "MulDiv(2147483648,1,1)", "")]
    [InlineData("UnsupportedVariant(VAR variant):int:kernel32",
        "UnsupportedVariant(1)", "対応していません")]
    public async Task ScriptErrorsAreAttachedToTheCallLine(
        string declaration, string call, string diagnostic)
    {
        var interpreter = new Interpreter(windows: new WindowsAutomation());
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            interpreter.RunAsync($"DEF_DLL {declaration}\nPRINT {call}", _ => { }));

        Assert.Equal(2, error.Line);
        Assert.Contains(diagnostic, error.Message);
    }
}
