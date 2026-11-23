using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

internal sealed class VisualStylesContext : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Context
    {
        internal uint Size;
        internal uint Flags;
        internal string Source;
        internal ushort Architecture;
        internal ushort Language;
        internal nint Directory;
        internal nint Resource;
        internal nint Application;
        internal nint Module;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Controls
    {
        internal uint Size;
        internal uint Classes;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool Initialize(ref Controls controls);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateActCtxW(ref Context context);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ActivateActCtx(nint context, out nuint cookie);

    [DllImport("kernel32.dll")]
    private static extern bool DeactivateActCtx(uint flags, nuint cookie);

    [DllImport("kernel32.dll")]
    private static extern void ReleaseActCtx(nint context);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadLibraryW(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern nint GetProcAddress(nint module, string name);

    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(nint module);

    private readonly string path = Path.Combine(Path.GetTempPath(), $"uwsc-link-{Guid.NewGuid():N}.manifest");
    private nint context;
    private nint module;
    private nuint cookie;
    private bool active;

    internal VisualStylesContext()
    {
        try
        {
            File.WriteAllText(path, """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
                  <dependency><dependentAssembly>
                    <assemblyIdentity type="win32" name="Microsoft.Windows.Common-Controls"
                      version="6.0.0.0" processorArchitecture="*"
                      publicKeyToken="6595b64144ccf1df" language="*" />
                  </dependentAssembly></dependency>
                </assembly>
                """);
            var value = new Context { Size = (uint)Marshal.SizeOf<Context>(), Source = path };
            context = CreateActCtxW(ref value);
            Assert.NotEqual((nint)(-1), context);
            active = ActivateActCtx(context, out cookie);
            Assert.True(active);
            // 既に読み込まれたv5のP/Invoke bindingと分けてv6を初期化します。
            module = LoadLibraryW("comctl32.dll");
            Assert.NotEqual(0, module);
            nint address = GetProcAddress(module, "InitCommonControlsEx");
            Assert.NotEqual(0, address);
            Initialize initialize = Marshal.GetDelegateForFunctionPointer<Initialize>(address);
            var controls = new Controls { Size = 8, Classes = 0x8000 };
            Assert.True(initialize(ref controls));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (module != 0)
        {
            Assert.True(FreeLibrary(module));
            module = 0;
        }

        if (active)
        {
            Assert.True(DeactivateActCtx(0, cookie));
            active = false;
        }

        if (context != 0 && context != -1)
        {
            ReleaseActCtx(context);
            context = 0;
        }

        File.Delete(path);
    }
}
