#include <cstdint>
#include <cstddef>
#include "SceTypes.hpp"
#include "prx/libc/include/General.hpp"
#include <windows.h>
#include <atomic>
#include <cstdio>
#include <map>
#include <mutex>

// No network is emulated: contexts, templates and requests can be created, but any request
// that would touch the network fails with the library's network error.
static constexpr int ERROR_NETWORK = static_cast<int>(0x80435001);
static std::atomic<int> g_nextHandle{1};

namespace {

constexpr int SSL_ERROR_NOT_FOUND = static_cast<int>(0x8095F004);
constexpr int SSL_ERROR_INVALID_ARG = static_cast<int>(0x8095177A);

struct SslData {
    char* ptr;
    size_t size;
};

struct SslCaCerts {
    SslData* certs;
    size_t num;
    void* pool;
};

}

extern "C" {

int APS5_VABI sceSslFreeCaCerts(int ssl_ctx_id, void* ca_certs) {
    (void)ssl_ctx_id;
    if (!ca_certs) return SSL_ERROR_INVALID_ARG;
    *static_cast<SslCaCerts*>(ca_certs) = {};
    return 0;
}

int APS5_VABI sceSslGetCaCerts(int ssl_ctx_id, void* ca_certs) {
    (void)ssl_ctx_id;
    if (!ca_certs) return SSL_ERROR_INVALID_ARG;
    *static_cast<SslCaCerts*>(ca_certs) = {};
    return SSL_ERROR_NOT_FOUND;
}

int APS5_VABI sceSslGetMemoryPoolStats() {
    return 0;
}

int APS5_VABI sceSslInit_nid_postfix(uint64_t pool_size) {
    (void)pool_size;
    return g_nextHandle.fetch_add(1, std::memory_order_relaxed);
}

int APS5_VABI sceSslTerm_nid_postfix(int ssl_ctx_id) {
    (void)ssl_ctx_id;
    return 0;
}


int APS5_VABI sceSslGetSerialNumber() {
 NotImplemented_nid_no_patch(__func__);
 return 0;
}

}

// Offline SSL compatibility exports; network requests are unavailable.
extern "C" {
int APS5_VABI sceSslCheckRecvPending() { return ERROR_NETWORK; }
int APS5_VABI sceSslCreateSslConnection() { return ERROR_NETWORK; }
int APS5_VABI sceSslDeleteSslConnection() { return ERROR_NETWORK; }
int APS5_VABI sceSslDisableOptionInternal() { return ERROR_NETWORK; }
int APS5_VABI sceSslDisableOptionInternalInsecure() { return ERROR_NETWORK; }
int APS5_VABI sceSslEnableOptionInternal() { return ERROR_NETWORK; }
int APS5_VABI sceSslFreeCaList() { return ERROR_NETWORK; }
int APS5_VABI sceSslFreeSslCertName() { return ERROR_NETWORK; }
int APS5_VABI sceSslGetCaList() { return ERROR_NETWORK; }
int APS5_VABI sceSslGetFingerprint() { return ERROR_NETWORK; }
int APS5_VABI sceSslGetIssuerName() { return ERROR_NETWORK; }
int APS5_VABI sceSslGetNameEntryCount() { return ERROR_NETWORK; }
int APS5_VABI sceSslGetNameEntryInfo() { return ERROR_NETWORK; }
int APS5_VABI sceSslGetNanoSSLModuleId() { return ERROR_NETWORK; }
int APS5_VABI sceSslGetNotAfter() { return ERROR_NETWORK; }
int APS5_VABI sceSslGetNotBefore() { return ERROR_NETWORK; }
int APS5_VABI sceSslGetPeerCert() { return ERROR_NETWORK; }
int APS5_VABI sceSslGetPem() { return ERROR_NETWORK; }
int APS5_VABI sceSslGetSslError() { return ERROR_NETWORK; }
int APS5_VABI sceSslGetSubjectName() { return ERROR_NETWORK; }
int APS5_VABI sceSslInit() { return ERROR_NETWORK; }
int APS5_VABI sceSslLoadCert() { return ERROR_NETWORK; }
int APS5_VABI sceSslLoadRootCACert() { return ERROR_NETWORK; }
int APS5_VABI sceSslRecv() { return ERROR_NETWORK; }
int APS5_VABI sceSslReuseConnection() { return ERROR_NETWORK; }
int APS5_VABI sceSslSend() { return ERROR_NETWORK; }
int APS5_VABI sceSslSetSslVersion() { return ERROR_NETWORK; }
int APS5_VABI sceSslSetVerifyCallback() { return ERROR_NETWORK; }
int APS5_VABI sceSslShowMemoryStat() { return ERROR_NETWORK; }
int APS5_VABI sceSslTerm() { return ERROR_NETWORK; }
int APS5_VABI sceSslUnloadCert() { return ERROR_NETWORK; }
}

// Connections made by games' own HTTP clients (GT7's): Polyphony's hosts are RyGTServer, which
// libSceNet's resolver and connect() send them to, and RyGTServer speaks plain HTTP. So a
// connection is the socket itself: no handshake, and reads and writes pass straight through.
namespace {

// libSceNet's exports carry their NIDs as names.
template<typename Function>
Function netFunction(const char* nid) {
    static const HMODULE net = GetModuleHandleA("libSceNet.prx");
    return reinterpret_cast<Function>(reinterpret_cast<void*>(GetProcAddress(net, nid)));
}

int64_t sceNetRecv(int s, void* buf, size_t len, int flags) {
    static const auto recv = netFunction<int64_t (APS5_VABI*)(int, void*, size_t, int)>("9wO9XrMsNhc");
    return recv(s, buf, len, flags);
}

int64_t sceNetSend(int s, const void* buf, size_t len, int flags) {
    static const auto send = netFunction<int64_t (APS5_VABI*)(int, const void*, size_t, int)>("beRjXBn-z+o");
    return send(s, buf, len, flags);
}

int* sceNetErrnoLoc() {
    static const auto errnoLoc = netFunction<int* (APS5_VABI*)()>("HQOwnfMGipQ");
    return errnoLoc();
}

constexpr int NET_MSG_PEEK = 0x2;

std::mutex g_connectionsMutex;
std::map<int, int> g_connections;  // connection ID -> socket
int g_nextConnection = 1;

// Logs the first calls, while games' use of these is being learned.
void trace(const char* what, int connection, long long result) {
    static std::atomic<int> lines{0};
    if (lines.fetch_add(1) < 200) std::fprintf(stderr, "[ssl] %s(conn %d) -> %lld (%#llx)\n", what, connection, result, result);
}

int socketFor(int connection) {
    std::lock_guard<std::mutex> lock(g_connectionsMutex);
    const auto it = g_connections.find(connection);
    return it == g_connections.end() ? -1 : it->second;
}

// Socket results as the library reports them: a byte count, or SCE_NET_ERROR_* for errno.
int netResult(int64_t result) {
    return result >= 0 ? static_cast<int>(result) : static_cast<int>(0x80410100u | static_cast<unsigned>(*sceNetErrnoLoc()));
}

}

extern "C" {

int APS5_VABI sceSslCreateConnection(int ssl_ctx_id, int socket, const char* server_name) {
    (void)ssl_ctx_id;
    (void)server_name;
    if (socket < 0) return SSL_ERROR_INVALID_ARG;
    std::lock_guard<std::mutex> lock(g_connectionsMutex);
    const int connection = g_nextConnection++;
    g_connections[connection] = socket;
    trace("CreateConnection", connection, socket);
    return connection;
}

int APS5_VABI sceSslDeleteConnection(int connection) {
    trace("DeleteConnection", connection, 0);
    std::lock_guard<std::mutex> lock(g_connectionsMutex);
    return g_connections.erase(connection) != 0 ? 0 : SSL_ERROR_NOT_FOUND;
}

int APS5_VABI sceSslConnect(int connection) {
    trace("Connect", connection, socketFor(connection));
    return socketFor(connection) < 0 ? SSL_ERROR_NOT_FOUND : 0;
}

int APS5_VABI sceSslClose(int connection) {
    return socketFor(connection) < 0 ? SSL_ERROR_NOT_FOUND : 0;
}

int APS5_VABI sceSslRead(int connection, void* buffer, int size, int flags) {
    const int socket = socketFor(connection);
    if (socket < 0) return SSL_ERROR_NOT_FOUND;
    const int result = netResult(sceNetRecv(socket, buffer, static_cast<size_t>(size), (flags & 2) ? NET_MSG_PEEK : 0));
    trace("Read", connection, result);
    return result;
}

int APS5_VABI sceSslWrite(int connection, const void* buffer, int size, int flags) {
    (void)flags;
    const int socket = socketFor(connection);
    if (socket < 0) return SSL_ERROR_NOT_FOUND;
    const int result = netResult(sceNetSend(socket, buffer, static_cast<size_t>(size), 0));
    trace("Write", connection, result);
    return result;
}

// No ALPN is negotiated, so the client speaks HTTP/1.1.
int APS5_VABI sceSslSetAlpn(int connection, const void* protocols, size_t size) {
    (void)protocols;
    (void)size;
    return socketFor(connection) < 0 ? SSL_ERROR_NOT_FOUND : 0;
}

const char* APS5_VABI sceSslGetAlpnSelected(int connection) {
    trace("GetAlpnSelected", connection, 0);
    return nullptr;
}

int APS5_VABI sceSslEnableOption(int connection, int options) { (void)connection; (void)options; return 0; }
int APS5_VABI sceSslDisableOption(int connection, int options) { (void)connection; (void)options; return 0; }
int APS5_VABI sceSslEnableVerifyOption(int connection, int options) { (void)connection; (void)options; return 0; }
int APS5_VABI sceSslDisableVerifyOption(int connection, int options) { (void)connection; (void)options; return 0; }
int APS5_VABI sceSslSetMinSslVersion(int connection, int version) { (void)connection; (void)version; return 0; }

}
