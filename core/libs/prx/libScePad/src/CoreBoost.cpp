// P-cores for GT7's busy threads. On a hybrid CPU (Intel P/E cores) Windows may run the title's
// main job thread ("Job#0", at full load on one core) or the renderer's recorder on an efficiency
// core, and may throttle them (EcoQoS) when it sees fit. A watcher pins those threads to the
// performance cores, turns power throttling off for them and the process, and raises Job#0 a step.
// Other threads stay free to use every core. RYGT_PCORES=0 turns it off; non-hybrid CPUs are left
// alone.
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cwchar>
#include <thread>
#include <unordered_set>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <tlhelp32.h>

#include "prx/libScePad/include/PadState.hpp"
#include "prx/libc/include/CpuTopology.hpp"

namespace {

// The threads that set the frame rate (by the renderer's perf monitor): the game's main job thread,
// the renderer's command threads, presentation and the audio mixer.
constexpr const wchar_t* BusyThreads[] = {
    L"Job#0",
    L"shadGT:GpuRecorder",
    L"shadGT:GpuCommandProcessor",
    L"shadGT:VkCommands",
    L"shadGT:PresentThread",
    L"SceSndzAudioOutMain",
};

// The power throttling switch (THREAD_POWER_THROTTLING_STATE / PROCESS_POWER_THROTTLING_STATE).
struct PowerThrottling {
    ULONG version;
    ULONG control_mask;
    ULONG state_mask;
};
constexpr ULONG ThrottleExecutionSpeed = 0x1;
constexpr int ThreadPowerThrottlingClass = 3;   // THREAD_INFORMATION_CLASS ThreadPowerThrottling
constexpr int ProcessPowerThrottlingClass = 4;  // PROCESS_INFORMATION_CLASS ProcessPowerThrottling

using GetThreadDescriptionFn = HRESULT(WINAPI*)(HANDLE, PWSTR*);
using SetThreadInformationFn = BOOL(WINAPI*)(HANDLE, int, LPVOID, DWORD);
using SetProcessInformationFn = BOOL(WINAPI*)(HANDLE, int, LPVOID, DWORD);

bool IsBusy(const wchar_t* name) {
    for (const wchar_t* busy : BusyThreads)
        if (std::wcscmp(name, busy) == 0) return true;
    return false;
}

void Watch(std::uint64_t mask) {
    const HMODULE kernel = GetModuleHandleA("kernel32.dll");
    const auto describe = reinterpret_cast<GetThreadDescriptionFn>(GetProcAddress(kernel, "GetThreadDescription"));
    const auto set_thread = reinterpret_cast<SetThreadInformationFn>(GetProcAddress(kernel, "SetThreadInformation"));
    const auto set_process = reinterpret_cast<SetProcessInformationFn>(GetProcAddress(kernel, "SetProcessInformation"));
    if (describe == nullptr) return;
    PowerThrottling full_speed{1, ThrottleExecutionSpeed, 0};
    if (set_process != nullptr)
        set_process(GetCurrentProcess(), ProcessPowerThrottlingClass, &full_speed, sizeof(full_speed));

    const DWORD process = GetCurrentProcessId();
    std::unordered_set<DWORD> done;
    for (;;) {
        const HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
        if (snapshot != INVALID_HANDLE_VALUE) {
            THREADENTRY32 entry{};
            entry.dwSize = sizeof(entry);
            for (BOOL ok = Thread32First(snapshot, &entry); ok; ok = Thread32Next(snapshot, &entry)) {
                if (entry.th32OwnerProcessID != process || done.count(entry.th32ThreadID) != 0) continue;
                const HANDLE thread = OpenThread(THREAD_QUERY_LIMITED_INFORMATION | THREAD_QUERY_INFORMATION |
                    THREAD_SET_INFORMATION | THREAD_SET_LIMITED_INFORMATION, FALSE, entry.th32ThreadID);
                if (thread == nullptr) continue;
                PWSTR name = nullptr;
                if (SUCCEEDED(describe(thread, &name)) && name != nullptr) {
                    if (IsBusy(name)) {
                        done.insert(entry.th32ThreadID);
                        const bool pinned = SetThreadAffinityMask(thread, static_cast<DWORD_PTR>(mask)) != 0;
                        if (set_thread != nullptr)
                            set_thread(thread, ThreadPowerThrottlingClass, &full_speed, sizeof(full_speed));
                        if (std::wcscmp(name, L"Job#0") == 0) SetThreadPriority(thread, THREAD_PRIORITY_ABOVE_NORMAL);
                        std::fprintf(stderr, "[pcores] %ls (tid %lu) -> P-cores 0x%llx%s\n", name, entry.th32ThreadID,
                            static_cast<unsigned long long>(mask), pinned ? "" : " (not pinned)");
                        std::fflush(stderr);
                    }
                    LocalFree(name);
                }
                CloseHandle(thread);
            }
            CloseHandle(snapshot);
        }
        Sleep(1000);  // threads come and go between menus and races
    }
}

}

namespace Pad {

void InstallCoreBoost() {
    static bool done = false;
    if (done) return;
    done = true;
    if (const char* env = std::getenv("RYGT_PCORES"); env != nullptr && env[0] == '0') return;
    const auto& layout = CpuTopology::Get();
    std::uint64_t mask = layout.performant;
    if (layout.process != 0) mask &= layout.process;
    if (!layout.hybrid || mask == 0) return;
    std::fprintf(stderr, "[pcores] hybrid CPU: busy threads go to the P-cores 0x%llx (E-cores 0x%llx)\n",
        static_cast<unsigned long long>(mask), static_cast<unsigned long long>(layout.efficient));
    std::fflush(stderr);
    std::thread(Watch, mask).detach();
}

}
