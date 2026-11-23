namespace UWSCSharp.Core;

public sealed partial class Interpreter
{
    private sealed partial class Execution
    {
        private async ValueTask<Flow> BlockAsync(
            IReadOnlyList<Statement> statements, Scope scope)
        {
            foreach (Statement statement in statements)
            {
                Flow flow;
                try
                {
                    await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
                    scope.CurrentLine = statement.Line;
                    flow = await StatementAsync(statement,
                        scope).ConfigureAwait(WorkerThreadContext.IsCurrent);
                    // 最後の文やEXITでも、復帰時の取消と非同期障害を成功として扱いません。
                    await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
                }
                catch (AutomationFailureException failure)
                {
                    ScriptException error = ScriptAutomationFailure(statement.Line, failure);
                    error.SetSourcePath(statement.SourcePath);
                    throw error;
                }
                catch (ScriptException error)
                {
                    error.SetSourcePath(statement.SourcePath);
                    throw;
                }

                if (flow.Kind != FlowKind.Normal)
                {
                    return flow;
                }
            }

            return default;
        }

        private async ValueTask<Flow> StatementAsync(
            Statement statement, Scope scope)
        {
            await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
            switch (statement)
            {
                case NativeDllDeclarationStatement declaration:
                    (scope.Module?.NativeDlls ?? ActiveNativeDlls)
                        .Register(declaration);
                    return default;
                case ComErrorControlStatement control:
                    State.IgnoreComErrors = control.Ignore;
                    if (control.Ignore)
                    {
                        State.ComErrorOccurred = false;
                    }

                    return default;
                case DeclarationGroupStatement group:
                    return await BlockAsync(group.Declarations, scope)
                        .ConfigureAwait(WorkerThreadContext.IsCurrent);
                case HashDeclareStatement hash:
                    Write(scope, hash.Name,
                        await EvaluateAsync(hash.Initializer, scope, hash.Line).ConfigureAwait(WorkerThreadContext.IsCurrent),
                        hash.Line, true);
                    return default;
                case AssignStatement assign:
                    if (assign.Expression is null)
                    {
                        scope.Locals.TryAdd(assign.Name,
                            Value.Empty);
                        return default;
                    }

                    Write(scope, assign.Name,
                        await EvaluateAsync(assign.Expression, scope,
                        assign.Line, completeDllAssignment: true)
                        .ConfigureAwait(WorkerThreadContext.IsCurrent), assign.Line,
                        assign.Declare);
                    return default;
                case IndexAssignStatement assign:
                    await EvaluateWriteAsync(assign.Target,
                        ArrayElement(await EvaluateAsync(assign.Expression, scope,
                        assign.Line).ConfigureAwait(WorkerThreadContext.IsCurrent), assign.Line),
                        scope, assign.Line).ConfigureAwait(WorkerThreadContext.IsCurrent);
                    return default;
                case ArrayDeclareStatement declaration:
                    return await DeclareArrayAsync(declaration,
                        scope).ConfigureAwait(WorkerThreadContext.IsCurrent);
                case PrintStatement print:
                    return await PrintAsync(print,
                        scope).ConfigureAwait(WorkerThreadContext.IsCurrent);
                case RuntimeOptionsStatement options:
                    return await ExecuteRuntimeOptionsAsync(options, scope)
                        .ConfigureAwait(WorkerThreadContext.IsCurrent);
                case LogOptionsStatement logging:
                    return await ConfigureLogAsync(logging, scope).ConfigureAwait(WorkerThreadContext.IsCurrent);
                case CallStatement call:
                    await EvaluateAsync(call.Call, scope,
                        call.Line).ConfigureAwait(WorkerThreadContext.IsCurrent);
                    return default;
                case MemberStatement member:
                    await EvaluateAsync(new AssignedMemberExpr(member.Member, member.Assigned),
                        scope, member.Line).ConfigureAwait(WorkerThreadContext.IsCurrent);
                    return default;
                case ThreadStatement thread:
                    try
                    {
                        await StartThreadAsync(thread.Call, scope)
                            .ConfigureAwait(WorkerThreadContext.IsCurrent);
                    }
                    catch (Exception failure) when (ScriptErrors.IsScriptError(failure))
                    {
                        throw new ScriptException(thread.Line, failure.Message, failure);
                    }
                    return default;
                case ExternalCallStatement external:
                    return await CallExternalAsync(external,
                        scope).ConfigureAwait(WorkerThreadContext.IsCurrent);
                case FlowStatement flow:
                    if (flow.Kind == FlowKind.Stop)
                    {
                        int code = 0;
                        if (flow.ExitCode is not null)
                        {
                            Value value = await EvaluateAsync(flow.ExitCode,
                                scope, flow.Line).ConfigureAwait(WorkerThreadContext.IsCurrent);
                            double number = Math.Truncate(At(flow.Line,
                                () => NumericOperations.RuntimeNumber(value)));
                            if (!double.IsFinite(number)
                                || number < int.MinValue || number > int.MaxValue)
                            {
                                throw new ScriptException(flow.Line,
                                    "終了コードはInt32の範囲で指定してください。");
                            }

                            code = (int)number;
                        }

                        throw new StopSignal(code);
                    }

                    return new(flow.Kind, flow.Levels);
                case WithStatement with:
                {
                    Value target = await EvaluateAsync(with.Target, scope, with.Line)
                        .ConfigureAwait(WorkerThreadContext.IsCurrent);
                    if (target.Kind != ValueKind.Object || target.ObjectReference is null)
                    {
                        throw new ScriptException(with.Line, "WITHにはモジュールまたはobjectを指定してください。");
                    }

                    Value? previous = scope.WithTarget;
                    scope.WithTarget = target;
                    try
                    {
                        return await BlockAsync(with.Body, scope).ConfigureAwait(WorkerThreadContext.IsCurrent);
                    }
                    finally
                    {
                        scope.WithTarget = previous;
                    }
                }

                case IfStatement conditional:
                    foreach (IfBranch branch in conditional.Branches)
                    {
                        if (branch.Condition is null
                            || await ConditionAsync(branch.Condition, scope,
                            branch.Line == 0 ? conditional.Line
                            : branch.Line).ConfigureAwait(WorkerThreadContext.IsCurrent))
                        {
                            return await BlockAsync(
                                branch.Body, scope).ConfigureAwait(WorkerThreadContext.IsCurrent);
                        }
                    }

                    return default;
                case SelectStatement select:
                    return await SelectAsync(select,
                        scope).ConfigureAwait(WorkerThreadContext.IsCurrent);
                case ForStatement loop:
                    return await ForAsync(loop,
                        scope).ConfigureAwait(WorkerThreadContext.IsCurrent);
                case ForInStatement loop:
                    return await ForInAsync(loop,
                        scope).ConfigureAwait(WorkerThreadContext.IsCurrent);
                case WhileStatement loop:
                    while (await ConditionAsync(
                        loop.Condition, scope, loop.Line).ConfigureAwait(WorkerThreadContext.IsCurrent))
                    {
                        await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
                        Flow result = await BlockAsync(
                            loop.Body, scope).ConfigureAwait(WorkerThreadContext.IsCurrent);
                        if (LeaveLoop(result,
                            out Flow propagated))
                        {
                            return propagated;
                        }
                    }

                    return default;
                case RepeatStatement loop:
                    do
                    {
                        await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
                        Flow result = await BlockAsync(
                            loop.Body, scope).ConfigureAwait(WorkerThreadContext.IsCurrent);
                        if (LeaveLoop(result,
                            out Flow propagated))
                        {
                            return propagated;
                        }
                    }
                    while (!await ConditionAsync(
                        loop.Condition, scope, loop.ConditionLine).ConfigureAwait(
                        false));
                    return default;
                case TryStatement attempt:
                    return await TryAsync(attempt,
                        scope).ConfigureAwait(WorkerThreadContext.IsCurrent);
                default:
                    throw new InvalidOperationException(
                        "Unknown statement node");
            }
        }

        private async ValueTask<Flow> ExecuteRuntimeOptionsAsync(
            RuntimeOptionsStatement options, Scope scope)
        {
            foreach (RuntimeOption option in options.Options)
            {
                if (option.Name == Keywords.OnlySapi)
                {
                    onlySapi = true;
                    continue;
                }

                if (option.Name is Keywords.TopStopForm or Keywords.NoStopHotkey)
                {
                    if (!stopOptions.Supported)
                    {
                        throw new ScriptException(options.Line, "この実行元は停止設定に対応していません。");
                    }

                    ScriptStopFormOption setting = option.Name == Keywords.TopStopForm
                        ? ScriptStopFormOption.TopMost : ScriptStopFormOption.DisableStopHotkey;
                    await HostCallAsync(async () =>
                    {
                        await stopOptions.ApplyAsync(setting, token)
                            .ConfigureAwait(WorkerThreadContext.IsCurrent);
                    }).ConfigureAwait(WorkerThreadContext.IsCurrent);
                    await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
                    continue;
                }

                if (option.Name is Keywords.LogPath or Keywords.LogLines or Keywords.LogFile)
                {
                    await ConfigureLogAsync(new LogOptionsStatement(options.Line,
                        [new LogOption(option.Name, option.Expressions[0])]), scope)
                        .ConfigureAwait(WorkerThreadContext.IsCurrent);
                    continue;
                }

                if (option.Name == Keywords.Position)
                {
                    if (host is not IScriptWindowPositionHost target
                        || !target.SupportsWindowPosition)
                    {
                        throw new ScriptException(options.Line,
                            "この実行元は画面の配置変更に対応していません。");
                    }

                    List<Value> coordinates = [];
                    foreach (Expr expression in option.Expressions)
                    {
                        coordinates.Add(await EvaluateAsync(expression, scope, options.Line)
                            .ConfigureAwait(WorkerThreadContext.IsCurrent));
                    }

                    ScriptWindowPosition request = At(options.Line,
                        () => ScriptWindowPosition.Read(coordinates));
                    await HostCallAsync(async () =>
                    {
                        await target.SetWindowPositionAsync(request, token)
                            .ConfigureAwait(WorkerThreadContext.IsCurrent);
                    }).ConfigureAwait(WorkerThreadContext.IsCurrent);
                    await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
                    continue;
                }

                if (windows is null)
                {
                    throw new ScriptException(options.Line, "この実行先はダイアログ設定に対応していません。");
                }

                List<Value> values = [];
                foreach (Expr expression in option.Expressions)
                {
                    values.Add(await EvaluateAsync(expression, scope, options.Line)
                        .ConfigureAwait(WorkerThreadContext.IsCurrent));
                }

                At(options.Line, () =>
                {
                    windows.ConfigureDialogOption(option.Name, values, token);
                    return true;
                });
            }

            return default;
        }

        private async ValueTask<Flow> DeclareArrayAsync(
            ArrayDeclareStatement declaration, Scope scope)
        {
            var bounds = new List<int>();
            foreach (Expr bound in declaration.Bounds)
            {
                Value boundValue = await EvaluateAsync(
                    bound, scope, declaration.Line).ConfigureAwait(WorkerThreadContext.IsCurrent);
                bounds.Add(At(declaration.Line,
                    () => ArrayOperations.Coordinate(boundValue)));
            }

            var initializers = new List<Value>();
            foreach (Expr initializer in declaration.Initializers)
            {
                initializers.Add(ArrayElement(
                    await EvaluateAsync(initializer, scope,
                    declaration.Line).ConfigureAwait(WorkerThreadContext.IsCurrent), declaration.Line));
            }

            if (declaration.Infer)
            {
                bounds.Add(initializers.Count - 1);
            }

            Write(scope, declaration.Name, At(
                declaration.Line, () => Value.FromArray(UwscArray.Create(
                bounds, initializers))), declaration.Line, true);
            return default;
        }
    }
}
