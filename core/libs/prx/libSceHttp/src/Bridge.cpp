#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <winsock2.h>
#include <ws2tcpip.h>
#else
#include <netdb.h>
#include <sys/socket.h>
#include <sys/time.h>
#include <unistd.h>
#endif
#include <algorithm>
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <cstring>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <vector>
#include "OnlineConfig.hpp"
#include "StdoutLog.hpp"
#include "prx/libSceHttp/src/Bridge.hpp"
#include "prx/libSceHttp/src/HttpErrors.hpp"

namespace HttpBridge {
namespace {

#ifdef _WIN32
using NativeSocket = SOCKET;
constexpr NativeSocket INVALID_NATIVE_SOCKET = INVALID_SOCKET;
void closeSocket(NativeSocket socket) { closesocket(socket); }
bool initializeSockets() {
    static const bool ready = [] {
        WSADATA data{};
        return WSAStartup(MAKEWORD(2, 2), &data) == 0;
    }();
    return ready;
}
void setTimeout(NativeSocket socket, int option, unsigned seconds) {
    const DWORD value = seconds * 1000;
    setsockopt(socket, SOL_SOCKET, option, reinterpret_cast<const char*>(&value), sizeof(value));
}
#else
using NativeSocket = int;
constexpr NativeSocket INVALID_NATIVE_SOCKET = -1;
void closeSocket(NativeSocket socket) { ::close(socket); }
bool initializeSockets() { return true; }
void setTimeout(NativeSocket socket, int option, unsigned seconds) {
    timeval value{static_cast<time_t>(seconds), 0};
    setsockopt(socket, SOL_SOCKET, option, &value, sizeof(value));
}
#endif

constexpr unsigned SOCKET_TIMEOUT_SECONDS = 15;
constexpr std::uint32_t HEADER_MODE_ADD = 1;
constexpr int CONTENTLEN_EXIST = 0;
constexpr std::uint32_t NB_EVENT_IN = 0x01;
constexpr std::uint32_t NB_EVENT_OUT = 0x04;
constexpr std::uint32_t NB_EVENT_SOCK_ERR = 0x08;

struct NbEvent {
    std::uint32_t events;
    std::uint32_t eventDetail;
    int id;
    void* userArg;
};

struct Header {
    std::string name;
    std::string value;
};

struct Object {
    Kind kind = Kind::Context;
    int parent = 0;
    std::vector<Header> headers;
    // Template
    std::string userAgent;
    // Connection
    std::string scheme = "http";
    std::string host;
    std::uint16_t port = 0;
    // Request
    std::string method = "GET";
    std::string target = "/";
    std::uint64_t contentLength = 0;
    void* epoll = nullptr;
    void* epollArg = nullptr;
    bool sent = false;
    bool failed = false;
    bool reported = false;
    int status = 0;
    std::string responseHeaders;
    std::string body;
    std::size_t readOffset = 0;
};

std::mutex g_lock;
std::condition_variable g_completed;
std::map<int, std::shared_ptr<Object>> g_objects;
int g_nextId = 1;
// host (lower case) -> cookie name -> value; guarded by g_lock.
std::map<std::string, std::map<std::string, std::string>> g_cookies;

std::string lowerCase(std::string_view text) {
    std::string out(text);
    for (char& c : out) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return out;
}

// Stores "name=value; attributes..." for the host; an empty value or a past expiry removes it.
void storeCookie(const std::string& host, std::string_view cookie) {
    const auto attributes = cookie.find(';');
    std::string_view pair = cookie.substr(0, attributes);
    const auto equals = pair.find('=');
    if (equals == pair.npos) return;
    const auto trim = [](std::string_view text) {
        const auto first = text.find_first_not_of(" \t");
        return first == text.npos ? std::string_view{} : text.substr(first, text.find_last_not_of(" \t") - first + 1);
    };
    const std::string name(trim(pair.substr(0, equals)));
    const std::string value(trim(pair.substr(equals + 1)));
    if (name.empty()) return;
    const std::string rest = attributes == cookie.npos ? std::string() : lowerCase(cookie.substr(attributes));
    if (value.empty() || rest.find("max-age=0") != rest.npos) g_cookies[host].erase(name);
    else g_cookies[host][name] = value;
}

bool equalsIgnoreCase(std::string_view left, std::string_view right) {
    return left.size() == right.size() && std::equal(left.begin(), left.end(), right.begin(), [](char a, char b) {
        return std::tolower(static_cast<unsigned char>(a)) == std::tolower(static_cast<unsigned char>(b));
    });
}

std::uint16_t defaultPort(std::string_view scheme) { return equalsIgnoreCase(scheme, "https") ? 443 : 80; }

struct Url {
    std::string scheme, host, target;
    std::uint16_t port = 0;
};

// Splits "scheme://host[:port]/path?query" or a bare "/path?query".
bool parseUrl(std::string_view text, Url& url) {
    const auto schemeEnd = text.find("://");
    if (schemeEnd == text.npos) {
        url.target = text.empty() ? "/" : std::string(text);
        if (url.target.front() != '/') url.target.insert(url.target.begin(), '/');
        return true;
    }
    url.scheme = std::string(text.substr(0, schemeEnd));
    text.remove_prefix(schemeEnd + 3);
    const auto pathStart = text.find_first_of("/?#");
    std::string_view authority = text.substr(0, pathStart);
    if (const auto at = authority.rfind('@'); at != authority.npos) authority.remove_prefix(at + 1);
    url.port = defaultPort(url.scheme);
    if (const auto colon = authority.rfind(':'); colon != authority.npos && authority.find(']') == authority.npos) {
        url.port = static_cast<std::uint16_t>(std::atoi(std::string(authority.substr(colon + 1)).c_str()));
        authority = authority.substr(0, colon);
    }
    url.host = std::string(authority);
    std::string_view rest = pathStart == text.npos ? std::string_view{} : text.substr(pathStart);
    if (const auto fragment = rest.find('#'); fragment != rest.npos) rest = rest.substr(0, fragment);
    url.target = rest.empty() ? "/" : std::string(rest);
    if (url.target.front() == '?') url.target.insert(url.target.begin(), '/');
    return !url.host.empty();
}

bool isDirect(std::string_view scheme, std::string_view host, std::uint16_t port) {
    if (!equalsIgnoreCase(scheme, "http")) return false;
    if (port != 80 || equalsIgnoreCase(host, "localhost")) return true;
    // An IPv4 or IPv6 address rather than a name.
    return !host.empty() && (host.find(':') != host.npos ||
                             host.find_first_not_of("0123456789.") == host.npos);
}

std::shared_ptr<Object> find(int id, Kind kind) {
    const auto it = g_objects.find(id);
    return it != g_objects.end() && it->second->kind == kind ? it->second : nullptr;
}

std::shared_ptr<Object> findAny(int id) {
    const auto it = g_objects.find(id);
    return it != g_objects.end() ? it->second : nullptr;
}

int add(std::shared_ptr<Object> object) {
    const int id = g_nextId++;
    g_objects.emplace(id, std::move(object));
    return id;
}

void setHeader(std::vector<Header>& headers, std::string_view name, std::string_view value, bool append) {
    if (!append) {
        std::erase_if(headers, [&](const Header& header) { return equalsIgnoreCase(header.name, name); });
    }
    headers.push_back({std::string(name), std::string(value)});
}

bool sendAll(NativeSocket socket, const char* data, std::size_t size) {
    while (size > 0) {
        const int sent = ::send(socket, data, static_cast<int>(std::min<std::size_t>(size, 1 << 20)), 0);
        if (sent <= 0) return false;
        data += sent;
        size -= static_cast<std::size_t>(sent);
    }
    return true;
}

std::string decodeChunked(std::string_view body) {
    std::string out;
    while (!body.empty()) {
        const auto lineEnd = body.find("\r\n");
        if (lineEnd == body.npos) break;
        const std::size_t size = std::strtoull(std::string(body.substr(0, lineEnd)).c_str(), nullptr, 16);
        body.remove_prefix(lineEnd + 2);
        if (size == 0 || size > body.size()) break;
        out.append(body.substr(0, size));
        body.remove_prefix(std::min(body.size(), size + 2));
    }
    return out;
}

// Whether the response holds all of its body, by Content-Length or the chunked encoding's last
// chunk. Servers may keep the connection open after answering despite "Connection: close" (the
// shadNet web API does), so reading stops here instead of waiting for the server to close.
bool responseComplete(std::string_view response, bool head_request) {
    const auto headerEnd = response.find("\r\n\r\n");
    if (headerEnd == response.npos) return false;
    if (head_request) return true;
    const auto headers = response.substr(0, headerEnd + 2);
    const std::size_t bodySize = response.size() - headerEnd - 4;
    const auto space = response.find(' ');
    const int status = space != response.npos ? std::atoi(std::string(response.substr(space + 1, 3)).c_str()) : 0;
    if (status == 204 || status == 304 || (status >= 100 && status < 200)) return true;
    for (std::size_t lineStart = headers.find("\r\n") + 2; lineStart < headers.size();) {
        const auto lineEnd = headers.find("\r\n", lineStart);
        const auto line = headers.substr(lineStart, lineEnd - lineStart);
        lineStart = lineEnd + 2;
        const auto colon = line.find(':');
        if (colon == line.npos) continue;
        const auto name = line.substr(0, colon);
        const auto value = line.substr(colon + 1);
        if (equalsIgnoreCase(name, "Content-Length")) {
            return bodySize >= static_cast<std::size_t>(std::atoll(std::string(value).c_str()));
        }
        if (equalsIgnoreCase(name, "Transfer-Encoding") && value.find("chunked") != value.npos) {
            return response.ends_with("\r\n0\r\n\r\n") || (bodySize == 5 && response.ends_with("0\r\n\r\n"));
        }
    }
    return false; // the body runs until the server closes the connection
}

// One request/response exchange over a fresh connection.
bool exchange(const std::string& request, const std::string& host, std::uint16_t port, std::string& response) {
    if (!initializeSockets()) return false;
    addrinfo hints{};
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    addrinfo* addresses = nullptr;
    if (getaddrinfo(host.c_str(), std::to_string(port).c_str(), &hints, &addresses) != 0) return false;
    NativeSocket socket = INVALID_NATIVE_SOCKET;
    for (addrinfo* address = addresses; address; address = address->ai_next) {
        socket = ::socket(address->ai_family, address->ai_socktype, address->ai_protocol);
        if (socket == INVALID_NATIVE_SOCKET) continue;
        setTimeout(socket, SO_RCVTIMEO, SOCKET_TIMEOUT_SECONDS);
        setTimeout(socket, SO_SNDTIMEO, SOCKET_TIMEOUT_SECONDS);
        if (::connect(socket, address->ai_addr, static_cast<int>(address->ai_addrlen)) == 0) break;
        closeSocket(socket);
        socket = INVALID_NATIVE_SOCKET;
    }
    freeaddrinfo(addresses);
    if (socket == INVALID_NATIVE_SOCKET) return false;
    bool ok = sendAll(socket, request.data(), request.size());
    const bool head = request.starts_with("HEAD ");
    char buffer[16384];
    while (ok && !responseComplete(response, head)) {
        const int received = ::recv(socket, buffer, sizeof(buffer), 0);
        if (received < 0) ok = false;
        if (received <= 0) break;
        response.append(buffer, static_cast<std::size_t>(received));
    }
    closeSocket(socket);
    return ok && !response.empty();
}

bool parseResponse(const std::string& response, Object& request) {
    const auto headerEnd = response.find("\r\n\r\n");
    if (headerEnd == response.npos || !response.starts_with("HTTP/")) return false;
    const auto space = response.find(' ');
    if (space == response.npos || space > headerEnd) return false;
    request.status = std::atoi(response.c_str() + space + 1);
    request.responseHeaders = response.substr(0, headerEnd + 4);
    std::string_view body = std::string_view(response).substr(headerEnd + 4);

    bool chunked = false;
    long long contentLength = -1;
    std::string_view headers = std::string_view(request.responseHeaders);
    for (std::size_t lineStart = headers.find("\r\n") + 2; lineStart < headers.size();) {
        const auto lineEnd = headers.find("\r\n", lineStart);
        const auto line = headers.substr(lineStart, lineEnd - lineStart);
        lineStart = lineEnd + 2;
        const auto colon = line.find(':');
        if (colon == line.npos) continue;
        const auto name = line.substr(0, colon);
        std::string value(line.substr(colon + 1));
        value.erase(0, value.find_first_not_of(' '));
        if (equalsIgnoreCase(name, "Content-Length")) contentLength = std::atoll(value.c_str());
        else if (equalsIgnoreCase(name, "Transfer-Encoding") && value.find("chunked") != value.npos) chunked = true;
    }
    if (chunked) request.body = decodeChunked(body);
    else if (contentLength >= 0) request.body = std::string(body.substr(0, static_cast<std::size_t>(contentLength)));
    else request.body = std::string(body);
    return true;
}

}

const char* MethodName(int method) {
    static constexpr const char* names[] = {"GET", "POST", "HEAD", "OPTIONS", "PUT", "DELETE", "TRACE", "CONNECT"};
    return method >= 0 && method < static_cast<int>(std::size(names)) ? names[method] : nullptr;
}

int Create(Kind kind, int parent) {
    std::lock_guard lock(g_lock);
    auto object = std::make_shared<Object>();
    object->kind = kind;
    object->parent = parent;
    return add(std::move(object));
}

int CreateConnection(int tmpl_id, std::string_view scheme, std::string_view host, std::uint16_t port) {
    std::lock_guard lock(g_lock);
    if (!find(tmpl_id, Kind::Template)) return ERROR_INVALID_ID;
    auto object = std::make_shared<Object>();
    object->kind = Kind::Connection;
    object->parent = tmpl_id;
    object->scheme = scheme.empty() ? "http" : std::string(scheme);
    object->host = std::string(host);
    object->port = port ? port : defaultPort(object->scheme);
    return add(std::move(object));
}

int CreateConnectionWithUrl(int tmpl_id, const char* url) {
    if (!url) return ERROR_INVALID_VALUE;
    Url parts;
    if (!parseUrl(url, parts) || parts.host.empty()) return ERROR_INVALID_URL;
    return CreateConnection(tmpl_id, parts.scheme, parts.host, parts.port);
}

int CreateRequest(int conn_id, std::string_view method, const char* path_or_url, std::uint64_t content_length) {
    if (!path_or_url) return ERROR_INVALID_VALUE;
    Url parts;
    if (!parseUrl(path_or_url, parts)) return ERROR_INVALID_URL;
    std::lock_guard lock(g_lock);
    const auto connection = find(conn_id, Kind::Connection);
    if (!connection) return ERROR_INVALID_ID;
    auto object = std::make_shared<Object>();
    object->kind = Kind::Request;
    object->parent = conn_id;
    object->method = std::string(method);
    object->target = parts.target;
    object->contentLength = content_length;
    return add(std::move(object));
}

int Delete(int id) {
    std::lock_guard lock(g_lock);
    return g_objects.erase(id) ? 0 : ERROR_INVALID_ID;
}

int SetUserAgent(int tmpl_id, const char* user_agent) {
    std::lock_guard lock(g_lock);
    const auto tmpl = find(tmpl_id, Kind::Template);
    if (!tmpl) return ERROR_INVALID_ID;
    tmpl->userAgent = user_agent ? user_agent : "";
    return 0;
}

int AddHeader(int id, const char* name, const char* value, std::uint32_t mode) {
    if (!name || !value) return ERROR_INVALID_VALUE;
    std::lock_guard lock(g_lock);
    const auto object = findAny(id);
    if (!object) return ERROR_INVALID_ID;
    setHeader(object->headers, name, value, mode == HEADER_MODE_ADD);
    return 0;
}

int SetContentLength(int req_id, std::uint64_t content_length) {
    std::lock_guard lock(g_lock);
    const auto request = find(req_id, Kind::Request);
    if (!request) return ERROR_INVALID_ID;
    request->contentLength = content_length;
    return 0;
}

int SetEpoll(int id, void* epoll, void* user_arg) {
    std::lock_guard lock(g_lock);
    const auto request = find(id, Kind::Request);
    if (!request) return ERROR_INVALID_ID;
    request->epoll = epoll;
    request->epollArg = user_arg;
    return 0;
}

int UnsetEpoll(int id) {
    std::lock_guard lock(g_lock);
    const auto request = find(id, Kind::Request);
    if (!request) return ERROR_INVALID_ID;
    request->epoll = nullptr;
    return 0;
}

int Send(int req_id, const void* post_data, std::size_t size) {
    std::shared_ptr<Object> request;
    std::vector<Header> headers;
    std::string userAgent, scheme, host;
    std::uint16_t port = 0;
    {
        std::lock_guard lock(g_lock);
        request = find(req_id, Kind::Request);
        if (!request) return ERROR_INVALID_ID;
        // Headers set on the template and connection apply to every request made through them.
        std::vector<std::shared_ptr<Object>> chain{request};
        for (int parent = request->parent; parent;) {
            auto object = findAny(parent);
            if (!object) break;
            chain.push_back(object);
            parent = object->parent;
        }
        for (auto it = chain.rbegin(); it != chain.rend(); ++it) {
            for (const auto& header : (*it)->headers) setHeader(headers, header.name, header.value, false);
            if ((*it)->kind == Kind::Template) userAgent = (*it)->userAgent;
            if ((*it)->kind == Kind::Connection) {
                scheme = (*it)->scheme;
                host = (*it)->host;
                port = (*it)->port;
            }
        }
        const auto jar = g_cookies.find(lowerCase(host));
        const bool hasCookieHeader = std::any_of(headers.begin(), headers.end(), [](const Header& header) {
            return equalsIgnoreCase(header.name, "Cookie");
        });
        if (jar != g_cookies.end() && !jar->second.empty() && !hasCookieHeader) {
            std::string cookies;
            for (const auto& [name, value] : jar->second) cookies += (cookies.empty() ? "" : "; ") + name + "=" + value;
            headers.push_back({"Cookie", cookies});
        }
    }

    // Plain HTTP to an address, localhost or an unusual port is a server the player set up (the
    // shadNet web API that shadGT's NP libraries use, for one), so it is reached as it is. Everything
    // else is the game's own servers and goes to RyGTServer, and only with online on.
    const bool direct = isDirect(scheme, host, port);
    if (!direct && !RyGTOnline::Enabled()) return ERROR_NETWORK;
    const auto& config = RyGTOnline::Get();
    const std::size_t bodySize = post_data ? size : 0;

    std::string text = request->method + " " + request->target + " HTTP/1.1\r\n";
    text += "Host: " + host + (port != defaultPort(scheme) ? ":" + std::to_string(port) : "") + "\r\n";
    text += "X-RyGT-Scheme: " + scheme + "\r\n";
    text += "X-RyGT-Online-Id: " + config.onlineId + "\r\n";
    if (!userAgent.empty()) text += "User-Agent: " + userAgent + "\r\n";
    for (const auto& header : headers) {
        if (equalsIgnoreCase(header.name, "Host") || equalsIgnoreCase(header.name, "Connection") ||
            equalsIgnoreCase(header.name, "Content-Length") || equalsIgnoreCase(header.name, "Transfer-Encoding")) {
            continue;
        }
        text += header.name + ": " + header.value + "\r\n";
    }
    if (bodySize || request->method != "GET") text += "Content-Length: " + std::to_string(bodySize) + "\r\n";
    text += "Connection: close\r\n\r\n";
    if (bodySize) text.append(static_cast<const char*>(post_data), bodySize);

    std::string response;
    Object result;
    const bool ok = (direct ? exchange(text, host, port, response)
                            : exchange(text, config.serverHost, config.serverPort, response)) &&
                    parseResponse(response, result);
    // Every request at Info; failures (no server answered) at Warning, the default level.
    if (RuntimeStdoutLog::Enabled(ok ? 2 : 3)) {
        std::printf("[online] %s %s://%s:%u%s via %s -> %d\n", request->method.c_str(), scheme.c_str(), host.c_str(),
                    port, request->target.c_str(), direct ? "direct" : "RyGTServer", ok ? result.status : -1);
        std::fflush(stdout);
    }
    {
        std::lock_guard lock(g_lock);
        if (ok) {
            const std::string_view head(result.responseHeaders);
            for (std::size_t lineStart = head.find("\r\n") + 2; lineStart < head.size();) {
                const auto lineEnd = head.find("\r\n", lineStart);
                const auto line = head.substr(lineStart, lineEnd - lineStart);
                lineStart = lineEnd + 2;
                if (line.size() > 11 && equalsIgnoreCase(line.substr(0, 11), "Set-Cookie:")) storeCookie(lowerCase(host), line.substr(11));
            }
        }
        request->sent = true;
        request->failed = !ok;
        request->reported = false;
        request->status = result.status;
        request->responseHeaders = std::move(result.responseHeaders);
        request->body = std::move(result.body);
        request->readOffset = 0;
    }
    g_completed.notify_all();
    return ok ? 0 : ERROR_NETWORK;
}

int Abort(int req_id) {
    std::lock_guard lock(g_lock);
    return find(req_id, Kind::Request) ? 0 : ERROR_INVALID_ID;
}

int GetStatusCode(int req_id, int* status_code) {
    if (!status_code) return ERROR_INVALID_VALUE;
    std::lock_guard lock(g_lock);
    const auto request = find(req_id, Kind::Request);
    if (!request) return ERROR_INVALID_ID;
    if (!request->sent || request->failed) return ERROR_NETWORK;
    *status_code = request->status;
    return 0;
}

int GetContentLength(int req_id, int* result, std::uint64_t* content_length) {
    if (!result || !content_length) return ERROR_INVALID_VALUE;
    std::lock_guard lock(g_lock);
    const auto request = find(req_id, Kind::Request);
    if (!request) return ERROR_INVALID_ID;
    if (!request->sent || request->failed) return ERROR_NETWORK;
    *result = CONTENTLEN_EXIST;
    *content_length = request->body.size();
    return 0;
}

int GetAllHeaders(int req_id, char** header, std::size_t* header_size) {
    if (!header || !header_size) return ERROR_INVALID_VALUE;
    std::lock_guard lock(g_lock);
    const auto request = find(req_id, Kind::Request);
    if (!request) return ERROR_INVALID_ID;
    if (!request->sent || request->failed) return ERROR_NETWORK;
    // The buffer belongs to the request and stays valid until it is deleted, as on the system.
    *header = request->responseHeaders.data();
    *header_size = request->responseHeaders.size();
    return 0;
}

int Read(int req_id, void* data, std::size_t size) {
    if (!data && size) return ERROR_INVALID_VALUE;
    std::lock_guard lock(g_lock);
    const auto request = find(req_id, Kind::Request);
    if (!request) return ERROR_INVALID_ID;
    if (!request->sent || request->failed) return ERROR_NETWORK;
    const std::size_t count = std::min(size, request->body.size() - request->readOffset);
    std::memcpy(data, request->body.data() + request->readOffset, count);
    request->readOffset += count;
    return static_cast<int>(count);
}

int Wait(void* epoll, void* events, int max_events, int timeout_usec) {
    if (!epoll || !events || max_events <= 0) return ERROR_INVALID_VALUE;
    auto* out = static_cast<NbEvent*>(events);
    const auto limit = std::chrono::microseconds(timeout_usec < 0 ? 1000000 : timeout_usec);
    std::unique_lock lock(g_lock);
    int count = 0;
    const auto collect = [&] {
        for (auto& [id, object] : g_objects) {
            if (count >= max_events) break;
            if (object->kind != Kind::Request || object->epoll != epoll || !object->sent || object->reported) continue;
            object->reported = true;
            out[count++] = {object->failed ? NB_EVENT_SOCK_ERR : NB_EVENT_IN | NB_EVENT_OUT, 0, id, object->epollArg};
        }
        return count > 0;
    };
    g_completed.wait_for(lock, limit, collect);
    return count;
}

int AddCookie(const char* url, const char* cookie, std::size_t length) {
    if (!url || !cookie) return ERROR_INVALID_VALUE;
    Url parts;
    if (!parseUrl(url, parts) || parts.host.empty()) return ERROR_INVALID_URL;
    std::lock_guard lock(g_lock);
    storeCookie(lowerCase(parts.host), std::string_view(cookie, length ? length : std::strlen(cookie)));
    return 0;
}

int ExportCookies(void* buffer, std::size_t size, std::size_t* exported) {
    std::string text;
    {
        std::lock_guard lock(g_lock);
        for (const auto& [host, cookies] : g_cookies)
            for (const auto& [name, value] : cookies) text += host + "\t" + name + "=" + value + "\n";
    }
    if (exported) *exported = text.size();
    if (!buffer) return 0;
    if (size < text.size()) return ERROR_OUT_OF_MEMORY;
    std::memcpy(buffer, text.data(), text.size());
    return 0;
}

int ImportCookies(const void* buffer, std::size_t size) {
    if (!buffer && size) return ERROR_INVALID_VALUE;
    const std::string_view text(static_cast<const char*>(buffer), size);
    std::lock_guard lock(g_lock);
    for (std::size_t start = 0; start < text.size();) {
        const auto end = std::min(text.find('\n', start), text.size());
        const auto line = text.substr(start, end - start);
        start = end + 1;
        const auto tab = line.find('\t');
        if (tab != line.npos) storeCookie(lowerCase(line.substr(0, tab)), line.substr(tab + 1));
    }
    return 0;
}

void ForgetEpoll(void* epoll) {
    std::lock_guard lock(g_lock);
    for (auto& [id, object] : g_objects) {
        if (object->epoll == epoll) object->epoll = nullptr;
    }
}

}
