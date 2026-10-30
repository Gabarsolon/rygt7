#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#else
#include <arpa/inet.h>
#include <cerrno>
#include <fcntl.h>
#include <netdb.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <sys/socket.h>
#include <poll.h>
#include <unistd.h>
#endif
#include <algorithm>
#include <atomic>
#include <climits>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <cstdarg>
#include <cstdio>
#include <cstddef>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <thread>
#include <memory>
#include <map>
#include <mutex>
#include <new>
#include <vector>
#include <set>
#include <string>
#include "SceTypes.hpp"
#include "OnlineConfig.hpp"
#include "prx/libc/include/General.hpp"

// Guest socket descriptors retain PS5 semantics while their transport is backed by host sockets.

namespace {
// FreeBSD errno numbers as reported through sceNetErrnoLoc (SCE_NET_ERROR_* is 0x80410100 + errno).
constexpr int NET_ENOENT = 2;
constexpr int NET_EBADF = 9;
constexpr int NET_EINVAL = 22;
constexpr int NET_EAGAIN = 35;
constexpr int NET_ENOTSOCK = 38;
constexpr int NET_EOPNOTSUPP = 45;
constexpr int NET_EPROTONOSUPPORT = 43;
constexpr int NET_EAFNOSUPPORT = 47;
constexpr int NET_EADDRINUSE = 48;
constexpr int NET_ENETUNREACH = 51;
constexpr int NET_ECONNABORTED = 53;
constexpr int NET_EMSGSIZE = 40;
constexpr int NET_ETIMEDOUT = 60;
constexpr int NET_ECONNREFUSED = 61;
constexpr int NET_ERROR_RESOLVER_ENODNS = static_cast<int>(0x804101E1u);

constexpr int NET_AF_INET = 2;
constexpr int NET_AF_INET6 = 28;
constexpr int NET_SOCK_STREAM = 1;
constexpr int NET_SOCK_DGRAM = 2;
constexpr int NET_SOCK_RAW = 3;
constexpr int NET_SOL_SOCKET = 0xFFFF;
constexpr int NET_SO_SNDTIMEO = 0x1005;
constexpr int NET_SO_RCVTIMEO = 0x1006;
constexpr int NET_SO_NBIO = 0x1200;

#ifdef _WIN32
using NativeSocket = SOCKET;
using NativeLength = int;
constexpr NativeSocket INVALID_NATIVE_SOCKET = INVALID_SOCKET;
bool initialize_sockets() {
    static const int result = [] {
        WSADATA data{};
        return WSAStartup(MAKEWORD(2, 2), &data);
    }();
    return result == 0;
}
void close_socket(NativeSocket socket) { closesocket(socket); }
int native_error() {
    switch (WSAGetLastError()) {
        case WSAEWOULDBLOCK: return NET_EAGAIN;
        case WSAEADDRINUSE: return NET_EADDRINUSE;
        case WSAEAFNOSUPPORT: return NET_EAFNOSUPPORT;
        case WSAENETUNREACH: return NET_ENETUNREACH;
        case WSAEHOSTUNREACH: return NET_ENETUNREACH;
        case WSAECONNABORTED: return NET_ECONNABORTED;
        case WSAECONNRESET: return 54;
        case WSAETIMEDOUT: return NET_ETIMEDOUT;
        case WSAECONNREFUSED: return NET_ECONNREFUSED;
        case WSAEMSGSIZE: return NET_EMSGSIZE;
        case WSAEINTR: return 4;
        case WSAEINVAL: return NET_EINVAL;
        case WSAENOTSOCK: return NET_ENOTSOCK;
        case WSAEALREADY: return 37;  // EALREADY
        case WSAEISCONN: return 56;   // EISCONN
        default: return 5;
    }
}
#else
using NativeSocket = int;
using NativeLength = socklen_t;
constexpr NativeSocket INVALID_NATIVE_SOCKET = -1;
bool initialize_sockets() { return true; }
void close_socket(NativeSocket socket) { ::close(socket); }
int native_error() {
    switch (errno) {
        case EAGAIN: return NET_EAGAIN;
#if EWOULDBLOCK != EAGAIN
        case EWOULDBLOCK: return NET_EAGAIN;
#endif
        case EADDRINUSE: return NET_EADDRINUSE;
        case EAFNOSUPPORT: return NET_EAFNOSUPPORT;
        case ENETUNREACH: return NET_ENETUNREACH;
        case EHOSTUNREACH: return NET_ENETUNREACH;
        case ECONNABORTED: return NET_ECONNABORTED;
        case ECONNRESET: return 54;
        case ETIMEDOUT: return NET_ETIMEDOUT;
        case ECONNREFUSED: return NET_ECONNREFUSED;
        case EMSGSIZE: return NET_EMSGSIZE;
        case EINTR: return 4;
        case EINVAL: return NET_EINVAL;
        case ENOTSOCK: return NET_ENOTSOCK;
        default: return 5;
    }
}
#endif

struct NativeSocketHandle {
    NativeSocket value;
    explicit NativeSocketHandle(NativeSocket socket) : value(socket) {}
    ~NativeSocketHandle() {
        if (value != INVALID_NATIVE_SOCKET) close_socket(value);
    }
};

bool guest_to_native_address(const void* address, std::uint32_t length,
                             sockaddr_storage& native, NativeLength& native_length) {
    if (!address || length < 2) return false;
    const auto* bytes = static_cast<const std::uint8_t*>(address);
    if (bytes[1] == NET_AF_INET && bytes[0] == 16 && length >= 16) {
        auto& v4 = reinterpret_cast<sockaddr_in&>(native);
        v4.sin_family = AF_INET;
        std::memcpy(&v4.sin_port, bytes + 2, 2);
        std::memcpy(&v4.sin_addr, bytes + 4, 4);
        native_length = sizeof(v4);
        return true;
    }
    if (bytes[1] == NET_AF_INET6 && bytes[0] == 28 && length >= 28) {
        auto& v6 = reinterpret_cast<sockaddr_in6&>(native);
        v6.sin6_family = AF_INET6;
        std::memcpy(&v6.sin6_port, bytes + 2, 2);
        std::memcpy(&v6.sin6_flowinfo, bytes + 4, 4);
        std::memcpy(&v6.sin6_addr, bytes + 8, 16);
        std::memcpy(&v6.sin6_scope_id, bytes + 24, 4);
        native_length = sizeof(v6);
        return true;
    }
    return false;
}

bool native_to_guest_address(const sockaddr_storage& native, void* address, std::uint32_t* length) {
    if (!address || !length) return false;
    std::uint8_t bytes[28]{};
    if (native.ss_family == AF_INET) {
        const auto& v4 = reinterpret_cast<const sockaddr_in&>(native);
        bytes[0] = 16;
        bytes[1] = NET_AF_INET;
        std::memcpy(bytes + 2, &v4.sin_port, 2);
        std::memcpy(bytes + 4, &v4.sin_addr, 4);
    } else if (native.ss_family == AF_INET6) {
        const auto& v6 = reinterpret_cast<const sockaddr_in6&>(native);
        bytes[0] = 28;
        bytes[1] = NET_AF_INET6;
        std::memcpy(bytes + 2, &v6.sin6_port, 2);
        std::memcpy(bytes + 4, &v6.sin6_flowinfo, 4);
        std::memcpy(bytes + 8, &v6.sin6_addr, 16);
        std::memcpy(bytes + 24, &v6.sin6_scope_id, 4);
    } else {
        return false;
    }
    const auto required = static_cast<std::uint32_t>(bytes[0]);
    std::memcpy(address, bytes, std::min(*length, required));
    *length = required;
    return true;
}

struct Sock {
    int family = 0;
    int type = 0;
    std::shared_ptr<NativeSocketHandle> native;
    bool nonblock = false;
    bool bound = false;
    bool listening = false;
    bool aborted = false;
    int rcv_timeout_us = 0;
    int snd_timeout_us = 0;
    // P2P datagram sockets (SCE_NET_SOCK_DGRAM_P2P): `native` is the socket's loopback mailbox.
    bool p2p = false;
    std::uint16_t vport = 0;  // host byte order
    sockaddr_in mailbox{};    // where the receive thread delivers this socket's datagrams
    // P2P stream sockets (SCE_NET_SOCK_STREAM_P2P): until connected, `native` is a loopback
    // "doorbell" UDP socket that gets one byte per connection waiting to be accepted.
    sockaddr_in peer{};       // the other end of a connected P2P stream
    std::uint16_t peer_vport = 0;
    bool reset_reported = false;  // a stream reset/aborted: reported once, then reads see the end
};

// P2P sockets. On the PS4 every P2P socket of a game shares one UDP port (3658 by default) and
// datagrams carry a virtual port, so a game can run several P2P channels over one NAT mapping.
// Here all of them share one host UDP socket ("the transport"); each datagram on the wire starts
// with a small header naming its destination and source virtual ports. A receive thread hands each
// datagram to the guest socket bound to its virtual port through that socket's own loopback UDP
// socket (its mailbox), so blocking reads, timeouts, non-blocking mode and epoll all keep working
// on a real host socket. The mailbox datagram is prefixed with the sender's address.
constexpr int NET_SOCK_DGRAM_P2P = 6;
constexpr int NET_SOCK_STREAM_P2P = 10;
constexpr std::uint16_t P2P_DEFAULT_PORT = 3658;
constexpr std::uint8_t P2P_MAGIC[2] = {'R', 'P'};
constexpr std::size_t P2P_WIRE_HEADER = 6;      // magic, destination vport, source vport
constexpr std::size_t P2P_MAILBOX_HEADER = 8;   // source address, source port, source vport
constexpr std::size_t P2P_MAX_DATAGRAM = 65507;
// A P2P socket bound to this vport sends and receives raw datagrams on the shared port, without
// the vport header: anything arriving without the header goes to it. RyGT's PSN signaling uses it
// (its handshakes and the shadNet STUN exchange must come from the game's own P2P port).
constexpr std::uint16_t P2P_RAW_VPORT = 0xFFFF;

// A trace of P2P traffic in stderr.log (the first 400 events of a run), for working out why two
// games do not connect.
void p2p_trace(const char* format, ...) {
    static std::atomic<int> lines{0};
    if (lines.fetch_add(1) >= 2000) return;
    char text[512];
    va_list args;
    va_start(args, format);
    std::vsnprintf(text, sizeof(text), format, args);
    va_end(args);
    std::fprintf(stderr, "[online] P2P: %s\n", text);
    std::fflush(stderr);
}

// P2P traffic per peer and direction, written as a summary every 5 seconds (each packet would
// flood the log: GT Sport's lobby sends several a second to every player).
void p2p_count(const std::string& what, std::size_t bytes) {
    static std::mutex mutex;
    static std::map<std::string, std::pair<std::uint64_t, std::uint64_t>> counts;
    static auto last = std::chrono::steady_clock::now();
    std::lock_guard<std::mutex> lk(mutex);
    auto& count = counts[what];
    ++count.first;
    count.second += bytes;
    const auto now = std::chrono::steady_clock::now();
    if (now - last < std::chrono::seconds(5)) return;
    last = now;
    for (const auto& entry : counts)
        std::fprintf(stderr, "[online] P2P: last 5 s: %s: %llu packets, %llu bytes\n", entry.first.c_str(),
                     static_cast<unsigned long long>(entry.second.first), static_cast<unsigned long long>(entry.second.second));
    std::fflush(stderr);
    counts.clear();
}

std::string p2p_text(const void* ip, std::uint16_t port_nbo, std::uint16_t vport_nbo) {
    const auto* b = static_cast<const std::uint8_t*>(ip);
    char text[40];
    std::snprintf(text, sizeof(text), "%u.%u.%u.%u:%u:%u", b[0], b[1], b[2], b[3], ntohs(port_nbo), ntohs(vport_nbo));
    return text;
}

struct P2PTransport {
    std::mutex mutex;
    NativeSocket socket = INVALID_NATIVE_SOCKET;   // the shared UDP port
    NativeSocket relay = INVALID_NATIVE_SOCKET;    // sends into mailboxes
    std::uint16_t port = 0;                        // host byte order
    std::map<std::uint16_t, sockaddr_in> mailboxes;  // vport -> mailbox address
    bool running = false;
};

P2PTransport& p2p_transport() {
    static P2PTransport* transport = new P2PTransport();
    return *transport;
}

void disable_udp_connreset(NativeSocket socket) {
#ifdef _WIN32
    // Without this, an ICMP "port unreachable" from a peer that left makes the next recvfrom fail.
    BOOL off = FALSE;
    DWORD returned = 0;
    WSAIoctl(socket, _WSAIOW(IOC_VENDOR, 12), &off, sizeof(off), nullptr, 0, &returned, nullptr, nullptr);
#else
    (void)socket;
#endif
}

std::uint16_t p2p_configured_port(std::uint16_t requested) {
    if (const char* text = std::getenv("RYGT_P2P_PORT")) {
        const long value = std::strtol(text, nullptr, 10);
        if (value > 0 && value < 65536) return static_cast<std::uint16_t>(value);
    }
    return requested != 0 ? requested : P2P_DEFAULT_PORT;
}

void p2p_receive_loop() {
    P2PTransport& transport = p2p_transport();
    std::vector<char> buffer(P2P_MAX_DATAGRAM + P2P_MAILBOX_HEADER);
    for (;;) {
        sockaddr_in from{};
        NativeLength from_length = sizeof(from);
        // Receive just far enough in that the payload ends up right after the mailbox header, which
        // then overwrites the wire header in place.
        char* receive_at = buffer.data() + (P2P_MAILBOX_HEADER - P2P_WIRE_HEADER);
        const int received = ::recvfrom(transport.socket, receive_at, static_cast<int>(P2P_MAX_DATAGRAM + P2P_WIRE_HEADER),
            0, reinterpret_cast<sockaddr*>(&from), &from_length);
        if (received < 0) {
#ifdef _WIN32
            const int error = WSAGetLastError();
            if (error == WSAECONNRESET || error == WSAEMSGSIZE) continue;
#endif
            std::lock_guard<std::mutex> lk(transport.mutex);
            if (!transport.running) return;
            continue;
        }
        if (static_cast<std::size_t>(received) < P2P_WIRE_HEADER ||
            receive_at[0] != static_cast<char>(P2P_MAGIC[0]) || receive_at[1] != static_cast<char>(P2P_MAGIC[1])) {
            // No vport header: a raw datagram for the raw socket, if there is one.
            sockaddr_in mailbox{};
            {
                std::lock_guard<std::mutex> lk(transport.mutex);
                auto it = transport.mailboxes.find(P2P_RAW_VPORT);
                if (it == transport.mailboxes.end()) {
                    p2p_trace("UDP in  %d bytes from %s without a vport header: dropped (no raw socket)", received,
                              p2p_text(&from.sin_addr, from.sin_port, 0).c_str());
                    continue;
                }
                mailbox = it->second;
            }
            p2p_count("UDP in  from " + p2p_text(&from.sin_addr, from.sin_port, 0) + " raw (signaling)", static_cast<std::size_t>(received));
            std::vector<char> raw(P2P_MAILBOX_HEADER + static_cast<std::size_t>(received));
            std::memcpy(raw.data(), &from.sin_addr, 4);
            std::memcpy(raw.data() + 4, &from.sin_port, 2);
            std::memset(raw.data() + 6, 0, 2);
            std::memcpy(raw.data() + P2P_MAILBOX_HEADER, receive_at, static_cast<std::size_t>(received));
            ::sendto(transport.relay, raw.data(), static_cast<int>(raw.size()), 0,
                     reinterpret_cast<const sockaddr*>(&mailbox), sizeof(mailbox));
            continue;
        }
        std::uint16_t destination_vport;
        std::uint16_t source_vport;
        std::memcpy(&destination_vport, receive_at + 2, 2);
        std::memcpy(&source_vport, receive_at + 4, 2);
        // A socket bound to virtual port 0 sends as the P2P port itself: GT Sport binds its lobby's
        // message socket to vport 0 but knows each player as address:3658:3658, and ignored every
        // message it received from address:3658:0 (another player's keep-alives among them).
        if (source_vport == 0) source_vport = from.sin_port;
        sockaddr_in mailbox{};
        {
            std::lock_guard<std::mutex> lk(transport.mutex);
            auto it = transport.mailboxes.find(ntohs(destination_vport));
            if (it == transport.mailboxes.end()) it = transport.mailboxes.find(0);
            if (it == transport.mailboxes.end()) {
                p2p_trace("UDP in  %d bytes from %s to vport %u: dropped (nothing bound there)",
                          received - static_cast<int>(P2P_WIRE_HEADER),
                          p2p_text(&from.sin_addr, from.sin_port, source_vport).c_str(), ntohs(destination_vport));
                continue;
            }
            mailbox = it->second;
        }
        p2p_count("UDP in  from " + p2p_text(&from.sin_addr, from.sin_port, source_vport) + " to vport " + std::to_string(ntohs(destination_vport)),
                  static_cast<std::size_t>(received) - P2P_WIRE_HEADER);
        // Mailbox header: source address and port (network order) and source vport (network order).
        char* header = buffer.data();
        std::memcpy(header, &from.sin_addr, 4);
        std::memcpy(header + 4, &from.sin_port, 2);
        std::memcpy(header + 6, &source_vport, 2);
        const int payload = received - static_cast<int>(P2P_WIRE_HEADER);
        ::sendto(transport.relay, header, static_cast<int>(P2P_MAILBOX_HEADER) + payload, 0,
                 reinterpret_cast<const sockaddr*>(&mailbox), sizeof(mailbox));
    }
}

// Opens the shared P2P port the first time a P2P socket is bound. Returns 0 or a NET_E* code.
int p2p_start_transport(std::uint16_t requested_port) {
    P2PTransport& transport = p2p_transport();
    std::lock_guard<std::mutex> lk(transport.mutex);
    if (transport.running) return 0;
    const std::uint16_t port = p2p_configured_port(requested_port);
    NativeSocket socket = ::socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    NativeSocket relay = ::socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (socket == INVALID_NATIVE_SOCKET || relay == INVALID_NATIVE_SOCKET) {
        const int error = native_error();
        if (socket != INVALID_NATIVE_SOCKET) close_socket(socket);
        if (relay != INVALID_NATIVE_SOCKET) close_socket(relay);
        return error;
    }
    int size = 4 << 20;
    ::setsockopt(socket, SOL_SOCKET, SO_RCVBUF, reinterpret_cast<const char*>(&size), sizeof(size));
    ::setsockopt(socket, SOL_SOCKET, SO_SNDBUF, reinterpret_cast<const char*>(&size), sizeof(size));
    disable_udp_connreset(socket);
    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_port = htons(port);
    address.sin_addr.s_addr = htonl(INADDR_ANY);
    if (::bind(socket, reinterpret_cast<const sockaddr*>(&address), sizeof(address)) != 0) {
        const int error = native_error();
        std::fprintf(stderr, "[online] P2P: cannot open UDP port %u (another copy of the game running?)\n", port);
        std::fflush(stderr);
        close_socket(socket);
        close_socket(relay);
        return error;
    }
    transport.socket = socket;
    transport.relay = relay;
    transport.port = port;
    transport.running = true;
    std::thread(p2p_receive_loop).detach();
    std::fprintf(stderr, "[online] P2P: listening on UDP port %u\n", port);
    std::fflush(stderr);
    return 0;
}

// A loopback UDP socket for a P2P socket to read from.
NativeSocket p2p_open_mailbox(sockaddr_in& address) {
    NativeSocket socket = ::socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (socket == INVALID_NATIVE_SOCKET) return socket;
    int size = 2 << 20;
    ::setsockopt(socket, SOL_SOCKET, SO_RCVBUF, reinterpret_cast<const char*>(&size), sizeof(size));
    disable_udp_connreset(socket);
    address = {};
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    NativeLength length = sizeof(address);
    if (::bind(socket, reinterpret_cast<const sockaddr*>(&address), sizeof(address)) != 0 ||
        ::getsockname(socket, reinterpret_cast<sockaddr*>(&address), &length) != 0) {
        close_socket(socket);
        return INVALID_NATIVE_SOCKET;
    }
    return socket;
}

// Guest P2P address: sockaddr_in with sin_vport (network order) at offset 8.
void p2p_guest_address(void* address, std::uint32_t* length, const void* ip, const void* port, const void* vport) {
    std::uint8_t bytes[16]{};
    bytes[0] = 16;
    bytes[1] = NET_AF_INET;
    std::memcpy(bytes + 2, port, 2);
    std::memcpy(bytes + 4, ip, 4);
    std::memcpy(bytes + 8, vport, 2);
    std::memcpy(address, bytes, std::min<std::uint32_t>(*length, 16));
    *length = 16;
}

std::mutex g_mutex;
std::condition_variable g_cv;
std::map<int, Sock> g_socks;
std::set<int> g_epolls;
std::map<int, std::map<int, NetEpollEvent>> g_epoll_socks;
std::set<int> g_epoll_aborted;
std::set<int> g_pools;
std::set<int> g_resolvers;
int g_next_sock = 32;
int g_next_epoll = 0x4000;
int g_next_pool = 1;
int g_next_resolver = 0x100;
bool g_net_inited = false;

int* errno_slot() {
    static thread_local int e = 0;
    return &e;
}

int fail(int err) {
    *errno_slot() = err;
    return -1;
}

void log_soft(const char* func, const char* what) {
    static std::mutex mtx;
    static std::map<std::string, int> hits;
    std::lock_guard<std::mutex> lk(mtx);
    if (++hits[func] > 3) {
        return;
    }
    std::fprintf(stderr, "[SOFT-NET] %s -> %s\n", func, what);
    std::fflush(stderr);
}

// send/recv flags. The PS4 adds its own to FreeBSD's (SCE_NET_MSG_USECRYPTO 0x100000,
// SCE_NET_MSG_USESIGNATURE 0x200000, used on GT Sport's encrypted P2P sockets); those, and any
// other flag with no host meaning, are ignored. MSG_DONTWAIT has no Windows flag, so a blocking
// socket is checked for readiness first.
constexpr int NET_MSG_PEEK = 0x2;
constexpr int NET_MSG_WAITALL = 0x40;
constexpr int NET_MSG_DONTWAIT = 0x80;

bool ready_now(NativeSocket socket, bool for_write) {
#ifdef _WIN32
    WSAPOLLFD descriptor{};
    descriptor.fd = socket;
    descriptor.events = for_write ? POLLWRNORM : POLLRDNORM;
    return WSAPoll(&descriptor, 1, 0) > 0;
#else
    pollfd descriptor{};
    descriptor.fd = socket;
    descriptor.events = for_write ? POLLOUT : POLLIN;
    return ::poll(&descriptor, 1, 0) > 0;
#endif
}

// Host recv flags for guest ones; false when MSG_DONTWAIT finds nothing to read (EAGAIN).
bool recv_flags(int flags, const Sock& socket, int& native) {
    native = 0;
    if (flags & NET_MSG_PEEK) native |= MSG_PEEK;
    const bool dontwait = (flags & NET_MSG_DONTWAIT) != 0;
    if ((flags & NET_MSG_WAITALL) && !dontwait && !socket.nonblock &&
        (socket.type == NET_SOCK_STREAM || socket.type == NET_SOCK_STREAM_P2P))
        native |= MSG_WAITALL;
    return !dontwait || socket.nonblock || ready_now(socket.native->value, false);
}

// A reset or aborted TCP connection: Windows reports the error on every read, but FreeBSD (and so
// the games) reports it once and then reads return 0, the end of the stream, which is when a game
// closes the socket. Without this GT Sport kept reading a dead P2P connection forever.
int64_t stream_read_error(int s, const Sock& socket) {
    const int error = native_error();
    const bool is_stream = socket.type == NET_SOCK_STREAM || socket.type == NET_SOCK_STREAM_P2P;
    if (!is_stream || (error != 54 && error != NET_ECONNABORTED)) return fail(error);
    std::lock_guard<std::mutex> lk(g_mutex);
    auto it = g_socks.find(s);
    if (it == g_socks.end()) return fail(NET_EBADF);
    if (it->second.reset_reported) return 0;
    it->second.reset_reported = true;
    if (socket.p2p) p2p_trace("connection with %s %s", p2p_text(&socket.peer.sin_addr, htons(3658), htons(socket.peer_vport)).c_str(),
                              error == 54 ? "was reset by the other end" : "was aborted");
    return fail(error);
}

bool send_ready(int flags, const Sock& socket) {
    return !(flags & NET_MSG_DONTWAIT) || socket.nonblock || ready_now(socket.native->value, true);
}

std::uint16_t swap16(std::uint16_t v) { return static_cast<std::uint16_t>((v << 8) | (v >> 8)); }
std::uint32_t swap32(std::uint32_t v) {
    return (v << 24) | ((v & 0xFF00u) << 8) | ((v >> 8) & 0xFF00u) | (v >> 24);
}

// P2P stream sockets are real TCP connections to the P2P port number (TCP 3658 next to UDP 3658).
// A connecting side first sends the wire header (magic, destination vport, source vport); the
// accept thread reads it and queues the connection for the guest socket listening on that vport,
// ringing that socket's doorbell so a blocking accept or an epoll wait wakes up. Accepted and
// connected sockets are plain host TCP sockets from then on.
struct P2PPendingStream {
    NativeSocket socket;
    sockaddr_in from;
    std::uint16_t source_vport;  // network order
};

struct P2PStreamListener {
    sockaddr_in doorbell{};
    std::deque<P2PPendingStream> pending;
};

struct P2PStreamTransport {
    std::mutex mutex;
    NativeSocket listener = INVALID_NATIVE_SOCKET;
    NativeSocket ringer = INVALID_NATIVE_SOCKET;   // sends doorbell bytes
    std::map<std::uint16_t, P2PStreamListener> listeners;  // vport -> listener
    bool running = false;
};

P2PStreamTransport& p2p_streams() {
    static P2PStreamTransport* transport = new P2PStreamTransport();
    return *transport;
}

bool recv_exactly(NativeSocket socket, char* data, int length) {
    while (length > 0) {
        const int got = ::recv(socket, data, length, 0);
        if (got <= 0) return false;
        data += got;
        length -= got;
    }
    return true;
}

void p2p_handshake(NativeSocket socket, sockaddr_in from) {
#ifdef _WIN32
    const DWORD timeout_ms = 5000;
    ::setsockopt(socket, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&timeout_ms), sizeof(timeout_ms));
#endif
    char header[P2P_WIRE_HEADER];
    if (!recv_exactly(socket, header, static_cast<int>(P2P_WIRE_HEADER)) ||
        header[0] != static_cast<char>(P2P_MAGIC[0]) || header[1] != static_cast<char>(P2P_MAGIC[1])) {
        p2p_trace("TCP in  from %s: no RyGT P2P header, closed", p2p_text(&from.sin_addr, from.sin_port, 0).c_str());
        close_socket(socket);
        return;
    }
#ifdef _WIN32
    const DWORD no_timeout = 0;
    ::setsockopt(socket, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&no_timeout), sizeof(no_timeout));
#endif
    std::uint16_t destination_vport;
    std::uint16_t source_vport;
    std::memcpy(&destination_vport, header + 2, 2);
    std::memcpy(&source_vport, header + 4, 2);
    P2PStreamTransport& streams = p2p_streams();
    std::lock_guard<std::mutex> lk(streams.mutex);
    auto it = streams.listeners.find(ntohs(destination_vport));
    if (it == streams.listeners.end()) it = streams.listeners.find(0);
    if (it == streams.listeners.end()) {
        p2p_trace("TCP in  from %s to vport %u: refused (nothing listening there)",
                  p2p_text(&from.sin_addr, from.sin_port, source_vport).c_str(), ntohs(destination_vport));
        close_socket(socket);
        return;
    }
    it->second.pending.push_back({socket, from, source_vport});
    p2p_trace("TCP in  from %s to vport %u: waiting for the game to accept",
              p2p_text(&from.sin_addr, from.sin_port, source_vport).c_str(), ntohs(destination_vport));
    const char bell = 1;
    ::sendto(streams.ringer, &bell, 1, 0, reinterpret_cast<const sockaddr*>(&it->second.doorbell), sizeof(sockaddr_in));
}

void p2p_accept_loop() {
    P2PStreamTransport& streams = p2p_streams();
    for (;;) {
        sockaddr_in from{};
        NativeLength from_length = sizeof(from);
        const NativeSocket socket = ::accept(streams.listener, reinterpret_cast<sockaddr*>(&from), &from_length);
        if (socket == INVALID_NATIVE_SOCKET) {
            std::lock_guard<std::mutex> lk(streams.mutex);
            if (!streams.running) return;
            std::this_thread::sleep_for(std::chrono::milliseconds(10));
            continue;
        }
        std::thread(p2p_handshake, socket, from).detach();
    }
}

int p2p_start_stream_transport(std::uint16_t requested_port) {
    P2PStreamTransport& streams = p2p_streams();
    std::lock_guard<std::mutex> lk(streams.mutex);
    if (streams.running) return 0;
    const std::uint16_t port = p2p_configured_port(requested_port);
    NativeSocket listener = ::socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    NativeSocket ringer = ::socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (listener == INVALID_NATIVE_SOCKET || ringer == INVALID_NATIVE_SOCKET) {
        const int error = native_error();
        if (listener != INVALID_NATIVE_SOCKET) close_socket(listener);
        if (ringer != INVALID_NATIVE_SOCKET) close_socket(ringer);
        return error;
    }
    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_port = htons(port);
    address.sin_addr.s_addr = htonl(INADDR_ANY);
    if (::bind(listener, reinterpret_cast<const sockaddr*>(&address), sizeof(address)) != 0 ||
        ::listen(listener, 16) != 0) {
        const int error = native_error();
        std::fprintf(stderr, "[online] P2P: cannot open TCP port %u\n", port);
        std::fflush(stderr);
        close_socket(listener);
        close_socket(ringer);
        return error;
    }
    streams.listener = listener;
    streams.ringer = ringer;
    streams.running = true;
    std::thread(p2p_accept_loop).detach();
    std::fprintf(stderr, "[online] P2P: listening on TCP port %u\n", port);
    std::fflush(stderr);
    return 0;
}

}  // namespace

extern "C" {

int* APS5_VABI sceNetErrnoLoc(void) {
    return errno_slot();
}

// GT7's debug log. GT7 (CUSA24769 1.1.0) keeps its engine logger (category, level, source file,
// line, printf format...) in the retail build but sends the text nowhere; with RYGT_GAME_LOG=1 its
// first instruction is replaced by a jump here, which writes every message to gt7_game.log. The
// logger is found by its address in GT7.exe and recognised by its first bytes, so other games
// and builds are left alone.
#ifdef _WIN32
using GuestVsnprintf = int(__attribute__((sysv_abi))*)(char*, std::size_t, const char*, __builtin_sysv_va_list);
GuestVsnprintf g_guest_vsnprintf = nullptr;
std::FILE* g_game_log = nullptr;
std::mutex g_game_log_mutex;

__attribute__((sysv_abi)) void gt7_log(void* category, int level, const char* file, int line, const char* format, ...) {
    (void)category;
    char text[2048];
    __builtin_sysv_va_list arguments;
    __builtin_sysv_va_start(arguments, format);
    g_guest_vsnprintf(text, sizeof(text), format, arguments);
    __builtin_sysv_va_end(arguments);
    const char* name = file ? file : "?";
    for (const char* c = name; *c; ++c)
        if (*c == '\\' || *c == '/') name = c + 1;
    std::lock_guard<std::mutex> lk(g_game_log_mutex);
    std::fprintf(g_game_log, "[%d] %s:%d %s\n", level, name, line, text);
    std::fflush(g_game_log);
}

void game_log_line(const char* format, ...) {
    if (!g_game_log) return;
    char text[1024];
    va_list arguments;
    va_start(arguments, format);
    std::vsnprintf(text, sizeof(text), format, arguments);
    va_end(arguments);
    std::lock_guard<std::mutex> lk(g_game_log_mutex);
    std::fprintf(g_game_log, "[rygt] %s\n", text);
    std::fflush(g_game_log);
}

// Overwrites the start of a function in GT7 with "mov rax, destination; jmp rax" (12 bytes).
bool write_jump(unsigned char* target, const void* destination) {
    unsigned char jump[12] = {0x48, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0xff, 0xe0};
    const auto address = reinterpret_cast<std::uint64_t>(destination);
    std::memcpy(jump + 2, &address, sizeof(address));
    DWORD old = 0;
    if (!VirtualProtect(target, sizeof(jump), PAGE_EXECUTE_READWRITE, &old)) return false;
    std::memcpy(target, jump, sizeof(jump));
    VirtualProtect(target, sizeof(jump), old, &old);
    FlushInstructionCache(GetCurrentProcess(), target, sizeof(jump));
    return true;
}

// Probes: a GT7 function (by its ELF address) jumps to a logging function, which then calls the
// original through a trampoline holding the instructions the jump replaced and a jump back. Only
// prologue instructions that can run anywhere are moved: push rbp/rbx/r8-r15, mov rbp, rsp,
// register-to-register mov and sub rsp, imm.
std::size_t movable_length(const unsigned char* code) {
    if (code[0] == 0x55 || (code[0] >= 0x50 && code[0] <= 0x57)) return 1;                      // push reg
    if (code[0] == 0x41 && code[1] >= 0x50 && code[1] <= 0x57) return 2;                        // push r8-r15
    if ((code[0] == 0x48 || code[0] == 0x49 || code[0] == 0x4c || code[0] == 0x4d) && code[1] == 0x89 &&
        (code[2] & 0xc0) == 0xc0) return 3;                                                      // mov reg, reg
    if (code[0] == 0x48 && code[1] == 0x83 && code[2] == 0xec) return 4;                        // sub rsp, imm8
    if (code[0] == 0x48 && code[1] == 0x81 && code[2] == 0xec) return 7;                        // sub rsp, imm32
    return 0;
}

// "jmp [rip+0]" followed by the address: 14 bytes that change no register (al still holds a
// variadic call's vector-register count).
void write_absolute_jump(unsigned char* at, const void* destination) {
    const unsigned char jump[6] = {0xff, 0x25, 0, 0, 0, 0};
    const auto address = reinterpret_cast<std::uint64_t>(destination);
    std::memcpy(at, jump, sizeof(jump));
    std::memcpy(at + sizeof(jump), &address, sizeof(address));
}

// Executable memory within reach of a 32-bit jump from GT7's code.
unsigned char* allocate_near_gt7(std::size_t size) {
    static std::mutex mutex;
    static unsigned char* block = nullptr;
    static std::size_t used = 0;
    std::lock_guard<std::mutex> lk(mutex);
    if (!block) {
        auto base = reinterpret_cast<std::uintptr_t>(GetModuleHandleA(nullptr));
        for (std::uintptr_t candidate = base - 0x100000; candidate > base - 0x70000000 && !block; candidate -= 0x100000)
            block = static_cast<unsigned char*>(VirtualAlloc(reinterpret_cast<void*>(candidate), 0x10000,
                MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE));
        if (!block) return nullptr;
    }
    if (used + size > 0x10000) return nullptr;
    unsigned char* result = block + used;
    used += (size + 15) & ~std::size_t(15);
    return result;
}

void* hook_gt7(std::uintptr_t elf_address, const void* replacement) {
    auto* target = reinterpret_cast<unsigned char*>(GetModuleHandleA(nullptr)) + 0x10000 + elf_address;
    std::size_t moved = 0;
    while (moved < 5) {
        const std::size_t length = movable_length(target + moved);
        if (length == 0) {
            game_log_line("probe at %#llx: unexpected code, not installed", static_cast<unsigned long long>(elf_address));
            return nullptr;
        }
        moved += length;
    }
    // GT7 jumps (5 bytes) to a relay next to it, which jumps to the replacement; the trampoline runs
    // the moved instructions and jumps back.
    unsigned char* relay = allocate_near_gt7(14);
    unsigned char* trampoline = allocate_near_gt7(moved + 14);
    if (!relay || !trampoline) return nullptr;
    write_absolute_jump(relay, replacement);
    std::memcpy(trampoline, target, moved);
    write_absolute_jump(trampoline + moved, target + moved);
    FlushInstructionCache(GetCurrentProcess(), relay, 14);
    FlushInstructionCache(GetCurrentProcess(), trampoline, moved + 14);
    unsigned char jump[5] = {0xe9, 0, 0, 0, 0};
    const auto offset = static_cast<std::int32_t>(relay - (target + 5));
    std::memcpy(jump + 1, &offset, sizeof(offset));
    DWORD old = 0;
    if (!VirtualProtect(target, sizeof(jump), PAGE_EXECUTE_READWRITE, &old)) return nullptr;
    std::memcpy(target, jump, sizeof(jump));
    VirtualProtect(target, sizeof(jump), old, &old);
    FlushInstructionCache(GetCurrentProcess(), target, sizeof(jump));
    return trampoline;
}

// Reads a C string from GT7 memory that may not be valid.
const char* guest_text(const void* pointer) {
    MEMORY_BASIC_INFORMATION region{};
    if (!pointer || !VirtualQuery(pointer, &region, sizeof(region)) || region.State != MEM_COMMIT ||
        (region.Protect & (PAGE_NOACCESS | PAGE_GUARD)))
        return "?";
    return static_cast<const char*>(pointer);
}

std::uint64_t guest_read(const void* base, std::size_t offset, std::size_t size = 8) {
    std::uint64_t value = 0;
    MEMORY_BASIC_INFORMATION region{};
    const auto* address = static_cast<const unsigned char*>(base) + offset;
    if (base && VirtualQuery(address, &region, sizeof(region)) && region.State == MEM_COMMIT &&
        !(region.Protect & (PAGE_NOACCESS | PAGE_GUARD)))
        std::memcpy(&value, address, size);
    return value;
}

using Sysv6 = void*(__attribute__((sysv_abi))*)(void*, void*, void*, void*, void*, void*);

// Vegas client: reports a failed API call (it builds the "vegas_api_failed" analytics event). The
// second argument is the call: error code at +0x38, domain and operation names at +0x40/+0x48.
Sysv6 g_report_api_failure = nullptr;
__attribute__((sysv_abi)) void* probe_api_failure(void* a, void* call, void* c, void* d, void* e, void* f) {
    game_log_line("vegas api failed: %s.%s code %#x",
        guest_text(reinterpret_cast<const char*>(guest_read(call, 0x40))),
        guest_text(reinterpret_cast<const char*>(guest_read(call, 0x48))), static_cast<unsigned>(guest_read(call, 0x38, 4)));
    return g_report_api_failure(a, call, c, d, e, f);
}

// A string object in GT7 (MSVC layout: 16-byte inline buffer or pointer, size, capacity).
std::string guest_string(const void* object) {
    const std::uint64_t size = guest_read(object, 0x10), capacity = guest_read(object, 0x18);
    if (size > 4096) return "?";
    const void* data = capacity >= 0x10 ? reinterpret_cast<const void*>(guest_read(object, 0)) : object;
    std::string text(size, '\0');
    for (std::uint64_t i = 0; i < size; ++i) text[i] = static_cast<char>(guest_read(data, i, 1));
    return text;
}

// Vegas client: decides whether a request may be sent (first argument: the client's state, 0
// refuses everything; second: the request, whose host string is at [[[req]+0x40]+0x1b8]+0x68).
Sysv6 g_may_send = nullptr;
__attribute__((sysv_abi)) void* probe_may_send(void* state, void* request, void* c, void* d, void* e, void* f) {
    void* result = g_may_send(state, request, c, d, e, f);
    const std::uint64_t req = guest_read(request, 0), owner = guest_read(reinterpret_cast<void*>(req), 0x40);
    const std::uint64_t host = guest_read(reinterpret_cast<void*>(owner), 0x1b8);
    game_log_line("vegas may send: state %d host '%s' -> %d", static_cast<int>(reinterpret_cast<std::uintptr_t>(state)),
        guest_string(reinterpret_cast<const unsigned char*>(host) + 0x68).c_str(),
        static_cast<int>(reinterpret_cast<std::uintptr_t>(result) & 0xff));
    return result;
}

// Vegas sign-in: two routes (0x2d4b910 and 0x2d4d590) that, after a successful call, store the
// token and set the client's "signed in" flag; 0x2d4b780 and 0x2d4be00 call them.
// Each traced call is logged with its result (the first 5 calls, then every 500th, since some are
// polled every frame).
#define RYGT_TRACE_PROBE(name, address)                                                                        \
    Sysv6 g_##name = nullptr;                                                                                  \
    std::atomic<unsigned> g_##name##_calls{0};                                                                 \
    __attribute__((sysv_abi)) void* probe_##name(void* a, void* b, void* c, void* d, void* e, void* f) {        \
        const unsigned call = ++g_##name##_calls;                                                              \
        const bool show = call <= 5 || call % 500 == 0;                                                        \
        if (show) game_log_line("enter " #name " #%u", call);                                                  \
        void* result = g_##name(a, b, c, d, e, f);                                                             \
        if (show) game_log_line("leave " #name " #%u -> %#llx", call,                                          \
            static_cast<unsigned long long>(reinterpret_cast<std::uintptr_t>(result)));                        \
        return result;                                                                                         \
    }
// Vegas sign-in: two routes (0x2d4b910 and 0x2d4d590) that, after a successful call, store the
// token and set the client's "signed in" flag; the token manager's callback (0x2d4b780) calls
// Client::Impl::signIn (0x2d4be00), which calls them.
RYGT_TRACE_PROBE(sign_in_a, 0x2d4b910)
RYGT_TRACE_PROBE(token_callback, 0x2d4b780)
RYGT_TRACE_PROBE(sign_in_b, 0x2d4d590)
RYGT_TRACE_PROBE(client_sign_in, 0x2d4be00)
// MLoginGT7, the scripts' login module (adhoc/modules/pdiapp/src/MLoginGT7.cpp): its methods, from
// its registration table at 0x2d303b0, and the steps of its login.
RYGT_TRACE_PROBE(login_isOnline, 0x2d30a10)
RYGT_TRACE_PROBE(login_isRunning, 0x2d30ba0)
RYGT_TRACE_PROBE(login_backgroundLogin, 0x2d30d30)
RYGT_TRACE_PROBE(login_sync, 0x2d30ec0)
RYGT_TRACE_PROBE(login_sync2, 0x2d31050)
RYGT_TRACE_PROBE(login_makeSureLogin, 0x2d311e0)
RYGT_TRACE_PROBE(login_checkEula, 0x2d315b0)
RYGT_TRACE_PROBE(login_isParentalLock, 0x2d31a70)
RYGT_TRACE_PROBE(login_getLastError, 0x2d31bf0)
RYGT_TRACE_PROBE(login_getRetMap, 0x2d31fd0)
RYGT_TRACE_PROBE(login_isWaitingUi, 0x2d329a0)
RYGT_TRACE_PROBE(login_getUiFunction, 0x2d32cd0)
RYGT_TRACE_PROBE(login_requestNpAuthorizationCode, 0x2d33210)
RYGT_TRACE_PROBE(login_step_auth_code, 0x2d337c0)
RYGT_TRACE_PROBE(login_step_auth_code_caller, 0x2d335e0)
RYGT_TRACE_PROBE(login_step_network_online, 0x2d36190)
RYGT_TRACE_PROBE(login_step_network_online_caller, 0x2d360c0)
RYGT_TRACE_PROBE(login_step_check_eula, 0x2d33f10)
RYGT_TRACE_PROBE(login_step_after_login, 0x2d34b10)
RYGT_TRACE_PROBE(login_step_after_login_caller, 0x2d36070)
// GT7's PSN web API wrapper: the user context is made on demand (0x30c9135 -> 0x30c9160, which
// first initialises the library through 0x30cb310); requests (0x30c91b0) fail with 0x80553404
// (PE-0553404) when it was never made.
RYGT_TRACE_PROBE(webapi_entry, 0x30c9135)
RYGT_TRACE_PROBE(webapi_user_context, 0x30c9160)
RYGT_TRACE_PROBE(webapi_initialize, 0x30cb310)
#undef RYGT_TRACE_PROBE

// Vegas client: applies a call's security scheme ("bearerAuth", "accessKeyAuth", "apiKeyAuth";
// third argument -> scheme string object at +8). Without a token or a sign-in under way, a
// bearerAuth call fails here with 0xce210717 (CE-210717) before anything is sent.
Sysv6 g_apply_auth = nullptr;
__attribute__((sysv_abi)) void* probe_client_sign_in(void*, void*, void*, void*, void*, void*);
std::atomic<bool> g_forced_sign_in{false};
__attribute__((sysv_abi)) void* probe_apply_auth(void* client, void* call, void* requirement, void* d, void* e, void* f) {
    void* result = g_apply_auth(client, call, requirement, d, e, f);
    auto code = static_cast<unsigned>(reinterpret_cast<std::uintptr_t>(result));
    if (code != 0) {
        const std::uint64_t scheme = guest_read(requirement, 0);
        game_log_line("vegas auth refused: scheme '%s' -> %#x",
            guest_string(reinterpret_cast<const unsigned char*>(scheme) + 8).c_str(), code);
    }
    // Experiment: nothing in this setup makes the client sign in, so sign in here, once, the way
    // the token manager's callback does (Client::Impl::signIn(client)), and check again.
    if (code == 0xce210717u && !g_forced_sign_in.exchange(true)) {
        game_log_line("forcing Client::Impl::signIn");
        // Called directly: Client::Impl::signIn (0x2d4be00) is not hooked.
        const auto sign_in = reinterpret_cast<Sysv6>(reinterpret_cast<unsigned char*>(GetModuleHandleA(nullptr)) + 0x10000 + 0x2d4be00);
        sign_in(client, nullptr, nullptr, nullptr, nullptr, nullptr);
        result = g_apply_auth(client, call, requirement, d, e, f);
        code = static_cast<unsigned>(reinterpret_cast<std::uintptr_t>(result));
        game_log_line("after forced sign-in: auth -> %#x", code);
    }
    return result;
}

// GT7's network stack set-up for PSN's web API (sceNetPoolCreate, sceSslInit, sceHttp2Init): its
// arguments include the pool sizes it checks first.
Sysv6 g_net_stack_init = nullptr;
__attribute__((sysv_abi)) void* probe_net_stack_init(void* a, void* b, void* c, void* d, void* e, void* f) {
    void* result = g_net_stack_init(a, b, c, d, e, f);
    game_log_line("net stack init(%p, %p, %#llx, %#llx, %#llx, %#llx) -> %#llx", a, b,
        (unsigned long long)(std::uintptr_t)c, (unsigned long long)(std::uintptr_t)d, (unsigned long long)(std::uintptr_t)e,
        (unsigned long long)(std::uintptr_t)f, (unsigned long long)((std::uintptr_t)result & 0xff));
    if (a)
        game_log_line("  state: pool %d ssl %d http2 %d error %#x", (int)guest_read(a, 4, 4), (int)guest_read(a, 0xc, 4),
            (int)guest_read(a, 8, 4), (unsigned)guest_read(a, 0x10, 4));
    return result;
}

#define RYGT_INSTALL_PROBE(name, address) \
    g_##name = reinterpret_cast<Sysv6>(hook_gt7(address, reinterpret_cast<const void*>(&probe_##name)))

void install_gt7_probes() {
    g_report_api_failure = reinterpret_cast<Sysv6>(hook_gt7(0x2d6e950, reinterpret_cast<const void*>(&probe_api_failure)));
    g_may_send = reinterpret_cast<Sysv6>(hook_gt7(0x2d6ba90, reinterpret_cast<const void*>(&probe_may_send)));
    g_apply_auth = reinterpret_cast<Sysv6>(hook_gt7(0x2d514f0, reinterpret_cast<const void*>(&probe_apply_auth)));
    // The traced functions (RYGT_TRACE_PROBE) are not hooked: the relay keeps only rax and the six
    // register arguments, which breaks functions returning rax:rdx or taking stack arguments.
    g_net_stack_init = reinterpret_cast<Sysv6>(hook_gt7(0x33610b0, reinterpret_cast<const void*>(&probe_net_stack_init)));
}
#undef RYGT_INSTALL_PROBE

void install_game_log() {
    static std::once_flag once;
    std::call_once(once, [] {
        // RYGT_GAME_LOG=1, or game_log=1 in RyGT.ini.
        const char* enabled = std::getenv("RYGT_GAME_LOG");
        bool on = enabled && std::strcmp(enabled, "1") == 0;
        if (!enabled) {
            if (std::FILE* ini = std::fopen("RyGT.ini", "r")) {
                char line[256];
                while (std::fgets(line, sizeof(line), ini))
                    if (std::strncmp(line, "game_log=1", 10) == 0) on = true;
                std::fclose(ini);
            }
        }
        if (!on) return;
        constexpr std::uintptr_t LOGGER = 0x10000 + 0x3372ac0;  // ELF address 0x3372ac0 in .elf0
        static const unsigned char prologue[] = {0x55, 0x48, 0x89, 0xe5, 0x41, 0x57, 0x41, 0x56, 0x41, 0x55, 0x41, 0x54, 0x53};
        auto* target = reinterpret_cast<unsigned char*>(GetModuleHandleA(nullptr)) + LOGGER;
        MEMORY_BASIC_INFORMATION region{};
        if (!VirtualQuery(target, &region, sizeof(region)) || region.State != MEM_COMMIT) return;
        if (std::memcmp(target, prologue, sizeof(prologue)) != 0) {
            std::fprintf(stderr, "[online] GT7 game log: logger not found (%02x %02x %02x %02x %02x)\n",
                target[0], target[1], target[2], target[3], target[4]);
            return;
        }
        g_guest_vsnprintf = reinterpret_cast<GuestVsnprintf>(
            reinterpret_cast<void*>(GetProcAddress(GetModuleHandleA("libc.prx"), "Q2V+iqvjgC0")));  // vsnprintf
        if (!g_guest_vsnprintf) {
            std::fprintf(stderr, "[online] GT7 game log: no vsnprintf in libc.prx\n");
            return;
        }
        g_game_log = std::fopen("gt7_game.log", "w");
        if (!g_game_log) return;
        if (!write_jump(target, reinterpret_cast<const void*>(&gt7_log))) return;
        std::fprintf(stderr, "[online] GT7 game log: writing gt7_game.log\n");
        install_gt7_probes();
    });
}

// Install as early as possible, so the log covers the boot: this library loads before the game
// starts, but the hook needs RyGT's libc, which may load after it.
struct EarlyGameLog {
    EarlyGameLog() {
        std::thread([] {
            for (int waited = 0; waited < 20000 && !GetModuleHandleA("libc.prx"); ++waited)
                std::this_thread::sleep_for(std::chrono::milliseconds(1));
            install_game_log();
        }).detach();
    }
} g_early_game_log;
#else
void install_game_log() {}
#endif

int APS5_VABI sceNetInit_nid_postfix(void) {
    std::lock_guard<std::mutex> lk(g_mutex);
    install_game_log();
    g_net_inited = true;
    return 0;
}

int APS5_VABI sceNetTerm(void) {
    std::lock_guard<std::mutex> lk(g_mutex);
    g_net_inited = false;
    return 0;
}

int APS5_VABI sceNetPoolCreate(const char* name, int size, int flags) {
    (void)name;
    (void)flags;
    if (size <= 0) {
        return fail(NET_EINVAL);
    }
    std::lock_guard<std::mutex> lk(g_mutex);
    const int id = g_next_pool++;
    g_pools.insert(id);
    std::fprintf(stderr, "[net] sceNetPoolCreate(%s, %d) -> %d\n", name ? name : "", size, id);
    return id;
}

int APS5_VABI sceNetPoolDestroy(int memid) {
    std::lock_guard<std::mutex> lk(g_mutex);
    return g_pools.erase(memid) != 0 ? 0 : fail(NET_EBADF);
}

int APS5_VABI sceNetSocket(const char* name, int family, int type, int protocol) {
    install_game_log();
    (void)name;
    if (family != NET_AF_INET && family != NET_AF_INET6) {
        return fail(NET_EAFNOSUPPORT);
    }
    if (type == NET_SOCK_DGRAM_P2P || type == NET_SOCK_STREAM_P2P) {
        if (family != NET_AF_INET) return fail(NET_EAFNOSUPPORT);
        if (!initialize_sockets()) return fail(5);
        sockaddr_in mailbox{};
        const NativeSocket native = p2p_open_mailbox(mailbox);
        if (native == INVALID_NATIVE_SOCKET) return fail(native_error());
        std::lock_guard<std::mutex> lk(g_mutex);
        const int fd = g_next_sock++;
        Sock s;
        s.family = family;
        s.type = type;
        s.p2p = true;
        s.mailbox = mailbox;
        s.native = std::make_shared<NativeSocketHandle>(native);
        g_socks[fd] = s;
        return fd;
    }
    if (type != NET_SOCK_STREAM && type != NET_SOCK_DGRAM && type != NET_SOCK_RAW) {
        return fail(NET_EPROTONOSUPPORT);
    }
    if (!initialize_sockets()) return fail(5);
    const NativeSocket native = ::socket(family == NET_AF_INET ? AF_INET : AF_INET6, type, protocol);
    if (native == INVALID_NATIVE_SOCKET) return fail(native_error());
    std::shared_ptr<NativeSocketHandle> handle;
    try {
        handle = std::make_shared<NativeSocketHandle>(native);
    } catch (const std::bad_alloc&) {
        close_socket(native);
        return fail(12);
    }
    std::lock_guard<std::mutex> lk(g_mutex);
    const int fd = g_next_sock++;
    Sock s;
    s.family = family;
    s.type = type;
    s.native = std::move(handle);
    g_socks[fd] = s;
    return fd;
}

int APS5_VABI sceNetSocketClose(int s) {
    std::shared_ptr<NativeSocketHandle> native;
    {
        std::lock_guard<std::mutex> lk(g_mutex);
        const auto it = g_socks.find(s);
        if (it == g_socks.end()) return fail(NET_EBADF);
        native = it->second.native;
        if (it->second.p2p && it->second.listening) {
            P2PStreamTransport& streams = p2p_streams();
            std::lock_guard<std::mutex> streams_lock(streams.mutex);
            auto listener = streams.listeners.find(it->second.vport);
            if (listener != streams.listeners.end() &&
                listener->second.doorbell.sin_port == it->second.mailbox.sin_port) {
                for (auto& pending : listener->second.pending) close_socket(pending.socket);
                streams.listeners.erase(listener);
            }
        } else if (it->second.p2p && it->second.type == NET_SOCK_DGRAM_P2P && it->second.bound) {
            P2PTransport& transport = p2p_transport();
            std::lock_guard<std::mutex> transport_lock(transport.mutex);
            auto mailbox = transport.mailboxes.find(it->second.vport);
            if (mailbox != transport.mailboxes.end() && mailbox->second.sin_port == it->second.mailbox.sin_port)
                transport.mailboxes.erase(mailbox);
        }
        g_socks.erase(it);
    }
    if (native) {
#ifdef _WIN32
        ::shutdown(native->value, SD_BOTH);
#else
        ::shutdown(native->value, SHUT_RDWR);
#endif
    }
    g_cv.notify_all();
    return 0;
}

int APS5_VABI sceNetSocketAbort(int s, int flags) {
    (void)flags;
    std::shared_ptr<NativeSocketHandle> native;
    {
        std::lock_guard<std::mutex> lk(g_mutex);
        auto it = g_socks.find(s);
        if (it == g_socks.end()) return fail(NET_EBADF);
        it->second.aborted = true;
        native = it->second.native;
    }
#ifdef _WIN32
    if (native) ::shutdown(native->value, SD_BOTH);
#else
    if (native) ::shutdown(native->value, SHUT_RDWR);
#endif
    g_cv.notify_all();
    return 0;
}

int APS5_VABI sceNetBind_nid_postfix(int s, const void* addr, uint32_t addrlen) {
    Sock socket;
    {
        std::lock_guard<std::mutex> lk(g_mutex);
        auto it = g_socks.find(s);
        if (it == g_socks.end()) return fail(NET_EBADF);
        socket = it->second;
        if (it->second.bound) return fail(NET_EINVAL);
    }
    if (socket.p2p) {
        if (!addr || addrlen < 10) return fail(NET_EINVAL);
        const auto* bytes = static_cast<const std::uint8_t*>(addr);
        std::uint16_t port;
        std::uint16_t vport;
        std::memcpy(&port, bytes + 2, 2);
        std::memcpy(&vport, bytes + 8, 2);
        if (socket.type == NET_SOCK_STREAM_P2P) {
            // The vport is claimed by listen(); a connecting stream socket only names its source vport.
            const int error = p2p_start_stream_transport(ntohs(port));
            if (error != 0) return fail(error);
            std::lock_guard<std::mutex> lk(g_mutex);
            auto it = g_socks.find(s);
            if (it != g_socks.end()) {
                it->second.bound = true;
                it->second.vport = ntohs(vport);
            }
            return 0;
        }
        const int error = p2p_start_transport(ntohs(port));
        if (error != 0) return fail(error);
        {
            P2PTransport& transport = p2p_transport();
            std::lock_guard<std::mutex> transport_lock(transport.mutex);
            if (transport.mailboxes.count(ntohs(vport)) != 0) return fail(NET_EADDRINUSE);
            transport.mailboxes[ntohs(vport)] = socket.mailbox;
        }
        std::lock_guard<std::mutex> lk(g_mutex);
        auto it = g_socks.find(s);
        if (it != g_socks.end()) {
            it->second.bound = true;
            it->second.vport = ntohs(vport);
        }
        return 0;
    }
    sockaddr_storage native{};
    NativeLength native_length = 0;
    if (!guest_to_native_address(addr, addrlen, native, native_length)) return fail(NET_EINVAL);
    if (native.ss_family != (socket.family == NET_AF_INET ? AF_INET : AF_INET6)) return fail(NET_EAFNOSUPPORT);
    if (::bind(socket.native->value, reinterpret_cast<const sockaddr*>(&native), native_length) != 0)
        return fail(native_error());
    std::lock_guard<std::mutex> lk(g_mutex);
    auto it = g_socks.find(s);
    if (it != g_socks.end()) it->second.bound = true;
    return 0;
}

int APS5_VABI sceNetListen(int s, int backlog) {
    Sock socket;
    {
        std::lock_guard<std::mutex> lk(g_mutex);
        auto it = g_socks.find(s);
        if (it == g_socks.end()) return fail(NET_EBADF);
        socket = it->second;
    }
    if (socket.p2p) {
        if (socket.type != NET_SOCK_STREAM_P2P) return fail(NET_EOPNOTSUPP);
        const int error = p2p_start_stream_transport(0);
        if (error != 0) return fail(error);
        {
            P2PStreamTransport& streams = p2p_streams();
            std::lock_guard<std::mutex> streams_lock(streams.mutex);
            if (streams.listeners.count(socket.vport) != 0) return fail(NET_EADDRINUSE);
            streams.listeners[socket.vport].doorbell = socket.mailbox;
        }
        p2p_trace("listen() on vport %u", socket.vport);
        std::lock_guard<std::mutex> lk(g_mutex);
        auto it = g_socks.find(s);
        if (it != g_socks.end()) {
            it->second.bound = true;
            it->second.listening = true;
        }
        return 0;
    }
    if (socket.type != NET_SOCK_STREAM) return fail(NET_EOPNOTSUPP);
    if (::listen(socket.native->value, backlog) != 0) return fail(native_error());
    std::lock_guard<std::mutex> lk(g_mutex);
    auto it = g_socks.find(s);
    if (it != g_socks.end()) {
        it->second.bound = true;
        it->second.listening = true;
    }
    return 0;
}

int APS5_VABI sceNetAccept(int s, void* addr, uint32_t* addrlen) {
    Sock listener;
    {
        std::lock_guard<std::mutex> lk(g_mutex);
        auto it = g_socks.find(s);
        if (it == g_socks.end()) return fail(NET_EBADF);
        listener = it->second;
    }
    if (!listener.listening) return fail(NET_EINVAL);
    if (addr && !addrlen) return fail(NET_EINVAL);
    if (listener.p2p) {
        char bell;
        if (::recv(listener.native->value, &bell, 1, 0) < 0) return fail(native_error());
        P2PPendingStream pending{};
        {
            P2PStreamTransport& streams = p2p_streams();
            std::lock_guard<std::mutex> streams_lock(streams.mutex);
            auto it = streams.listeners.find(listener.vport);
            if (it == streams.listeners.end() || it->second.pending.empty()) {
                p2p_trace("accept() on vport %u: doorbell rang but nothing is queued", listener.vport);
                return fail(NET_EAGAIN);
            }
            pending = it->second.pending.front();
            it->second.pending.pop_front();
        }
        p2p_trace("accept() on vport %u: %s", listener.vport,
                  p2p_text(&pending.from.sin_addr, pending.from.sin_port, pending.source_vport).c_str());
        Sock connection;
        connection.family = NET_AF_INET;
        connection.type = NET_SOCK_STREAM_P2P;
        connection.p2p = true;
        connection.bound = true;
        connection.vport = listener.vport;
        connection.peer = pending.from;
        connection.peer_vport = ntohs(pending.source_vport);
        connection.native = std::make_shared<NativeSocketHandle>(pending.socket);
        int descriptor;
        {
            std::lock_guard<std::mutex> lk(g_mutex);
            descriptor = g_next_sock++;
            g_socks.emplace(descriptor, connection);
        }
        if (addr) {
            // The peer's P2P port is the listening port, not the TCP source port it connected from.
            const std::uint16_t port = htons(p2p_configured_port(0));
            p2p_guest_address(addr, addrlen, &pending.from.sin_addr, &port, &pending.source_vport);
        }
        return descriptor;
    }
    sockaddr_storage peer{};
    NativeLength peer_length = sizeof(peer);
    const NativeSocket accepted = ::accept(listener.native->value,
        addr ? reinterpret_cast<sockaddr*>(&peer) : nullptr, addr ? &peer_length : nullptr);
    if (accepted == INVALID_NATIVE_SOCKET) return fail(native_error());
    std::shared_ptr<NativeSocketHandle> handle;
    try {
        handle = std::make_shared<NativeSocketHandle>(accepted);
    } catch (const std::bad_alloc&) {
        close_socket(accepted);
        return fail(12);
    }
    Sock connection;
    connection.family = listener.family;
    connection.type = NET_SOCK_STREAM;
    connection.native = std::move(handle);
    int descriptor;
    {
        std::lock_guard<std::mutex> lk(g_mutex);
        descriptor = g_next_sock++;
        g_socks.emplace(descriptor, connection);
    }
    if (addr && !native_to_guest_address(peer, addr, addrlen)) {
        sceNetSocketClose(descriptor);
        return fail(NET_EAFNOSUPPORT);
    }
    return descriptor;
}

// Addresses sceNetResolverStartNtoa gave out for Polyphony's hosts (RyGTServer's): connections to
// them on the HTTPS/HTTP ports go to RyGTServer's port instead (see sceNetConnect).
std::mutex g_polyphony_mutex;
std::set<std::uint32_t> g_polyphony_addresses;

int APS5_VABI sceNetConnect(int s, const void* addr, uint32_t addrlen) {
    Sock socket;
    {
        std::lock_guard<std::mutex> lk(g_mutex);
        auto it = g_socks.find(s);
        if (it == g_socks.end()) return fail(NET_EBADF);
        socket = it->second;
    }
    if (socket.p2p) {
        if (socket.type != NET_SOCK_STREAM_P2P || socket.listening) return fail(NET_EOPNOTSUPP);
        if (!addr || addrlen < 10) return fail(NET_EINVAL);
        const auto* bytes = static_cast<const std::uint8_t*>(addr);
        sockaddr_in destination{};
        destination.sin_family = AF_INET;
        std::memcpy(&destination.sin_port, bytes + 2, 2);
        std::memcpy(&destination.sin_addr, bytes + 4, 4);
        if (destination.sin_port == 0) destination.sin_port = htons(p2p_configured_port(0));
        std::uint16_t destination_vport;
        std::memcpy(&destination_vport, bytes + 8, 2);
        const NativeSocket tcp = ::socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        if (tcp == INVALID_NATIVE_SOCKET) return fail(native_error());
        auto handle = std::make_shared<NativeSocketHandle>(tcp);
        int no_delay = 1;
        ::setsockopt(tcp, IPPROTO_TCP, TCP_NODELAY, reinterpret_cast<const char*>(&no_delay), sizeof(no_delay));
        if (::connect(tcp, reinterpret_cast<const sockaddr*>(&destination), sizeof(destination)) != 0) {
            const int error = native_error();
            p2p_trace("connect() to %s failed (%d)", p2p_text(&destination.sin_addr, destination.sin_port, destination_vport).c_str(), error);
            return fail(error);
        }
        p2p_trace("connect() to %s: connected", p2p_text(&destination.sin_addr, destination.sin_port, destination_vport).c_str());
        char header[P2P_WIRE_HEADER];
        header[0] = static_cast<char>(P2P_MAGIC[0]);
        header[1] = static_cast<char>(P2P_MAGIC[1]);
        const std::uint16_t source_vport = htons(socket.vport);
        std::memcpy(header + 2, &destination_vport, 2);
        std::memcpy(header + 4, &source_vport, 2);
        if (::send(tcp, header, static_cast<int>(P2P_WIRE_HEADER), 0) != static_cast<int>(P2P_WIRE_HEADER))
            return fail(native_error());
        if (socket.nonblock) {
#ifdef _WIN32
            u_long enabled = 1;
            ioctlsocket(tcp, FIONBIO, &enabled);
#else
            fcntl(tcp, F_SETFL, fcntl(tcp, F_GETFL, 0) | O_NONBLOCK);
#endif
        }
        std::lock_guard<std::mutex> lk(g_mutex);
        auto it = g_socks.find(s);
        if (it == g_socks.end()) return fail(NET_EBADF);
        it->second.native = handle;  // the doorbell socket closes with its last reference
        it->second.peer = destination;
        it->second.peer_vport = ntohs(destination_vport);
        return 0;
    }
    sockaddr_storage native{};
    NativeLength native_length = 0;
    if (!guest_to_native_address(addr, addrlen, native, native_length)) return fail(NET_EINVAL);
    if (native.ss_family != (socket.family == NET_AF_INET ? AF_INET : AF_INET6)) return fail(NET_EAFNOSUPPORT);
    if (native.ss_family == AF_INET) {
        auto& destination = reinterpret_cast<sockaddr_in&>(native);
        const auto port = ntohs(destination.sin_port);
        bool polyphony = false;
        {
            std::lock_guard<std::mutex> lk(g_polyphony_mutex);
            polyphony = g_polyphony_addresses.count(destination.sin_addr.s_addr) != 0;
        }
        if (polyphony && (port == 443 || port == 80)) {
            destination.sin_port = htons(RyGTOnline::Get().serverPort);
            log_soft(__func__, ("Polyphony server port " + std::to_string(port) + " sent to RyGTServer").c_str());
        }
    }
    if (::connect(socket.native->value, reinterpret_cast<const sockaddr*>(&native), native_length) == 0) return 0;
    // A non-blocking connect that is under way is EINPROGRESS on the PS4 (Windows says WOULDBLOCK);
    // games then wait for the socket to become writable.
    const int error = native_error();
    return fail(error == NET_EAGAIN ? 36 : error);
}

int64_t APS5_VABI sceNetRecvfrom(int s, void* buf, size_t len, int flags, void* from, uint32_t* fromlen);

int64_t APS5_VABI sceNetRecv(int s, void* buf, size_t len, int flags) {
    if (!buf && len) return fail(NET_EINVAL);
    if (len > INT_MAX) return fail(NET_EMSGSIZE);
    Sock socket;
    {
        std::lock_guard<std::mutex> lk(g_mutex);
        auto it = g_socks.find(s);
        if (it == g_socks.end()) return fail(NET_EBADF);
        socket = it->second;
    }
    if (socket.p2p && socket.type == NET_SOCK_DGRAM_P2P) return sceNetRecvfrom(s, buf, len, flags, nullptr, nullptr);
    int native_flags;
    if (!recv_flags(flags, socket, native_flags)) return fail(NET_EAGAIN);
    const int result = ::recv(socket.native->value, static_cast<char*>(buf), static_cast<int>(len), native_flags);
    if (result < 0) return stream_read_error(s, socket);
    if (socket.p2p) p2p_count("TCP in  from " + p2p_text(&socket.peer.sin_addr, htons(3658), htons(socket.peer_vport)) + (result == 0 ? " (closed)" : ""), result);
    return result;
}

int64_t APS5_VABI sceNetRecvfrom(int s, void* buf, size_t len, int flags, void* from, uint32_t* fromlen) {
    if (!buf && len) return fail(NET_EINVAL);
    if (len > INT_MAX) return fail(NET_EMSGSIZE);
    if (from && !fromlen) return fail(NET_EINVAL);
    Sock socket;
    {
        std::lock_guard<std::mutex> lk(g_mutex);
        auto it = g_socks.find(s);
        if (it == g_socks.end()) return fail(NET_EBADF);
        socket = it->second;
    }
    int native_flags;
    if (!recv_flags(flags, socket, native_flags)) return fail(NET_EAGAIN);
    if (socket.p2p && socket.type == NET_SOCK_DGRAM_P2P) {
        std::vector<char> datagram(P2P_MAILBOX_HEADER + len);
        const int got = ::recv(socket.native->value, datagram.data(), static_cast<int>(datagram.size()),
                               native_flags & MSG_PEEK);
#ifdef _WIN32
        // A guest buffer smaller than the datagram truncates it, as on the PS4.
        if (got < 0 && WSAGetLastError() == WSAEMSGSIZE) {
            if (from) p2p_guest_address(from, fromlen, datagram.data(), datagram.data() + 4, datagram.data() + 6);
            if (len) std::memcpy(buf, datagram.data() + P2P_MAILBOX_HEADER, len);
            return static_cast<int64_t>(len);
        }
#endif
        if (got < 0) return fail(native_error());
        if (static_cast<std::size_t>(got) < P2P_MAILBOX_HEADER) return fail(NET_EAGAIN);
        const std::size_t payload = static_cast<std::size_t>(got) - P2P_MAILBOX_HEADER;
        if (payload) std::memcpy(buf, datagram.data() + P2P_MAILBOX_HEADER, payload);
        if (from) p2p_guest_address(from, fromlen, datagram.data(), datagram.data() + 4, datagram.data() + 6);
        return static_cast<int64_t>(payload);
    }
    sockaddr_storage peer{};
    NativeLength peer_length = sizeof(peer);
    const int result = ::recvfrom(socket.native->value, static_cast<char*>(buf), static_cast<int>(len),
        native_flags, from ? reinterpret_cast<sockaddr*>(&peer) : nullptr, from ? &peer_length : nullptr);
    if (result < 0) return stream_read_error(s, socket);
    if (socket.p2p) p2p_count("TCP in  from " + p2p_text(&socket.peer.sin_addr, htons(3658), htons(socket.peer_vport)) + (result == 0 ? " (closed)" : ""), result);
    if (from && !native_to_guest_address(peer, from, fromlen)) return fail(NET_EAFNOSUPPORT);
    return result;
}

int64_t APS5_VABI sceNetSend(int s, const void* buf, size_t len, int flags) {
    if (!buf && len) return fail(NET_EINVAL);
    if (len > INT_MAX) return fail(NET_EMSGSIZE);
    Sock socket;
    {
        std::lock_guard<std::mutex> lk(g_mutex);
        auto it = g_socks.find(s);
        if (it == g_socks.end()) return fail(NET_EBADF);
        socket = it->second;
    }
    if (socket.p2p && socket.type == NET_SOCK_DGRAM_P2P) return fail(39);  // EDESTADDRREQ
    if (!send_ready(flags, socket)) return fail(NET_EAGAIN);
    const int result = ::send(socket.native->value, static_cast<const char*>(buf), static_cast<int>(len), 0);
    if (socket.p2p) p2p_count("TCP out to " + p2p_text(&socket.peer.sin_addr, htons(3658), htons(socket.peer_vport)) + (result >= 0 ? "" : " FAILED"), len);
    return result >= 0 ? result : fail(native_error());
}

int64_t APS5_VABI sceNetSendto(int s, const void* buf, size_t len, int flags, const void* to, uint32_t tolen) {
    if (!buf && len) return fail(NET_EINVAL);
    if (len > INT_MAX) return fail(NET_EMSGSIZE);
    Sock socket;
    {
        std::lock_guard<std::mutex> lk(g_mutex);
        auto it = g_socks.find(s);
        if (it == g_socks.end()) return fail(NET_EBADF);
        socket = it->second;
    }
    if (!(socket.p2p && socket.type == NET_SOCK_DGRAM_P2P) && !send_ready(flags, socket)) return fail(NET_EAGAIN);
    if (socket.p2p && socket.type == NET_SOCK_DGRAM_P2P) {
        if (!to || tolen < 10) return fail(39);  // EDESTADDRREQ: P2P sockets are never connected here
        if (len > P2P_MAX_DATAGRAM - P2P_WIRE_HEADER) return fail(NET_EMSGSIZE);
        const int error = p2p_start_transport(0);
        if (error != 0) return fail(error);
        const auto* address = static_cast<const std::uint8_t*>(to);
        if (socket.vport == P2P_RAW_VPORT) {
            sockaddr_in destination{};
            destination.sin_family = AF_INET;
            std::memcpy(&destination.sin_port, address + 2, 2);
            std::memcpy(&destination.sin_addr, address + 4, 4);
            const int sent = ::sendto(p2p_transport().socket, static_cast<const char*>(buf), static_cast<int>(len), 0,
                                      reinterpret_cast<const sockaddr*>(&destination), sizeof(destination));
            p2p_count("UDP out to " + p2p_text(&destination.sin_addr, destination.sin_port, 0) + " raw (signaling)" + (sent >= 0 ? "" : " FAILED"), len);
            return sent >= 0 ? static_cast<int64_t>(len) : fail(native_error());
        }
        std::vector<char> datagram(P2P_WIRE_HEADER + len);
        datagram[0] = static_cast<char>(P2P_MAGIC[0]);
        datagram[1] = static_cast<char>(P2P_MAGIC[1]);
        std::memcpy(datagram.data() + 2, address + 8, 2);  // destination vport, network order
        const std::uint16_t source_vport = htons(socket.vport);
        std::memcpy(datagram.data() + 4, &source_vport, 2);
        if (len) std::memcpy(datagram.data() + P2P_WIRE_HEADER, buf, len);
        sockaddr_in destination{};
        destination.sin_family = AF_INET;
        std::memcpy(&destination.sin_port, address + 2, 2);
        std::memcpy(&destination.sin_addr, address + 4, 4);
        if (destination.sin_port == 0) destination.sin_port = htons(p2p_configured_port(0));
        P2PTransport& transport = p2p_transport();
        const int sent = ::sendto(transport.socket, datagram.data(), static_cast<int>(datagram.size()), 0,
                                  reinterpret_cast<const sockaddr*>(&destination), sizeof(destination));
        p2p_count("UDP out from vport " + std::to_string(socket.vport) + " to " +
                  p2p_text(&destination.sin_addr, destination.sin_port, *reinterpret_cast<const std::uint16_t*>(address + 8)) +
                  (sent >= 0 ? "" : " FAILED"), len);
        return sent >= 0 ? static_cast<int64_t>(len) : fail(native_error());
    }
    int result;
    if (!to) {
        if (tolen != 0) return fail(NET_EINVAL);
        result = ::send(socket.native->value, static_cast<const char*>(buf), static_cast<int>(len), 0);
    } else {
        sockaddr_storage destination{};
        NativeLength destination_length = 0;
        if (!guest_to_native_address(to, tolen, destination, destination_length)) return fail(NET_EINVAL);
        if (destination.ss_family != (socket.family == NET_AF_INET ? AF_INET : AF_INET6)) return fail(NET_EAFNOSUPPORT);
        result = ::sendto(socket.native->value, static_cast<const char*>(buf), static_cast<int>(len), 0,
            reinterpret_cast<const sockaddr*>(&destination), destination_length);
    }
    return result >= 0 ? result : fail(native_error());
}

int APS5_VABI sceNetShutdown(int s, int how) {
    if (how < 0 || how > 2) return fail(NET_EINVAL);
    Sock socket;
    {
        std::lock_guard<std::mutex> lk(g_mutex);
        auto it = g_socks.find(s);
        if (it == g_socks.end()) return fail(NET_EBADF);
        socket = it->second;
    }
    return ::shutdown(socket.native->value, how) == 0 ? 0 : fail(native_error());
}

int APS5_VABI sceNetSetsockopt(int s, int level, int optname, const void* optval, uint32_t optlen) {
    Sock socket;
    {
        std::lock_guard<std::mutex> lk(g_mutex);
        auto it = g_socks.find(s);
        if (it == g_socks.end()) return fail(NET_EBADF);
        socket = it->second;
    }
    if (level != NET_SOL_SOCKET) return socket.p2p ? 0 : fail(NET_EOPNOTSUPP);
    if (!optval || optlen < sizeof(int)) return fail(NET_EINVAL);
    int value = 0;
    std::memcpy(&value, optval, sizeof(value));
    if (optname == NET_SO_NBIO) {
#ifdef _WIN32
        u_long enabled = value != 0;
        if (ioctlsocket(socket.native->value, FIONBIO, &enabled) != 0) return fail(native_error());
#else
        const int old_flags = fcntl(socket.native->value, F_GETFL, 0);
        if (old_flags < 0 || fcntl(socket.native->value, F_SETFL,
            value ? old_flags | O_NONBLOCK : old_flags & ~O_NONBLOCK) != 0) return fail(native_error());
#endif
    } else if (optname == NET_SO_RCVTIMEO || optname == NET_SO_SNDTIMEO) {
        if (value < 0) return fail(NET_EINVAL);
#ifdef _WIN32
        const DWORD timeout_ms = value == 0 ? 0 : static_cast<DWORD>((value + 999) / 1000);
        const int native_option = optname == NET_SO_RCVTIMEO ? SO_RCVTIMEO : SO_SNDTIMEO;
        if (::setsockopt(socket.native->value, SOL_SOCKET, native_option,
                         reinterpret_cast<const char*>(&timeout_ms), sizeof(timeout_ms)) != 0)
            return fail(native_error());
#else
        timeval timeout{};
        timeout.tv_sec = value / 1000000;
        timeout.tv_usec = value % 1000000;
        const int native_option = optname == NET_SO_RCVTIMEO ? SO_RCVTIMEO : SO_SNDTIMEO;
        if (::setsockopt(socket.native->value, SOL_SOCKET, native_option, &timeout, sizeof(timeout)) != 0)
            return fail(native_error());
#endif
    } else if (socket.p2p) {
        // SO_USECRYPTO, SO_USESIGNATURE, buffer sizes and the like: the transport handles these.
        return 0;
    } else if (optname == 0x4 || optname == 0x20 || optname == 0x1001 || optname == 0x1002) {
        // SO_REUSEADDR, SO_BROADCAST, SO_SNDBUF and SO_RCVBUF have the same meaning on the host.
        const int native_option = optname == 0x4 ? SO_REUSEADDR : optname == 0x20 ? SO_BROADCAST
                                : optname == 0x1001 ? SO_SNDBUF : SO_RCVBUF;
        if (::setsockopt(socket.native->value, SOL_SOCKET, native_option,
                         reinterpret_cast<const char*>(&value), sizeof(value)) != 0)
            return fail(native_error());
        return 0;
    } else {
        return fail(NET_EOPNOTSUPP);
    }
    std::lock_guard<std::mutex> lk(g_mutex);
    auto it = g_socks.find(s);
    if (it != g_socks.end()) {
        if (optname == NET_SO_NBIO) it->second.nonblock = value != 0;
        if (optname == NET_SO_RCVTIMEO) it->second.rcv_timeout_us = value;
        if (optname == NET_SO_SNDTIMEO) it->second.snd_timeout_us = value;
    }
    return 0;
}

int APS5_VABI sceNetGetsockopt(int s, int level, int optname, void* optval, uint32_t* optlen) {
    Sock socket;
    {
        std::lock_guard<std::mutex> lk(g_mutex);
        auto it = g_socks.find(s);
        if (it == g_socks.end()) return fail(NET_EBADF);
        socket = it->second;
    }
    if (optval == nullptr || optlen == nullptr || *optlen < sizeof(int)) {
        return fail(NET_EINVAL);
    }
    if (level != NET_SOL_SOCKET && !socket.p2p) return fail(NET_EOPNOTSUPP);
    int value = 0;
    switch (level != NET_SOL_SOCKET ? -1 : optname) {
        case NET_SO_NBIO: value = socket.nonblock ? 1 : 0; break;
        case NET_SO_RCVTIMEO: value = socket.rcv_timeout_us; break;
        case NET_SO_SNDTIMEO: value = socket.snd_timeout_us; break;
        case 0x1007: value = 0; break;            // SO_ERROR
        case 0x1008: value = socket.type; break;  // SO_TYPE
        case 0x1001:                              // SO_SNDBUF
        case 0x1002: {                            // SO_RCVBUF
            NativeLength length = sizeof(value);
            if (::getsockopt(socket.native->value, SOL_SOCKET, optname == 0x1001 ? SO_SNDBUF : SO_RCVBUF,
                             reinterpret_cast<char*>(&value), &length) != 0)
                value = 65536;
            break;
        }
        default:
            // P2P-only options (SO_USECRYPTO and the like) read back as off.
            if (!socket.p2p) return fail(NET_EOPNOTSUPP);
            value = 0;
            break;
    }
    std::memcpy(optval, &value, sizeof(value));
    *optlen = sizeof(value);
    return 0;
}

int APS5_VABI sceNetGetsockname(int s, void* addr, uint32_t* addrlen) {
    Sock socket;
    {
        std::lock_guard<std::mutex> lk(g_mutex);
        auto it = g_socks.find(s);
        if (it == g_socks.end()) return fail(NET_EBADF);
        socket = it->second;
    }
    if (!addr || !addrlen) return fail(NET_EINVAL);
    if (socket.p2p) {
        const std::uint32_t any = 0;
        const std::uint16_t port = htons(p2p_transport().port != 0 ? p2p_transport().port : p2p_configured_port(0));
        const std::uint16_t vport = htons(socket.vport);
        p2p_guest_address(addr, addrlen, &any, &port, &vport);
        return 0;
    }
    sockaddr_storage native{};
    NativeLength native_length = sizeof(native);
    if (::getsockname(socket.native->value, reinterpret_cast<sockaddr*>(&native), &native_length) != 0)
        return fail(native_error());
    return native_to_guest_address(native, addr, addrlen) ? 0 : fail(NET_EAFNOSUPPORT);
}

int APS5_VABI sceNetGetpeername(int s, void* addr, uint32_t* addrlen) {
    Sock socket;
    {
        std::lock_guard<std::mutex> lk(g_mutex);
        auto it = g_socks.find(s);
        if (it == g_socks.end()) return fail(NET_EBADF);
        socket = it->second;
    }
    if (!addr || !addrlen) return fail(NET_EINVAL);
    if (socket.p2p) {
        if (socket.peer.sin_family != AF_INET) return fail(57);  // ENOTCONN
        const std::uint16_t port = htons(p2p_configured_port(0));
        const std::uint16_t vport = htons(socket.peer_vport);
        p2p_guest_address(addr, addrlen, &socket.peer.sin_addr, &port, &vport);
        return 0;
    }
    sockaddr_storage native{};
    NativeLength native_length = sizeof(native);
    if (::getpeername(socket.native->value, reinterpret_cast<sockaddr*>(&native), &native_length) != 0)
        return fail(native_error());
    return native_to_guest_address(native, addr, addrlen) ? 0 : fail(NET_EAFNOSUPPORT);
}

int APS5_VABI sceNetSendmsg() {
    return fail(NET_EOPNOTSUPP);
}

int APS5_VABI sceNetRecvmsg() {
    return fail(NET_EOPNOTSUPP);
}

int APS5_VABI sceNetShowIfconfig() {
    return 0;
}

int APS5_VABI sceNetGetSockInfo(int s, void* info, int n, int flags) {
    (void)info;
    (void)n;
    (void)flags;
    std::lock_guard<std::mutex> lk(g_mutex);
    return g_socks.count(s) != 0 ? 0 : fail(NET_EBADF);
}

int APS5_VABI sceNetEpollCreate(const char* name, int flags) {
    (void)name;
    (void)flags;
    std::lock_guard<std::mutex> lk(g_mutex);
    const int id = g_next_epoll++;
    g_epolls.insert(id);
    g_epoll_socks.emplace(id, std::map<int, NetEpollEvent>{});
    return id;
}

int APS5_VABI sceNetEpollDestroy(int eid) {
    std::lock_guard<std::mutex> lk(g_mutex);
    if (g_epolls.erase(eid) == 0) {
        return fail(NET_EBADF);
    }
    g_epoll_socks.erase(eid);
    g_epoll_aborted.erase(eid);
    g_cv.notify_all();
    return 0;
}

int APS5_VABI sceNetEpollAbort(int eid, int flags) {
    (void)flags;
    std::lock_guard<std::mutex> lk(g_mutex);
    if (g_epolls.count(eid) == 0) {
        return fail(NET_EBADF);
    }
    g_epoll_aborted.insert(eid);
    g_cv.notify_all();
    return 0;
}

int APS5_VABI sceNetEpollControl(int eid, int op, int id, const NetEpollEvent* event) {
    std::lock_guard<std::mutex> lk(g_mutex);
    if (g_epolls.count(eid) == 0) {
        return fail(NET_EBADF);
    }
    if (op < 1 || op > 3) return fail(NET_EINVAL);
    if (op != 2 && !event) return fail(NET_EINVAL);
    if (op != 2 && g_socks.count(id) == 0) return fail(NET_EBADF);
    auto& registrations = g_epoll_socks[eid];
    if (op == 1) {
        if (!registrations.emplace(id, *event).second) return fail(NET_EADDRINUSE);
    } else if (op == 2) {
        if (registrations.erase(id) == 0) return fail(NET_ENOENT);
    } else {
        auto registration = registrations.find(id);
        if (registration == registrations.end()) return fail(NET_ENOENT);
        registration->second = *event;
    }
    g_cv.notify_all();
    return 0;
}

int APS5_VABI sceNetEpollWait(int eid, NetEpollEvent* events, int maxevents, int timeout) {
    if (!events || maxevents <= 0) return fail(NET_EINVAL);
    const auto deadline = timeout > 0
        ? std::chrono::steady_clock::now() + std::chrono::microseconds(timeout)
        : std::chrono::steady_clock::time_point::max();
    for (;;) {
        std::vector<NetEpollEvent> registered_events;
        std::vector<std::shared_ptr<NativeSocketHandle>> native_sockets;
        {
            std::lock_guard<std::mutex> lk(g_mutex);
            if (g_epolls.count(eid) == 0) return fail(NET_EBADF);
            if (g_epoll_aborted.erase(eid) != 0) return fail(NET_ECONNABORTED);
            const auto registration = g_epoll_socks.find(eid);
            if (registration != g_epoll_socks.end()) {
                for (const auto& entry : registration->second) {
                    const auto socket = g_socks.find(entry.first);
                    if (socket == g_socks.end()) continue;
                    registered_events.push_back(entry.second);
                    native_sockets.push_back(socket->second.native);
                }
            }
        }
        if (registered_events.empty()) {
            if (timeout == 0) return 0;
            std::unique_lock<std::mutex> lk(g_mutex);
            const auto now = std::chrono::steady_clock::now();
            if (timeout > 0 && now >= deadline) return 0;
            const auto slice = std::chrono::milliseconds(100);
            const auto wake = timeout > 0 ? std::min(deadline, now + slice) : now + slice;
            g_cv.wait_until(lk, wake);
            continue;
        }

#ifdef _WIN32
        std::vector<WSAPOLLFD> descriptors(registered_events.size());
        for (std::size_t index = 0; index < descriptors.size(); ++index) {
            descriptors[index].fd = native_sockets[index]->value;
            if (registered_events[index].events & 1) descriptors[index].events |= POLLRDNORM;
            if (registered_events[index].events & 4) descriptors[index].events |= POLLWRNORM;
        }
#else
        std::vector<pollfd> descriptors(registered_events.size());
        for (std::size_t index = 0; index < descriptors.size(); ++index) {
            descriptors[index].fd = native_sockets[index]->value;
            if (registered_events[index].events & 1) descriptors[index].events |= POLLIN;
            if (registered_events[index].events & 4) descriptors[index].events |= POLLOUT;
        }
#endif
        const auto now = std::chrono::steady_clock::now();
        if (timeout > 0 && now >= deadline) return 0;
        int wait_ms = 100;
        if (timeout == 0) wait_ms = 0;
        else if (timeout > 0) {
            const auto remaining = std::chrono::duration_cast<std::chrono::microseconds>(deadline - now).count();
            wait_ms = static_cast<int>(std::min<std::int64_t>(100, (remaining + 999) / 1000));
        }
#ifdef _WIN32
        const int ready_count = WSAPoll(descriptors.data(), static_cast<ULONG>(descriptors.size()), wait_ms);
#else
        const int ready_count = ::poll(descriptors.data(), descriptors.size(), wait_ms);
#endif
        if (ready_count < 0) return fail(native_error());
        {
            std::lock_guard<std::mutex> lk(g_mutex);
            if (g_epolls.count(eid) == 0) return fail(NET_EBADF);
            if (g_epoll_aborted.erase(eid) != 0) return fail(NET_ECONNABORTED);
        }
        if (ready_count > 0) {
            int count = 0;
            for (std::size_t index = 0; index < descriptors.size() && count < maxevents; ++index) {
                const auto native_events = descriptors[index].revents;
                if (!native_events) continue;
                std::uint32_t guest_events = 0;
#ifdef _WIN32
                if (native_events & (POLLRDNORM | POLLPRI)) guest_events |= 1;
                if (native_events & POLLWRNORM) guest_events |= 4;
#else
                if (native_events & (POLLIN | POLLPRI)) guest_events |= 1;
                if (native_events & POLLOUT) guest_events |= 4;
#endif
                if (native_events & POLLERR) guest_events |= 8;
                if (native_events & POLLHUP) guest_events |= 16;
                events[count] = registered_events[index];
                events[count].events = guest_events;
                ++count;
            }
            if (count > 0) return count;
        }
        if (timeout == 0) return 0;
    }
}

// select() over network sockets, for runtimes whose libkernel has none (GT7's): see libkernel's
// Socket.cpp, which does the same through these epoll calls. fd_set is FreeBSD's (a bit per
// descriptor in 64-bit words), timeval two 64-bit fields.
int APS5_VABI select_nid_postfix(int nfds, void* readfds, void* writefds, void* exceptfds, const void* timeout) {
    if (nfds < 0) return fail(NET_EINVAL);
    if (nfds > 1024) nfds = 1024;
    auto* read_bits = static_cast<std::uint64_t*>(readfds);
    auto* write_bits = static_cast<std::uint64_t*>(writefds);
    auto has = [](const std::uint64_t* bits, int fd) { return bits && ((bits[fd / 64] >> (fd % 64)) & 1); };

    int timeout_us = -1;
    if (timeout) {
        const auto* tv = static_cast<const std::int64_t*>(timeout);
        const std::int64_t total = tv[0] * 1000000 + tv[1];
        timeout_us = total <= 0 ? 0 : total > 0x7FFFFFFF ? 0x7FFFFFFF : static_cast<int>(total);
    }

    const int epoll = sceNetEpollCreate("select", 0);
    if (epoll < 0) return -1;
    int watched = 0;
    for (int fd = 0; fd < nfds; ++fd) {
        NetEpollEvent event{};
        event.events = (has(read_bits, fd) ? 1u : 0u) | (has(write_bits, fd) ? 4u : 0u);
        if (event.events == 0) continue;
        event.ident = static_cast<std::uint64_t>(fd);
        if (sceNetEpollControl(epoll, 1, fd, &event) == 0) ++watched;
    }
    std::vector<NetEpollEvent> ready(64);
    int got = 0;
    if (watched > 0) {
        got = sceNetEpollWait(epoll, ready.data(), static_cast<int>(ready.size()), timeout_us);
    } else if (timeout_us != 0) {
        std::this_thread::sleep_for(std::chrono::microseconds(timeout_us < 0 ? 100000 : timeout_us));
    }
    sceNetEpollDestroy(epoll);
    if (got < 0) got = 0;
    {
        static std::atomic<int> lines{0};
        if (lines.fetch_add(1) < 100)
            std::fprintf(stderr, "[select] nfds %d watched %d timeout %d us -> %d ready\n", nfds, watched, timeout_us, got);
    }

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
        if ((ready[i].events & (4u | 8u | 16u)) && has(write_bits, fd)) {
            new_write[fd / 64] |= 1ull << (fd % 64);
            ++count;
        }
    }
    if (read_bits) std::memcpy(read_bits, new_read.data(), words * sizeof(std::uint64_t));
    if (write_bits) std::memcpy(write_bits, new_write.data(), words * sizeof(std::uint64_t));
    if (exceptfds) std::memset(exceptfds, 0, words * sizeof(std::uint64_t));
    return count;
}

// Byte order and text conversion.
uint32_t APS5_VABI sceNetHtonl_nid_postfix(uint32_t host32) { return swap32(host32); }
uint16_t APS5_VABI sceNetHtons_nid_postfix(uint16_t host16) { return swap16(host16); }
uint32_t APS5_VABI sceNetNtohl_nid_postfix(uint32_t net32) { return swap32(net32); }
uint16_t APS5_VABI sceNetNtohs_nid_postfix(uint16_t net16) { return swap16(net16); }

int APS5_VABI sceNetInetPton(int af, const char* src, void* dst) {
    if (src == nullptr || dst == nullptr) {
        return fail(NET_EINVAL);
    }
    if (af != NET_AF_INET && af != NET_AF_INET6) {
        return fail(NET_EAFNOSUPPORT);
    }
#ifdef _WIN32
    return InetPtonA(af == NET_AF_INET ? AF_INET : AF_INET6, src, dst);
#else
    return ::inet_pton(af == NET_AF_INET ? AF_INET : AF_INET6, src, dst);
#endif
}

const char* APS5_VABI sceNetInetNtop(int af, const void* src, char* dst, uint32_t size) {
    if (src == nullptr || dst == nullptr || size == 0) {
        *errno_slot() = NET_EINVAL;
        return nullptr;
    }
    if (af != NET_AF_INET && af != NET_AF_INET6) {
        *errno_slot() = NET_EAFNOSUPPORT;
        return nullptr;
    }
#ifdef _WIN32
    return InetNtopA(af == NET_AF_INET ? AF_INET : AF_INET6, const_cast<void*>(src), dst, size);
#else
    return ::inet_ntop(af == NET_AF_INET ? AF_INET : AF_INET6, src, dst, size);
#endif
}

int APS5_VABI sceNetEtherNtostr(const NetEtherAddr* n, char* str, size_t len) {
    if (n == nullptr || str == nullptr || len < 18) {
        return fail(NET_EINVAL);
    }
    std::snprintf(str, len, "%02x:%02x:%02x:%02x:%02x:%02x", n->data[0], n->data[1], n->data[2], n->data[3], n->data[4], n->data[5]);
    return 0;
}

int APS5_VABI sceNetGetMacAddress(NetEtherAddr* addr, int flags) {
    (void)flags;
    if (addr == nullptr) {
        return fail(NET_EINVAL);
    }
    // Keep guest identity stable without exposing the host adapter address.
    const std::uint8_t mac[6] = {0x02, 0x50, 0x53, 0x35, 0x00, 0x01};
    std::memcpy(addr->data, mac, 6);
    return 0;
}

// Resolver results use the guest IPv4 address format while delegating lookup to host DNS.
int APS5_VABI sceNetResolverCreate(const char* name, int memid, int flags) {
    (void)name;
    (void)memid;
    (void)flags;
    std::lock_guard<std::mutex> lk(g_mutex);
    const int id = g_next_resolver++;
    g_resolvers.insert(id);
    return id;
}

int APS5_VABI sceNetResolverDestroy(int rid) {
    std::lock_guard<std::mutex> lk(g_mutex);
    return g_resolvers.erase(rid) != 0 ? 0 : fail(NET_EBADF);
}

int APS5_VABI sceNetResolverStartNtoa(int rid, const char* hostname, void* addr, int timeout, int retry, int flags) {
    install_game_log();
    (void)timeout;
    (void)retry;
    (void)flags;
    if (!hostname || !addr) return fail(NET_EINVAL);
    {
        std::lock_guard<std::mutex> lk(g_mutex);
        if (g_resolvers.count(rid) == 0) return fail(NET_EBADF);
    }
    if (!initialize_sockets()) return fail(5);
    addrinfo hints{};
    hints.ai_family = AF_INET;
    addrinfo* results = nullptr;
    // With online play on, Polyphony's servers are RyGTServer: GT7's online service (Vegas) looks
    // its hosts up itself before connecting, sometimes still as templates such as
    // "asset.{stage}.{base_domain}", which no DNS server knows.
    const std::string name = hostname;
    const bool polyphony = name.find('{') != std::string::npos || name.find("gran-turismo.com") != std::string::npos ||
        name.find("granturismo-online.net") != std::string::npos;
    const char* lookup = polyphony && RyGTOnline::Enabled() ? RyGTOnline::Get().serverHost.c_str() : hostname;
    if (lookup != hostname) log_soft(__func__, ("sending " + name + " to RyGTServer").c_str());
    const int result = ::getaddrinfo(lookup, nullptr, &hints, &results);
    if (result != 0) {
        *errno_slot() = result == EAI_AGAIN ? NET_ETIMEDOUT : NET_ENOENT;
        log_soft(__func__, ("host DNS lookup failed for " + std::string(hostname)).c_str());
        return NET_ERROR_RESOLVER_ENODNS;
    }
    const auto* address = reinterpret_cast<const sockaddr_in*>(results->ai_addr);
    std::memcpy(addr, &address->sin_addr, sizeof(address->sin_addr));
    if (lookup != hostname) {
        std::lock_guard<std::mutex> lk(g_polyphony_mutex);
        g_polyphony_addresses.insert(address->sin_addr.s_addr);
    }
    ::freeaddrinfo(results);
    return 0;
}

int APS5_VABI sceNetResolverStartAton(int rid, const void* addr, char* hostname, int len, int timeout, int retry, int flags) {
    (void)timeout;
    (void)retry;
    (void)flags;
    if (!addr || !hostname || len <= 0) return fail(NET_EINVAL);
    {
        std::lock_guard<std::mutex> lk(g_mutex);
        if (g_resolvers.count(rid) == 0) return fail(NET_EBADF);
    }
    if (!initialize_sockets()) return fail(5);
    sockaddr_in address{};
    address.sin_family = AF_INET;
    std::memcpy(&address.sin_addr, addr, sizeof(address.sin_addr));
    const int result = ::getnameinfo(reinterpret_cast<const sockaddr*>(&address), sizeof(address), hostname,
        static_cast<NativeLength>(len), nullptr, 0, NI_NAMEREQD);
    if (result != 0) {
        *errno_slot() = result == EAI_AGAIN ? NET_ETIMEDOUT : NET_ENOENT;
        log_soft(__func__, "host reverse DNS lookup failed");
        return NET_ERROR_RESOLVER_ENODNS;
    }
    return 0;
}
}
