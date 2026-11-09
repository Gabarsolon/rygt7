#pragma once

#include <cstddef>
#include <cstdint>
#include <cstring>
#include <memory>
#include <new>
#include <optional>
#include <stdexcept>

namespace {

enum class GuestReadPhase : std::uint32_t {
    Prepare = 64,
    Direct = 65,
    Staged = 66,
    Allocate = 67,
    Retry = 68,
    Copy = 69,
    DirectAgain = 78,
};

class GuestReadBuffer {
public:
    static constexpr std::size_t MinimumSize = 32 * 1024;
    static constexpr std::size_t MaximumSize = 1024 * 1024;

    char* Acquire(std::size_t bytes) {
        if (inUse || bytes < MinimumSize || bytes > MaximumSize) return nullptr;
        if (bytes > capacity) {
            const std::size_t next = (bytes + 65535) & ~std::size_t{65535};
            std::unique_ptr<char[]> allocated{new (std::nothrow) char[next]};
            if (!allocated) return nullptr;
            data = std::move(allocated);
            capacity = next;
        }
        inUse = true;
        return data.get();
    }

    void Release() { inUse = false; }

private:
    std::unique_ptr<char[]> data;
    std::size_t capacity{};
    bool inUse{};
};

template <typename TRead, typename TPrepare, typename TObserve>
std::optional<std::int64_t> ReadGuestStaged(GuestReadBuffer& storage, void* target,
                                         std::size_t bytes, TRead&& read,
                                         TPrepare&& prepare, TObserve&& observe) {
    if (!target) return std::nullopt;
    char* scratch = observe(GuestReadPhase::Allocate, bytes, [&] { return storage.Acquire(bytes); });
    if (!scratch) return std::nullopt;
    struct Lease {
        GuestReadBuffer& storage;
        ~Lease() { storage.Release(); }
    } lease{storage};
    const std::int64_t received = observe(GuestReadPhase::Staged, bytes,
                                        [&] { return read(scratch); });
    if (received <= 0) return received;
    if (static_cast<std::uint64_t>(received) > bytes)
        throw std::runtime_error("File read returned more bytes than requested");
    const auto copied = static_cast<std::size_t>(received);
    observe(GuestReadPhase::Prepare, copied, [&] { prepare(target, copied); });
    observe(GuestReadPhase::Copy, copied, [&] { std::memcpy(target, scratch, copied); });
    return received;
}

}
