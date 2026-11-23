#include "../NativeDllLifetimeProbe/fixture.cpp"

namespace
{
    HANDLE explicitUnload = nullptr;
    HANDLE processUnload = nullptr;
}

extern "C" __declspec(dllexport) int __stdcall WatchUnload(
    const wchar_t* explicitName, const wchar_t* processName)
{
    EventHandle explicitEvent(explicitName, EVENT_MODIFY_STATE);
    EventHandle processEvent(processName, EVENT_MODIFY_STATE);
    if (explicitEvent.value == nullptr || processEvent.value == nullptr
        || explicitUnload != nullptr || processUnload != nullptr)
    {
        return -30;
    }
    explicitUnload = explicitEvent.value;
    processUnload = processEvent.value;
    explicitEvent.value = nullptr;
    processEvent.value = nullptr;
    return 1;
}

BOOL WINAPI DllMain(HINSTANCE, DWORD reason, LPVOID reserved)
{
    if (reason == DLL_PROCESS_DETACH)
    {
        // 待機や資源の新規取得は行わず、事前に開いたイベントだけを通知します。
        HANDLE notification = reserved == nullptr ? explicitUnload : processUnload;
        if (notification != nullptr)
        {
            SetEvent(notification);
        }
        if (explicitUnload != nullptr)
        {
            CloseHandle(explicitUnload);
        }
        if (processUnload != nullptr)
        {
            CloseHandle(processUnload);
        }
    }
    return TRUE;
}
