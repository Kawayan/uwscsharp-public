namespace UWSCSharp.Core;
/// <summary>
/// 解析済みの再利用可能なスクリプト。実行状態は実行ごとに個別に作成します。
/// </summary>
public sealed class ScriptProgram
{
    internal ScriptProgram(IReadOnlyList<Statement> main,
        IReadOnlyDictionary<string, Routine> routines,
        IReadOnlyList<GlobalDeclaration> globals, bool optFinally = false,
        bool sameString = false, bool explicitVariables = false,
        IReadOnlyList<ModuleDefinition>? modules = null,
        bool optPublic = false)
    {
        Main = main;
        Routines = routines;
        Globals = globals;
        OptFinally = optFinally;
        SameString = sameString;
        ExplicitVariables = explicitVariables;
        Modules = modules ?? [];
        OptPublic = optPublic;
    }

    internal IReadOnlyList<Statement> Main
    {
        get;
    }

    internal IReadOnlyDictionary<string, Routine> Routines
    {
        get;
    }

    internal IReadOnlyList<NativeDllDeclarationStatement> NativeDllDeclarations
    {
        get; init;
    } = [];

    internal IReadOnlyList<GlobalDeclaration> Globals
    {
        get;
    }

    internal bool OptFinally
    {
        get;
    }

    internal bool ShortCircuit
    {
        get; init;
    }

    internal bool ContainsTextBlocks
    {
        get; init;
    }

    internal bool SameString
    {
        get;
    }

    internal bool ExplicitVariables
    {
        get;
    }

    internal bool OptPublic
    {
        get;
    }

    internal IReadOnlyList<ModuleDefinition> Modules
    {
        get;
    }

    internal IReadOnlyList<string> InitializationFiles
    {
        get; init;
    } = [];

    /// <summary>
    /// 無名ソースではnullとなる主スクリプトの実パスです。
    /// </summary>
    internal string? SourcePath
    {
        get; init;
    }

    internal IReadOnlyList<ExternalScript> ExternalScripts
    {
        get; init;
    } = [];
}

internal sealed record ModuleDefinition(int Line, string Name,
    IReadOnlyList<ModuleFieldDefinition> Fields,
    IReadOnlyDictionary<string, Routine> Routines, string? SourcePath,
    IReadOnlyList<NativeDllDeclarationStatement> NativeDllDeclarations);

internal sealed record ModuleFieldDefinition(int Line, string Name,
    ModuleFieldKind Kind, Expr? Initializer,
    IReadOnlyList<Expr> Bounds, IReadOnlyList<Expr> Initializers,
    bool Infer, string? SourcePath)
{
    internal int SourceOrder
    {
        get; init;
    } = int.MaxValue;
}

internal enum ModuleFieldKind
{
    Private,
    Public,
    Constant
}

internal abstract record Statement(int Line)
{
    internal string? SourcePath
    {
        get; init;
    }
}
internal sealed record WithStatement(int Line, Expr Target,
    IReadOnlyList<Statement> Body) : Statement(Line);
internal sealed record AssignStatement(int Line,
    string Name, Expr? Expression, bool Declare) : Statement(Line);
internal sealed record DeclarationGroupStatement(int Line,
    IReadOnlyList<Statement> Declarations) : Statement(Line);
internal sealed record HashDeclareStatement(int Line,
    string Name, HashCreateExpr Initializer) : Statement(Line);
internal sealed record IndexAssignStatement(int Line,
    IndexExpr Target, Expr Expression) : Statement(Line);
internal sealed record ArrayDeclareStatement(int Line,
    string Name, IReadOnlyList<Expr> Bounds,
    IReadOnlyList<Expr> Initializers, bool Infer) : Statement(Line);
internal sealed record PrintStatement(int Line,
    Expr Expression) : Statement(Line);
internal sealed record RuntimeOption(string Name, IReadOnlyList<Expr> Expressions);
internal sealed record RuntimeOptionsStatement(int Line,
    IReadOnlyList<RuntimeOption> Options) : Statement(Line);
internal sealed record ComErrorControlStatement(int Line,
    bool Ignore) : Statement(Line);
internal sealed record LogOption(string Name, Expr Expression);
internal sealed record LogOptionsStatement(int Line,
    IReadOnlyList<LogOption> Options) : Statement(Line);
internal sealed record CallStatement(int Line,
    CallExpr Call) : Statement(Line);
internal sealed record ThreadStatement(int Line,
    Expr Call) : Statement(Line);
internal sealed record NativeDllDeclarationStatement(int Line,
    NativeDllDeclaration Declaration) : Statement(Line);
internal sealed record MemberStatement(int Line,
    MemberExpr Member, Expr? Assigned) : Statement(Line);
internal sealed record ExternalCallStatement(int Line,
    ExternalScript Script, IReadOnlyList<Expr> Arguments) : Statement(Line);
internal sealed record IfBranch(Expr? Condition,
    IReadOnlyList<Statement> Body, int Line = 0);
internal sealed record IfStatement(int Line,
    IReadOnlyList<IfBranch> Branches) : Statement(Line);
internal sealed record ForStatement(int Line, string Name,
    Expr Start, Expr End, Expr Step,
    IReadOnlyList<Statement> Body) : Statement(Line);
internal sealed record ForInStatement(int Line, string Name,
    Expr Collection,
    IReadOnlyList<Statement> Body) : Statement(Line);
internal sealed record WhileStatement(int Line,
    Expr Condition, IReadOnlyList<Statement> Body) : Statement(
    Line);
internal sealed record RepeatStatement(int Line,
    Expr Condition, int ConditionLine,
    IReadOnlyList<Statement> Body) : Statement(Line);
internal sealed record CaseBranch(
    IReadOnlyList<Expr>? Values, IReadOnlyList<Statement> Body,
    int Line);
internal sealed record SelectStatement(int Line,
    Expr Selector,
    IReadOnlyList<CaseBranch> Cases) : Statement(Line);
internal sealed record FlowStatement(int Line,
    FlowKind Kind, int Levels, Expr? ExitCode = null) : Statement(Line);
internal sealed record TryStatement(int Line,
    IReadOnlyList<Statement> Body,
    IReadOnlyList<Statement> Handler,
    bool Finally) : Statement(Line);
internal sealed record GlobalDeclaration(int Line,
    string Name, Expr? Initializer, bool Constant)
{
    internal GlobalDeclarationKind Kind
    {
        get; init;
    }

    internal IReadOnlyList<Expr> Bounds
    {
        get; init;
    } = [];

    internal IReadOnlyList<Expr> Initializers
    {
        get; init;
    } = [];

    internal bool Infer
    {
        get; init;
    }

    internal int SourceOrder
    {
        get; init;
    } = int.MaxValue;

    internal string? SourcePath
    {
        get; init;
    }
}
internal enum GlobalDeclarationKind
{
    Scalar,
    Array,
    HashTable
}
internal sealed record Parameter(string Name,
    bool ByReference, Expr? Default, int ArrayRank = 0);
internal sealed record Routine(int Line, string Name,
    bool ReturnsValue, IReadOnlyList<Parameter> Parameters,
    IReadOnlyList<Statement> Body);
internal enum FlowKind
{
    Normal,
    Break,
    Continue,
    Exit,
    Stop
}

internal readonly record struct Flow(
    FlowKind Kind = FlowKind.Normal, int Levels = 0);
