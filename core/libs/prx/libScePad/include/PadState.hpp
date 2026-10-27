#ifndef CORE_LIBS_PRX_LIBSCEPAD_PADSTATE_HPP
#define CORE_LIBS_PRX_LIBSCEPAD_PADSTATE_HPP

#include <array>
#include <cstddef>
#include <cstdint>
#include <exception>
#include "SceTypes.hpp"

struct PadTouchPoint {
    bool active = false;
    std::uint16_t x = 0;
    std::uint16_t y = 0;
    std::uint8_t id = 0;

    bool operator==(const PadTouchPoint&) const = default;
};

struct PadInputState {
    std::uint32_t buttons = 0;
    std::array<std::uint8_t, 4> sticks{128, 128, 128, 128};
    std::uint8_t analogButtonsL2 = 0;
    std::uint8_t analogButtonsR2 = 0;
    bool touchLeft = false;
    bool touchRight = false;
    bool hasMotion = false;
    std::array<float, 3> accel{0.0f, 9.80665f, 0.0f};
    std::array<float, 3> gyro{0.0f, 0.0f, 0.0f};
    std::array<PadTouchPoint, 2> touch{};
    std::uint8_t deviceKind = 0;
};

struct PadTriggerRequest {
    bool valid = false;
    std::uint8_t effect[11]{};
    std::uint8_t fallback = 0;
    std::uint32_t mode = 0;
};

struct PadOutputState {
    std::uint32_t sequence = 0;
    std::uint8_t vibrationLarge = 0;
    std::uint8_t vibrationSmall = 0;
    bool lightBarValid = false;
    std::uint8_t lightBar[3]{};
    PadTriggerRequest trigger[2];
    bool triggerTouched = false;
    bool motionEnabled = true;
    int vibrationMode = 0;
};

constexpr int PAD_MAX_PLAYERS = 4;
// Players the game sees (GT Sport's Split Screen is for 2). The input bridge still feeds all
// PAD_MAX_PLAYERS slots; players 3 and 4 are never connected.
constexpr int PAD_ACTIVE_PLAYERS = 2;

// Per player: slot 0 is player 1 (the initial user), slots 1-3 players 2-4.
namespace Pad {
void Initialize();
// GT7 ultrawide: widens the race camera to GPU.aspect_ratio (WideCamera.cpp).
void InstallWideCamera();
// Hybrid CPUs: pins the busy threads to the P-cores, without power throttling (CoreBoost.cpp).
void InstallCoreBoost();
PadData ReadState(int slot);
bool Connected(int slot);
void SetVibration(int slot, std::uint8_t large, std::uint8_t small);
void SetVibrationMode(int slot, int mode);
void SetLightBar(int slot, bool valid, std::uint8_t r, std::uint8_t g, std::uint8_t b);
void SetTriggerCommand(int slot, int trigger, const std::uint8_t* command);
void ResetOrientation(int slot);
void SetMotionEnabled(int slot, bool enabled);
}

struct WheelState;

namespace Pad {
// Racing wheels and other joysticks, read on their own thread (WheelBridge.cpp).
void StartWheel();
void ApplyWheel(PadInputState& state);
WheelState ReadWheel();
// While the title has the wheel open as a steering wheel, its L1/R1 (the G29's shift paddles)
// belong to the wheel alone; on the ordinary pad they would also move the camera.
void SetWheelOpened(bool opened);
// A force-feedback output report the title sent to the wheel (Logitech classic commands).
void WheelReport(const std::uint8_t* report, std::size_t length);
// A Fanatec wheel's configuration (feature report 0x31), as the title's Fanatec SDK reads it.
// Returns false when the wheel is not presented as a Fanatec wheel.
bool WheelFeatureReport(int reportId, std::uint8_t* report, std::size_t length);
// Waits up to the given time for the wheel thread's first sample; false if it is not ready.
bool WaitForWheel(int milliseconds);

// ryty-wheel.ini beside the game, read once.
struct WheelSettings {
    int rotation = 900;          // degrees lock to lock, as set in the wheel's software
    unsigned pedalMax = 65535;   // full-press value reported to the game
    bool angleRadians = false;   // steering angle in radians instead of degrees
    // The wheel model the game is told it has: a Fanatec direct-drive base by default (16-bit
    // forces), or a Logitech G29 (046d:c24f).
    unsigned vendor = 0x0eb7;
    unsigned product = 0x0e05;
    bool IsFanatec() const { return vendor == 0x0eb7; }
    int capability = 0;          // steering-wheel capability bits reported to the game
    int ffbGain = 100;           // force feedback strength in percent
    bool ffbInvert = false;      // reverse the direction of the title's constant forces
    // Damping always applied on top of the title's, 0-100 (stops a strong wheel oscillating).
    int ffbDamping = 10;
    // Extra strength for quick changes in the title's force (kerbs, bumps), 0-200 percent.
    int ffbDetail = 0;
    // The player's own pad reports itself as the steering wheel too, as a G29 does on a PS4;
    // the title only turns on its wheel mode (and force feedback) for such a pad. Off by
    // default: in that mode the paddles do not shift yet.
    bool padIsWheel = false;
};
const WheelSettings& Wheel();

// True when [address, address + size) can be read, for logging what the game passes.
bool Readable(const void* address, std::size_t size);
}

extern "C" void PadPublishInput_nid_postfix(const PadInputState& input);
extern "C" void PadReportInputFailure_nid_postfix(std::exception_ptr error);
extern "C" bool PadFetchOutput_nid_postfix(std::uint32_t* seenSequence, PadOutputState* out);
extern "C" void PadPublishInputSlot_nid_postfix(int slot, const PadInputState& input);
extern "C" bool PadFetchOutputSlot_nid_postfix(int slot, std::uint32_t* seenSequence, PadOutputState* out);
extern "C" void PadSetConnectedSlots_nid_postfix(std::uint32_t mask);

#endif
