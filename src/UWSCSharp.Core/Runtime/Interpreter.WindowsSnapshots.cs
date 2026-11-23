namespace UWSCSharp.Core;

public sealed partial class Interpreter
{
    private sealed partial class Execution
    {
        /// <summary>
        /// GETITEMで取得した配列を公開します。adapterの失敗時も、最後に取得できた配列を公開します。
        /// </summary>
        private void PublishItemSnapshot(string name,
            IWindowsAutomationSession session, Scope caller,
            Exception? operationFailure)
        {
            if (!name.Equals(BuiltinFunctionNames.GetItem,
                StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                IReadOnlyList<string> items = session.AllItems;
                PublishGlobal(caller, BuiltinVariableNames.AllItemList,
                    StringArray(items));
            }
            catch (Exception snapshotFailure) when (operationFailure is not null)
            {
                IEnumerable<Exception> previous = operationFailure.Data[
                    "UWSCSharp.SnapshotErrors"] is AggregateException errors
                        ? errors.InnerExceptions : [];
                operationFailure.Data["UWSCSharp.SnapshotErrors"] =
                    new AggregateException("GETITEMのsnapshot公開に失敗しました。",
                        previous.Append(snapshotFailure));
            }
        }

        /// <summary>
        /// 成功した関数の取得結果を、関数ごとの組み込み変数へ公開します。
        /// </summary>
        private ValueTask PublishResultSnapshotsAsync(string name,
            IWindowsAutomationSession session, Scope caller) =>
            name.ToUpperInvariant() switch
            {
                BuiltinFunctionNames.GetAllWin => PublishWindowIdsAsync(session, caller),
                BuiltinFunctionNames.GetOleItem => PublishOleItemsAsync(session, caller),
                BuiltinFunctionNames.ChkImg => PublishImageResultAsync(session, caller),
                _ => ValueTask.CompletedTask
            };

        private async ValueTask PublishWindowIdsAsync(
            IWindowsAutomationSession session, Scope caller)
        {
            var snapshot = Value.FromArray(UwscArray.FromValues(
                session.AllWindowIds.Select(id => Value.Numeric(id))));
            await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
            PublishGlobal(caller, BuiltinVariableNames.AllWinId, snapshot);
        }

        private async ValueTask PublishOleItemsAsync(
            IWindowsAutomationSession session, Scope caller)
        {
            var snapshot = Value.FromArray(UwscArray.FromValues(session.AllOleItems));
            await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
            PublishGlobal(caller, BuiltinVariableNames.AllOleItem, snapshot);
        }

        private async ValueTask PublishImageResultAsync(
            IWindowsAutomationSession session, Scope caller)
        {
            WindowsImageResult image = session.ImageResult;
            // 全結果の構築が成功してから公開し、途中失敗による混在を防ぎます。
            var imageX = Value.Numeric(image.X);
            var imageY = Value.Numeric(image.Y);
            var allX = Value.FromArray(UwscArray.Create(
                [image.AllX.Count - 1], image.AllX.Select(x => Value.Numeric(x))));
            var allY = Value.FromArray(UwscArray.Create(
                [image.AllY.Count - 1], image.AllY.Select(y => Value.Numeric(y))));
            await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
            State.Snapshots[BuiltinVariableNames.GImgX] = imageX;
            State.Snapshots[BuiltinVariableNames.GImgY] = imageY;
            State.Snapshots[BuiltinVariableNames.AllImgX] = allX;
            State.Snapshots[BuiltinVariableNames.AllImgY] = allY;
            foreach (string variable in new[]
            {
                BuiltinVariableNames.GImgX,
                BuiltinVariableNames.GImgY,
                BuiltinVariableNames.AllImgX,
                BuiltinVariableNames.AllImgY
            })
            {
                caller.Locals.Remove(variable);
            }
        }
    }
}
