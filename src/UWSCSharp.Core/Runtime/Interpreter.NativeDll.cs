using System.Runtime.ExceptionServices;

namespace UWSCSharp.Core;

/// <summary>
/// DLL引数を渡し、参照先の内容を呼出し後の値へ更新します。
/// </summary>
internal interface INativeDllScalarInvoker
{
    object? InvokeNativeDll(NativeDllDeclaration declaration,
        string workingDirectory, object?[] arguments, CancellationToken token,
        ushort? controlWord);
}

public sealed partial class Interpreter
{
    private sealed partial class Execution
    {
        private static readonly TimeSpan NativeDllCallbackCleanupTimeout = TimeSpan.FromSeconds(5);

        private sealed class NativeDllRoutineStopSignal(
            NativeDllRoutineFrame frame) : OperationCanceledException
        {
            internal NativeDllRoutineFrame Frame { get; } = frame;
        }

        private sealed class NativeDllIndexAbortSignal(
            NativeDllWriteBackContext context) : Exception
        {
            internal NativeDllWriteBackContext Context { get; } = context;
        }

        private bool ContinueStoppedNativeDllExpression(CallExpr call, Scope caller) =>
            State.NativeDllWriteBackDepth > 0 && finallyDepth == 0
            && State.LifetimeToken.IsCancellationRequested
            && State.NativeDllRoutine is { } frame
            && frame.WriteBackDepth == State.NativeDllWriteBackDepth
            && (SkipStoppedNativeDllBuiltin(call.Name)
                || call.Name.Equals(BuiltinFunctionNames.Abs,
                    StringComparison.OrdinalIgnoreCase)
                || ResolvesToUserRoutine(call.Name, caller));

        private bool ResolvesToUserRoutine(string name, Scope caller)
        {
            if (BuiltinFunctions.IsBuiltin(name))
            {
                return false;
            }
            ModuleState? module = caller.Module;
            // 通常の呼出しと同じ順序で、モジュールのDLL宣言を優先します。
            return module?.Definition.Routines.ContainsKey(name) == true
                || (module?.NativeDlls.TryGet(name, out _) != true
                    && TryGetGlobalRoutine(name, out _));
        }

        private bool SkipStoppedNativeDllBuiltin(string name) =>
            State.NativeDllWriteBackDepth > 0 && finallyDepth == 0
            && State.LifetimeToken.IsCancellationRequested
            && (name.Equals(BuiltinFunctionNames.VarType,
                    StringComparison.OrdinalIgnoreCase)
                || name.Equals(BuiltinFunctionNames.Val,
                    StringComparison.OrdinalIgnoreCase));

        private void ThrowIfStoppedNativeDllRoutine()
        {
            if (State.NativeDllWriteBackDepth > 0 && finallyDepth == 0
                && State.NativeDllRoutine is { } frame
                && frame.WriteBackDepth == State.NativeDllWriteBackDepth
                && State.LifetimeToken.IsCancellationRequested)
            {
                // 実行中の添字関数を抜け、外側のDLL書戻しを続けます。
                throw new NativeDllRoutineStopSignal(frame);
            }
        }

        private async ValueTask<Value> EvaluateNativeDllArgumentAsync(
            Expr expression, Scope caller)
        {
            Value value = await expression.EvaluateAsync(caller, token)
                .ConfigureAwait(WorkerThreadContext.IsCurrent);
            // 型変換や配列の位置計算より先に添字関数の停止を確認します。
            ThrowIfStoppedNativeDllRoutine();
            return value;
        }

        private async ValueTask<ExceptionDispatchInfo?> RunNativeDllRoutineBodyAsync(
            Routine routine, Scope callee)
        {
            NativeDllRoutineFrame? saved = State.NativeDllRoutine;
            var frame = new NativeDllRoutineFrame(State.NativeDllWriteBackDepth);
            State.NativeDllRoutine = frame;
            try
            {
                await BlockAsync(routine.Body, callee)
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
            }
            catch (NativeDllRoutineStopSignal stop) when (
                ReferenceEquals(stop.Frame, frame)
                && stop.Data["UWSCSharp.FinallyErrors"] is null)
            {
                // FINALLYを通過した後のRESULTを外側の書戻しへ返します。
            }
            catch (ScriptException error) when (
                IsNativeDllRoutineCopybackFailure(error, frame)
                && error.Data["UWSCSharp.FinallyErrors"] is null)
            {
                // 未処理の内側VARエラーでも、添字関数のVAR書戻しを完了します。
                return ExceptionDispatchInfo.Capture(error);
            }
            finally
            {
                State.NativeDllRoutine = saved;
            }
            return null;
        }

        private static bool IsNativeDllRoutineCopybackFailure(
            Exception error, NativeDllRoutineFrame frame)
        {
            while (true)
            {
                if (ReferenceEquals(error, frame.CopybackFailure))
                {
                    return true;
                }
                if (error is not ScriptException || error.InnerException is null)
                {
                    return false;
                }
                error = error.InnerException;
            }
        }

        private sealed class NativeDllAssignment
        {
            internal CancellationToken? CompletionToken;
        }

        private ValueTask<Value> EvaluateNativeDllAssignmentAsync(
            Expr expression, Scope scope)
        {
            if (expression is CallExpr call)
            {
                return InvokeAsync(call, scope, new NativeDllAssignment());
            }
            if (expression is not BinaryExpr binary)
            {
                return expression.EvaluateAsync(scope, token);
            }

            var assignment = new NativeDllAssignment();
            return BinaryExpressionEvaluator.EvaluateAsync(binary, scope, token,
                shortCircuit: false, EvaluateOperandAsync,
                () => assignment.CompletionToken ?? token);

            ValueTask<Value> EvaluateOperandAsync(Expr operand,
                CancellationToken activeToken)
            {
                if (assignment.CompletionToken is null)
                {
                    return operand is CallExpr callOperand
                        ? InvokeAsync(callOperand, scope, assignment)
                        : operand.EvaluateAsync(scope, activeToken);
                }
                return EvaluateStoppedAssignmentOperandAsync(
                    operand, scope, activeToken);
            }
        }

        private bool IsSkippedStoppedAssignmentRoutine(Expr operand, Scope scope) =>
            operand is GroupedExpr group
                ? IsSkippedStoppedAssignmentRoutine(group.Inner, scope)
                : operand is CallExpr call
                    && !FileFunctions.IsBuiltin(call.Name)
                    && !WindowsFunctions.IsBuiltin(call.Name)
                    && ResolvesToUserRoutine(call.Name, scope);

        private async ValueTask<Value> EvaluateStoppedAssignmentOperandAsync(
            Expr operand, Scope scope, CancellationToken activeToken)
        {
            activeToken.ThrowIfCancellationRequested();
            ThrowIfAutomationFailed();
            switch (operand)
            {
                case GroupedExpr group:
                    return await EvaluateStoppedAssignmentOperandAsync(
                        group.Inner, scope, activeToken)
                        .ConfigureAwait(WorkerThreadContext.IsCurrent);
                case UnaryExpr unary:
                    return unary.Apply(await EvaluateStoppedAssignmentOperandAsync(
                        unary.Operand, scope, activeToken)
                        .ConfigureAwait(WorkerThreadContext.IsCurrent));
                case BinaryExpr binary:
                    return await BinaryExpressionEvaluator.EvaluateAsync(
                        binary, scope, activeToken, shortCircuit: false,
                        (expression, currentToken) => EvaluateStoppedAssignmentOperandAsync(
                            expression, scope, currentToken))
                        .ConfigureAwait(WorkerThreadContext.IsCurrent);
                case CallExpr call when call.Name.Equals(BuiltinFunctionNames.Abs,
                    StringComparison.OrdinalIgnoreCase)
                    || call.Name.Equals(BuiltinFunctionNames.Val,
                        StringComparison.OrdinalIgnoreCase):
                    return await EvaluateStoppedNumericBuiltinAsync(
                        call, scope, activeToken)
                        .ConfigureAwait(WorkerThreadContext.IsCurrent);
                case CallExpr when IsSkippedStoppedAssignmentRoutine(operand, scope):
                    // 停止後のユーザー関数は引数と本文を省き、EMPTYを返します。
                    return Value.Empty;
                case LiteralExpr or NameExpr or OmittedExpr:
                    return await operand.EvaluateAsync(scope, activeToken)
                        .ConfigureAwait(WorkerThreadContext.IsCurrent);
                default:
                    // 停止後は新たな副作用を起こさず、子スレッドの取消を通知します。
                    throw new OperationCanceledException(State.LifetimeToken);
            }
        }

        private async ValueTask<Value> EvaluateStoppedNumericBuiltinAsync(
            CallExpr call, Scope scope, CancellationToken activeToken)
        {
            List<Value> arguments = await BuiltinInvocation
                .EvaluateValidatedArgumentsAsync(call, scope, activeToken,
                    (expression, currentToken) => EvaluateStoppedAssignmentOperandAsync(
                        expression, scope, currentToken))
                .ConfigureAwait(WorkerThreadContext.IsCurrent);
            activeToken.ThrowIfCancellationRequested();
            if (call.Name.Equals(BuiltinFunctionNames.Val, StringComparison.OrdinalIgnoreCase)
                && arguments[0].Kind == ValueKind.Empty
                && IsSkippedStoppedAssignmentRoutine(call.Arguments[0], scope))
            {
                // 停止で省いた関数の返値を直接渡すと、原版のVALは0を返します。
                return Value.Numeric(0);
            }
            return BuiltinFunctions.Evaluate(call.Name, arguments,
                EffectiveSameString, NumericArithmetic as INumericBuiltinArithmetic);
        }

        private sealed record NativeDllWriteBack(Expr Target,
            NativeDllPrimitive Primitive, object?[] Values, int Index,
            UwscArray? Array, bool ArrayParameter = false,
            bool NullReference = false, bool DeferArrayBoundsError = false);

        private readonly record struct NativeDllArgument(
            NativeDllScalarType Type, Expr Expression, object?[] Values,
            int Index, bool Fields, Value Value);

        private async ValueTask<Value> InvokeNativeDllAsync(
            NativeDllDeclarationStatement statement,
            IReadOnlyList<Expr> arguments, Scope caller,
            NativeDllAssignment? assignment = null)
        {
            NativeDllDeclaration declaration = statement.Declaration;
            if (windows is not INativeDllScalarInvoker invoker
                || !NativeDllScalarInput.Supports(declaration))
            {
                throw new InvalidOperationException(
                    $"{Keywords.DefDll}の関数呼出しには対応していません: {declaration.Name}");
            }

            if (!NativeDllScalarInput.MatchesArguments(declaration, arguments))
            {
                throw new InvalidOperationException(
                    $"{Keywords.DefDll}の引数の数または形式が不正です: {declaration.Name}");
            }

            object?[] supplied = new object?[declaration.Parameters.Count];
            List<NativeDllWriteBack>? targets = null;
            int argumentIndex = 0;
            List<NativeDllArgument> evaluated = [];
            await EvaluateArgumentsAsync(declaration.Parameters, supplied, fields: false)
                .ConfigureAwait(WorkerThreadContext.IsCurrent);
            foreach (NativeDllArgument argument in evaluated)
            {
                await ReadArgumentAsync(argument)
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
            }

            object? result;
            try
            {
                token.ThrowIfCancellationRequested();
                // 引数の評価中に停止した場合は、新たなDLL呼出しを始めません。
                ThrowIfStoppedNativeDllRoutine();
                result = invoker.InvokeNativeDll(declaration,
                    ReadCurrentDirectory().Text, supplied, token,
                    (State.NumericArithmetic as INumericControlArithmetic)?.ActiveControlWord);
            }
            catch (Exception error) when (error is DllNotFoundException
                or EntryPointNotFoundException or BadImageFormatException
                or PlatformNotSupportedException or ArgumentException)
            {
                throw new InvalidOperationException(
                    $"{Keywords.DefDll} {declaration.Name}: {error.Message}", error);
            }

            NativeDllPrimitive resultType =
                (declaration.ReturnType as NativeDllScalarType)?.Primitive
                ?? NativeDllPrimitive.Void;
            Value returned = NativeDllScalarResult.Convert(resultType, result,
                preserveIntegerWidth: true);
            ExceptionDispatchInfo? copybackFailure = null;
            if (targets is not null)
            {
                copybackFailure = await CompleteNativeDllWriteBackAsync(targets, caller)
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
            }
            if (assignment is not null && copybackFailure is null
                && State.NativeDllWriteBackDepth == 0 && finallyDepth == 0
                && State.Control is { } control)
            {
                bool mainEnded = mainEndedByScript
                    && State.LifetimeToken.IsCancellationRequested;
                if (mainEnded || (control.IsQuitRequested
                    && !runCancellation.IsCancellationRequested))
                {
                    // メイン自身の終了による子の停止と外部からの実行取消を区別します。
                    CancellationToken completionToken = mainEnded
                        ? cancellationToken : runCancellation.Token;
                    completionToken.ThrowIfCancellationRequested();
                    ThrowIfAutomationFailed();
                    assignment.CompletionToken = completionToken;
                    return returned;
                }
            }
            await ObserveCancellationAsync()
                .ConfigureAwait(WorkerThreadContext.IsCurrent);
            copybackFailure?.Throw();

            return returned;

            async ValueTask EvaluateArgumentsAsync(IReadOnlyList<NativeDllType> types,
                object?[] values, bool fields)
            {
                for (int index = 0; index < types.Count; index++)
                {
                    if (types[index] is NativeDllStructureType structure)
                    {
                        object?[] children = new object?[structure.Fields.Count];
                        values[index] = children;
                        await EvaluateArgumentsAsync(structure.Fields, children, fields: true)
                            .ConfigureAwait(WorkerThreadContext.IsCurrent);
                        continue;
                    }

                    var scalar = (NativeDllScalarType)types[index];
                    Expr argument = arguments[argumentIndex++];
                    if (argument is ReferenceExpr reference)
                    {
                        argument = reference.Target;
                    }

                    // 通常引数の値を先に評価し、配列の参照解決とABI変換は後で行います。
                    Value value = scalar.IsArray ? Value.Empty
                        : await EvaluateNativeDllArgumentAsync(argument, caller)
                            .ConfigureAwait(WorkerThreadContext.IsCurrent);
                    evaluated.Add(new(scalar, argument, values, index, fields, value));
                }
            }

            async ValueTask ReadArgumentAsync(NativeDllArgument input)
            {
                NativeDllScalarType scalar = input.Type;
                Expr argument = input.Expression;
                object?[] values = input.Values;
                int index = input.Index;
                bool fields = input.Fields;
                // 配列引数の関数や定数は、値を評価する前に拒否します。
                if (scalar.IsArray
                    && argument is not LiteralExpr { Value.Kind: ValueKind.Null }
                    && NativeDllWriteTarget(argument, caller) is null)
                {
                    throw new InvalidOperationException(
                        "DLLの配列引数には、書込み可能な変数・配列要素またはNULLを指定してください。");
                }

                Value value = scalar.IsArray
                    ? await ReadNativeDllArrayArgumentAsync(argument, caller)
                        .ConfigureAwait(WorkerThreadContext.IsCurrent)
                    : input.Value;
                bool text = NativeDllScalarInput.IsText(scalar.Primitive);
                bool numericReference = scalar.IsByReference
                    && !scalar.IsArray && !text;
                if (numericReference && value.Kind == ValueKind.Null)
                {
                    // NULL直接指定以外は、呼出し後の書戻し位置で失敗させます。
                    if (argument is not LiteralExpr)
                    {
                        targets ??= [];
                        targets.Add(new(argument, scalar.Primitive,
                            values, index, null, NullReference: true));
                    }
                    return;
                }

                if (scalar.IsArray && value.Kind == ValueKind.Null)
                {
                    if (argument is LiteralExpr)
                    {
                        return;
                    }

                    if (scalar.Primitive == NativeDllPrimitive.Extended)
                    {
                        throw new InvalidOperationException(
                            "DLLのEXTENDED配列引数にNULL変数は指定できません。");
                    }
                }

                ushort controlWord = (State.NumericArithmetic
                    as INumericControlArithmetic)?.ActiveControlWord ?? 0;
                bool arrayScalar = scalar.IsArray
                    && value.Kind != ValueKind.Array;
                UwscArray? array = scalar.IsArray && !arrayScalar
                    ? NativeDllArray(value) : null;
                bool handleText = value.Kind == ValueKind.String
                    && NativeDllScalarInput.IsHandleByValue(scalar);
                values[index] = text || handleText ? value : array is null
                    ? arrayScalar && value.Kind == ValueKind.Null
                        && scalar.Primitive is NativeDllPrimitive.Uint
                            or NativeDllPrimitive.Dword
                        ? (object)0u
                        : NativeDllScalarInput.Convert(scalar.Primitive,
                            value, controlWord)
                    : ConvertNativeDllArray(array, scalar.Primitive, controlWord);
                bool emptyText = text && (value.Kind is ValueKind.Null
                    or ValueKind.Empty || value is { Kind: ValueKind.String, Text.Length: 0 });
                if (((fields && !emptyText) || scalar.IsByReference || scalar.IsArray)
                    && NativeDllWriteTarget(argument, caller) is { } target)
                {
                    targets ??= [];
                    targets.Add(new(target, scalar.Primitive, values, index,
                        array, ArrayParameter: scalar.IsArray,
                        DeferArrayBoundsError: scalar.IsByReference
                            && (text ? fields : !scalar.IsArray)));
                }
            }
        }

        private static UwscArray NativeDllArray(Value value) =>
            value.Kind == ValueKind.Array && value.Array is { Count: > 0 } array
                ? array : throw new InvalidOperationException(
                    "DLLの配列引数には、要素のある数値配列またはNULLを指定してください。");

        private object?[] ConvertNativeDllArray(UwscArray array,
            NativeDllPrimitive primitive, ushort controlWord)
        {
            object?[] converted = new object?[array.Count];
            for (int index = 0; index < array.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                converted[index] = NativeDllScalarInput.Convert(primitive,
                    array.Values[index], controlWord);
            }

            return converted;
        }

        private Expr? NativeDllWriteTarget(Expr argument, Scope caller)
        {
            if (argument is MemberExpr { Target: WithTargetExpr }
                && caller.WithTarget?.ObjectReference
                    is not CoreNamespace { Module: not null })
            {
                return null;
            }

            Expr target = NormalizeModuleReference(argument, caller);
            if (target is not (NameExpr or IndexExpr))
            {
                return null;
            }

            string name = ReferenceName(target);
            int dot = name.IndexOf('.');
            if (dot >= 0 && (caller.Read(name[..dot]).ObjectReference
                is not CoreNamespace { Module: { } module }
                || module.FindField(name[(dot + 1)..]) is null))
            {
                return null;
            }

            if (IsConstant(caller, name)
                || BuiltinVariableNames.IsLiveInformation(name)
                || name.Equals(BuiltinVariableNames.ComErrFlg,
                    StringComparison.OrdinalIgnoreCase)
                || (!caller.Locals.ContainsKey(name)
                    && BuiltinConstants.TryGet(name, out _)))
            {
                return null;
            }

            return target;
        }

        private static Value ConvertNativeDllWriteBack(
            NativeDllWriteBack target, object? updated) =>
            updated switch
            {
                uint unsigned when target.ArrayParameter =>
                    NativeDllScalarResult.UnsignedArrayValue(unsigned),
                nuint handle when target.ArrayParameter =>
                    NativeDllScalarResult.UnsignedArrayValue(handle),
                _ => NativeDllScalarResult.Convert(target.Primitive, updated,
                    preserveIntegerWidth: true)
            };

        private async ValueTask<ExceptionDispatchInfo?> CompleteNativeDllWriteBackAsync(
            IReadOnlyList<NativeDllWriteBack> targets, Scope caller)
        {
            CancellationToken saved = token;
            NativeDllWriteBackContext? savedWriteBack = State.NativeDllWriteBack;
            var writeBack = new NativeDllWriteBackContext();
            State.NativeDllWriteBack = writeBack;
            CancellationTokenSource? budget = null;
            CancellationTokenRegistration registration = default;
            State.NativeDllWriteBackDepth++;
            try
            {
                if (State.NativeDllWriteBackDepth == 1 && finallyDepth == 0)
                {
                    // DLL復帰後の書戻しを終えてから、元の取消を通知します。
                    // 取消後に添字の再評価が終わらない場合は5秒で打ち切ります。
                    budget = new CancellationTokenSource(
                        Timeout.InfiniteTimeSpan, clock);
                    registration = saved.UnsafeRegister(static state =>
                        ((CancellationTokenSource)state!).CancelAfter(
                            NativeDllCallbackCleanupTimeout), budget);
                    token = budget.Token;
                }
                await WriteNativeDllArgumentsAsync(targets, caller)
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
                return writeBack.CopybackFailure;
            }
            finally
            {
                token = saved;
                State.NativeDllWriteBack = savedWriteBack;
                State.NativeDllWriteBackDepth--;
                registration.Dispose();
                budget?.Dispose();
            }
        }

        private async ValueTask WriteNativeDllArgumentsAsync(
            IReadOnlyList<NativeDllWriteBack> targets, Scope caller)
        {
            // 原版に合わせ、引数の順に書き戻し、添字は代入時に再評価します。
            foreach (NativeDllWriteBack target in targets)
            {
                if (target.NullReference)
                {
                    throw new InvalidOperationException(
                        "DLLの数値VAR引数がNULLのため書き戻せません。");
                }

                object? updated = target.Values[target.Index];
                Value value = updated is Value scriptValue ? scriptValue
                    : target.Array is { } array
                    ? Value.FromArray(array.WithValues(
                        ((object?[])updated!).Select(element =>
                            element is Value arrayValue ? arrayValue
                                : ConvertNativeDllWriteBack(target, element)), token))
                    : ConvertNativeDllWriteBack(target, updated);
                await ObserveCancellationAsync()
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
                Expr reference = target.Target;
                var index = reference as IndexExpr;
                string? arrayName = null;
                if (target.ArrayParameter
                    && NormalizeModuleReference(reference, caller) is IndexExpr arrayIndex
                    && NativeDllArrayVariable(arrayIndex, caller).Name is { } name
                    && caller.Read(name).Kind == ValueKind.Array)
                {
                    arrayName = name;
                    index = arrayIndex;
                }
                if (index is not null)
                {
                    Value[] indices;
                    try
                    {
                        indices = await ResolveNativeDllWriteBackIndicesAsync(
                            index, caller, arrayName is not null
                                || caller.Read(index.Name).Kind == ValueKind.Array)
                            .ConfigureAwait(WorkerThreadContext.IsCurrent);
                    }
                    catch (Exception error) when (
                        State.NativeDllWriteBack!.CopybackFailure is not null
                        && (error is ScriptException || ScriptErrors.IsScriptError(error)))
                    {
                        // 添字の評価に失敗したら打ち切り、先に記録したエラーを通知します。
                        break;
                    }
                    if (arrayName is not null)
                    {
                        WriteNativeDllArray(arrayName, indices, value, caller);
                        continue;
                    }
                    reference = new IndexExpr(index.Name,
                        [.. indices.Select(coordinate => (Expr)new LiteralExpr(coordinate))]);
                }
                await WriteReferenceAsync(reference, value, caller,
                    deferArrayBoundsError: target.DeferArrayBoundsError)
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
            }
        }
    }
}
