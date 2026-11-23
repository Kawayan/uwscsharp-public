namespace UWSCSharp.Core;

public static partial class ScriptCompiler
{
    private sealed partial class Parser
    {
        private static bool IsDialogOption(string name) =>
            name.ToUpperInvariant() is Keywords.DefaultFont or Keywords.DlgTitle or Keywords.Position;

        private static bool IsStopOption(string name) =>
            name.ToUpperInvariant() is Keywords.TopStopForm or Keywords.NoStopHotkey;

        private static bool IsFlagOption(string name) =>
            IsStopOption(name) || name.ToUpperInvariant() is
                Keywords.FixBalloon or Keywords.OnlySapi;

        private static IEnumerable<string> SplitOptions(string text)
        {
            string[] items = [.. Split(text, ',')];
            for (int i = 0; i < items.Length; i++)
            {
                string item = items[i];
                int equal = Find(item, "=");
                if (equal >= 0 && item[..equal].Trim().Equals(
                    Keywords.Position, StringComparison.OrdinalIgnoreCase)
                    && i + 1 < items.Length && Find(items[i + 1], "=") < 0
                    && !IsFlagOption(items[i + 1].Trim()))
                {
                    item += "," + items[++i];
                }

                yield return item;
            }
        }

        private Statement ParseRuntimeOptions(string text, int line)
        {
            string[] items = [.. SplitOptions(text)];
            if (!items.Any(item => Find(item, "=") is int equal
                && ((equal >= 0 && (IsDialogOption(item[..equal].Trim())
                    || IsFlagOption(item[..equal].Trim())))
                    || (equal < 0 && IsFlagOption(item.Trim())))))
            {
                return ParseLogOptions(text, line);
            }

            List<RuntimeOption> options = [];
            foreach (string item in items)
            {
                int equal = Find(item, "=");
                string name = (equal < 0 ? item : item[..equal]).Trim().ToUpperInvariant();
                if (IsFlagOption(name))
                {
                    if (equal >= 0)
                    {
                        throw Error("このOPTIONには値を指定しないでください。", line);
                    }

                    options.Add(new(name, []));
                    continue;
                }
                if (equal < 0 || (!IsDialogOption(name) && !IsLogOption(name)))
                {
                    throw Error("実行位置のOPTIONには対応する設定を指定してください。", line);
                }

                string[] expressions = name == Keywords.Position
                    ? [.. Split(item[(equal + 1)..], ',')] : [item[(equal + 1)..]];
                if (expressions.Length != (name == Keywords.Position ? 2 : 1)
                    || expressions.Any(string.IsNullOrWhiteSpace))
                {
                    throw Error("OPTIONの値の数が不正です。", line);
                }

                options.Add(new(name, [.. expressions.Select(value => ParseExpression(value, line))]));
            }

            return new RuntimeOptionsStatement(line, options.AsReadOnly());
        }

        private static bool IsLogOption(string name) =>
            name.ToUpperInvariant() is Keywords.LogPath or Keywords.LogLines
                or Keywords.LogFile;

        private LogOptionsStatement ParseLogOptions(string text, int line)
        {
            var options = new List<LogOption>();
            foreach (string item in Split(text, ','))
            {
                int equal = Find(item, "=");
                string name = equal < 0 ? item.Trim() : item[..equal].Trim();
                if (equal < 0 || !IsLogOption(name))
                {
                    throw Error("実行位置のOPTIONにはログ設定の代入を指定してください。", line);
                }

                options.Add(new LogOption(name.ToUpperInvariant(),
                    ParseExpression(item[(equal + 1)..], line)));
            }

            return new LogOptionsStatement(line, options.AsReadOnly());
        }
    }
}
