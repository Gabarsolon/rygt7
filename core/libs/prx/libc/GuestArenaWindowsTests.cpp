#include "prx/libc/include/GuestArena.hpp"
#include "prx/libc/include/WindowsMappings.hpp"
#include <windows.h>
#include <cstdint>
#include <cstdio>
#include <stdexcept>

namespace {
void Require(bool ok, const char* message) {
    if (!ok) throw std::runtime_error(message);
}

MEMORY_BASIC_INFORMATION Query(const void* address) {
    MEMORY_BASIC_INFORMATION info{};
    Require(VirtualQuery(address, &info, sizeof(info)) == sizeof(info), "VirtualQuery failed");
    return info;
}
}

int main() {
    using namespace GuestArena;
    constexpr std::size_t Page = 0x4000;
    void* driverBase = nullptr;
    void* largeHost = nullptr;
    constexpr std::size_t DriverBytes = 0xc00000000ull;
    constexpr std::size_t LargeHostBytes = 0x4000000000ull;
    try {
        // An unconstrained 256 GiB AMD reservation must not occupy guest addresses.
        largeHost = VirtualAlloc(nullptr, LargeHostBytes, MEM_RESERVE, PAGE_NOACCESS);
        Require(largeHost && reinterpret_cast<std::uintptr_t>(largeHost) >= 0xfc00000000ull,
                "large host reservation entered the guest mapping windows");
        // NVIDIA needs 48 GiB of low virtual addresses for its interop reservations.
        driverBase = VirtualAlloc(reinterpret_cast<void*>(0xe000000000ull), DriverBytes, MEM_RESERVE, PAGE_NOACCESS);
        Require(driverBase != nullptr, "guest arena left no low-address interop space");
        auto* race = reinterpret_cast<std::uint8_t*>(0x29000e0000ull);
        Require(Query(race).State == MEM_RESERVE, "race fixed mapping window was not protected");
        GuestArenaMarkUsed_nid_postfix(race, 0x10000);
        GuestArenaCommit_nid_postfix(race, 0x10000, PAGE_READWRITE, Page);
        race[0] = 9;
        GuestArenaReset_nid_postfix(race, 0x10000);
        GuestArenaRelease_nid_postfix(race, 0x10000);
        auto* startup = reinterpret_cast<std::uint8_t*>(0x1020000000ull);
        Require(Query(startup).State == MEM_RESERVE, "startup fixed mapping window was not protected");
        GuestArenaMarkUsed_nid_postfix(startup, 0x200000);
        GuestArenaCommit_nid_postfix(startup, 0x200000, PAGE_READWRITE, Page);
        startup[0] = 7;
        GuestArenaReset_nid_postfix(startup, 0x200000);
        GuestArenaRelease_nid_postfix(startup, 0x200000);

        auto* first = static_cast<std::uint8_t*>(GuestArenaAllocate_nid_postfix(Page, Page));
        auto* second = static_cast<std::uint8_t*>(GuestArenaAllocate_nid_postfix(Page, Page));
        Require(second == first + Page, "sub-granularity guest allocations stopped packing together");
        GuestArenaCommit_nid_postfix(first, Page, PAGE_READWRITE, Page);
        GuestArenaCommit_nid_postfix(second, Page, PAGE_READWRITE, Page);
        first[0] = 11;
        second[0] = 22;
        GuestArenaReset_nid_postfix(first, Page);
        GuestArenaRelease_nid_postfix(first, Page);
        Require(second[0] == 22, "reset damaged a neighboring guest mapping");
        Require(GuestArenaAllocate_nid_postfix(Page, Page) == first, "released guest range was not reused");
        GuestArenaCommit_nid_postfix(first, Page, PAGE_READWRITE, Page);
        first[0] = 33;

        const auto driverAddress = reinterpret_cast<std::uintptr_t>(largeHost);
        auto* large = WindowsMappings::Get().FindAndReserve(driverAddress - 0x10000, driverAddress + LargeHostBytes + 0x200000000ull,
                                                          0x200000000ull, 0x10000);
        Require(large && reinterpret_cast<std::uintptr_t>(large) >= driverAddress + LargeHostBytes,
                "reservation search did not skip foreign host memory");
        Require(Query(driverBase).AllocationBase == driverBase, "driver reservation was changed");

        bool rejected = false;
        try { GuestArenaMarkUsed_nid_postfix(driverBase, Page); }
        catch (const std::runtime_error&) { rejected = true; }
        Require(rejected, "fixed guest allocation accepted a host reservation");
        rejected = false;
        try { GuestArenaMarkUsed_nid_postfix(reinterpret_cast<void*>(0xec00000000ull), Page); }
        catch (const std::runtime_error&) { rejected = true; }
        Require(rejected, "fixed guest allocation stole unused interop address space");
        rejected = false;
        try { GuestArenaReset_nid_postfix(driverBase, Page); }
        catch (const std::runtime_error&) { rejected = true; }
        Require(rejected && Query(driverBase).AllocationBase == driverBase,
                "guest reset changed a host reservation");

        auto* fixed = reinterpret_cast<std::uint8_t*>(0xf3ffe00000ull);
        GuestArenaMarkUsed_nid_postfix(fixed, 2 * Page);
        GuestArenaCommit_nid_postfix(fixed, 2 * Page, PAGE_READWRITE, Page);
        fixed[0] = 44;
        fixed[Page] = 55;
        GuestArenaReset_nid_postfix(fixed, 2 * Page);
        HANDLE section = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_EXECUTE_READWRITE, 0, 2 * Page, nullptr);
        Require(section != nullptr, "could not create guest backing");
        GuestArenaMap_nid_postfix(fixed, 2 * Page, section, 0, PAGE_READWRITE);
        fixed[0] = 66;
        fixed[Page] = 77;
        auto* alias = static_cast<std::uint8_t*>(GuestArenaMapAlias_nid_postfix(reinterpret_cast<std::uintptr_t>(fixed), 2 * Page));
        Require(alias[Page] == 77, "guest alias did not share the backing");
        alias[Page] = 88;
        GuestArenaReset_nid_postfix(fixed, Page);
        Require(fixed[Page] == 88, "partial unmap damaged the rest of a shared view");
        GuestArenaCommit_nid_postfix(fixed, Page, PAGE_READWRITE, Page);
        fixed[0] = 99;
        Require(fixed[Page] == 88, "partial recommit damaged shared backing");
        GuestArenaUnmapAlias_nid_postfix(alias);
        GuestArenaReset_nid_postfix(fixed, 2 * Page);
        GuestArenaRelease_nid_postfix(fixed, 2 * Page);
        CloseHandle(section);

        // GT7 streams direct memory as 64 KB maps at ascending addresses of descending backing
        // offsets into free placeholder space, out of order here: one exact view each, carved from
        // the placeholder without disturbing the neighbors or the backing.
        constexpr std::size_t Piece = 0x10000;
        constexpr int Pieces = 16;
        auto* stream = reinterpret_cast<std::uint8_t*>(0xf3ff000000ull);
        GuestArenaMarkUsed_nid_postfix(stream, Pieces * Piece + Page);
        HANDLE pool = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_EXECUTE_READWRITE, 0, Pieces * Piece, nullptr);
        Require(pool != nullptr, "could not create streaming backing");
        auto* backing = static_cast<std::uint8_t*>(MapViewOfFile(pool, FILE_MAP_ALL_ACCESS, 0, 0, Pieces * Piece));
        Require(backing != nullptr, "could not view streaming backing");
        for (int i : {3, 0, 15, 7, 1, 2, 14, 4, 5, 6, 8, 9, 13, 10, 11, 12}) {
            GuestArenaMap_nid_postfix(stream + i * Piece, Piece, pool, (Pieces - 1 - i) * Piece, PAGE_READWRITE);
            stream[i * Piece] = static_cast<std::uint8_t>(100 + i);
        }
        for (int i = 0; i < Pieces; ++i) {
            const auto view = Query(stream + i * Piece);
            Require(view.Type == MEM_MAPPED && view.AllocationBase == stream + i * Piece && view.RegionSize == Piece, "streamed piece is not one exact view");
            Require(backing[(Pieces - 1 - i) * Piece] == 100 + i, "streamed piece maps the wrong backing offset");
        }
        Require(Query(stream + Pieces * Piece).State == MEM_RESERVE, "placeholder after the streamed pieces was lost");
        GuestArenaMap_nid_postfix(stream + Pieces * Piece, Page, pool, 0, PAGE_READONLY);
        Require(stream[Pieces * Piece] == 115 && Query(stream + Pieces * Piece).Protect == PAGE_READONLY, "single page streamed map");
        GuestArenaReset_nid_postfix(stream + 7 * Piece, Piece);
        Require(Query(stream + 7 * Piece).State == MEM_RESERVE && Query(stream + 7 * Piece).RegionSize == Piece, "released piece is not its own placeholder");
        Require(stream[6 * Piece] == 106 && stream[8 * Piece] == 108, "releasing a piece damaged its neighbors");
        GuestArenaMap_nid_postfix(stream + 7 * Piece, Piece, pool, (Pieces - 1) * Piece, PAGE_READWRITE);
        stream[7 * Piece] = 42;
        Require(stream[0] == 42, "remapped piece does not alias its backing");
        // Mapping over pieces that are already mapped takes the general path.
        GuestArenaMap_nid_postfix(stream + 10 * Piece, 2 * Piece, pool, 0, PAGE_READWRITE);
        Require(stream[10 * Piece] == 115 && Query(stream + 10 * Piece).RegionSize == 2 * Piece, "map over mapped pieces");
        Require(stream[9 * Piece] == 109 && stream[12 * Piece] == 112, "map over mapped pieces damaged its neighbors");
        GuestArenaReset_nid_postfix(stream, Pieces * Piece + Page);
        Require(Query(stream).State == MEM_RESERVE && Query(stream + Pieces * Piece).State == MEM_RESERVE, "streamed pieces were not released");
        UnmapViewOfFile(backing);
        CloseHandle(pool);
        VirtualFree(driverBase, 0, MEM_RELEASE);
        VirtualFree(largeHost, 0, MEM_RELEASE);
        std::puts("PASS: partitioned arena, host collisions, reuse, fixed mappings, partial unmap, shared aliases, streamed pieces");
        return 0;
    } catch (const std::exception& error) {
        std::fprintf(stderr, "FAIL: %s (Windows error %lu)\n", error.what(), GetLastError());
        return 1;
    }
}
