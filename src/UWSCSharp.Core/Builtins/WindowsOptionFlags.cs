namespace UWSCSharp.Core;

/// <summary>
/// CLKITEMの種別と操作方法です。
/// </summary>
[Flags]
internal enum ClickItemFlags
{
    Button = 1,
    List = 2,
    Menu = 4,
    Tab = 8,
    TreeView = 16,
    ListView = 32,
    Toolbar = 64,
    Link = 128,
    Partial = 256,
    Background = 512,
    MouseMove = 1024,
    LeftClick = 2048,
    RightClick = 4096,
    DoubleClick = 8192,
    OfThis = 16384,
    Accessible = 32768,
    FromLast = 65536,
    Continue = 33554432,
    ExtendedAccessible = 268435456,
    Api = 536870912,
    Uia = 1073741824,
    Types = Button | List | Menu | Tab | TreeView
        | ListView | Toolbar | Link,
    NativeTypes = Types & ~Link,
    Accessible2 = Accessible | ExtendedAccessible
}

/// <summary>
/// GETITEMの取得対象と追加指定です。
/// </summary>
[Flags]
internal enum ItemKinds
{
    Button = 1,
    List = 2,
    Menu = 4,
    Tab = 8,
    TreeView = 16,
    ListView = 32,
    Toolbar = 64,
    Link = 128,
    Background = 512,
    FromLast = 65536,
    Edit = 131072,
    Static = 262144,
    StatusBar = 524288,
    AccessibleClick = 4194304,
    AccessibleText = 8388608,
    AccessibleEdit = 16777216,
    ExtendedAccessible = 268435456,
    AccessibleTypes = AccessibleClick | AccessibleText | AccessibleEdit,
    AccessibleClick2 = AccessibleClick | ExtendedAccessible,
    AccessibleText2 = AccessibleText | ExtendedAccessible
}

/// <summary>
/// STATUSが返す情報の項目です。
/// </summary>
internal enum WindowStatusItem
{
    X = 1,
    Y = 2,
    Width = 3,
    Height = 4,
    ClientX = 5,
    ClientY = 6,
    ClientWidth = 7,
    ClientHeight = 8,
    Title = 9,
    Iconic = 10,
    Maximized = 11,
    Visible = 12,
    Active = 13,
    Class = 14,
    Busy = 15,
    Parent = 16,
    Path = 17,
    Process = 18,
    Win64 = 19,
    Monitor = 20,
    IsId = 21,
    Java = 22,
}
