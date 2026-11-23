using System.Globalization;
using System.Runtime.InteropServices.ComTypes;

namespace UWSCSharp.Windows;

internal sealed partial class NativeComEventConnection
{
    private static (Guid InterfaceId, int MemberId) ResolveTypeLibraryEvent(
        object target, string interfaceName, string eventName,
        CancellationToken token)
    {
        if (target is not IDispatchTypeInformation dispatch)
        {
            throw ComOperationErrors.Record(new InvalidOperationException(
                "COMイベントのディスパッチ型情報が必要です。"));
        }

        ComOperationErrors.ThrowIfFailed(dispatch.GetTypeInfoCount(out uint count));
        if (count == 0)
        {
            throw ComOperationErrors.Record(new InvalidOperationException(
                "COMイベントのディスパッチ型情報がありません。"));
        }

        ITypeInfo? information = null;
        Exception? informationFailure = null;
        try
        {
            token.ThrowIfCancellationRequested();
            ComOperationErrors.ThrowIfFailed(dispatch.GetTypeInfo(0,
                (uint)CultureInfo.CurrentCulture.LCID, out information));
            if (information is null)
            {
                throw ComOperationErrors.Record(new InvalidOperationException(
                    "COMイベントのディスパッチ型情報が返されませんでした。"));
            }

            ITypeLib? library = null;
            Exception? libraryFailure = null;
            try
            {
                // 既定の入力側インタフェースを、イベント側と混同しません。
                ComOperationErrors.ThrowIfFailed(((IEventTypeInfo)information).GetContainingTypeLib(out library, out _));
                if (library is null)
                {
                    throw ComOperationErrors.Record(new InvalidOperationException(
                        "COMイベントの型ライブラリが返されませんでした。"));
                }

                return ResolveLibraryMember(library, interfaceName, eventName, token);
            }
            catch (Exception failure)
            {
                libraryFailure = failure;
                throw;
            }
            finally
            {
                Release(library, libraryFailure);
            }
        }
        catch (Exception failure)
        {
            informationFailure = failure;
            throw;
        }
        finally
        {
            Release(information, informationFailure);
        }
    }

    private static (Guid InterfaceId, int MemberId) ResolveLibraryMember(
        ITypeLib library, string interfaceName, string eventName,
        CancellationToken token)
    {
        int count = 0;
        Operation(() => count = library.GetTypeInfoCount());
        if (count < 0)
        {
            throw ComOperationErrors.Record(new InvalidOperationException(
                "COM型ライブラリの型情報数が不正です。"));
        }

        for (int index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            TYPEKIND kind = default;
            Operation(() => library.GetTypeInfoType(index, out kind));
            if (kind is not (TYPEKIND.TKIND_DISPATCH or TYPEKIND.TKIND_INTERFACE))
            {
                continue;
            }

            ITypeInfo? candidate = null;
            Exception? candidateFailure = null;
            try
            {
                Operation(() => library.GetTypeInfo(index, out candidate));
                if (candidate is null)
                {
                    throw ComOperationErrors.Record(new InvalidOperationException(
                        "COMイベントのインタフェース型情報が返されませんでした。"));
                }

                string name = "";
                ComOperationErrors.ThrowIfFailed(((IEventTypeInfo)candidate).GetDocumentation(-1, out name,
                    out _, out _, out _));
                if (!string.Equals(name, interfaceName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                TYPEATTR attribute = ReadTypeAttributes(candidate);
                if (attribute.typekind != TYPEKIND.TKIND_DISPATCH)
                {
                    throw ComOperationErrors.Record(new InvalidOperationException(
                        "ディスパッチ型のCOMイベントを指定してください。"));
                }

                // 接続時のFindConnectionPointで、対象の出力側IIDも検証します。
                return (attribute.guid, ResolveMember(
                    candidate, attribute, eventName, token));
            }
            catch (Exception failure)
            {
                candidateFailure = failure;
                throw;
            }
            finally
            {
                Release(candidate, candidateFailure);
            }
        }

        throw ComOperationErrors.Record(new InvalidOperationException(
            $"COMイベントのインタフェースがありません: {interfaceName}"));
    }
}
