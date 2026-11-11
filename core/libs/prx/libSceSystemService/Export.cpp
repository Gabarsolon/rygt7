#include <cstddef>
#include <cstdint>
#include <cstring>
#include <cstdlib>
#ifdef _WIN32
#include <windows.h>
#endif
#include "prx/libc/include/Shutdown.hpp"
#include "SceTypes.hpp"
#include "prx/libc/include/General.hpp"
#include "prx/libSceSystemService/SystemService.hpp"
#include "prx/libSceSystemService/HdrLuminance.hpp"

extern "C" {

int APS5_VABI sceSystemServiceGetVersionNumberOfCameraCalibrationData() {
    return 0;
}

int APS5_VABI sceSystemServiceRequestCameraCalibration() {
    return 0;
}

int APS5_VABI sceSystemServiceSetOutOfVrPlayAreaFlag(int flag) {
    (void)flag;
    return 0;
}

int APS5_VABI sceSystemServiceSetOutOfVrPlayZoneWarning(int flag) {
    (void)flag;
    return 0;
}

int APS5_VABI sceSystemServiceDisableMusicPlayer() {
    return 0;
}

int APS5_VABI sceSystemServiceReenableMusicPlayer() {
    return 0;
}

int APS5_VABI sceSystemServiceLoadExec(const char* path, const char* const* arguments) {
    if (!path || !*path) return SYSTEM_SERVICE_ERROR_PARAMETER;
    if (std::strcmp(path, "exit") != 0) {
        NotImplemented_nid_no_patch("sceSystemServiceLoadExec: executable replacement");
    }
    (void)arguments;
    LibcRunShutdown_nid_postfix();
    std::exit(0);
}

int APS5_VABI sceSystemServiceDisableNoticeScreenSkipFlagAutoSet(void) {
 return SYSTEM_SERVICE_OK;
}

int APS5_VABI sceSystemServiceGetDisplaySafeAreaInfo(SystemServiceDisplaySafeAreaInfo* info) {
 if (info == nullptr) {
  return SYSTEM_SERVICE_ERROR_PARAMETER;
 }
 *info = SystemServiceDisplaySafeAreaInfo{};
 info->ratio = 1.0f;
 return SYSTEM_SERVICE_OK;
}

int APS5_VABI sceSystemServiceGetHdrToneMapLuminance(SystemServiceHdrToneMapLuminance* luminance) {
 if (luminance == nullptr) {
  return SYSTEM_SERVICE_ERROR_PARAMETER;
 }
 // FIX-GT7-141: the display's luminance when HDR output is allowed and Windows runs a display
 // in HDR (HdrLuminance.hpp); 100 / 100 / 0 as before otherwise.
 const HdrLuminance::Luminance& display = HdrLuminance::ForGame();
 luminance->max_full_frame_tone_map_luminance = display.full_frame;
 luminance->max_tone_map_luminance = display.peak;
 luminance->min_tone_map_luminance = display.min;
 return SYSTEM_SERVICE_OK;
}

int APS5_VABI sceSystemServiceGetNoticeScreenSkipFlag(bool* value) {
 if (value == nullptr) {
  return SYSTEM_SERVICE_ERROR_PARAMETER;
 }
 *value = false;
 return SYSTEM_SERVICE_OK;
}

int APS5_VABI sceSystemServiceGetStatus(SystemServiceStatus* status) {
 if (status == nullptr) {
  return SYSTEM_SERVICE_ERROR_PARAMETER;
 }
 *status = SystemServiceStatus{};
 return SYSTEM_SERVICE_OK;
}

int APS5_VABI sceSystemServiceHideSplashScreen(void) {
#ifdef _WIN32
    // RyGT: the renderer (shadGT's libSceVideoOut.prx) shows the game's splash screen until the
    // game hides it, as the system does on the console.
    using HideFunction = void (*)();
    if (const HMODULE renderer = GetModuleHandleA("libSceVideoOut.prx")) {
        if (const auto hide = reinterpret_cast<HideFunction>(
                reinterpret_cast<void*>(GetProcAddress(renderer, "RytyGpuHideSplashScreen")))) {
            hide();
        }
    }
#endif
 return SYSTEM_SERVICE_OK;
}

int APS5_VABI sceSystemServiceParamGetInt(int paramId, int* value) {
 if (value == nullptr) {
  return SYSTEM_SERVICE_ERROR_PARAMETER;
 }
 switch (paramId) {
  case SYSTEM_SERVICE_PARAM_ID_LANG: *value = SYSTEM_SERVICE_PARAM_LANG_ENGLISH_US; break;
  case SYSTEM_SERVICE_PARAM_ID_DATE_FORMAT: *value = SYSTEM_SERVICE_PARAM_DATE_FORMAT_DDMMYYYY; break;
  case SYSTEM_SERVICE_PARAM_ID_TIME_FORMAT: *value = SYSTEM_SERVICE_PARAM_TIME_FORMAT_24HOUR; break;
  case SYSTEM_SERVICE_PARAM_ID_TIME_ZONE: *value = 0; break;
  case SYSTEM_SERVICE_PARAM_ID_SUMMERTIME: *value = 0; break;
  case SYSTEM_SERVICE_PARAM_ID_GAME_PARENTAL_LEVEL: *value = SYSTEM_SERVICE_PARAM_GAME_PARENTAL_OFF; break;
  case SYSTEM_SERVICE_PARAM_ID_ENTER_BUTTON_ASSIGN: *value = SYSTEM_SERVICE_PARAM_ENTER_BUTTON_CROSS; break;
  default: *value = 0; break;
 }
 return SYSTEM_SERVICE_OK;
}

int APS5_VABI sceSystemServiceParamGetString(int param_id, char* buf, size_t buf_size) {
 (void)param_id;
 (void)buf;
 (void)buf_size;
 NotImplemented_nid_no_patch(__func__);
 return 0;
}

int APS5_VABI sceSystemServicePowerTick(void) {
 NotImplemented_nid_no_patch(__func__);
 return 0;
}

int APS5_VABI sceSystemServiceReceiveEvent(SystemServiceEvent* event) {
 if (event == nullptr) {
  return SYSTEM_SERVICE_ERROR_PARAMETER;
 }
 return SYSTEM_SERVICE_ERROR_NO_EVENT;
}

int APS5_VABI sceSystemServiceReportAbnormalTermination(const void* info) {
 (void)info;
 NotImplemented_nid_no_patch(__func__);
 return 0;
}

int APS5_VABI sceSystemServiceSetNoticeScreenSkipFlag(void) {
 return SYSTEM_SERVICE_OK;
}

int APS5_VABI sceSystemServiceInitializePlayerDialogParam(void* param) {
 if (param == nullptr) return SYSTEM_SERVICE_ERROR_PARAMETER;
 return SYSTEM_SERVICE_OK;
}

int APS5_VABI sceSystemServiceLaunchPlayerDialog(const void* param) {
 if (param == nullptr) return SYSTEM_SERVICE_ERROR_PARAMETER;
 return SYSTEM_SERVICE_OK;
}

}
