using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;

namespace UWSCSharp.Windows;

/// <summary>
/// 認識通知の一時参照を回収し、結果の文字列だけを取り出します。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ComSpeechRecognitionText
{
    // 引数resultの取得済み参照は、このメソッドへ所有権を移します。
    internal static string Read(IComRuntime runtime, ComVariantValue result)
    {
        List<object> acquisitions = [];
        Exception? primary = null;
        string? text = null;
        try
        {
            object resultObject = RequireObject(Track(result, acquisitions));
            object phrase = RequireObject(Track(runtime.Invoke(resultObject,
                "PhraseInfo", [], false), acquisitions));
            object? value = Track(runtime.Invoke(phrase, "GetText",
                [new ComVariantValue(3, 0), new ComVariantValue(3, -1),
                    new ComVariantValue(11, true)], false), acquisitions);
            text = value is ComVariantValue typed
                ? typed.Type == 8 ? (string?)typed.Value ?? string.Empty
                    : throw new InvalidDataException("認識結果がBSTRではありません。")
                : value as string
                    ?? throw new InvalidDataException("認識結果が文字列ではありません。");
        }
        catch (Exception failure)
        {
            primary = failure;
        }

        List<Exception> errors = [];
        for (int index = acquisitions.Count - 1; index >= 0; index--)
        {
            try
            {
                // 同じRCWでも取得回数分を解放し、参照を重複排除しません。
                runtime.Release(acquisitions[index]);
            }
            catch (Exception cleanup)
            {
                errors.Add(cleanup);
            }
        }

        if (errors.Count != 0)
        {
            if (primary is not null)
            {
                errors.Insert(0, primary);
            }

            throw new AggregateException("認識結果の一時参照を解放できませんでした。", errors);
        }

        if (primary is not null)
        {
            ExceptionDispatchInfo.Capture(primary).Throw();
        }

        return text!;
    }

    private static object? Track(object? value, List<object> acquisitions)
    {
        if (value is ComVariantValue typed)
        {
            if (typed.Acquisitions is not null)
            {
                acquisitions.AddRange(typed.Acquisitions);
            }
            else if (typed.Type is 9 or 13 && typed.Value is not null)
            {
                acquisitions.Add(typed.Value);
            }
        }
        else if (value is not null && value is not string
            && !value.GetType().IsValueType)
        {
            acquisitions.Add(value);
        }

        return value;
    }

    private static object RequireObject(object? value)
    {
        if (value is ComVariantValue typed)
        {
            if (typed.Type is not (9 or 13) || typed.Value is null)
            {
                throw new InvalidDataException("認識結果のCOM参照が不正です。");
            }

            return typed.Value;
        }

        if (value is null || value is string || value.GetType().IsValueType)
        {
            throw new InvalidDataException("認識結果のCOM参照がありません。");
        }

        return value;
    }
}
