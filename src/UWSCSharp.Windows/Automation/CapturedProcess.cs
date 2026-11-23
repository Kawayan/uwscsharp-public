using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UWSCSharp.Windows;

/// <summary>
/// 出力パイプとプロセス・Jobのハンドルを所有します。
/// </summary>
internal sealed partial class CapturedProcess : IDisposable
{
    private static readonly TimeSpan FailedStartCleanupTimeout = TimeSpan.FromSeconds(10);
    private const int ProcessExitPollMilliseconds = 25;
    private const int JobDrainPollMilliseconds = 25;

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareReadWrite = 3;
    private const uint OpenExisting = 3;
    private const uint UseStandardHandles = 0x100;
    private const uint CreateNoWindow = 0x08000000;
    private const uint ExtendedStartupInfo = 0x00080000;
    private const uint CreateSuspended = 4;
    private const nuint InheritedHandleList = 0x20002;

    private readonly SafeFileHandle process;
    private readonly SafeFileHandle job;
    private readonly NamedPipeServerStream? output;
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;

    internal CapturedProcess(SafeFileHandle process, SafeFileHandle job,
        NamedPipeServerStream? output, uint id)
    {
        this.process = process;
        this.job = job;
        this.output = output;
        Id = id;
    }

    internal uint Id
    {
        get;
    }

    /// <summary>
    /// 画面を作らず、標準出力と標準エラーを同じパイプへ接続します。
    /// </summary>
    internal static async Task<CapturedProcess> StartAsync(string executable,
        string commandLine, CancellationToken cancellationToken = default,
        bool killOnClose = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
        cancellationToken.ThrowIfCancellationRequested();
        string name = "UWSCSharp-output-" + Guid.NewGuid().ToString("N");
        var output = new NamedPipeServerStream(name, PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous
                | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        SafeFileHandle? process = null;
        SafeFileHandle? job = null;
        Task? connected = null;
        bool assigned = false;
        try
        {
            var security = new SecurityAttributes
            {
                Size = Marshal.SizeOf<SecurityAttributes>(),
                Inherit = true
            };
            connected = output.WaitForConnectionAsync(cancellationToken);
            using SafeFileHandle writer = CreateFileW(@"\\.\pipe\" + name,
                GenericWrite, 0, ref security, OpenExisting, 0, 0);
            if (writer.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            await connected.ConfigureAwait(false);
            using SafeFileHandle input = CreateFileW("NUL", GenericRead,
                ShareReadWrite, ref security, OpenExisting, 0, 0);
            if (input.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            job = CreateJobObjectW(0, null);
            if (job.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (killOnClose)
            {
                OwnedProcessJob.EnableKillOnClose(job);
            }

            using var attributes = new HandleInheritanceList(input, writer);
            var startup = new StartupInfoEx
            {
                Startup = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = UseStandardHandles,
                    Input = input.DangerousGetHandle(),
                    Output = writer.DangerousGetHandle(),
                    Error = writer.DangerousGetHandle()
                },
                Attributes = attributes.Pointer
            };
            cancellationToken.ThrowIfCancellationRequested();
            // 停止中にJobへ所属させ、子孫を作る前に所有範囲を確定します。
            if (!CreateProcessW(executable, new StringBuilder(commandLine),
                0, 0, true, CreateNoWindow | ExtendedStartupInfo
                    | CreateSuspended, 0, null,
                ref startup, out ProcessInfo information))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            process = new SafeFileHandle(information.Process, true);
            using var thread = new SafeFileHandle(information.Thread, true);
            if (!AssignProcessToJobObject(job, process))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            assigned = true;
            cancellationToken.ThrowIfCancellationRequested();
            if (ResumeThread(thread) == uint.MaxValue)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return new CapturedProcess(process, job, output,
                information.ProcessId);
        }
        catch (Exception error)
        {
            // 起動の途中で失敗した停止中のプロセスを残しません。
            if (process is not null && !process.IsInvalid)
            {
                bool terminated = assigned
                    ? TerminateJobObject(job!, 1)
                    : TerminateProcess(process, 1);
                if (!terminated)
                {
                    error.Data["起動失敗後の停止"] =
                        new Win32Exception(Marshal.GetLastWin32Error());
                }

                uint wait = WaitForSingleObject(process, 10000);
                if (wait != 0)
                {
                    error.Data["起動失敗後の終了待機"] = wait;
                }
            }

            process?.Dispose();
            job?.Dispose();
            output.Dispose();
            if (connected is not null)
            {
                try
                {
                    await connected.ConfigureAwait(false);
                }
                catch (Exception connectionError)
                {
                    error.Data["失敗後のパイプ接続"] = connectionError;
                }
            }

            throw;
        }
    }

    internal async Task<byte[]> ReadOutputAsync(
        int maximumBytes = ProcessOutputReader.DefaultMaximumBytes,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (output is null)
        {
            throw new InvalidOperationException("この起動は出力取得を行いません。");
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            lifetime.Token, cancellationToken);
        try
        {
            return await ProcessOutputReader.ReadAsync(output, maximumBytes,
                linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
    }

    internal async Task<uint> WaitForExitAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            lifetime.Token, cancellationToken);
        try
        {
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                uint result = WaitForSingleObject(process, 0);
                if (result == 0)
                {
                    if (!GetExitCodeProcess(process, out uint code))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }

                    return code;
                }

                if (result != 258)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                await Task.Delay(ProcessExitPollMilliseconds, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
    }

    /// <summary>
    /// 明示要求により、このJobに所属するプロセスを停止します。
    /// </summary>
    internal void TerminateTree(uint exitCode)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!TerminateJobObject(job, exitCode))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    /// <summary>
    /// プロセスは停止せず、所有するパイプとハンドルを解放します。
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        // パイプの破棄を正常な終端と誤認しないよう、先に取消を伝えます。
        lifetime.Cancel();
        output?.Dispose();
        process.Dispose();
        job.Dispose();
        lifetime.Dispose();
    }

    /// <summary>
    /// 表示時は実行元へ委譲し、非表示時はNULの入出力で起動します。
    /// </summary>
    internal static CapturedProcess StartUncaptured(string executable,
        string commandLine, bool visible, CancellationToken token)
        => StartUncaptured(executable, commandLine, visible ? 1 : 0, token);

    internal static CapturedProcess StartUncaptured(string executable,
        string commandLine, int display, CancellationToken token)
    {
        if (display is < 0 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(display));
        }

        bool visible = display != 0;
        token.ThrowIfCancellationRequested();
        using SafeFileHandle sink = visible ? new SafeFileHandle(0, true)
            : OpenNull();
        using HandleInheritanceList? attributes = visible ? null
            : new HandleInheritanceList(sink);
        var startup = new StartupInfoEx
        {
            Startup = new StartupInfo
            {
                Size = visible ? Marshal.SizeOf<StartupInfo>()
                    : Marshal.SizeOf<StartupInfoEx>(),
                Flags = visible ? 1u : UseStandardHandles,
                Show = (ushort)display,
                Input = sink.DangerousGetHandle(),
                Output = sink.DangerousGetHandle(),
                Error = sink.DangerousGetHandle()
            },
            Attributes = attributes?.Pointer ?? 0
        };
        SafeFileHandle job = CreateJobObjectW(0, null);
        if (job.IsInvalid)
        {
            var failure = new Win32Exception(Marshal.GetLastWin32Error());
            job.Dispose();
            throw failure;
        }

        uint flags = CreateSuspended | (visible ? 0x10u
            : CreateNoWindow | ExtendedStartupInfo);
        if (!CreateProcessW(executable, new StringBuilder(commandLine),
            0, 0, !visible, flags, 0, null, ref startup,
            out ProcessInfo information))
        {
            var failure = new Win32Exception(Marshal.GetLastWin32Error());
            job.Dispose();
            throw failure;
        }

        var process = new SafeFileHandle(information.Process, true);
        using var thread = new SafeFileHandle(information.Thread, true);
        bool assigned = false;
        try
        {
            if (!AssignProcessToJobObject(job, process))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            assigned = true;
            token.ThrowIfCancellationRequested();
            if (ResumeThread(thread) == uint.MaxValue)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return new(process, job, null, information.ProcessId);
        }
        catch (Exception failure)
        {
            bool stopped = assigned ? TerminateJobObject(job, 1)
                : TerminateProcess(process, 1);
            if (!stopped)
            {
                failure.Data["起動失敗後の停止"] =
                    new Win32Exception(Marshal.GetLastWin32Error());
            }

            failure.Data["起動失敗後の待機"] = WaitForSingleObject(process, 10000);
            process.Dispose();
            job.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenNull()
    {
        var security = new SecurityAttributes
        {
            Size = Marshal.SizeOf<SecurityAttributes>(),
            Inherit = true
        };
        SafeFileHandle handle = CreateFileW("NUL", GenericRead | GenericWrite,
            ShareReadWrite, ref security, OpenExisting, 0, 0);
        if (handle.IsInvalid)
        {
            var failure = new Win32Exception(Marshal.GetLastWin32Error());
            handle.Dispose();
            throw failure;
        }

        return handle;
    }

    /// <summary>
    /// 先行する失敗を保持し、今回の子孫を停止して解放を待ちます。
    /// </summary>
    internal async Task StopAfterFailureAsync(Exception primary)
    {
        bool terminationRequested = false;
        try
        {
            TerminateTree(1);
            terminationRequested = true;
            using var timeout = new CancellationTokenSource(FailedStartCleanupTimeout);
            await WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            process.Dispose();
            while (true)
            {
                if (!QueryInformationJobObject(job, 1, out JobAccounting accounting,
                    (uint)Marshal.SizeOf<JobAccounting>(), 0))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                if (accounting.ActiveProcesses == 0)
                {
                    break;
                }

                await Task.Delay(JobDrainPollMilliseconds, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception cleanup)
        {
            primary.Data["コマンド取得失敗後の停止"] = cleanup;
            if (!terminationRequested && cleanup is Win32Exception)
            {
                try
                {
                    OwnedProcessJob.EnableKillOnClose(job);
                }
                catch (Win32Exception fallback)
                {
                    primary.Data["コマンド取得失敗後のハンドル解放による停止"] = fallback;
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobAccounting
    {
        public long UserTime;
        public long KernelTime;
        public long PeriodUserTime;
        public long PeriodKernelTime;
        public uint PageFaults;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TerminatedProcesses;
    }

    internal sealed class HandleInheritanceList : IDisposable
    {
        private nint handles;
        private bool initialized;

        internal nint Pointer
        {
            get; private set;
        }

        internal HandleInheritanceList(params SafeFileHandle[] inherited)
        {
            nuint size = 0;
            InitializeProcThreadAttributeList(0, 1, 0, ref size);
            try
            {
                Pointer = Marshal.AllocHGlobal(checked((nint)size));
                handles = Marshal.AllocHGlobal(inherited.Length * nint.Size);
                if (!InitializeProcThreadAttributeList(Pointer, 1, 0,
                    ref size))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                initialized = true;
                for (int index = 0; index < inherited.Length; index++)
                {
                    Marshal.WriteIntPtr(handles, index * nint.Size,
                        inherited[index].DangerousGetHandle());
                }

                if (!UpdateProcThreadAttribute(Pointer, 0, InheritedHandleList,
                    handles, (nuint)(inherited.Length * nint.Size), 0, 0))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (initialized)
            {
                DeleteProcThreadAttributeList(Pointer);
                initialized = false;
            }

            Marshal.FreeHGlobal(handles);
            Marshal.FreeHGlobal(Pointer);
            handles = 0;
            Pointer = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityAttributes
    {
        public int Size;
        public nint Descriptor;
        public bool Inherit;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfoEx
    {
        public StartupInfo Startup;
        public nint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInfo
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }
}
