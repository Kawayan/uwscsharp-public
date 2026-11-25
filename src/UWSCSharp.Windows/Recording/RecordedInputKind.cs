namespace UWSCSharp.Windows;

// 記録した入力の種類です。値は記録順の並べ替えや直列化で比較に使います。
internal enum RecordedInputKind
{
    // キーの押下・解放です。
    Key = 0,

    // ボタンを押していない間も含むマウス移動です。
    MouseMove = 1,

    // 座標付きのマウスボタンの押下・解放です。
    MouseButton = 2,

    // フック復旧時に補う、座標なしのボタン解放です。
    ButtonRelease = 3,

    // 縦横のホイール回転です。
    Wheel = 4,

    // ウインドウの移動・サイズ変更です。
    WindowMovement = 5,
}
