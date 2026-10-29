#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <winsock2.h>
#include <ws2tcpip.h>
#include <iphlpapi.h>
#else
#include <arpa/inet.h>
#include <ifaddrs.h>
#include <net/if.h>
#include <netinet/in.h>
#include <unistd.h>
#endif
#include <algorithm>
#include <cstdint>
#include <cstddef>
#include <cstdio>
#include <cstring>
#include "SceTypes.hpp"
#include "OnlineConfig.hpp"
#include "prx/libc/include/General.hpp"
#include <cstdlib>
#include <mutex>
#include <string>
#ifdef _WIN32
#include <vector>
#endif

// Network-control state reflects host interface addressing, not PSN sign-in status.
static constexpr int SCE_NET_CTL_ERROR_CALLBACK_MAX = static_cast<int>(0x80412103);
static constexpr int SCE_NET_CTL_ERROR_INVALID_ID = static_cast<int>(0x80412105);
static constexpr int SCE_NET_CTL_ERROR_INVALID_CODE = static_cast<int>(0x80412106);
static constexpr int SCE_NET_CTL_ERROR_INVALID_ADDR = static_cast<int>(0x80412107);
static constexpr int SCE_NET_CTL_ERROR_NOT_CONNECTED = static_cast<int>(0x80412108);
static constexpr int NET_CTL_STATE_DISCONNECTED = 0;
static constexpr int NET_CTL_STATE_IPOBTAINED = 3;
static constexpr int NET_CTL_EVENT_TYPE_IPOBTAINED = 3;
static constexpr int MAX_CALLBACKS = 8;

// The game's callbacks are guest code.
using GuestNetCtlCallback = void (APS5_VABI*)(int event_type, void* arg);

namespace {

struct Callback {
    GuestNetCtlCallback func = nullptr;
    void* arg = nullptr;
    bool notified = false;
};

std::mutex g_callbackLock;
Callback g_callbacks[MAX_CALLBACKS] = {};

// sceNetCtlGetInfo's union (256 bytes on the console).
union NetCtlInfoData {
    std::uint32_t device;
    std::uint8_t ether_addr[6];
    std::uint32_t mtu;
    std::uint32_t link;
    std::uint8_t bssid[6];
    char ssid[33];
    std::uint32_t wifi_security;
    std::uint8_t rssi_dbm;
    std::uint8_t rssi_percentage;
    std::uint8_t channel;
    std::uint32_t ip_config;
    char dhcp_hostname[256];
    char pppoe_auth_name[128];
    char ip_address[16];
    char netmask[16];
    char default_route[16];
    char primary_dns[16];
    char secondary_dns[16];
    std::uint32_t http_proxy_config;
    char http_proxy_server[256];
    std::uint16_t http_proxy_port;
};
static_assert(sizeof(NetCtlInfoData) == 256);

struct NetCtlNatInfoData {
    std::uint32_t size;
    std::int32_t stun_status;
    std::int32_t nat_type;
    std::uint32_t mapped_addr;
};

enum InfoCode {
    INFO_DEVICE = 1, INFO_ETHER_ADDR, INFO_MTU, INFO_LINK, INFO_BSSID, INFO_SSID, INFO_WIFI_SECURITY,
    INFO_RSSI_DBM, INFO_RSSI_PERCENTAGE, INFO_CHANNEL, INFO_IP_CONFIG, INFO_DHCP_HOSTNAME,
    INFO_PPPOE_AUTH_NAME, INFO_IP_ADDRESS, INFO_NETMASK, INFO_DEFAULT_ROUTE, INFO_PRIMARY_DNS,
    INFO_SECONDARY_DNS, INFO_HTTP_PROXY_CONFIG, INFO_HTTP_PROXY_SERVER, INFO_HTTP_PROXY_PORT,
};

// The host's main IPv4 interface, as the console reports its own.
struct HostInterface {
    bool found = false;
    std::uint8_t mac[6] = {};
    std::uint32_t mtu = 1500;
    std::string address, netmask, gateway, dns1, dns2, hostname;
    std::uint32_t addressValue = 0;
};

std::string ipv4Text(std::uint32_t networkOrder) {
    char text[16] = {};
    const auto* b = reinterpret_cast<const std::uint8_t*>(&networkOrder);
    std::snprintf(text, sizeof(text), "%u.%u.%u.%u", b[0], b[1], b[2], b[3]);
    return text;
}

#ifdef _WIN32
bool initializeSockets() {
    static const bool ready = [] {
        WSADATA data{};
        return WSAStartup(MAKEWORD(2, 2), &data) == 0;
    }();
    return ready;
}
#endif

bool HostHasAddress(int family) {
#ifdef _WIN32
    if (!initializeSockets()) return false;
    ULONG size = 0;
    const ULONG flags = GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_DNS_SERVER;
    if (GetAdaptersAddresses(family, flags, nullptr, nullptr, &size) != ERROR_BUFFER_OVERFLOW) return false;
    std::vector<std::uint8_t> storage(size);
    auto* adapters = reinterpret_cast<IP_ADAPTER_ADDRESSES*>(storage.data());
    if (GetAdaptersAddresses(family, flags, nullptr, adapters, &size) != NO_ERROR) return false;
    for (auto* adapter = adapters; adapter; adapter = adapter->Next) {
        if (adapter->OperStatus != IfOperStatusUp || adapter->IfType == IF_TYPE_SOFTWARE_LOOPBACK) continue;
        for (auto* address = adapter->FirstUnicastAddress; address; address = address->Next) {
            if (!address->Address.lpSockaddr) continue;
            if (address->Address.lpSockaddr->sa_family == AF_INET) {
                const auto* v4 = reinterpret_cast<const sockaddr_in*>(address->Address.lpSockaddr);
                const auto host = ntohl(v4->sin_addr.s_addr);
                if (host != 0 && (host >> 24) != 127) return true;
            } else if (address->Address.lpSockaddr->sa_family == AF_INET6) {
                const auto* v6 = reinterpret_cast<const sockaddr_in6*>(address->Address.lpSockaddr);
                if (!IN6_IS_ADDR_UNSPECIFIED(&v6->sin6_addr) && !IN6_IS_ADDR_LOOPBACK(&v6->sin6_addr)) return true;
            }
        }
    }
    return false;
#else
    ifaddrs* interfaces = nullptr;
    if (getifaddrs(&interfaces) != 0) return false;
    bool found = false;
    for (auto* item = interfaces; item && !found; item = item->ifa_next) {
        if (!item->ifa_addr || !(item->ifa_flags & IFF_UP) || (item->ifa_flags & IFF_LOOPBACK)) continue;
        const int address_family = item->ifa_addr->sa_family;
        if ((family == AF_UNSPEC || family == address_family) && address_family == AF_INET) {
            const auto* v4 = reinterpret_cast<const sockaddr_in*>(item->ifa_addr);
            const auto host = ntohl(v4->sin_addr.s_addr);
            found = host != 0 && (host >> 24) != 127;
        } else if ((family == AF_UNSPEC || family == address_family) && address_family == AF_INET6) {
            const auto* v6 = reinterpret_cast<const sockaddr_in6*>(item->ifa_addr);
            found = !IN6_IS_ADDR_UNSPECIFIED(&v6->sin6_addr) && !IN6_IS_ADDR_LOOPBACK(&v6->sin6_addr);
        }
    }
    freeifaddrs(interfaces);
    return found;
#endif
}

// The interface with a default gateway first (the one that reaches the internet), else the first
// IPv4 interface that is up.
HostInterface FindHostInterface() {
    HostInterface best;
#ifdef _WIN32
    if (!initializeSockets()) return best;
    ULONG size = 0;
    const ULONG flags = GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_INCLUDE_GATEWAYS;
    if (GetAdaptersAddresses(AF_INET, flags, nullptr, nullptr, &size) != ERROR_BUFFER_OVERFLOW) return best;
    std::vector<std::uint8_t> storage(size);
    auto* adapters = reinterpret_cast<IP_ADAPTER_ADDRESSES*>(storage.data());
    if (GetAdaptersAddresses(AF_INET, flags, nullptr, adapters, &size) != NO_ERROR) return best;
    // Which interface the game reports as its own decides the address other players are given
    // (lobby P2P puts it in the room's game ticket). In order: the one holding online_address; the
    // one Windows routes RyGTServer traffic through, when the server is another PC (a friend's
    // Radmin or Tailscale adapter); the one internet traffic goes through.
    DWORD routed = 0;
    const auto& online = RyGTOnline::Get();
    in_addr wanted{};
    if (!online.address.empty() && InetPtonA(AF_INET, online.address.c_str(), &wanted) == 1) {
        for (auto* adapter = adapters; adapter && routed == 0; adapter = adapter->Next) {
            for (auto* unicast = adapter->FirstUnicastAddress; unicast; unicast = unicast->Next) {
                if (unicast->Address.lpSockaddr && unicast->Address.lpSockaddr->sa_family == AF_INET &&
                    reinterpret_cast<const sockaddr_in*>(unicast->Address.lpSockaddr)->sin_addr.s_addr == wanted.s_addr) {
                    routed = adapter->IfIndex;
                    break;
                }
            }
        }
    }
    in_addr server{};
    if (routed == 0 && online.enabled && InetPtonA(AF_INET, online.serverHost.c_str(), &server) == 1 &&
        (ntohl(server.s_addr) >> 24) != 127) {
        if (GetBestInterface(server.s_addr, &routed) != NO_ERROR) routed = 0;
    }
    if (routed == 0 && GetBestInterface(htonl(0x01010101), &routed) != NO_ERROR) routed = 0;
    bool routedFound = false;
    for (auto* adapter = adapters; adapter; adapter = adapter->Next) {
        if (adapter->OperStatus != IfOperStatusUp || adapter->IfType == IF_TYPE_SOFTWARE_LOOPBACK) continue;
        const bool isRouted = routed != 0 && adapter->IfIndex == routed;
        if (routedFound && !isRouted) continue;
        HostInterface candidate;
        for (auto* unicast = adapter->FirstUnicastAddress; unicast; unicast = unicast->Next) {
            if (!unicast->Address.lpSockaddr || unicast->Address.lpSockaddr->sa_family != AF_INET) continue;
            const auto* v4 = reinterpret_cast<const sockaddr_in*>(unicast->Address.lpSockaddr);
            const auto host = ntohl(v4->sin_addr.s_addr);
            if (host == 0 || (host >> 24) == 127) continue;
            candidate.found = true;
            candidate.addressValue = v4->sin_addr.s_addr;
            candidate.address = ipv4Text(v4->sin_addr.s_addr);
            const unsigned prefix = unicast->OnLinkPrefixLength <= 32 ? unicast->OnLinkPrefixLength : 24;
            candidate.netmask = ipv4Text(htonl(prefix == 0 ? 0 : 0xFFFFFFFFu << (32 - prefix)));
            break;
        }
        if (!candidate.found) continue;
        std::memcpy(candidate.mac, adapter->PhysicalAddress, std::min<ULONG>(adapter->PhysicalAddressLength, 6));
        if (adapter->Mtu > 0 && adapter->Mtu < 65536) candidate.mtu = adapter->Mtu;
        for (auto* gateway = adapter->FirstGatewayAddress; gateway; gateway = gateway->Next) {
            if (gateway->Address.lpSockaddr && gateway->Address.lpSockaddr->sa_family == AF_INET) {
                candidate.gateway = ipv4Text(reinterpret_cast<const sockaddr_in*>(gateway->Address.lpSockaddr)->sin_addr.s_addr);
                break;
            }
        }
        for (auto* dns = adapter->FirstDnsServerAddress; dns; dns = dns->Next) {
            if (!dns->Address.lpSockaddr || dns->Address.lpSockaddr->sa_family != AF_INET) continue;
            auto text = ipv4Text(reinterpret_cast<const sockaddr_in*>(dns->Address.lpSockaddr)->sin_addr.s_addr);
            if (candidate.dns1.empty()) candidate.dns1 = text;
            else if (candidate.dns2.empty()) candidate.dns2 = text;
        }
        if (isRouted) {
            best = candidate;
            routedFound = true;
            break;
        }
        if (!best.found || (best.gateway.empty() && !candidate.gateway.empty())) best = candidate;
    }
    char hostname[256] = {};
    DWORD hostnameSize = sizeof(hostname);
    if (GetComputerNameA(hostname, &hostnameSize)) best.hostname = hostname;
#else
    ifaddrs* interfaces = nullptr;
    if (getifaddrs(&interfaces) != 0) return best;
    for (auto* item = interfaces; item; item = item->ifa_next) {
        if (!item->ifa_addr || item->ifa_addr->sa_family != AF_INET || !(item->ifa_flags & IFF_UP) ||
            (item->ifa_flags & IFF_LOOPBACK)) continue;
        const auto* v4 = reinterpret_cast<const sockaddr_in*>(item->ifa_addr);
        best.found = true;
        best.addressValue = v4->sin_addr.s_addr;
        best.address = ipv4Text(v4->sin_addr.s_addr);
        if (item->ifa_netmask) best.netmask = ipv4Text(reinterpret_cast<const sockaddr_in*>(item->ifa_netmask)->sin_addr.s_addr);
        break;
    }
    freeifaddrs(interfaces);
    char hostname[256] = {};
    if (gethostname(hostname, sizeof(hostname) - 1) == 0) best.hostname = hostname;
#endif
    if (best.netmask.empty()) best.netmask = "255.255.255.0";
    if (best.dns1.empty()) best.dns1 = best.gateway.empty() ? "1.1.1.1" : best.gateway;
    if (best.dns2.empty()) best.dns2 = best.dns1;
    if (best.gateway.empty()) best.gateway = best.address;
    return best;
}

const HostInterface& Interface() {
    static const HostInterface host = FindHostInterface();
    return host;
}

// Without RyGT online the console reports itself offline, as before: sceNetCtlGetInfo used to have
// no host equivalent, and a connected state without it made titles take online paths that fail
// halfway (GT Sport's boot script read a nil network value). RYTY_NETWORK_CONNECTED still forces it.
bool ReportConnected(int family) {
    static const bool enabled = std::getenv("RYTY_NETWORK_CONNECTED") != nullptr || RyGTOnline::Enabled();
    return enabled && HostHasAddress(family);
}

void copyText(char* out, std::size_t size, const std::string& text) {
    std::memset(out, 0, size);
    std::memcpy(out, text.data(), std::min(text.size(), size - 1));
}

}

extern "C" {

int APS5_VABI sceNetCtlGetIfStat() {
    return SCE_NET_CTL_ERROR_NOT_CONNECTED;
}

// Registered callbacks hear IPOBTAINED once, on the game's own thread, when the network is up.
int APS5_VABI sceNetCtlCheckCallback(void) {
    if (!ReportConnected(AF_UNSPEC)) return 0;
    Callback due[MAX_CALLBACKS] = {};
    {
        std::lock_guard lock(g_callbackLock);
        for (int index = 0; index < MAX_CALLBACKS; ++index) {
            if (g_callbacks[index].func && !g_callbacks[index].notified) {
                g_callbacks[index].notified = true;
                due[index] = g_callbacks[index];
            }
        }
    }
    for (const auto& callback : due) {
        if (callback.func) callback.func(NET_CTL_EVENT_TYPE_IPOBTAINED, callback.arg);
    }
    return 0;
}

int APS5_VABI sceNetCtlGetInfo(int code, NetCtlInfo* info) {
    if (!info) return SCE_NET_CTL_ERROR_INVALID_ADDR;
    if (!ReportConnected(AF_INET)) return SCE_NET_CTL_ERROR_NOT_CONNECTED;
    const auto& host = Interface();
    auto* data = reinterpret_cast<NetCtlInfoData*>(info);
    switch (code) {
    case INFO_DEVICE: data->device = 0; break; // wired
    case INFO_ETHER_ADDR: std::memcpy(data->ether_addr, host.mac, 6); break;
    case INFO_MTU: data->mtu = host.mtu; break;
    case INFO_LINK: data->link = 1; break;
    case INFO_BSSID: std::memset(data->bssid, 0, 6); break;
    case INFO_SSID: copyText(data->ssid, sizeof(data->ssid), ""); break;
    case INFO_WIFI_SECURITY: data->wifi_security = 0; break;
    case INFO_RSSI_DBM: data->rssi_dbm = 0; break;
    case INFO_RSSI_PERCENTAGE: data->rssi_percentage = 100; break;
    case INFO_CHANNEL: data->channel = 0; break;
    case INFO_IP_CONFIG: data->ip_config = 0; break; // DHCP
    case INFO_DHCP_HOSTNAME: copyText(data->dhcp_hostname, sizeof(data->dhcp_hostname), host.hostname); break;
    case INFO_PPPOE_AUTH_NAME: copyText(data->pppoe_auth_name, sizeof(data->pppoe_auth_name), ""); break;
    case INFO_IP_ADDRESS: copyText(data->ip_address, sizeof(data->ip_address), host.address); break;
    case INFO_NETMASK: copyText(data->netmask, sizeof(data->netmask), host.netmask); break;
    case INFO_DEFAULT_ROUTE: copyText(data->default_route, sizeof(data->default_route), host.gateway); break;
    case INFO_PRIMARY_DNS: copyText(data->primary_dns, sizeof(data->primary_dns), host.dns1); break;
    case INFO_SECONDARY_DNS: copyText(data->secondary_dns, sizeof(data->secondary_dns), host.dns2); break;
    case INFO_HTTP_PROXY_CONFIG: data->http_proxy_config = 0; break; // off
    case INFO_HTTP_PROXY_SERVER: copyText(data->http_proxy_server, sizeof(data->http_proxy_server), ""); break;
    case INFO_HTTP_PROXY_PORT: data->http_proxy_port = 0; break;
    default: return SCE_NET_CTL_ERROR_INVALID_CODE;
    }
    return 0;
}

// A NAT type 2 router (the common home setup) with the host's own address.
int APS5_VABI sceNetCtlGetNatInfo(NetCtlNatInfo* nat_info) {
    if (!nat_info) return SCE_NET_CTL_ERROR_INVALID_ADDR;
    if (!ReportConnected(AF_INET)) return SCE_NET_CTL_ERROR_NOT_CONNECTED;
    auto* data = reinterpret_cast<NetCtlNatInfoData*>(nat_info);
    data->stun_status = 1;
    data->nat_type = 2;
    data->mapped_addr = Interface().addressValue;
    return 0;
}

int APS5_VABI sceNetCtlGetResult(int event_type, int* error_code) {
    (void)event_type;
    if (!error_code) return SCE_NET_CTL_ERROR_INVALID_ADDR;
    *error_code = 0;
    return 0;
}

int APS5_VABI sceNetCtlGetState(int* state) {
    if (!state) return SCE_NET_CTL_ERROR_INVALID_ADDR;
    *state = ReportConnected(AF_UNSPEC) ? NET_CTL_STATE_IPOBTAINED : NET_CTL_STATE_DISCONNECTED;
    return 0;
}

int APS5_VABI sceNetCtlGetStateV6(int* state) {
    if (!state) return SCE_NET_CTL_ERROR_INVALID_ADDR;
    *state = ReportConnected(AF_INET6) ? NET_CTL_STATE_IPOBTAINED : NET_CTL_STATE_DISCONNECTED;
    return 0;
}

int APS5_VABI sceNetCtlInit(void) {
    return 0;
}

int APS5_VABI sceNetCtlRegisterCallback(NetCtlCallback func, void* arg, int* cid) {
    if (!func || !cid) return SCE_NET_CTL_ERROR_INVALID_ADDR;
    std::lock_guard lock(g_callbackLock);
    for (int index = 0; index < MAX_CALLBACKS; ++index) {
        if (!g_callbacks[index].func) {
            g_callbacks[index] = {reinterpret_cast<GuestNetCtlCallback>(func), arg, false};
            *cid = index;
            return 0;
        }
    }
    return SCE_NET_CTL_ERROR_CALLBACK_MAX;
}

void APS5_VABI sceNetCtlTerm(void) {
}

int APS5_VABI sceNetCtlUnregisterCallback(int cid) {
    if (cid < 0 || cid >= MAX_CALLBACKS) return SCE_NET_CTL_ERROR_INVALID_ID;
    std::lock_guard lock(g_callbackLock);
    g_callbacks[cid] = {};
    return 0;
}

}
