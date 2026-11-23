// DROPFILEの比較専用に、受信内容をUTF16コード単位で保存します。
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>
#include <cstdint>

static std::filesystem::path Destination;
static unsigned int Sequence = 0;

static void Publish(const std::filesystem::path& path, const std::string& text)
{
    std::filesystem::path pending = path;
    pending += L".pending";
    {
        std::ofstream stream(pending, std::ios::binary);
        stream.exceptions(std::ios::failbit | std::ios::badbit);
        stream << text;
    }
    std::filesystem::rename(pending, path);
}

static LRESULT CALLBACK Dispatch(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
{
    if (message == WM_DROPFILES)
    {
        HDROP drop = reinterpret_cast<HDROP>(wParam);
        try
        {
            if (std::filesystem::exists(Destination / "hold-drop"))
            {
                Publish(Destination / "drop-entered.json", "{\"entered\":true}");
                ULONGLONG started = GetTickCount64();
                while (!std::filesystem::exists(Destination / "read-drop"))
                {
                    if (GetTickCount64() - started >= 10000)
                    {
                        throw std::runtime_error("drop read deadline");
                    }
                    Sleep(10);
                }
            }
            POINT point{};
            BOOL client = DragQueryPoint(drop, &point);
            UINT count = DragQueryFileW(drop, 0xffffffff, nullptr, 0);
            std::string text = "{\"recipient\":"
                + std::to_string(reinterpret_cast<std::uintptr_t>(window))
                + ",\"point\":[" + std::to_string(point.x)
                + "," + std::to_string(point.y) + "],\"client\":"
                + (client ? "true" : "false") + ",\"in_send\":"
                + std::to_string(InSendMessageEx(nullptr)) + ",\"header\":";
            SIZE_T size = GlobalSize(reinterpret_cast<HGLOBAL>(drop));
            const auto* header = size >= sizeof(DROPFILES)
                ? static_cast<const DROPFILES*>(GlobalLock(reinterpret_cast<HGLOBAL>(drop)))
                : nullptr;
            if (header != nullptr)
            {
                text += "{\"bytes\":" + std::to_string(size)
                    + ",\"offset\":" + std::to_string(header->pFiles)
                    + ",\"point\":[" + std::to_string(header->pt.x) + ","
                    + std::to_string(header->pt.y) + "]"
                    + ",\"nonclient\":" + std::to_string(header->fNC)
                    + ",\"wide\":" + std::to_string(header->fWide) + "}";
                GlobalUnlock(reinterpret_cast<HGLOBAL>(drop));
            }
            else text += "null";
            text += ",\"paths\":[";
            ULONGLONG scanStarted = GetTickCount64();
            wchar_t shortPath[512];
            constexpr UINT shortCapacity = sizeof(shortPath) / sizeof(shortPath[0]);
            std::vector<wchar_t> longPath;
            for (UINT index = 0; index < count; ++index)
            {
                const wchar_t* path = shortPath;
                UINT length = DragQueryFileW(drop, index, shortPath, shortCapacity);
                // 短いバッファーに収まるか不明な場合は、明示的に長さを調べます。
                if (length >= shortCapacity - 1)
                {
                    length = DragQueryFileW(drop, index, nullptr, 0);
                    longPath.resize(static_cast<size_t>(length) + 1);
                    if (DragQueryFileW(drop, index, longPath.data(), length + 1) != length)
                    {
                        throw std::runtime_error("DragQueryFileW");
                    }
                    path = longPath.data();
                }
                if (index != 0) text += ",";
                text += "[";
                for (UINT offset = 0; offset < length; ++offset)
                {
                    if (offset != 0) text += ",";
                    text += std::to_string(static_cast<unsigned int>(path[offset]));
                }
                text += "]";
            }
            text += "],\"scan_ms\":" + std::to_string(GetTickCount64() - scanStarted)
                + "}";
            // 受信側で読み終えてから解放し、通知を確定します。
            DragFinish(drop);
            drop = nullptr;
            Publish(Destination / ("drop-" + std::to_string(Sequence++) + ".json"), text);
        }
        catch (...)
        {
            if (drop != nullptr) DragFinish(drop);
            PostQuitMessage(4);
        }
        return 0;
    }
    if (message == WM_TIMER || message == WM_DESTROY)
    {
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(window, message, wParam, lParam);
}

int wmain(int argc, wchar_t** argv)
{
    // 誤ってホストで起動しないよう、ゲストの利用者名を確認します。
    wchar_t username[256]{};
    DWORD capacity = 256;
    if (!GetUserNameW(username, &capacity)
        || std::wstring(username) != L"WDAGUtilityAccount") return 10;
    std::wstring mode = argc == 4 ? argv[3] : L"";
    bool hasChild = mode == L"child-drop" || mode == L"child-no-drop"
        || mode == L"child-disabled" || mode == L"child-transparent"
        || mode == L"child-nested";
    if (argc != 3 && !(argc == 4 && (hasChild
        || mode == L"root-no-drop" || mode == L"hold-before-pump"))) return 2;
    Destination = argv[1];
    SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    HINSTANCE instance = GetModuleHandleW(nullptr);
    WNDCLASSW type{};
    type.lpfnWndProc = Dispatch;
    type.hInstance = instance;
    type.lpszClassName = L"UWSCSharp.DropFile.OwnedProbe";
    if (!RegisterClassW(&type)) return 3;
    bool rootAccepts = mode != L"root-no-drop";
    HWND window = CreateWindowExW(rootAccepts ? WS_EX_ACCEPTFILES : 0, type.lpszClassName,
        argv[2], WS_OVERLAPPEDWINDOW, 80, 90, 480, 320,
        nullptr, nullptr, instance, nullptr);
    if (window == nullptr) return 3;
    DragAcceptFiles(window, rootAccepts);
    HWND child = nullptr;
    if (hasChild)
    {
        bool childAccepts = mode != L"child-no-drop" && mode != L"child-nested";
        DWORD extendedStyle = childAccepts ? WS_EX_ACCEPTFILES : 0;
        if (mode == L"child-transparent") extendedStyle |= WS_EX_TRANSPARENT;
        DWORD style = WS_CHILD | WS_VISIBLE;
        if (mode == L"child-disabled") style |= WS_DISABLED;
        child = CreateWindowExW(extendedStyle, type.lpszClassName,
            L"Owned drop child", style, 20, 20, 160, 120,
            window, nullptr, instance, nullptr);
        if (child == nullptr) { DestroyWindow(window); return 3; }
        DragAcceptFiles(child, childAccepts);
        if (mode == L"child-nested")
        {
            HWND container = child;
            child = CreateWindowExW(WS_EX_ACCEPTFILES, type.lpszClassName,
                L"Owned drop grandchild", WS_CHILD | WS_VISIBLE, 8, 11, 80, 60,
                container, nullptr, instance, nullptr);
            HWND overlay = CreateWindowExW(WS_EX_ACCEPTFILES | WS_EX_TRANSPARENT,
                type.lpszClassName, L"Owned transparent sibling", WS_CHILD | WS_VISIBLE,
                8, 11, 80, 60, container, nullptr, instance, nullptr);
            if (child == nullptr || overlay == nullptr
                || !SetWindowPos(overlay, HWND_TOP, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE))
            {
                DestroyWindow(window);
                return 3;
            }
            DragAcceptFiles(child, TRUE);
            DragAcceptFiles(overlay, TRUE);
        }
    }
    ShowWindow(window, SW_SHOWNOACTIVATE);
    SetTimer(window, 1, 30000, nullptr);
    RECT rectangle{};
    RECT client{};
    GetWindowRect(window, &rectangle);
    GetClientRect(window, &client);
    POINT origin{};
    ClientToScreen(window, &origin);
    try
    {
        Publish(Destination / "ready.json", "{\"hwnd\":"
            + std::to_string(reinterpret_cast<std::uintptr_t>(window))
            + ",\"bits\":" + std::to_string(sizeof(void*) * 8)
            + ",\"pid\":" + std::to_string(GetCurrentProcessId())
            + ",\"child\":" + std::to_string(reinterpret_cast<std::uintptr_t>(child))
            + ",\"rect\":[" + std::to_string(rectangle.left) + ","
            + std::to_string(rectangle.top) + "," + std::to_string(rectangle.right)
            + "," + std::to_string(rectangle.bottom) + "],\"client_size\":["
            + std::to_string(client.right) + "," + std::to_string(client.bottom)
            + "],\"client_origin\":[" + std::to_string(origin.x) + ","
            + std::to_string(origin.y) + "]}");
    }
    catch (...) { DestroyWindow(window); return 4; }
    if (mode == L"hold-before-pump")
    {
        // 送信メッセージを処理しながら、WM_DROPFILES はキューに残します。
        ULONGLONG started = GetTickCount64();
        MSG pending{};
        while (GetTickCount64() - started < 15000)
        {
            if (PeekMessageW(&pending, window, WM_DROPFILES, WM_DROPFILES,
                PM_NOREMOVE))
            {
                Publish(Destination / "drop-queued.json", "{\"queued\":true}");
                Sleep(15000);
                break;
            }
            Sleep(10);
        }
        DestroyWindow(window);
        return 6;
    }
    MSG message{};
    int result;
    while ((result = GetMessageW(&message, nullptr, 0, 0)) > 0)
    {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
    DestroyWindow(window);
    return result < 0 ? 5 : static_cast<int>(message.wParam);
}
