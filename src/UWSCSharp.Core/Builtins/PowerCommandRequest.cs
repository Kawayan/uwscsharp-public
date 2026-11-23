using System.Collections.Frozen;

namespace UWSCSharp.Core;

internal enum PowerCommand
{
    PowerOff = 16,
    LogOff = 128,
    Reboot = 64,
    Hibernate = 256,
    Standby = 512,
    MonitorLowPower = 1024,
    MonitorOff = 2048,
    MonitorOn = 4096,
    ScreenSaver = 8192,
    RestartApplication = 16384
}

/// <summary>
/// 電源操作に渡す前に、コマンドと修飾を検証します。
/// </summary>
internal readonly record struct PowerCommandRequest(
    PowerCommand Command, bool Force, bool RestartScript)
{
    internal const int ForceFlag = 8;

    private static readonly FrozenDictionary<string, int> Constants =
        new Dictionary<string, int>
        {
            ["P_POWEROFF"] = (int)PowerCommand.PowerOff,
            ["P_LOGOFF"] = (int)PowerCommand.LogOff,
            ["P_REBOOT"] = (int)PowerCommand.Reboot,
            ["P_SUSPEND"] = (int)PowerCommand.Hibernate,
            ["P_SUSPEND2"] = (int)PowerCommand.Standby,
            ["P_MONIPOWER"] = (int)PowerCommand.MonitorLowPower,
            ["P_MONIPOWER2"] = (int)PowerCommand.MonitorOff,
            ["P_MONIPOWER3"] = (int)PowerCommand.MonitorOn,
            ["P_SCREENSAVE"] = (int)PowerCommand.ScreenSaver,
            ["P_UWSC_REEXEC"] = (int)PowerCommand.RestartApplication,
            ["P_FORCE"] = ForceFlag
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    internal static bool TryGetConstant(string name, out int value) =>
        Constants.TryGetValue(name, out value);

    internal static PowerCommandRequest Read(IReadOnlyList<Value> arguments)
    {
        BuiltinFunctions.CheckArguments(
            BuiltinFunctionNames.Poff, arguments, 1, 2);
        var args = new CallArguments(arguments);
        int value = ReadInteger(args.Get(0));
        var command = (PowerCommand)(value & ~ForceFlag);
        bool force = (value & ForceFlag) != 0;
        if (!Enum.IsDefined(command)
            || (force && command is not (PowerCommand.PowerOff
                or PowerCommand.LogOff or PowerCommand.Reboot)))
        {
            throw Error("コマンドの指定が不正です。");
        }

        if (!args.Has(1))
        {
            return new(command, force, false);
        }

        if (command != PowerCommand.RestartApplication)
        {
            throw Error("第2引数は再起動コマンドにだけ指定できます。");
        }

        int restartScript = ReadInteger(args.Get(1));
        if (restartScript is not (0 or 1))
        {
            throw Error("再実行フラグにはTRUEまたはFALSEを指定してください。");
        }

        return new(command, force, restartScript == 1);
    }

    private static int ReadInteger(Value value)
    {
        if (value.Kind is not (ValueKind.Number or ValueKind.String))
        {
            throw Error("引数には数値または数値文字列を指定してください。");
        }

        double number = NumericOperations.RuntimeNumber(value);
        if (!double.IsFinite(number) || number != Math.Truncate(number)
            || number < 0 || number > int.MaxValue)
        {
            throw Error("引数には0以上の整数を指定してください。");
        }

        return (int)number;
    }

    private static InvalidOperationException Error(string message) =>
        new($"{BuiltinFunctionNames.Poff}: {message}");
}
