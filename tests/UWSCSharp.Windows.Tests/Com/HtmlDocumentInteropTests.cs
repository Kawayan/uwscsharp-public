using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 固定HTMLをプロセス内の文書に読み込み、画面や入力を使いません。
[SupportedOSPlatform("windows")]
[Trait("Category", "Headless")]
public sealed class HtmlDocumentInteropTests
{
    [Fact]
    public async Task ReadsStaticDocumentThroughTheComSession()
    {
        await using var session = new ComSession();
        Value document = await CreateDocumentAsync(session, """
            <p id="first">確認</p><p id="second">乙</p>
            """);

        Value window = await session.InvokeAsync(document,
            "parentWindow", [], null, default);
        Value windowDocument = await session.InvokeAsync(window,
            "document", [], null, default);
        Value tags = await session.InvokeAsync(windowDocument,
            "getElementsByTagName", [Value.String("p")], null, default);
        Value count = await session.InvokeAsync(tags, "length", [], null, default);
        Value element = await session.InvokeAsync(tags, "item",
            [Value.Numeric(0)], null, default);
        Value id = await session.InvokeAsync(element, "id", [], null, default);
        Value inner = await session.InvokeAsync(element, "innerHTML", [], null, default);
        Value outer = await session.InvokeAsync(element, "outerHTML", [], null, default);

        Assert.Equal(2, count.Number);
        Assert.Equal("first", id.Text);
        Assert.Equal("確認", inner.Text);
        Assert.Contains("確認</p>", outer.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadsAndUpdatesNamedInputsThroughTheComSession()
    {
        await using var session = new ComSession();
        Value document = await CreateDocumentAsync(session, """
            <input name="field" value="旧">
            <input name="field" type="checkbox" value="選択" checked>
            """);

        Value inputs = await session.InvokeAsync(document,
            "getElementsByName", [Value.String("field")], null, default);
        Value count = await session.InvokeAsync(inputs, "length", [], null, default);
        Value text = await session.InvokeAsync(inputs, "item",
            [Value.Numeric(0)], null, default);
        Value checkbox = await session.InvokeAsync(inputs, "item",
            [Value.Numeric(1)], null, default);

        Value before = await session.InvokeAsync(text, "value", [], null, default);
        await session.InvokeAsync(text, "value", [], Value.String("新"), default);
        Value after = await session.InvokeAsync(text, "value", [], null, default);
        Value check = await session.InvokeAsync(checkbox, "checked", [], null, default);

        Assert.Equal(2, count.Number);
        Assert.Equal("旧", before.Text);
        Assert.Equal("新", after.Text);
        Assert.Equal((ushort)11, check.NativeVariantType);
        Assert.Equal(1, check.Number);
    }

    [Fact]
    [Trait("Priority", "Critical")]
    public async Task IeSetDataPreservesDisabledNamedInputAndUpdatesOtherInputs()
    {
        var output = new List<string>();
        var automation = new WindowsAutomation(_ =>
            throw new InvalidOperationException("画面操作は不要です。"));
        await new Interpreter(windows: automation).RunAsync("""
            doc=CREATEOLEOBJ("htmlfile")
            doc.write("<html><body><form><input id='hidden' name='hidden' type='hidden' value='old'><input name='locked' readonly value='old'><input name='disabled' disabled value='old'><input id='reset' name='reset' type='reset' value='reset-label' onclick='this.title=String(1)'></form></body></html>")
            doc.close()
            win=doc.parentWindow
            fields=Split("hidden,locked,disabled",",")
            FOR n=0 TO LENGTH(fields)-1
                field=fields[n]
                PRINT field+"-before="+IEGETDATA(win,field)
                PRINT field+"-write="+IESETDATA(win,"updated",field)
                PRINT field+"-after="+IEGETDATA(win,field)
            NEXT
            PRINT "reset="+IESETDATA(win,1,"reset")
            reset=doc.getElementById("reset")
            PRINT "reset-value="+reset.value
            PRINT "reset-clicked="+reset.title
            PRINT "hidden-after-reset="+IEGETDATA(win,"hidden")
            PRINT "locked-after-reset="+IEGETDATA(win,"locked")
            """, output.Add);
        Assert.Equal(["hidden-before=old", "hidden-write=1", "hidden-after=updated",
            "locked-before=old", "locked-write=1", "locked-after=updated",
            "disabled-before=old", "disabled-write=0", "disabled-after=old",
            "reset=1", "reset-value=1", "reset-clicked=",
            "hidden-after-reset=updated", "locked-after-reset=updated"], output);
    }

    [Fact]
    [Trait("Priority", "Critical")]
    public async Task IeGetDataReadsTagsInputsAndTableCellsFromTheWindow()
    {
        var output = new List<string>();
        var automation = new WindowsAutomation(_ =>
            throw new InvalidOperationException("画面操作は不要です。"));
        await new Interpreter(windows: automation).RunAsync("""
            doc = CREATEOLEOBJ("htmlfile")
            doc.write("<html><body>")
            doc.write("<p id='first' class='sample'>確認</p><p id='second'>乙</p>")
            doc.write("<input name='field' value='alpha'><input name='field' value='beta'>")
            doc.write("<table><tr><td>左</td><td>右</td></tr></table>")
            doc.write("</body></html>")
            doc.close()
            win = doc.parentWindow
            PRINT IEGETDATA(win, "TAG=P")
            PRINT IEGETDATA(win, "TAG=P", 0)
            PRINT IEGETDATA(win, "TAG=P", 2)
            PRINT IEGETDATA(win, "TAG=P", "ID=second")
            PRINT IEGETDATA(win, "TAG=P", "ClassName=sample")
            PRINT IEGETDATA(win, "TAG=P", "innerText=乙")
            PRINT IEGETDATA(win, "TAG=P", "innerHTML=確認")
            PRINT IEGETDATA(win, "field")
            PRINT IEGETDATA(win, "field", "beta")
            PRINT IEGETDATA(win, "field", , 2)
            PRINT IEGETDATA(win, "TAG=TABLE", 1, 1, 2)
            PRINT VARTYPE(IEGETDATA(win, "missing"))
            PRINT VARTYPE(IEGETDATA(win, "TAG=P", 3))
            PRINT VARTYPE(IEGETDATA(win, "TAG=TABLE", 1, 1, 3))
            PRINT VARTYPE(IEGETDATA(win, "TAG=P", 1))
            """, output.Add);

        Assert.Equal(["確認", "確認", "乙", "乙", "確認", "乙", "確認",
            "alpha", "beta", "beta", "右", "0", "0", "0", "258"], output);
    }

    [Fact]
    [Trait("Priority", "Critical")]
    public async Task IeGetDataReturnsLinkImageAndFormTagValues()
    {
        var output = new List<string>();
        var automation = new WindowsAutomation(_ =>
            throw new InvalidOperationException("画面操作は不要です。"));
        await new Interpreter(windows: automation).RunAsync("""
            doc = CREATEOLEOBJ("htmlfile")
            doc.write("<html><body>")
            doc.write("<a href='about:blank#target'>Link label</a>")
            doc.write("<img src='about:blank#image' alt='Image label'>")
            doc.write("<input id='field' value='Default field'>")
            doc.write("<textarea id='area'>Default area</textarea>")
            doc.write("<select id='choice'><option value='first-value'>First label</option>")
            doc.write("<option value='second-value' selected>Second label</option></select>")
            doc.write("<select id='multi' multiple><option selected>One</option>")
            doc.write("<option>Two</option><option selected>Three</option></select>")
            doc.write("</body></html>")
            doc.close()
            field = doc.getElementById("field")
            field.value = "Live field"
            area = doc.getElementById("area")
            area.value = "Live area"
            win = doc.parentWindow
            tags = SPLIT("A,IMG,INPUT,TEXTAREA,SELECT,OPTION", ",")
            FOR i = 0 TO LENGTH(tags) - 1
                tag = tags[i]
                value = IEGETDATA(win, "TAG=" + tag)
                PRINT tag + "|" + VARTYPE(value) + "|[" + value + "]"
            NEXT
            PRINT VARTYPE(IEGETDATA(win, "TAG=IMG", 2))
            multi = doc.getElementById("multi")
            value = IEGETDATA(win, "TAG=SELECT", "ID=multi")
            PRINT "MULTI|" + VARTYPE(value) + "|[" + value + "]"
            multi.selectedIndex = -1
            value = IEGETDATA(win, "TAG=SELECT", "ID=multi")
            PRINT "UNSELECTED|" + VARTYPE(value) + "|[" + value + "]"
            """, output.Add);

        Assert.Equal([
            "A|258|[Link label]", "IMG|258|[]", "INPUT|258|[Live field]",
            "TEXTAREA|258|[Live area]", "SELECT|258|[Second label]",
            "OPTION|258|[First label]", "0", "MULTI|258|[One\tThree]",
            "UNSELECTED|258|[]"], output);
    }

    [Fact]
    [Trait("Priority", "Critical")]
    public async Task IeSetDataUpdatesFormsAndTagTextFromTheWindow()
    {
        var output = new List<string>();
        var automation = new WindowsAutomation(_ =>
            throw new InvalidOperationException("画面操作は不要です。"));
        await new Interpreter(windows: automation).RunAsync("""
            doc = CREATEOLEOBJ("htmlfile")
            doc.write("<html><body>")
            doc.write("<input name='field' value='old'><input name='field' value='keep'>")
            doc.write("<input id='flag' name='flag' type='checkbox'>")
            doc.write("<input id='alpha' name='choice' type='radio' value='alpha' checked>")
            doc.write("<input id='beta' name='choice' type='radio' value='beta'>")
            doc.write("<p id='paragraph'>old</p></body></html>")
            doc.close()
            win = doc.parentWindow
            PRINT IESETDATA(win, "new", "field", "old")
            PRINT IEGETDATA(win, "field")
            PRINT IEGETDATA(win, "field", , 2)
            flag = doc.getElementById("flag")
            PRINT IESETDATA(win, TRUE, "flag")
            PRINT flag.checked
            PRINT IESETDATA(win, FALSE, "flag")
            PRINT flag.checked
            PRINT IESETDATA(win, TRUE, "choice", "beta")
            alpha = doc.getElementById("alpha")
            beta = doc.getElementById("beta")
            PRINT alpha.checked
            PRINT beta.checked
            PRINT IESETDATA(win, "<b>new</b>", "TAG=P", "ID=paragraph")
            PRINT IEGETDATA(win, "TAG=P")
            paragraph = doc.getElementById("paragraph")
            PRINT paragraph.innerHTML
            PRINT IESETDATA(win, "new", "missing")
            PRINT VARTYPE(IESETDATA(win, "new", "missing"))
            """, output.Add);

        Assert.Equal(["1", "new", "keep", "1", "True", "1", "False", "1",
            "False", "True", "1", "<b>new</b>", "&lt;b&gt;new&lt;/b&gt;", "0", "11"], output);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Priority", "Critical")]
    public async Task IeSetDataActivatesOnlyTheSelectedButtonOrImage(bool eventsAvailable)
    {
        var output = new List<string>();
        var runtime = new RecordingHtmlRuntime(eventsAvailable);
        var automation = new WindowsAutomation(_ =>
            throw new InvalidOperationException("画面操作は不要です。"), comRuntime: runtime);
        await new Interpreter(windows: automation).RunAsync("""
            doc = CREATEOLEOBJ("htmlfile")
            doc.write("<html><body>")
            doc.write("<input id='action' name='action' type='button' value='run'>")
            doc.write("<input id='submit' name='send' type='submit' value='send'>")
            doc.write("<img id='first' src='about:blank#asset-first'>")
            doc.write("<img id='second' src='about:blank#asset-second'>")
            doc.write("</body></html>")
            doc.close()
            win = doc.parentWindow
            PRINT IESETDATA(win, FALSE, "action", "run")
            PRINT IESETDATA(win, TRUE, "action", "run")
            PRINT IESETDATA(win, TRUE, "send", "send")
            PRINT IESETDATA(win, TRUE, "TAG=IMG", "#ASSET", 2)
            PRINT IESETDATA(win, FALSE, "TAG=IMG", "#asset")
            PRINT IESETDATA(win, "new", "TAG=IMG", "#asset")
            PRINT IESETDATA(win, TRUE, "TAG=IMG", "#missing")
            PRINT IESETDATA(win, "renamed", "action", "run")
            PRINT IEGETDATA(win, "action")
            image = doc.getElementById("second")
            PRINT image.src
            """, output.Add);

        Assert.Equal(["1", "1", "1", "1", "1", "0", "0", "1", "renamed",
            "about:blank#asset-second"], output);
        string action = eventsAvailable ? "dispatchEvent" : "click";
        Assert.Equal([$"{action}:action", $"{action}:submit", $"{action}:second"],
            runtime.Activations);
    }

    [Fact]
    [Trait("Priority", "Critical")]
    public async Task IeLinkSelectsLinksWithEmptyTitleOrClass()
    {
        var output = new List<string>();
        var runtime = new RecordingHtmlRuntime(eventsAvailable: true);
        var automation = new WindowsAutomation(_ =>
            throw new InvalidOperationException("画面操作は不要です。"),
            comRuntime: runtime);
        await new Interpreter(windows: automation).RunAsync("""
            doc = CREATEOLEOBJ("htmlfile")
            doc.write("<a id='first' class=''>First</a>")
            doc.write("<a id='second' title='tip' class='accent'>Second</a>")
            doc.write("<a id='third' title='' class='other'>Third</a>")
            doc.close()
            win = doc.parentWindow
            PRINT IELINK(win,"TITLE=tip",2,TRUE)
            PRINT IELINK(win,"class=accent")
            PRINT IELINK(win,"TITLE=tip",3)
            """, output.Add);

        Assert.Equal(["1", "1", "0"], output);
        Assert.Equal(["dispatchEvent:third", "dispatchEvent:first"],
            runtime.Activations);
    }

    [Fact]
    [Trait("Priority", "Critical")]
    public async Task IeLinkSelectsAnOccurrenceByDisplayedText()
    {
        var output = new List<string>();
        var runtime = new RecordingHtmlRuntime(eventsAvailable: true);
        var automation = new WindowsAutomation(_ =>
            throw new InvalidOperationException("画面操作は不要です。"), comRuntime: runtime);
        await new Interpreter(windows: automation).RunAsync("""
            doc = CREATEOLEOBJ("htmlfile")
            doc.write("<html><body>")
            doc.write("<a id='first' href='about:blank#first'>Alpha one</a>")
            doc.write("<a id='second' href='about:blank#second'>Alpha two</a>")
            doc.write("<a id='third' href='about:blank#third'>Beta</a>")
            doc.write("<a id='anchor'>Anchor only</a></body></html>")
            doc.close()
            win = doc.parentWindow
            PRINT IELINK(win, "Alpha")
            PRINT IELINK(win, "alpha", 2)
            PRINT IELINK(win, "Alpha", 0)
            PRINT IELINK(win, "Alpha", 3)
            PRINT IELINK(win, "Alpha", 1, TRUE)
            PRINT IELINK(win, "alpha two", , TRUE)
            PRINT IELINK(win, "Anchor only")
            PRINT VARTYPE(IELINK(win, "Missing"))
            PRINT IELINK(win, "")
            """, output.Add);

        Assert.Equal(["1", "1", "1", "0", "0", "1", "1", "11", "0"], output);
        Assert.Equal(["dispatchEvent:first", "dispatchEvent:second",
            "dispatchEvent:first", "dispatchEvent:second", "dispatchEvent:anchor"], runtime.Activations);
    }

    [Fact]
    [Trait("Priority", "Critical")]
    public async Task IeSourceFunctionsReadAndReplaceTheWholeTag()
    {
        var output = new List<string>();
        var automation = new WindowsAutomation(_ =>
            throw new InvalidOperationException("画面操作は不要です。"));
        await new Interpreter(windows: automation).RunAsync("""
            doc = CREATEOLEOBJ("htmlfile")
            doc.write("<html><body><p><b>first</b></p><p><i>second</i></p></body></html>")
            doc.close()
            win = doc.parentWindow
            PRINT IEGETSRC(win, "P")
            PRINT IEGETSRC(win, "tag=p", 0)
            PRINT IEGETSRC(win, "p", 2)
            PRINT VARTYPE(IEGETSRC(win, "aside"))
            changed = IESETSRC(win, "<u>new</u>", "TAG=P", 2)
            PRINT changed
            PRINT VARTYPE(changed)
            PRINT IEGETSRC(win, "U")
            PRINT IEGETSRC(win, "P")
            PRINT VARTYPE(IEGETSRC(win, "P", 2))
            missing = IESETSRC(win, "missing", "aside")
            unsupported = IESETSRC(win, "<html></html>", "HTML")
            PRINT missing
            PRINT VARTYPE(missing)
            PRINT unsupported
            PRINT VARTYPE(unsupported)
            PRINT IEGETDATA(win, "TAG=P")
            PRINT IEGETDATA(win, "TAG=U")
            PRINT IESETSRC(win, "<span>retry</span>", "U")
            PRINT IEGETDATA(win, "TAG=SPAN")
            """, output.Add);

        Assert.Equal(["<p><b>first</b></p>", "<p><b>first</b></p>",
            "<p><i>second</i></p>", "0", "1", "11", "<u>new</u>",
            "<p><b>first</b></p>", "0", "0", "11", "0", "11",
            "first", "new", "1", "retry"],
            output.Select(line => line.Trim().ToLowerInvariant()));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("Priority", "Critical")]
    public async Task IeGetFrameReturnsAReusableDocumentWindow(
        bool cycle, bool distinctWrappers)
    {
        var output = new List<string>();
        var runtime = new FrameFixtureRuntime(cycleAfterLastFrame: cycle,
            distinctWrappers: distinctWrappers);
        var automation = new WindowsAutomation(_ =>
            throw new InvalidOperationException("画面操作は不要です。"), comRuntime: runtime);
        await new Interpreter(windows: automation).RunAsync("""
            doc = CREATEOLEOBJ("htmlfile")
            doc.write("<html><body></body></html>")
            doc.close()
            win = doc.parentWindow
            firstDoc = CREATEOLEOBJ("htmlfile")
            firstDoc.write("<html><body><p>first</p></body></html>")
            firstDoc.close()
            secondDoc = CREATEOLEOBJ("htmlfile")
            secondDoc.write("<html><body><p>second</p></body></html>")
            secondDoc.close()
            frame = IEGETFRAME(win, "SECOND")
            first = IEGETFRAME(win, 0)
            PRINT VARTYPE(frame)
            PRINT IEGETDATA(frame, "TAG=P")
            PRINT IEGETDATA(first, "TAG=P")
            PRINT IEGETDATA(IEGETFRAME(win, 1), "TAG=P")
            PRINT IEGETDATA(IEGETFRAME(win, 2), "TAG=P")
            PRINT IEGETDATA(IEGETFRAME(win, "2"), "TAG=P")
            PRINT IEGETDATA(IEGETFRAME(win, ""), "TAG=P")
            PRINT VARTYPE(IEGETFRAME(win, 9))
            PRINT VARTYPE(IEGETFRAME(win, "missing"))
            PRINT IEGETDATA(frame, "TAG=P")
            """, output.Add);

        Assert.Equal(
            ["9", "second", "first", "first", "second", "second", "first",
                "0", "0", "second"],
            output);
    }

    [Theory]
    [InlineData("IEGETDATA(frame, \"TAG=P\")")]
    [InlineData("IEGETFRAME(frame, 0)")]
    [Trait("Priority", "Critical")]
    public async Task IeFrameDocumentAccessDeniedIsNotAnEmptyResult(string call)
    {
        var runtime = new FrameFixtureRuntime(denySecondDocument: true);
        var automation = new WindowsAutomation(_ =>
            throw new InvalidOperationException("画面操作は不要です。"),
            comRuntime: runtime);
        var output = new List<string>();
        ScriptException failure = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: automation).RunAsync($$"""
                doc = CREATEOLEOBJ("htmlfile")
                firstDoc = CREATEOLEOBJ("htmlfile")
                secondDoc = CREATEOLEOBJ("htmlfile")
                win = doc.parentWindow
                PRINT VARTYPE(IEGETFRAME(win, "missing"))
                frame = IEGETFRAME(win, "second")
                PRINT VARTYPE(frame)
                PRINT {{call}}
                """, output.Add));

        Assert.Equal(["0", "9"], output);
        Assert.Equal(8, failure.Line);
        COMException cause = Assert.IsType<COMException>(failure.GetBaseException());
        Assert.Equal(unchecked((int)0x80070005), cause.HResult);
    }

    [Fact]
    public async Task IeGetFrameSkipsUnreadableNameButKeepsNumericPosition()
    {
        var runtime = new FrameFixtureRuntime(denyFirstName: true);
        var automation = new WindowsAutomation(_ => { },
            swappedMouseButtons: false, comRuntime: runtime);
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync("""
            doc = CREATEOLEOBJ("htmlfile")
            firstDoc = CREATEOLEOBJ("htmlfile")
            secondDoc = CREATEOLEOBJ("htmlfile")
            secondDoc.write("<p>second</p>")
            secondDoc.close()
            win = doc.parentWindow
            PRINT VARTYPE(IEGETFRAME(win, "first"))
            PRINT VARTYPE(IEGETFRAME(win, 1))
            second = IEGETFRAME(win, "second")
            PRINT IEGETDATA(second, "TAG=P")
            """, output.Add);
        Assert.Equal(["0", "9", "second"], output);
    }

    [Fact]
    [Trait("Priority", "Critical")]
    public async Task IeGetSourcePreservesUnicodeAndDistinguishesEmptyTags()
    {
        var output = new List<string>();
        var automation = new WindowsAutomation(_ =>
            throw new InvalidOperationException("画面操作は不要です。"));
        await new Interpreter(windows: automation).RunAsync("""
            doc = CREATEOLEOBJ("htmlfile")
            doc.write("<html><body><p>日本語𠮷</p><p></p></body></html>")
            doc.close()
            win = doc.parentWindow
            PRINT IEGETSRC(win, "P")
            PRINT IEGETSRC(win, "p", -1)
            PRINT IEGETSRC(win, "TAG=P")
            PRINT IEGETSRC(win, "p", 0) = IEGETSRC(win, "p")
            PRINT POS("<HTML>", IEGETSRC(win, "html")) > 0
            PRINT IEGETSRC(win, "*", 1) = IEGETSRC(win, "html")
            PRINT IEGETSRC(win, "P", 2)
            PRINT IESETSRC(win, "<span>変更後𠮷</span>", "TAG=P", 2)
            PRINT IEGETSRC(win, "span")
            PRINT VARTYPE(IEGETSRC(win, "P", 3))
            count = doc.getElementsByTagName("*").length
            PRINT IESETSRC(win, "<i>outside</i>", "*", count + 1)
            PRINT IESETSRC(win, "<u>last</u>", "*", count)
            PRINT IEGETDATA(win, "TAG=U")
            PRINT IEGETSRC(win, "P")
            emptyDoc = CREATEOLEOBJ("htmlfile")
            emptyDoc.write("<html><body></body></html>")
            emptyDoc.close()
            PRINT VARTYPE(IEGETSRC(emptyDoc.parentWindow, "P"))
            """, output.Add);

        Assert.Equal(
            ["<p>日本語𠮷</p>", "<p>日本語𠮷</p>", "<p>日本語𠮷</p>",
                "1", "1", "1", "<p></p>", "1",
                "<span>変更後𠮷</span>", "0", "0", "1", "last",
                "<p>日本語𠮷</p>", "0"],
            output.Select(line => line.Trim().ToLowerInvariant()));
    }

    [Fact]
    public async Task IeSetSourceUpdatesFollowingTagAndFormLookups()
    {
        var output = new List<string>();
        var automation = new WindowsAutomation(_ =>
            throw new InvalidOperationException("画面操作は不要です。"));
        await new Interpreter(windows: automation).RunAsync("""
            doc = CREATEOLEOBJ("htmlfile")
            doc.write("<html><body><form><p>before</p><p>tail</p></form></body></html>")
            doc.close()
            win = doc.parentWindow
            PRINT IESETSRC(win, "<span>変更後</span><input name='entry' value='初期値'>", "P")
            PRINT IEGETDATA(win, "TAG=SPAN")
            PRINT IEGETDATA(win, "entry")
            PRINT IESETDATA(win, "更新値", "entry")
            PRINT IEGETDATA(win, "entry")
            PRINT IEGETSRC(win, "P")
            PRINT VARTYPE(IEGETSRC(win, "P", 2))
            """, output.Add);

        Assert.Equal(["1", "変更後", "初期値", "1", "更新値", "<p>tail</p>", "0"],
            output.Select(line => line.Trim().ToLowerInvariant()));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task IeSetSourceCancellationRespectsTheComWriteBoundary(
        bool cancelAfterWrite, bool denyAfterWrite)
    {
        using var cancellation = new CancellationTokenSource();
        var runtime = new SourceCancellationRuntime(cancellation, cancelAfterWrite,
            denyAfterWrite: denyAfterWrite);
        await using var session = new ComSession(runtime);
        Value document = await CreateDocumentAsync(session,
            "<p>before</p><u>untouched</u>");
        Value window = await session.InvokeAsync(document, "parentWindow",
            [], null, default);
        var write = new CallArguments(
            [window, Value.String("<span>after</span>"), Value.String("P")]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.SetHtmlSourceAsync(write, cancellation.Token).AsTask());

        var read = new CallArguments(
            [window, Value.String(cancelAfterWrite ? "SPAN" : "P")]);
        Value markup = await session.ReadHtmlSourceAsync(read, default);
        Assert.Equal(cancelAfterWrite ? "<span>after</span>" : "<p>before</p>",
            markup.Text.Trim().ToLowerInvariant());
        var sibling = new CallArguments([window, Value.String("U")]);
        Value siblingMarkup = await session.ReadHtmlSourceAsync(sibling, default);
        Assert.Equal("<u>untouched</u>", siblingMarkup.Text.Trim().ToLowerInvariant());
        Assert.Equal(cancelAfterWrite, runtime.WroteMarkup);
        Assert.Equal(denyAfterWrite, runtime.DeniedAfterWrite);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IeSetDataInterruptionKeepsTheFirstSelection(bool denyWrite)
    {
        using var cancellation = new CancellationTokenSource();
        var runtime = new SourceCancellationRuntime(cancellation,
            cancelAfterWrite: true, member: "selected", denySecondWrite: denyWrite);
        await using var session = new ComSession(runtime);
        Value document = await CreateDocumentAsync(session, """
            <select name="choice" multiple>
              <option selected>Old</option><option>First</option>
              <option>Second</option>
            </select>
            """);
        Value window = await session.InvokeAsync(document, "parentWindow",
            [], null, default);
        var write = new CallArguments(
            [window, Value.String("First\tSecond"), Value.String("choice")]);
        if (denyWrite)
        {
            COMException error = await Assert.ThrowsAsync<COMException>(() =>
                session.SetHtmlDataAsync(write, default).AsTask());
            Assert.Equal(unchecked((int)0x80070005), error.HResult);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                session.SetHtmlDataAsync(write, cancellation.Token).AsTask());
        }

        var read = new CallArguments([window, Value.String("choice")]);
        Assert.Equal("First", (await session.ReadHtmlDataAsync(read, default)).Text);
        Assert.Equal(Value.Logical(true),
            await session.SetHtmlDataAsync(write, default));
        Assert.Equal("First\tSecond",
            (await session.ReadHtmlDataAsync(read, default)).Text);
    }

    private sealed class SourceCancellationRuntime(
        CancellationTokenSource cancellation, bool cancelAfterWrite,
        string member = "outerHTML", bool denySecondWrite = false,
        bool denyAfterWrite = false) : IComRuntime
    {
        private readonly NativeComRuntime native = new();
        private int writes;

        internal bool WroteMarkup
        {
            get;
            private set;
        }

        internal bool DeniedAfterWrite
        {
            get;
            private set;
        }

        public object Create(string name) => native.Create(name);

        public object GetActive(string name) => native.GetActive(name);

        public void Release(object target) => native.Release(target);

        public object? Invoke(object target, string name, object?[] arguments, bool assign)
        {
            if (denySecondWrite && name == member && assign && ++writes == 2)
            {
                throw ComOperationErrors.Record(new COMException(
                    "書き込みが拒否されました。", unchecked((int)0x80070005)));
            }

            object? result = native.Invoke(target, name, arguments, assign);
            if (name == member && assign)
            {
                WroteMarkup = true;
                cancellation.Cancel();
                if (denyAfterWrite)
                {
                    DeniedAfterWrite = true;
                    throw ComOperationErrors.Record(new COMException(
                        "書き込み後にアクセスが拒否されました。",
                        unchecked((int)0x80070005)));
                }
            }
            else if (!cancelAfterWrite && name == "item")
            {
                cancellation.Cancel();
            }

            return result;
        }
    }

    [Fact]
    [Trait("Priority", "Critical")]
    public async Task IeGetFrameCountsDeeplyNestedFrames()
    {
        const int depth = 8192;
        await using var session = new ComSession(new DeepFrameRuntime(depth));
        Value root = await session.CreateAsync("fixture", false, default);
        Value count = await session.GetHtmlFrameAsync(
            new CallArguments([root, Value.Numeric(-1)]), default);
        Assert.Equal(Value.Numeric(depth + 1), count);
    }

    [Fact]
    [Trait("Priority", "Critical")]
    public async Task IeGetFrameCancellationReleasesFrameReferencesInReverseOrder()
    {
        using var cancellation = new CancellationTokenSource();
        var runtime = new DeepFrameRuntime(8, cancellation);
        await using var session = new ComSession(runtime);
        Value root = await session.CreateAsync("fixture", false, default);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.GetHtmlFrameAsync(
                new CallArguments([root, Value.Numeric(-1)]),
                cancellation.Token).AsTask());
        Assert.Equal([4, 3, 2, 1], runtime.Released);

        Value count = await session.GetHtmlFrameAsync(
            new CallArguments([root, Value.Numeric(-1)]), default);
        Assert.Equal(Value.Numeric(9), count);
    }

    [Theory]
    [InlineData("IEGETFRAME(Probe())")]
    [InlineData("IEGETFRAME(Probe(),)")]
    public async Task IeGetFrameRejectsMissingSelectorBeforeEvaluatingTarget(
        string expression)
    {
        var printed = new List<string>();
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: new WindowsAutomation()).RunAsync($"""
                PRINT {expression}
                FUNCTION Probe()
                    PRINT "evaluated"
                    RESULT = EMPTY
                FEND
                """, printed.Add));
        Assert.Contains("IEGETFRAME", error.Message);
        Assert.Empty(printed);
    }

    private sealed class DeepFrameRuntime(
        int depth, CancellationTokenSource? cancellation = null) : IComRuntime
    {
        private sealed record Frame(int Depth);

        internal List<int> Released { get; } = [];

        public object Create(string name) => new Frame(0);

        public object GetActive(string name) => Create(name);

        public void Release(object target)
        {
            Released.Add(Assert.IsType<Frame>(target).Depth);
        }

        public object? Invoke(object target, string name,
            object?[] arguments, bool assign)
        {
            Frame frame = Assert.IsType<Frame>(target);
            return name switch
            {
                "document" or "frames" => frame,
                "length" => frame.Depth < depth ? 1 : 0,
                "item" => AcquireFrame(frame.Depth + 1),
                _ => throw new InvalidOperationException(name)
            };
        }

        private ComVariantValue AcquireFrame(int level)
        {
            var frame = new Frame(level);
            if (level == 4)
            {
                cancellation?.Cancel();
            }

            return new ComVariantValue(9, frame) { Acquisitions = [frame] };
        }
    }

    private sealed class FrameFixtureRuntime(bool denySecondDocument = false,
        bool denyFirstName = false, bool cycleAfterLastFrame = false,
        bool distinctWrappers = false)
        : IComRuntime
    {
        private readonly NativeComRuntime native = new();
        private readonly List<object> documents = [];
        private readonly object frames = new();
        private readonly HashSet<object> childDocuments =
            new(ReferenceEqualityComparer.Instance);
        private object? cyclicDocument;
        private int cyclicReads;
        private readonly Dictionary<object, string> names = new(
            ReferenceEqualityComparer.Instance);

        public object Create(string name)
        {
            object document = native.Create(name);
            documents.Add(document);
            return document;
        }

        public object GetActive(string name) => native.GetActive(name);

        public void Release(object target) => native.Release(target);

        public object? Invoke(object target, string name, object?[] arguments, bool assign)
        {
            if (denyFirstName && name == "name"
                && names.GetValueOrDefault(target) == "first")
            {
                throw ComOperationErrors.Record(new COMException(
                    "フレーム名へのアクセスが拒否されました。",
                    unchecked((int)0x80070005)));
            }

            if (denySecondDocument && name == "document"
                && names.GetValueOrDefault(target) == "second")
            {
                throw ComOperationErrors.Record(new COMException(
                    "フレーム文書へのアクセスが拒否されました。",
                    unchecked((int)0x80070005)));
            }

            if (name == "document" && names.ContainsKey(target))
            {
                object? result = native.Invoke(target, name, arguments, assign);
                if (result is ComVariantValue { Value: { } document } variant)
                {
                    childDocuments.Add(document);
                    if (cycleAfterLastFrame && names[target] == "second")
                    {
                        Assert.InRange(++cyclicReads, 1, 16);
                        if (distinctWrappers)
                        {
                            nint identity = Marshal.GetIUnknownForObject(document);
                            try
                            {
                                object wrapper = Marshal.GetUniqueObjectForIUnknown(identity);
                                Assert.NotSame(document, wrapper);
                                result = variant with
                                {
                                    Value = wrapper,
                                    Acquisitions =
                                        [.. variant.Acquisitions ?? [], wrapper]
                                };
                                childDocuments.Add(wrapper);
                                document = wrapper;
                            }
                            finally
                            {
                                Marshal.Release(identity);
                            }
                        }

                        cyclicDocument = document;
                    }
                }

                return result;
            }

            if (ReferenceEquals(target, frames))
            {
                if (name == "length")
                {
                    return documents.Count - 1;
                }

                Assert.Equal("item", name);
                int index = Assert.IsType<int>(Assert.Single(arguments));
                ComVariantValue window = Assert.IsType<ComVariantValue>(
                    native.Invoke(documents[index + 1], "parentWindow", [], false));
                names[window.Value!] = index == 0 ? "first" : "second";
                return window;
            }

            if (name == "frames" && (!childDocuments.Contains(target)
                || ReferenceEquals(target, cyclicDocument)))
            {
                return frames;
            }

            return name == "name" && names.TryGetValue(target, out string? frameName)
                ? frameName : native.Invoke(target, name, arguments, assign);
        }
    }

    [Theory]
    [InlineData("innerText", false)]
    [InlineData("dispatchEvent", true)]
    public async Task IeLinkCancellationRespectsTheClickBoundary(
        string cancelAfterMember, bool clickSent)
    {
        using var cancellation = new CancellationTokenSource();
        var runtime = new RecordingHtmlRuntime(eventsAvailable: true,
            afterInvoke: member =>
            {
                if (member == cancelAfterMember)
                {
                    cancellation.Cancel();
                }
            });
        await using var session = new ComSession(runtime);
        Value document = await CreateDocumentAsync(session,
            """<a id="selected" href="#end">Open</a>""");
        Value window = await session.InvokeAsync(document,
            "parentWindow", [], null, default);
        var args = new CallArguments([window, Value.String("Open")]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.ActivateHtmlLinkAsync(args, cancellation.Token).AsTask());
        Assert.Equal(clickSent ? ["dispatchEvent:selected"] : Array.Empty<string>(),
            runtime.Activations);
    }

    [Fact]
    public async Task IeLinkDoesNotRetryAnEventRejectedByTheProvider()
    {
        bool reject = true;
        var runtime = new RecordingHtmlRuntime(eventsAvailable: true,
            afterInvoke: member =>
            {
                if (member == "dispatchEvent" && reject)
                {
                    reject = false;
                    throw ComOperationErrors.Record(new COMException(
                        "クリック通知が拒否されました。",
                        unchecked((int)0x80070005)));
                }
            });
        var automation = new WindowsAutomation(_ => { },
            swappedMouseButtons: false, comRuntime: runtime);
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync("""
            doc = CREATEOLEOBJ("htmlfile")
            doc.write("<a id='target' href='#target'>Open</a>")
            doc.close()
            win = doc.parentWindow
            TRY
                PRINT IELINK(win, "Open")
            EXCEPT
                PRINT "rejected"
            ENDTRY
            PRINT IELINK(win, "Open")
            """, output.Add);
        Assert.Equal(["rejected", "1"], output);
        Assert.Equal(["dispatchEvent:target", "dispatchEvent:target"],
            runtime.Activations);
    }

    [Fact]
    public async Task IeLinkReturnsFalseForReservedPrefixes()
    {
        var runtime = new RecordingHtmlRuntime(eventsAvailable: true);
        await using var session = new ComSession(runtime);
        Value document = await CreateDocumentAsync(session, """
            <a id="target" name="token" title="token" href="#token">Open</a>
            <a id="literal" href="#literal">ID=target</a>
            <a href="#literal">NAME=token</a>
            <a href="#literal">ALT=token</a>
            <a href="#literal">HREF=#token</a>
            <a id="image" href="#image"><img alt="token"></a>
            """);
        Value window = await session.InvokeAsync(document,
            "parentWindow", [], null, default);

        foreach (string text in new[] { "NAME=token", "ID=target",
            "ALT=token", "HREF=#token" })
        {
            Value result = await session.ActivateHtmlLinkAsync(
                new CallArguments([window, Value.String(text)]), default);
            Assert.Equal(0, result.Number);
        }

        Assert.Empty(runtime.Activations);
    }

    [Fact]
    public async Task IeLinkUsesWhitespaceFromTheRenderedText()
    {
        var runtime = new RecordingHtmlRuntime(eventsAvailable: true);
        await using var session = new ComSession(runtime);
        Value document = await CreateDocumentAsync(session, """
            <a id="space" href="#space">Open    <b>file</b></a>
            <a id="break" href="#break">Open<br>file</a>
            <a id="nbsp" href="#nbsp">Open&nbsp;file</a>
            """);
        Value window = await session.InvokeAsync(document,
            "parentWindow", [], null, default);

        (string Text, int Occurrence, int Expected)[] cases =
        [
            ("Open file", 1, 1),
            ("Open\r\nfile", 1, 1),
            ("Open file", 2, 1),
            ("Open\u00a0file", 1, 0),
            ("Open    file", 1, 0)
        ];
        foreach ((string text, int occurrence, int expected) in cases)
        {
            Value result = await session.ActivateHtmlLinkAsync(new CallArguments(
                [window, Value.String(text), Value.Numeric(occurrence), Value.Logical(true)]),
                default);
            Assert.Equal(expected, result.Number);
        }

        Assert.Equal(["dispatchEvent:space", "dispatchEvent:break",
            "dispatchEvent:nbsp"], runtime.Activations);
    }

    [Fact]
    [Trait("Priority", "Critical")]
    public async Task IeGetDataMatchesTagSelectorValuesIgnoringCase()
    {
        var output = new List<string>();
        var automation = new WindowsAutomation(_ =>
            throw new InvalidOperationException("画面操作は不要です。"));
        await new Interpreter(windows: automation).RunAsync("""
            doc = CREATEOLEOBJ("htmlfile")
            doc.write("<p id='Marker' class='Accent'>Upper</p><p id='marker' class='accent'>upper</p>")
            doc.close()
            win = doc.parentWindow
            PRINT IEGETDATA(win, "TAG=P", "ID=marker")
            PRINT IEGETDATA(win, "TAG=P", "ClassName=accent")
            PRINT IEGETDATA(win, "TAG=P", "innerText=upper")
            PRINT IEGETDATA(win, "TAG=P", "innerHTML=upper")
            """, output.Add);

        Assert.Equal(["Upper", "Upper", "Upper", "Upper"], output);
    }

    [Fact]
    [Trait("Priority", "Critical")]
    public async Task IeSetDataMatchesTagSelectorValuesIgnoringCase()
    {
        var output = new List<string>();
        var automation = new WindowsAutomation(_ =>
            throw new InvalidOperationException("画面操作は不要です。"));
        await new Interpreter(windows: automation).RunAsync("""
            doc = CREATEOLEOBJ("htmlfile")
            doc.write("<p id='Marker' class='Accent'>Upper</p><p id='marker' class='accent'>upper</p>")
            doc.close()
            win = doc.parentWindow
            firstTag = doc.getElementsByTagName("P").item(0)
            secondTag = doc.getElementsByTagName("P").item(1)
            DIM selectors[] = "ID=marker", "ClassName=accent", "innerText=upper", "innerHTML=upper"
            FOR selector IN selectors
                PRINT IESETDATA(win, "updated", "TAG=P", selector)
                PRINT firstTag.innerText
                PRINT secondTag.innerText
                firstTag.innerText = "Upper"
                secondTag.innerText = "upper"
            NEXT
            """, output.Add);

        Assert.Equal(
            ["1", "updated", "upper", "1", "updated", "upper",
                "1", "updated", "upper", "1", "updated", "upper"],
            output);
    }

    [Fact]
    public async Task IeGetDataCountsTableRowsAndCellsInDomOrder()
    {
        var output = new List<string>();
        var automation = new WindowsAutomation(_ =>
            throw new InvalidOperationException("画面操作は不要です。"));
        await new Interpreter(windows: automation).RunAsync("""
            doc = CREATEOLEOBJ("htmlfile")
            doc.write("<table><tr><td>first</td></tr></table>")
            doc.write("<table id='selected'><thead><tr><th colspan='2'>head</th></tr></thead>")
            doc.write("<tbody><tr><td rowspan='2'>tall</td><td>right</td></tr>")
            doc.write("<tr><td>last</td></tr></tbody></table>")
            doc.close()
            win = doc.parentWindow
            PRINT IEGETDATA(win, "TAG=TABLE", 2, 1, 1)
            PRINT VARTYPE(IEGETDATA(win, "TAG=TABLE", 2, 1, 2))
            PRINT IEGETDATA(win, "TAG=TABLE", 2, 2, 2)
            PRINT IEGETDATA(win, "TAG=TABLE", "ID=selected", 3, 1)
            PRINT VARTYPE(IEGETDATA(win, "TAG=TABLE", 2, 3, 2))
            tableObj = doc.getElementById("selected")
            rowObj = tableObj.rows.item(1)
            PRINT IEGETDATA(win, "TAG=TABLE", 2, 0, 1) = tableObj.innerText
            PRINT IEGETDATA(win, "TAG=TABLE", 2, 2, 0) = rowObj.innerText
            PRINT IEGETDATA(win, "TAG=TABLE", 2) = tableObj.innerText
            PRINT IEGETDATA(win, "TAG=TABLE", 2, 2) = rowObj.innerText
            PRINT VARTYPE(IEGETDATA(win, "TAG=TABLE", 2, 4, 1))
            PRINT VARTYPE(IEGETDATA(win, "TAG=TABLE", 2, -1, 1))
            PRINT VARTYPE(IEGETDATA(win, "TAG=TABLE", 2, 2, -1))
            """, output.Add);

        Assert.Equal(
            ["head", "0", "right", "last", "0", "1", "1", "1", "1",
                "0", "0", "0"],
            output);
    }

    [Fact]
    public async Task IeSetDataPreservesEarlierWritesWhenComRejectsAFormValue()
    {
        var runtime = new RecordingHtmlRuntime(eventsAvailable: true,
            rejectValue: "blocked");
        var output = new List<string>();
        var automation = new WindowsAutomation(_ =>
            throw new InvalidOperationException("画面操作は不要です。"),
            comRuntime: runtime);
        await new Interpreter(windows: automation).RunAsync("""
            doc = CREATEOLEOBJ("htmlfile")
            doc.write("<input name='first' value='old'><input name='second' value='keep'>")
            doc.close()
            win = doc.parentWindow
            PRINT IESETDATA(win, "saved", "first")
            TRY
                PRINT IESETDATA(win, "blocked", "second")
            EXCEPT
                PRINT "rejected"
            ENDTRY
            PRINT IEGETDATA(win, "first")
            PRINT IEGETDATA(win, "second")
            PRINT IESETDATA(win, "retry", "second")
            PRINT IEGETDATA(win, "second")
            """, output.Add);

        Assert.Equal(["1", "rejected", "saved", "keep", "1", "retry"], output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Priority", "Critical")]
    public async Task IeSetDataUsesOptionLabelsForNamedSelectElements(bool multiple)
    {
        var output = new List<string>();
        var automation = new WindowsAutomation(_ =>
            throw new InvalidOperationException("画面操作は不要です。"));
        string attribute = multiple ? " multiple" : "";
        await new Interpreter(windows: automation).RunAsync($$"""
            doc = CREATEOLEOBJ("htmlfile")
            doc.write("<select id='choice' name='choice'{{attribute}}><option value='a' selected>Alpha</option><option value='b'>Beta</option></select>")
            doc.close()
            win = doc.parentWindow
            choice = doc.getElementById("choice")
            PRINT IEGETDATA(win, "choice")
            PRINT IESETDATA(win, "b", "choice")
            PRINT IEGETDATA(win, "choice")
            PRINT choice.selectedIndex
            PRINT IESETDATA(win, "Beta", "choice")
            PRINT choice.selectedIndex
            PRINT IEGETDATA(win, "choice", "Beta")
            PRINT IEGETDATA(win, "choice", "b")
            PRINT IESETDATA(win, "alpha", "choice")
            PRINT IEGETDATA(win, "choice")
            """, output.Add);

        Assert.Equal(["Alpha", "0", "", "-1", "1", "1", "", "Beta",
            "1", "Alpha"], output);
    }

    [Fact]
    public async Task IeSetDataKeepsFirstSelectionWhenSecondWriteIsRejected()
    {
        var runtime = new RecordingHtmlRuntime(eventsAvailable: true,
            rejectSelectedWrite: 2);
        var automation = new WindowsAutomation(_ => { },
            swappedMouseButtons: false, comRuntime: runtime);
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync("""
            doc = CREATEOLEOBJ("htmlfile")
            doc.write("<select name='choice' multiple><option>Alpha</option><option>Beta</option></select>")
            doc.close()
            win = doc.parentWindow
            TRY
                IESETDATA(win, "Alpha" + CHR(9) + "Beta", "choice")
            EXCEPT
                PRINT "rejected"
            ENDTRY
            PRINT IEGETDATA(win, "choice")
            PRINT IESETDATA(win, "Beta", "choice")
            PRINT IEGETDATA(win, "choice")
            """, output.Add);
        Assert.Equal(["rejected", "Alpha", "1", "Beta"], output);
    }

    [Fact]
    [Trait("Priority", "Critical")]
    public async Task IeGetDataJoinsSelectedLabelsInOptionOrder()
    {
        var output = new List<string>();
        var automation = new WindowsAutomation(_ =>
            throw new InvalidOperationException("画面操作は不要です。"));
        await new Interpreter(windows: automation).RunAsync("""
            doc=CREATEOLEOBJ("htmlfile")
            doc.write("<select name='choice' id='choice' multiple><option value='a'>Same</option><option value='b' selected>Same</option><option value='c' selected>Other</option></select>")
            doc.close()
            win=doc.parentWindow
            choice=doc.getElementById("choice")
            PRINT IEGETDATA(win,"choice")
            PRINT IEGETDATA(win,"choice","b")
            PRINT VARTYPE(IEGETDATA(win,"choice","c"))
            PRINT IESETDATA(win,"Same","choice")
            PRINT choice.selectedIndex
            PRINT choice.options.item(0).selected
            PRINT choice.options.item(1).selected
            PRINT choice.options.item(2).selected
            PRINT IESETDATA(win,"Same"+CHR(9)+"Other","choice")
            PRINT IEGETDATA(win,"choice")
            PRINT IESETDATA(win,"Missing"+CHR(9)+"Other","choice")
            PRINT IEGETDATA(win,"choice")
            PRINT IESETDATA(win,"Same"+CHR(9)+"Same","choice")
            PRINT IEGETDATA(win,"choice")
            PRINT IESETDATA(win,"Missing"+CHR(9)+"Absent","choice")
            PRINT choice.selectedIndex
            PRINT VARTYPE(IEGETDATA(win,"choice"))
            PRINT "["+IEGETDATA(win,"choice")+"]"
            PRINT VARTYPE(IEGETDATA(win,"choice","a"))
            """, output.Add);

        Assert.Equal(
            ["Same\tOther", "Same\tOther", "0", "1", "0",
                "True", "False", "False", "1", "Same\tOther",
                "1", "Other", "1", "Same", "0", "-1", "258", "[]", "0"],
            output);
    }

    [Fact]
    [Trait("Priority", "Critical")]
    public async Task IeGetDataMatchesCurrentValuesIgnoringCase()
    {
        var output = new List<string>();
        var automation = new WindowsAutomation(_ =>
            throw new InvalidOperationException("画面操作は不要です。"));
        await new Interpreter(windows: automation).RunAsync("""
            doc = CREATEOLEOBJ("htmlfile")
            doc.write("<input name='field' value='Key'><input name='field' value='key'><input name='field' value='Key'>")
            doc.close()
            win = doc.parentWindow
            PRINT IEGETDATA(win, "field", "Key", 2)
            PRINT IEGETDATA(win, "field", "key", 1)
            PRINT IEGETDATA(win, "field", "key", 3)
            PRINT IEGETDATA(win, "field", "key", 0)
            PRINT IEGETDATA(win, "field", "key", -1)
            PRINT IEGETDATA(win, "field", "Key", 4) = EMPTY
            """, output.Add);

        Assert.Equal(["key", "Key", "Key", "Key", "Key", "1"], output);
    }

    [Fact]
    [Trait("Priority", "Critical")]
    public async Task IeSetDataCountsOnlyFieldsMatchingTheCurrentValue()
    {
        var output = new List<string>();
        var automation = new WindowsAutomation(_ =>
            throw new InvalidOperationException("画面操作は不要です。"));
        await new Interpreter(windows: automation).RunAsync("""
            doc = CREATEOLEOBJ("htmlfile")
            doc.write("<input name='field' value='Key'><input name='field' value='key'><input name='field' value='Key'>")
            doc.close()
            win = doc.parentWindow
            PRINT IESETDATA(win, "last", "field", "Key", 2)
            PRINT IESETDATA(win, "unused", "field", "Key", 2)
            PRINT IESETDATA(win, "lower", "field", "key", 0)
            PRINT IESETDATA(win, "first", "field", "Key", -1)
            PRINT IEGETDATA(win, "field")
            PRINT IEGETDATA(win, "field", , 2)
            PRINT IEGETDATA(win, "field", , 3)
            PRINT IESETDATA(win, "negative", "field", "lower", -1)
            PRINT IEGETDATA(win, "field")
            """, output.Add);

        Assert.Equal(
            ["1", "1", "1", "0", "lower", "last", "unused", "1", "negative"],
            output);
    }

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task IeSourceFunctionsReturnFailureWhenComDeniesMarkup()
    {
        var output = new List<string>();
        var runtime = new RecordingHtmlRuntime(
            eventsAvailable: true, rejectMarkup: true);
        var automation = new WindowsAutomation(_ =>
            throw new InvalidOperationException("画面操作は不要です。"),
            comRuntime: runtime);

        await new Interpreter(windows: automation).RunAsync("""
            doc = CREATEOLEOBJ("htmlfile")
            doc.write("<p>before</p>")
            doc.close()
            win = doc.parentWindow
            PRINT VARTYPE(IEGETSRC(win, "P"))
            PRINT VARTYPE(IESETSRC(win, "<span>after</span>", "P"))
            PRINT IESETSRC(win, "<span>after</span>", "P")
            PRINT IEGETDATA(win, "TAG=P")
            PRINT VARTYPE(IEGETDATA(win, "TAG=SPAN"))
            PRINT "retry"
            PRINT IEGETSRC(win, "P")
            PRINT IESETSRC(win, "<span>after</span>", "P")
            PRINT IEGETDATA(win, "TAG=SPAN")
            """, line =>
        {
            if (line == "retry")
            {
                runtime.RejectMarkup = false;
                return;
            }

            output.Add(line);
        });

        Assert.Equal(["0", "11", "0", "before", "0"], output.Take(5));
        Assert.Equal("<p>before</p>", output[5].Trim().ToLowerInvariant());
        Assert.Equal(["1", "after"], output.Skip(6));
    }

    [Theory]
    [InlineData("document")]
    [InlineData("getElementsByTagName")]
    [InlineData("item")]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task IeGetSourceCanRetryAfterComDeniesTagLookup(string member)
    {
        const string denied = "取得前にCOMがアクセスを拒否しました。";
        var runtime = new RecordingHtmlRuntime(eventsAvailable: true);
        var automation = new WindowsAutomation(_ =>
            throw new InvalidOperationException("画面操作は不要です。"),
            comRuntime: runtime);
        var output = new List<string>();

        await new Interpreter(windows: automation).RunAsync("""
            doc = CREATEOLEOBJ("htmlfile")
            doc.write("<p>before</p>")
            doc.close()
            win = doc.parentWindow
            PRINT "deny"
            TRY
                PRINT VARTYPE(IEGETSRC(win, "P"))
            EXCEPT
                PRINT TRY_ERRMSG
            ENDTRY
            PRINT "retry"
            PRINT IEGETSRC(win, "P")
            PRINT VARTYPE(IEGETSRC(win, "P", 2))
            """, line =>
        {
            switch (line)
            {
                case "deny":
                    runtime.DeniedReadMember = member;
                    break;
                case "retry":
                    runtime.DeniedReadMember = null;
                    break;
                default:
                    output.Add(line);
                    break;
            }
        });

        Assert.Collection(output,
            error => Assert.Contains(denied, error),
            markup => Assert.Equal("<p>before</p>", markup.Trim().ToLowerInvariant()),
            missing => Assert.Equal("0", missing));
    }

    [Fact]
    [Trait("Category", "Headless")]
    public async Task IeSetSourceKeepsMarkupAppliedBeforeAComError()
    {
        var output = new List<string>();
        bool errorReported = false;
        var runtime = new RecordingHtmlRuntime(eventsAvailable: true,
            afterInvoke: name =>
            {
                if (name == "outerHTML" && !errorReported)
                {
                    errorReported = true;
                    throw ComOperationErrors.Record(new COMException(
                        "更新後に COM がエラーを返しました。",
                        unchecked((int)0x80070005)));
                }
            });
        var automation = new WindowsAutomation(_ =>
            throw new InvalidOperationException("画面操作は不要です。"),
            comRuntime: runtime);

        await new Interpreter(windows: automation).RunAsync("""
            doc = CREATEOLEOBJ("htmlfile")
            doc.write("<p>before</p><p>untouched</p>")
            doc.close()
            win = doc.parentWindow
            changed = IESETSRC(win, "<span>after</span>", "P")
            PRINT VARTYPE(changed)
            PRINT changed
            PRINT IEGETDATA(win, "TAG=SPAN")
            PRINT IEGETDATA(win, "TAG=P")
            PRINT IESETSRC(win, "<b>retry</b>", "SPAN")
            PRINT IEGETDATA(win, "TAG=B")
            """, output.Add);

        Assert.True(errorReported);
        Assert.Equal(["11", "0", "after", "untouched", "1", "retry"],
            output);
    }

    private sealed class RecordingHtmlRuntime(
        bool eventsAvailable, Action<string>? afterInvoke = null,
        string? rejectValue = null, int rejectSelectedWrite = 0,
        bool rejectMarkup = false) : IComRuntime
    {
        private readonly NativeComRuntime native = new();
        private readonly object clickEvent = new();
        private int selectedWrites;

        internal bool RejectMarkup { get; set; } = rejectMarkup;

        internal string? DeniedReadMember
        {
            get;
            set;
        }

        internal List<string> Activations { get; } = [];

        public object Create(string name) => native.Create(name);

        public object GetActive(string name) => native.GetActive(name);

        public void Release(object target) => native.Release(target);

        public object? Invoke(object target, string name, object?[] arguments, bool assign)
        {
            if (!assign && name == DeniedReadMember)
            {
                throw ComOperationErrors.Record(new COMException(
                    "取得前にCOMがアクセスを拒否しました。", unchecked((int)0x80070005)));
            }

            if (RejectMarkup && name == "outerHTML")
            {
                throw ComOperationErrors.Record(new COMException(
                    "タグのHTMLへのアクセスが拒否されました。",
                    unchecked((int)0x80070005)));
            }

            if (assign && ((name == "value" && rejectValue is not null
                && Equals(Assert.Single(arguments), rejectValue))
                || (name == "selected" && ++selectedWrites == rejectSelectedWrite)))
            {
                throw ComOperationErrors.Record(new COMException(
                    "フォーム値の設定が拒否されました。", unchecked((int)0x80070005)));
            }

            switch (name)
            {
                case "createEvent":
                    Assert.Equal("MouseEvents", Assert.Single(arguments));
                    return eventsAvailable ? clickEvent : throw ComOperationErrors.Record(
                        new COMException("イベントAPIはありません。", unchecked((int)0x80020006)));
                case "initEvent":
                    Assert.Same(clickEvent, target);
                    Assert.Equal(["click", true, true], arguments);
                    return null;
                case "dispatchEvent":
                    Assert.Same(clickEvent, Assert.Single(arguments));
                    break;
                case "click":
                    Assert.Empty(arguments);
                    break;
                default:
                    object? result = native.Invoke(target, name, arguments, assign);
                    afterInvoke?.Invoke(name);
                    return result;
            }

            ComVariantValue id = Assert.IsType<ComVariantValue>(native.Invoke(target, "id", [], false));
            Activations.Add($"{name}:{id.Value}");
            afterInvoke?.Invoke(name);
            return null;
        }
    }

    private static async Task<Value> CreateDocumentAsync(ComSession session, string body)
    {
        Value document = await session.CreateAsync("htmlfile", false, default);
        var html = Value.String($"<html><body>{body}</body></html>");
        await session.InvokeAsync(document, "write", [html], null, default);
        await session.InvokeAsync(document, "close", [], null, default);
        return document;
    }
}
