#include <cstdint>
#include <cstddef>
#include <cctype>
#include <string_view>
#include "SceTypes.hpp"
#include "prx/libc/include/General.hpp"
#include "prx/libSceHttp/src/Bridge.hpp"
#include "prx/libSceHttp/src/HttpErrors.hpp"

// Requests go to RyGTServer when online is enabled in RyGT.ini (see OnlineConfig.hpp and
// src/Bridge.cpp). Otherwise contexts, templates and requests can still be created, but any
// request that would touch the network fails with the library's network error.
using HttpBridge::Kind;

extern "C" {

int APS5_VABI sceHttpParseResponseHeader(const char* header, size_t header_len, const char* field_str,
                                         const char** field_value, size_t* value_len) {
    if (!header || !field_str || !field_value || !value_len) return ERROR_INVALID_VALUE;
    const std::string_view headers(header, header_len);
    const std::string_view field(field_str);
    for (size_t lineStart = 0; lineStart < headers.size();) {
        size_t lineEnd = headers.find("\r\n", lineStart);
        if (lineEnd == headers.npos) lineEnd = headers.size();
        const auto line = headers.substr(lineStart, lineEnd - lineStart);
        lineStart = lineEnd + 2;
        if (line.find(':') != field.size()) continue;
        bool match = true;
        for (size_t i = 0; i < field.size() && match; ++i)
            match = std::tolower(static_cast<unsigned char>(line[i])) == std::tolower(static_cast<unsigned char>(field[i]));
        if (!match) continue;
        size_t valueStart = field.size() + 1;
        while (valueStart < line.size() && (line[valueStart] == ' ' || line[valueStart] == '\t')) ++valueStart;
        *field_value = line.data() + valueStart;
        *value_len = line.size() - valueStart;
        return static_cast<int>(lineEnd);
    }
    return ERROR_PARSE_HTTP_NOT_FOUND;
}

int APS5_VABI sceHttpAbortRequest(int request_id) {
    return HttpBridge::Abort(request_id);
}

int APS5_VABI sceHttpGetMemoryPoolStats(int http_ctx_id, void* stats) {
    (void)http_ctx_id;
    (void)stats;
    return 0;
}

int APS5_VABI sceHttpAddRequestHeader(int id, const char* name, const char* value, uint32_t mode) {
    return HttpBridge::AddHeader(id, name, value, mode);
}

int APS5_VABI sceHttpCreateConnection(int tmpl_id, const char* server_name, const char* scheme, uint16_t port, int enable_keep_alive) {
    (void)enable_keep_alive;
    if (!server_name) return ERROR_INVALID_VALUE;
    return HttpBridge::CreateConnection(tmpl_id, scheme ? scheme : "http", server_name, port);
}

int APS5_VABI sceHttpCreateConnectionWithURL(int tmpl_id, const char* url, int enable_keep_alive) {
    (void)enable_keep_alive;
    return HttpBridge::CreateConnectionWithUrl(tmpl_id, url);
}

int APS5_VABI sceHttpCreateEpoll(int http_ctx_id, HttpEpollHandle* eh) {
    (void)http_ctx_id;
    if (!eh) return ERROR_INVALID_VALUE;
    *eh = new HttpEpoll{};
    return 0;
}

int APS5_VABI sceHttpCreateRequest(int conn_id, int method, const char* path, uint64_t content_length) {
    const char* name = HttpBridge::MethodName(method);
    if (!name) return ERROR_INVALID_VALUE;
    return HttpBridge::CreateRequest(conn_id, name, path, content_length);
}

int APS5_VABI sceHttpCreateRequest2(int conn_id, const char* method, const char* path,
                                   uint64_t content_length) {
    if (!method) return ERROR_INVALID_VALUE;
    return HttpBridge::CreateRequest(conn_id, method, path, content_length);
}

int APS5_VABI sceHttpCreateRequestWithURL2(int conn_id, const char* method, const char* url, uint64_t content_length) {
    if (!method) return ERROR_INVALID_VALUE;
    return HttpBridge::CreateRequest(conn_id, method, url, content_length);
}

int APS5_VABI sceHttpCreateTemplate(int http_ctx_id, const char* user_agent, int http_ver, int is_auto_proxy_conf) {
    (void)http_ver;
    (void)is_auto_proxy_conf;
    const int id = HttpBridge::Create(Kind::Template, http_ctx_id);
    HttpBridge::SetUserAgent(id, user_agent);
    return id;
}

int APS5_VABI sceHttpDeleteConnection(int conn_id) {
    HttpBridge::Delete(conn_id);
    return 0;
}

int APS5_VABI sceHttpDeleteRequest(int req_id) {
    HttpBridge::Delete(req_id);
    return 0;
}

int APS5_VABI sceHttpDeleteTemplate(int tmpl_id) {
    HttpBridge::Delete(tmpl_id);
    return 0;
}

int APS5_VABI sceHttpDestroyEpoll(int http_ctx_id, HttpEpollHandle eh) {
    (void)http_ctx_id;
    HttpBridge::ForgetEpoll(eh);
    delete eh;
    return 0;
}

int APS5_VABI sceHttpGetAllResponseHeaders(int request_id, char** header, size_t* header_size) {
    return HttpBridge::GetAllHeaders(request_id, header, header_size);
}

int APS5_VABI sceHttpGetResponseContentLength(int request_id, int* result, uint64_t* content_length) {
    return HttpBridge::GetContentLength(request_id, result, content_length);
}

int APS5_VABI sceHttpGetStatusCode(int request_id, int* status_code) {
    return HttpBridge::GetStatusCode(request_id, status_code);
}

int APS5_VABI sceHttpInit_nid_postfix(int memid, int ssl_ctx_id, uint64_t pool_size) {
    (void)memid;
    (void)ssl_ctx_id;
    (void)pool_size;
    return HttpBridge::Create(Kind::Context, 0);
}

int APS5_VABI sceHttpsDisableOption(int id, uint32_t ssl_flags) {
    (void)id;
    (void)ssl_flags;
    return 0;
}

int APS5_VABI sceHttpSendRequest(int request_id, const void* post_data, size_t size) {
    return HttpBridge::Send(request_id, post_data, size);
}

int APS5_VABI sceHttpSetAuthEnabled(int id, int enable) {
    (void)id;
    (void)enable;
    return 0;
}

int APS5_VABI sceHttpSetAutoRedirect(int id, int enable) {
    (void)id;
    (void)enable;
    return 0;
}

int APS5_VABI sceHttpSetConnectTimeOut(int id, uint32_t usec) {
    (void)id;
    (void)usec;
    return 0;
}

int APS5_VABI sceHttpSetEpoll(int id, HttpEpollHandle eh, void* user_arg) {
    return HttpBridge::SetEpoll(id, eh, user_arg);
}

// Requests complete within sceHttpSendRequest, so non-blocking mode needs no separate handling:
// a non-blocking caller sees the send succeed and its epoll report the request ready.
int APS5_VABI sceHttpSetNonblock(int id, int enable) {
    (void)id;
    (void)enable;
    return 0;
}

int APS5_VABI sceHttpSetRecvTimeOut(int id, uint32_t usec) {
    (void)id;
    (void)usec;
    return 0;
}

int APS5_VABI sceHttpSetRequestContentLength(int request_id, uint64_t content_length) {
    return HttpBridge::SetContentLength(request_id, content_length);
}

int APS5_VABI sceHttpSetResolveRetry(int id, int32_t retry) {
    (void)id;
    (void)retry;
    return 0;
}

int APS5_VABI sceHttpSetResolveTimeOut(int id, uint32_t usec) {
    (void)id;
    (void)usec;
    return 0;
}

int APS5_VABI sceHttpSetSendTimeOut(int id, uint32_t usec) {
    (void)id;
    (void)usec;
    return 0;
}

int APS5_VABI sceHttpsSetMinSslVersion(int id, uint32_t ssl_version) {
    (void)id;
    (void)ssl_version;
    return 0;
}

// RyGTServer is reached over plain HTTP, so there is no certificate for the callback to check.
int APS5_VABI sceHttpsSetSslCallback(int id, HttpsCallback cbfunc, void* user_arg) {
    (void)id;
    (void)cbfunc;
    (void)user_arg;
    return 0;
}

int APS5_VABI sceHttpTerm_nid_postfix(int http_ctx_id) {
    HttpBridge::Delete(http_ctx_id);
    return 0;
}

int APS5_VABI sceHttpUnsetEpoll(int id) {
    return HttpBridge::UnsetEpoll(id);
}

int APS5_VABI sceHttpWaitRequest(HttpEpollHandle eh, HttpNBEvent* nbev, int maxevents, int timeout) {
    return HttpBridge::Wait(eh, nbev, maxevents, timeout);
}

int APS5_VABI sceHttpCreateRequestWithURL(int conn_id, int method, const char* url, uint64_t content_length) {
    const char* name = HttpBridge::MethodName(method);
    if (!name) return ERROR_INVALID_VALUE;
    return HttpBridge::CreateRequest(conn_id, name, url, content_length);
}

int APS5_VABI sceHttpReadData(int request_id, void* data, size_t size) {
    return HttpBridge::Read(request_id, data, size);
}

int APS5_VABI sceHttpSetChunkedTransferEnabled(int id, int enable) {
    (void)id;
    (void)enable;
    return 0;
}

int APS5_VABI sceHttpGetAutoRedirect(int id, int* is_enable) {
    (void)id;
    if (!is_enable) return ERROR_INVALID_VALUE;
    *is_enable = 1;
    return 0;
}

int APS5_VABI sceHttpGetLastErrno(int request_id, int* err_num) {
    (void)request_id;
    if (!err_num) return ERROR_INVALID_VALUE;
    *err_num = 0;
    return 0;
}

int APS5_VABI sceHttpAddCookie(int http_ctx_id, const char* url, const char* cookie, size_t cookie_length) {
    (void)http_ctx_id;
    return HttpBridge::AddCookie(url, cookie, cookie_length);
}

int APS5_VABI sceHttpCookieExport(int http_ctx_id, void* buffer, size_t buffer_size, size_t* export_size) {
    (void)http_ctx_id;
    return HttpBridge::ExportCookies(buffer, buffer_size, export_size);
}

int APS5_VABI sceHttpCookieImport(int http_ctx_id, const void* buffer, size_t buffer_size) {
    (void)http_ctx_id;
    return HttpBridge::ImportCookies(buffer, buffer_size);
}

// Cookies are stored by the bridge itself, so there is no system cookie store to report from.
int APS5_VABI sceHttpSetCookieRecvCallback(int id, void* cbfunc, void* user_arg) {
    (void)id;
    (void)cbfunc;
    (void)user_arg;
    return 0;
}

// The game's HTTPS goes to RyGTServer as plain HTTP, so there is no TLS: certificates and options
// are accepted and no TLS error is ever reported.
int APS5_VABI sceHttpsLoadCert(int http_ctx_id, int ca_cert_num, const void** ca_list, const void* cert, const void* priv_key) {
    (void)http_ctx_id;
    (void)ca_cert_num;
    (void)ca_list;
    (void)cert;
    (void)priv_key;
    return 0;
}

int APS5_VABI sceHttpsUnloadCert(int http_ctx_id) {
    (void)http_ctx_id;
    return 0;
}

int APS5_VABI sceHttpsEnableOption(int id, uint32_t ssl_flags) {
    (void)id;
    (void)ssl_flags;
    return 0;
}

int APS5_VABI sceHttpsGetSslError(int id, int* err_num, uint32_t* detail) {
    (void)id;
    if (err_num) *err_num = 0;
    if (detail) *detail = 0;
    return 0;
}

}

// Remaining HTTP APIs report network unavailable; no PSN transport is emulated.
extern "C" {
int APS5_VABI sceHttp2AbortRequest() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2AddCookie() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2AddRequestHeader() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2AuthCacheFlush() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2CookieExport() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2CookieFlush() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2CookieImport() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2CreateCookieBox() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2CreateRequestWithURL() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2CreateTemplate() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2DeleteCookieBox() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2DeleteRequest() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2DeleteTemplate() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2GetAllResponseHeaders() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2GetAllTrailingHeaders() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2GetAuthEnabled() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2GetAutoRedirect() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2GetCookie() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2GetCookieBox() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2GetCookieStats() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2GetMemoryPoolStats() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2GetResponseContentLength() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2GetStatusCode() { return ERROR_NETWORK; }
// GT7 initialises HTTP/2 before PSN's web API and gives up on the web API (PE-0553404 in Lobby)
// when this fails. The web API itself is shadGT's (in libSceNpAuth.prx), which only needs an ID.
int APS5_VABI sceHttp2Init(int libnet_mem_id, int libssl_ctx_id, size_t pool_size, int max_requests) {
    (void)libnet_mem_id;
    (void)libssl_ctx_id;
    (void)pool_size;
    (void)max_requests;
    static int next = 0;
    return ++next;
}
int APS5_VABI sceHttp2ReadData() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2ReadDataAsync() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2RedirectCacheFlush() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2RemoveRequestHeader() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2SendRequest() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2SendRequestAsync() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2SetAuthEnabled() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2SetAuthInfoCallback() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2SetAutoRedirect() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2SetConnectionWaitTimeOut() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2SetConnectTimeOut() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2SetCookieBox() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2SetCookieMaxNum() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2SetCookieMaxNumPerDomain() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2SetCookieMaxSize() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2SetCookieRecvCallback() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2SetCookieSendCallback() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2SetInflateGZIPEnabled() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2SetMinSslVersion() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2SetPreSendCallback() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2SetRecvTimeOut() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2SetRedirectCallback() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2SetRequestContentLength() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2SetRequestNoContentLength() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2SetResolveRetry() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2SetResolveTimeOut() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2SetSendTimeOut() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2SetSslCallback() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2SetTimeOut() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2SslDisableOption() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2SslEnableOption() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2Term(int) { return 0; }
int APS5_VABI sceHttp2WaitAsync() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2WebSocketClose() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2WebSocketCloseAsync() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2WebSocketCreateRequest() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2WebSocketSendDataMessage() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2WebSocketSendDataMessageAsync() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2WebSocketSendTextMessage() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2WebSocketSendTextMessageAsync() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2WebSocketSetPingInterval() { return ERROR_NETWORK; }
int APS5_VABI sceHttp2WebSocketSetPingTimeout() { return ERROR_NETWORK; }
int APS5_VABI sceHttpAbortRequestForce() { return ERROR_NETWORK; }
int APS5_VABI sceHttpAbortWaitRequest() { return ERROR_NETWORK; }
int APS5_VABI sceHttpAddQuery() { return ERROR_NETWORK; }
int APS5_VABI sceHttpAddRequestHeaderRaw() { return ERROR_NETWORK; }
int APS5_VABI sceHttpAuthCacheExport() { return ERROR_NETWORK; }
int APS5_VABI sceHttpAuthCacheFlush() { return ERROR_NETWORK; }
int APS5_VABI sceHttpAuthCacheImport() { return ERROR_NETWORK; }
int APS5_VABI sceHttpCacheClear() { return ERROR_NETWORK; }
int APS5_VABI sceHttpCacheClearAll() { return ERROR_NETWORK; }
int APS5_VABI sceHttpCacheCompleteRequest() { return ERROR_NETWORK; }
int APS5_VABI sceHttpCacheCreateRequest() { return ERROR_NETWORK; }
int APS5_VABI sceHttpCacheCreateRequestWithTag() { return ERROR_NETWORK; }
int APS5_VABI sceHttpCacheDeleteRequest() { return ERROR_NETWORK; }
int APS5_VABI sceHttpCacheInit() { return ERROR_NETWORK; }
int APS5_VABI sceHttpCacheReadData() { return ERROR_NETWORK; }
int APS5_VABI sceHttpCacheRedirectedConnectionEnabled() { return ERROR_NETWORK; }
int APS5_VABI sceHttpCacheRetrieve() { return ERROR_NETWORK; }
int APS5_VABI sceHttpCacheRetrieveWithMemoryPool() { return ERROR_NETWORK; }
int APS5_VABI sceHttpCacheRevalidate() { return ERROR_NETWORK; }
int APS5_VABI sceHttpCacheRevalidateWithMemoryPool() { return ERROR_NETWORK; }
int APS5_VABI sceHttpCacheSetCacheSharing() { return ERROR_NETWORK; }
int APS5_VABI sceHttpCacheSetData() { return ERROR_NETWORK; }
int APS5_VABI sceHttpCacheSetQuota() { return ERROR_NETWORK; }
int APS5_VABI sceHttpCacheSetResponseHeader() { return ERROR_NETWORK; }
int APS5_VABI sceHttpCacheSystemClearAll() { return ERROR_NETWORK; }
int APS5_VABI sceHttpCacheSystemInit() { return ERROR_NETWORK; }
int APS5_VABI sceHttpCacheSystemSendStatistics() { return ERROR_NETWORK; }
int APS5_VABI sceHttpCacheSystemShutdown() { return ERROR_NETWORK; }
int APS5_VABI sceHttpCacheSystemTerm() { return ERROR_NETWORK; }
int APS5_VABI sceHttpCacheTerm() { return ERROR_NETWORK; }
int APS5_VABI sceHttpCookieFlush() { return ERROR_NETWORK; }
int APS5_VABI sceHttpDbgEnableProfile() { return ERROR_NETWORK; }
int APS5_VABI sceHttpDbgGetConnectionStat() { return ERROR_NETWORK; }
int APS5_VABI sceHttpDbgGetRequestStat() { return ERROR_NETWORK; }
int APS5_VABI sceHttpDbgSetPrintf() { return ERROR_NETWORK; }
int APS5_VABI sceHttpDbgShowConnectionStat() { return ERROR_NETWORK; }
int APS5_VABI sceHttpDbgShowMemoryPoolStat() { return ERROR_NETWORK; }
int APS5_VABI sceHttpDbgShowRequestStat() { return ERROR_NETWORK; }
int APS5_VABI sceHttpDbgShowStat() { return ERROR_NETWORK; }
int APS5_VABI sceHttpGetAcceptEncodingGZIPEnabled() { return ERROR_NETWORK; }
int APS5_VABI sceHttpGetAuthEnabled() { return ERROR_NETWORK; }
int APS5_VABI sceHttpGetConnectionStat() { return ERROR_NETWORK; }
int APS5_VABI sceHttpGetCookie() { return ERROR_NETWORK; }
int APS5_VABI sceHttpGetCookieEnabled() { return ERROR_NETWORK; }
int APS5_VABI sceHttpGetCookieStats() { return ERROR_NETWORK; }
int APS5_VABI sceHttpGetEpoll() { return ERROR_NETWORK; }
int APS5_VABI sceHttpGetEpollId() { return ERROR_NETWORK; }
int APS5_VABI sceHttpGetNonblock() { return ERROR_NETWORK; }
int APS5_VABI sceHttpGetRegisteredCtxIds() { return ERROR_NETWORK; }
int APS5_VABI sceHttpInit() { return ERROR_NETWORK; }
int APS5_VABI sceHttpParseStatusLine() { return ERROR_NETWORK; }
int APS5_VABI sceHttpRedirectCacheFlush() { return ERROR_NETWORK; }
int APS5_VABI sceHttpRemoveRequestHeader() { return ERROR_NETWORK; }
int APS5_VABI sceHttpRequestGetAllHeaders() { return ERROR_NETWORK; }
int APS5_VABI sceHttpsDisableOptionPrivate() { return ERROR_NETWORK; }
int APS5_VABI sceHttpsEnableOptionPrivate() { return ERROR_NETWORK; }
int APS5_VABI sceHttpSetAcceptEncodingGZIPEnabled() { return ERROR_NETWORK; }
int APS5_VABI sceHttpSetAuthInfoCallback() { return ERROR_NETWORK; }
int APS5_VABI sceHttpSetCookieEnabled() { return ERROR_NETWORK; }
int APS5_VABI sceHttpSetCookieMaxNum() { return ERROR_NETWORK; }
int APS5_VABI sceHttpSetCookieMaxNumPerDomain() { return ERROR_NETWORK; }
int APS5_VABI sceHttpSetCookieMaxSize() { return ERROR_NETWORK; }
int APS5_VABI sceHttpSetCookieSendCallback() { return ERROR_NETWORK; }
int APS5_VABI sceHttpSetCookieTotalMaxSize() { return ERROR_NETWORK; }
int APS5_VABI sceHttpSetDefaultAcceptEncodingGZIPEnabled() { return ERROR_NETWORK; }
int APS5_VABI sceHttpSetDelayBuildRequestEnabled() { return ERROR_NETWORK; }
int APS5_VABI sceHttpSetEpollId() { return ERROR_NETWORK; }
int APS5_VABI sceHttpSetHttp09Enabled() { return ERROR_NETWORK; }
int APS5_VABI sceHttpSetInflateGZIPEnabled() { return ERROR_NETWORK; }
int APS5_VABI sceHttpSetPolicyOption() { return ERROR_NETWORK; }
int APS5_VABI sceHttpSetPriorityOption() { return ERROR_NETWORK; }
int APS5_VABI sceHttpSetProxy() { return ERROR_NETWORK; }
int APS5_VABI sceHttpSetRecvBlockSize() { return ERROR_NETWORK; }
int APS5_VABI sceHttpSetRedirectCallback() { return ERROR_NETWORK; }
int APS5_VABI sceHttpSetRequestStatusCallback() { return ERROR_NETWORK; }
int APS5_VABI sceHttpSetResponseHeaderMaxSize() { return ERROR_NETWORK; }
int APS5_VABI sceHttpSetSocketCreationCallback() { return ERROR_NETWORK; }
int APS5_VABI sceHttpsFreeCaList() { return ERROR_NETWORK; }
int APS5_VABI sceHttpsGetCaList() { return ERROR_NETWORK; }
int APS5_VABI sceHttpsSetSslVersion() { return ERROR_NETWORK; }
int APS5_VABI sceHttpTerm() { return ERROR_NETWORK; }
int APS5_VABI sceHttpTryGetNonblock() { return ERROR_NETWORK; }
int APS5_VABI sceHttpTrySetNonblock() { return ERROR_NETWORK; }
int APS5_VABI sceHttpUriCopy() { return ERROR_NETWORK; }
int APS5_VABI sceHttpUriMerge() { return ERROR_NETWORK; }
int APS5_VABI sceHttpUriSweepPath() { return ERROR_NETWORK; }
}

