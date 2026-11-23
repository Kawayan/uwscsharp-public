namespace UWSCSharp.Wpf;

/// <summary>
/// GUI検証で使う条件の確認を提供します。
/// </summary>
internal static class VerificationAssert
{
    /// <summary>
    /// 条件が成り立たない場合に、指定した文言で検証を失敗させます。
    /// </summary>
    /// <param name="condition">確認する条件。</param>
    /// <param name="message">失敗時の例外に使う文言。</param>
    internal static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    /// <summary>
    /// 条件を確認し、成り立った確認項目の名前を一覧へ記録します。
    /// </summary>
    /// <param name="checks">成功した確認項目を記録する一覧。</param>
    /// <param name="condition">確認する条件。</param>
    /// <param name="name">確認項目の名前と、失敗時の例外の文言。</param>
    internal static void Require(ICollection<string> checks,
        bool condition, string name)
    {
        Require(condition, name);
        checks.Add(name);
    }
}
