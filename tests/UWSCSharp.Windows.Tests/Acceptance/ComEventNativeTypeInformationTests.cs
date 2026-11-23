using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Xunit;
using ITestOutputHelper = Xunit.Abstractions.ITestOutputHelper;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class ComEventNativeTypeInformationTests(ITestOutputHelper output)
{
    [Fact]
    public void ObserveNativeInheritanceAndDispatchDescriptions()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string path = Path.Combine(AppContext.BaseDirectory,
            "com-event-native-types", "x64", "com-event-native-types.tlb");
        Assert.True(File.Exists(path),
            "scripts/build_com_event_type_library.ps1で型ライブラリを生成してください。");
        Marshal.ThrowExceptionForHR(LoadTypeLibEx(path, 2, out ITypeLib library));
        try
        {
            int count = library.GetTypeInfoCount();
            Assert.Equal(9, count);
            var descriptions = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int index = 0; index < count; index++)
            {
                library.GetTypeInfo(index, out ITypeInfo info);
                try
                {
                    string description = Describe(info);
                    output.WriteLine(description);
                    descriptions.Add(description.Split('|')[0], description);
                }
                finally
                {
                    Marshal.ReleaseComObject(info);
                }
            }

            Assert.Contains("|base:IEventBase", descriptions["IEventDerived"]);
            string[] dispatchNames =
            [
                "IDirectEvents", "IFlattenedEvents", "IDualEventDerived"
            ];
            foreach (string name in dispatchNames)
            {
                Assert.Contains("|TKIND_DISPATCH|", descriptions[name]);
                Assert.Contains("|1:Changed:INVOKE_FUNC", descriptions[name]);
                Assert.Contains("|2:Other:INVOKE_FUNC", descriptions[name]);
                Assert.EndsWith("|base:IDispatch", descriptions[name]);
            }

            Assert.Contains("TYPEFLAG_FDUAL", descriptions["IDualEventDerived"]);
        }
        finally
        {
            Marshal.ReleaseComObject(library);
        }
    }

    private static string Describe(ITypeInfo info)
    {
        info.GetDocumentation(-1, out string name, out _, out _, out _);
        info.GetTypeAttr(out nint pointer);
        TYPEATTR attribute;
        try
        {
            attribute = Marshal.PtrToStructure<TYPEATTR>(pointer);
        }
        finally
        {
            info.ReleaseTypeAttr(pointer);
        }

        List<string> parts = [name, attribute.typekind.ToString(),
            attribute.wTypeFlags.ToString(), attribute.cFuncs.ToString(),
            attribute.cImplTypes.ToString()];
        for (int index = 0; index < attribute.cFuncs; index++)
        {
            info.GetFuncDesc(index, out nint description);
            try
            {
                FUNCDESC function = Marshal.PtrToStructure<FUNCDESC>(description);
                info.GetDocumentation(function.memid, out string member,
                    out _, out _, out _);
                parts.Add($"{function.memid}:{member}:{function.invkind}");
            }
            finally
            {
                info.ReleaseFuncDesc(description);
            }
        }

        for (int index = 0; index < attribute.cImplTypes; index++)
        {
            info.GetRefTypeOfImplType(index, out int reference);
            info.GetRefTypeInfo(reference, out ITypeInfo inherited);
            try
            {
                inherited.GetDocumentation(-1, out string inheritedName,
                    out _, out _, out _);
                parts.Add($"base:{inheritedName}");
            }
            finally
            {
                Marshal.ReleaseComObject(inherited);
            }
        }

        return string.Join("|", parts);
    }

    [DllImport("oleaut32.dll", CharSet = CharSet.Unicode)]
    private static extern int LoadTypeLibEx(string file, int registration,
        out ITypeLib library);
}
