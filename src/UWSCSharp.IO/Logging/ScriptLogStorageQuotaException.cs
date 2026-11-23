namespace UWSCSharp.IO;

/// <summary>
/// 既定ログの容量を確保できず、保存を省略する場合の診断です。
/// </summary>
public sealed class ScriptLogStorageQuotaException(string message) : IOException(message);
