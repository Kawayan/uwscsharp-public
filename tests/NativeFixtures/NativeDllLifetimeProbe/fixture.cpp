#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

namespace
{
    constexpr DWORD waitMilliseconds = 10000;

    class EventHandle
    {
    public:
        EventHandle(const wchar_t* name, DWORD access)
        {
            if (name == nullptr)
            {
                return;
            }
            wchar_t copied[128]{};
            for (SIZE_T index = 0; index < 128; ++index)
            {
                SIZE_T read = 0;
                if (!ReadProcessMemory(GetCurrentProcess(), name + index,
                    copied + index, sizeof(wchar_t), &read)
                    || read != sizeof(wchar_t))
                {
                    return;
                }
                if (copied[index] == 0)
                {
                    value = OpenEventW(access, FALSE, copied);
                    return;
                }
            }
        }

        ~EventHandle()
        {
            if (value != nullptr)
            {
                CloseHandle(value);
            }
        }

        EventHandle(const EventHandle&) = delete;
        EventHandle& operator=(const EventHandle&) = delete;

        HANDLE value = nullptr;
    };
}

extern "C" __declspec(dllexport) int __stdcall Hold(int* value,
    const wchar_t* enteredName, const wchar_t* releaseName,
    const wchar_t* verifiedName)
{
    EventHandle entered(enteredName, EVENT_MODIFY_STATE);
    EventHandle release(releaseName, SYNCHRONIZE);
    if (entered.value == nullptr || release.value == nullptr
        || !SetEvent(entered.value)
        || WaitForSingleObject(release.value, waitMilliseconds) != WAIT_OBJECT_0)
    {
        return -10;
    }
    int original = 0;
    SIZE_T copied = 0;
    if (!ReadProcessMemory(GetCurrentProcess(), value, &original,
        sizeof(original), &copied) || copied != sizeof(original) || original != 7)
    {
        return -11;
    }
    int changed = 101;
    if (!WriteProcessMemory(GetCurrentProcess(), value, &changed,
        sizeof(changed), &copied) || copied != sizeof(changed))
    {
        return -12;
    }
    EventHandle verified(verifiedName, EVENT_MODIFY_STATE);
    return verified.value != nullptr && SetEvent(verified.value) ? 42 : -13;
}

extern "C" __declspec(dllexport) int __stdcall Gate(
    const wchar_t* childName, const wchar_t* enteredName,
    const wchar_t* releaseName, const wchar_t* verifiedName)
{
    EventHandle child(childName, SYNCHRONIZE);
    EventHandle entered(enteredName, EVENT_MODIFY_STATE);
    EventHandle release(releaseName, SYNCHRONIZE);
    if (child.value == nullptr || entered.value == nullptr
        || release.value == nullptr
        || WaitForSingleObject(child.value, waitMilliseconds) != WAIT_OBJECT_0
        || !SetEvent(entered.value)
        || WaitForSingleObject(release.value, waitMilliseconds) != WAIT_OBJECT_0)
    {
        return -20;
    }
    EventHandle reread(releaseName, SYNCHRONIZE);
    EventHandle verified(verifiedName, EVENT_MODIFY_STATE);
    return reread.value != nullptr && verified.value != nullptr
        && SetEvent(verified.value) ? 11 : -21;
}
