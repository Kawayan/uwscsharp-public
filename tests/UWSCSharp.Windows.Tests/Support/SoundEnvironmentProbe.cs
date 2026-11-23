using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace UWSCSharp.Windows.Tests;

/// <summary>
/// 音声照会を専用Jobへ隔離し、待機したAPIの開始通知と期限を記録します。
/// </summary>
internal static class SoundEnvironmentProbe
{
    internal static async Task<object> ReadAsync()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        const string executable = @"C:\ui-tools\pwsh\pwsh.exe";
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(Script));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timer = Stopwatch.StartNew();
        CapturedProcess? process = null;
        Exception? primary = null;
        bool stopAttempted = false;
        try
        {
            process = await CapturedProcess.StartAsync(executable,
                $"\"{executable}\" -NoLogo -NoProfile -NonInteractive -EncodedCommand {encoded}", timeout.Token);
            Task<byte[]> reading = process.ReadOutputAsync();
            uint? exit = null;
            bool timedOut = false;
            try
            {
                exit = await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                timedOut = true;
                var cleanup = new InvalidOperationException("音声照会の期限後の停止");
                stopAttempted = true;
                await process.StopAfterFailureAsync(cleanup);
                if (cleanup.Data.Count != 0)
                {
                    throw cleanup;
                }
            }

            string text = new UTF8Encoding(false, true).GetString(
                await reading.WaitAsync(TimeSpan.FromSeconds(5)));
            JsonElement? data = null;
            if (!timedOut)
            {
                Assert.True(exit == 0, text);
                string result = text.Split('\n').Single(line => line.StartsWith("RESULT=", StringComparison.Ordinal));
                using var document = JsonDocument.Parse(result[7..]);
                data = document.RootElement.Clone();
            }

            return new
            {
                status = timedOut ? "TimedOut" : "Completed",
                exit_code = exit,
                elapsed_ms = timer.Elapsed.TotalMilliseconds,
                data,
                stdout = text,
                job_cleanup = "active=0"
            };
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            if (process is not null)
            {
                Exception cleanup = primary ?? new InvalidOperationException("音声照会プロセスの解放");
                if (!stopAttempted)
                {
                    await process.StopAfterFailureAsync(cleanup);
                }

                process.Dispose();
                if (primary is null && cleanup.Data.Count != 0)
                {
                    throw cleanup;
                }
            }
        }
    }

    private const string Script = """
        $ErrorActionPreference='Stop'
        if([Environment]::UserName -ne 'WDAGUtilityAccount'){throw 'Sandbox is required'}
        [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
        Add-Type -TypeDefinition @'
        using System;
        using System.Runtime.InteropServices;
        public static class SoundInventory
        {
            [StructLayout(LayoutKind.Sequential, Pack = 2)]
            public struct WaveFormat
            {
                public ushort Tag;
                public ushort Channels;
                public uint SamplesPerSecond;
                public uint BytesPerSecond;
                public ushort BlockAlignment;
                public ushort BitsPerSample;
                public ushort ExtraBytes;
            }
            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            public struct Caps
            {
                public ushort Manufacturer;
                public ushort Product;
                public uint DriverVersion;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
                public string Name;
                public uint Formats;
                public ushort Channels;
                public ushort Reserved;
                public uint Support;
            }
            public static uint Query(uint device)
            {
                WaveFormat format = new WaveFormat
                {
                    Tag = 1, Channels = 1, SamplesPerSecond = 22050,
                    BytesPerSecond = 44100, BlockAlignment = 2,
                    BitsPerSample = 16, ExtraBytes = 0
                };
                return waveOutOpen(IntPtr.Zero, device, ref format,
                    UIntPtr.Zero, UIntPtr.Zero, 1);
            }
            [DllImport("winmm.dll")]
            public static extern uint waveOutGetNumDevs();
            [DllImport("winmm.dll")]
            public static extern uint midiOutGetNumDevs();
            [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
            public static extern uint waveOutGetDevCapsW(UIntPtr device,
                out Caps caps, uint size);
            [DllImport("winmm.dll")]
            private static extern uint waveOutOpen(IntPtr handle, uint device,
                ref WaveFormat format, UIntPtr callback, UIntPtr instance, uint flags);
        }
        '@
        function Mark([string]$value){[Console]::WriteLine('PHASE='+$value);[Console]::Out.Flush()}
        Mark 'waveOutGetNumDevs'
        $count=[SoundInventory]::waveOutGetNumDevs()
        [Console]::WriteLine('COUNT='+$count)
        $devices=[Collections.Generic.List[object]]::new()
        for([uint32]$index=0;$index -lt $count;$index++){
            $caps=[SoundInventory+Caps]::new()
            Mark ('waveOutGetDevCapsW '+$index)
            $result=[SoundInventory]::waveOutGetDevCapsW([UIntPtr]::new($index),[ref]$caps,[Runtime.InteropServices.Marshal]::SizeOf($caps))
            Mark ('waveOutOpen QUERY '+$index)
            $query=[SoundInventory]::Query($index)
            $devices.Add([ordered]@{index=$index;result=$result;name=$caps.Name;channels=$caps.Channels;formats=$caps.Formats;support=$caps.Support;pcm_query_result=$query})
        }
        Mark 'midiOutGetNumDevs'
        $midi=[SoundInventory]::midiOutGetNumDevs()
        Mark 'waveOutOpen QUERY mapper'
        $mapper=[SoundInventory]::Query([uint32]::MaxValue)
        [Console]::WriteLine('RESULT='+([ordered]@{output_devices=$count;midi_output_devices=$midi;mapper_pcm_query_result=$mapper;devices=$devices;process_bits=[IntPtr]::Size*8}|ConvertTo-Json -Depth 5 -Compress))
        """;
}
