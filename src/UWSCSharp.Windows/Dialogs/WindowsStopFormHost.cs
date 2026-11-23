using System.Runtime.Versioning;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// CLIなどの実行が所有する停止画面と出力先をまとめます。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsStopFormHost : IScriptStopFormOptionsHost,
    IScriptWindowPositionHost, IAsyncDisposable
{
    private readonly object gate = new();
    private readonly IScriptHost inner;
    private readonly Action requestStop;
    private readonly string scriptName;
    private readonly (int Width, int Height)? size;
    private NativeDialogService? service;
    private bool disposed;
    private Task? disposal;
    private OptionsSession? activeOptions;

    /// <summary>
    /// 同じ実行で変更した最前面設定の復元先を保持します。
    /// </summary>
    public async ValueTask<IScriptStopFormOptionsSession> CreateStopFormOptionsSessionAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OptionsSession created;
        NativeDialogService? current;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (activeOptions is not null)
            {
                throw new InvalidOperationException("停止設定のセッションは既に使用中です。");
            }

            created = new OptionsSession(this);
            activeOptions = created;
            current = service;
        }

        try
        {
            created.PreviousTopMost = current is null
                || await current.GetStopTopMostAsync(cancellationToken).ConfigureAwait(false);
            return created;
        }
        catch
        {
            lock (gate)
            {
                if (ReferenceEquals(activeOptions, created))
                {
                    activeOptions = null;
                }
            }

            throw;
        }
    }

    private sealed class OptionsSession(WindowsStopFormHost owner) : IScriptStopFormOptionsSession
    {
        private readonly SemaphoreSlim serial = new(1, 1);
        internal bool PreviousTopMost = true;
        private bool topChanged;
        private bool closed;

        public async ValueTask ApplyAsync(ScriptStopFormOption option, CancellationToken cancellationToken)
        {
            await serial.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                NativeDialogService? current;
                lock (owner.gate)
                {
                    ObjectDisposedException.ThrowIf(closed || owner.disposed, this);
                    if (!ReferenceEquals(owner.activeOptions, this))
                    {
                        throw new InvalidOperationException("停止設定の実行元が一致しません。");
                    }

                    current = owner.service;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (option == ScriptStopFormOption.TopMost)
                {
                    topChanged = true;
                    // 画面がない場合の既定値も最前面なので、作成は必要になるまで遅らせます。
                    if (current is not null)
                    {
                        await current.SetStopTopMostAsync(true, cancellationToken).ConfigureAwait(false);
                    }
                }
                else if (option != ScriptStopFormOption.DisableStopHotkey)
                {
                    throw new ArgumentOutOfRangeException(nameof(option));
                }
                // CLIはグローバル停止キーを登録しないため、取消経路を変更しません。
            }
            finally
            {
                serial.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await serial.WaitAsync().ConfigureAwait(false);
            try
            {
                if (closed)
                {
                    return;
                }

                closed = true;
                NativeDialogService? current;
                lock (owner.gate)
                {
                    current = owner.disposed ? null : owner.service;
                }

                if (topChanged && current is not null)
                {
                    await current.SetStopTopMostAsync(PreviousTopMost, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                lock (owner.gate)
                {
                    if (ReferenceEquals(owner.activeOptions, this))
                    {
                        owner.activeOptions = null;
                    }
                }

                serial.Release();
            }
        }
    }

    /// <summary>
    /// 画面の作成を必要な時まで遅らせ、実行の取消先を保持します。
    /// </summary>
    public WindowsStopFormHost(IScriptHost inner, Action requestStop, string scriptName = "")
        : this(inner, requestStop, scriptName, null)
    {
    }

    /// <summary>
    /// 指定した物理外寸を保持し、画面作成時に操作可能な範囲へ補正します。
    /// </summary>
    public WindowsStopFormHost(IScriptHost inner, Action requestStop, string scriptName,
        (int Width, int Height)? size)
    {
        if (size is { } dimensions)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions.Width);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions.Height);
        }

        this.size = size;
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.requestStop = requestStop ?? throw new ArgumentNullException(nameof(requestStop));
        this.scriptName = scriptName ?? throw new ArgumentNullException(nameof(scriptName));
    }

    /// <summary>
    /// 必要なら非表示の停止画面を作成し、同じ実行のHWNDを返します。
    /// </summary>
    public async ValueTask<nint> GetWindowHandleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await GetService().GetStopFormHandleAsync(this, scriptName, requestStop, size,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 所有する停止画面へ表示と位置を反映します。
    /// </summary>
    public ValueTask SetStopFormAsync(ScriptStopForm request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return new(GetService().SetStopFormAsync(this, scriptName, requestStop, size, request, cancellationToken));
    }

    private NativeDialogService GetService()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return service ??= new NativeDialogService();
        }
    }

    /// <summary>
    /// 所有する停止画面だけを移動し、標準出力先は変更しません。
    /// </summary>
    public ValueTask SetWindowPositionAsync(ScriptWindowPosition request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return new(GetService().SetStopPositionAsync(this, scriptName,
            requestStop, size, request, cancellationToken));
    }

    /// <summary>
    /// 出力は実際の出力先へ転送します。
    /// </summary>
    public ValueTask WriteOutputAsync(string text, CancellationToken cancellationToken) =>
        inner.WriteOutputAsync(text, cancellationToken);

    /// <summary>
    /// 待機は実際の出力先へ転送します。
    /// </summary>
    public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken) =>
        inner.DelayAsync(duration, cancellationToken);

    /// <summary>
    /// 全要求の終了を待ち、画面とUIスレッドを解放します。
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            disposed = true;
            disposal ??= service?.DisposeAsync().AsTask() ?? Task.CompletedTask;
            return new(disposal);
        }
    }
}
