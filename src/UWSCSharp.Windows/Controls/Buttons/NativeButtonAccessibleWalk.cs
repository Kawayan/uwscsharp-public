using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UWSCSharp.Windows;

/// <summary>
/// BTN補助の自己要素と子を順に探索します。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NativeButtonAccessibleWalk
{
    internal static bool Read(nint accessible, Native.Point point,
        CancellationToken token, out int role, out int state)
        => Walk(accessible, point, null, token, out role, out state);

    internal static bool Act(nint accessible, Native.Point point,
        IListSelectionPlatform platform, CancellationToken token)
        => Walk(accessible, point, platform, token, out _, out _);

    private static bool Walk(nint accessible, Native.Point point,
        IListSelectionPlatform? platform, CancellationToken token,
        out int role, out int state)
    {
        role = 0;
        state = 0;
        nint enumerator = 0;
        nint dispatch = 0;
        nint element = 0;
        var child = NativeButtonVariant.Child(0);
        Exception? primary = null;
        try
        {
            token.ThrowIfCancellationRequested();
            bool found = platform is null
                ? NativeButtonAccessibleNode.Read(accessible, child, point,
                    token, out role, out state)
                : NativeButtonAccessibleNode.Act(accessible, child, point, platform, token);
            if (found)
            {
                return true;
            }

            Clear(ref child);
            NativeButtonAccessibleApi.Query(accessible,
                NativeButtonAccessibleApi.EnumeratorId, out enumerator);
            token.ThrowIfCancellationRequested();
            if (enumerator != 0)
            {
                NativeButtonAccessibleApi.Reset(enumerator);
                token.ThrowIfCancellationRequested();
            }

            child = NativeButtonVariant.Child(0);
            int result = NativeButtonAccessibleApi.ChildCount(accessible, out int count);
            token.ThrowIfCancellationRequested();
            if (result != 0)
            {
                count = 0;
            }

            for (int index = 1; index <= count; index = WindowsOperationErrors.Add(index, 1))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (enumerator != 0)
                    {
                        // HRESULT/fetchedを使わず、providerが残した値も処理します。
                        NativeButtonAccessibleApi.Next(enumerator, ref child, out _);
                    }
                    else
                    {
                        Clear(ref child);
                        child = NativeButtonVariant.Child(index);
                    }

                    token.ThrowIfCancellationRequested();
                    if (child.Type == 3)
                    {
                        NativeButtonAccessibleApi.Release(ref dispatch);
                        result = NativeButtonAccessibleApi.Child(accessible, child, out dispatch);
                        token.ThrowIfCancellationRequested();
                        if (result != 0)
                        {
                            continue;
                        }
                    }
                    else
                    {
                        AssignDispatch(ref dispatch, child);
                        Clear(ref child);
                    }

                    if (dispatch != 0)
                    {
                        NativeButtonAccessibleApi.Release(ref element);
                        NativeButtonAccessibleApi.Query(dispatch,
                            NativeButtonAccessibleApi.AccessibleId, out element);
                        token.ThrowIfCancellationRequested();
                        if (element != 0)
                        {
                            found = Walk(element, point, platform, token, out role, out state);
                            NativeButtonAccessibleApi.Release(ref element);
                        }

                        NativeButtonAccessibleApi.Release(ref dispatch);
                        if (found)
                        {
                            return true;
                        }
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // 子の失敗後は次へ進み、残存参照は次回取得前か外側で解放します。
                    token.ThrowIfCancellationRequested();
                }
            }

            return false;
        }
        catch (Exception failure)
        {
            primary = failure;
            throw;
        }
        finally
        {
            var cleanup = new ProviderCleanup(primary);
            cleanup.ReleasePointer(ref dispatch);
            cleanup.ReleasePointer(ref enumerator);
            cleanup.ReleasePointer(ref element);
            cleanup.ClearVariant(ref child);
            cleanup.Finish();
        }
    }

    internal static void AssignDispatch(ref nint destination, NativeButtonVariant value)
    {
        switch (value.Type)
        {
            case 0:
                NativeButtonAccessibleApi.Release(ref destination);
                break;
            case 9:
                NativeButtonAccessibleApi.Assign(ref destination, value.Pointer);
                break;
            case 0x4009:
                NativeButtonAccessibleApi.Assign(ref destination,
                    Marshal.ReadIntPtr(value.Pointer));
                break;
            default:
                // 原本のNULL設定は初期値1です。登録拡張型の移植は未対応です。
                throw new InvalidCastException("VARIANTをdispatch参照へ変換できません。");
        }
    }

    private static void Clear(ref NativeButtonVariant value)
        => Marshal.ThrowExceptionForHR(NativeButtonAccessibleApi.Clear(ref value));
}
