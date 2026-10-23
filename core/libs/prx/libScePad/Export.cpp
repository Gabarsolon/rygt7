#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <stdexcept>
#include <chrono>
#include <thread>

#include "SceTypes.hpp"
#include "prx//libc/include/General.hpp"
#include "prx/libScePad/include/Pad.hpp"
#include "prx/libScePad/include/PadState.hpp"
#include "prx/libScePad/include/WheelInput.hpp"

namespace {

constexpr int PAD_ERROR_DEVICE_NO_HANDLE = static_cast<int>(0x80920008);
constexpr int PAD_ERROR_ALREADY_OPENED = static_cast<int>(0x80920004);

constexpr int USER_ID_FIRST = 0x10000000; // libSceUserService's player 1

// Player slot of a user (players 1-2 have user ids 0x10000000-0x10000001); the system
// user's remote port is player 1's.
int SlotOfUser(int userId) {
    if (userId == PAD_USER_ID_SYSTEM) return 0;
    const int slot = userId - USER_ID_FIRST;
    return slot >= 0 && slot < PAD_ACTIVE_PLAYERS ? slot : -1;
}

bool g_opened[PAD_MAX_PLAYERS] = {};

bool g_wheelOpened = false;

// How often the title reads the wheel through each path (logged every few seconds).
int g_parsePad = 0, g_parseWheel = 0, g_readWheel = 0;

// Logs the first few calls of each wheel-related function (the game's use of them is not
// documented, so the log is how their arguments are learned).
bool LogCall(int& count, int limit = 8) {
    return count++ < limit;
}

void LogBytes(const char* what, const void* address, std::size_t size) {
    if (!Pad::Readable(address, size)) {
        APS5_LOG_OUT("Pad:   %s %p (not readable)", what, address);
        return;
    }
    char text[3 * 64 + 1] = {};
    const auto* bytes = static_cast<const std::uint8_t*>(address);
    for (std::size_t i = 0; i < size && i < 64; ++i) std::snprintf(text + 3 * i, 4, "%02x ", bytes[i]);
    APS5_LOG_OUT("Pad:   %s %p: %s", what, address, text);
}

// The wheel handle, or the player's pad while it stands in for the wheel.
bool IsWheelHandle(int handle) {
    if (handle == PAD_WHEEL_HANDLE) return true;
    return handle == PAD_HANDLE && Pad::Wheel().padIsWheel && Pad::ReadWheel().connected;
}

void FillControllerInformation(PadControllerInformation* info, int slot, int handle) {
 info->touchPadInfo.pixelDensity = 44.86f;
 info->touchPadInfo.resolution.x = 1920;
 info->touchPadInfo.resolution.y = 943;
 info->stickInfo.deadZoneLeft = 2;
 info->stickInfo.deadZoneRight = 2;
 info->connectionType = PAD_CONNECTION_TYPE_LOCAL;
 info->connectedCount = 1;
 info->connected = handle == PAD_WHEEL_HANDLE ? Pad::ReadWheel().connected : Pad::Connected(slot);
 info->deviceClass = IsWheelHandle(handle) ? PAD_DEVICE_CLASS_STEERING_WHEEL : PAD_DEVICE_CLASS_STANDARD;
}

bool ValidPort(int userId, int type, int index) {
    const bool personalPort = type == PAD_PORT_TYPE_STANDARD || type == PAD_PORT_TYPE_SPECIAL;
    const bool systemRemote = userId == PAD_USER_ID_SYSTEM && type == PAD_PORT_TYPE_REMOTE;
    return index == 0 && SlotOfUser(userId) >= 0 && (personalPort || systemRemote);
}

// Handles are player slot + 1 (player 1's is PAD_HANDLE).
int SlotOfHandle(int handle);

// Like SlotOfHandle, with an open wheel's handle belonging to player 1.
int SlotOfAnyHandle(int handle) {
    if (handle == PAD_WHEEL_HANDLE) return g_wheelOpened ? 0 : -1;
    return SlotOfHandle(handle);
}

int SlotOfHandle(int handle) {
    const int slot = handle - PAD_HANDLE;
    return slot >= 0 && slot < PAD_MAX_PLAYERS && g_opened[slot] ? slot : -1;
}

}

extern "C" {

int APS5_VABI scePadClose_nid_postfix(int handle) {
 if (handle == PAD_WHEEL_HANDLE && g_wheelOpened) {
  APS5_LOG_OUT("Pad: scePadClose(%s)", "wheel");
  g_wheelOpened = false;
  Pad::SetWheelOpened(false);
  return PAD_OK;
 }
 const int slot = SlotOfHandle(handle);
 if (slot < 0) {
  return PAD_ERROR_INVALID_HANDLE;
 }
 g_opened[slot] = false;
 return PAD_OK;
}

int APS5_VABI scePadDeviceClassGetExtendedInformation(int handle, PadDeviceClassExtendedInformation* info) {
 static int calls = 0;
 if (LogCall(calls)) APS5_LOG_OUT("Pad: scePadDeviceClassGetExtendedInformation(handle %d)", handle);
 const int slot = SlotOfAnyHandle(handle);
 if (slot < 0) return PAD_ERROR_INVALID_HANDLE;
 if (info == nullptr) return PAD_ERROR_INVALID_ARG;
 std::memset(info, 0, sizeof(*info));
 if (!IsWheelHandle(handle)) {
  info->deviceClass = PAD_DEVICE_CLASS_STANDARD;
  return PAD_OK;
 }
 info->deviceClass = PAD_DEVICE_CLASS_STEERING_WHEEL;
 info->classData.steeringWheel.capability = static_cast<std::uint8_t>(Pad::Wheel().capability);
 info->classData.steeringWheel.maxPhysicalWheelAngle = static_cast<std::uint16_t>(Pad::Wheel().rotation);
 return PAD_OK;
}

int APS5_VABI scePadDeviceClassParseData(int handle, const PadData* data, PadDeviceClassData* class_data) {
 static int calls = 0;
 const bool log = LogCall(calls, 4);
 if (log) APS5_LOG_OUT("Pad: scePadDeviceClassParseData(handle %d)", handle);
 ++(handle == PAD_WHEEL_HANDLE ? g_parseWheel : g_parsePad);
 if (SlotOfAnyHandle(handle) < 0) return PAD_ERROR_INVALID_HANDLE;
 if (data == nullptr || class_data == nullptr) return PAD_ERROR_INVALID_ARG;
 std::memset(class_data, 0, sizeof(*class_data));
 if (!IsWheelHandle(handle)) {
  class_data->deviceClass = PAD_DEVICE_CLASS_STANDARD;
  class_data->dataValid = data->connected;
  return PAD_OK;
 }
 const auto& settings = Pad::Wheel();
 (void)data;
 const WheelState wheel = Pad::ReadWheel();
 const float position = static_cast<float>(wheel.steering) / 32768.0f;
 const float degrees = position * static_cast<float>(settings.rotation) * 0.5f;
 const auto pedal = [&settings](std::uint16_t value) {
  return static_cast<std::uint16_t>((static_cast<std::uint32_t>(value) * settings.pedalMax + 32767) / 65535);
 };
 class_data->deviceClass = PAD_DEVICE_CLASS_STEERING_WHEEL;
 class_data->dataValid = wheel.connected;
 class_data->classData.steeringWheel.angle = settings.angleRadians ? degrees * 0.017453292f : degrees;
 class_data->classData.steeringWheel.wheel = static_cast<std::uint16_t>(wheel.steering + 32768);
 class_data->classData.steeringWheel.accelerator = pedal(wheel.throttle);
 class_data->classData.steeringWheel.brake = pedal(wheel.brake);
 class_data->classData.steeringWheel.clutch = pedal(wheel.clutch);
 // Every few seconds, what the title receives (to check ranges against how the car responds).
 static auto nextReport = std::chrono::steady_clock::time_point{};
 const auto now = std::chrono::steady_clock::now();
 if (log || now >= nextReport) {
  nextReport = now + std::chrono::seconds(3);
  APS5_LOG_OUT("Pad: wheel reads: parse pad %d, parse wheel %d, read wheel %d", g_parsePad, g_parseWheel, g_readWheel);
  APS5_LOG_OUT("Pad: wheel data: angle %.1f wheel %u throttle %u brake %u clutch %u valid %d", class_data->classData.steeringWheel.angle,
   class_data->classData.steeringWheel.wheel, class_data->classData.steeringWheel.accelerator, class_data->classData.steeringWheel.brake,
   class_data->classData.steeringWheel.clutch, class_data->dataValid);
 }
 return PAD_OK;
}

int APS5_VABI scePadGetControllerInformation(int handle, PadControllerInformation* info) {
 const int slot = SlotOfAnyHandle(handle);
 if (slot < 0) {
  return PAD_ERROR_INVALID_HANDLE;
 }
 if (info == nullptr) {
  return PAD_ERROR_INVALID_ARG;
 }
 std::memset(info, 0, sizeof(*info));
 FillControllerInformation(info, slot, handle);
 return PAD_OK;
}

int APS5_VABI scePadGetHandle(int user_id, int type, int index) {
 if (!ValidPort(user_id, type, index)) return PAD_ERROR_DEVICE_NO_HANDLE;
 const int slot = SlotOfUser(user_id);
 if (!g_opened[slot]) return PAD_ERROR_DEVICE_NO_HANDLE;
 return PAD_HANDLE + slot;
}

int APS5_VABI scePadGetInfo_nid_postfix(PadInfo* info) {
 if (info == nullptr) return PAD_ERROR_INVALID_ARG;
 std::memset(info, 0, sizeof(*info));
 info->maxConnectCount = PAD_ACTIVE_PLAYERS;
 int connected = 0;
 for (int slot = 0; slot < PAD_MAX_PLAYERS; ++slot) connected += Pad::Connected(slot) ? 1 : 0;
 info->connectedCount[0] = static_cast<std::uint8_t>(connected);
 info->connectedCount[1] = 0;
 PadInfo::PadTypeInfo& slot = info->padTypeInfo[0];
 slot.connectPort = 0;
 slot.status = 1;
 slot.deviceType = PAD_DEVICE_TYPE_DUAL_SENSE;
 slot.connectType = PAD_CONNECT_TYPE_BLUETOOTH;
 std::memcpy(slot.bluetoothMacAddress, PAD_BD_ADDRESS, sizeof(slot.bluetoothMacAddress));
 return PAD_OK;
}

int APS5_VABI scePadGetTriggerEffectState(int handle, PadTriggerEffectStateInformation* info) {
 const int slot = SlotOfHandle(handle);
 if (slot < 0) return PAD_ERROR_INVALID_HANDLE;
 if (info == nullptr) return PAD_ERROR_INVALID_ARG;
 std::memset(info, 0, sizeof(*info));
 return PAD_OK;
}

int APS5_VABI scePadGetExtControllerInformation(int handle, void* info) {
 static int calls = 0;
 if (LogCall(calls)) APS5_LOG_OUT("Pad: scePadGetExtControllerInformation(handle %d)", handle);
 const int slot = SlotOfAnyHandle(handle);
 if (slot < 0) return PAD_ERROR_INVALID_HANDLE;
 if (info == nullptr) return PAD_ERROR_INVALID_ARG;
 std::memset(info, 0, 0x2c);
 FillControllerInformation(static_cast<PadControllerInformation*>(info), slot, handle);
 if (IsWheelHandle(handle)) {
  // Extended part: the wheel's USB vendor and product (logiWheel picks its model from them),
  // capability, then the class's union (max wheel angle).
  auto* bytes = static_cast<std::uint8_t*>(info);
  const auto vendor = static_cast<std::uint16_t>(Pad::Wheel().vendor);
  const auto product = static_cast<std::uint16_t>(Pad::Wheel().product);
  std::memcpy(bytes + 0x1c, &vendor, 2);
  std::memcpy(bytes + 0x1e, &product, 2);
  bytes[0x20] = static_cast<std::uint8_t>(Pad::Wheel().capability);
  const std::int32_t angle = Pad::Wheel().rotation;
  std::memcpy(bytes + 0x24, &angle, sizeof(angle));
 }
 return PAD_OK;
}

int APS5_VABI scePadSetProcessPrivilege(int privilege) {
 (void)privilege;
 return PAD_OK;
}

int APS5_VABI scePadSetParticularMode(bool mode) {
 (void)mode;
 return PAD_OK;
}

int APS5_VABI scePadInit_nid_postfix(void) {
 Pad::Initialize();
 return PAD_OK;
}

int APS5_VABI scePadOpen_nid_postfix(int userId, int type, int index, const void* param) {
 (void)param;
 static int calls = 0;
 if (!ValidPort(userId, type, index)) {
  if (LogCall(calls, 8)) APS5_LOG_OUT("Pad: scePadOpen(user 0x%x, type %d, index %d) -> invalid port", userId, type, index);
  return PAD_ERROR_INVALID_ARG;
 }
 const int slot = SlotOfUser(userId);
 g_opened[slot] = true;
 if (LogCall(calls, 8)) APS5_LOG_OUT("Pad: scePadOpen(user 0x%x, type %d, index %d) -> handle %d", userId, type, index, PAD_HANDLE + slot);
 return PAD_HANDLE + slot;
}

int APS5_VABI scePadReadState(int handle, PadData* data);

int APS5_VABI scePadRead_nid_postfix(int handle, PadData* data, int num) {
    // The title drains the queued states; one current state is reported per call.
    if (data == nullptr || num <= 0) return PAD_ERROR_INVALID_ARG;
    const int result = scePadReadState(handle, data);
    if (result != 0) return result;
    return 1;
}

int APS5_VABI scePadOpenExt(int userId, int type, int index, const std::uint16_t* param) {
 const bool readable = Pad::Readable(param, 6);
 const unsigned vendor = readable ? param[0] : 0;
 const unsigned product = readable ? param[1] : 0;
 const unsigned product2 = readable ? param[2] : 0;
 // The request names a vendor and a product range; answer only the one the wheel presents as.
 // Titles ask while starting up, so give the wheel thread a moment to find the wheel first.
 Pad::WaitForWheel(5000);
 const WheelState wheel = Pad::ReadWheel();
 const auto& settings = Pad::Wheel();
 const unsigned last = product2 != 0 ? product2 : product;
 const bool wanted = vendor == settings.vendor && settings.product >= product && settings.product <= last;
 int result;
 if (wheel.connected && g_wheelOpened && wanted && settings.IsFanatec()) {
  // The Fanatec SDK checks for the wheel now and then by opening it again; on a PS4 the wheel
  // it already has open still counts.
  result = PAD_ERROR_ALREADY_OPENED;
 } else if (!wheel.connected || g_wheelOpened || !wanted) {
  result = PAD_ERROR_DEVICE_NOT_CONNECTED;
 } else {
  g_wheelOpened = true;
  Pad::SetWheelOpened(true);
  result = PAD_WHEEL_HANDLE;
 }
 APS5_LOG_OUT("Pad: scePadOpenExt(user 0x%x, type %d, index %d, device %04x:%04x/%04x) -> 0x%x", userId, type, index, vendor, product, product2,
  static_cast<unsigned>(result));
 if (param != nullptr) LogBytes("param", param, 16);
 return result;
}

int APS5_VABI scePadReadStateExt(int handle, PadData* data) {
 static int calls = 0;
 if (LogCall(calls, 2)) APS5_LOG_OUT("Pad: scePadReadStateExt(handle %d)", handle);
 if (handle == PAD_HANDLE) {
  if (data == nullptr) return PAD_ERROR_INVALID_ARG;
  *data = Pad::ReadState(0);
  return PAD_OK;
 }
 if (handle != PAD_WHEEL_HANDLE || !g_wheelOpened) return PAD_ERROR_INVALID_HANDLE;
 if (data == nullptr) return PAD_ERROR_INVALID_ARG;
 ++g_readWheel;
 const WheelState wheel = Pad::ReadWheel();
 std::memset(data, 0, sizeof(*data));
 data->buttons = wheel.buttons;
 data->left_stick_x = data->left_stick_y = data->right_stick_x = data->right_stick_y = 128;
 if (Pad::Wheel().IsFanatec()) {
  // A Fanatec base reports its wheel in the pad data's device-unique block, which GT Sport's
  // Fanatec port reads in a race (eboot 0x1c38174): steering (16 bits, centre 0x8000), then
  // accelerator and brake (16 bits, released at 0xffff, with a deadzone of 3276 at that end),
  // handbrake (byte 7) and clutch (byte 10) released at 0xff, and the H-shifter gear (byte 8,
  // one bit per gear, 0 in neutral).
  const auto pedal = [](std::uint16_t value) {
   return static_cast<std::uint16_t>(0xffff - std::min<std::uint32_t>(0xffff, 3276u + static_cast<std::uint32_t>(value) * 62259u / 65535u));
  };
  const auto steering = static_cast<std::uint16_t>(std::clamp(wheel.steering + 32768, 0, 65535));
  const std::uint16_t accelerator = pedal(wheel.throttle);
  const std::uint16_t brake = pedal(wheel.brake);
  data->device_unique_data_len = 12;
  std::memcpy(data->device_unique_data + 0, &steering, 2);
  std::memcpy(data->device_unique_data + 2, &accelerator, 2);
  std::memcpy(data->device_unique_data + 4, &brake, 2);
  data->device_unique_data[7] = 0xff;
  data->device_unique_data[8] = 0;
  data->device_unique_data[10] = static_cast<std::uint8_t>(0xff - (wheel.clutch >> 8));
 }
 data->orientation_w = 1.0f;
 data->acceleration_y = 1.0f;
 data->connected = wheel.connected;
 data->connected_count = 1;
 data->timestamp = Pad::ReadState(0).timestamp;
 return PAD_OK;
}

int APS5_VABI scePadReadExt(int handle, PadData* data, int num) {
 static int calls = 0;
 if (LogCall(calls, 2)) APS5_LOG_OUT("Pad: scePadReadExt(handle %d, num %d)", handle, num);
 if (data == nullptr || num <= 0) return PAD_ERROR_INVALID_ARG;
 const int result = scePadReadStateExt(handle, data);
 return result != PAD_OK ? result : 1;
}

// Force feedback and wheel configuration reach a PS4 wheel as raw HID reports through these.
// Their arguments are not documented, so they are logged and accepted for now.
// scePadOutputReport(handle, report id, report, length): logiWheel sends the G29's force
// feedback as report 0x30, a report-id byte followed by Logitech classic 8-byte commands; the
// Fanatec SDK sends one 7-byte command per report 0x30 (0x34 for some).
int APS5_VABI scePadOutputReport(std::uint64_t a, std::uint64_t b, std::uint64_t c, std::uint64_t d) {
 if (static_cast<int>(a) == PAD_WHEEL_HANDLE && (b == 0x30 || b == 0x34) && c > 0x10000 && d >= 2 && d <= 64 && Pad::Readable(reinterpret_cast<const void*>(c), d)) {
  Pad::WheelReport(reinterpret_cast<const std::uint8_t*>(c), static_cast<std::size_t>(d));
 }
 static int calls = 0;
 if (LogCall(calls, 10)) {
  APS5_LOG_OUT("Pad: scePadOutputReport(0x%llx, 0x%llx, 0x%llx, 0x%llx)", static_cast<unsigned long long>(a), static_cast<unsigned long long>(b),
   static_cast<unsigned long long>(c), static_cast<unsigned long long>(d));
  if (b > 0x10000) LogBytes("b", reinterpret_cast<const void*>(b), 32);
  if (c > 0x10000) LogBytes("c", reinterpret_cast<const void*>(c), 32);
 }
 return PAD_OK;
}

int APS5_VABI scePadGetFeatureReport(std::uint64_t a, std::uint64_t b, std::uint64_t c, std::uint64_t d) {
 static int calls = 0;
 if (static_cast<int>(a) == PAD_WHEEL_HANDLE && g_wheelOpened && c > 0x10000 && d >= 1 && d <= 256 &&
  Pad::WheelFeatureReport(static_cast<int>(b), reinterpret_cast<std::uint8_t*>(c), static_cast<std::size_t>(d))) {
  if (LogCall(calls, 20)) APS5_LOG_OUT("Pad: scePadGetFeatureReport(report 0x%llx, length %llu) -> wheel configuration",
   static_cast<unsigned long long>(b), static_cast<unsigned long long>(d));
  return PAD_OK;
 }
 if (LogCall(calls, 20)) {
  APS5_LOG_OUT("Pad: scePadGetFeatureReport(0x%llx, 0x%llx, 0x%llx, 0x%llx) -> not supported", static_cast<unsigned long long>(a),
   static_cast<unsigned long long>(b), static_cast<unsigned long long>(c), static_cast<unsigned long long>(d));
  if (b > 0x10000) LogBytes("b", reinterpret_cast<const void*>(b), 16);
  if (c > 0x10000) LogBytes("c", reinterpret_cast<const void*>(c), 16);
 }
 return PAD_ERROR_INVALID_ARG;
}

int APS5_VABI scePadSetFeatureReport(std::uint64_t a, std::uint64_t b, std::uint64_t c, std::uint64_t d) {
 static int calls = 0;
 if (LogCall(calls, 20)) {
  APS5_LOG_OUT("Pad: scePadSetFeatureReport(0x%llx, 0x%llx, 0x%llx, 0x%llx)", static_cast<unsigned long long>(a), static_cast<unsigned long long>(b),
   static_cast<unsigned long long>(c), static_cast<unsigned long long>(d));
  if (b > 0x10000) LogBytes("b", reinterpret_cast<const void*>(b), 32);
  if (c > 0x10000) LogBytes("c", reinterpret_cast<const void*>(c), 32);
 }
 return PAD_OK;
}

int APS5_VABI scePadReadState(int handle, PadData* data) {
 if (handle == PAD_WHEEL_HANDLE) return scePadReadStateExt(handle, data);
 const int slot = SlotOfHandle(handle);
 if (slot < 0) {
  // GT7 reads handles it has not opened (yet); the PS4 answers with an error, not a crash.
  static int calls = 0;
  if (LogCall(calls, 4)) APS5_LOG_OUT("Pad: scePadReadState(handle %d): not an open handle", handle);
  return PAD_ERROR_INVALID_HANDLE;
 }
 if (data == nullptr) return PAD_ERROR_INVALID_ARG;

 *data = Pad::ReadState(slot);

 return 0;
}

int APS5_VABI scePadResetLightBar(int handle) {
 const int slot = SlotOfHandle(handle);
 if (slot < 0) return PAD_ERROR_INVALID_HANDLE;
 Pad::SetLightBar(slot, false, 0, 0, 0);
 return PAD_OK;
}

int APS5_VABI scePadResetOrientation(int handle) {
 const int slot = SlotOfHandle(handle);
 if (slot < 0) return PAD_ERROR_INVALID_HANDLE;
 Pad::ResetOrientation(slot);
 return PAD_OK;
}

int APS5_VABI scePadSetAngularVelocityDeadbandState(int handle, bool enable) {
 (void)handle;
 (void)enable;
 NotImplemented_nid_no_patch(__func__);
 return 0;
}

int APS5_VABI scePadSetLightBar(int handle, const PadLightBarParam* param) {
 const int slot = SlotOfHandle(handle);
 if (slot < 0) return PAD_ERROR_INVALID_HANDLE;
 if (param == nullptr) return PAD_ERROR_INVALID_ARG;
 Pad::SetLightBar(slot, true, param->r, param->g, param->b);
 return PAD_OK;
}

int APS5_VABI scePadSetMotionSensorState(int handle, bool enable) {
 const int slot = SlotOfHandle(handle);
 if (slot < 0) return PAD_ERROR_INVALID_HANDLE;
 Pad::SetMotionEnabled(slot, enable);
 return PAD_OK;
}

int APS5_VABI scePadSetTiltCorrectionState(int handle, bool enabled) {
 (void)enabled;
 return SlotOfHandle(handle) < 0 ? PAD_ERROR_INVALID_HANDLE : PAD_OK;
}

int APS5_VABI scePadSetTriggerEffect(int handle, const void* param) {
 const int slot = SlotOfHandle(handle);
 if (slot < 0) return PAD_ERROR_INVALID_HANDLE;
 if (param == nullptr) return PAD_ERROR_INVALID_ARG;
 const std::uint8_t* bytes = static_cast<const std::uint8_t*>(param);
 const std::uint8_t mask = bytes[0];
 if ((mask & ~0x3u) != 0) {
  return PAD_ERROR_INVALID_ARG;
 }
 std::uint32_t mode[2] = {};
 for (int trigger = 0; trigger < 2; ++trigger) std::memcpy(&mode[trigger], bytes + 8 + 56 * trigger, 4);
 if (mode[0] > 6 || mode[1] > 6) {
  return PAD_ERROR_INVALID_ARG;
 }
 for (int trigger = 0; trigger < 2; ++trigger) {
  if ((mask & (1u << trigger)) != 0) Pad::SetTriggerCommand(slot, trigger, bytes + 8 + 56 * trigger);
 }
 return PAD_OK;
}

int APS5_VABI scePadSetVibration(int handle, const PadVibrationParam* param) {
 const int slot = SlotOfHandle(handle);
 if (slot < 0) return PAD_ERROR_INVALID_HANDLE;
 if (param == nullptr) return PAD_ERROR_INVALID_ARG;
 Pad::SetVibration(slot, param->large_motor, param->small_motor);
 return PAD_OK;
}

int APS5_VABI scePadSetVibrationMode(int handle, int mode) {
 const int slot = SlotOfHandle(handle);
 if (slot < 0) return PAD_ERROR_INVALID_HANDLE;
 if (mode != 0 && mode != 1) return PAD_ERROR_INVALID_ARG;
 Pad::SetVibrationMode(slot, mode);
 return PAD_OK;
}

int APS5_VABI scePadSetVibrationTriggerEffectWeakWhileEmbeddedMicInUse(bool enabled) {
 (void)enabled;
 return PAD_OK;
}

}

// Controller extensions unsupported by this runtime.
extern "C" {
int APS5_VABI scePadClose() { return PAD_OK; }
int APS5_VABI scePadConnectPort() { return PAD_OK; }
int APS5_VABI scePadDeviceOpen() { return PAD_OK; }
int APS5_VABI scePadDisableTriggerEffect() { return PAD_OK; }
int APS5_VABI scePadDisableVibration() { return PAD_OK; }
int APS5_VABI scePadDisconnectDevice() { return PAD_OK; }
int APS5_VABI scePadDisconnectPort() { return PAD_OK; }
int APS5_VABI scePadEnableAutoDetect() { return PAD_OK; }
int APS5_VABI scePadEnableExtensionPort() { return PAD_OK; }
int APS5_VABI scePadEnableSpecificDeviceClass() { return PAD_OK; }
int APS5_VABI scePadEnableUsbConnection() { return PAD_OK; }
int APS5_VABI scePadGetActiveTime() { return PAD_OK; }
int APS5_VABI scePadGetBluetoothAddress() { return PAD_OK; }
int APS5_VABI scePadGetCapability() { return PAD_OK; }
int APS5_VABI scePadGetDataInternal() { return PAD_OK; }
int APS5_VABI scePadGetDeviceId() { return PAD_OK; }
int APS5_VABI scePadGetDeviceInfo() { return PAD_OK; }
int APS5_VABI scePadGetExtensionUnitInfo() { return PAD_OK; }
int APS5_VABI scePadGetFeatureReport2() { return PAD_OK; }
int APS5_VABI scePadGetIdleCount() { return PAD_OK; }
int APS5_VABI scePadGetInfo() { return PAD_OK; }
int APS5_VABI scePadGetInfoByPortType() { return PAD_OK; }
int APS5_VABI scePadGetLicenseControllerInformation() { return PAD_OK; }
int APS5_VABI scePadGetMotionSensorPosition() { return PAD_OK; }
int APS5_VABI scePadGetMotionTimerUnit() { return PAD_OK; }
int APS5_VABI scePadGetSphereRadius() { return PAD_OK; }
int APS5_VABI scePadGetVersionInfo() { return PAD_OK; }
int APS5_VABI scePadInit() { return PAD_OK; }
int APS5_VABI scePadIsBlasterConnected() { return PAD_OK; }
int APS5_VABI scePadIsDS4Connected() { return PAD_OK; }
int APS5_VABI scePadIsLightBarBaseBrightnessControllable() { return PAD_OK; }
int APS5_VABI scePadIsMoveConnected() { return PAD_OK; }
int APS5_VABI scePadIsMoveReproductionModel() { return PAD_OK; }
int APS5_VABI scePadIsValidHandle() { return PAD_OK; }
int APS5_VABI scePadLastLotusButtonData() { return PAD_OK; }
int APS5_VABI scePadMbusInit() { return PAD_OK; }
int APS5_VABI scePadMbusTerm() { return PAD_OK; }
int APS5_VABI scePadOpen() { return PAD_OK; }
int APS5_VABI scePadOpenExt2() { return PAD_OK; }
int APS5_VABI scePadRead() { return PAD_OK; }
int APS5_VABI scePadReadBlasterForTracker() { return PAD_OK; }
int APS5_VABI scePadReadForTracker() { return PAD_OK; }
int APS5_VABI scePadReadHistory() { return PAD_OK; }
int APS5_VABI scePadRemoteControlInsertData() { return PAD_OK; }
int APS5_VABI scePadResetLightBarAll() { return PAD_OK; }
int APS5_VABI scePadResetLightBarAllByPortType() { return PAD_OK; }
int APS5_VABI scePadResetOrientationForTracker() { return PAD_OK; }
int APS5_VABI scePadSetAngularVelocityBiasCorrectionState() { return PAD_OK; }
int APS5_VABI scePadSetAutoPowerOffCount() { return PAD_OK; }
int APS5_VABI scePadSetButtonRemappingInfo() { return PAD_OK; }
int APS5_VABI scePadSetConnection() { return PAD_OK; }
int APS5_VABI scePadSetExtensionReport() { return PAD_OK; }
int APS5_VABI scePadSetFeatureReport2() { return PAD_OK; }
int APS5_VABI scePadSetForceIntercepted() { return PAD_OK; }
int APS5_VABI scePadSetLightBarBaseBrightness() { return PAD_OK; }
int APS5_VABI scePadSetLightBarBlinking() { return PAD_OK; }
int APS5_VABI scePadSetLightBarForTracker() { return PAD_OK; }
int APS5_VABI scePadSetLoginUserNumber() { return PAD_OK; }
int APS5_VABI scePadSetProcessFocus() { return PAD_OK; }
int APS5_VABI scePadSetProcessPrivilegeOfButtonRemapping() { return PAD_OK; }
int APS5_VABI scePadSetShareButtonMaskForRemotePlay() { return PAD_OK; }
int APS5_VABI scePadSetUserColor() { return PAD_OK; }
int APS5_VABI scePadSetUserNumber() { return PAD_OK; }
int APS5_VABI scePadSetVibrationForce() { return PAD_OK; }
int APS5_VABI scePadSetVrTrackingMode() { return PAD_OK; }
int APS5_VABI scePadShareOutputData() { return PAD_OK; }
int APS5_VABI scePadStartRecording() { return PAD_OK; }
int APS5_VABI scePadStopRecording() { return PAD_OK; }
int APS5_VABI scePadSwitchConnection() { return PAD_OK; }
int APS5_VABI scePadTrackerCalibrate() { return PAD_OK; }
int APS5_VABI scePadTrackerGetWorkingMemorySize() { return PAD_OK; }
int APS5_VABI scePadTrackerInit() { return PAD_OK; }
int APS5_VABI scePadTrackerReadState() { return PAD_OK; }
int APS5_VABI scePadTrackerTerm() { return PAD_OK; }
int APS5_VABI scePadTrackerUpdate() { return PAD_OK; }
int APS5_VABI scePadVertualDeviceAddDevice() { return PAD_OK; }
int APS5_VABI scePadVirtualDeviceAddDevice() { return PAD_OK; }
int APS5_VABI scePadVirtualDeviceDeleteDevice() { return PAD_OK; }
int APS5_VABI scePadVirtualDeviceDisableButtonRemapping() { return PAD_OK; }
int APS5_VABI scePadVirtualDeviceGetRemoteSetting() { return PAD_OK; }
int APS5_VABI scePadVirtualDeviceInsertData() { return PAD_OK; }
int APS5_VABI scePadVrControllerGetDeviceInformation() { return PAD_OK; }
int APS5_VABI scePadVrControllerGetTriggerEffectState() { return PAD_OK; }
int APS5_VABI scePadVrControllerGetTriggerEffectStates() { return PAD_OK; }
int APS5_VABI scePadVrControllerRead() { return PAD_OK; }
int APS5_VABI scePadVrControllerReadCombinedState() { return PAD_OK; }
int APS5_VABI scePadVrControllerReadState() { return PAD_OK; }
int APS5_VABI scePadVrControllerSetTriggerEffect() { return PAD_OK; }
int APS5_VABI scePadVrControllerSetTriggerEffects() { return PAD_OK; }
int APS5_VABI scePadVrControllerSetVibration() { return PAD_OK; }
int APS5_VABI scePadVrControllerSetVibrationMode() { return PAD_OK; }
}

