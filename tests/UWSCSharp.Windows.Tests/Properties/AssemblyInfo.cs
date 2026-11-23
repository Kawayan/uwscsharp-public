using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Xunit;

// Win32 APIと実ウィンドウを使うWindows専用のテストです。
[assembly: SupportedOSPlatform("windows")]

[assembly: InternalsVisibleTo("LinkNotificationProbe")]

// desktop状態とnative workerの待機枠を共有するため、テスト間を直列化します。
// 各テスト内部で明示的に起動する並行処理は、そのまま検証します。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
