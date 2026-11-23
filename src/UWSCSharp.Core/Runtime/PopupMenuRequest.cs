namespace UWSCSharp.Core;

/// <summary>
/// 配列位置を保持したメニュー階層と表示位置を表します。
/// </summary>
internal sealed record PopupMenuRequest(IReadOnlyList<PopupMenuItem> Items, int? X, int? Y)
{
    internal static PopupMenuRequest Read(IReadOnlyList<Value> arguments,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (arguments.Count is < 1 or > 3 || arguments[0].Kind != ValueKind.Array)
        {
            throw new InvalidOperationException("POPUPMENUには項目の配列と省略可能な座標を指定してください。");
        }

        UwscArray array = arguments[0].AsArray();
        if (array.UpperBounds.Count != 1 || array.Values.Count > 4096)
        {
            throw new InvalidOperationException("POPUPMENUは4096要素までの1次元配列に対応します。");
        }

        List<PopupMenuItem> root = [];
        var levels = new Stack<List<PopupMenuItem>>();
        List<PopupMenuItem> current = root;
        int skipped = 0;
        for (int offset = 0; offset < array.Values.Count; offset++)
        {
            token.ThrowIfCancellationRequested();
            if (array.Values[offset].Kind == ValueKind.Array)
            {
                throw new InvalidOperationException("POPUPMENUの項目に配列は指定できません。");
            }

            string text = BuiltinText.Convert(array.Values[offset]);
            if (text.Contains('\0'))
            {
                throw new InvalidOperationException("POPUPMENUの項目にNULは指定できません。");
            }

            if (text.Length == 0)
            {
                skipped++;
                continue;
            }

            if (text.StartsWith('{'))
            {
                if (text.Length == 1 || levels.Count >= 16 || current.Count == 0
                    || current[^1].Separator || current[^1].Children.Count != 0)
                {
                    throw InvalidHierarchy();
                }

                levels.Push(current);
                current = current[^1].Children;
                text = text[1..];
            }

            bool close = text.EndsWith('}');
            if (close)
            {
                text = text[..^1];
                if (text.Length == 0 || levels.Count == 0)
                {
                    throw InvalidHierarchy();
                }
            }

            int index = checked(array.LowerBounds[0] + offset);
            current.Add(new(text, index, [])
            {
                SelectionIndex = offset - skipped
            });
            if (close)
            {
                current = levels.Pop();
            }
        }

        token.ThrowIfCancellationRequested();
        if (levels.Count != 0)
        {
            throw InvalidHierarchy();
        }

        bool coordinates = arguments.Count == 3
            && arguments[1].Kind != ValueKind.Omitted && arguments[2].Kind != ValueKind.Omitted;
        return new(root, coordinates ? NumericOperations.Integer(arguments[1]) : null,
            coordinates ? NumericOperations.Integer(arguments[2]) : null);
    }

    private static InvalidOperationException InvalidHierarchy() =>
        new("POPUPMENUの階層は、親項目の後で{を開始し、項目末尾の}で閉じてください。単独の波括弧には対応しません。");
}

/// <summary>
/// 表示名、元配列の添字、選択番号と子項目を保持します。
/// </summary>
internal sealed record PopupMenuItem(string Text, int Index,
    List<PopupMenuItem> Children)
{
    internal int SelectionIndex { get; init; } = Index;

    internal bool Separator => Text == "-";
}
