using System.Runtime.InteropServices;

namespace UWSCSharp.Wpf;

internal static class ScriptDropShortcutVerification
{
    // 所有する検証用リンクだけを作成し、リンク先は起動しません。
    internal static void CreateDropShortcut(string path,
        string target, string arguments, string workingDirectory)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!);
            dynamic factory = shell!;
            shortcut = factory.CreateShortcut(path);
            dynamic link = shortcut;
            link.TargetPath = target;
            link.Arguments = arguments;
            link.WorkingDirectory = workingDirectory;
            link.Save();
        }
        finally
        {
            if (shortcut is not null)
            {
                Marshal.FinalReleaseComObject(shortcut);
            }
            if (shell is not null)
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    internal const string ShortcutProbeSource = """
        LOGPRINT(FALSE)
        PRINT "NAME="+GET_UWSC_NAME
        PRINT "DIR="+GET_CUR_DIR
        PRINT "COUNT="+LENGTH(PARAM_STR)
        IF LENGTH(PARAM_STR)>0 THEN
            DIM index
            FOR index=0 TO LENGTH(PARAM_STR)-1
                PRINT "ARG["+index+"]=["+PARAM_STR[index]+"]"
            NEXT
        ENDIF
        """;
}
