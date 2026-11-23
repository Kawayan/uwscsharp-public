using System.Globalization;

namespace UWSCSharp.Core;

/// <summary>
/// スクリプトで扱う値の種類を表します。
/// </summary>
public enum ValueKind
{
    Empty,
    Null,
    Number,
    String,
    Array,
    Omitted,
    /// <summary>
    /// オブジェクトの無い状態。
    /// </summary>
    Nothing,
    /// <summary>
    /// Variantエラーコードを保持する値。
    /// </summary>
    Error,
    /// <summary>
    /// 外部sessionが寿命を管理するオブジェクト参照。
    /// </summary>
    Object,
    /// <summary>
    /// キーと値の独立した内容を保持する連想配列。
    /// </summary>
    HashTable
}

/// <summary>
/// 種類と内容を保持するスクリプトの値。
/// </summary>
public readonly record struct Value(ValueKind Kind,
    double Number = 0, string Text = "", UwscArray? Array = null)
{
    // 借用ポインターを持たず、型照会用の元のBYREF型だけを保存します。
    internal ushort? NativeReferenceType
    {
        get; init;
    }

    // 論理値の型照会を可能にし、従来の0/1による演算とCOM送信は維持します。
    internal bool IsLogical
    {
        get; init;
    }

    internal static Value Logical(bool value) => Numeric(value ? 1 : 0) with
    {
        IsLogical = true
    };

    // バイト関数が生成した符号化済みの内容を保持します。
    internal EncodedText? ByteText
    {
        get; init;
    }

    // COMやDLLから受け取ったscalarの元型と精度を保持します。
    internal ushort? NativeVariantType
    {
        get; init;
    }

    internal object? NativeScalar
    {
        get; init;
    }

    /// <summary>
    /// 数値が変更されていない場合だけ、元のscalarを返します。
    /// </summary>
    internal object? GetUnchangedNativeScalar()
    {
        if (Kind != ValueKind.Number || NativeVariantType is null)
        {
            return null;
        }

        double? original = NativeScalar switch
        {
            DateTime date => date.ToOADate(),
            bool flag => flag ? 1 : 0,
            sbyte or byte or short or ushort or int or uint
                or long or ulong or float or double or decimal =>
                Convert.ToDouble(NativeScalar, CultureInfo.InvariantCulture),
            _ => null
        };
        return original is double number
            && BitConverter.DoubleToInt64Bits(Number)
                == BitConverter.DoubleToInt64Bits(number)
            ? NativeScalar : null;
    }

    /// <summary>
    /// native変換metadataを除いた既存の値内容を比較します。
    /// </summary>
    public bool Equals(Value other) => Kind == other.Kind && Number.Equals(other.Number)
        && Text == other.Text && Equals(Array, other.Array)
        && Equals(HashTable, other.HashTable) && Equals(ObjectReference, other.ObjectReference);

    /// <summary>
    /// 値内容に対応するhashを取得します。
    /// </summary>
    public override int GetHashCode() => HashCode.Combine(Kind, Number, Text, Array, HashTable, ObjectReference);

    /// <summary>
    /// 連想配列の内容です。更新時は新しい内容へ置き換えます。
    /// </summary>
    public UwscHashTable? HashTable
    {
        get; init;
    }

    /// <summary>
    /// 連想配列から値を作成します。
    /// </summary>
    public static Value FromHashTable(UwscHashTable table) => new(ValueKind.HashTable)
    {
        HashTable = table
    };

    /// <summary>
    /// 連想配列の内容を取得します。
    /// </summary>
    public UwscHashTable AsHashTable() => Kind == ValueKind.HashTable && HashTable is not null
        ? HashTable : throw new InvalidOperationException("HASHTBLを指定してください。");

    /// <summary>
    /// 外部オブジェクトの参照です。寿命は生成したsessionが管理します。
    /// </summary>
    public object? ObjectReference
    {
        get; init;
    }

    /// <summary>
    /// 空の値を生成します。
    /// </summary>
    public static Value Empty => new(ValueKind.Empty);

    /// <summary>
    /// オブジェクトの無い状態を生成します。
    /// </summary>
    public static Value Nothing => new(ValueKind.Nothing);

    /// <summary>
    /// 数値とは区別したVariantエラー値を生成します。
    /// </summary>
    public static Value FromError(int code) => new(ValueKind.Error, code);

    /// <summary>
    /// 外部オブジェクトの参照を保持します。nullはNOTHINGになります。
    /// </summary>
    public static Value FromObject(object? instance) => instance is null
        ? Nothing : new(ValueKind.Object)
        {
            ObjectReference = instance
        };

    /// <summary>
    /// Variantエラーコードを取得します。
    /// </summary>
    public int AsErrorCode() => Kind == ValueKind.Error
        ? checked((int)Number)
        : throw new InvalidOperationException("Variantエラー値を指定してください。");

    /// <summary>
    /// 外部オブジェクトを取得します。NOTHINGはnullになります。
    /// </summary>
    public object? AsObject() => Kind switch
    {
        ValueKind.Nothing => null,
        ValueKind.Object when ObjectReference is not null => ObjectReference,
        _ => throw new InvalidOperationException("オブジェクト参照を指定してください。")
    };

    /// <summary>
    /// 有限の数値から値を作成します。
    /// </summary>
    public static Value Numeric(
        double number) => double.IsFinite(number) ? new(
        ValueKind.Number, number)
        : throw new InvalidOperationException("数値が有限範囲を超えました。");

    /// <summary>
    /// 文字列から値を作成します。
    /// </summary>
    public static Value String(string text) => new(
        ValueKind.String, Text: text);

    /// <summary>
    /// 配列から値を作成します。
    /// </summary>
    public static Value FromArray(UwscArray array) => new(
        ValueKind.Array, Array: array);

    /// <summary>
    /// 配列の値を取得します。配列以外は例外になります。
    /// </summary>
    public UwscArray AsArray() => Kind == ValueKind.Array
        && Array is not null ? Array
        : throw new InvalidOperationException("配列を指定してください。");

    /// <summary>
    /// 数値を取得します。EMPTYは0として扱います。
    /// </summary>
    public double AsNumber() => Kind switch
    {
        ValueKind.Number => double.IsFinite(Number) ? Number
            : throw new InvalidOperationException("数値が有限範囲を超えました。"),
        ValueKind.Empty => 0,
        _ => throw new InvalidOperationException(
            "この段階では文字列・NULLの数値変換に対応していません。")
    };

    /// <summary>
    /// 出力用の文字列表現を返します。
    /// </summary>
    public string Display() => Kind switch
    {
        ValueKind.Empty => "",
        ValueKind.String => Text,
        ValueKind.Number => Number.ToString("G",
            CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException(
            "この値の表示には対応していません。")
    };
}

/// <summary>
/// スクリプトの行番号と実行エラーを保持します。
/// </summary>
public sealed class ScriptException : Exception
{
    /// <summary>
    /// 行番号と本文からエラーを作成します。
    /// </summary>
    public ScriptException(int line, string message)
        : this(line, message, null)
    {
    }

    /// <summary>
    /// 行番号・本文と、原因となった例外を保持します。
    /// </summary>
    public ScriptException(int line, string message, Exception? innerException)
        : base($"{line}行: {message}", innerException)
    {
        Line = line;
    }

    /// <summary>
    /// エラーが発生したスクリプトの行番号です。
    /// </summary>
    public int Line
    {
        get;
    }

    /// <summary>
    /// 定義元ファイルがあれば、そのパスを含む表示用の診断です。
    /// </summary>
    public string DiagnosticMessage => Data["UWSCSharp.SourcePath"] is string path
        ? $"{path}: {Message}" : Message;

    // 内側の失敗位置を優先し、伝播時に呼出し元で上書きしません。
    internal void SetSourcePath(string? path)
    {
        if (path is not null && !Data.Contains("UWSCSharp.SourcePath"))
        {
            Data["UWSCSharp.SourcePath"] = path;
        }
    }
}
