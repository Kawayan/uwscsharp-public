namespace UWSCSharp.Core;

/// <summary>
/// DLL宣言を保持し、型の実行可否やDLL探索は後続で判定します。
/// </summary>
internal sealed class NativeDllDeclaration
{
    internal NativeDllDeclaration(string name,
        IEnumerable<NativeDllType> parameters, NativeDllType? returnType,
        string libraryText, bool packed)
    {
        Name = name;
        Parameters = Array.AsReadOnly(parameters.ToArray());
        ReturnType = returnType;
        LibraryText = libraryText;
        Packed = packed;
    }

    internal string Name
    {
        get;
    }

    internal IReadOnlyList<NativeDllType> Parameters
    {
        get;
    }

    // nullは戻り型の省略であり、VOIDや既定ABI型へ置き換えません。
    internal NativeDllType? ReturnType
    {
        get;
    }

    internal string LibraryText
    {
        get;
    }

    internal bool Packed
    {
        get;
    }
}
