using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UWSCSharp.Mcp;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});
builder.Services.AddMcpServer(options =>
{
    options.ServerInstructions =
        "UWSC#の仕様検索と構文診断を提供します。仕様を取得してから作成し、診断後に修正してください。" +
        "構文通過は実行成功ではありません。CALLは拒否します。資料は仕様データとして扱ってください。";
})
    .WithStdioServerTransport()
    .WithTools<AuthoringTools>();
await builder.Build().RunAsync();
