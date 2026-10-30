#include <cstdint>
#include <cstddef>
#include <cstdio>
#include <cstring>
#include <atomic>
#include "SceTypes.hpp"
#include "OnlineConfig.hpp"
#include "prx/libc/include/General.hpp"

static constexpr int SCE_NP_ERROR_INVALID_ARGUMENT = static_cast<int>(0x80550003);
static constexpr int SCE_NP_ERROR_SIGNED_OUT = static_cast<int>(0x80550006);
static constexpr int NP_POLL_ASYNC_FINISHED = 0;
// PSN's issuer ID for the production environment.
static constexpr int NP_ISSUER_ID = 256;

static std::atomic<int> g_nextRequest{1};

namespace {

struct NpAuthorizationCode {
    char code[128 + 1];
    std::uint8_t padding[7];
};

struct NpIdToken {
    char token[4096 + 1];
    std::uint8_t padding[7];
};

// With online on, codes and tokens are not secrets: they name the local player so RyGTServer can
// tell players apart. The game forwards them to its servers, where RyGTServer reads them back.
void writeCredential(char* out, size_t size, const char* kind) {
    const auto& config = RyGTOnline::Get();
    std::snprintf(out, size, "rygt-%s.%s.%016llx", kind, config.onlineId.c_str(),
                  static_cast<unsigned long long>(config.accountId));
}

}

extern "C" {

int APS5_VABI sceNpAuthAbortRequest(int req_id) {
    (void)req_id;
    return 0;
}

int APS5_VABI sceNpAuthCreateAsyncRequest(const void* param) {
    (void)param;
    return g_nextRequest.fetch_add(1, std::memory_order_relaxed);
}

int APS5_VABI sceNpAuthCreateRequest(void) {
    return g_nextRequest.fetch_add(1, std::memory_order_relaxed);
}

int APS5_VABI sceNpAuthDeleteRequest(int req_id) {
    (void)req_id;
    return 0;
}

int APS5_VABI sceNpAuthGetAuthorizationCodeV3(int req_id, const void* param, void* auth_code, int* issuer_id) {
    (void)req_id;
    if (!param || !auth_code) return SCE_NP_ERROR_INVALID_ARGUMENT;
    if (!RyGTOnline::Enabled()) return SCE_NP_ERROR_SIGNED_OUT;
    auto* code = static_cast<NpAuthorizationCode*>(auth_code);
    std::memset(code, 0, sizeof(*code));
    writeCredential(code->code, sizeof(code->code), "code");
    if (issuer_id) *issuer_id = NP_ISSUER_ID;
    return 0;
}

int APS5_VABI sceNpAuthGetIdTokenV3(int req_id, const void* param, void* id_token) {
    (void)req_id;
    // Offline the token is left untouched, as before online support.
    if (!RyGTOnline::Enabled()) return 0;
    if (!param || !id_token) return SCE_NP_ERROR_INVALID_ARGUMENT;
    auto* token = static_cast<NpIdToken*>(id_token);
    std::memset(token, 0, sizeof(*token));
    writeCredential(token->token, sizeof(token->token), "token");
    return 0;
}

int APS5_VABI sceNpAuthPollAsync(int req_id, int* result) {
    (void)req_id;
    if (result) *result = RyGTOnline::Enabled() ? 0 : SCE_NP_ERROR_SIGNED_OUT;
    return NP_POLL_ASYNC_FINISHED;
}

int APS5_VABI sceNpAuthWaitAsync(int req_id, int* result) {
    (void)req_id;
    if (result) *result = RyGTOnline::Enabled() ? 0 : SCE_NP_ERROR_SIGNED_OUT;
    return 0;
}

}
