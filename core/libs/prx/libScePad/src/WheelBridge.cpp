#include "prx/libScePad/include/PadState.hpp"

#include <atomic>
#include <cctype>
#include <chrono>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <exception>
#include <fstream>
#include <memory>
#include <string>
#include <thread>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include "SDL.h"
#include "prx/libScePad/include/InputMapping.hpp"
#include "prx/libScePad/include/WheelInput.hpp"
#include "prx/libc/include/General.hpp"

namespace {
std::atomic<WheelInput*> wheel{nullptr};
std::atomic<bool> started{false};
std::atomic<bool> ready{false};
// When the wheel thread started; titles ask for a wheel during the first seconds after that.
std::atomic<std::int64_t> startedAt{0};
std::atomic<bool> wheelOpened{false};

// The G29's four effect slots, as set by the title's Logitech classic force-feedback commands
// (byte 0: slot mask << 4 | operation; byte 1: effect type; bytes 2-7: parameters).
struct ForceSlot {
    bool playing = false;
    std::uint8_t type = 0;
    std::uint8_t parameters[6]{};
};
std::mutex forceMutex;
ForceSlot forceSlots[4];
bool autocenter = true;
int commandsLogged = 0;

// A Fanatec wheel's effect slots, as set by the title's Fanatec SDK. Each report holds one
// 7-byte command after the report id: byte 0 is slot << 4 | 0x01 to play (0x03 to stop),
// byte 1 the effect type (0x08 constant, 0x0b spring, 0x0c damper / inertia / friction).
struct FanatecSlot {
    bool playing = false;
    std::uint8_t command[7]{};
};
FanatecSlot fanatecSlots[16];
bool fanatecDriven = false;  // the title has sent effects (the startup spring is off)

WheelForces DecodeFanatec(int fallbackSpring) {
    WheelForces result;
    result.active = true;
    bool anySpring = false;
    for (int index = 0; index < 16; ++index) {
        const FanatecSlot& slot = fanatecSlots[index];
        if (!slot.playing) continue;
        const std::uint8_t* c = slot.command;
        switch (c[1]) {
            case 0x08:  // constant: unsigned around 0x80, or (byte 6 = 1) 16 bits around 0x8000
                result.constant += c[6] == 1 ? (c[2] | c[3] << 8) - 0x8000 : (static_cast<int>(c[2]) - 0x80) * 256;
                break;
            case 0x0b: {  // spring: deadband edges (0x80 = centre), stiffness nibbles (0-15), saturation
                const int k = std::max(c[4] >> 4, c[4] & 0x0f);
                result.spring = std::max(result.spring, k * 32767 / 15);
                result.springCenter = ((static_cast<int>(c[2]) + static_cast<int>(c[3])) / 2 - 0x80) * 256;
                result.springLimit = c[6] != 0 ? c[6] * 0xFFFF / 0xFF : 0xFFFF;
                anySpring = true;
                break;
            }
            case 0x0c: {  // damper-like: coefficients (0-15) at bytes 2 and 4, signs at 3 and 5
                const int k = std::max(c[2] & 0x0f, c[4] & 0x0f) * 32767 / 15;
                if (index == 4) result.friction = std::max(result.friction, k);
                else if (index != 3) result.damper = std::max(result.damper, k);  // slot 3 is inertia
                break;
            }
            default: break;
        }
    }
    if (!anySpring && !fanatecDriven) result.spring = fallbackSpring;
    result.constant = std::clamp(result.constant, -32767, 32767);
    return result;
}

void LogFanatec(const std::uint8_t* report, const char* note) {
    if (commandsLogged++ >= 300) return;
    APS5_LOG_OUT("Pad: fanatec ffb [%02x] %02x %02x %02x %02x %02x %02x %02x%s", report[0], report[1], report[2], report[3], report[4],
        report[5], report[6], report[7], note);
}

int FallbackSpring() {
    const char* text = std::getenv("RTY_WHEEL_SPRING");
    return 0x7FFF * std::clamp(std::atoi(text != nullptr ? text : "30"), 0, 100) / 100;
}

void FanatecReport(const std::uint8_t* report, std::size_t length) {
    std::uint8_t r[8]{};
    std::memcpy(r, report, std::min<std::size_t>(length, 8));
    const std::uint8_t* c = r + 1;
    const std::uint8_t first = c[0];
    if ((first & 0x0f) == 0x01 || (first & 0x0f) == 0x03) {
        FanatecSlot& slot = fanatecSlots[first >> 4];
        const bool stop = (first & 0x02) != 0;
        // Log a slot's command when it starts, stops or changes type (not every force update).
        const bool news = slot.playing == stop || slot.command[1] != c[1];
        slot.playing = !stop;
        std::memcpy(slot.command, c, 7);
        fanatecDriven = true;
        if (news) LogFanatec(r, stop ? " (stop)" : "");
    } else {
        LogFanatec(r, first == 0xf8 ? " (extended)" : " (not handled)");
    }
    static int updates = 0;
    if (++updates % 2000 == 0) {
        const WheelForces f = DecodeFanatec(0);
        APS5_LOG_OUT("Pad: fanatec ffb: %d reports, constant %d spring %d damper %d friction %d", updates, f.constant, f.spring, f.damper,
            f.friction);
    }
    if (WheelInput* input = wheel.load()) {
        const bool playForce = length >= 8 && (first & 0x0f) == 0x01 &&
                               (c[1] == 0x08 || c[1] == 0x0b || c[1] == 0x0c);
        input->SetForces(DecodeFanatec(FallbackSpring()), playForce);
    }
}

void LogCommand(const std::uint8_t* command, const char* note) {
    if (commandsLogged++ >= 200) return;
    APS5_LOG_OUT("Pad: wheel ffb %02x %02x %02x %02x %02x %02x %02x %02x%s", command[0], command[1], command[2], command[3], command[4],
        command[5], command[6], command[7], note);
}

WheelForces DecodeForces(int fallbackSpring) {
    WheelForces result;
    result.active = true;
    bool anySpring = false;
    for (int index = 0; index < 4; ++index) {
        const ForceSlot& slot = forceSlots[index];
        if (!slot.playing) continue;
        const std::uint8_t* p = slot.parameters;
        const auto level = [](std::uint8_t value) { return (static_cast<int>(value) - 0x80) * 256; };
        switch (slot.type) {
            case 0x00: result.constant += level(p[index]); break;           // constant: one level per slot
            case 0x08: result.constant += level(p[0]); break;               // variable force
            case 0x01:                                                      // spring (K 0-7)
            case 0x0b: {                                                    // high-resolution spring (K 0-15)
                const int maximum = slot.type == 0x01 ? 7 : 15;
                const int k = std::max(p[2] & 0x0f, p[2] >> 4);
                result.spring = std::max(result.spring, std::min(k, maximum) * 32767 / maximum);
                result.springCenter = ((static_cast<int>(p[0]) + static_cast<int>(p[1])) / 2 - 0x80) * 256;
                // Byte 6 of a spring command caps its force (0x60 is about 38%).
                result.springLimit = p[4] != 0 ? p[4] * 0xFFFF / 0xFF : 0xFFFF;
                anySpring = true;
                break;
            }
            case 0x02:                                                      // damper (K 0-7)
            case 0x0c: {                                                    // high-resolution damper (K 0-15)
                const int maximum = slot.type == 0x02 ? 7 : 15;
                const int k = std::max(p[0] & 0x0f, p[2] & 0x0f);
                result.damper = std::max(result.damper, std::min(k, maximum) * 32767 / maximum);
                break;
            }
            case 0x0e:                                                      // friction (K 0-255)
                result.friction = std::max(result.friction, std::max<int>(p[0], p[1]) * 32767 / 255);
                break;
            default: break;
        }
    }
    if (!anySpring && autocenter) result.spring = fallbackSpring;
    result.constant = std::clamp(result.constant, -32767, 32767);
    return result;
}

// Wheels are read here rather than by the window's controller code, because the window may come
// from another renderer (shadGT) that only reads gamepads. SDL's joystick API stays on this thread.
void wheelThread() {
    // Gamepads belong to the window's controller code: keep SDL away from their HID handles.
    SDL_SetHint(SDL_HINT_JOYSTICK_HIDAPI, "0");
    SDL_SetHint(SDL_HINT_JOYSTICK_RAWINPUT, "0");
    Pad::InputConfig config;
    try {
        config = Pad::LoadInputConfig();
    } catch (const std::exception& error) {
        APS5_LOG_ERR("Pad: wheel settings ignored: %s", error.what());
    }
    static WheelInput input(config);
    wheel.store(&input);
    for (;;) {
        SDL_PumpEvents();
        SDL_Event event;
        while (SDL_PeepEvents(&event, 1, SDL_GETEVENT, SDL_JOYDEVICEADDED, SDL_JOYDEVICEREMOVED) > 0) input.HandleEvent(event);
        SDL_FlushEvents(SDL_FIRSTEVENT, SDL_LASTEVENT);
        input.Update();
        ready.store(true);
        std::this_thread::sleep_for(std::chrono::milliseconds(4));
    }
}
}

namespace Pad {

void StartWheel() {
    if (started.exchange(true)) return;
    startedAt.store(std::chrono::steady_clock::now().time_since_epoch().count());
    std::thread(wheelThread).detach();
}

void ApplyWheel(PadInputState& state) {
    const WheelInput* input = wheel.load();
    if (input == nullptr) return;
    PadInputState fromWheel;
    fromWheel.buttons = 0;
    input->Apply(fromWheel);
    if (wheelOpened.load() && !Wheel().padIsWheel) {
        // The window's controller code may have the wheel open as a gamepad too. (With the pad
        // reporting itself as the wheel, the title is in wheel mode and shifts with L1/R1.)
        fromWheel.buttons &= ~static_cast<std::uint32_t>(0x0C00);
        state.buttons &= ~static_cast<std::uint32_t>(0x0C00);
    }
    state.buttons |= fromWheel.buttons;
    for (std::size_t axis = 0; axis < state.sticks.size(); ++axis) {
        if (fromWheel.sticks[axis] != 128) state.sticks[axis] = fromWheel.sticks[axis];
    }
    state.touchLeft = state.touchLeft || fromWheel.touchLeft;
    state.touchRight = state.touchRight || fromWheel.touchRight;
}

void SetWheelOpened(bool opened) {
    wheelOpened.store(opened);
}

void WheelReport(const std::uint8_t* report, std::size_t length) {
    if (report == nullptr || length < 2) return;
    std::lock_guard lock(forceMutex);
    if (Wheel().IsFanatec()) {
        FanatecReport(report, length);
        return;
    }
    bool drivingCommand = false;
    // Byte 0 is the report id; the rest holds 8-byte commands.
    for (std::size_t offset = 1; offset + 8 <= length; offset += 8) {
        const std::uint8_t* c = report + offset;
        bool empty = true;
        for (int i = 0; i < 8; ++i) empty = empty && c[i] == 0;
        if (empty) continue;
        const std::uint8_t first = c[0];
        if (first == 0xf8) {
            LogCommand(c, c[1] == 0x81 ? " (wheel range)" : c[1] == 0x12 ? " (leds)" : " (extended)");
            continue;
        }
        if (first == 0xf5) { autocenter = false; LogCommand(c, " (autocenter off)"); continue; }
        if (first == 0x14) { autocenter = true; LogCommand(c, " (autocenter on)"); continue; }
        if (first >= 0xf0 || first == 0xfe) { LogCommand(c, " (not handled)"); continue; }
        const int mask = first >> 4;
        const int operation = first & 0x0f;
        if (mask == 0 || (operation != 0x0 && operation != 0x1 && operation != 0x2 && operation != 0x3 && operation != 0xc)) {
            LogCommand(c, " (not handled)");
            continue;
        }
        for (int index = 0; index < 4; ++index) {
            if ((mask & (1 << index)) == 0) continue;
            ForceSlot& slot = forceSlots[index];
            if (operation == 0x0 || operation == 0x1 || operation == 0xc) {
                slot.type = c[1];
                std::memcpy(slot.parameters, c + 2, 6);
            }
            if (operation == 0x1 || operation == 0x2) slot.playing = true;
            if (operation == 0x3) slot.playing = false;
            drivingCommand |= slot.playing;
        }
        LogCommand(c, "");
    }
    if (WheelInput* input = wheel.load()) {
        input->SetForces(DecodeForces(FallbackSpring()), drivingCommand);
    }
}

bool WheelFeatureReport(int reportId, std::uint8_t* report, std::size_t length) {
    if (!Wheel().IsFanatec() || report == nullptr) return false;
    if (reportId == 0x33) {
        // The rim's extra inputs (analog paddles, dials, encoders), read while driving: all at
        // rest. Without an answer the SDK's input update fails and the car cannot be driven.
        std::uint8_t extra[64]{};
        extra[0] = 0x33;
        std::memcpy(report, extra, std::min<std::size_t>(length, sizeof(extra)));
        return true;
    }
    if (reportId != 0x31) return false;
    // What the SDK reads: byte 3 the rim (1-18), byte 6 the pedals on the base (1 ClubSport,
    // 2 CSL; without pedals the title ignores the pedal axes), bytes 7-8 the base's firmware
    // version (big endian; 16-bit constant forces need 674 or later), the rest left at zero.
    std::uint8_t config[64]{};
    config[0] = 0x31;
    config[2] = 0x01;
    config[3] = 0x01;
    config[6] = 0x01;  // ClubSport pedals (with a clutch)
    config[7] = 0x02;
    config[8] = 0xb0;  // firmware 688
    std::memcpy(report, config, std::min<std::size_t>(length, sizeof(config)));
    return true;
}

WheelState ReadWheel() {
    if (const WheelInput* input = wheel.load()) return input->State();
    return {};
}

bool WaitForWheel(int milliseconds) {
    // Windows reports a wheel a moment after the joystick subsystem starts, so the first sample
    // can come before it. Wait for a wheel itself, but only until the given time after startup,
    // so later requests (or a game with no wheel attached) are not held up.
    StartWheel();
    const auto start = std::chrono::steady_clock::time_point(std::chrono::steady_clock::duration(startedAt.load()));
    const auto deadline = start + std::chrono::milliseconds(milliseconds);
    while (!(ready.load() && ReadWheel().connected) && std::chrono::steady_clock::now() < deadline) {
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }
    return ready.load() && ReadWheel().connected;
}

const WheelSettings& Wheel() {
    static const WheelSettings settings = [] {
        WheelSettings result;
        std::unique_ptr<char, decltype(&SDL_free)> base(SDL_GetBasePath(), SDL_free);
        const std::string path = std::string(base ? base.get() : "") + "ryty-wheel.ini";
        std::ifstream file(path);
        std::string line;
        while (std::getline(file, line)) {
            line = line.substr(0, line.find_first_of("#;"));
            const auto equals = line.find('=');
            if (equals == std::string::npos) continue;
            auto trim = [](std::string text) {
                while (!text.empty() && std::isspace(static_cast<unsigned char>(text.front()))) text.erase(0, 1);
                while (!text.empty() && std::isspace(static_cast<unsigned char>(text.back()))) text.pop_back();
                return text;
            };
            std::string key = trim(line.substr(0, equals));
            const std::string value = trim(line.substr(equals + 1));
            for (auto& c : key) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
            if (key == "rotation") result.rotation = std::max(90, std::atoi(value.c_str()));
            else if (key == "pedalmax") result.pedalMax = static_cast<unsigned>(std::max(1L, std::strtol(value.c_str(), nullptr, 0)));
            else if (key == "angleunit") result.angleRadians = value == "rad" || value == "radians";
            else if (key == "ffbgain") result.ffbGain = std::atoi(value.c_str());
            else if (key == "padiswheel") result.padIsWheel = std::atoi(value.c_str()) != 0;
            else if (key == "ffbinvert") result.ffbInvert = std::atoi(value.c_str()) != 0;
            else if (key == "ffbdamping") result.ffbDamping = std::clamp(std::atoi(value.c_str()), 0, 100);
            else if (key == "ffbdetail") result.ffbDetail = std::clamp(std::atoi(value.c_str()), 0, 200);
            else if (key == "capability") result.capability = static_cast<int>(std::strtol(value.c_str(), nullptr, 0));
            else if (key == "present") {
                const auto colon = value.find(':');
                if (colon != std::string::npos) {
                    result.vendor = static_cast<unsigned>(std::strtoul(value.substr(0, colon).c_str(), nullptr, 16));
                    result.product = static_cast<unsigned>(std::strtoul(value.substr(colon + 1).c_str(), nullptr, 16));
                }
            } else APS5_LOG_OUT("Pad: unknown setting '%s' in %s", key.c_str(), path.c_str());
        }
        APS5_LOG_OUT("Pad: force feedback: gain %d%%, damping %d%%, detail %d%%%s", result.ffbGain, result.ffbDamping, result.ffbDetail,
            result.ffbInvert ? ", reversed" : "");
        APS5_LOG_OUT("Pad: wheel settings: rotation %d, pedal max %u, angle in %s, present as %04x:%04x, capability 0x%x",
            result.rotation, result.pedalMax, result.angleRadians ? "radians" : "degrees", result.vendor, result.product, result.capability);
        return result;
    }();
    return settings;
}

bool Readable(const void* address, std::size_t size) {
    if (address == nullptr || size == 0) return false;
    MEMORY_BASIC_INFORMATION info;
    auto cursor = static_cast<const char*>(address);
    const auto end = cursor + size;
    while (cursor < end) {
        if (VirtualQuery(cursor, &info, sizeof(info)) == 0 || info.State != MEM_COMMIT) return false;
        if ((info.Protect & (PAGE_READONLY | PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE)) == 0) return false;
        if ((info.Protect & PAGE_GUARD) != 0) return false;
        cursor = static_cast<const char*>(info.BaseAddress) + info.RegionSize;
    }
    return true;
}

}
