using System.Runtime.InteropServices;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

internal sealed partial class ComSession
{
    private enum HtmlOperation
    {
        Read,
        Set,
        Link,
        ReadSource,
        SetSource,
        Frame
    }

    internal ValueTask<Value> ReadHtmlDataAsync(
        CallArguments args, CancellationToken token) =>
        SubmitAsync(() => AccessHtmlData(args, HtmlOperation.Read, token), token);

    internal ValueTask<Value> SetHtmlDataAsync(
        CallArguments args, CancellationToken token) =>
        SubmitAsync(() => AccessHtmlData(args, HtmlOperation.Set, token), token);

    internal ValueTask<Value> ActivateHtmlLinkAsync(
        CallArguments args, CancellationToken token) =>
        SubmitAsync(() => AccessHtmlData(args, HtmlOperation.Link, token), token);

    internal ValueTask<Value> ReadHtmlSourceAsync(
        CallArguments args, CancellationToken token) =>
        SubmitAsync(() => AccessHtmlData(args, HtmlOperation.ReadSource, token), token);

    internal ValueTask<Value> SetHtmlSourceAsync(
        CallArguments args, CancellationToken token) =>
        SubmitAsync(() => AccessHtmlData(args, HtmlOperation.SetSource, token), token);

    internal ValueTask<Value> GetHtmlFrameAsync(
        CallArguments args, CancellationToken token) =>
        SubmitAsync(() => AccessHtmlData(args, HtmlOperation.Frame, token), token);

    private Value AccessHtmlData(
        CallArguments args, HtmlOperation operation, CancellationToken token)
    {
        Value? assigned = operation is HtmlOperation.Set or HtmlOperation.SetSource
            ? args.Get(1) : null;
        int selectorPosition = assigned.HasValue ? 2 : 1;
        int filterPosition = selectorPosition + 1;
        Value notFound = operation is HtmlOperation.Read or HtmlOperation.ReadSource or HtmlOperation.Frame
            ? Value.Empty : Value.Logical(false);
        var temporary = new List<object>();
        var frameAncestors = new HashSet<object>(
            ReferenceEqualityComparer.Instance);
        var nativeFrameAncestors = new HashSet<nint>();
        Exception? primary = null;
        try
        {
            object target = Resolve(args.Get(0));
            object? document = Read(target, "document");
            if (document is null)
            {
                return notFound;
            }

            if (operation == HtmlOperation.Frame)
            {
                return GetFrame(document);
            }

            if (operation == HtmlOperation.Link)
            {
                return ActivateMatchingLink(document);
            }

            string selector = args.Str(selectorPosition);
            if (operation is HtmlOperation.ReadSource or HtmlOperation.SetSource)
            {
                return AccessTagSource(document, selector);
            }

            if (!selector.StartsWith("TAG=", StringComparison.OrdinalIgnoreCase))
            {
                (object? named, string? current) = SelectNamed(document, selector);
                if (named is null)
                {
                    return notFound;
                }

                return assigned is Value input ? WriteNamed(document, named, input)
                    : Value.String(current!);
            }

            string tag = selector[4..];
            object? element = SelectTag(document, tag);
            if (element is null)
            {
                return notFound;
            }

            if (assigned is not Value content)
            {
                return tag.ToUpperInvariant() switch
                {
                    "IMG" => Value.String(""),
                    "SELECT" => Value.String(ReadSelectedLabels(element,
                        Read(element, "type") as string) ?? ""),
                    "INPUT" or "TEXTAREA" => ReadText(element, "value"),
                    _ => ReadText(element, "innerText")
                };
            }

            return tag.Equals("IMG", StringComparison.OrdinalIgnoreCase)
                ? Activate(document, element, content)
                : Write(element, "innerText", InputText(content));
        }
        catch (Exception failure)
        {
            primary = failure;
            throw;
        }
        finally
        {
            ReleaseHtmlReferences(temporary, primary);
        }

        object? Invoke(object instance, string member,
            object?[] parameters, bool assign)
        {
            token.ThrowIfCancellationRequested();
            object? result = runtime.Invoke(instance, member, parameters, assign);
            if (result is ComVariantValue typed)
            {
                if (typed.Acquisitions is not null)
                {
                    temporary.AddRange(typed.Acquisitions);
                }

                return typed.Value;
            }

            return result;
        }

        object? Read(object instance, string member, params object?[] parameters)
        {
            try
            {
                return Invoke(instance, member, parameters, false);
            }
            catch (Exception failure) when (IsMissingHtmlMember(failure))
            {
                return null;
            }
        }

        Value Write(object instance, string member, object value)
        {
            try
            {
                _ = Invoke(instance, member, [value], true);
                return Value.Logical(true);
            }
            catch (Exception failure) when (IsMissingHtmlMember(failure))
            {
                return Value.Logical(false);
            }
        }

        Value WriteNamed(object document, object element, Value value)
        {
            string? type = Read(element, "type") as string;
            switch (type?.ToLowerInvariant())
            {
                case "text" or "checkbox" or "radio" or "button"
                    when Read(element, "disabled") is true:
                    return Value.Logical(false);
                case "checkbox":
                case "radio":
                    bool isChecked = ComOperationErrors.Validate(
                        () => NumericOperations.RuntimeNumber(value)) != 0;
                    return Write(element, "checked", isChecked);
                case "button":
                case "submit":
                case "image":
                    return value.Kind == ValueKind.Number
                        ? Activate(document, element, value)
                        : Write(element, "value", InputText(value));
                case "select-one":
                case "select-multiple":
                    if (Read(element, "disabled") is true)
                    {
                        return Value.Logical(false);
                    }

                    string text = InputText(value);
                    if (type.Equals("select-multiple",
                        StringComparison.OrdinalIgnoreCase) && text.Contains('\t'))
                    {
                        return WriteMultipleOptions(element, text);
                    }

                    object? options = Read(element, "options");
                    int selected = -1;
                    if (options is not null)
                    {
                        int count = Count(options);
                        for (int index = 0; index < count; index++)
                        {
                            object? option = At(options, index);
                            if (option is not null
                                && Read(option, "text") is string label
                                && string.Equals(label, text,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                selected = index;
                                break;
                            }
                        }
                    }

                    Value written = Write(element, "selectedIndex", selected);
                    return selected < 0 ? Value.Logical(false) : written;
                default:
                    return Write(element, "value", InputText(value));
            }
        }

        Value WriteMultipleOptions(object element, string text)
        {
            if (Read(element, "options") is not object options
                || Write(element, "selectedIndex", -1).Number == 0)
            {
                return Value.Logical(false);
            }

            var remaining = new HashSet<string>(text.Split('\t'),
                StringComparer.OrdinalIgnoreCase);
            bool matched = false;
            int count = Count(options);
            for (int index = 0; remaining.Count > 0 && index < count; index++)
            {
                if (At(options, index) is object option
                    && Read(option, "text") is string label
                    && remaining.Remove(label))
                {
                    if (Write(option, "selected", true).Number == 0)
                    {
                        return Value.Logical(false);
                    }

                    matched = true;
                }
            }

            return Value.Logical(matched);
        }

        Value GetFrame(object document)
        {
            string name = args.Str(1);
            bool byNumber = args.Get(1).Kind == ValueKind.Number
                || name.Length == 0
                || NumericOperations.TryParse(name, out _, false);
            int number = byNumber && name.Length != 0 ? ReadIndex(1) : 0;
            if (number < -1)
            {
                return notFound;
            }

            int count = 0;
            foreach ((ComVariantValue frame, int start) in EnumerateFrames(document))
            {
                count++;
                if (byNumber ? number != -1 && count == Math.Max(1, number)
                    : MatchesFrameName(frame.Value!, name))
                {
                    // 返却するウィンドウだけをセッションの所有参照へ移します。
                    temporary.RemoveRange(start, frame.Acquisitions?.Count ?? 0);
                    return Wrap(frame, token: token);
                }
            }

            return number == -1 ? Value.Numeric(count == 0 ? 0 : count + 1d)
                : notFound;
        }

        bool MatchesFrameName(object window, string name)
        {
            try
            {
                return string.Equals(Read(window, "name") as string, name,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception failure) when (IsFrameAccessDenied(failure))
            {
                return false;
            }
        }

        static bool IsFrameAccessDenied(Exception failure) =>
            ComOperationErrors.Contains(failure)
            && failure.HResult == unchecked((int)0x80070005);

        IEnumerable<(ComVariantValue Frame, int Start)> EnumerateFrames(object document)
        {
            var pending = new Stack<IEnumerator<(ComVariantValue Frame, int Start)>>();
            pending.Push(EnumerateDocument(document).GetEnumerator());
            try
            {
                while (pending.Count > 0)
                {
                    IEnumerator<(ComVariantValue Frame, int Start)> current =
                        pending.Peek();
                    if (!current.MoveNext())
                    {
                        pending.Pop().Dispose();
                        continue;
                    }

                    (ComVariantValue frame, int start) = current.Current;
                    yield return (frame, start);
                    token.ThrowIfCancellationRequested();
                    object? childDocument;
                    try
                    {
                        childDocument = Read(frame.Value!, "document");
                    }
                    catch (Exception failure) when (IsFrameAccessDenied(failure))
                    {
                        continue;
                    }

                    if (childDocument is not null)
                    {
                        pending.Push(EnumerateDocument(childDocument).GetEnumerator());
                    }
                }
            }
            finally
            {
                while (pending.Count > 0)
                {
                    pending.Pop().Dispose();
                }
            }
        }

        IEnumerable<(ComVariantValue Frame, int Start)> EnumerateDocument(object document)
        {
            nint identity = Marshal.IsComObject(document)
                ? Marshal.GetIUnknownForObject(document)
                : 0;
            try
            {
                bool added = identity == 0 ? frameAncestors.Add(document)
                    : nativeFrameAncestors.Add(identity);
                if (!added)
                {
                    yield break;
                }

                try
                {
                    foreach ((ComVariantValue Frame, int Start) frame
                        in EnumerateFrameChildren(document))
                    {
                        yield return frame;
                    }
                }
                finally
                {
                    if (identity == 0)
                    {
                        frameAncestors.Remove(document);
                    }
                    else
                    {
                        nativeFrameAncestors.Remove(identity);
                    }
                }
            }
            finally
            {
                if (identity != 0)
                {
                    Marshal.Release(identity);
                }
            }
        }

        IEnumerable<(ComVariantValue Frame, int Start)> EnumerateFrameChildren(
            object document)
        {
            if (Read(document, "frames") is not object frames)
            {
                yield break;
            }

            int count = Count(frames);
            for (int index = 0; index < count; index++)
            {
                token.ThrowIfCancellationRequested();
                object? result;
                try
                {
                    result = runtime.Invoke(frames, "item", [index], false);
                }
                catch (Exception failure) when (IsMissingHtmlMember(failure)
                    || ComOperationErrors.Contains(failure)
                        && failure.HResult is unchecked((int)0x8002000b)
                            or unchecked((int)0x80004005))
                {
                    continue;
                }

                if (result is not ComVariantValue frame)
                {
                    continue;
                }

                int start = temporary.Count;
                if (frame.Acquisitions is not null)
                {
                    temporary.AddRange(frame.Acquisitions);
                }

                if (frame.Type is not (9 or 13) || frame.Value is null)
                {
                    continue;
                }

                yield return (frame, start);
            }
        }

        Value AccessTagSource(object document, string tag)
        {
            if (tag.StartsWith("TAG=", StringComparison.OrdinalIgnoreCase))
            {
                tag = tag[4..];
            }

            object? tags = Read(document, "getElementsByTagName", tag);
            int index = Math.Max(1, ReadIndex(filterPosition, 1)) - 1;
            object? element = tags is null ? null : At(tags, index);
            if (element is null)
            {
                return notFound;
            }

            string? markup = assigned is Value content ? InputText(content) : null;
            try
            {
                return markup is null ? ReadText(element, "outerHTML")
                    : Write(element, "outerHTML", markup);
            }
            catch (Exception failure) when (ComOperationErrors.Contains(failure))
            {
                // outerHTMLを扱えない要素は取得・更新の失敗として返します。
                return notFound;
            }
        }

        Value ActivateMatchingLink(object document)
        {
            string label = args.Str(1);
            object? links = Read(document, "getElementsByTagName", "A");
            if (links is null || label.Length == 0)
            {
                return notFound;
            }

            int occurrence = Math.Max(1, ReadIndex(2, 1));
            bool exact = args.Has(3) && ComOperationErrors.Validate(
                () => NumericOperations.RuntimeNumber(args.Get(3))) != 0;
            string prefix = label[..(label.IndexOf('=') + 1)]
                .ToUpperInvariant();
            if (prefix is "ID=" or "NAME=" or "ALT=" or "HREF=")
            {
                return notFound;
            }

            string? attribute = prefix switch
            {
                "TITLE=" => "title",
                "CLASS=" => "className",
                _ => null
            };
            object? link = attribute is null
                ? SelectMatchingElement(links, "innerText", label,
                    occurrence, StringComparison.OrdinalIgnoreCase, partial: !exact)
                : SelectMatchingElement(links, attribute, "", occurrence,
                    nullAsEmpty: true);
            return link is null ? notFound : Click(document, link);
        }

        Value Activate(object document, object element, Value value)
        {
            if (value.Kind != ValueKind.Number)
            {
                return Value.Logical(false);
            }

            return NumericOperations.RuntimeNumber(value) == 0
                ? Value.Logical(true) : Click(document, element);
        }

        Value Click(object document, object element)
        {
            try
            {
                object? clickEvent = Invoke(document, "createEvent", ["MouseEvents"], false);
                if (clickEvent is not null)
                {
                    _ = Invoke(clickEvent, "initEvent", ["click", true, true], false);
                    _ = Invoke(element, "dispatchEvent", [clickEvent], false);
                    return Value.Logical(true);
                }
            }
            catch (Exception failure) when (IsMissingHtmlMember(failure))
            {
                // イベントAPIがない文書では要素のclickを使います。
            }

            try
            {
                _ = Invoke(element, "click", [], false);
                return Value.Logical(true);
            }
            catch (Exception failure) when (IsMissingHtmlMember(failure))
            {
                return Value.Logical(false);
            }
        }

        static string InputText(Value value) =>
            ComOperationErrors.Validate(() => BuiltinText.Convert(value));

        int Count(object collection) =>
            Read(collection, "length") is int length ? length : 0;

        object? At(object collection, int index) =>
            index >= 0 && index < Count(collection)
                ? Read(collection, "item", index) : null;

        int ReadIndex(int position, int fallback = 0)
        {
            if (!args.Has(position))
            {
                return fallback;
            }

            return ComOperationErrors.Validate(() =>
            {
                double number = Math.Truncate(
                    NumericOperations.RuntimeNumber(args.Get(position)));
                if (number < int.MinValue || number > int.MaxValue)
                {
                    throw new InvalidOperationException("候補番号が整数の範囲外です。");
                }

                return (int)number;
            });
        }

        Value ReadText(object element, string member) =>
            Read(element, member) is string value ? Value.String(value) : Value.Empty;

        (object? Element, string? Value) SelectNamed(object document, string name)
        {
            object? collection = Read(document, "getElementsByName", name);
            if (collection is null)
            {
                return (null, null);
            }

            string filter = args.Str(filterPosition);
            int occurrence = Math.Max(1, ReadIndex(filterPosition + 1, 1));
            int count = Count(collection);
            for (int index = 0; index < count; index++)
            {
                object? element = Read(collection, "item", index);
                if (element is null)
                {
                    continue;
                }

                string? value = Read(element, "value") as string;
                string? type = Read(element, "type") as string;
                bool select = type?.ToLowerInvariant()
                    is "select-one" or "select-multiple";
                if (value is null && select && filter.Length == 0)
                {
                    value = "";
                }

                if (value is not null
                    && (filter.Length == 0
                        || string.Equals(value, filter,
                            StringComparison.OrdinalIgnoreCase))
                    && --occurrence == 0)
                {
                    if (select && !assigned.HasValue)
                    {
                        value = ReadSelectedLabels(element, type) ?? value;
                    }

                    return (element, value);
                }
            }

            return (null, null);
        }

        string? ReadSelectedLabels(object element, string? type)
        {
            if (Read(element, "options") is not object options
                || Read(element, "selectedIndex") is not int selected
                || selected < 0 || At(options, selected) is not object option)
            {
                return null;
            }

            if (!string.Equals(type, "select-multiple",
                StringComparison.OrdinalIgnoreCase))
            {
                return Read(option, "text") as string ?? "";
            }

            var labels = new List<string>();
            int count = Count(options);
            for (int index = selected; index < count; index++)
            {
                if (At(options, index) is object candidate
                    && Read(candidate, "selected") is true)
                {
                    labels.Add(Read(candidate, "text") as string ?? "");
                }
            }

            return string.Join('\t', labels);
        }

        object? SelectTag(object document, string tag)
        {
            object? collection = Read(document, "getElementsByTagName", tag);
            if (collection is null)
            {
                return null;
            }

            string selection = args.Str(filterPosition);
            int separator = selection.IndexOf('=');
            string? member = separator < 0 ? null
                : selection[..separator].ToUpperInvariant() switch
                {
                    "ID" => "id",
                    "INNERHTML" => "innerHTML",
                    "INNERTEXT" => "innerText",
                    "CLASSNAME" => "className",
                    _ => null
                };
            object? element;
            if (member is not null)
            {
                element = SelectMatchingElement(
                    collection, member, selection[(separator + 1)..],
                    comparison: StringComparison.OrdinalIgnoreCase);
            }
            else if (assigned.HasValue && tag.Equals("IMG", StringComparison.OrdinalIgnoreCase)
                && selection.Length > 0 && !NumericOperations.TryParse(selection, out _))
            {
                int occurrence = Math.Max(1, ReadIndex(filterPosition + 1, 1));
                element = SelectMatchingElement(collection, "src", selection,
                    occurrence, StringComparison.OrdinalIgnoreCase, partial: true);
            }
            else if (separator >= 0)
            {
                return null;
            }
            else
            {
                int index = selection.Length == 0 ? 0 : Math.Max(1, ReadIndex(filterPosition)) - 1;
                element = At(collection, index);
            }

            if (element is not null && tag.Equals("TABLE", StringComparison.OrdinalIgnoreCase)
                && !assigned.HasValue && args.Has(3))
            {
                int row = ReadIndex(3);
                if (row == 0)
                {
                    return element;
                }

                object? rows = Read(element, "rows");
                element = rows is not null && row > 0 ? At(rows, row - 1) : null;
                if (element is not null && args.Has(4))
                {
                    int column = ReadIndex(4);
                    if (column == 0)
                    {
                        return element;
                    }

                    object? cells = Read(element, "cells");
                    element = cells is not null && column > 0
                        ? At(cells, column - 1) : null;
                }
            }

            return element;
        }

        object? SelectMatchingElement(object collection, string member,
            string expected, int occurrence = 1,
            StringComparison comparison = StringComparison.Ordinal,
            bool partial = false, bool nullAsEmpty = false)
        {
            int count = Count(collection);
            for (int index = 0; index < count; index++)
            {
                object? element = Read(collection, "item", index);
                if (element is null)
                {
                    continue;
                }

                object? value = Read(element, member);
                string? text = value is null && nullAsEmpty ? "" : value as string;
                if (text is not null
                    && (partial ? text.Contains(expected, comparison)
                        : string.Equals(text, expected, comparison))
                    && --occurrence == 0)
                {
                    return element;
                }
            }

            return null;
        }
    }

    private static bool IsMissingHtmlMember(Exception failure) =>
        ComOperationErrors.Contains(failure)
        && failure.HResult is unchecked((int)0x80020003)
            or unchecked((int)0x80020006);

    private void ReleaseHtmlReferences(List<object> temporary, Exception? primary)
    {
        var errors = new List<Exception>();
        for (int index = temporary.Count - 1; index >= 0; index--)
        {
            try
            {
                runtime.Release(temporary[index]);
            }
            catch (Exception failure)
            {
                errors.Add(failure);
            }
        }

        if (errors.Count > 0)
        {
            var cleanup = new AggregateException("HTMLのCOM参照を解放できません。", errors);
            if (primary is null)
            {
                throw cleanup;
            }

            primary.Data["UWSCSharp.CleanupErrors"] = cleanup;
        }
    }
}
