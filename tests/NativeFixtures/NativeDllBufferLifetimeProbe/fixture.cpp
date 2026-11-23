#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

static_assert(sizeof(void*) == 8);

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

namespace
{
    template<typename T>
    bool Read(const void* source, T& value)
    {
        SIZE_T copied = 0;
        return ReadProcessMemory(GetCurrentProcess(), source, &value,
            sizeof(value), &copied) && copied == sizeof(value);
    }

    template<typename T>
    bool Write(void* destination, const T& value)
    {
        SIZE_T copied = 0;
        return WriteProcessMemory(GetCurrentProcess(), destination, &value,
            sizeof(value), &copied) && copied == sizeof(value);
    }
}

extern "C" __declspec(dllexport) int __stdcall Hold(
    void* first, void* second, void* third, int shape, int wide,
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
    void* items = first;
    void* text = second;
    void* number = third;
    if (shape != 0)
    {
        if (shape != 1 && shape != 2)
        {
            return -11;
        }
        auto* fields = static_cast<unsigned char*>(first);
        const SIZE_T start = shape == 1 ? 8 : 1;
        unsigned char tag = 0;
        unsigned char tail = 0;
        if (!Read(fields, tag) || tag != 17
            || !Read(fields + start, items)
            || !Read(fields + start + 8, text)
            || !Read(fields + start + 16, number)
            || !Read(fields + start + 24, tail) || tail != 34)
        {
            return -12;
        }
    }
    if (items == nullptr || text == nullptr || number == nullptr
        || items == text || items == number || text == number)
    {
        return -13;
    }
    int original[3]{};
    int counter = 0;
    if (!Read(items, original) || original[0] != 7
        || original[1] != 8 || original[2] != 9
        || !Read(number, counter) || counter != 19)
    {
        return -14;
    }
    if (wide != 0)
    {
        wchar_t originalText[6]{};
        if (!Read(text, originalText)
            || originalText[0] != L'日' || originalText[1] != L'本'
            || originalText[2] != L'語' || originalText[3] != L'a'
            || originalText[4] != L'b' || originalText[5] != 0)
        {
            return -15;
        }
        const wchar_t changed[6]{ L'あ', 0, L'う', L'え', L'お', 0 };
        if (!Write(text, changed))
        {
            return -16;
        }
    }
    else
    {
        char originalText[6]{};
        if (!Read(text, originalText)
            || originalText[0] != 'a' || originalText[1] != 'b'
            || originalText[2] != 'c' || originalText[3] != 'd'
            || originalText[4] != 'e' || originalText[5] != 0)
        {
            return -17;
        }
        const char changed[6]{ 'X', 0, 'Z', 'Q', 'R', 0 };
        if (!Write(text, changed))
        {
            return -18;
        }
    }
    const int changedItems[3]{ 101, 102, 103 };
    const int changedCounter = 109;
    if (!Write(items, changedItems) || !Write(number, changedCounter))
    {
        return -19;
    }
    EventHandle verified(verifiedName, EVENT_MODIFY_STATE);
    return verified.value != nullptr && SetEvent(verified.value) ? 42 : -20;
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
