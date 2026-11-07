#include "prx/libkernel/DirectMemory/DirectMemory.hpp"
#include "prx/libc/include/GuestAllocations.hpp"
#include "prx/libc/include/GuestArena.hpp"
#include "prx/libc/include/GuestWriteWatch.hpp"
#include "prx/libkernel/Time/include/Time.hpp"
#include <algorithm>
#include <cerrno>
#include <iterator>
#include <map>
#include <mutex>
#include <condition_variable>
#include <deque>
#include <thread>
#include <chrono>
#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <memory>
#include <limits>
#include <stdexcept>
#include <system_error>
#include <vector>

#if defined(__linux__)
#include <sys/mman.h>
#include <unistd.h>
#include <fstream>
#include <sstream>
#include <string>
#include <vector>
#else
#include <windows.h>

static constexpr int PROT_NONE = 0;
static constexpr int PROT_READ = 1;
static constexpr int PROT_WRITE = 2;
static constexpr int PROT_EXEC = 4;
static constexpr int MAP_PRIVATE = 0x02;
static constexpr int MAP_ANONYMOUS = 0x20;
static constexpr int MAP_FIXED = 0x10;
static void* const MAP_FAILED = reinterpret_cast<void*>(-1);

static DWORD WinProtFromPosix(int prot) {
    if (prot == PROT_NONE) return PAGE_NOACCESS;
    if ((prot & PROT_EXEC) && (prot & PROT_WRITE)) return PAGE_EXECUTE_READWRITE;
    if ((prot & PROT_EXEC) && (prot & PROT_READ)) return PAGE_EXECUTE_READ;
    if (prot & PROT_EXEC) return PAGE_EXECUTE;
    if (prot & PROT_WRITE) return PAGE_READWRITE;
    return PAGE_READONLY;
}

// Guest mappings share libc's guest address space arena so they stay inside the PS5 map area.
struct KernelArena {
    static KernelArena& Get() {
        static KernelArena arena;
        return arena;
    }
    bool Contains(const void* pointer, size_t len) const { return GuestArena::GuestArenaContains_nid_postfix(pointer, len); }
    void* Allocate(size_t len, size_t alignment) { return GuestArena::GuestArenaAllocate_nid_postfix(len, alignment); }
    void MarkUsed(const void* pointer, size_t len) { GuestArena::GuestArenaMarkUsed_nid_postfix(pointer, len); }
    void Release(const void* pointer, size_t len) { GuestArena::GuestArenaRelease_nid_postfix(pointer, len); }
};

static void CommitArenaRange(void* addr, size_t len, DWORD winProt) {
    GuestArena::GuestArenaCommit_nid_postfix(addr, len, winProt, PS5_PAGE_SIZE);
}

static void* mmap_aligned(size_t len, int prot, size_t alignment) {
    auto& arena = KernelArena::Get();
    void* result = arena.Allocate(len, alignment);
    if (prot != PROT_NONE) {
        try {
            CommitArenaRange(result, len, WinProtFromPosix(prot));
        } catch (...) {
            arena.Release(result, len);
            throw;
        }
    }
    return result;
}

static void* mmap(void* addr, size_t len, int prot, int flags, int, int) {
    DWORD winProt = WinProtFromPosix(prot);
    if (flags & MAP_FIXED) {
        auto& arena = KernelArena::Get();
        if (arena.Contains(addr, len)) {
            arena.MarkUsed(addr, len);
            if (prot != PROT_NONE) {
                try {
                    CommitArenaRange(addr, len, winProt);
                } catch (...) {
                    arena.Release(addr, len);
                    throw;
                }
            }
            return addr;
        }
        void* result = VirtualAlloc(addr, len, MEM_RESERVE | MEM_COMMIT, winProt);
        if (!result) throw std::system_error(static_cast<int>(GetLastError()), std::system_category(), "VirtualAlloc fixed failed");
        return result;
    }
    return mmap_aligned(len, prot, PS5_PAGE_SIZE);
}

static int munmap(void* addr, size_t len) {
    auto& arena = KernelArena::Get();
    if (arena.Contains(addr, len)) GuestArena::GuestArenaReset_nid_postfix(addr, len);
    else if (!VirtualFree(addr, len, MEM_DECOMMIT)) throw std::system_error(static_cast<int>(GetLastError()), std::system_category(), "VirtualFree decommit failed");
    if (arena.Contains(addr, len)) arena.Release(addr, len);
    return 0;
}

static int munmap_release(void* addr) {
    if (!VirtualFree(addr, 0, MEM_RELEASE))
        throw std::system_error(static_cast<int>(GetLastError()), std::system_category(), "VirtualFree release failed");
    return 0;
}

static int mprotect(void* addr, size_t len, int prot) {
    GuestArena::GuestArenaSetProtection_nid_postfix(reinterpret_cast<std::uintptr_t>(addr), len, WinProtFromPosix(prot));
    if (prot != PROT_NONE && KernelArena::Get().Contains(addr, len)) CommitArenaRange(addr, len, WinProtFromPosix(prot));
    auto cursor = reinterpret_cast<std::uintptr_t>(addr);
    const auto end = cursor + len;
    while (cursor < end) {
        MEMORY_BASIC_INFORMATION memory{};
        if (VirtualQuery(reinterpret_cast<void*>(cursor), &memory, sizeof(memory)) != sizeof(memory))
            throw std::system_error(static_cast<int>(GetLastError()), std::system_category(), "VirtualQuery failed");
        const auto regionEnd = std::min(end, reinterpret_cast<std::uintptr_t>(memory.BaseAddress) + memory.RegionSize);
        if (memory.State == MEM_COMMIT) {
            DWORD old;
            if (!VirtualProtect(reinterpret_cast<void*>(cursor), regionEnd - cursor, WinProtFromPosix(prot), &old))
                throw std::system_error(static_cast<int>(GetLastError()), std::system_category(), "VirtualProtect failed");
        } else if (prot != PROT_NONE) {
            throw std::runtime_error("mprotect of uncommitted memory outside the guest arena");
        }
        cursor = regionEnd;
    }
    return 0;
}
#endif

namespace {

constexpr int GuestMapFixedFlag = 0x10;

class DependencyScope {
public:
    explicit DependencyScope(unsigned zone, std::size_t bytes = 0) : zone(zone), bytes(bytes) {
        static const bool enabled = [] {
            const char* value = std::getenv("RYGT_DIAG_DEPENDENCIES");
            return value && value[0] == '1';
        }();
        if (enabled && KernelDiagEnabled()) start = std::chrono::steady_clock::now();
    }
    ~DependencyScope() { Finish(); }
    void Finish() {
        if (start == std::chrono::steady_clock::time_point{}) return;
        const auto elapsed = std::chrono::steady_clock::now() - start;
        start = {};
        KernelDiagRecord(static_cast<KernelDiagZone>(zone),
            std::chrono::duration_cast<std::chrono::nanoseconds>(elapsed).count(),
            reinterpret_cast<const void*>(bytes));
    }
    DependencyScope(const DependencyScope&) = delete;
    DependencyScope& operator=(const DependencyScope&) = delete;
private:
    unsigned zone;
    std::size_t bytes;
    std::chrono::steady_clock::time_point start{};
};

#if defined(__linux__)
void* MapAtOrAbove(std::uintptr_t start, size_t len, int prot, size_t alignment) {
    constexpr std::uintptr_t UserLimit = 0x7fff00000000ull;
    for (int attempt = 0; attempt < 8; ++attempt) {
        std::vector<std::pair<std::uintptr_t, std::uintptr_t>> used;
        std::ifstream maps("/proc/self/maps");
        std::string line;
        while (std::getline(maps, line)) {
            std::istringstream fields(line);
            std::uintptr_t begin = 0, end = 0;
            char dash = 0;
            if (fields >> std::hex >> begin >> dash >> end) used.emplace_back(begin, end);
        }
        std::sort(used.begin(), used.end());
        auto candidate = (start + alignment - 1) & ~(static_cast<std::uintptr_t>(alignment) - 1);
        for (const auto& [begin, end] : used) {
            if (end <= candidate) continue;
            if (begin >= candidate + len) break;
            candidate = (end + alignment - 1) & ~(static_cast<std::uintptr_t>(alignment) - 1);
        }
        if (candidate + len > UserLimit || candidate + len < candidate) throw std::runtime_error("No free range above the mapping address hint");
        void* result = mmap(reinterpret_cast<void*>(candidate), len, prot, MAP_PRIVATE | MAP_ANONYMOUS | MAP_FIXED_NOREPLACE, -1, 0);
        if (result != MAP_FAILED) return result;
        if (errno != EEXIST) throw std::system_error(errno, std::generic_category(), "Hinted mmap failed");
    }
    throw std::runtime_error("Hinted mmap kept racing with other mappings");
}
#endif

void ValidateLength(size_t len) {
    if (len == 0 || (len & (PS5_PAGE_SIZE - 1)) != 0) {
        // return SCE_KERNEL_ERROR_EINVAL;
        throw std::invalid_argument("Memory length must be a positive multiple of the guest page size");
    }
}

size_t ValidateAlignment(size_t alignment) {
    if (alignment == 0) return PS5_PAGE_SIZE;
    if (alignment < PS5_PAGE_SIZE || (alignment & (alignment - 1)) != 0) {
        // return SCE_KERNEL_ERROR_EINVAL;
        throw std::invalid_argument("Memory alignment must be a power of two no smaller than the guest page size");
    }
    return alignment;
}

void ValidateRange(const void* addr, size_t len, size_t alignment) {
    ValidateLength(len);
    const auto start = reinterpret_cast<std::uintptr_t>(addr);
    if (!addr || (start & (alignment - 1)) != 0 || len > std::numeric_limits<std::uintptr_t>::max() - start) {
        // return SCE_KERNEL_ERROR_EINVAL;
        throw std::invalid_argument("Invalid memory address, alignment or range");
    }
}

int LinuxProtFromSce(int prot) {
    if ((prot & ~0xF7) != 0) {
        // return SCE_KERNEL_ERROR_EINVAL;
        throw std::invalid_argument("Unsupported memory protection bits: " + std::to_string(prot));
    }
    int result = PROT_NONE;
    if (prot & 0x13) result |= PROT_READ;
    if (prot & 0x22) result |= PROT_READ | PROT_WRITE;
    if (prot & 4) result |= PROT_READ | PROT_EXEC;
    return result;
}

bool TraceEnabled() {
    static const bool enabled = std::getenv("APS5_TRACE_MEMORY") != nullptr;
    return enabled;
}

void Trace(const char* format, ...) {
    if (!TraceEnabled()) return;
    va_list args;
    va_start(args, format);
    std::fprintf(stderr, "[memory] ");
    std::vfprintf(stderr, format, args);
    std::fputc('\n', stderr);
    va_end(args);
}

class PhysicalBacking {
public:
    explicit PhysicalBacking(std::size_t bytes, int memoryType) : memoryType(memoryType) {
#ifdef _WIN32
        const auto size = static_cast<std::uint64_t>(bytes);
        section = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_EXECUTE_READWRITE, static_cast<DWORD>(size >> 32), static_cast<DWORD>(size), nullptr);
        if (!section) throw std::system_error(static_cast<int>(GetLastError()), std::system_category(), "create direct memory backing");
#else
        file = memfd_create("direct memory", MFD_CLOEXEC);
        if (file < 0) throw std::system_error(errno, std::generic_category(), "create direct memory backing");
        if (ftruncate(file, static_cast<off_t>(bytes)) != 0) {
            const int error = errno;
            ::close(file);
            throw std::system_error(error, std::generic_category(), "size direct memory backing");
        }
#endif
    }

    int MemoryType() const { return memoryType; }

    ~PhysicalBacking() {
#ifdef _WIN32
        CloseHandle(section);
#else
        ::close(file);
#endif
    }

    PhysicalBacking(const PhysicalBacking&) = delete;
    PhysicalBacking& operator=(const PhysicalBacking&) = delete;

    void Map(std::uintptr_t address, std::size_t bytes, std::uint64_t offset, int protection) const {
        DependencyScope dependency(95, bytes);
#ifdef _WIN32
        GuestArena::GuestArenaMap_nid_postfix(reinterpret_cast<void*>(address), bytes, section, offset, WinProtFromPosix(protection));
#else
        if (::mmap(reinterpret_cast<void*>(address), bytes, protection, MAP_SHARED | MAP_FIXED, file, static_cast<off_t>(offset)) == MAP_FAILED) throw std::system_error(errno, std::generic_category(), "map direct memory backing");
#endif
    }

private:
    int memoryType;
#ifdef _WIN32
    HANDLE section = nullptr;
#else
    int file = -1;
#endif
};

struct PhysicalPage {
    std::shared_ptr<PhysicalBacking> backing;
    std::uint64_t offset;
};

std::mutex g_directLock;
std::map<std::uint64_t, PhysicalPage> g_physPages;

struct DirectMapping {
    std::uintptr_t end;
    std::uint64_t phys;
    int memoryType;
    std::shared_ptr<PhysicalBacking> backing;
};

std::map<std::uintptr_t, DirectMapping> g_directMappings;

void EraseMappings(std::uintptr_t start, std::uintptr_t end) {
    auto it = g_directMappings.lower_bound(start);
    if (it != g_directMappings.begin() && std::prev(it)->second.end > start) --it;
    while (it != g_directMappings.end() && it->first < end) {
        const auto base = it->first;
        const auto mapping = it->second;
        it = g_directMappings.erase(it);
        if (base < start) g_directMappings.emplace(base, DirectMapping{start, mapping.phys, mapping.memoryType, mapping.backing});
        if (mapping.end > end) it = g_directMappings.emplace(end, DirectMapping{mapping.end, mapping.phys + end - base, mapping.memoryType, mapping.backing}).first;
    }
}


void ValidatePhysicalRange(std::uint64_t phys, std::size_t len) {
    for (std::size_t offset = 0; offset < len; offset += PS5_PAGE_SIZE) {
        if (!g_physPages.contains(phys + offset)) {
            char message[160];
            std::snprintf(message, sizeof(message), "direct memory range 0x%llx+0x%zx references unallocated physical page 0x%llx", static_cast<unsigned long long>(phys), len, static_cast<unsigned long long>(phys + offset));
            throw std::invalid_argument(message);
        }
    }
}

#if defined(__linux__)
void WatchMapping(std::uintptr_t address, std::size_t len) {
    GuestWriteWatch::GuestWriteWatchRegister_nid_postfix(reinterpret_cast<const void*>(address), len);
    const auto end = address + len;
    for (auto added = g_directMappings.lower_bound(address); added != g_directMappings.end() && added->first < end; ++added) {
        const auto addedPhys = added->second.phys;
        const auto addedPhysEnd = addedPhys + (added->second.end - added->first);
        for (const auto& [base, other] : g_directMappings) {
            if ((base >= address && base < end) || other.backing != added->second.backing) continue;
            const auto first = std::max(addedPhys, other.phys);
            const auto last = std::min(addedPhysEnd, other.phys + (other.end - base));
            if (first >= last) continue;
            GuestWriteWatch::GuestWriteWatchUnregister_nid_postfix(reinterpret_cast<const void*>(added->first + (first - addedPhys)), last - first);
            GuestWriteWatch::GuestWriteWatchUnregister_nid_postfix(reinterpret_cast<const void*>(base + (first - other.phys)), last - first);
        }
    }
}
#endif

void AddMapping(std::uintptr_t address, std::size_t len, std::uint64_t phys, int nativeProt) {
    ValidatePhysicalRange(phys, len);
    EraseMappings(address, address + len);
    for (std::size_t offset = 0; offset < len;) {
        const auto& page = g_physPages.at(phys + offset);
        std::size_t bytes = PS5_PAGE_SIZE;
        while (offset + bytes < len) {
            const auto& next = g_physPages.at(phys + offset + bytes);
            if (next.backing != page.backing || next.offset != page.offset + bytes) break;
            bytes += PS5_PAGE_SIZE;
        }
        page.backing->Map(address + offset, bytes, page.offset, nativeProt);
        g_directMappings.emplace(address + offset, DirectMapping{address + offset + bytes, phys + offset, page.backing->MemoryType(), page.backing});
        offset += bytes;
    }
#if defined(__linux__)
    WatchMapping(address, len);
#endif
}

bool RemapFixedIntoRegistered(GuestAllocations::Mutation& mutation, void* addr, size_t len, int prot, int flags, int64_t physStart = -1) {
    constexpr int GuestMapFixed = 0x10;
    constexpr int GuestMapNoOverwrite = 0x80;
    if (addr == nullptr || (flags & GuestMapFixed) == 0 || (flags & GuestMapNoOverwrite) != 0 || !mutation.Covers(addr, len)) return false;
    ValidateRange(addr, len, PS5_PAGE_SIZE);
    Trace("remap fixed %p+0x%zx prot=0x%x phys=0x%llx", addr, len, prot, static_cast<long long>(physStart));
    const auto nativeProtection = LinuxProtFromSce(prot);
    mutation.Protect(addr, len, (prot & 3) != 0, (prot & 2) != 0, [&] {
        const auto address = reinterpret_cast<std::uintptr_t>(addr);
        DependencyScope direct_wait(97);
        std::lock_guard lock(g_directLock);
        direct_wait.Finish();
        if (physStart >= 0) {
            AddMapping(address, len, static_cast<std::uint64_t>(physStart), nativeProtection);
        } else {
            EraseMappings(address, address + len);
#ifdef _WIN32
            GuestArena::GuestArenaReset_nid_postfix(addr, len);
            if (nativeProtection != PROT_NONE) CommitArenaRange(addr, len, WinProtFromPosix(nativeProtection));
#else
            if (::mmap(addr, len, nativeProtection, MAP_PRIVATE | MAP_ANONYMOUS | MAP_FIXED, -1, 0) == MAP_FAILED) throw std::system_error(errno, std::generic_category(), "remap anonymous memory");
            GuestWriteWatch::GuestWriteWatchRegister_nid_postfix(addr, len);
#endif
        }
    });
    return true;
}

void Unmap(void* addr, size_t len) {
#if defined(__linux__)
    if (munmap(addr, len) != 0) throw std::system_error(errno, std::generic_category(), "munmap failed");
    GuestWriteWatch::GuestWriteWatchUnregister_nid_postfix(addr, len);
#else
    if (KernelArena::Get().Contains(addr, len)) munmap(addr, len);
    else munmap_release(addr);
#endif
}

void* MapPlaced(void* addr, size_t len, int prot, int flags, size_t alignment) {
    ValidateLength(len);
    alignment = ValidateAlignment(alignment);
    constexpr int GuestMapFixed = 0x10;
    constexpr int GuestMapNoOverwrite = 0x80;
    constexpr int GuestMapNoCoalesce = 0x400000;
    // MAP_NO_OVERWRITE (GT7 maps direct memory with MAP_FIXED | MAP_NO_OVERWRITE): DoMapDirect and
    // DoMapAnon skip ReleaseFixedOverlap for it and call RequireAvailable before placing, so on
    // Windows the range is known to be free here and a plain fixed placement is right.
    constexpr int SupportedFlags = GuestMapFixed | GuestMapNoOverwrite | GuestMapNoCoalesce;
    if ((flags & ~SupportedFlags) != 0) {
        char message[64];
        std::snprintf(message, sizeof(message), "Unsupported memory mapping flags 0x%x", flags);
        throw std::invalid_argument(message);
    }
    if ((flags & GuestMapFixed) != 0) {
        ValidateRange(addr, len, alignment);
#if defined(__linux__)
        const int placement = (flags & GuestMapNoOverwrite) != 0 ? MAP_FIXED_NOREPLACE : MAP_FIXED;
#else
        const int placement = MAP_FIXED;
#endif
        void* result = mmap(addr, len, prot, MAP_PRIVATE | MAP_ANONYMOUS | placement, -1, 0);
        if (result == MAP_FAILED) {
            // return SCE_KERNEL_ERROR_ENOMEM;
            throw std::system_error(errno, std::generic_category(), "Fixed mmap failed");
        }
        return result;
    }
    if (addr) {
#if defined(__linux__)
        return MapAtOrAbove(reinterpret_cast<std::uintptr_t>(addr), len, prot, alignment);
#elif defined(_WIN32)
        // A non-fixed address is only a hint; PS4 titles accept any placement in the arena.
        return mmap_aligned(len, prot, alignment);
#else
        throw std::invalid_argument("Non-fixed mapping address hints are not implemented");
#endif
    }
#ifdef _WIN32
    return mmap_aligned(len, prot, alignment);
#endif
    if (len > std::numeric_limits<size_t>::max() - alignment) {
        throw std::overflow_error("Aligned mapping size overflow");
    }
    const size_t allocLen = len + alignment;
    void* result = mmap(nullptr, allocLen, prot, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    if (result == MAP_FAILED) {
        // return SCE_KERNEL_ERROR_ENOMEM;
        throw std::system_error(errno, std::generic_category(), "Aligned mmap failed");
    }
    const auto raw = reinterpret_cast<std::uintptr_t>(result);
    const size_t prefix = (alignment - (raw & (alignment - 1))) & (alignment - 1);
    void* aligned = reinterpret_cast<void*>(raw + prefix);
    const size_t suffix = allocLen - prefix - len;
    if (prefix != 0 && munmap(result, prefix) != 0) {
        const int error = errno;
        Unmap(result, allocLen);
        throw std::system_error(error, std::generic_category(), "Mapping prefix munmap failed");
    }
    if (suffix != 0 && munmap(reinterpret_cast<void*>(raw + prefix + len), suffix) != 0) {
        const int error = errno;
        Unmap(aligned, allocLen - prefix);
        throw std::system_error(error, std::generic_category(), "Mapping suffix munmap failed");
    }
    return aligned;
}

struct ProtectedRange {
    std::uintptr_t end;
    int prot;
};
std::mutex g_protectionLock;
std::map<std::uintptr_t, ProtectedRange> g_protections;

void EraseProtections(std::uintptr_t start, std::uintptr_t end) {
    auto it = g_protections.lower_bound(start);
    if (it != g_protections.begin() && std::prev(it)->second.end > start) --it;
    while (it != g_protections.end() && it->first < end) {
        const auto rangeStart = it->first;
        const auto range = it->second;
        it = g_protections.erase(it);
        if (rangeStart < start) g_protections.emplace(rangeStart, ProtectedRange{start, range.prot});
        if (range.end > end) it = g_protections.emplace(end, ProtectedRange{range.end, range.prot}).first;
    }
}

// Whether every byte of [start, end) already has the protection prot.
bool ProtectionUnchanged(std::uintptr_t start, std::uintptr_t end, int prot) {
    std::lock_guard lock(g_protectionLock);
    auto it = g_protections.upper_bound(start);
    if (it == g_protections.begin()) return false;
    --it;
    for (auto cursor = start; cursor < end; ++it) {
        if (it == g_protections.end() || it->first > cursor || it->second.end <= cursor || it->second.prot != prot) return false;
        cursor = it->second.end;
    }
    return true;
}

void RecordProtection(const void* addr, size_t len, int prot) {
    DependencyScope dependency(98, len);
    const auto start = reinterpret_cast<std::uintptr_t>(addr);
    std::lock_guard lock(g_protectionLock);
    EraseProtections(start, start + len);
    if (prot >= 0) g_protections.emplace(start, ProtectedRange{start + len, prot});
}

void* MapAligned(void* addr, size_t len, int prot, int flags, size_t alignment) {
    void* mapped = MapPlaced(addr, len, prot, flags, alignment);
#if defined(__linux__)
    GuestWriteWatch::GuestWriteWatchRegister_nid_postfix(mapped, len);
#endif
    return mapped;
}

void ValidateOutput(void** addr) {
    if (!addr) {
        // return SCE_KERNEL_ERROR_EINVAL;
        throw std::invalid_argument("Null memory mapping output");
    }
}

}

// A renderer that caches GPU copies of guest memory (the shadGT-based libSceVideoOut.prx) must
// drop them when the game unmaps or replaces that memory; it exports these entry points for it.
// Called outside the allocation lock, since the renderer may read guest memory while flushing.
void NotifyRenderer(const char* name, const void* addr, size_t len) {
#ifdef _WIN32
    using RangeFunction = void (*)(std::uint64_t, std::uint64_t);
    static HMODULE renderer = nullptr;
    if (renderer == nullptr) renderer = GetModuleHandleA("libSceVideoOut.prx");
    if (renderer == nullptr || addr == nullptr || len == 0) return;
    if (const auto function = reinterpret_cast<RangeFunction>(reinterpret_cast<void*>(GetProcAddress(renderer, name)))) {
        function(reinterpret_cast<std::uint64_t>(addr), len);
    }
#else
    (void)name;
    (void)addr;
    (void)len;
#endif
}

// MAP_FIXED without MAP_NO_OVERWRITE replaces whatever is mapped there (PS4 titles rely on it,
// e.g. GT Sport remaps a larger block over an existing one). A range wholly inside one registered
// allocation is remapped in place by RemapFixedIntoRegistered; any other overlap is unmapped first.
// The renderer is told first, so it drops its copies of the replaced memory. Free address space
// has none to drop: what was mapped there went through DoMunmap, which told the renderer. Each of
// these reports waits for the renderer's GPU thread, behind the work it already recorded, and GT7
// maps thousands of 64 KB pages of direct memory into free space when a race starts.
void ReleaseFixedOverlap(void* addr, size_t len, int flags) {
    DependencyScope dependency(99, len);
    constexpr int GuestMapNoOverwrite = 0x80;
    if (addr == nullptr || (flags & GuestMapFixedFlag) == 0) return;
    bool registered = false;
    bool overlaps = false;
    {
        DependencyScope allocation_wait(96);
        GuestAllocations::Mutation mutation;
        allocation_wait.Finish();
        registered = mutation.Overlaps(addr, len);
        overlaps = registered && !mutation.Covers(addr, len);
    }
    if (!registered) return;
    NotifyRenderer("RytyGpuUnmapMemory", addr, len);
    if (overlaps && (flags & GuestMapNoOverwrite) == 0) {
        Trace("fixed mapping %p+0x%zx replaces existing mappings", addr, len);
        DoMunmap(addr, len);
    }
}

int DoMapDirect(void** addr, size_t len, int prot, int flags, int64_t physStart, size_t alignment) {
    DependencyScope dependency(92, len);
    ValidateOutput(addr);
    if (len == 0 || (len & (PS5_PAGE_SIZE - 1)) != 0) return SCE_KERNEL_ERROR_EINVAL;
    if (physStart < 0 || (static_cast<std::uint64_t>(physStart) & (PS5_PAGE_SIZE - 1)) != 0 || static_cast<std::uint64_t>(physStart) >= DIRECT_MEMORY_SIZE || len > DIRECT_MEMORY_SIZE - static_cast<std::uint64_t>(physStart)) {
        return SCE_KERNEL_ERROR_EINVAL;
    }
    ReleaseFixedOverlap(*addr, len, flags);
    DependencyScope allocation_wait(96);
    GuestAllocations::Mutation mutation;
    allocation_wait.Finish();
    if (*addr != nullptr && (flags & GuestMapFixedFlag) != 0) FlushPendingUnmaps(*addr, len); // PERF-GT7-092
    if (RemapFixedIntoRegistered(mutation, *addr, len, prot, flags, physStart)) {
        RecordProtection(*addr, len, prot);
        NotifyRenderer("RytyGpuMapMemory", *addr, len);
        return 0;
    }
    if (*addr != nullptr && (flags & GuestMapFixedFlag) != 0) mutation.RequireAvailable(*addr, len);
    DependencyScope direct_wait(97);
    std::lock_guard lock(g_directLock);
    direct_wait.Finish();
    ValidatePhysicalRange(static_cast<std::uint64_t>(physStart), len);
    void* mapped = MapAligned(*addr, len, PROT_NONE, flags, alignment);
    try {
        AddMapping(reinterpret_cast<std::uintptr_t>(mapped), len, static_cast<std::uint64_t>(physStart), LinuxProtFromSce(prot));
        mutation.Add(mapped, len, (prot & 3) != 0, (prot & 2) != 0);
    } catch (...) {
        EraseMappings(reinterpret_cast<std::uintptr_t>(mapped), reinterpret_cast<std::uintptr_t>(mapped) + len);
        Unmap(mapped, len);
        throw;
    }
    *addr = mapped;
    RecordProtection(mapped, len, prot);
    Trace("map direct %p+0x%zx phys=0x%llx prot=0x%x flags=0x%x align=0x%zx", mapped, len, static_cast<unsigned long long>(physStart), prot, flags, alignment);
    NotifyRenderer("RytyGpuMapMemory", mapped, len);
    return 0;
}

namespace {

// With g_directLock held.
bool PhysicalRangeAllocated(std::uint64_t phys, std::size_t len) {
    for (std::size_t offset = 0; offset < len; offset += PS5_PAGE_SIZE) {
        if (!g_physPages.contains(phys + offset)) return false;
    }
    return true;
}

// With g_directLock held. Without coalesce each part goes through AddMapping, as in DoMapDirect.
// With coalesce a physically contiguous group of parts is one native view, but the direct mapping
// records stay one per part, as DoMapDirect leaves them (sceKernelVirtualQuery reports each part).
void AddRunMappings(std::uintptr_t address, const DirectMapPart* parts, std::size_t count, int nativeProt, bool coalesce) {
    std::uintptr_t cursor = address;
    for (std::size_t i = 0; i < count;) {
        const auto phys = static_cast<std::uint64_t>(parts[i].phys);
        std::size_t bytes = parts[i].length;
        std::size_t next = i + 1;
        while (coalesce && next < count && static_cast<std::uint64_t>(parts[next].phys) == phys + bytes) {
            bytes += parts[next].length;
            ++next;
        }
        if (next == i + 1) {
            AddMapping(cursor, bytes, phys, nativeProt);
        } else {
            // AddMapping's native views for the group, then its records split at the parts.
            AddMapping(cursor, bytes, phys, nativeProt);
            EraseMappings(cursor, cursor + bytes);
            std::uintptr_t partAddress = cursor;
            for (std::size_t k = i; k < next; ++k) {
                const auto partPhys = static_cast<std::uint64_t>(parts[k].phys);
                const auto& page = g_physPages.at(partPhys);
                g_directMappings.emplace(partAddress, DirectMapping{partAddress + parts[k].length, partPhys, page.backing->MemoryType(), page.backing});
                partAddress += parts[k].length;
            }
        }
        cursor += bytes;
        i = next;
    }
}

// The allocation registry gets one range per part, as DoMapDirect leaves it (a later partial unmap
// and sceKernelVirtualQuery then see what they saw before the runs).
void AddRunAllocations(GuestAllocations::Mutation& mutation, std::uintptr_t address, const DirectMapPart* parts, std::size_t count, int prot) {
    std::uintptr_t cursor = address;
    for (std::size_t i = 0; i < count; ++i) {
        mutation.Add(reinterpret_cast<void*>(cursor), parts[i].length, (prot & 3) != 0, (prot & 2) != 0);
        cursor += parts[i].length;
    }
}

}

// PERF-GT7-090: GT7 streams textures with sceKernelBatchMap2 batches of about 1,000 fixed 64 KB
// direct mappings and 100 unmaps (Trial Mountain's landmark: 43-48 ms on the game's job thread,
// which its render thread waits for). Taken one by one, each part paid the allocation lock, the
// overlap checks, the protection record and the renderer's report (its locks, its region lookups,
// the page warmer's wake-up) on top of the native mapping itself: about half of the batch. A run
// of consecutive parts takes the path DoMapDirect takes for each of them: all over registered
// memory (remapped in place) or all over free space. Covers() holds for the run exactly when it
// holds for every part, the parts being contiguous; a run over both kinds, or with a part
// DoMapDirect would refuse, goes back part by part.
int DoMapDirectRun(void* addr, size_t len, int prot, int flags, const DirectMapPart* parts, size_t count, bool coalesce) {
    DependencyScope dependency(92, len);
    constexpr int GuestMapNoOverwrite = 0x80;
    if (addr == nullptr || parts == nullptr || count < 2 || (flags & GuestMapFixedFlag) == 0) return 1;
    const auto address = reinterpret_cast<std::uintptr_t>(addr);
    if (len == 0 || (len & (PS5_PAGE_SIZE - 1)) != 0 || (address & (PS5_PAGE_SIZE - 1)) != 0) return 1;
    if (len > std::numeric_limits<std::uintptr_t>::max() - address) return 1;
    if ((prot & ~0xF7) != 0) return 1; // LinuxProtFromSce throws: the first part reports it
    std::size_t total = 0;
    for (std::size_t i = 0; i < count; ++i) {
        const auto& part = parts[i];
        if (part.length == 0 || (part.length & (PS5_PAGE_SIZE - 1)) != 0) return 1;
        if (part.phys < 0 || (static_cast<std::uint64_t>(part.phys) & (PS5_PAGE_SIZE - 1)) != 0 || static_cast<std::uint64_t>(part.phys) >= DIRECT_MEMORY_SIZE || part.length > DIRECT_MEMORY_SIZE - static_cast<std::uint64_t>(part.phys)) return 1;
        total += part.length;
    }
    if (total != len) return 1;
    const auto nativeProtection = LinuxProtFromSce(prot);
    bool registered = false;
    {
        DependencyScope allocation_wait(96);
        GuestAllocations::Mutation mutation;
        allocation_wait.Finish();
        registered = mutation.Overlaps(addr, len);
        if (registered && (!mutation.Covers(addr, len) || (flags & GuestMapNoOverwrite) != 0)) return 1;
    }
    {
        DependencyScope direct_wait(97);
        std::lock_guard lock(g_directLock);
        direct_wait.Finish();
        for (std::size_t i = 0; i < count; ++i) {
            if (!PhysicalRangeAllocated(static_cast<std::uint64_t>(parts[i].phys), parts[i].length)) return 1;
        }
    }
    if (registered) {
        // ReleaseFixedOverlap for a covered range: the renderer drops its copies, nothing is unmapped.
        NotifyRenderer("RytyGpuUnmapMemory", addr, len);
        DependencyScope allocation_wait(96);
        GuestAllocations::Mutation mutation;
        allocation_wait.Finish();
        FlushPendingUnmaps(addr, len); // PERF-GT7-092 (not expected over registered memory)
        // Changed by another thread since: the parts go one by one (the report above only made
        // the renderer drop copies their own reports drop again).
        if (!mutation.Covers(addr, len)) return 1;
        Trace("remap fixed run %p+0x%zx prot=0x%x parts=%zu", addr, len, prot, count);
        mutation.Protect(addr, len, (prot & 3) != 0, (prot & 2) != 0, [&] {
            DependencyScope direct_wait(97);
            std::lock_guard lock(g_directLock);
            direct_wait.Finish();
            AddRunMappings(address, parts, count, nativeProtection, coalesce);
        });
        RecordProtection(addr, len, prot);
        NotifyRenderer("RytyGpuMapMemory", addr, len);
        return 0;
    }
    DependencyScope allocation_wait(96);
    GuestAllocations::Mutation mutation;
    allocation_wait.Finish();
    FlushPendingUnmaps(addr, len); // PERF-GT7-092
    if (mutation.Overlaps(addr, len)) return 1; // registered by another thread since
    DependencyScope direct_wait(97);
    std::lock_guard lock(g_directLock);
    direct_wait.Finish();
    for (std::size_t i = 0; i < count; ++i) {
        if (!PhysicalRangeAllocated(static_cast<std::uint64_t>(parts[i].phys), parts[i].length)) return 1;
    }
    void* mapped = MapAligned(addr, len, PROT_NONE, flags, 0);
    bool registeredParts = false;
    try {
        AddRunMappings(reinterpret_cast<std::uintptr_t>(mapped), parts, count, nativeProtection, coalesce);
        registeredParts = true;
        AddRunAllocations(mutation, reinterpret_cast<std::uintptr_t>(mapped), parts, count, prot);
    } catch (...) {
        if (registeredParts) {
            // The parts registered before the failure go again (nothing native is unmapped twice).
            try { mutation.Unmap(mapped, len, [](const void*, std::size_t, const void*, bool) {}); } catch (...) {}
        }
        EraseMappings(reinterpret_cast<std::uintptr_t>(mapped), reinterpret_cast<std::uintptr_t>(mapped) + len);
        Unmap(mapped, len);
        throw;
    }
    RecordProtection(mapped, len, prot);
    Trace("map direct run %p+0x%zx prot=0x%x flags=0x%x parts=%zu", mapped, len, prot, flags, count);
    NotifyRenderer("RytyGpuMapMemory", mapped, len);
    return 0;
}

int DoMapAnon(void** addr, size_t len, int prot, int flags) {
    DependencyScope dependency(93, len);
    ValidateOutput(addr);
    if (len == 0 || (len & (PS5_PAGE_SIZE - 1)) != 0) return SCE_KERNEL_ERROR_EINVAL;
    ReleaseFixedOverlap(*addr, len, flags);
    DependencyScope allocation_wait(96);
    GuestAllocations::Mutation mutation;
    allocation_wait.Finish();
    if (*addr != nullptr && (flags & GuestMapFixedFlag) != 0) FlushPendingUnmaps(*addr, len); // PERF-GT7-092
    if (RemapFixedIntoRegistered(mutation, *addr, len, prot, flags)) {
        RecordProtection(*addr, len, prot);
        NotifyRenderer("RytyGpuMapMemory", *addr, len);
        return 0;
    }
    if (*addr != nullptr && (flags & GuestMapFixedFlag) != 0) mutation.RequireAvailable(*addr, len);
    void* mapped = MapAligned(*addr, len, LinuxProtFromSce(prot), flags, PS5_PAGE_SIZE);
    try {
        mutation.Add(mapped, len, (prot & 3) != 0, (prot & 2) != 0);
    } catch (...) {
        Unmap(mapped, len);
        throw;
    }
    *addr = mapped;
    RecordProtection(mapped, len, prot);
    Trace("map anon %p+0x%zx prot=0x%x flags=0x%x", mapped, len, prot, flags);
    NotifyRenderer("RytyGpuMapMemory", mapped, len);
    return 0;
}

int DoMprotect(const void* addr, size_t len, int prot) {
    Trace("protect %p+0x%zx prot=0x%x", addr, len, prot);
    const auto address = reinterpret_cast<std::uintptr_t>(addr);
    constexpr auto pageMask = static_cast<std::uintptr_t>(PS5_PAGE_SIZE - 1);
    const auto limit = std::numeric_limits<std::uintptr_t>::max();
    if (address == 0 || len == 0 || len > limit - address || address + len > limit - pageMask) throw std::invalid_argument("Invalid guest memory protection range");
    const auto first = address & ~pageMask;
    const auto end = (address + len + pageMask) & ~pageMask;
    const auto bytes = static_cast<std::size_t>(end - first);
    // Like the PS4 kernel, an unchanged protection changes nothing. GT Sport re-applies read-write
    // to its 2 MB pool chunks during play; re-protecting them dropped the renderer's write
    // tracking there, so later CPU writes no longer reached its caches (wrong glyphs).
    // Only in the guest arena, where the pools are: elsewhere (the kernel arena commits pages on
    // mprotect) the recorded protection need not match what is committed.
    constexpr std::uintptr_t GuestArenaBase = 0x200000000;
    constexpr std::uintptr_t GuestArenaEnd = 0xFC00000000;
    if (first >= GuestArenaBase && end <= GuestArenaEnd && ProtectionUnchanged(first, end, prot)) return 0;
    const auto* pointer = reinterpret_cast<const void*>(first);
    const auto nativeProtection = LinuxProtFromSce(prot);
    DependencyScope allocation_wait(96);
    GuestAllocations::Mutation mutation;
    allocation_wait.Finish();
    FlushPendingUnmaps(pointer, bytes); // PERF-GT7-092
#ifdef _WIN32
    MEMORY_BASIC_INFORMATION memory{};
    if (VirtualQuery(pointer, &memory, sizeof(memory)) != sizeof(memory)) throw std::runtime_error("Cannot query guest memory protection range");
    if (memory.Type == MEM_IMAGE) {
        if (memory.AllocationBase != GetModuleHandleW(nullptr)) throw std::invalid_argument("Memory protection of a foreign image is not supported");
        mutation.RegisterMainImage();
    }
#else
    mutation.RegisterMainImage();
#endif
    mutation.Protect(pointer, bytes, (prot & 3) != 0, (prot & 2) != 0, [&] {
        if (mprotect(const_cast<void*>(pointer), bytes, nativeProtection) != 0) throw std::system_error(errno, std::generic_category(), "mprotect failed");
    });
    RecordProtection(pointer, bytes, prot);
    NotifyRenderer("RytyGpuProtectionChanged", pointer, bytes);
    return 0;
}

int DoMunmap(void* addr, size_t len) {
    DependencyScope dependency(94, len);
    Trace("unmap %p+0x%zx", addr, len);
    if (len == 0 || (len & (PS5_PAGE_SIZE - 1)) != 0 || !addr) return SCE_KERNEL_ERROR_EINVAL;
    NotifyRenderer("RytyGpuUnmapMemory", addr, len);
    DependencyScope allocation_wait(96);
    GuestAllocations::Mutation mutation;
    allocation_wait.Finish();
    FlushPendingUnmaps(addr, len); // PERF-GT7-092
    // PERF-GT7-090 (RYGT_BATCHMAP_MERGE=2): adjacent guest arena pieces go to libc as one range
    // once the registry is done (the same unmap: a run mapped as one native view and released part
    // by part was split again for every part, quadratic in its length).
    static const bool mergePieces = [] {
        const char* value = std::getenv("RYGT_BATCHMAP_MERGE");
        return value && std::atoi(value) >= 2;
    }();
    std::uintptr_t arenaBegin = 0;
    std::uintptr_t arenaEnd = 0;
    const auto flushArena = [&] {
        if (arenaEnd == arenaBegin) return;
        munmap(reinterpret_cast<void*>(arenaBegin), arenaEnd - arenaBegin);
        arenaBegin = arenaEnd = 0;
    };
    mutation.Unmap(addr, len, [&](const void* piece, std::size_t pieceBytes, const void* allocation, bool last) {
        auto* pieceAddress = const_cast<void*>(piece);
        DependencyScope direct_wait(97);
        std::lock_guard lock(g_directLock);
        direct_wait.Finish();
        EraseMappings(reinterpret_cast<std::uintptr_t>(piece), reinterpret_cast<std::uintptr_t>(piece) + pieceBytes);
#if defined(__linux__)
        Unmap(pieceAddress, pieceBytes);
#else
        if (KernelArena::Get().Contains(pieceAddress, pieceBytes) && !mergePieces) {
            munmap(pieceAddress, pieceBytes);
            return;
        }
        if (KernelArena::Get().Contains(pieceAddress, pieceBytes)) {
            const auto begin = reinterpret_cast<std::uintptr_t>(piece);
            if (arenaEnd != begin) flushArena();
            if (arenaEnd == arenaBegin) arenaBegin = begin;
            arenaEnd = begin + pieceBytes;
            return;
        }
        flushArena();
        if (last) munmap_release(const_cast<void*>(allocation));
        else munmap(pieceAddress, pieceBytes);
#endif
    });
    {
        std::lock_guard lock(g_directLock);
        flushArena();
    }
    RecordProtection(addr, len, -1);
    // The renderer was told before the unmap so it stops using the memory; this tells it the
    // pages are gone, so answers it cached in between are dropped.
    NotifyRenderer("RytyGpuMappingChanged", addr, len);
    return 0;
}

// PERF-GT7-092 (RYGT_DEFER_UNMAP=1): GT7's streaming batches unmap about 1,000 64 KB views on the
// job thread its render thread waits for (Trial Mountain's landmark: ~8 ms of UnmapViewOfFile2
// and placeholder coalescing, each view a TLB flush on every core). The guest-visible unmap stays
// immediate (allocation registry, direct mappings, protection records, and the renderer's report,
// which may still use the memory); only the native views are removed later, on a worker thread,
// once the batches have paused. Until then the old views keep the physical pages mapped where the
// game no longer looks. Anything placing or protecting memory over a pending range finishes its
// removal first (FlushPendingUnmaps), and the arena does not hand the range out meanwhile (it is
// released with the views).
namespace {

struct PendingUnmap {
    std::uintptr_t begin = 0;
    std::uintptr_t end = 0;
    std::vector<std::pair<void*, std::size_t>> pieces;
    std::chrono::steady_clock::time_point queued{};
};

std::mutex g_pendingLock;
std::condition_variable g_pendingWake;
std::deque<PendingUnmap> g_pendingUnmaps;
std::uintptr_t g_inflightBegin = 0;
std::uintptr_t g_inflightEnd = 0;
std::mutex g_unmapWork; // held while a pending unmap is carried out
std::atomic<long long> g_lastBatchNs{0};
std::atomic<unsigned long long> g_deferredCount{0};
std::atomic<unsigned long long> g_flushedEarly{0};

long long SteadyNs() {
    return std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}

void CarryOutPending(PendingUnmap& entry) {
    {
        // Adjacent pieces as one range (see DoMunmap).
        std::lock_guard lock(g_directLock);
        std::uintptr_t begin = 0;
        std::uintptr_t end = 0;
        for (const auto& [piece, bytes] : entry.pieces) {
            const auto start = reinterpret_cast<std::uintptr_t>(piece);
            if (start != end && end != begin) {
                munmap(reinterpret_cast<void*>(begin), end - begin);
                begin = end = 0;
            }
            if (end == begin) begin = start;
            end = start + bytes;
        }
        if (end != begin) munmap(reinterpret_cast<void*>(begin), end - begin);
    }
    NotifyRenderer("RytyGpuMappingChanged", reinterpret_cast<void*>(entry.begin), entry.end - entry.begin);
}

void UnmapWorker() {
#ifdef _WIN32
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_BELOW_NORMAL);
    using SetDescription = HRESULT(WINAPI*)(HANDLE, PCWSTR);
    if (const auto set = reinterpret_cast<SetDescription>(reinterpret_cast<void*>(GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "SetThreadDescription")))) set(GetCurrentThread(), L"Ryty:UnmapLater");
#endif
    for (;;) {
        {
            std::unique_lock lock(g_pendingLock);
            g_pendingWake.wait(lock, [] { return !g_pendingUnmaps.empty(); });
        }
        // The game's batches first: 1 ms without one, or 30 ms after the oldest pending unmap.
        for (;;) {
            long long oldest = 0;
            {
                std::lock_guard lock(g_pendingLock);
                if (g_pendingUnmaps.empty()) break;
                oldest = std::chrono::duration_cast<std::chrono::nanoseconds>(g_pendingUnmaps.front().queued.time_since_epoch()).count();
            }
            const long long now = SteadyNs();
            if (now - g_lastBatchNs.load(std::memory_order_relaxed) > 1'000'000 || now - oldest > 30'000'000) break;
            std::this_thread::sleep_for(std::chrono::microseconds(500));
        }
        std::lock_guard work(g_unmapWork);
        PendingUnmap entry;
        {
            std::lock_guard lock(g_pendingLock);
            if (g_pendingUnmaps.empty()) continue;
            entry = std::move(g_pendingUnmaps.front());
            g_pendingUnmaps.pop_front();
            g_inflightBegin = entry.begin;
            g_inflightEnd = entry.end;
        }
        CarryOutPending(entry);
        std::lock_guard lock(g_pendingLock);
        g_inflightBegin = 0;
        g_inflightEnd = 0;
    }
}

}

bool DeferUnmapEnabled() {
    static const bool enabled = [] {
        const char* value = std::getenv("RYGT_DEFER_UNMAP");
        const bool on = value && value[0] == '1';
        if (on) std::fprintf(stderr, "[memory] PERF-GT7-092: native views of batch unmaps are removed later (RYGT_DEFER_UNMAP=1)\n");
        return on;
    }();
    return enabled;
}

void NoteBatchMapActivity() {
    if (DeferUnmapEnabled()) g_lastBatchNs.store(SteadyNs(), std::memory_order_relaxed);
}

bool PendingUnmapContains(std::uintptr_t address) {
    if (!DeferUnmapEnabled()) return false;
    std::lock_guard lock(g_pendingLock);
    if (address >= g_inflightBegin && address < g_inflightEnd) return true;
    for (const auto& entry : g_pendingUnmaps) {
        if (address >= entry.begin && address < entry.end) return true;
    }
    return false;
}

void FlushPendingUnmaps(const void* addr, size_t len) {
    if (!DeferUnmapEnabled() || len == 0) return;
    const auto begin = reinterpret_cast<std::uintptr_t>(addr);
    const auto end = begin + len;
    {
        std::lock_guard lock(g_pendingLock);
        bool any = g_inflightEnd > begin && g_inflightBegin < end;
        for (const auto& entry : g_pendingUnmaps) {
            if (any) break;
            any = entry.end > begin && entry.begin < end;
        }
        if (!any) return;
    }
    std::lock_guard work(g_unmapWork); // the worker's unmap in flight finishes first
    std::vector<PendingUnmap> mine;
    {
        std::lock_guard lock(g_pendingLock);
        for (auto it = g_pendingUnmaps.begin(); it != g_pendingUnmaps.end();) {
            if (it->end > begin && it->begin < end) {
                mine.push_back(std::move(*it));
                it = g_pendingUnmaps.erase(it);
            } else {
                ++it;
            }
        }
    }
    for (auto& entry : mine) CarryOutPending(entry);
    g_flushedEarly.fetch_add(mine.size(), std::memory_order_relaxed);
}

int DoMunmapDeferred(void* addr, size_t len) {
    if (!DeferUnmapEnabled()) return DoMunmap(addr, len);
    if (len == 0 || (len & (PS5_PAGE_SIZE - 1)) != 0 || !addr) return SCE_KERNEL_ERROR_EINVAL;
#ifdef _WIN32
    // Only guest arena memory (all of GT7's streaming pools); anything else as before.
    if (!KernelArena::Get().Contains(addr, len)) return DoMunmap(addr, len);
#else
    return DoMunmap(addr, len);
#endif
    DependencyScope dependency(94, len);
    Trace("unmap (native views later) %p+0x%zx", addr, len);
    NotifyRenderer("RytyGpuUnmapMemory", addr, len);
    static std::once_flag started;
    std::call_once(started, [] { std::thread(UnmapWorker).detach(); });
    PendingUnmap entry;
    entry.begin = reinterpret_cast<std::uintptr_t>(addr);
    entry.end = entry.begin + len;
    bool queued = false;
    {
        DependencyScope allocation_wait(96);
        GuestAllocations::Mutation mutation;
        allocation_wait.Finish();
        FlushPendingUnmaps(addr, len); // an older pending unmap over the range (not expected)
        try {
            mutation.Unmap(addr, len, [&](const void* piece, std::size_t pieceBytes, const void*, bool) {
                DependencyScope direct_wait(97);
                std::lock_guard lock(g_directLock);
                direct_wait.Finish();
                EraseMappings(reinterpret_cast<std::uintptr_t>(piece), reinterpret_cast<std::uintptr_t>(piece) + pieceBytes);
                entry.pieces.emplace_back(const_cast<void*>(piece), pieceBytes);
            });
        } catch (...) {
            CarryOutPending(entry); // the pieces already taken off the registry go now
            throw;
        }
        // Queued before the registry is released: a map over the range by another thread takes the
        // registry after this and finds the entry (FlushPendingUnmaps inside its Mutation).
        if (!entry.pieces.empty()) {
            entry.queued = std::chrono::steady_clock::now();
            std::lock_guard lock(g_pendingLock);
            g_pendingUnmaps.push_back(std::move(entry));
            queued = true;
        }
    }
    RecordProtection(addr, len, -1);
    if (!queued) return 0;
    g_pendingWake.notify_one();
    const auto n = g_deferredCount.fetch_add(1, std::memory_order_relaxed) + 1;
    if (n <= 3 || n % 2000 == 0) {
        std::fprintf(stderr, "[memory] PERF-GT7-092: %llu unmaps deferred, %llu finished early by a map over them\n", n, g_flushedEarly.load());
    }
    return 0;
}

int DoReserveVirtual(void** addr, size_t len, int flags, size_t alignment) {
    ValidateOutput(addr);
    if (len == 0 || (len & (PS5_PAGE_SIZE - 1)) != 0) return SCE_KERNEL_ERROR_EINVAL;
    DependencyScope allocation_wait(96);
    GuestAllocations::Mutation mutation;
    allocation_wait.Finish();
    const bool fixed = *addr != nullptr && (flags & GuestMapFixedFlag) != 0;
    if (fixed) FlushPendingUnmaps(*addr, len); // PERF-GT7-092
    if (fixed) mutation.RequireAvailable(*addr, len);
    constexpr int GuestMapNoCoalesce = 0x400000;
    void* mapped = MapAligned(fixed ? *addr : nullptr, len, PROT_NONE, fixed ? GuestMapFixedFlag | (flags & GuestMapNoCoalesce) : 0, alignment);
    try {
        mutation.Add(mapped, len, false, false);
    } catch (...) {
        Unmap(mapped, len);
        throw;
    }
    *addr = mapped;
    Trace("reserve %p+0x%zx flags=0x%x align=0x%zx", mapped, len, flags, alignment);
    return 0;
}

void CreateDirectMemoryBacking(int64_t start, size_t len, int memoryType) {
    DependencyScope direct_wait(97);
    std::lock_guard lock(g_directLock);
    direct_wait.Finish();
    const auto first = static_cast<std::uint64_t>(start);
    for (std::size_t offset = 0; offset < len; offset += PS5_PAGE_SIZE) {
        if (g_physPages.contains(first + offset)) throw std::runtime_error("physical allocation overlaps live direct memory");
    }
    const auto backing = std::make_shared<PhysicalBacking>(len, memoryType);
    std::map<std::uint64_t, PhysicalPage> pages;
    for (std::size_t offset = 0; offset < len; offset += PS5_PAGE_SIZE) pages.emplace(first + offset, PhysicalPage{backing, offset});
    g_physPages.merge(pages);
    Trace("allocate physical 0x%llx+0x%zx", static_cast<unsigned long long>(first), len);
}

// Releasing direct memory on a PS4 also unmaps every virtual mapping of it; titles release
// without unmapping first (GT Sport maps, queries and releases scratch blocks this way).
void UnmapDirectMemory(int64_t start, size_t len) {
    const auto first = static_cast<std::uint64_t>(start);
    const auto last = first + len;
    std::vector<std::pair<std::uintptr_t, std::size_t>> ranges;
    {
        DependencyScope direct_wait(97);
        std::lock_guard lock(g_directLock);
        direct_wait.Finish();
        for (const auto& [base, mapping] : g_directMappings) {
            const auto mappingEnd = mapping.phys + (mapping.end - base);
            const auto overlapFirst = std::max(first, mapping.phys);
            const auto overlapLast = std::min(last, mappingEnd);
            if (overlapFirst >= overlapLast) continue;
            ranges.emplace_back(base + (overlapFirst - mapping.phys), overlapLast - overlapFirst);
        }
    }
    for (const auto& [address, bytes] : ranges) {
        Trace("release unmaps %p+0x%zx", reinterpret_cast<void*>(address), bytes);
        DoMunmap(reinterpret_cast<void*>(address), bytes);
    }
}

void ForgetDirectMemory(int64_t start, size_t len) {
    const auto first = static_cast<std::uint64_t>(start);
    DependencyScope direct_wait(97);
    std::lock_guard lock(g_directLock);
    direct_wait.Finish();
    ValidatePhysicalRange(first, len);
    g_physPages.erase(g_physPages.lower_bound(first), g_physPages.lower_bound(first + len));
    Trace("release physical 0x%llx+0x%zx", static_cast<unsigned long long>(first), len);
}

bool GuestProtection(uintptr_t addr, int* prot) {
    std::lock_guard lock(g_protectionLock);
    auto next = g_protections.upper_bound(addr);
    if (next == g_protections.begin()) return false;
    const auto containing = std::prev(next);
    if (addr >= containing->second.end) return false;
    *prot = containing->second.prot;
    return true;
}

bool QueryDirectMapping(std::uintptr_t address, std::uintptr_t* start, std::uintptr_t* end, std::uint64_t* offset, int* memoryType) {
    DependencyScope direct_wait(97);
    std::lock_guard lock(g_directLock);
    direct_wait.Finish();
    const auto next = g_directMappings.upper_bound(address);
    if (next == g_directMappings.begin()) return false;
    const auto it = std::prev(next);
    if (address >= it->second.end) return false;
    *start = it->first;
    *end = it->second.end;
    *offset = it->second.phys;
    *memoryType = it->second.memoryType;
    return true;
}
