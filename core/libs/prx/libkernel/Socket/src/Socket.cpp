#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdint>
#include <cstddef>
#include <cstring>
#include <vector>
#include "SceTypes.hpp"
#include "prx/libc/include/General.hpp"

extern "C" {

const char* APS5_VABI __inet_ntop_nid_postfix(int family, const void* source, char* destination, std::uint32_t capacity);
int APS5_VABI __inet_pton_nid_postfix(int family, const char* text, void* destination);

const char* APS5_VABI inet_ntop_nid_postfix(int af, const void* src, char* dst, uint32_t size) {
    return __inet_ntop_nid_postfix(af, src, dst, size);
}

int APS5_VABI inet_pton_nid_postfix(int af, const char* src, void* dst) {
    return __inet_pton_nid_postfix(af, src, dst);
}

// select() over network sockets. On the PS4 the descriptors sceNetSocket returns are kernel file
// descriptors, so games wait on them with the BSD select() (GT Sport's lobby waits for incoming
// P2P connections this way); in RyGT they live in libSceNet, so this asks libSceNet through its
// epoll calls, which also cover P2P sockets. fd_set is FreeBSD's: a bit per descriptor in 64-bit
// words; timeval is two 64-bit fields. Descriptors that are not network sockets never show ready.
int APS5_VABI select_nid_postfix(int nfds, void* readfds, void* writefds, void* exceptfds, const void* timeout) {
    using EpollCreate = int(APS5_VABI*)(const char*, int);
    using EpollControl = int(APS5_VABI*)(int, int, int, const NetEpollEvent*);
    using EpollWait = int(APS5_VABI*)(int, NetEpollEvent*, int, int);
    using EpollDestroy = int(APS5_VABI*)(int);
    static HMODULE net = GetModuleHandleA("libSceNet.prx");
    static const auto create = reinterpret_cast<EpollCreate>(reinterpret_cast<void*>(GetProcAddress(net, "SF47kB2MNTo")));
    static const auto control = reinterpret_cast<EpollControl>(reinterpret_cast<void*>(GetProcAddress(net, "ZVw46bsasAk")));
    static const auto wait = reinterpret_cast<EpollWait>(reinterpret_cast<void*>(GetProcAddress(net, "drjIbDbA7UQ")));
    static const auto destroy = reinterpret_cast<EpollDestroy>(reinterpret_cast<void*>(GetProcAddress(net, "Inp1lfL+Jdw")));
    if (!create || !control || !wait || !destroy || nfds < 0) {
        NotImplemented_nid_no_patch(__func__);
        return 0;
    }
    if (nfds > 1024) nfds = 1024;
    auto* read_bits = static_cast<std::uint64_t*>(readfds);
    auto* write_bits = static_cast<std::uint64_t*>(writefds);
    auto has = [](const std::uint64_t* bits, int fd) { return bits && ((bits[fd / 64] >> (fd % 64)) & 1); };

    int timeout_us = -1;  // no timeval: wait until something is ready
    if (timeout) {
        const auto* tv = static_cast<const std::int64_t*>(timeout);
        const std::int64_t total = tv[0] * 1000000 + tv[1];
        timeout_us = total <= 0 ? 0 : total > 0x7FFFFFFF ? 0x7FFFFFFF : static_cast<int>(total);
    }

    const int epoll = create("select", 0);
    if (epoll < 0) return -1;
    int watched = 0;
    for (int fd = 0; fd < nfds; ++fd) {
        NetEpollEvent event{};
        event.events = (has(read_bits, fd) ? 1u : 0u) | (has(write_bits, fd) ? 4u : 0u);
        if (event.events == 0) continue;
        event.ident = static_cast<std::uint64_t>(fd);
        if (control(epoll, 1, fd, &event) == 0) ++watched;
    }
    std::vector<NetEpollEvent> ready(64);
    int got = 0;
    if (watched > 0) {
        got = wait(epoll, ready.data(), static_cast<int>(ready.size()), timeout_us);
    } else if (timeout_us != 0) {
        // Nothing here select() can watch: wait out the timeout (a short while for "forever").
        Sleep(static_cast<DWORD>(timeout_us < 0 ? 100 : (timeout_us + 999) / 1000));
    }
    destroy(epoll);
    if (got < 0) got = 0;

    const int words = (nfds + 63) / 64;
    std::vector<std::uint64_t> new_read(words > 0 ? words : 1, 0), new_write(words > 0 ? words : 1, 0);
    int count = 0;
    for (int i = 0; i < got; ++i) {
        const int fd = static_cast<int>(ready[i].ident);
        if (fd < 0 || fd >= nfds) continue;
        if ((ready[i].events & (1u | 8u | 16u)) && has(read_bits, fd)) {
            new_read[fd / 64] |= 1ull << (fd % 64);
            ++count;
        }
        if ((ready[i].events & 4u) && has(write_bits, fd)) {
            new_write[fd / 64] |= 1ull << (fd % 64);
            ++count;
        }
    }
    if (read_bits) std::memcpy(read_bits, new_read.data(), words * sizeof(std::uint64_t));
    if (write_bits) std::memcpy(write_bits, new_write.data(), words * sizeof(std::uint64_t));
    if (exceptfds) std::memset(exceptfds, 0, words * sizeof(std::uint64_t));
    return count;
}

}
