using System.Globalization;

namespace UWSCSharp.Core;
/// <summary>
/// Windows自動操作関数の定義と引数の検証を提供します。
/// </summary>
public static class WindowsFunctions
{
    /// <summary>
    /// 評価後の引数1個に要求する値の種類です。
    /// </summary>
    private enum ArgumentKind
    {
        /// <summary>
        /// 整数へ変換できる値です。
        /// </summary>
        Integer,

        /// <summary>
        /// 表示文字列へ変換できる値です。
        /// </summary>
        Text,

        /// <summary>
        /// doubleで正確に保持できるハンドル値です。
        /// </summary>
        Handle,

        /// <summary>
        /// BTNのwheel指定時は回転量、それ以外は整数です。
        /// </summary>
        WheelAmount
    }

    /// <summary>
    /// Windows自動操作関数1件の引数規則です。
    /// </summary>
    /// <param name="Min">呼出し式に必要な引数の数です。</param>
    /// <param name="Max">呼出し式に指定できる引数の数です。</param>
    private sealed record Definition(int Min, int Max)
    {
        /// <summary>
        /// 評価後の検証で必要な引数の数です。
        /// </summary>
        public int ValidationMin { get; init; } = Min;

        /// <summary>
        /// 呼出し式で省略できない先頭からの引数の数です。
        /// </summary>
        public int RequiredPositions { get; init; } = Min;

        /// <summary>
        /// 呼出し式のすべての位置で省略を拒否するかどうかです。
        /// </summary>
        public bool RejectsOmitted
        {
            get; init;
        }

        /// <summary>
        /// 引数全体を読み取って検証し、以降の検証を省略します。
        /// </summary>
        public Action<IReadOnlyList<Value>>? Read
        {
            get; init;
        }

        /// <summary>
        /// 式を評価する前に、関数固有の省略位置を検証します。
        /// </summary>
        public Action<IReadOnlyList<Expr>>? CheckSyntax
        {
            get; init;
        }

        /// <summary>
        /// 第1引数が空のときのメッセージです。{0}は関数名です。
        /// </summary>
        public string? MissingTargetMessage
        {
            get; init;
        }

        /// <summary>
        /// 位置ごとの値の種類です。範囲外の位置は整数です。
        /// </summary>
        public ArgumentKind[] Arguments { get; init; } = [];

        /// <summary>
        /// 値の種類の検証後に行う範囲と組合せの検証です。
        /// </summary>
        public Action<string, IReadOnlyList<Value>>? Check
        {
            get; init;
        }

        public ArgumentKind GetKind(int index) => index < Arguments.Length
            ? Arguments[index] : ArgumentKind.Integer;
    }

    private const string MissingWindowIdMessage = "{0}にはウィンドウIDを指定してください。";

    private static readonly Dictionary<string, Definition> Definitions = new(
        StringComparer.OrdinalIgnoreCase)
    {
        [BuiltinFunctionNames.Balloon] = new(0, 9)
        {
            Read = static arguments => _ = BalloonRequest.Read(arguments)
        },
        [BuiltinFunctionNames.Fukidasi] = new(0, 9)
        {
            Read = static arguments => _ = BalloonRequest.Read(arguments)
        },
        [BuiltinFunctionNames.SlctBox] = new(3, int.MaxValue)
        {
            ValidationMin = 2,
            RequiredPositions = 2
        },
        [BuiltinFunctionNames.PopupMenu] = new(1, 3),
        [BuiltinFunctionNames.Input] = new(1, 5)
        {
            Read = static arguments => _ = InputBoxRequest.Read(arguments)
        },
        [BuiltinFunctionNames.MsgBox] = new(1, 6)
        {
            Read = static arguments => _ = MessageBoxRequest.Read(arguments)
        },
        [BuiltinFunctionNames.CreateOleObj] = new(1, 1)
        {
            Read = ReadComName
        },
        [BuiltinFunctionNames.VarType] = new(1, 2)
        {
            Read = static arguments => _ = VariantTypeRequest.Read(arguments)
        },
        [BuiltinFunctionNames.GetActiveOleObj] = new(1, 3)
        {
            Read = ReadComName
        },
        [BuiltinFunctionNames.IeGetData] = new(2, 5)
        {
            Read = ReadHtmlTarget
        },
        [BuiltinFunctionNames.IeSetData] = new(3, 5)
        {
            Read = ReadHtmlTarget
        },
        [BuiltinFunctionNames.IeLink] = new(2, 4)
        {
            Read = ReadHtmlTarget
        },
        [BuiltinFunctionNames.IeGetSrc] = new(2, 3)
        {
            Read = ReadHtmlTarget
        },
        [BuiltinFunctionNames.IeSetSrc] = new(3, 4)
        {
            Read = ReadHtmlTarget
        },
        [BuiltinFunctionNames.IeGetFrame] = new(2, 2)
        {
            Read = ReadHtmlTarget
        },
        [BuiltinFunctionNames.GetOleItem] = new(1, 1)
        {
            Read = ReadComCollection
        },
        [BuiltinFunctionNames.SafeArray] = new(0, 5)
        {
            CheckSyntax = static arguments => SafeArrayRequest.ValidatePositions(
                arguments.Count, index => arguments[index] is OmittedExpr),
            Read = static arguments => _ = SafeArrayRequest.Read(arguments)
        },
        [BuiltinFunctionNames.GetId] = new(1, 4)
        {
            Arguments =
            [
                ArgumentKind.Text,
                ArgumentKind.Text,
                ArgumentKind.Integer,
                ArgumentKind.Text
            ],
            Check = CheckGetId
        },
        [BuiltinFunctionNames.GetAllWin] = new(0, 1),
        [BuiltinFunctionNames.GetCtlHnd] = new(2, 3)
        {
            Arguments = [ArgumentKind.Integer, ArgumentKind.Text]
        },
        [BuiltinFunctionNames.MusCur] = new(0, 0),
        [BuiltinFunctionNames.Monitor] = new(0, 2)
        {
            RejectsOmitted = true,
            Read = static arguments => _ = MonitorRequest.Read(arguments)
        },
        [BuiltinFunctionNames.KindOfOs] = new(0, 1)
        {
            Read = static arguments => _ = NumericOperations.RuntimeNumber(
                new CallArguments(arguments).Get(0))
        },
        [BuiltinFunctionNames.CpuUserate] = new(0, 0),
        [BuiltinFunctionNames.Sensor] = new(1, 1)
        {
            RejectsOmitted = true
        },
        [BuiltinFunctionNames.ScKey] = new(2, int.MaxValue)
        {
            RejectsOmitted = true,
            Read = static arguments => _ = ShortcutKeyArguments.Read(arguments)
        },
        [BuiltinFunctionNames.GetKeyState] = new(1, 1)
        {
            Read = static arguments => _ = KeyStateRequest.Read(arguments[0])
        },
        [BuiltinFunctionNames.Exec] = new(1, 6)
        {
            Read = static arguments => _ = ExecArguments.Read(arguments)
        },
        [BuiltinFunctionNames.DosCmd] = new(1, 4)
        {
            Read = static arguments => _ = DosCommandRequest.Read(arguments)
        },
        [BuiltinFunctionNames.PowerShell] = new(1, 3)
        {
            Read = static arguments => _ = PowerShellRequest.Read(arguments)
        },
        [BuiltinFunctionNames.DropFile] = new(3, 36)
        {
            Read = static arguments => _ = DropFileRequest.Read(arguments)
        },
        [BuiltinFunctionNames.LockHard] = new(0, 1)
        {
            Read = static arguments => _ = InputLockRequest.Read(arguments)
        },
        [BuiltinFunctionNames.LockHardEx] = new(0, 2)
        {
            // 引数を指定する場合は、第1引数を省略できません。
            RequiredPositions = 1,
            Read = static arguments => _ = SelectiveInputLockRequest.Read(arguments)
        },
        [BuiltinFunctionNames.IdToHnd] = new(1, 1)
        {
            MissingTargetMessage = "IDTOHNDにはウィンドウIDを指定してください。"
        },
        [BuiltinFunctionNames.HndToId] = new(1, 1)
        {
            MissingTargetMessage = "HNDTOIDにはウィンドウハンドルを指定してください。",
            Arguments = [ArgumentKind.Handle]
        },
        [BuiltinFunctionNames.Status] = new(2, 2)
        {
            MissingTargetMessage = "STATUSにはウィンドウIDを指定してください。",
            Check = CheckStatus
        },
        [BuiltinFunctionNames.CtrlWin] = new(2, 4)
        {
            Read = static arguments => _ = WindowControlArguments.Read(arguments)
        },
        [BuiltinFunctionNames.SendStr] = new(2, 5)
        {
            Arguments = [ArgumentKind.Integer, ArgumentKind.Text],
            Check = CheckSendStr
        },
        [BuiltinFunctionNames.MouseOrg] = new(1, 3)
        {
            Arguments = [ArgumentKind.Handle],
            Check = CheckMouseOrg
        },
        [BuiltinFunctionNames.Mmv] = new(2, 3)
        {
            Check = CheckMmv
        },
        [BuiltinFunctionNames.Btn] = new(1, 5)
        {
            Arguments = [ArgumentKind.Integer, ArgumentKind.WheelAmount],
            Check = CheckBtn
        },
        [BuiltinFunctionNames.Kbd] = new(1, 3)
        {
            Check = CheckKbd
        },
        [BuiltinFunctionNames.Acw] = new(1, 6)
        {
            MissingTargetMessage = "ACWにはウィンドウIDを指定してください。",
            Check = CheckAcw
        },
        [BuiltinFunctionNames.ClkItem] = new(2, 5)
        {
            Arguments = [ArgumentKind.Integer, ArgumentKind.Text],
            Check = CheckClkItem
        },
        [BuiltinFunctionNames.GetStr] = new(1, 4)
        {
            Check = CheckGetStr
        },
        [BuiltinFunctionNames.ChkBtn] = new(2, 4)
        {
            Arguments = [ArgumentKind.Integer, ArgumentKind.Text]
        },
        [BuiltinFunctionNames.GetItem] = new(2, 6)
        {
            Check = CheckGetItem
        },
        [BuiltinFunctionNames.GetSlctLst] = new(1, 3),
        [BuiltinFunctionNames.GetSlider] = new(1, 3)
        {
            MissingTargetMessage = MissingWindowIdMessage,
            Check = CheckGetSlider
        },
        [BuiltinFunctionNames.SetSlider] = new(2, 3)
        {
            MissingTargetMessage = MissingWindowIdMessage
        },
        [BuiltinFunctionNames.PeekColor] = new(2, 4)
        {
            Check = CheckPeekColor
        },
        [BuiltinFunctionNames.ChkImg] = new(0, 8)
        {
            Arguments = [ArgumentKind.Text],
            Check = CheckChkImg
        },
        [BuiltinFunctionNames.SaveImg] = new(0, 9)
        {
            Arguments = [ArgumentKind.Text],
            Check = CheckSaveImg
        },
        [BuiltinFunctionNames.PosAcc] = new(3, 4)
        {
            Check = CheckPosAcc
        }
    };

    /// <summary>
    /// 組み込み関数として定義されているかどうかを判定します。
    /// </summary>
    public static bool IsBuiltin(
        string name) => Definitions.ContainsKey(name);

    /// <summary>
    /// 評価前の呼出し式について、引数の数と省略位置を検証します。
    /// </summary>
    internal static void CheckCall(string name, IReadOnlyList<Expr> arguments)
    {
        Definition definition = Definitions[name];
        if (arguments.Count < definition.Min || arguments.Count > definition.Max
            || arguments.Take(definition.RequiredPositions)
                .Any(argument => argument is OmittedExpr)
            || (definition.RejectsOmitted
                && arguments.Any(argument => argument is OmittedExpr)))
        {
            throw new InvalidOperationException(
                $"引数の数または省略位置が不正です: {name}");
        }

        definition.CheckSyntax?.Invoke(arguments);
    }

    /// <summary>
    /// 指定された名前と引数の妥当性を検証します。
    /// </summary>
    public static void Validate(string name,
        IReadOnlyList<Value> arguments) => Validate(name, arguments, default);

    internal static void Validate(string name,
        IReadOnlyList<Value> arguments, CancellationToken token)
    {
        Definition definition = Definitions[name];
        BuiltinFunctions.CheckArguments(name, arguments,
            definition.ValidationMin, definition.Max);
        if (name.Equals(BuiltinFunctionNames.SlctBox,
            StringComparison.OrdinalIgnoreCase))
        {
            _ = SelectionBoxRequest.Read(arguments, token);
            return;
        }

        if (name.Equals(BuiltinFunctionNames.PopupMenu,
            StringComparison.OrdinalIgnoreCase))
        {
            _ = PopupMenuRequest.Read(arguments, token);
            return;
        }

        if (definition.Read is not null)
        {
            definition.Read(arguments);
            return;
        }

        if (definition.MissingTargetMessage is not null
            && arguments[0].Kind == ValueKind.Empty)
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture, definition.MissingTargetMessage, name));
        }

        for (int i = 0; i < arguments.Count; i++)
        {
            if (arguments[i].Kind != ValueKind.Omitted)
            {
                ValidateArgument(definition.GetKind(i), arguments, i);
            }
        }

        definition.Check?.Invoke(name, arguments);
    }

    private static void ValidateArgument(ArgumentKind kind,
        IReadOnlyList<Value> arguments, int index)
    {
        switch (kind)
        {
            case ArgumentKind.Handle:
                double handle = NumericOperations.Number(arguments[index]);
                // doubleで整数を正確に保持できる範囲に制限します。
                if (!double.IsFinite(handle)
                    || Math.Abs(handle) > 9007199254740991d)
                {
                    throw new InvalidOperationException(
                        "ハンドル値が正確に表現できる範囲を超えています。");
                }

                return;
            case ArgumentKind.WheelAmount
                when NumericOperations.Integer(arguments[0]) is 5 or 6:
                double delta = NumericOperations.Number(arguments[index]) * 120;
                if (delta < int.MinValue || delta > int.MaxValue)
                {
                    throw new InvalidOperationException("wheelの回転量が範囲外です。");
                }

                return;
            case ArgumentKind.Text:
                _ = arguments[index].Display();
                return;
            default:
                _ = NumericOperations.Integer(arguments[index]);
                return;
        }
    }

    private static int Int(IReadOnlyList<Value> arguments, int index,
        int fallback = 0) => index < arguments.Count
            && arguments[index].Kind != ValueKind.Omitted
            ? NumericOperations.Integer(arguments[index]) : fallback;

    private static void Range(string name, IReadOnlyList<Value> arguments,
        int index, int min, int max)
    {
        if (Int(arguments, index) < min || Int(arguments, index) > max)
        {
            throw new InvalidOperationException(
                $"{name}の引数{index + 1}が範囲外です。");
        }
    }

    private static void ReadComName(IReadOnlyList<Value> arguments)
    {
        if (arguments[0].Kind != ValueKind.String || arguments[0].Text.Length == 0)
        {
            throw new InvalidOperationException("COM名には空でない文字列を指定してください。");
        }

        if (arguments.Skip(1).Any(value => value.Kind != ValueKind.Omitted))
        {
            throw new InvalidOperationException("タイトルと順番によるCOM取得には未対応です。");
        }
    }

    private static void ReadHtmlTarget(IReadOnlyList<Value> arguments)
    {
        if (arguments[0].Kind != ValueKind.Object)
        {
            throw new InvalidOperationException("COMオブジェクトを指定してください。");
        }
    }

    private static void ReadComCollection(IReadOnlyList<Value> arguments)
    {
        if (arguments[0].Kind is not (ValueKind.Object or ValueKind.Array))
        {
            throw new InvalidOperationException("COMコレクションを指定してください。");
        }
    }

    private static void CheckClkItem(string name, IReadOnlyList<Value> arguments)
    {
        var kind = (ClickItemFlags)Int(arguments, 2, 1);
        ClickItemFlags types = kind & ClickItemFlags.Types;
        ClickItemFlags routes = kind & (ClickItemFlags.Accessible
            | ClickItemFlags.Uia);
        const ClickItemFlags allowed = ClickItemFlags.Types
            | ClickItemFlags.Partial | ClickItemFlags.Background
            | ClickItemFlags.MouseMove | ClickItemFlags.LeftClick
            | ClickItemFlags.RightClick | ClickItemFlags.DoubleClick
            | ClickItemFlags.OfThis | ClickItemFlags.Accessible
            | ClickItemFlags.Api | ClickItemFlags.Uia
            | ClickItemFlags.FromLast | ClickItemFlags.ExtendedAccessible
            | ClickItemFlags.Continue;
        if ((kind & ~allowed) != 0)
        {
            throw new InvalidOperationException(
                "CLKITEMの種別または番号指定は未対応です。");
        }

        // 種別省略時はadapterが複数経路を探索するため、数値要求を広げません。
        if (((ClickItemFlags)Int(arguments, 2) & ClickItemFlags.Types)
                != ClickItemFlags.Button && routes == 0)
        {
            Range(name, arguments, 3, 0, 1);
        }

        if ((kind & ClickItemFlags.OfThis) != 0
            && (types != ClickItemFlags.List
                || (routes & ClickItemFlags.Accessible) != 0))
        {
            throw new InvalidOperationException(
                "CLK_OFTHISはLISTの名前照合に対応しています。");
        }

        if (routes != 0
            && ((kind & ClickItemFlags.Api) != 0
                || routes == (ClickItemFlags.Accessible | ClickItemFlags.Uia)))
        {
            throw new InvalidOperationException("CLK_ACC・CLK_UIA・CLK_APIは同時に指定できません。");
        }

        if (((kind & (ClickItemFlags.FromLast | ClickItemFlags.ExtendedAccessible)) != 0
                && (kind & ClickItemFlags.Accessible) == 0)
            || ((kind & ClickItemFlags.Link) != 0
                && (kind & ClickItemFlags.Api) != 0))
        {
            throw new InvalidOperationException("CLK_FROMLAST・CLK_ACC2はMSAA操作、CLK_LINKはUIA操作に対応しています。");
        }
    }

    private static void CheckPosAcc(string name, IReadOnlyList<Value> arguments)
    {
        if (Int(arguments, 0) < 0 || (Int(arguments, 3) & ~0x20f) != 0
            || (Int(arguments, 3) & 15) is 2 or > 8)
        {
            throw new InvalidOperationException(
                "POSACCはID 0または正のIDとACC経路に対応しています。API trapは未対応です。");
        }
    }

    private static void CheckPeekColor(string name, IReadOnlyList<Value> arguments)
    {
        if (Int(arguments, 2) is not (0 or 3 or 4 or 5 or 6))
        {
            throw new InvalidOperationException(
                "PEEKCOLORの色指定が不正です。");
        }
    }

    private static void CheckChkImg(string name, IReadOnlyList<Value> arguments)
    {
        Range(name, arguments, 1, -1, 4);
        Range(name, arguments, 7, 0, 0xffffff);
    }

    private static void CheckSaveImg(string name, IReadOnlyList<Value> arguments)
    {
        Range(name, arguments, 1, 0, int.MaxValue);
        Range(name, arguments, 4, 0, int.MaxValue);
        Range(name, arguments, 5, 0, int.MaxValue);
        Range(name, arguments, 6, 0, 1);
        int quality = Int(arguments, 7);
        if (quality > 100 || (quality < 0
            && quality is not (-1 or -4 or -8 or -16 or -24 or -32)))
        {
            throw new InvalidOperationException(
                "SAVEIMGの画像形式指定が不正です。");
        }

        Range(name, arguments, 8, 0, 2);
    }

    private static void CheckGetSlider(string name, IReadOnlyList<Value> arguments)
    {
        if (Int(arguments, 2) < 0 || Int(arguments, 2) > 8)
        {
            throw new InvalidOperationException(
                "GETSLIDERはSLD_POS～SLD_Y2に対応しています。");
        }
    }

    private static void CheckGetItem(string name, IReadOnlyList<Value> arguments)
    {
        var kinds = (ItemKinds)Int(arguments, 1);
        bool accessibleItems = (kinds & ItemKinds.AccessibleTypes) != 0;
        const ItemKinds allowed = ItemKinds.Button
            | ItemKinds.List
            | ItemKinds.Menu
            | ItemKinds.Tab
            | ItemKinds.TreeView
            | ItemKinds.ListView
            | ItemKinds.Toolbar
            | ItemKinds.Link
            | ItemKinds.Edit
            | ItemKinds.Static
            | ItemKinds.StatusBar
            | ItemKinds.AccessibleTypes
            | ItemKinds.FromLast
            | ItemKinds.Background
            | ItemKinds.ExtendedAccessible;
        const ItemKinds accessibleOnly = ItemKinds.FromLast
            | ItemKinds.Background
            | ItemKinds.ExtendedAccessible;
        if (Int(arguments, 1) == 0
            || (kinds & ~allowed) != 0
            || (!accessibleItems && (kinds & accessibleOnly) != 0)
            || (accessibleItems
                && (Int(arguments, 2, 1) < -1 || Int(arguments, 2, 1) == 0))
            || Int(arguments, 3) < -1
            || ((kinds & ItemKinds.ListView) == 0 && Int(arguments, 3) != 0)
            || Int(arguments, 5) < 0
            || (!accessibleItems && Int(arguments, 5) != 0))
        {
            throw new InvalidOperationException(
                "GETITEMはnative各種・LINK・ACCCLK/ACCTXT/ACCEDITに対応しています。"
                + "native単独経路は番号0を1へ補正し、負番号は全取得です。"
                + "ACCを含む場合は正番号または-1に対応しています。"
                + "列はLISTVIEW、最大取得数・FROMLAST・BACKはACC各種のみ対応しています。");
        }

        Range(name, arguments, 4, 0, 1);
    }

    private static void CheckGetStr(string name, IReadOnlyList<Value> arguments)
    {
        Range(name, arguments, 2, 0, 5);
        if (Int(arguments, 2) >= 3 && Int(arguments, 1) == int.MinValue)
        {
            throw new InvalidOperationException("GETSTRのACC番号が範囲外です。");
        }

        Range(name, arguments, 3, 0, 1);
    }

    private static void CheckGetId(string name, IReadOnlyList<Value> arguments)
    {
        if (arguments.Count > 2
            && arguments[2].Kind != ValueKind.Omitted
            && arguments[2].AsNumber() < 0 && arguments[2].AsNumber() != -1)
        {
            throw new InvalidOperationException(
                "GETIDの待ち時間が範囲外です。");
        }
    }

    private static void CheckStatus(string name, IReadOnlyList<Value> arguments) =>
        Range(name, arguments, 1, 1, 21);

    private static void CheckSendStr(string name, IReadOnlyList<Value> arguments)
    {
        Range(name, arguments, 3, 0, 2);
        if (Int(arguments, 4) is not (0 or 1 or 2 or 5))
        {
            throw new InvalidOperationException(
                "SENDSTRのACC指定が範囲外です。");
        }
    }

    private static void CheckMouseOrg(string name, IReadOnlyList<Value> arguments)
    {
        Range(name, arguments, 1, 0, 2);
        Range(name, arguments, 2, 0, 2);
    }

    private static void CheckKbd(string name, IReadOnlyList<Value> arguments)
    {
        Range(name, arguments, 0, 1, 65535);
        Range(name, arguments, 1, 0, 2);
        Range(name, arguments, 2, 0, int.MaxValue);
    }

    private static void CheckBtn(string name, IReadOnlyList<Value> arguments)
    {
        if (Int(arguments, 0) is < 0 or > MouseButtonCodes.Touch)
        {
            throw new InvalidOperationException(
                "未対応のマウスボタンです。");
        }

        if (!MouseButtonCodes.IsWheel(Int(arguments, 0)))
        {
            Range(name, arguments, 1, 0, 2);
        }

        if ((arguments.Count > 2
            && arguments[2].Kind != ValueKind.Omitted)
            != (arguments.Count > 3 && arguments[3].Kind
            != ValueKind.Omitted))
        {
            throw new InvalidOperationException(
                "BTNの座標はxとyの両方を指定してください。");
        }

        Range(name, arguments, 4, 0, int.MaxValue);
    }

    private static void CheckMmv(string name, IReadOnlyList<Value> arguments) =>
        Range(name, arguments, 2, 0, int.MaxValue);

    private static void CheckAcw(string name, IReadOnlyList<Value> arguments)
    {
        Range(name, arguments, 3, 0, int.MaxValue);
        Range(name, arguments, 4, 0, int.MaxValue);
        Range(name, arguments, 5, 0, int.MaxValue);
    }
}
