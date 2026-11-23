using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

internal enum SpeechEngine
{
    MicrosoftSpeechPlatform,
    Sapi5,
    PreferSpeechPlatform
}

/// <summary>
/// 指定した音声エンジンを専用STA内で所有します。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ComSpeechVoice : IAsyncDisposable
{
    private readonly ComSession session;
    private readonly Value voice;

    private ComSpeechVoice(ComSession session, Value voice)
    {
        this.session = session;
        this.voice = voice;
    }

    internal static async Task<ComSpeechVoice> CreateAsync(
        SpeechEngine engine, IComRuntime? runtime = null,
        CancellationToken token = default)
    {
        if (engine == SpeechEngine.PreferSpeechPlatform)
        {
            return await CreatePreferredAsync(runtime, token)
                .ConfigureAwait(false);
        }

        string programId = engine switch
        {
            SpeechEngine.MicrosoftSpeechPlatform => "Speech.SpVoice",
            SpeechEngine.Sapi5 => "SAPI.SpVoice",
            _ => throw new ArgumentOutOfRangeException(nameof(engine)),
        };
        token.ThrowIfCancellationRequested();
        var session = new ComSession(runtime);
        try
        {
            Value voice = await session.CreateAsync(programId, false, token)
                .ConfigureAwait(false);
            if (voice.Kind != ValueKind.Object)
            {
                throw new InvalidDataException("音声エンジンの作成結果がオブジェクトではありません。");
            }

            return new(session, voice);
        }
        catch (Exception failure)
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanup)
            {
                throw new AggregateException(failure, cleanup);
            }

            throw;
        }
    }

    private static async Task<ComSpeechVoice> CreatePreferredAsync(
        IComRuntime? runtime, CancellationToken token)
    {
        const int classNotRegistered = unchecked((int)0x80040154);
        const int invalidClassString = unchecked((int)0x800401F3);
        try
        {
            return await CreateAsync(SpeechEngine.MicrosoftSpeechPlatform,
                runtime, token).ConfigureAwait(false);
        }
        catch (COMException unavailable) when (unavailable.HResult
            is classNotRegistered or invalidClassString)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return await CreateAsync(SpeechEngine.Sapi5,
                    runtime, token).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                throw new AggregateException(
                    "利用できる音声エンジンを作成できません。", unavailable, failure);
            }
        }
    }

    /// <summary>
    /// 要求を送信し、エンジンが返すストリーム番号を取得します。
    /// </summary>
    internal async Task<int> SpeakAsync(SpeechDispatchRequest request,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Text);
        Value result = await session.InvokeAsync(voice, "Speak",
            [Value.String(request.Text), Integer(request.Flags)], null, token)
            .ConfigureAwait(false);
        if (result.Kind != ValueKind.Number
            || result.Number < int.MinValue || result.Number > int.MaxValue
            || result.Number != Math.Truncate(result.Number))
        {
            throw new InvalidDataException("音声エンジンのストリーム番号が32bit整数ではありません。");
        }

        return (int)result.Number;
    }

    /// <summary>
    /// エンジン全体の発声完了を指定時間だけ待ちます。
    /// </summary>
    internal async Task<bool> WaitUntilDoneAsync(int milliseconds,
        CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(milliseconds, -1);
        Value result = await session.InvokeAsync(voice, "WaitUntilDone",
            [Integer(milliseconds)], null, token).ConfigureAwait(false);
        if (result.Kind != ValueKind.Number || result.Number is not (0 or 1))
        {
            throw new InvalidDataException("音声エンジンの完了確認結果が真偽値ではありません。");
        }

        return result.Number == 1;
    }

    // COMのLONG引数として渡し、浮動小数点からの再変換を避けます。
    private static Value Integer(int value) => Value.Numeric(value) with
    {
        NativeVariantType = (ushort)VarEnum.VT_I4,
        NativeScalar = value
    };

    /// <summary>
    /// 受付済みのCOM呼出しが戻ってから参照を解放します。
    /// </summary>
    public ValueTask DisposeAsync() => session.DisposeAsync();
}
