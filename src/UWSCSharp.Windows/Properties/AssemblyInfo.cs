using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

// Win32 APIに依存するため、呼出し元へCA1416によるOS確認を要求します。
[assembly: SupportedOSPlatform("windows")]
[assembly: InternalsVisibleTo("UWSCSharp.Windows.Tests")]
[assembly: InternalsVisibleTo("UWSCSharp.RecordingSoak")]
[assembly: InternalsVisibleTo("UWSCSharp.ControlFixture")]
[assembly: InternalsVisibleTo("UWSCSharp.Wpf")]
[assembly: InternalsVisibleTo("UWSCSharp.Cli")]
[assembly: InternalsVisibleTo("DefDllAbiProbe")]
[assembly: InternalsVisibleTo("DefDllRelayProbe")]
[assembly: InternalsVisibleTo("UWSCSharp.DllRelay")]

[assembly: InternalsVisibleTo("FloatingPointControlProbe")]
