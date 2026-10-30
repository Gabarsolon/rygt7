#include <cstdint>
#include <cstddef>
#include <cstring>
#include <algorithm>
#include "SceTypes.hpp"
#include "OnlineConfig.hpp"
#include "prx/libc/include/General.hpp"
#include <atomic>
#include <mutex>

// PSN is replaced by RyGTServer. With online off (the default) the user is reported as signed out
// and online queries fail. With online on, the user is signed in under the local identity from
// RyGT.ini, and the game's online traffic goes to RyGTServer through libSceHttp.
static constexpr int SCE_NP_ERROR_INVALID_ARGUMENT = static_cast<int>(0x80550003);
static constexpr int SCE_NP_ERROR_SIGNED_OUT = static_cast<int>(0x80550006);
static constexpr uint32_t NP_STATE_SIGNED_OUT = 1;
static constexpr uint32_t NP_STATE_SIGNED_IN = 2;
static constexpr int NP_POLL_ASYNC_FINISHED = 0;
static constexpr uint32_t NP_REACHABILITY_STATE_UNAVAILABLE = 0;
static constexpr uint32_t NP_REACHABILITY_STATE_REACHABLE = 2;
// libSceUserService's only user.
static constexpr int INITIAL_USER_ID = 0x10000000;

using NpStateCallback = void (APS5_VABI*)(int user_id, uint32_t state, NpId* np_id, void* userdata);
using NpStateCallbackA = void (APS5_VABI*)(int user_id, uint32_t state, void* userdata);
using NpReachabilityCallback = void (APS5_VABI*)(int user_id, uint32_t state, void* userdata);

static std::atomic<int> g_nextRequest{1};

namespace {

// Each registered callback is told about the signed-in state once, from sceNpCheckCallback on the
// game's own thread, as the system does after sign-in.
template <typename Callback>
struct PendingCallback {
    Callback func = nullptr;
    void* arg = nullptr;
    bool due = false;

    void Set(void* callback, void* userdata) {
        func = reinterpret_cast<Callback>(callback);
        arg = userdata;
        due = true;
    }
    Callback Take(void*& userdata) {
        if (!due) return nullptr;
        due = false;
        userdata = arg;
        return func;
    }
};

std::mutex g_callbackLock;
PendingCallback<NpStateCallback> g_stateCallback;
PendingCallback<NpStateCallbackA> g_stateCallbackA;
PendingCallback<NpReachabilityCallback> g_reachabilityCallback;

bool online() { return RyGTOnline::Enabled(); }

void fillOnlineId(NpOnlineId* online_id) {
    std::memset(online_id, 0, sizeof(*online_id));
    const auto& name = RyGTOnline::Get().onlineId;
    std::memcpy(online_id->data, name.data(), std::min<size_t>(name.size(), 16));
}

void fillNpId(NpId* np_id) {
    std::memset(np_id, 0, sizeof(*np_id));
    fillOnlineId(&np_id->online_id);
}

}

extern "C" {

int APS5_VABI sceNpAbortRequest(int req_id) {
    (void)req_id;
    return 0;
}

int APS5_VABI sceNpCheckCallback(void) {
    if (!online()) return 0;
    void *stateArg = nullptr, *stateAArg = nullptr, *reachabilityArg = nullptr;
    NpStateCallback state;
    NpStateCallbackA stateA;
    NpReachabilityCallback reachability;
    {
        std::lock_guard lock(g_callbackLock);
        state = g_stateCallback.Take(stateArg);
        stateA = g_stateCallbackA.Take(stateAArg);
        reachability = g_reachabilityCallback.Take(reachabilityArg);
    }
    if (state) {
        NpId npId{};
        fillNpId(&npId);
        state(INITIAL_USER_ID, NP_STATE_SIGNED_IN, &npId, stateArg);
    }
    if (stateA) stateA(INITIAL_USER_ID, NP_STATE_SIGNED_IN, stateAArg);
    if (reachability) reachability(INITIAL_USER_ID, NP_REACHABILITY_STATE_REACHABLE, reachabilityArg);
    return 0;
}

int APS5_VABI sceNpCheckNpAvailability(int req_id, const char* user, void* result) {
    (void)req_id;
    (void)user;
    (void)result;
    return 0;
}

int APS5_VABI sceNpCheckNpReachability(int req_id, int user_id) {
    (void)req_id;
    (void)user_id;
    return online() ? 0 : SCE_NP_ERROR_SIGNED_OUT;
}

int APS5_VABI sceNpCheckPremium(int req_id, const NpCheckPremiumParameter* param, NpCheckPremiumResult* result) {
    (void)req_id;
    (void)param;
    if (!online()) return SCE_NP_ERROR_SIGNED_OUT;
    if (!result) return SCE_NP_ERROR_INVALID_ARGUMENT;
    // Everyone on a RyGTServer counts as a PlayStation Plus member, so online play is allowed.
    auto* data = reinterpret_cast<NpCheckPremiumResultData*>(result);
    std::memset(data, 0, sizeof(*data));
    data->authorized = true;
    return 0;
}

int APS5_VABI sceNpCreateAsyncRequest(const NpCreateAsyncRequestParameter* param) {
    (void)param;
    return g_nextRequest.fetch_add(1, std::memory_order_relaxed);
}

int APS5_VABI sceNpCreateRequest(void) {
    return g_nextRequest.fetch_add(1, std::memory_order_relaxed);
}

int APS5_VABI sceNpDeleteRequest(int req_id) {
    (void)req_id;
    return 0;
}

int APS5_VABI sceNpGetAccountAge(int req_id, int user_id, uint8_t* age) {
    (void)req_id;
    (void)user_id;
    if (!age) return SCE_NP_ERROR_INVALID_ARGUMENT;
    if (!online()) return SCE_NP_ERROR_SIGNED_OUT;
    *age = 30;
    return 0;
}

int APS5_VABI sceNpGetAccountCountryA(int user_id, void* country_code) {
    (void)user_id;
    if (!online()) return SCE_NP_ERROR_SIGNED_OUT;
    if (!country_code) return SCE_NP_ERROR_INVALID_ARGUMENT;
    auto* code = static_cast<NpCountryCode*>(country_code);
    std::memset(code, 0, sizeof(*code));
    code->data[0] = 'u';
    code->data[1] = 's';
    return 0;
}

int APS5_VABI sceNpGetAccountIdA(int user_id, uint64_t* account_id) {
    (void)user_id;
    if (!online()) return SCE_NP_ERROR_SIGNED_OUT;
    if (!account_id) return SCE_NP_ERROR_INVALID_ARGUMENT;
    *account_id = RyGTOnline::Get().accountId;
    return 0;
}

int APS5_VABI sceNpGetNpId(int user_id, NpId* np_id) {
    (void)user_id;
    // Offline the ID is left untouched, as before online support.
    if (!online()) return 0;
    if (!np_id) return SCE_NP_ERROR_INVALID_ARGUMENT;
    fillNpId(np_id);
    return 0;
}

int APS5_VABI sceNpGetNpReachabilityState(int user_id, uint32_t* state) {
    (void)user_id;
    if (!state) return SCE_NP_ERROR_INVALID_ARGUMENT;
    *state = online() ? NP_REACHABILITY_STATE_REACHABLE : NP_REACHABILITY_STATE_UNAVAILABLE;
    return 0;
}

int APS5_VABI sceNpGetOnlineId(int user_id, NpOnlineId* online_id) {
    (void)user_id;
    if (!online()) return SCE_NP_ERROR_SIGNED_OUT;
    if (!online_id) return SCE_NP_ERROR_INVALID_ARGUMENT;
    fillOnlineId(online_id);
    return 0;
}

int APS5_VABI sceNpGetState(int user_id, uint32_t* state) {
    (void)user_id;
    if (!state) return SCE_NP_ERROR_INVALID_ARGUMENT;
    *state = online() ? NP_STATE_SIGNED_IN : NP_STATE_SIGNED_OUT;
    return 0;
}

int APS5_VABI sceNpHasSignedUp(int user_id, bool* has_signed_up) {
    (void)user_id;
    if (!has_signed_up) return SCE_NP_ERROR_INVALID_ARGUMENT;
    *has_signed_up = online();
    return 0;
}

int APS5_VABI sceNpPollAsync(int req_id, int* result) {
    (void)req_id;
    if (result) *result = online() ? 0 : SCE_NP_ERROR_SIGNED_OUT;
    return NP_POLL_ASYNC_FINISHED;
}

void APS5_VABI sceNpRegisterGamePresenceCallback(void* callback, void* userdata) {
    (void)callback;
    (void)userdata;
}

int APS5_VABI sceNpRegisterNpReachabilityStateCallback(void* callback, void* userdata) {
    if (!callback) return SCE_NP_ERROR_INVALID_ARGUMENT;
    std::lock_guard lock(g_callbackLock);
    g_reachabilityCallback.Set(callback, userdata);
    return 0;
}

int APS5_VABI sceNpRegisterPlusEventCallback(void* callback, void* userdata) {
    (void)userdata;
    if (!callback) return SCE_NP_ERROR_INVALID_ARGUMENT;
    return 0;
}

int APS5_VABI sceNpRegisterPremiumEventCallback(void* callback, void* userdata) {
    (void)userdata;
    if (!callback) return SCE_NP_ERROR_INVALID_ARGUMENT;
    return 0;
}

int APS5_VABI sceNpRegisterStateCallback(void* callback, void* userdata) {
    if (!callback) return SCE_NP_ERROR_INVALID_ARGUMENT;
    std::lock_guard lock(g_callbackLock);
    g_stateCallback.Set(callback, userdata);
    return 0;
}

int APS5_VABI sceNpSetContentRestriction(const NpContentRestriction* restriction) {
    (void)restriction;
    return 0;
}

int APS5_VABI sceNpSetNpTitleId(const NpTitleId* title_id, const NpTitleSecret* title_secret) {
    (void)title_id;
    (void)title_secret;
    return 0;
}

int APS5_VABI sceNpUnregisterStateCallback(void) {
    std::lock_guard lock(g_callbackLock);
    g_stateCallback = {};
    return 0;
}

int APS5_VABI sceNpGetAccountLanguage2(int req_id, int user_id, void* language) {
    (void)req_id;
    (void)user_id;
    (void)language;
    return SCE_NP_ERROR_SIGNED_OUT;
}

int APS5_VABI sceNpNotifyPremiumFeature(const void* param) {
    (void)param;
    return 0;
}

int APS5_VABI sceNpRegisterStateCallbackA(void* callback, void* userdata) {
    if (!callback) return SCE_NP_ERROR_INVALID_ARGUMENT;
    std::lock_guard lock(g_callbackLock);
    g_stateCallbackA.Set(callback, userdata);
    return 1;
}

int APS5_VABI sceNpUnregisterStateCallbackA(int callback_id) {
    (void)callback_id;
    std::lock_guard lock(g_callbackLock);
    g_stateCallbackA = {};
    return 0;
}

int APS5_VABI sceNpUnregisterPremiumEventCallback(void) {
    return 0;
}

}
