namespace UWSCSharp.Core;

/// <summary>
/// 転送先ID、任意の座標、ディレクトリとファイル名列を保持します。
/// </summary>
internal sealed record DropFileRequest(int WindowId, int? X, int? Y,
    string Directory, IReadOnlyList<string> Names)
{
    internal static DropFileRequest Read(IReadOnlyList<Value> values)
    {
        BuiltinFunctions.CheckArguments(BuiltinFunctionNames.DropFile, values, 3, 36);
        int id = NumericOperations.Integer(values[0]);
        if (id <= 0)
        {
            throw Error("正のウィンドウIDを指定してください。");
        }

        bool coordinates = values[1].Kind == ValueKind.Number
            && values[2].Kind == ValueKind.Number;
        int directoryIndex = coordinates ? 3 : 1;
        if (values.Count < directoryIndex + 2)
        {
            throw Error("ディレクトリとファイル名が必要です。");
        }

        int? x = coordinates ? NumericOperations.Integer(values[1]) : null;
        int? y = coordinates ? NumericOperations.Integer(values[2]) : null;
        Value directory = values[directoryIndex];
        if (directory.Kind != ValueKind.String || directory.Text.Length == 0
            || directory.Text.Contains('\0'))
        {
            throw Error("ディレクトリにはNULを含まない文字列を指定してください。");
        }

        var pending = new Stack<Value>();
        for (int index = values.Count - 1; index > directoryIndex; index--)
        {
            pending.Push(values[index]);
        }

        var names = new List<string>();
        int visited = 0;
        int maximum = UwscArray.MaximumElements + 36;
        while (pending.TryPop(out Value value))
        {
            visited++;
            if (value.Kind == ValueKind.Array)
            {
                UwscArray array = value.AsArray();
                if (array.Count > maximum - visited - pending.Count)
                {
                    throw Error("ファイル名列の要素数が上限を超えています。");
                }

                for (int index = array.Count - 1; index >= 0; index--)
                {
                    pending.Push(array.Values[index]);
                }

                continue;
            }

            if (value.Kind is not (ValueKind.Empty or ValueKind.Omitted
                or ValueKind.String or ValueKind.Number))
            {
                throw Error("ファイル名には文字列・数値またはその配列を指定してください。");
            }

            if (value.Kind == ValueKind.Number)
            {
                _ = NumericOperations.Number(value);
            }

            string name = value.Kind == ValueKind.Omitted ? "" : value.Display();
            if (name.Contains('\0'))
            {
                throw Error("ファイル名にNULは使えません。");
            }

            if (name.Length != 0)
            {
                names.Add(name);
                if (names.Count > UwscArray.MaximumElements)
                {
                    throw Error("ファイル名列の要素数が上限を超えています。");
                }
            }
        }

        if (names.Count == 0)
        {
            throw Error("送信するファイル名がありません。");
        }

        return new(id, x, y, directory.Text, names.AsReadOnly());
    }

    private static InvalidOperationException Error(string message)
        => new($"{BuiltinFunctionNames.DropFile}: {message}");
}
