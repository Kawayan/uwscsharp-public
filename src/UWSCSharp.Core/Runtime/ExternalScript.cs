namespace UWSCSharp.Core;

// 循環CALLの参照を先に確保し、コンパイル完了後だけ実行へ公開します。
internal sealed class ExternalScript(string path)
{
    internal string Path { get; } = path;

    internal ScriptProgram Program { get; set; } = null!;

    internal List<ExternalScript> Dependencies { get; } = [];

    internal bool PreservesSpecialCharacters
    {
        get; set;
    }

    internal bool DeclaresSpecialCharacters
    {
        get; set;
    }

    internal bool PolicyResolved
    {
        get; set;
    }

    internal List<(string Parent, int Line, bool Preserve)> PolicyRequests
    {
        get;
    } = [];
}

internal sealed class ScriptCompilation(string? sourcePath, bool allowCalls,
    CancellationToken token)
{
    private readonly Dictionary<string, ExternalScript> scripts = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Queue<(ExternalScript Script, bool SpecialCharacters)> pendingSources = new();

    internal string BasePath
    {
        get;
    } = sourcePath is null
        ? System.IO.Path.Combine(Environment.CurrentDirectory, "untitled.uws")
        : System.IO.Path.GetFullPath(sourcePath);

    internal ExternalScript Register(string path)
    {
        var script = new ExternalScript(path);
        scripts.Add(path, script);
        return script;
    }

    internal static ScriptCompilation CreateRuntime(ScriptProgram program,
        CancellationToken token)
    {
        var compilation = new ScriptCompilation(
            program.InitializationFiles.FirstOrDefault(), true, token);
        if (program.ExternalScripts.Count == 0)
        {
            compilation.Register(compilation.BasePath).Program = program;
        }
        else
        {
            compilation.CopyScripts(program.ExternalScripts);
        }

        return compilation;
    }

    internal ScriptCompilation Fork(CancellationToken cancellationToken)
    {
        ParsePending();
        var compilation = new ScriptCompilation(BasePath, true, cancellationToken);
        compilation.CopyScripts(scripts.Values);
        return compilation;
    }

    private void CopyScripts(IEnumerable<ExternalScript> originals)
    {
        ExternalScript[] sources = [.. originals];
        foreach (ExternalScript source in sources)
        {
            ExternalScript copy = Register(source.Path);
            copy.Program = source.Program;
            copy.PreservesSpecialCharacters = source.PreservesSpecialCharacters;
            copy.DeclaresSpecialCharacters = source.DeclaresSpecialCharacters;
            copy.PolicyResolved = source.PolicyResolved;
        }

        foreach (ExternalScript source in sources)
        {
            scripts[source.Path].Dependencies.AddRange(source.Dependencies
                .Select(dependency => scripts[dependency.Path]));
        }
    }

    internal IReadOnlyList<ExternalScript> Snapshot()
    {
        ParsePending();
        return [.. InitializationOrder()];
    }

    internal void ParsePending()
    {
        while (pendingSources.TryDequeue(out (ExternalScript Script, bool SpecialCharacters) pending))
        {
            token.ThrowIfCancellationRequested();
            ExternalScript script = pending.Script;
            try
            {
                // 子CALLはLoadで登録するだけにし、ホストのcall stackを増やしません。
                script.Program = ScriptCompiler.ParseExternal(Source.ReadFile(script.Path),
                    this, script.Path, pending.SpecialCharacters, token);
            }
            catch (Exception error)
            {
                if (!error.Data.Contains("UWSCSharp.SourcePath"))
                {
                    error.Data["UWSCSharp.SourcePath"] = script.Path;
                }

                throw;
            }
        }
    }

    internal void SetSpecialCharacterPolicy(string path,
        bool preservesSpecialCharacters, bool declaresSpecialCharacters)
    {
        ExternalScript script = scripts[path];
        script.PreservesSpecialCharacters = preservesSpecialCharacters;
        script.DeclaresSpecialCharacters = declaresSpecialCharacters;
        script.PolicyResolved = true;
        ValidateSpecialCharacterRequests(script);
    }

    private static void ValidateSpecialCharacterRequests(ExternalScript script)
    {
        if (!script.PolicyResolved || script.DeclaresSpecialCharacters)
        {
            return;
        }

        foreach ((string parent, int line, bool preserve) in script.PolicyRequests)
        {
            if (script.PreservesSpecialCharacters != preserve)
            {
                var error = new ScriptException(line,
                    "同じCALL先を異なるSPECIALCHAR設定で共有できません。共有先で設定を明示してください。");
                error.SetSourcePath(parent);
                throw error;
            }
        }
    }

    internal ExternalScript Load(string name, string parentPath, int line,
        bool specialCharacters)
    {
        if (!allowCalls)
        {
            throw new ScriptException(line, "この解析では外部CALLを読み込みません。");
        }

        token.ThrowIfCancellationRequested();
        if (name.Any(character => character < ' '))
        {
            throw new ScriptException(line, "CALLのファイル名に制御文字は使用できません。");
        }

        if (!System.IO.Path.HasExtension(name))
        {
            name += ".uws";
        }

        string path;
        try
        {
            path = System.IO.Path.GetFullPath(name,
                System.IO.Path.GetDirectoryName(parentPath)!);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            throw new ScriptException(line, "CALLのファイル名が不正です。", error);
        }
        if (scripts.TryGetValue(path, out ExternalScript? existing))
        {
            existing.PolicyRequests.Add((parentPath, line, specialCharacters));
            ValidateSpecialCharacterRequests(existing);
            TrackDependency(parentPath, existing);
            return existing;
        }

        if (scripts.Count >= ScriptLimits.MaxExternalScripts)
        {
            throw new ScriptException(line, "CALLの読込ファイル数が上限を超えました。");
        }

        ExternalScript script = Register(path);
        script.PreservesSpecialCharacters = specialCharacters;
        script.PolicyRequests.Add((parentPath, line, specialCharacters));
        TrackDependency(parentPath, script);
        pendingSources.Enqueue((script, specialCharacters));

        return script;
    }

    private void TrackDependency(string parentPath, ExternalScript script)
    {
        List<ExternalScript> dependencies = scripts[parentPath].Dependencies;
        if (!dependencies.Contains(script))
        {
            dependencies.Add(script);
        }
    }

    private IEnumerable<ExternalScript> InitializationOrder()
    {
        var pending = new Queue<ExternalScript>();
        var visited = new HashSet<ExternalScript>();
        ExternalScript root = scripts[BasePath];
        pending.Enqueue(root);
        visited.Add(root);
        while (pending.TryDequeue(out ExternalScript? script))
        {
            token.ThrowIfCancellationRequested();
            yield return script;
            foreach (ExternalScript dependency in script.Dependencies)
            {
                if (visited.Add(dependency))
                {
                    pending.Enqueue(dependency);
                }
            }
        }
    }

    internal ScriptProgram Complete(ScriptProgram root)
    {
        ParsePending();
        var routines = new Dictionary<string, Routine>(StringComparer.OrdinalIgnoreCase);
        var globals = new List<GlobalDeclaration>();
        var nativeDllDeclarations = new List<NativeDllDeclarationStatement>();
        var modules = new List<ModuleDefinition>();
        var moduleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, GlobalDeclaration>(StringComparer.OrdinalIgnoreCase);
        bool optPublic = scripts.Values.Any(script => script.Program.OptPublic);
        foreach (ExternalScript script in InitializationOrder())
        {
            nativeDllDeclarations.AddRange(script.Program.NativeDllDeclarations);
            foreach (ModuleDefinition module in script.Program.Modules)
            {
                if (!moduleNames.Add(module.Name))
                {
                    var error = new ScriptException(module.Line,
                        $"CALL間でモジュール名が重複しています: {module.Name}");
                    error.SetSourcePath(script.Path);
                    throw error;
                }

                modules.Add(module);
            }

            foreach (Routine routine in script.Program.Routines.Values)
            {
                if (!routines.TryAdd(routine.Name, routine))
                {
                    var error = new ScriptException(routine.Line,
                        $"CALL間で関数名が重複しています: {routine.Name}");
                    error.SetSourcePath(script.Path);
                    throw error;
                }
            }

            foreach (GlobalDeclaration declaration in script.Program.Globals)
            {
                if (names.TryGetValue(declaration.Name, out GlobalDeclaration? previous)
                    && (optPublic || previous.Constant || declaration.Constant
                        || previous.Kind != declaration.Kind
                        || declaration.Kind == GlobalDeclarationKind.HashTable))
                {
                    var error = new ScriptException(declaration.Line,
                        $"CALL間でグローバル名が重複しています: {declaration.Name}");
                    error.SetSourcePath(script.Path);
                    throw error;
                }

                names.TryAdd(declaration.Name, declaration);
                globals.Add(declaration);
            }
        }

        return new(root.Main, routines, globals.AsReadOnly(),
            scripts.Values.Any(script => script.Program.OptFinally),
            scripts.Values.Any(script => script.Program.SameString),
            scripts.Values.Any(script => script.Program.ExplicitVariables),
            modules.AsReadOnly(), optPublic)
        {
            ShortCircuit = scripts.Values.Any(script => script.Program.ShortCircuit),
            ContainsTextBlocks = scripts.Values.Any(script => script.Program.ContainsTextBlocks),
            InitializationFiles = [.. InitializationOrder().Select(script => script.Path)],
            SourcePath = sourcePath is null ? null : BasePath,
            ExternalScripts = Snapshot(),
            NativeDllDeclarations = nativeDllDeclarations.AsReadOnly()
        };
    }
}
