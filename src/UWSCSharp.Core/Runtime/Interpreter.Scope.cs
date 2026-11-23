namespace UWSCSharp.Core;

public sealed partial class Interpreter
{
    private sealed class Scope(
        Execution execution) : IEvaluationContext
    {
        private Value tryErrorMessage = Value.String("");
        private Value tryErrorLine = Value.String("");

        public bool SameString => execution.SameString;

        public INumericArithmetic? NumericArithmetic => execution.NumericArithmetic;

        internal Value? WithTarget
        {
            get; set;
        }

        private readonly Dictionary<ModuleState, string> withReferences = [];

        internal string BindWithModuleReference()
        {
            if (ReadWithTarget().ObjectReference is not CoreNamespace { Module: { } module })
            {
                throw new InvalidOperationException("モジュールの変数参照を指定してください。");
            }

            if (!withReferences.TryGetValue(module, out string? name))
            {
                // ソースから指定できない名前で、評価済みの対象を保持します。
                name = "\0WITH" + withReferences.Count;
                withReferences.Add(module, name);
                Locals.Add(name, Value.FromObject(new CoreNamespace(module)));
            }

            return name;
        }


        public Value ReadWithTarget() => WithTarget
            ?? throw new InvalidOperationException("先頭ドットはWITH内で使用してください。");

        public ValueTask<Value> AssignEvaluatedAsync(Expr target,
            Expr expression, CancellationToken token) =>
            execution.AssignEvaluatedAsync(target, expression, this);

        public Dictionary<string, Value> Locals
        {
            get;
        } = new(
            StringComparer.OrdinalIgnoreCase);

        public int CallDepth
        {
            get; init;
        }

        public bool HasRoutineResult
        {
            get; init;
        }

        internal bool RoutineReturnsValue
        {
            get; init;
        }

        internal ModuleState? Module
        {
            get; init;
        }

        internal string? SourcePath
        {
            get; init;
        }

        internal int CurrentLine
        {
            get; set;
        }

        public Value Read(string name)
        {
            lock (execution.sharedVariablesGate)
            {
                return ReadCore(name);
            }
        }

        private Value ReadCore(string name) => Locals.TryGetValue(name, out Value value)
            || execution.TryReadThreadVariable(name, out value)
            || execution.TryReadModule(this, name, out value)
            || execution.globals.TryGetValue(name, out value)
            || BuiltinConstants.TryGet(name, out value)
            || TryReadPowerConstant(name, out value)
            || TryReadInformation(name, out value) ? value
            : throw new InvalidOperationException($"未定義の変数: {name}");

        private bool TryReadPowerConstant(string name, out Value value)
        {
            if (execution.HasPowerOperation
                && PowerCommandRequest.TryGetConstant(name, out int constant))
            {
                value = Value.Numeric(constant);
                return true;
            }

            value = default;
            return false;
        }

        internal void ClearTryInformation()
        {
            tryErrorMessage = Value.String("");
            tryErrorLine = Value.String("");
        }

        internal void SetTryInformation(ScriptException failure)
        {
            tryErrorMessage = Value.String(failure.Message);
            tryErrorLine = Value.Numeric(failure.Line);
        }

        private bool TryReadInformation(string name, out Value value)
        {
            if (name.Equals(BuiltinVariableNames.GetCurDir,
                StringComparison.OrdinalIgnoreCase))
            {
                value = execution.ReadCurrentDirectory();
                return true;
            }

            if (BuiltinVariableNames.TryGetDisplayVariable(name,
                out WindowsDisplayVariable variable))
            {
                value = execution.ReadDisplayVariable(name, variable);
                return true;
            }

            if (name.Equals(BuiltinVariableNames.TryErrmsg,
                StringComparison.OrdinalIgnoreCase))
            {
                value = tryErrorMessage;
                return true;
            }

            if (name.Equals(BuiltinVariableNames.TryErrline,
                StringComparison.OrdinalIgnoreCase))
            {
                value = tryErrorLine;
                return true;
            }

            value = default;
            return false;
        }

        public void Write(string name, Value value, bool declare = false)
        {
            lock (execution.sharedVariablesGate)
            {
                WriteCore(name, value, declare);
            }
        }

        private void WriteCore(string name, Value value, bool declare)
        {
            if (name.Equals(BuiltinVariableNames.ComErrFlg,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"特殊変数は読み取り専用です: {name}");
            }

            bool replacesResult = HasRoutineResult
                && name.Equals(BuiltinVariableNames.Result,
                    StringComparison.OrdinalIgnoreCase);
            if (!declare && !replacesResult
                && (Locals.TryGetValue(name, out Value previous)
                || execution.TryReadModule(this, name, out previous)
                || execution.globals.TryGetValue(name, out previous)
                || (!BuiltinVariableNames.IsLiveInformation(name)
                    && TryReadInformation(name, out previous)))
                && previous.Kind == ValueKind.HashTable)
            {
                if (value.Kind == ValueKind.Number && value.Number == HashConstants.RemoveAll)
                {
                    value = Value.FromHashTable(previous.AsHashTable().Clear());
                }
                else if (value.Kind != ValueKind.HashTable)
                {
                    throw new InvalidOperationException("HASHTBLへの代入にはHASHTBLまたはHASH_REMOVEALLを指定してください。");
                }
            }

            if (declare)
            {
                Locals[name] = value;
                return;
            }

            if (Locals.ContainsKey(name))
            {
                Locals[name] = value;
            }
            else if (execution.TryWriteThreadVariable(name, value))
            {
                return;
            }
            else if (execution.TryWriteModule(this, name, value))
            {
                return;
            }
            else if (execution.constants.Contains(name))
            {
                throw new InvalidOperationException(
                    $"定数は変更できません: {name}");
            }
            else if (BuiltinVariableNames.IsLiveInformation(name))
            {
                throw new InvalidOperationException(
                    $"動的な特殊変数は読み取り専用です: {name}");
            }
            else if (execution.globals.ContainsKey(name))
            {
                execution.globals[name] = value;
            }
            else
            {
                if (execution.ExplicitVariables
                    && !TryReadInformation(name, out _))
                {
                    throw new InvalidOperationException($"変数の宣言が必要です: {name}");
                }

                Locals[name] = value;
            }
        }

        /// <summary>
        /// 指定した自動操作関数を非同期に実行します。
        /// </summary>
        public ValueTask<Value> InvokeAsync(CallExpr call,
            CancellationToken token) => execution.InvokeAsync(call, this);

        public bool IsCoreMemberTarget(Value target) =>
            target.ObjectReference is CoreNamespace;

        public ValueTask<Value> InvokeCoreMemberAsync(Value target,
            MemberExpr member, Expr? assignment, CancellationToken token) =>
            execution.InvokeCoreMemberAsync(target, member, assignment, this);

        void IEvaluationContext.Write(string name, Value value) => Write(name, value);

        public ValueTask<Value> InvokeReferenceMemberAsync(MemberExpr member, Expr? assignment,
            CancellationToken token) => execution.InvokeReferenceMemberAsync(member, assignment, this);

        public ValueTask<Value> InvokeMemberAsync(Value target, string name,
            IReadOnlyList<Value> arguments, Value? assigned,
            CancellationToken token) => execution.InvokeMemberAsync(
                target, name, arguments, assigned, token);
    }
}
