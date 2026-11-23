namespace UWSCSharp.Wpf;

/// <summary>
/// 関連付け登録の現在値を表します。
/// </summary>
internal readonly record struct AssociationSnapshot(
    bool CandidateExists,
    bool OpenWithValueExists,
    bool HasUnexpectedData,
    string? OwnerPath,
    string? OwnerToken,
    string? Command,
    string? ExpectedCommand);

/// <summary>
/// 登録または解除に先立つ所有状態です。
/// </summary>
internal enum AssociationOwnership
{
    /// <summary>
    /// 関連する登録が存在しません。
    /// </summary>
    Absent,

    /// <summary>
    /// 現在の配置が登録した値と一致します。
    /// </summary>
    Owned,

    /// <summary>
    /// 別の配置、部分状態、または変更された値です。
    /// </summary>
    Conflict
}

/// <summary>
/// 関連付けを安全に更新できるか判定します。
/// </summary>
internal static class AssociationOwnershipPolicy
{
    /// <summary>
    /// 候補と所有情報が現在の配置に一致するか判定します。
    /// </summary>
    internal static AssociationOwnership Classify(
        AssociationSnapshot snapshot, string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!snapshot.CandidateExists && !snapshot.OpenWithValueExists &&
            !snapshot.HasUnexpectedData && snapshot.OwnerPath is null &&
            snapshot.OwnerToken is null && snapshot.Command is null &&
            snapshot.ExpectedCommand is null)
        {
            return AssociationOwnership.Absent;
        }

        if (snapshot.CandidateExists && snapshot.OpenWithValueExists &&
            !snapshot.HasUnexpectedData &&
            string.Equals(snapshot.OwnerPath, executablePath,
                StringComparison.OrdinalIgnoreCase) &&
            (snapshot.OwnerToken is null ||
                Guid.TryParseExact(snapshot.OwnerToken, "N", out _)) &&
            snapshot.Command is not null &&
            string.Equals(snapshot.Command, snapshot.ExpectedCommand,
                StringComparison.Ordinal))
        {
            return AssociationOwnership.Owned;
        }

        return AssociationOwnership.Conflict;
    }
}
