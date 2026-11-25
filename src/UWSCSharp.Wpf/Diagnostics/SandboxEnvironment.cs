namespace UWSCSharp.Wpf;

// 環境を変更する検証は、使い捨てのWindows Sandbox内だけで実行します。
internal static class SandboxEnvironment
{
    private const string UserName = "WDAGUtilityAccount";

    // Windowsのユーザー名は大文字と小文字を区別しないため、同じ規則で比較します。
    internal static bool IsCurrent =>
        string.Equals(Environment.UserName, UserName, StringComparison.OrdinalIgnoreCase);
}
