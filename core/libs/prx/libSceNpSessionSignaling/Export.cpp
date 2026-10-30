// PSN session signaling (libSceNpSessionSignaling): how players in a PSN session find each other
// before their games talk peer to peer. GT7 uses it for rooms.
//
// Here RyGTServer is the signaling server. A game that activates a session registers with it
// (GET /rygt/p2p/session?id=<session>&online=<id>&port=<P2P port>, every second while active) and
// gets back the other members: online ID, PSN account ID, address and P2P port. Each new member
// becomes a connection that is reported established at once; the games then talk over RyGT's
// P2P transport (libSceNet, UDP port 3658 by default) at those addresses. Members who stop
// asking are reported dead.
//
// The calls and events, as GT7 uses them (from its code):
//   CreateContext2(param, &ctx): param = {u32 user, u32, three (callback, arg) pairs at +8, +0x18,
//     +0x28}. The second, (ctx, id, event, data, error, arg), takes event 0 (a request is done)
//     and event 2 (a peer activated; data = {u64 account, u32 platform}). The third,
//     (ctx, group, connection, event, error, arg), takes event 1 (established) and 0 (dead).
//   ActivateSession(ctx, session ID, flags, param, &group), ActivateUser(ctx, param, &group)
//   GetConnectionFromPeerAddress2(ctx, group, &{u64 account, u32 platform}, &connection)
//   GetConnectionInfo(ctx, connection, code, &info): 1 RTT (u32 us), 4 peer address and 5 mapped
//     address ({u32 address, u16 port}, network order), 6 packet loss, 7 peer {u64 account,
//     u32 platform}
//   GetConnectionStatus(ctx, connection, &status (0 inactive, 1 pending, 2 active), &address, &port)
//   GetLocalNetInfo(ctx, &{u32 local address, u32 mapped address, u32 NAT type})
// Callbacks run on a guest thread of this library, as the real library's do.

#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <map>
#include <mutex>
#include <string>
#include <vector>
#include "SceTypes.hpp"

extern "C" int APS5_VABI scePthreadCreate(Pthread* thread, const PthreadAttr* attr, PthreadEntry entry, void* arg, const char* name);
extern "C" int APS5_VABI scePthreadDetach(Pthread thread);
#include "prx/libc/include/General.hpp"

namespace {

constexpr int SIGNALING_ERROR_INVALID_ARGUMENT = static_cast<int>(0x80552D02);
constexpr int SIGNALING_ERROR_NOT_FOUND = static_cast<int>(0x80552D0B);
constexpr int EVENT_REQUEST_DONE = 0, EVENT_PEER_ACTIVATED = 2;
constexpr int CONNECTION_DEAD = 0, CONNECTION_ESTABLISHED = 1;
constexpr int STATUS_ACTIVE = 2;
constexpr uint32_t PLATFORM_PS4 = 1;

using RequestCallback = void (APS5_VABI*)(uint32_t context, uint32_t id, int event, const void* data, int error, void* arg);
using ConnectionCallback = void (APS5_VABI*)(uint32_t context, uint32_t group, uint32_t connection, int event, int error, void* arg);

struct ContextParam {
    uint32_t user;
    uint32_t reserved;
    void* callback1; void* arg1;
    RequestCallback request_callback; void* request_arg;
    ConnectionCallback connection_callback; void* connection_arg;
};

struct PeerAddress {
    uint64_t account;
    uint32_t platform;
    uint32_t reserved;
};

struct Connection {
    uint32_t id = 0;
    uint32_t group = 0;
    std::string online_id;
    uint64_t account = 0;
    uint32_t address = 0;  // network order
    uint16_t port = 0;     // network order
    bool alive = true;
};

struct Group {
    uint32_t context = 0;
    std::string session;  // empty for a user group
    bool active = true;
};

struct Context {
    ContextParam param{};
};

std::mutex g_mutex;
std::map<uint32_t, Context> g_contexts;
std::map<uint32_t, Group> g_groups;
std::map<uint32_t, Connection> g_connections;
uint32_t g_next_context = 1, g_next_group = 1, g_next_connection = 1, g_next_request = 1;
bool g_worker_started = false;

void Log(const char* format, ...) {
    char text[512];
    va_list args;
    va_start(args, format);
    std::vsnprintf(text, sizeof(text), format, args);
    va_end(args);
    std::fprintf(stderr, "[online] session signaling: %s\n", text);
    std::fflush(stderr);
}

// RyGT.ini: online_server (RyGTServer), online_id, online_address (this PC's address for others).
std::string IniValue(const char* key) {
    std::string value;
    if (std::FILE* ini = std::fopen("RyGT.ini", "r")) {
        char line[256];
        const size_t length = std::strlen(key);
        while (std::fgets(line, sizeof(line), ini)) {
            if (std::strncmp(line, key, length) == 0 && line[length] == '=') {
                value = line + length + 1;
                value.erase(value.find_last_not_of(" \r\n") + 1);
            }
        }
        std::fclose(ini);
    }
    return value;
}

uint16_t P2PPort() {
    if (const char* text = std::getenv("RYGT_P2P_PORT")) {
        const int port = std::atoi(text);
        if (port > 0 && port < 65536) return static_cast<uint16_t>(port);
    }
    return 3658;
}

std::string UrlEncode(const std::string& text) {
    static const char* hex = "0123456789ABCDEF";
    std::string out;
    for (unsigned char c : text) {
        if (std::isalnum(c) || c == '-' || c == '_' || c == '.') {
            out += static_cast<char>(c);
        } else {
            out += '%';
            out += hex[c >> 4];
            out += hex[c & 15];
        }
    }
    return out;
}

// A plain HTTP GET to RyGTServer; the body, or false.
bool HttpGet(const std::string& path, std::string* body) {
    static const std::string server = IniValue("online_server");
    static std::once_flag wsa;
    std::call_once(wsa, [] { WSADATA data; WSAStartup(MAKEWORD(2, 2), &data); });
    const auto colon = server.rfind(':');
    if (server.empty() || colon == std::string::npos) return false;
    addrinfo hints{}, *found = nullptr;
    hints.ai_family = AF_INET;
    hints.ai_socktype = SOCK_STREAM;
    if (getaddrinfo(server.substr(0, colon).c_str(), server.substr(colon + 1).c_str(), &hints, &found) != 0) return false;
    SOCKET s = socket(found->ai_family, found->ai_socktype, found->ai_protocol);
    DWORD timeout = 3000;
    setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&timeout), sizeof(timeout));
    setsockopt(s, SOL_SOCKET, SO_SNDTIMEO, reinterpret_cast<const char*>(&timeout), sizeof(timeout));
    const bool connected = s != INVALID_SOCKET && connect(s, found->ai_addr, static_cast<int>(found->ai_addrlen)) == 0;
    freeaddrinfo(found);
    if (!connected) {
        if (s != INVALID_SOCKET) closesocket(s);
        return false;
    }
    const std::string request = "GET " + path + " HTTP/1.1\r\nHost: " + server + "\r\nConnection: close\r\n\r\n";
    send(s, request.data(), static_cast<int>(request.size()), 0);
    std::string reply;
    char buffer[4096];
    for (int n; (n = recv(s, buffer, sizeof(buffer), 0)) > 0;) reply.append(buffer, n);
    closesocket(s);
    const auto split = reply.find("\r\n\r\n");
    if (reply.compare(0, 12, "HTTP/1.1 200") != 0 || split == std::string::npos) return false;
    if (body) *body = reply.substr(split + 4);
    return true;
}

void FireConnection(const ContextParam& param, uint32_t context, const Connection& connection, int event) {
    if (param.connection_callback)
        param.connection_callback(context, connection.group, connection.id, event, 0, param.connection_arg);
}

// One poll of RyGTServer for a session group: new members become established connections,
// members gone become dead ones. Called with g_mutex held; returns the callbacks to make.
struct Pending {
    ContextParam param;
    uint32_t context;
    Connection connection;
    bool peer_activated;
    int event;
};

void PollGroup(uint32_t group_id, Group& group, const std::string& body, std::vector<Pending>* pending) {
    auto context = g_contexts.find(group.context);
    if (context == g_contexts.end()) return;
    std::map<std::string, bool> present;
    size_t start = 0;
    while (start < body.size()) {
        size_t end = body.find('\n', start);
        if (end == std::string::npos) end = body.size();
        char online[64] = {}, address[64] = {};
        unsigned long long account = 0;
        unsigned port = 0;
        if (std::sscanf(body.substr(start, end - start).c_str(), "%63s %llu %63s %u", online, &account, address, &port) == 4) {
            present[online] = true;
            bool known = false;
            for (auto& [id, connection] : g_connections) {
                if (connection.group == group_id && connection.online_id == online && connection.alive) known = true;
            }
            if (!known) {
                Connection connection;
                connection.id = g_next_connection++;
                connection.group = group_id;
                connection.online_id = online;
                connection.account = account;
                inet_pton(AF_INET, address, &connection.address);
                connection.port = htons(static_cast<uint16_t>(port));
                g_connections[connection.id] = connection;
                Log("%s (account %llu) at %s:%u joined session %s: connection %u", online, account, address, port,
                    group.session.c_str(), connection.id);
                pending->push_back({context->second.param, group.context, connection, true, CONNECTION_ESTABLISHED});
            }
        }
        start = end + 1;
    }
    for (auto& [id, connection] : g_connections) {
        if (connection.group == group_id && connection.alive && !present.count(connection.online_id)) {
            connection.alive = false;
            Log("%s left session %s: connection %u dead", connection.online_id.c_str(), group.session.c_str(), id);
            pending->push_back({context->second.param, group.context, connection, false, CONNECTION_DEAD});
        }
    }
}

// The guest thread that keeps every active session group up to date.
void* APS5_VABI Worker(void*) {
    static const std::string online_id = IniValue("online_id");
    for (;;) {
        std::vector<std::pair<uint32_t, std::string>> sessions;
        {
            std::lock_guard lock(g_mutex);
            for (auto& [id, group] : g_groups)
                if (group.active && !group.session.empty()) sessions.emplace_back(id, group.session);
        }
        for (const auto& [group_id, session] : sessions) {
            std::string body;
            const bool ok = HttpGet("/rygt/p2p/session?id=" + UrlEncode(session) + "&online=" + UrlEncode(online_id) +
                                        "&port=" + std::to_string(P2PPort()), &body);
            std::vector<Pending> pending;
            {
                std::lock_guard lock(g_mutex);
                auto group = g_groups.find(group_id);
                if (ok && group != g_groups.end() && group->second.active) PollGroup(group_id, group->second, body, &pending);
            }
            for (const Pending& p : pending) {
                if (p.peer_activated && p.param.request_callback) {
                    const PeerAddress peer{p.connection.account, PLATFORM_PS4, 0};
                    p.param.request_callback(p.context, p.connection.group, EVENT_PEER_ACTIVATED, &peer, 0, p.param.request_arg);
                }
                FireConnection(p.param, p.context, p.connection, p.event);
            }
        }
        Sleep(1000);
    }
    return nullptr;
}

void StartWorker() {
    if (g_worker_started) return;
    g_worker_started = true;
    Pthread thread{};
    if (scePthreadCreate(&thread, nullptr, Worker, nullptr, "NpSessionSignaling") == 0) {
        scePthreadDetach(thread);
    } else {
        Log("could not start its thread");
    }
}

// Reports a finished request (prepare, activation) on a guest thread, as the real library does.
struct RequestDone {
    ContextParam param;
    uint32_t context;
    uint32_t id;
};

void* APS5_VABI RequestDoneRunner(void* arg) {
    auto* done = static_cast<RequestDone*>(arg);
    Sleep(10);
    if (done->param.request_callback)
        done->param.request_callback(done->context, done->id, EVENT_REQUEST_DONE, nullptr, 0, done->param.request_arg);
    delete done;
    return nullptr;
}

void ReportRequestDone(uint32_t context, uint32_t id) {
    RequestDone* done;
    {
        std::lock_guard lock(g_mutex);
        auto found = g_contexts.find(context);
        if (found == g_contexts.end()) return;
        done = new RequestDone{found->second.param, context, id};
    }
    Pthread thread{};
    if (scePthreadCreate(&thread, nullptr, RequestDoneRunner, done, "NpSessionSignalingRequest") == 0) {
        scePthreadDetach(thread);
    } else {
        delete done;
    }
}

// This PC's address on the route to RyGTServer (network order), or 0.
uint32_t RouteAddress() {
    const std::string server = IniValue("online_server");
    const auto colon = server.rfind(':');
    if (colon == std::string::npos) return 0;
    addrinfo hints{}, *found = nullptr;
    hints.ai_family = AF_INET;
    hints.ai_socktype = SOCK_DGRAM;
    if (getaddrinfo(server.substr(0, colon).c_str(), server.substr(colon + 1).c_str(), &hints, &found) != 0) return 0;
    uint32_t address = 0;
    SOCKET s = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (s != INVALID_SOCKET) {
        sockaddr_in local{};
        int length = sizeof(local);
        // A UDP connect sends nothing; it only picks the route.
        if (connect(s, found->ai_addr, static_cast<int>(found->ai_addrlen)) == 0 &&
            getsockname(s, reinterpret_cast<sockaddr*>(&local), &length) == 0)
            address = local.sin_addr.s_addr;
        closesocket(s);
    }
    freeaddrinfo(found);
    return address;
}

// This PC's local and mapped (public) addresses, network order: online_address when RyGT.ini has
// it (a VPN address serves as both), else the address on the route to RyGTServer and the one
// RyGTServer sees this PC at. GT7 calls an address of 0 an unknown NAT and warns about it.
void OwnAddresses(uint32_t* local, uint32_t* mapped) {
    static uint32_t cached_local = 0, cached_mapped = 0;
    static std::once_flag once;
    std::call_once(once, [] {
        inet_pton(AF_INET, IniValue("online_address").c_str(), &cached_local);
        if (cached_local != 0) {
            cached_mapped = cached_local;
            return;
        }
        cached_local = RouteAddress();
        std::string seen;
        if (HttpGet("/rygt/p2p/whoami", &seen)) {
            seen.erase(seen.find_last_not_of(" \r\n") + 1);
            inet_pton(AF_INET, seen.c_str(), &cached_mapped);
        }
        if (cached_mapped == 0) cached_mapped = cached_local;
        char a[INET_ADDRSTRLEN] = {}, b[INET_ADDRSTRLEN] = {};
        inet_ntop(AF_INET, &cached_local, a, sizeof(a));
        inet_ntop(AF_INET, &cached_mapped, b, sizeof(b));
        Log("this PC is %s locally, %s as RyGTServer sees it", a, b);
    });
    *local = cached_local;
    *mapped = cached_mapped;
}

}  // namespace

extern "C" {

int APS5_VABI sceNpSessionSignalingInitialize(void* param) {
    (void)param;
    Log("initialized; RyGTServer at %s, this player '%s'", IniValue("online_server").c_str(), IniValue("online_id").c_str());
    return 0;
}

int APS5_VABI sceNpSessionSignalingTerminate(void) {
    return 0;
}

int APS5_VABI sceNpSessionSignalingCreateContext2(const void* param, uint32_t* context_id) {
    if (!param || !context_id) return SIGNALING_ERROR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mutex);
    const uint32_t id = g_next_context++;
    std::memcpy(&g_contexts[id].param, param, sizeof(ContextParam));
    *context_id = id;
    Log("context %u made", id);
    return 0;
}

int APS5_VABI sceNpSessionSignalingCreateContext(const void* param, uint32_t* context_id) {
    return sceNpSessionSignalingCreateContext2(param, context_id);
}

int APS5_VABI sceNpSessionSignalingDestroyContext(uint32_t context_id) {
    std::lock_guard lock(g_mutex);
    g_contexts.erase(context_id);
    for (auto& [id, group] : g_groups)
        if (group.context == context_id) group.active = false;
    return 0;
}

// Prepare: nothing to set up; done at once.
int APS5_VABI sceNpSessionSignalingRequestPrepare(uint32_t context_id, uint32_t* request_id) {
    uint32_t id;
    {
        std::lock_guard lock(g_mutex);
        id = g_next_request++;
    }
    if (request_id) *request_id = id;
    ReportRequestDone(context_id, id);
    return 0;
}

int APS5_VABI sceNpSessionSignalingActivateUser(uint32_t context_id, const void* param, uint32_t* group_id) {
    (void)param;
    uint32_t id;
    {
        std::lock_guard lock(g_mutex);
        id = g_next_group++;
        g_groups[id] = Group{context_id, "", true};
    }
    if (group_id) *group_id = id;
    Log("user group %u activated", id);
    return static_cast<int>(id);
}

int APS5_VABI sceNpSessionSignalingActivateSession(uint32_t context_id, const char* session_id, int flags,
                                                   const void* param, uint32_t* group_id) {
    (void)flags; (void)param;
    if (!session_id) return SIGNALING_ERROR_INVALID_ARGUMENT;
    uint32_t id;
    {
        std::lock_guard lock(g_mutex);
        id = g_next_group++;
        g_groups[id] = Group{context_id, session_id, true};
        StartWorker();
    }
    if (group_id) *group_id = id;
    Log("session %s activated: group %u", session_id, id);
    return static_cast<int>(id);
}

int APS5_VABI sceNpSessionSignalingDeactivate(uint32_t context_id, uint32_t group_id) {
    (void)context_id;
    std::string session;
    {
        std::lock_guard lock(g_mutex);
        auto group = g_groups.find(group_id);
        if (group == g_groups.end()) return 0;
        group->second.active = false;
        session = group->second.session;
        for (auto& [id, connection] : g_connections)
            if (connection.group == group_id) connection.alive = false;
    }
    if (!session.empty()) {
        static const std::string online_id = IniValue("online_id");
        HttpGet("/rygt/p2p/session?id=" + UrlEncode(session) + "&online=" + UrlEncode(online_id) + "&port=" +
                    std::to_string(P2PPort()) + "&leave=1", nullptr);
        Log("session %s deactivated (group %u)", session.c_str(), group_id);
    }
    return 0;
}

int APS5_VABI sceNpSessionSignalingGetConnectionFromPeerAddress2(uint32_t context_id, uint32_t group_id,
                                                                 const PeerAddress* peer, uint32_t* connection_id) {
    (void)context_id;
    if (!peer || !connection_id) return SIGNALING_ERROR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mutex);
    for (const auto& [id, connection] : g_connections) {
        if (connection.alive && connection.account == peer->account && (group_id == 0 || connection.group == group_id)) {
            *connection_id = id;
            return 0;
        }
    }
    return SIGNALING_ERROR_NOT_FOUND;
}

int APS5_VABI sceNpSessionSignalingGetConnectionInfo(uint32_t context_id, uint32_t connection_id, int code, void* info) {
    (void)context_id;
    if (!info) return SIGNALING_ERROR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mutex);
    auto found = g_connections.find(connection_id);
    if (found == g_connections.end()) return SIGNALING_ERROR_NOT_FOUND;
    const Connection& connection = found->second;
    auto* bytes = static_cast<uint8_t*>(info);
    switch (code) {
    case 1: {  // RTT, microseconds
        const uint32_t rtt = 30000;
        std::memcpy(bytes, &rtt, 4);
        return 0;
    }
    case 4:    // the peer's address
    case 5:    // its mapped address (the same: RyGTServer saw it there)
        std::memcpy(bytes, &connection.address, 4);
        std::memcpy(bytes + 4, &connection.port, 2);
        return 0;
    case 6: {  // packet loss
        const uint32_t loss = 0;
        std::memcpy(bytes, &loss, 4);
        return 0;
    }
    case 7: {  // the peer: account and platform
        const PeerAddress peer{connection.account, PLATFORM_PS4, 0};
        std::memcpy(bytes, &peer, 12);
        return 0;
    }
    default:
        return SIGNALING_ERROR_INVALID_ARGUMENT;
    }
}

int APS5_VABI sceNpSessionSignalingGetConnectionStatus(uint32_t context_id, uint32_t connection_id, int* status,
                                                       uint32_t* address, uint16_t* port) {
    (void)context_id;
    std::lock_guard lock(g_mutex);
    auto found = g_connections.find(connection_id);
    if (found == g_connections.end()) return SIGNALING_ERROR_NOT_FOUND;
    if (status) *status = found->second.alive ? STATUS_ACTIVE : 0;
    if (address) *address = found->second.address;
    if (port) *port = found->second.port;
    return 0;
}

int APS5_VABI sceNpSessionSignalingGetLocalNetInfo(uint32_t context_id, uint32_t* info) {
    (void)context_id;
    if (!info) return SIGNALING_ERROR_INVALID_ARGUMENT;
    OwnAddresses(&info[0], &info[1]);  // local address, mapped address
    info[2] = 1;             // NAT type 1: directly reachable (over the VPN)
    return 0;
}

}
