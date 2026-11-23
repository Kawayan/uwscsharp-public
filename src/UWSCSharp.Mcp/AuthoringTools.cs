using System.ComponentModel;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using UWSCSharp.Core;

namespace UWSCSharp.Mcp;

/// <summary>
/// UWSC#の仕様取得と実行を伴わない構文・呼出し診断を提供します。
/// </summary>
[McpServerToolType]
public sealed class AuthoringTools
{
    private static readonly TimeSpan HelpMarkupRegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ScriptAnalysisTimeout = TimeSpan.FromSeconds(2);

    private static readonly JsonElement Reference = Read("reference.json");
    private static readonly JsonElement Ledger = Read("features.json");
    private static readonly string ReferenceHash = Hash(Reference);
    private static readonly string LedgerHash = Hash(Ledger);
    private static readonly Dictionary<string, string> Documents = new()
    {
        ["authoring"] = "作成支援・依頼条件・入力条件・後処理・終了コード・検査の限界",
        ["language"] = "基本構文・変数・式",
        ["control-flow"] = "条件分岐・繰返し・IFB・FOR・WHILE・CALL",
        ["routines"] = "関数・手続き・FUNCTION・PROCEDURE・VAR・THREAD",
        ["data-types"] = "変数・定数・配列・HASHTBL・OPTION・DIM・PUBLIC・CONST"
    };
    private const string ValidationScope =
        "構文のみ。未知関数・引数数・型・ホスト公開範囲・実行成功・原版互換は未検査。外部CALLは拒否し、実行しません。";
    private const string CallAnalysisScope =
        "構文と直接呼出しの名前・引数形式を静的に確認します。警告0件でも実行成功を保証しません。型・値・到達可能性・メンバー解決・動的コード内・コールバック先・ホスト公開範囲・原版互換は未検査。外部CALLは拒否し、実行しません。行番号は文・宣言の開始行です。";

    /// <summary>
    /// 関数名・機能名・説明を検索します。
    /// </summary>
    [McpServerTool(Name = "search_reference", ReadOnly = true)]
    [Description("UWSC#の関数・構文・機能を検索し、仕様取得用の名前と機能IDを返します。")]
    public string SearchReference(string query, int limit = 10)
    {
        CheckText(query, 200);
        if (limit is < 1 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        var results = new List<object>();
        foreach (KeyValuePair<string, string> document in Documents)
        {
            if (Matches(document.Key + " " + document.Value, query))
            {
                results.Add(new
                {
                    name = document.Key,
                    description = document.Value,
                    feature = ""
                });
            }
        }
        foreach (JsonElement entry in Reference.GetProperty("entries").EnumerateArray())
        {
            string name = Text(entry, "name");
            string description = Text(entry, "description");
            if (Matches(name + " " + description, query))
            {
                results.Add(new
                {
                    name,
                    description,
                    feature = Text(entry, "feature")
                });
            }
        }

        foreach (JsonElement feature in Ledger.GetProperty("features").EnumerateArray())
        {
            if (Matches(Text(feature, "title") + " " + Text(feature, "id"), query))
            {
                results.Add(new
                {
                    name = Text(feature, "id"),
                    description = Text(feature, "title"),
                    feature = Text(feature, "id")
                });
            }
        }

        return Encode(new
        {
            version = Version(),
            total = results.Count,
            results = results.Take(limit)
        });
    }

    /// <summary>
    /// 関数仕様または機能IDの採用範囲を返します。
    /// </summary>
    [McpServerTool(Name = "get_reference", ReadOnly = true)]
    [Description("関数名または機能IDから書式・使用例・注意・対応範囲・根拠を取得します。")]
    public string GetReference(string name)
    {
        CheckText(name, 200);
        string? documentName = Documents.Keys.FirstOrDefault(key => MatchesExact(key, name));
        if (documentName is not null)
        {
            using Stream stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(documentName)!;
            using var reader = new StreamReader(stream);
            string html = reader.ReadToEnd();
            string text = WebUtility.HtmlDecode(Regex.Replace(html,
                "<[^>]+>", " ", RegexOptions.None, HelpMarkupRegexTimeout));
            return Encode(new
            {
                version = Version(),
                found = true,
                document = text,
                evidence = $"docs/user-help/content/{documentName}.html",
                scope = ValidationScope
            });
        }

        JsonElement? reference = null;
        string featureId = name;
        foreach (JsonElement entry in Reference.GetProperty("entries").EnumerateArray())
        {
            if (MatchesExact(Text(entry, "name"), name))
            {
                reference = entry;
                featureId = Text(entry, "feature");
                break;
            }
        }

        JsonElement? feature = FindFeature(featureId);
        return Encode(new
        {
            version = Version(),
            found = reference.HasValue || feature.HasValue,
            reference,
            capability = feature.HasValue ? Capability(feature.Value) : null,
            scope = ValidationScope
        });
    }

    /// <summary>
    /// 機能台帳の状態を、実行ホストの未検査と区別して返します。
    /// </summary>
    [McpServerTool(Name = "get_capabilities", ReadOnly = true)]
    [Description("機能IDの調査・実装・検証状態を取得します。CLI/WPFでの公開可否は別途確認が必要です。")]
    public string GetCapabilities(string featureId)
    {
        CheckText(featureId, 200);
        JsonElement? feature = FindFeature(featureId);
        return Encode(new
        {
            version = Version(),
            found = feature.HasValue,
            capability = feature.HasValue ? Capability(feature.Value) : null,
            hostAvailability = "未検査。台帳の実装状態だけでCLI/WPFの公開を保証しません。"
        });
    }

    /// <summary>
    /// 外部ファイルを読まずに最大64000文字を構文解析します。
    /// </summary>
    [McpServerTool(Name = "validate_script", ReadOnly = true)]
    [Description(".uwsソースを実行せず構文解析します。CALL拒否。未知関数・引数数・型の検査は含みません。")]
    public string ValidateScript(string source, CancellationToken cancellationToken)
    {
        CheckSource(source);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ScriptAnalysisTimeout);
        try
        {
            ScriptCompiler.CompileWithoutExternalFiles(source, deadline.Token);
            return Encode(new
            {
                version = Version(),
                valid = true,
                scope = ValidationScope
            });
        }
        catch (ScriptException error)
        {
            return Encode(new
            {
                version = Version(),
                valid = false,
                line = error.Line,
                message = error.Message,
                scope = ValidationScope
            });
        }
    }

    /// <summary>
    /// 実行せずに直接呼出しの名前と引数形式を警告します。
    /// </summary>
    [McpServerTool(Name = "analyze_script", ReadOnly = true)]
    [Description(".uwsを実行せず、未知の関数名と引数形式を行番号付きで警告します。型・値・動的呼出しは未検査。CALL拒否。")]
    public string AnalyzeScript(string source,
        CancellationToken cancellationToken)
    {
        CheckSource(source);
        using var deadline = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ScriptAnalysisTimeout);
        try
        {
            ScriptCallAnalysis analysis = ScriptCallAnalyzer.Analyze(
                source, deadline.Token);
            return Encode(new
            {
                version = Version(),
                syntaxValid = true,
                callsAnalyzed = true,
                diagnostics = analysis.Diagnostics.Select(item => new
                {
                    line = item.Line,
                    code = item.Code,
                    function = item.Function,
                    message = item.Message,
                    severity = "warning"
                }),
                truncated = analysis.Truncated,
                uncheckedScopes = new
                {
                    memberAccesses = analysis.UncheckedMemberAccesses,
                    dynamicCalls = analysis.UncheckedDynamicCalls,
                    syntaxNodes = analysis.UncheckedSyntaxNodes
                },
                scope = CallAnalysisScope
            });
        }
        catch (ScriptException error)
        {
            return Encode(new
            {
                version = Version(),
                syntaxValid = false,
                callsAnalyzed = false,
                line = error.Line,
                message = error.Message,
                diagnostics = Array.Empty<object>(),
                truncated = false,
                uncheckedScopes = (object?)null,
                scope = CallAnalysisScope
            });
        }
    }

    private static JsonElement Read(string name)
    {
        using Stream stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(name)!;
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    private static object Version() => new
    {
        server = typeof(AuthoringTools).Assembly.GetName().Version?.ToString(),
        helpDate = Text(Reference, "date"),
        ledgerDate = Text(Ledger, "updated"),
        ledgerBaseline = Text(Ledger, "baseline"),
        referenceHash = ReferenceHash,
        ledgerHash = LedgerHash,
        target = "UWSC#"
    };

    private static JsonElement? FindFeature(string id)
    {
        foreach (JsonElement feature in Ledger.GetProperty("features").EnumerateArray())
        {
            if (MatchesExact(Text(feature, "id"), id))
            {
                return feature;
            }
        }

        return null;
    }

    private static object Capability(JsonElement feature) => new
    {
        id = Text(feature, "id"),
        title = Text(feature, "title"),
        research = Text(feature, "research"),
        implementation = Text(feature, "implementation"),
        verification = Text(feature, "verification"),
        scope = Text(feature, "scope"),
        remaining = Text(feature, "remaining"),
        sources = feature.GetProperty("sources")
    };

    private static string Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out JsonElement property)
            ? property.GetString() ?? "" : "";

    private static bool Matches(string text, string query) =>
        text.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesExact(string text, string query) =>
        text.Equals(query, StringComparison.OrdinalIgnoreCase);

    private static string Encode(object result) => JsonSerializer.Serialize(result);

    private static string Hash(JsonElement value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value.GetRawText())));

    private static void CheckSource(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length > 64000)
        {
            throw new ArgumentOutOfRangeException(nameof(source),
                "64000文字以下で指定してください。");
        }
    }

    private static void CheckText(string text, int maximum)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum)
        {
            throw new ArgumentException($"1〜{maximum}文字で指定してください。");
        }
    }
}
