#include <atomic>
#include <cstdint>
#include <cstddef>
#include <cstring>
#include "SceTypes.hpp"
#include "prx/libc/include/General.hpp"
#include "prx/libc/include/GuestAllocations.hpp"
#include "prx/libkernel/DirectMemory/DirectMemory.hpp"
#include "prx/libkernel/KernelErrors.hpp"
#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#else
#include <fstream>
#include <sstream>
#include <sys/mman.h>
#endif
#include <algorithm>
#include <stdexcept>
#include <string>
#include <iterator>
#include <map>
#include <cstdio>
#include <mutex>
#include <cstdlib>
#include <vector>
#include <chrono>

namespace {

constexpr size_t FLEXIBLE_MEMORY_SIZE = 448ULL * 1024 * 1024;

constexpr int PRT_APERTURE_COUNT = 3;

struct PrtAperture {
    void* address;
    size_t length;
};

struct NamedRange {
 uintptr_t end;
 std::string name;
};

std::mutex g_rangeNameLock;
std::map<uintptr_t, NamedRange> g_rangeNames;

void EraseRangeNames(uintptr_t start, uintptr_t end) {
 auto it = g_rangeNames.lower_bound(start);
 if (it != g_rangeNames.begin() && std::prev(it)->second.end > start) --it;
 while (it != g_rangeNames.end() && it->first < end) {
  const auto rangeStart = it->first;
  const auto range = it->second;
  it = g_rangeNames.erase(it);
  if (rangeStart < start) g_rangeNames.emplace(rangeStart, NamedRange{start, range.name});
  if (range.end > end) it = g_rangeNames.emplace(end, NamedRange{range.end, range.name}).first;
 }
}

void ApplyRangeName(uintptr_t address, VirtualQueryInfo* info) {
 std::lock_guard lock(g_rangeNameLock);
 auto next = g_rangeNames.upper_bound(address);
 if (next != g_rangeNames.begin()) {
  const auto containing = std::prev(next);
  if (address < containing->second.end) {
   info->start = std::max<uintptr_t>(info->start, containing->first);
   info->end = std::min<uintptr_t>(info->end, containing->second.end);
   std::strncpy(info->name, containing->second.name.c_str(), sizeof(info->name) - 1);
   return;
  }
  info->start = std::max<uintptr_t>(info->start, containing->second.end);
 }
 if (next != g_rangeNames.end()) info->end = std::min<uintptr_t>(info->end, next->first);
}

std::mutex g_prtLock;
PrtAperture g_prtApertures[PRT_APERTURE_COUNT] = {};

std::mutex g_flexibleLock;
std::map<uintptr_t, size_t> g_flexibleRanges;

size_t _flexibleUsedLocked() {
    size_t used = 0;
    for (const auto& [start, len] : g_flexibleRanges) used += len;
    return used;
}

int _mapFlexible(void** addr, size_t len, int prot, int flags) {
    std::lock_guard lock(g_flexibleLock);
    if (len > FLEXIBLE_MEMORY_SIZE - std::min(FLEXIBLE_MEMORY_SIZE, _flexibleUsedLocked())) {
        std::fprintf(stderr, "[memory] flexible memory exhausted: request 0x%zx with 0x%zx of 0x%zx in use\n", len, _flexibleUsedLocked(), static_cast<size_t>(FLEXIBLE_MEMORY_SIZE));
        return SCE_KERNEL_ERROR_ENOMEM;
    }
    const int result = DoMapAnon(addr, len, prot, flags);
    if (result == 0) g_flexibleRanges[reinterpret_cast<uintptr_t>(*addr)] = len;
    return result;
}

void _releaseFlexible(uintptr_t start, size_t len) {
    std::lock_guard lock(g_flexibleLock);
    const uintptr_t end = start + len;
    auto it = g_flexibleRanges.upper_bound(start);
    if (it != g_flexibleRanges.begin()) --it;
    while (it != g_flexibleRanges.end() && it->first < end) {
        const uintptr_t rangeStart = it->first;
        const uintptr_t rangeEnd = rangeStart + it->second;
        if (rangeEnd <= start) { ++it; continue; }
        it = g_flexibleRanges.erase(it);
        if (rangeStart < start) g_flexibleRanges[rangeStart] = start - rangeStart;
        if (rangeEnd > end) g_flexibleRanges[end] = rangeEnd - end;
    }
}

}

// A Ryty error thrown inside a memory call reached the game's code, which cannot unwind it, and
// ended the process (Lance's crash leaving a race for the home menu, 5,500 map changes in the last
// 2 s). The call now fails with an error the game can handle, and the reason is logged.
template <typename F>
int GuardMemoryCall(const char* name, F&& call) {
    try {
        return call();
    } catch (const std::bad_alloc&) {
        std::fprintf(stderr, "[memory] %s: out of host memory\n", name);
        return SCE_KERNEL_ERROR_ENOMEM;
    } catch (const std::exception& error) {
        static std::atomic<unsigned> logged{0};
        if (logged.fetch_add(1) < 50) {
            std::fprintf(stderr, "[memory] %s failed: %s\n", name, error.what());
        }
        return SCE_KERNEL_ERROR_EINVAL;
    }
}

extern "C" {

int APS5_VABI sceKernelAllocateDirectMemory(int64_t search_start, int64_t search_end, size_t len, size_t alignment, int memory_type, int64_t* phys_addr_out) {
 if (search_start < 0 || search_end <= search_start || len == 0
  || (len & (PS5_PAGE_SIZE - 1)) || !phys_addr_out
  || (alignment != 0 && (alignment & (PS5_PAGE_SIZE - 1))))
  return SCE_KERNEL_ERROR_EINVAL;
 const int result = DirectMemoryAlloc(search_start, search_end, len, alignment, memory_type, phys_addr_out);
 if (result != 0) std::fprintf(stderr, "[memory] allocate direct 0x%llx-0x%llx len=0x%zx align=0x%zx type=%d failed: 0x%x\n", static_cast<long long>(search_start), static_cast<long long>(search_end), len, alignment, memory_type, result);
 return result;
}

int APS5_VABI sceKernelAllocateMainDirectMemory(size_t len, size_t alignment, int memory_type, int64_t* phys_addr_out) {
 return sceKernelAllocateDirectMemory(0, static_cast<int64_t>(DIRECT_MEMORY_SIZE), len, alignment, memory_type, phys_addr_out);
}

int APS5_VABI sceKernelAvailableDirectMemorySize(int64_t search_start, int64_t search_end, size_t alignment, int64_t* phys_addr_out, size_t* size_out) {
 if (!phys_addr_out || !size_out) return SCE_KERNEL_ERROR_EINVAL;
 int64_t tmpPhys = 0;
 int ret = DirectMemoryAlloc(search_start, search_end, PS5_PAGE_SIZE, alignment, -1, &tmpPhys);
 if (ret != 0) { *phys_addr_out = 0; *size_out = 0; return ret; }
 DirectMemoryFree(tmpPhys, PS5_PAGE_SIZE);
 *phys_addr_out = tmpPhys;
 *size_out = DirectMemoryFreeRun(static_cast<uint64_t>(tmpPhys), static_cast<uint64_t>(search_end));
 return 0;
}

int APS5_VABI sceKernelDirectMemoryQuery(int64_t offset, int flags, void* info, size_t info_size) {
 constexpr int SCE_KERNEL_DMQ_FIND_NEXT = 1;
 if (!info || offset < 0) return SCE_KERNEL_ERROR_EINVAL;
 struct DirectMemoryQueryInfo { int64_t start; int64_t end; int memory_type; };
 if (info_size < sizeof(DirectMemoryQueryInfo)) return SCE_KERNEL_ERROR_EINVAL;
 auto* q = static_cast<DirectMemoryQueryInfo*>(info);
 if (!DirectMemoryFind(offset, (flags & SCE_KERNEL_DMQ_FIND_NEXT) != 0, &q->start, &q->end, &q->memory_type)) return SCE_KERNEL_ERROR_EACCES;
 return 0;
}

size_t APS5_VABI sceKernelGetDirectMemorySize(void) {
 return DIRECT_MEMORY_SIZE;
}

int APS5_VABI sceKernelMapDirectMemory(void** addr, size_t len, int prot, int flags, int64_t direct_memory_start, size_t alignment) {
 return GuardMemoryCall("sceKernelMapDirectMemory", [&]() -> int {
 void* requested = addr ? *addr : nullptr;
 const int result = DoMapDirect(addr, len, prot, flags, direct_memory_start, alignment);
 if (result != 0) std::fprintf(stderr, "[memory] map direct %p len=0x%zx prot=0x%x flags=0x%x phys=0x%llx align=0x%zx failed: 0x%x\n", requested, len, prot, flags, static_cast<long long>(direct_memory_start), alignment, result);
 return result;
 });
}

int APS5_VABI sceKernelMapDirectMemory2(void** addr, size_t len, int type, int prot, int flags, int64_t direct_memory_start, size_t alignment) {
 return GuardMemoryCall("sceKernelMapDirectMemory2", [&]() -> int {
 (void)type;
 return DoMapDirect(addr, len, prot, flags, direct_memory_start, alignment);
 });
}

int APS5_VABI sceKernelMapFlexibleMemory(void** addr_in_out, size_t len, int prot, int flags) {
 return GuardMemoryCall("sceKernelMapFlexibleMemory", [&]() -> int {
 return _mapFlexible(addr_in_out, len, prot, flags);
 });
}

int APS5_VABI sceKernelSetVirtualRangeName(const void* addr, uint64_t len, const char* name);

int APS5_VABI sceKernelMapNamedDirectMemory(void** addr, size_t len, int prot, int flags, int64_t direct_memory_start, size_t alignment, const char* name) {
 return GuardMemoryCall("sceKernelMapNamedDirectMemory", [&]() -> int {
 const int result = DoMapDirect(addr, len, prot, flags, direct_memory_start, alignment);
 if (result == 0 && name) sceKernelSetVirtualRangeName(*addr, len, name);
 return result;
 });
}

int32_t APS5_VABI sceKernelMapNamedFlexibleMemory(void** addr_in_out, size_t len, int prot, int flags, const char* name) {
 return GuardMemoryCall("sceKernelMapNamedFlexibleMemory", [&]() -> int {
 const int result = _mapFlexible(addr_in_out, len, prot, flags);
 if (result == 0 && name) sceKernelSetVirtualRangeName(*addr_in_out, len, name);
 return result;
 });
}

int APS5_VABI sceKernelMprotect(const void* addr, size_t len, int prot) {
 return GuardMemoryCall("sceKernelMprotect", [&]() -> int {
 return DoMprotect(addr, len, prot);
 });
}

int APS5_VABI sceKernelMunmap(uint64_t vaddr, size_t len) {
 return GuardMemoryCall("sceKernelMunmap", [&]() -> int {
 const int result = DoMunmap(reinterpret_cast<void*>(vaddr), len);
 if (result == 0) _releaseFlexible(static_cast<uintptr_t>(vaddr), len);
 return result;
 });
}

int APS5_VABI sceKernelReleaseDirectMemory(int64_t start, size_t len) {
 return GuardMemoryCall("sceKernelReleaseDirectMemory", [&]() -> int {
 if (start < 0 || len == 0) return SCE_KERNEL_ERROR_EINVAL;
 UnmapDirectMemory(start, len);
 DirectMemoryFree(start, len);
 return 0;
 });
}

int APS5_VABI sceKernelReserveVirtualRange(void** addr, size_t len, int flags, size_t alignment) {
 return GuardMemoryCall("sceKernelReserveVirtualRange", [&]() -> int {
 void* requested = addr ? *addr : nullptr;
 const int result = DoReserveVirtual(addr, len, flags, alignment);
 if (result != 0) std::fprintf(stderr, "[memory] reserve %p len=0x%zx flags=0x%x align=0x%zx failed: 0x%x\n", requested, len, flags, alignment, result);
 return result;
 });
}

int APS5_VABI sceKernelVirtualQuery(const void* addr, int flags, VirtualQueryInfo* info, uint64_t info_size) {
 if (!info || info_size < sizeof(VirtualQueryInfo)) return SCE_KERNEL_ERROR_EINVAL;
 memset(info, 0, sizeof(VirtualQueryInfo));
 const auto address = reinterpret_cast<uintptr_t>(addr);
 // The mapping that contains the address, or with SCE_KERNEL_VQ_FIND_NEXT (flags bit 0) the first
 // mapping at or above it: titles walk their mappings and check that a mapping covers a whole
 // allocation, so the answer must be the registered allocation, not a page.
 constexpr int findNext = 1;
 const auto lease = GuestAllocations::GuestAllocationsAcquire_nid_postfix();
 const GuestAllocations::Range* best = nullptr;
 for (const auto& range : lease) {
  const auto begin = range->allocationAddress;
  const auto end = begin + range->allocationBytes;
  if (address >= begin && address < end) { best = range.get(); break; }
  if ((flags & findNext) != 0 && begin > address && (best == nullptr || begin < best->allocationAddress)) best = range.get();
 }
 if (best != nullptr) {
  info->start = best->allocationAddress;
  info->end = best->allocationAddress + best->allocationBytes;
  info->protection = (best->readable ? 1 : 0) | (best->writable ? 2 : 0) | (!best->releasable ? 4 : 0);
  int recorded = 0;
  if (GuestProtection(std::max<uintptr_t>(address, info->start), &recorded)) info->protection = recorded;
  std::uintptr_t directStart = 0;
  std::uintptr_t directEnd = 0;
  std::uint64_t physicalOffset = 0;
  int memoryType = 0;
  const bool direct = QueryDirectMapping(std::max<uintptr_t>(address, info->start), &directStart, &directEnd, &physicalOffset, &memoryType);
  info->is_direct = direct ? 1u : 0u;
  info->is_flexible = !direct && best->releasable ? 1u : 0u;
  if (direct) {
   info->start = std::max(info->start, directStart);
   info->end = std::min(info->end, directEnd);
   info->memory_type = memoryType;
  }
  info->is_committed = 1;
  ApplyRangeName(std::max<uintptr_t>(address, info->start), info);
  if (direct) info->offset = physicalOffset + info->start - directStart;
  return 0;
 }
 // Memory the registry does not know (the title's own heap blocks, stacks): the host's committed
 // region around the address is the honest extent; unmapped memory is an error, as on the PS5.
 #ifdef _WIN32
 MEMORY_BASIC_INFORMATION host{};
 // PERF-GT7-092: unmapped for the game, even while its native views wait for the worker.
 if (PendingUnmapContains(address)) return SCE_KERNEL_ERROR_EACCES;
 if (VirtualQuery(addr, &host, sizeof(host)) == 0 || host.State != MEM_COMMIT) return SCE_KERNEL_ERROR_EACCES;
 info->start = reinterpret_cast<uintptr_t>(host.BaseAddress);
 info->end = info->start + host.RegionSize;
 const bool writable = (host.Protect & (PAGE_READWRITE | PAGE_EXECUTE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_WRITECOPY)) != 0;
 const bool executable = (host.Protect & (PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY)) != 0;
 #else
 std::ifstream maps("/proc/self/maps");
 std::string line;
 bool found = false, writable = false, executable = false;
 while (std::getline(maps, line)) {
  std::istringstream fields(line);
  uintptr_t begin = 0, end = 0;
  char dash = 0;
  std::string perms;
  if (!(fields >> std::hex >> begin >> dash >> end >> perms) || address < begin || address >= end) continue;
  if (perms.size() < 3 || perms[0] != 'r') return SCE_KERNEL_ERROR_EACCES;
  info->start = begin;
  info->end = end;
  writable = perms[1] == 'w';
  executable = perms[2] == 'x';
  found = true;
  break;
 }
 if (!found) return SCE_KERNEL_ERROR_EACCES;
 #endif
 info->protection = 1 | (writable ? 2 : 0) | (executable ? 4 : 0);
 info->is_flexible = 1;
 info->is_committed = 1;
 ApplyRangeName(std::max<uintptr_t>(address, info->start), info);
 return 0;
}

// ---------------------------------------------------------------------------
// Moved as-is (not yet implemented) from the monolithic libkernel/Export.cpp.
// ---------------------------------------------------------------------------

// As sceKernelReleaseDirectMemory, but the whole range must be allocated.
int APS5_VABI sceKernelCheckedReleaseDirectMemory(int64_t start, size_t len) {
 return GuardMemoryCall("sceKernelCheckedReleaseDirectMemory", [&]() -> int {
 if (start < 0 || len == 0 || (static_cast<std::uint64_t>(start) & (PS5_PAGE_SIZE - 1)) != 0 || (len & (PS5_PAGE_SIZE - 1)) != 0) return SCE_KERNEL_ERROR_EINVAL;
 for (std::uint64_t offset = 0; offset < len;) {
  int64_t blockStart = 0;
  int64_t blockEnd = 0;
  int memoryType = 0;
  const auto address = static_cast<std::uint64_t>(start) + offset;
  if (!DirectMemoryFind(static_cast<int64_t>(address), false, &blockStart, &blockEnd, &memoryType) || static_cast<std::uint64_t>(blockStart) > address) return SCE_KERNEL_ERROR_ENOENT;
  offset = static_cast<std::uint64_t>(blockEnd) - static_cast<std::uint64_t>(start);
 }
 return sceKernelReleaseDirectMemory(start, len);
 });
}

int APS5_VABI sceKernelMtypeprotect(const void* addr, size_t len, int type, int prot) {
 return GuardMemoryCall("sceKernelMtypeprotect", [&]() -> int {
 (void)type;
 return DoMprotect(addr, len, prot);
 });
}

int APS5_VABI sceKernelQueryMemoryProtection(void* addr, void** start, void** end, int* prot) {
 VirtualQueryInfo info{};
 const int result = sceKernelVirtualQuery(addr, 0, &info, sizeof(info));
 if (result != 0) return result;
 if (start) *start = reinterpret_cast<void*>(info.start);
 if (end) *end = reinterpret_cast<void*>(info.end);
 if (prot) *prot = info.protection;
 return 0;
}

int APS5_VABI sceKernelIsStack(void* addr, void** start, void** end) {
 (void)addr;
 (void)start;
 (void)end;
 NotImplemented_nid_no_patch(__func__);
 return 0;
}

int APS5_VABI sceKernelAvailableFlexibleMemorySize(size_t* size) {
 if (!size) return SCE_KERNEL_ERROR_EINVAL;
 std::lock_guard lock(g_flexibleLock);
 *size = FLEXIBLE_MEMORY_SIZE - std::min(FLEXIBLE_MEMORY_SIZE, _flexibleUsedLocked());
 return 0;
}

int APS5_VABI sceKernelConfiguredFlexibleMemorySize(size_t* size) {
 if (!size) return SCE_KERNEL_ERROR_EINVAL;
 *size = FLEXIBLE_MEMORY_SIZE;
 return 0;
}

int APS5_VABI sceKernelSetVirtualRangeName(const void* addr, uint64_t len, const char* name) {
 const auto start = reinterpret_cast<uintptr_t>(addr);
 if (!addr || len == 0 || !name || len > UINTPTR_MAX - start) return SCE_KERNEL_ERROR_EINVAL;
 std::lock_guard lock(g_rangeNameLock);
 EraseRangeNames(start, start + len);
 g_rangeNames.emplace(start, NamedRange{start + len, std::string(name, strnlen(name, 31))});
 return 0;
}

int APS5_VABI sceKernelClearVirtualRangeName(const void* addr, uint64_t len) {
 const auto start = reinterpret_cast<uintptr_t>(addr);
 if (!addr || len == 0 || len > UINTPTR_MAX - start) return SCE_KERNEL_ERROR_EINVAL;
 std::lock_guard lock(g_rangeNameLock);
 EraseRangeNames(start, start + len);
 return 0;
}

int APS5_VABI sceKernelGetPageTableStats(int* cpu_total, int* cpu_available, int* gpu_total, int* gpu_available) {
 (void)cpu_total;
 (void)cpu_available;
 (void)gpu_total;
 (void)gpu_available;
 NotImplemented_nid_no_patch(__func__);
 return 0;
}

int APS5_VABI sceKernelGetPrtAperture(int index, void** addr, size_t* len) {
 if (index < 0 || index >= PRT_APERTURE_COUNT || !addr || !len) return SCE_KERNEL_ERROR_EINVAL;
 std::lock_guard lock(g_prtLock);
 *addr = g_prtApertures[index].address;
 *len = g_prtApertures[index].length;
 return 0;
}

int APS5_VABI sceKernelSetPrtAperture(int index, void* addr, size_t len) {
 if (index < 0 || index >= PRT_APERTURE_COUNT) return SCE_KERNEL_ERROR_EINVAL;
 std::lock_guard lock(g_prtLock);
 g_prtApertures[index] = {addr, len};
 return 0;
}

int APS5_VABI sceKernelBatchMap2(KernelBatchMapEntry* entries, int num_entries, int* num_entries_out, int flags);

int APS5_VABI sceKernelBatchMap(KernelBatchMapEntry* entries, int num_entries, int* num_entries_out) {
 constexpr int MapFixed = 0x10;
 return sceKernelBatchMap2(entries, num_entries, num_entries_out, MapFixed);
}

}

namespace {

constexpr int BatchOpMapDirect = 0;
constexpr int BatchOpUnmap = 1;
constexpr int BatchMapFixed = 0x10;

// PERF-GT7-090: RYGT_BATCHMAP_MERGE=1 maps each run of consecutive fixed direct mappings with
// contiguous addresses and one protection in one call (one native view per part, as before) and
// unmaps each run of contiguous unmaps in one call; =2 also puts physically contiguous parts of a
// run in one native view. 0: one entry at a time, as before.
// PERF-GT7-095: 2 by default (it was 0); RYGT_BATCHMAP_MERGE=0 or 1 for the earlier ways.
int BatchMergeMode() {
    static const int mode = [] {
        const char* value = std::getenv("RYGT_BATCHMAP_MERGE");
        const int chosen = value ? std::atoi(value) : 2;
        if (chosen > 0) {
            std::fprintf(stderr, "[batchmap] PERF-GT7-090: RYGT_BATCHMAP_MERGE=%d, runs of a batch in one call%s\n", chosen,
                chosen >= 2 ? ", physically contiguous parts in one view" : ", one view per part");
        }
        return chosen;
    }();
    return mode;
}

// RYGT_DIAG_BATCHMAP=1: one stderr line per large batch (its shape and its time).
bool BatchDiagEnabled() {
    static const bool enabled = [] {
        const char* value = std::getenv("RYGT_DIAG_BATCHMAP");
        return value && value[0] == '1';
    }();
    return enabled;
}

// End of the run starting at first: consecutive fixed maps with contiguous addresses and the same
// protection, or consecutive unmaps of contiguous ranges. first + 1 when the entry has no run.
int BatchRunEnd(const KernelBatchMapEntry* entries, int first, int count, int flags) {
    const auto& head = entries[first];
    const bool map = head.operation == BatchOpMapDirect;
    if (!map && head.operation != BatchOpUnmap) return first + 1;
    if (map && (flags & BatchMapFixed) == 0) return first + 1;
    if (head.start == nullptr || head.length == 0 || (head.length & (PS5_PAGE_SIZE - 1)) != 0) return first + 1;
    auto next = reinterpret_cast<std::uintptr_t>(head.start);
    if (head.length > UINTPTR_MAX - next) return first + 1;
    next += head.length;
    int end = first + 1;
    while (end < count) {
        const auto& entry = entries[end];
        if (entry.operation != head.operation || reinterpret_cast<std::uintptr_t>(entry.start) != next) break;
        if (entry.length == 0 || (entry.length & (PS5_PAGE_SIZE - 1)) != 0 || entry.length > UINTPTR_MAX - next) break;
        if (map && entry.protection != head.protection) break;
        next += entry.length;
        ++end;
    }
    return end;
}

struct BatchShape {
    int maps = 0;
    int unmaps = 0;
    int others = 0;
    int mapRuns = 0;      // runs of two or more maps
    int mapRunParts = 0;
    int physGroups = 0;   // physically contiguous groups inside those runs
    int unmapRuns = 0;
    int unmapRunParts = 0;
};

// PERF-GT7-092: a batch's unmap; sceKernelMunmap as before with RYGT_DEFER_UNMAP=0.
int BatchUnmap(void* start, std::uint64_t length) {
    if (!DeferUnmapEnabled()) return sceKernelMunmap(reinterpret_cast<uint64_t>(start), length);
    return GuardMemoryCall("sceKernelMunmap", [&]() -> int {
        const int result = DoMunmapDeferred(start, length);
        if (result == 0) _releaseFlexible(reinterpret_cast<uintptr_t>(start), length);
        return result;
    });
}

// Diagnostic clock (RYGT_DIAG_BATCHMAP), nanoseconds.
long long BatchNowNs() {
    return std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}

struct BatchBurst {
    long long first = 0;
    long long last = 0;
    int calls = 0;
    int entries = 0;
    double ms = 0;
    int whole = 0;
    int fallback = 0;
    BatchShape shape;
};
thread_local BatchBurst g_batchBurst;

void FlushBatchBurst(BatchBurst& burst, long long frequency, int mode) {
    if (burst.calls > 0 && burst.entries >= 200) {
        static std::atomic<unsigned> printed{0};
        if (printed.fetch_add(1) < 300) {
            const double span = 1000.0 * static_cast<double>(burst.last - burst.first) / static_cast<double>(frequency);
            std::fprintf(stderr, "[batchmap] burst: %d calls, %d entries over %.2f ms (%.2f ms in the calls), merge %d | %d maps, %d in %d contiguous runs (%d physical groups) | %d unmaps, %d in %d runs | %d other | %d runs taken whole, %d part by part\n",
                burst.calls, burst.entries, span, burst.ms, mode, burst.shape.maps, burst.shape.mapRunParts, burst.shape.mapRuns, burst.shape.physGroups,
                burst.shape.unmaps, burst.shape.unmapRunParts, burst.shape.unmapRuns, burst.shape.others, burst.whole, burst.fallback);
        }
    }
    burst = BatchBurst{};
}

BatchShape MeasureBatch(const KernelBatchMapEntry* entries, int count, int flags) {
    BatchShape shape;
    for (int i = 0; i < count;) {
        const auto& entry = entries[i];
        if (entry.operation == BatchOpMapDirect) ++shape.maps;
        else if (entry.operation == BatchOpUnmap) ++shape.unmaps;
        else ++shape.others;
        const int end = BatchRunEnd(entries, i, count, flags);
        if (end - i >= 2) {
            if (entry.operation == BatchOpMapDirect) {
                ++shape.mapRuns;
                shape.mapRunParts += end - i;
                shape.maps += end - i - 1;
                ++shape.physGroups;
                for (int k = i + 1; k < end; ++k) {
                    if (entries[k].offset != entries[k - 1].offset + entries[k - 1].length) ++shape.physGroups;
                }
            } else {
                ++shape.unmapRuns;
                shape.unmapRunParts += end - i;
                shape.unmaps += end - i - 1;
            }
        }
        i = end;
    }
    return shape;
}

}

extern "C" {

int APS5_VABI sceKernelBatchMap2(KernelBatchMapEntry* entries, int num_entries, int* num_entries_out, int flags) {
 return GuardMemoryCall("sceKernelBatchMap2", [&]() -> int {
 if (!entries || num_entries < 0) return SCE_KERNEL_ERROR_EINVAL;
 constexpr int OpMapDirect = 0;
 constexpr int OpUnmap = 1;
 constexpr int OpProtect = 2;
 constexpr int OpMapFlexible = 3;
 constexpr int OpTypeProtect = 4;
 int processed = 0;
 int result = 0;
 const int mergeMode = BatchMergeMode();
 const bool diag = BatchDiagEnabled();
 BatchShape shape;
 long long started = 0;
 if (diag) {
  shape = MeasureBatch(entries, num_entries, flags);
  started = BatchNowNs();
 }
 int mergedRuns = 0;
 int fallbackRuns = 0;
 int plainUntil = 0; // entries of a run that fell back go one by one
 thread_local std::vector<DirectMapPart> parts;
 while (processed < num_entries) {
  auto& entry = entries[processed];
  if (mergeMode > 0 && processed >= plainUntil) {
   const int end = BatchRunEnd(entries, processed, num_entries, flags);
   if (end - processed >= 2) {
    std::size_t bytes = 0;
    for (int k = processed; k < end; ++k) bytes += entries[k].length;
    if (entry.operation == OpMapDirect) {
     parts.clear();
     for (int k = processed; k < end; ++k) parts.push_back(DirectMapPart{entries[k].length, static_cast<int64_t>(entries[k].offset)});
     if (DoMapDirectRun(entry.start, bytes, static_cast<uint8_t>(entry.protection), flags, parts.data(), parts.size(), mergeMode >= 2) == 0) {
      ++mergedRuns;
      processed = end;
      continue;
     }
     ++fallbackRuns; // part by part below
     plainUntil = end;
    } else {
     result = BatchUnmap(entry.start, bytes); // PERF-GT7-092
     if (result != 0) break;
     ++mergedRuns;
     processed = end;
     continue;
    }
   }
  }
  switch (entry.operation) {
  case OpMapDirect:
   result = DoMapDirect(&entry.start, entry.length, static_cast<uint8_t>(entry.protection), flags, static_cast<int64_t>(entry.offset), 0);
   break;
  case OpUnmap:
   result = BatchUnmap(entry.start, entry.length); // PERF-GT7-092
   break;
  case OpProtect:
  case OpTypeProtect:
   result = DoMprotect(entry.start, entry.length, static_cast<uint8_t>(entry.protection));
   break;
  case OpMapFlexible:
   result = _mapFlexible(&entry.start, entry.length, static_cast<uint8_t>(entry.protection), flags);
   break;
  default:
   throw std::invalid_argument("sceKernelBatchMap2: unsupported operation " + std::to_string(entry.operation));
  }
  if (result != 0) break;
  ++processed;
 }
 if (diag) {
  const long long finished = BatchNowNs();
  const double ms = static_cast<double>(finished - started) / 1e6;
  // A burst: this thread's batches with less than 5 ms between them (GT7's streaming frames send
  // many small ones); one line per burst of 200 entries or more.
  auto& burst = g_batchBurst;
  if (burst.calls > 0 && started - burst.last > 5'000'000) FlushBatchBurst(burst, 1'000'000'000, mergeMode);
  if (burst.calls == 0) burst.first = started;
  ++burst.calls;
  burst.entries += num_entries;
  burst.ms += ms;
  burst.last = finished;
  burst.whole += mergedRuns;
  burst.fallback += fallbackRuns;
  burst.shape.maps += shape.maps;
  burst.shape.unmaps += shape.unmaps;
  burst.shape.others += shape.others;
  burst.shape.mapRuns += shape.mapRuns;
  burst.shape.mapRunParts += shape.mapRunParts;
  burst.shape.physGroups += shape.physGroups;
  burst.shape.unmapRuns += shape.unmapRuns;
  burst.shape.unmapRunParts += shape.unmapRunParts;
  static std::atomic<unsigned> batches{0};
  static std::atomic<unsigned> printed{0};
  const unsigned n = num_entries >= 32 ? batches.fetch_add(1) + 1 : 0;
  if (n != 0 && (num_entries >= 256 || n <= 30) && printed.fetch_add(1) < 400) {
   std::fprintf(stderr, "[batchmap] batch %u: %d entries, %.3f ms, merge %d | %d maps, %d in %d contiguous runs (%d physical groups) | %d unmaps, %d in %d runs | %d other | %d runs taken whole, %d part by part%s\n",
    n, num_entries, ms, mergeMode, shape.maps, shape.mapRunParts, shape.mapRuns, shape.physGroups, shape.unmaps, shape.unmapRunParts, shape.unmapRuns, shape.others, mergedRuns, fallbackRuns,
    result != 0 ? " (failed)" : "");
  }
 }
 NoteBatchMapActivity(); // PERF-GT7-092: the worker waits for the batches to pause
 if (num_entries_out) *num_entries_out = processed;
 return result;
 });
}

}

extern "C" {

int APS5_VABI sceKernelMlock_nid_postfix(void* address, std::uint64_t length) {
    if (!address || length == 0 || length > static_cast<std::uint64_t>(SIZE_MAX)) return SCE_KERNEL_ERROR_EINVAL;
#ifdef _WIN32
    return VirtualLock(address, static_cast<SIZE_T>(length)) ? 0 : SCE_KERNEL_ERROR_ENOMEM;
#else
    return ::mlock(address, static_cast<std::size_t>(length)) == 0 ? 0 : SCE_KERNEL_ERROR_ENOMEM;
#endif
}

int APS5_VABI sceKernelMunlock() {
    return 0;
}

}
