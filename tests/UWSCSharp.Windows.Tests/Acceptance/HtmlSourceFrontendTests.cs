using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class HtmlSourceFrontendTests(ITestOutputHelper output)
{
    internal const string UpdatedBodySource = """
        doc=CREATEOLEOBJ("htmlfile")
        doc.write("<html><body><p>old</p><p>old-second</p></body></html>")
        doc.close()
        win=doc.parentWindow
        body=doc.body
        IFB POS("old-second",IEGETSRC(win,"P",2))>0
            Report("initial=found")
        ENDIF
        body.innerHTML=""
        Report("empty="+VARTYPE(IEGETSRC(win,"P")))
        body.innerHTML="<p>new</p>"
        IFB POS("new",IEGETSRC(win,"P"))>0
            Report("updated=found")
        ENDIF
        Report("second="+VARTYPE(IEGETSRC(win,"P",2)))
        """;

    [Theory]
    [InlineData("UWSCSharp.Cli")]
    [InlineData("UWSCSharp.Wpf")]
    public async Task ReadsCurrentBodyAfterReplacementThroughNormalFrontend(string frontend) =>
        await RunAsync(frontend, UpdatedBodySource + "\n" + """
            EXITEXIT 7
            PROCEDURE Report(message)
                PRINT message
            FEND
            """, "initial=found\nempty=0\nupdated=found\nsecond=0\n");

    internal const string DisabledRadioSource = """
        doc=CREATEOLEOBJ("htmlfile")
        doc.write("<html><body><input type='radio' name='choice' id='active' value='alpha' checked><input type='radio' name='choice' id='locked' value='beta' disabled></body></html>")
        doc.close()
        win=doc.parentWindow
        activeRadio=doc.getElementById("active")
        lockedRadio=doc.getElementById("locked")
        IFB IESETDATA(win,TRUE,"choice","beta")
            Report("disabled=changed")
        ELSE
            Report("disabled=rejected")
        ENDIF
        Report("disabled-state="+activeRadio.checked+"|"+lockedRadio.checked)
        lockedRadio.disabled=FALSE
        IFB IESETDATA(win,TRUE,"choice","beta")
            Report("enabled=changed")
        ELSE
            Report("enabled=rejected")
        ENDIF
        Report("enabled-state="+activeRadio.checked+"|"+lockedRadio.checked)
        """;

    [Theory]
    [InlineData("UWSCSharp.Cli")]
    [InlineData("UWSCSharp.Wpf")]
    public async Task KeepsDisabledNamedRadioSelection(string frontend) =>
        await RunAsync(frontend, DisabledRadioSource + "\n" + """
            EXITEXIT 7
            PROCEDURE Report(message)
                PRINT message
            FEND
            """, "disabled=rejected\ndisabled-state=1|0\n"
                + "enabled=changed\nenabled-state=0|1\n");

    internal const string MixedNamedFieldsSource = """
        doc=CREATEOLEOBJ("htmlfile")
        doc.write("<input id='first' name='entry' value='Key' disabled><input id='second' name='entry' value='key'><input id='third' name='entry' value='Key'>")
        doc.close()
        win=doc.parentWindow
        Report("before="+IEGETDATA(win,"entry","key",1)+"|"+IEGETDATA(win,"entry","key",2))
        IFB IESETDATA(win,"blocked","entry","key",1)
            Report("first=changed")
        ELSE
            Report("first=rejected")
        ENDIF
        IFB IESETDATA(win,"updated","entry","key",2)
            Report("second=changed")
        ELSE
            Report("second=rejected")
        ENDIF
        firstInput=doc.getElementById("first")
        secondInput=doc.getElementById("second")
        thirdInput=doc.getElementById("third")
        Report("after="+firstInput.value+"|"+secondInput.value+"|"+thirdInput.value)
        """;

    [Theory]
    [InlineData("UWSCSharp.Cli")]
    [InlineData("UWSCSharp.Wpf")]
    public async Task CountsDisabledNamedFieldsThroughNormalFrontend(string frontend) =>
        await RunAsync(frontend, MixedNamedFieldsSource + "\n" + """
            EXITEXIT 7
            PROCEDURE Report(message)
                PRINT message
            FEND
            """, "before=Key|key\nfirst=rejected\nsecond=changed\nafter=Key|updated|Key\n");

    [Theory]
    [InlineData("UWSCSharp.Cli")]
    [InlineData("UWSCSharp.Wpf")]
    public async Task RejectsDisabledNamedCheckboxAndButton(string frontend) =>
        await RunAsync(frontend, """
            doc=CREATEOLEOBJ("htmlfile")
            doc.write("<html><body><input type='checkbox' id='flag' name='flag' disabled><input type='button' id='action' name='action' value='run' title='idle' onclick='this.title=String(1)' disabled></body></html>")
            doc.close()
            win=doc.parentWindow
            flag=doc.getElementById("flag")
            action=doc.getElementById("action")
            PRINT IESETDATA(win,TRUE,"flag")
            PRINT flag.checked
            PRINT IESETDATA(win,TRUE,"action","run")
            PRINT action.title
            PRINT IESETDATA(win,"renamed","action","run")
            PRINT action.value
            action.value="run"
            flag.disabled=FALSE
            action.disabled=FALSE
            PRINT IESETDATA(win,TRUE,"flag")
            PRINT flag.checked
            PRINT IESETDATA(win,TRUE,"action","run")
            PRINT IESETDATA(win,"renamed","action","run")
            PRINT action.value
            EXITEXIT 7
            """, "0\nFalse\n0\nidle\n0\nrun\n1\nTrue\n1\n1\nrenamed\n");

    [Theory]
    [InlineData("UWSCSharp.Cli", "")]
    [InlineData("UWSCSharp.Cli", " multiple")]
    [InlineData("UWSCSharp.Wpf", "")]
    [InlineData("UWSCSharp.Wpf", " multiple")]
    public async Task KeepsDisabledNamedSelection(string frontend, string selectionMode) =>
        await RunAsync(frontend, $$"""
            doc=CREATEOLEOBJ("htmlfile")
            doc.write("<select id='choice' name='choice' disabled{{selectionMode}}><option selected>Alpha</option><option>Beta</option></select>")
            doc.close()
            win=doc.parentWindow
            choice=doc.getElementById("choice")
            PRINT IEGETDATA(win,"choice")
            PRINT IESETDATA(win,"Beta","choice")
            PRINT choice.selectedIndex
            PRINT IEGETDATA(win,"choice")
            choice.disabled=FALSE
            PRINT IESETDATA(win,"Beta","choice")
            PRINT choice.selectedIndex
            PRINT IEGETDATA(win,"choice")
            EXITEXIT 7
            """, "Alpha\n0\n0\nAlpha\n1\n1\nBeta\n");

    internal const string TextareaSource = """
        doc=CREATEOLEOBJ("htmlfile")
        doc.write("<textarea id='entry' name='entry'>default</textarea>")
        doc.close()
        win=doc.parentWindow
        area=doc.getElementById("entry")
        area.value="live"
        Report("before="+IEGETDATA(win,"TAG=TEXTAREA")+"|"+IEGETDATA(win,"entry"))
        IFB IESETDATA(win,"tag","TAG=TEXTAREA")
            Report("tag-set=1")
        ELSE
            Report("tag-set=0")
        ENDIF
        Report("after-tag="+area.innerText+"|"+area.value+"|"+IEGETDATA(win,"entry"))
        IFB IESETDATA(win,"named","entry")
            Report("name-set=1")
        ELSE
            Report("name-set=0")
        ENDIF
        Report("after-name="+area.innerText+"|"+area.value+"|"+IEGETDATA(win,"TAG=TEXTAREA"))
        """;

    [Theory]
    [InlineData("UWSCSharp.Cli")]
    [InlineData("UWSCSharp.Wpf")]
    public async Task ReadsAndUpdatesTextareaThroughNormalFrontend(string frontend) =>
        await RunAsync(frontend, TextareaSource + "\n" + """
            EXITEXIT 7
            PROCEDURE Report(message)
                PRINT message
            FEND
            """, "before=live|live\ntag-set=1\nafter-tag=tag|tag|tag\n"
                + "name-set=1\nafter-name=named|named|named\n");

    [Theory]
    [InlineData("UWSCSharp.Cli")]
    [InlineData("UWSCSharp.Wpf")]
    public async Task ReadsAndReplacesHtmlThroughNormalFrontend(
        string frontend) => await RunAsync(frontend, """
            doc=CREATEOLEOBJ("htmlfile")
            doc.write("<html><body><form><p>日本語𠮷</p><p></p></form></body></html>")
            doc.close()
            win=doc.parentWindow
            PRINT POS("日本語𠮷",IEGETSRC(win,"TAG=P"))>0
            PRINT LENGTH(IEGETSRC(win,"P",2))>0
            PRINT VARTYPE(IEGETSRC(win,"aside"))
            PRINT IESETSRC(win,"missing","aside")
            PRINT IESETSRC(win,"<html></html>","HTML")
            PRINT IESETSRC(win,"<span>変更後𠮷</span><input name='entry' value='initial'>","P")
            PRINT POS("変更後𠮷",IEGETSRC(win,"SPAN"))>0
            PRINT IEGETDATA(win,"entry")
            PRINT IESETDATA(win,"updated","entry")
            PRINT IEGETDATA(win,"entry")
            PRINT VARTYPE(IEGETSRC(win,"P",2))
            PRINT IEGETSRC(win,"*")=IEGETSRC(win,"HTML")
            tagCount=doc.getElementsByTagName("*").length
            PRINT IESETSRC(win,"<u>末尾</u>","*",tagCount+1)
            PRINT IESETSRC(win,"<u>末尾</u>","*",tagCount)
            PRINT POS("末尾",IEGETSRC(win,"U"))>0
            PRINT POS("変更後𠮷",IEGETSRC(win,"SPAN"))>0
            EXITEXIT 7
            """, "1\n1\n0\n0\n0\n1\n1\ninitial\n1\nupdated\n0\n1\n0\n1\n1\n1\n");

    [Theory]
    [InlineData("UWSCSharp.Cli")]
    [InlineData("UWSCSharp.Wpf")]
    public async Task ReadsTableAndUpdatesSelectedTagThroughNormalFrontend(
        string frontend) => await RunAsync(frontend, """
            doc=CREATEOLEOBJ("htmlfile")
            doc.write("<p class='Accent'>Upper</p><p class='accent'>lower</p>")
            doc.write("<table id='sample'><thead><tr><th colspan='2'>head</th></tr></thead><tbody><tr><td>left</td><td>right</td></tr></tbody></table>")
            doc.close()
            win=doc.parentWindow
            tableObj=doc.getElementById("sample")
            rowObj=tableObj.rows.item(1)
            PRINT IEGETDATA(win,"TAG=TABLE",1,1,1)
            PRINT VARTYPE(IEGETDATA(win,"TAG=TABLE",1,1,2))
            PRINT IEGETDATA(win,"TAG=TABLE",1,2,2)
            PRINT IEGETDATA(win,"TAG=TABLE",1,0,1)=tableObj.innerText
            PRINT IEGETDATA(win,"TAG=TABLE",1,2,0)=rowObj.innerText
            PRINT IEGETDATA(win,"TAG=P","ClassName=accent")
            PRINT IESETDATA(win,"updated","TAG=P","ClassName=accent")
            PRINT IEGETDATA(win,"TAG=P",1)
            PRINT IEGETDATA(win,"TAG=P",2)
            PRINT IESETDATA(win,"unused","TAG=P","ClassName=missing")
            EXITEXIT 7
            """, "head\n0\nright\n1\n1\nUpper\n1\nupdated\nlower\n0\n");

    [Theory]
    [InlineData("UWSCSharp.Cli")]
    [InlineData("UWSCSharp.Wpf")]
    public async Task ReadsAndUpdatesMultipleSelectionsThroughNormalFrontend(
        string frontend) => await RunAsync(frontend, """
            doc=CREATEOLEOBJ("htmlfile")
            doc.write("<select name='choice' multiple><option value='a'>Same</option><option value='b' selected>Same</option><option value='c' selected>Other</option></select>")
            doc.close()
            win=doc.parentWindow
            PRINT IEGETDATA(win,"choice")
            PRINT IEGETDATA(win,"choice","b")
            PRINT LENGTH(IEGETDATA(win,"choice","c"))
            PRINT IESETDATA(win,"Same","choice")
            PRINT IEGETDATA(win,"choice")
            PRINT IESETDATA(win,"Missing"+CHR(9)+"Other","choice")
            PRINT IEGETDATA(win,"choice")
            EXITEXIT 7
            """, "Same\tOther\nSame\tOther\n0\n1\nSame\n1\nOther\n");

    [Theory]
    [InlineData("UWSCSharp.Cli")]
    [InlineData("UWSCSharp.Wpf")]
    public async Task ReadsImageInputAndSelectionTagsThroughNormalFrontend(
        string frontend) => await RunAsync(frontend, """
            doc=CREATEOLEOBJ("htmlfile")
            doc.write("<img alt='Image label'><input id='field' value='Initial'>")
            doc.write("<select multiple><option selected>One</option>")
            doc.write("<option>Two</option><option selected>Three</option></select>")
            doc.close()
            field=doc.getElementById("field")
            field.value="Current"
            win=doc.parentWindow
            PRINT VARTYPE(IEGETDATA(win,"TAG=IMG"))
            PRINT LENGTH(IEGETDATA(win,"TAG=IMG"))
            PRINT IEGETDATA(win,"TAG=INPUT")
            PRINT IEGETDATA(win,"TAG=SELECT")
            EXITEXIT 7
            """, "258\n0\nCurrent\nOne\tThree\n");

    [Theory]
    [InlineData("UWSCSharp.Cli")]
    [InlineData("UWSCSharp.Wpf")]
    public async Task SelectsLinksByDisplayedTextThroughNormalFrontend(
        string frontend) => await RunAsync(frontend, """
            doc=CREATEOLEOBJ("htmlfile")
            doc.write("<html><body><a id='first'>Open one</a><a id='second'>Open two</a></body></html>")
            doc.close()
            win=doc.parentWindow
            PRINT IELINK(win,"open",2)
            PRINT IELINK(win,"open",3)
            PRINT IELINK(win,"Open",1,TRUE)
            PRINT IELINK(win,"open two",,TRUE)
            PRINT IELINK(win,"ID=second")
            PRINT IELINK(win,"")
            doc2=CREATEOLEOBJ("htmlfile")
            doc2.write("<a>Open    <b>file</b></a><a>Open<br>file</a><a>Open&nbsp;file</a>")
            doc2.close()
            win2=doc2.parentWindow
            PRINT IELINK(win2,"Open file",2,TRUE)
            PRINT IELINK(win2,"Open file",3,TRUE)
            PRINT IELINK(win2,"Open"+CHR(13)+CHR(10)+"file",1,TRUE)
            PRINT IELINK(win2,"Open    file",1,TRUE)
            EXITEXIT 7
            """, "1\n0\n0\n1\n0\n0\n1\n0\n1\n0\n");

    [Theory]
    [InlineData("UWSCSharp.Cli")]
    [InlineData("UWSCSharp.Wpf")]
    public async Task ReturnsEmptyWhenFrameIsAbsentThroughNormalFrontend(
        string frontend) => await RunAsync(frontend, """
            doc=CREATEOLEOBJ("htmlfile")
            doc.write("<html><body><p>parent</p></body></html>")
            doc.close()
            win=doc.parentWindow
            PRINT VARTYPE(IEGETFRAME(win,"missing"))
            PRINT VARTYPE(IEGETFRAME(win,0))
            PRINT IEGETFRAME(win,-1)
            PRINT IEGETDATA(win,"TAG=P")
            EXITEXIT 7
            """, "0\n0\n0\nparent\n");

    private async Task RunAsync(string frontend, string source, string expected)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string directory = Path.Combine(Path.GetTempPath(),
            "uwsc-html-source-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            string script = Path.Combine(directory, "source.uws");
            await File.WriteAllTextAsync(script, source, new UTF8Encoding(true));
            (int code, string receipt) = await SafeArrayFrontendTests.RunAsync(
                FixturePaths.Get(frontend, frontend + ".exe"), [script]);
            output.WriteLine(receipt);
            Assert.Equal(7, code);
            Assert.Equal(expected,
                receipt.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
