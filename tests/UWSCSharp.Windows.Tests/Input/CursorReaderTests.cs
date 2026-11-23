using System.ComponentModel;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class CursorReaderTests
{
    [Fact]
    public void ClassifiesEveryStandardCursorAndPublishedConstant()
    {
        string[] names = ["APPSTARTING", "ARROW", "CROSS", "HAND",
            "HELP", "IBEAM", "NO", "SIZEALL", "SIZENESW", "SIZENS",
            "SIZENWSE", "SIZEWE", "UPARROW", "WAIT"];
        for (int i = 0; i < names.Length; i++)
        {
            (int kind, int resource) = CursorReader.StandardCursors[i];
            Assert.Equal(kind, CursorReader.Classify(resource,
                id => id));
            Assert.Equal(Value.Numeric(kind),
                ExpressionEvaluator.Evaluate("CUR_" + names[i]));
        }
    }

    [Fact]
    public void PreservesUnknownHandleAndDoesNotLoadForNull()
    {
        Assert.Equal(-123456, CursorReader.Classify(123456, id => id));
        Assert.Equal(0, CursorReader.Classify(0,
            _ => throw new InvalidOperationException()));
        Assert.Throws<Win32Exception>(() =>
            CursorReader.Classify(123456, _ => 0));
    }
}
