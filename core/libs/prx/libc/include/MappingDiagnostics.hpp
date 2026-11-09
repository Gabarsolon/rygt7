#pragma once
#include <windows.h>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstring>

namespace GuestArena::MappingDiagnostics {
enum Part : unsigned { LockWait, Query, Placeholder, Duplicate, Compare, MapView, Protect, UnmapView, Metadata, PartCount };
struct Timing { std::uint64_t nanos = 0, calls = 0; };
struct Entry {
    std::uint64_t ready = 0, operation = 0, address = 0, bytes = 0, thread = 0, start = 0, end = 0;
    Timing parts[PartCount]{};
};
struct Header {
    char magic[8];
    std::uint64_t version, frequency, capacity, entry_bytes;
    volatile LONG64 claimed, dropped;
    std::uint64_t reserved;
};
static_assert(sizeof(Header) == 64 && sizeof(Entry) == 200);
inline thread_local Entry* current = nullptr;
inline std::uint64_t Now() {
    return std::chrono::duration_cast<std::chrono::nanoseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count();
}
class Store {
public:
    Store() {
        wchar_t path[32768];
        const DWORD length = GetEnvironmentVariableW(L"RYGT_DIAG_MAPPING_FILE", path, 32768);
        if (length == 0 || length >= 32768) return;
        file = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ, nullptr,
                           CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) { Failed(); return; }
        mapping = CreateFileMappingW(file, nullptr, PAGE_READWRITE, 0, StorageBytes, nullptr);
        if (!mapping) { Failed(); return; }
        header = static_cast<Header*>(MapViewOfFile(mapping, FILE_MAP_WRITE, 0, 0, StorageBytes));
        if (!header) { Failed(); return; }
        // Commit diagnostic storage before gameplay, never allocate or print per mapping.
        std::memset(header, 0, StorageBytes);
        std::memcpy(header->magic, "RYMAPD01", 8);
        header->version = 1; header->frequency = 1000000000;
        header->capacity = Capacity; header->entry_bytes = sizeof(Entry);
    }
    ~Store() {
        if (header) { FlushViewOfFile(header, 0); UnmapViewOfFile(header); }
        if (mapping) CloseHandle(mapping);
        if (file != INVALID_HANDLE_VALUE) CloseHandle(file);
    }
    bool Enabled() const { return header != nullptr; }
    void Add(Entry& entry) {
        const auto index = InterlockedIncrement64(&header->claimed) - 1;
        if (index >= Capacity) { InterlockedIncrement64(&header->dropped); return; }
        auto* destination = reinterpret_cast<Entry*>(header + 1) + index;
        *destination = entry;
        std::atomic_thread_fence(std::memory_order_release);
        destination->ready = 1;
    }
private:
    void Failed() { std::fprintf(stderr, "[mapping diagnostic] storage unavailable: %lu\n", GetLastError()); }
    static constexpr std::uint64_t Capacity = 65536;
    static constexpr DWORD StorageBytes = sizeof(Header) + Capacity * sizeof(Entry);
    HANDLE file = INVALID_HANDLE_VALUE, mapping = nullptr;
    Header* header = nullptr;
};
class Scope {
public:
    Scope(Store& storage, unsigned operation, std::uintptr_t address, std::size_t bytes)
        : storage(storage), previous(current), enabled(storage.Enabled()) {
        if (!enabled) return;
        entry.operation = operation; entry.address = address; entry.bytes = bytes;
        entry.thread = GetCurrentThreadId(); entry.start = Now(); current = &entry;
    }
    ~Scope() {
        if (!enabled) return;
        const DWORD error = GetLastError();
        entry.end = Now(); current = previous; storage.Add(entry); SetLastError(error);
    }
    void Locked() {
        if (enabled) entry.parts[LockWait] = {Now() - entry.start, 1};
    }
private:
    Store& storage;
    Entry entry{};
    Entry* previous;
    bool enabled;
};
class Interval {
public:
    explicit Interval(Part part) : entry(current), part(part), start(entry ? Now() : 0) {}
    ~Interval() {
        if (!entry) return;
        const DWORD error = GetLastError();
        entry->parts[part].nanos += Now() - start;
        ++entry->parts[part].calls;
        SetLastError(error);
    }
private:
    Entry* entry;
    Part part;
    std::uint64_t start;
};
template<class Function> inline auto Measure(Part part, Function&& fn) {
    Interval interval(part);
    return fn();
}
}
