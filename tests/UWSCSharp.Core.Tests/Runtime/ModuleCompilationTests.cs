using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ModuleCompilationTests
{
    [Fact]
    public void KeepsModuleFieldsAndRoutinesSeparateFromRootDeclarations()
    {
        ScriptProgram program = ScriptCompiler.Compile("""
            PUBLIC root = 2
            PRINT root
            MODULE Counter
                CONST increment = 3
                PUBLIC total = root
                DIM hidden = 1, other
                PROCEDURE Counter
                    total = total + increment
                FEND
                FUNCTION read()
                    RESULT = THIS.total
                FEND
            ENDMODULE
            FUNCTION rootRead()
                RESULT = root
            FEND
            CLASS Other
                PUBLIC value = 9
            ENDCLASS
            CONST tail = 4
            """);

        Assert.Equal(2, program.Modules.Count);
        Assert.Equal(["root", "tail"], program.Globals.Select(global => global.Name));
        Assert.Single(program.Routines);
        ModuleDefinition module = program.Modules[0];
        Assert.Equal("Counter", module.Name);
        Assert.Equal(["increment", "total", "hidden", "other"],
            module.Fields.Select(field => field.Name));
        Assert.Equal([ModuleFieldKind.Constant, ModuleFieldKind.Public,
            ModuleFieldKind.Private, ModuleFieldKind.Private],
            module.Fields.Select(field => field.Kind));
        Assert.Equal(2, module.Routines.Count);
        Assert.Empty(module.Routines["Counter"].Parameters);
        Assert.Null(module.Fields[3].Initializer);
        Assert.All(module.Fields, field => Assert.Equal(module.SourcePath, field.SourcePath));
    }

    [Fact]
    public void KeepsArrayBoundsAndInitializerCommasInTheirExistingRoles()
    {
        ScriptProgram program = ScriptCompiler.Compile("""
            MODULE Storage
                DIM grid[1, 2], seed = COPY("a,b", 1, 1)
                PUBLIC values[] = seed, seed = "a"
            ENDMODULE
            """);

        IReadOnlyList<ModuleFieldDefinition> fields = Assert.Single(program.Modules).Fields;
        Assert.Equal(3, fields.Count);
        Assert.Equal(2, fields[0].Bounds.Count);
        Assert.Empty(fields[0].Initializers);
        Assert.IsType<CallExpr>(fields[1].Initializer);
        Assert.True(fields[2].Infer);
        Assert.Equal(2, fields[2].Initializers.Count);
        Assert.IsType<NameExpr>(fields[2].Initializers[0]);
        Assert.IsType<BinaryExpr>(fields[2].Initializers[1]);
    }

    [Theory]
    [InlineData("MODULE M\nENDMODULE\nCLASS m\nENDCLASS")]
    [InlineData("MODULE M\nMODULE N\nENDMODULE\nENDMODULE")]
    [InlineData("MODULE M\nENDCLASS")]
    [InlineData("CLASS M\nENDMODULE")]
    [InlineData("MODULE M")]
    [InlineData("MODULE THIS\nENDMODULE")]
    [InlineData("CLASS GLOBAL\nENDCLASS")]
    [InlineData("MODULE M\nPROCEDURE M(value)\nFEND\nENDMODULE")]
    [InlineData("MODULE M\nFUNCTION M(value = 1)\nFEND\nENDMODULE")]
    [InlineData("MODULE M\nDIM x\nPUBLIC X\nENDMODULE")]
    [InlineData("MODULE M\nCONST missing\nENDMODULE")]
    [InlineData("MODULE M\nPRINT 1\nENDMODULE")]
    [InlineData("MODULE M\nDIM x,\nENDMODULE")]
    [InlineData("MODULE M\nENDMODULE extra")]
    [InlineData("DIM THIS")]
    [InlineData("FUNCTION GLOBAL()\nFEND")]
    public void RejectsInvalidModuleDeclarations(string source)
    {
        Assert.Throws<ScriptException>(() => ScriptCompiler.Compile(source));
    }

    [Fact]
    public void AggregatesCallModulesAndReportsDuplicateDefinitionSource()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            $"uwsc-module-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string root = Path.Combine(directory, "root.uws");
            string child = Path.Combine(directory, "child.uws");
            File.WriteAllText(root,
                "CALL child.uws\nMODULE RootModule\nENDMODULE");
            File.WriteAllText(child, "CLASS ChildModule\nENDCLASS");

            ScriptProgram program = ScriptCompiler.CompileFile(root);
            Assert.Equal(["RootModule", "ChildModule"],
                program.Modules.Select(module => module.Name));
            Assert.Equal(child, program.Modules[1].SourcePath);

            File.WriteAllText(child, "CLASS rootmodule\nENDCLASS");
            ScriptException error = Assert.Throws<ScriptException>(
                () => ScriptCompiler.CompileFile(root));
            Assert.Equal(child, error.Data["UWSCSharp.SourcePath"]);
            Assert.Equal(1, error.Line);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoreHookRetainsRawArgumentsBeforeAnyArgumentEvaluation(bool explicitReference)
    {
        int targets = 0;
        int arguments = 0;
        int assignments = 0;
        var target = new ProbeExpr(() => targets++, Value.String("module"));
        var argument = new ProbeExpr(() => arguments++, Value.Numeric(3));
        var assignment = new ProbeExpr(() => assignments++, Value.Numeric(4));
        Expr rawArgument = explicitReference ? new ReferenceExpr(argument) : argument;
        var member = new MemberExpr(target, "change", [rawArgument]);
        var context = new MemberContext
        {
            Core = true,
            CoreHandler = (resolved, raw, assigned) =>
            {
                Assert.Equal(Value.String("module"), resolved);
                Assert.Same(member, raw);
                Assert.Same(rawArgument, Assert.Single(raw.Arguments));
                Assert.Same(assignment, assigned);
                return Value.Numeric(8);
            }
        };

        Value result = await member.InvokeAsync(context, assignment, default);

        Assert.Equal(Value.Numeric(8), result);
        Assert.Equal(1, targets);
        Assert.Equal(0, arguments);
        Assert.Equal(0, assignments);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReferenceFallbackUsesResolvedTargetWithoutLosingMetadata(bool named)
    {
        int targets = 0;
        var target = new ProbeExpr(() => targets++, Value.String("object"));
        var reference = new ReferenceExpr(new NameExpr("actual"));
        Expr argument = named ? new NamedExpr("argument", reference) : reference;
        var assignment = new LiteralExpr(Value.Numeric(4));
        var member = new MemberExpr(target, "change", [argument]);
        var context = new MemberContext
        {
            ReferenceHandler = async (resolved, assigned) =>
            {
                Assert.IsType<LiteralExpr>(resolved.Target);
                Assert.Same(argument, Assert.Single(resolved.Arguments));
                Assert.Same(assignment, assigned);
                return await resolved.Target.EvaluateAsync(new MemberContext(), default);
            }
        };

        Value result = await member.InvokeAsync(context, assignment, default);

        Assert.Equal(Value.String("object"), result);
        Assert.Equal(1, targets);
    }

    [Fact]
    public async Task OrdinaryFallbackKeepsTargetArgumentAndAssignmentOrder()
    {
        var order = new List<string>();
        var member = new MemberExpr(
            new ProbeExpr(() => order.Add("target"), Value.String("object")),
            "change",
            [new ProbeExpr(() => order.Add("first"), Value.Numeric(1)),
                new ProbeExpr(() => order.Add("second"), Value.Numeric(2))]);
        var assignment = new ProbeExpr(() => order.Add("assignment"), Value.Numeric(3));
        var context = new MemberContext
        {
            ValueHandler = (target, name, arguments, assigned) =>
            {
                order.Add("invoke");
                Assert.Equal(Value.String("object"), target);
                Assert.Equal("change", name);
                Assert.Equal([Value.Numeric(1), Value.Numeric(2)], arguments);
                Assert.Equal(Value.Numeric(3), assigned);
                return Value.Empty;
            }
        };

        await member.InvokeAsync(context, assignment, default);

        Assert.Equal(["target", "first", "second", "assignment", "invoke"], order);
    }

    private sealed record ProbeExpr(Action Observe, Value Result) : Expr
    {
        public override ValueTask<Value> EvaluateAsync(
            IEvaluationContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Observe();
            return ValueTask.FromResult(Result);
        }
    }

    private sealed class MemberContext : IEvaluationContext
    {
        public bool Core
        {
            get; init;
        }

        public Func<Value, MemberExpr, Expr?, Value>? CoreHandler
        {
            get; init;
        }

        public Func<MemberExpr, Expr?, ValueTask<Value>>? ReferenceHandler
        {
            get; init;
        }

        public Func<Value, string, IReadOnlyList<Value>, Value?, Value>? ValueHandler
        {
            get; init;
        }

        public Value Read(string name) => throw new NotSupportedException();

        public ValueTask<Value> InvokeAsync(CallExpr call, CancellationToken token) =>
            throw new NotSupportedException();

        public bool IsCoreMemberTarget(Value target) => Core;

        public ValueTask<Value> InvokeCoreMemberAsync(Value target,
            MemberExpr member, Expr? assignment, CancellationToken token) =>
            ValueTask.FromResult(CoreHandler!(target, member, assignment));

        public ValueTask<Value> InvokeReferenceMemberAsync(MemberExpr member,
            Expr? assignment, CancellationToken token) => ReferenceHandler!(member, assignment);

        public ValueTask<Value> InvokeMemberAsync(Value target, string name,
            IReadOnlyList<Value> arguments, Value? assigned, CancellationToken token) =>
            ValueTask.FromResult(ValueHandler!(target, name, arguments, assigned));
    }
}
