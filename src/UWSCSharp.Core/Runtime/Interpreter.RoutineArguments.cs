namespace UWSCSharp.Core;

public sealed partial class Interpreter
{
    private sealed partial class Execution
    {
        /// <summary>
        /// ユーザー関数の仮引数へ実引数を評価して束縛します。
        /// </summary>
        /// <param name="call">呼出し式。</param>
        /// <param name="routine">呼出し先の定義。</param>
        /// <param name="caller">実引数を評価する呼出し元のスコープ。</param>
        /// <param name="callee">仮引数を登録する呼出し先のスコープ。</param>
        /// <param name="copybacks">
        /// VAR引数の書戻し先を受け取る一覧。
        /// nullの場合は開始時のコピーとし、書戻しを記録しません。
        /// </param>
        private async ValueTask BindRoutineArgumentsAsync(
            CallExpr call,
            Routine routine,
            Scope caller,
            Scope callee,
            List<(string Parameter, Expr Target)>? copybacks)
        {
            for (int i = 0; i < routine.Parameters.Count; i++)
            {
                Parameter parameter = routine.Parameters[i];
                bool supplied = i < call.Arguments.Count
                    && call.Arguments[i] is not OmittedExpr;
                Expr? argument = supplied
                    ? call.Arguments[i] : parameter.Default;
                if (argument is ReferenceExpr reference)
                {
                    if (!parameter.ByReference)
                    {
                        throw new InvalidOperationException(
                            "VAR指定には参照引数を宣言してください。");
                    }

                    argument = reference.Target;
                }

                if (argument is null)
                {
                    throw new InvalidOperationException(
                        $"必須引数を省略できません: {parameter.Name}");
                }

                if (parameter.ByReference && supplied)
                {
                    string name = ReferenceName(argument, caller);
                    if (IsConstant(caller, name))
                    {
                        throw new InvalidOperationException(
                            "定数をVAR引数に渡すことはできません。");
                    }

                    copybacks?.Add((parameter.Name, argument));
                }

                Scope argumentScope = supplied ? caller : callee;
                Value value = await argument.EvaluateAsync(
                    argumentScope, token)
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
                if (copybacks is null)
                {
                    // THREADでは引数を検査する前に添字関数の停止を確認します。
                    ThrowIfStoppedNativeDllRoutine();
                }

                if (parameter.ArrayRank > 0
                    && !(value.Kind == ValueKind.HashTable
                        && parameter.ArrayRank == 1)
                    && (value.Kind != ValueKind.Array
                        || value.AsArray().Rank != parameter.ArrayRank))
                {
                    throw new InvalidOperationException(
                        $"配列引数の次元数が一致しません: {parameter.Name}");
                }

                callee.Locals.Add(parameter.Name, value);
            }
        }
    }
}
