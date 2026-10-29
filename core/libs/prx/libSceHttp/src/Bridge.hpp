#ifndef CORE_LIBS_PRX_LIBSCEHTTP_SRC_BRIDGE_HPP
#define CORE_LIBS_PRX_LIBSCEHTTP_SRC_BRIDGE_HPP

#include <cstddef>
#include <cstdint>
#include <string_view>

// libSceHttp's objects (contexts, templates, connections and requests) and the transport that
// carries the game's requests to RyGTServer. Every request the game makes, HTTP or HTTPS, is sent
// as plain HTTP to the configured server with the original Host header kept, so one RyGTServer can
// answer for all of the game's hosts. With online off, sending fails with the network error as it
// always has.
namespace HttpBridge {

enum class Kind { Context, Template, Connection, Request };

int Create(Kind kind, int parent);
int CreateConnection(int tmpl_id, std::string_view scheme, std::string_view host, std::uint16_t port);
int CreateConnectionWithUrl(int tmpl_id, const char* url);
int CreateRequest(int conn_id, std::string_view method, const char* path_or_url, std::uint64_t content_length);
int Delete(int id);

int SetUserAgent(int tmpl_id, const char* user_agent);
int AddHeader(int id, const char* name, const char* value, std::uint32_t mode);
int SetContentLength(int req_id, std::uint64_t content_length);
int SetEpoll(int id, void* epoll, void* user_arg);
int UnsetEpoll(int id);

int Send(int req_id, const void* post_data, std::size_t size);
int Abort(int req_id);
int GetStatusCode(int req_id, int* status_code);
int GetContentLength(int req_id, int* result, std::uint64_t* content_length);
int GetAllHeaders(int req_id, char** header, std::size_t* header_size);
int Read(int req_id, void* data, std::size_t size);

// Fills up to max_events completed requests bound to the epoll; waits up to timeout_usec when
// none are ready (a negative timeout waits up to a second, so a polling game keeps running).
int Wait(void* epoll, void* events, int max_events, int timeout_usec);
void ForgetEpoll(void* epoll);

const char* MethodName(int method);

// Cookies, kept per host like the system library's cookie store: "Set-Cookie" in responses and
// sceHttpAddCookie add to it, and requests to that host send them back. Export writes the store as
// "host<TAB>name=value" lines, which Import reads back.
int AddCookie(const char* url, const char* cookie, std::size_t length);
int ExportCookies(void* buffer, std::size_t size, std::size_t* exported);
int ImportCookies(const void* buffer, std::size_t size);

}

#endif
