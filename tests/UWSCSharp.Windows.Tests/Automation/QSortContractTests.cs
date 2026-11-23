using System.Globalization;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class QSortContractTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    public async Task NumericGroupsAndNaturalKeysDoNotUseAmbientCulture(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            List<string> output = [];
            await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
                DIM a[]="10","2",3
                QSORT(a)
                PRINT JOIN(a,",")
                DIM b[]="file2","file10","file1"
                QSORT(b," 4e0 ")
                PRINT JOIN(b,",")
                """, output.Add);
            Assert.Equal(["3,10,2", "file1,file2,file10"], output);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task NativeBooleanKeysAndModeUseTheirSignedValueAndRoundTripTag()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            o=CREATEOLEOBJ("Scripting.Dictionary")
            o.Add("x",1)
            DIM a[]=0,o.Exists("x"),1
            DIM labels[]="zero","true","one"
            QSORT(a,0,labels)
            PRINT JOIN(labels,",")
            PRINT JOIN(a,",")
            o.Add("values",a)
            copied=o.Item("values")
            PRINT INT(copied[0])
            TRY
                QSORT(a,o.Exists("x"))
            EXCEPT
                PRINT "caught"
            ENDTRY
            PRINT JOIN(a,",")
            """, output.Add);
        Assert.Equal(["true,zero,one", "-1,0,1", "-1", "caught", "-1,0,1"], output);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void NativeModesKeepNulSurrogatesAndLongDigitsAttachedToTheirLabels(int mode)
    {
        Value[] values = [Value.String("\ud800a"), Value.String("\ud800b"),
            Value.String("a\0z"), Value.String("a\0b"), Value.String("😀"), Value.String("😁"),
            Value.String("file"+new string('9', 1000)), Value.String("file2")];
        var keys = UwscArray.FromValues(values);
        var labels = UwscArray.FromValues(Enumerable.Range(0, values.Length).Select(index => Value.Numeric(index)));
        IReadOnlyList<UwscArray> sorted = ArrayOperations.Sort([keys, labels], mode);
        Assert.Equal(values.Length, sorted[0].Count);
        for (int index = 0; index < values.Length; index++)
        {
            Assert.Equal(values[(int)sorted[1].Values[index].Number], sorted[0].Values[index]);
        }

        Assert.Equal(Enumerable.Range(0, values.Length).Select(index => Value.Numeric(index)),
            sorted[1].Values.OrderBy(value => value.Number));
        Assert.Equal(values, keys.Values);
    }
}
