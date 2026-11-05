#ifndef CORE_LIBS_PRX_LIBSCEPAD_WHEELINPUT_HPP
#define CORE_LIBS_PRX_LIBSCEPAD_WHEELINPUT_HPP

#include "SDL_events.h"
#include "SDL_haptic.h"
#include "SDL_joystick.h"
#include "prx/libScePad/include/PadInputTypes.hpp"
#include "prx/libScePad/include/PadState.hpp"
#include <array>
#include <chrono>
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

// What a wheel reports to the game's steering-wheel device class.
struct WheelState {
    bool connected = false;
    // -32768 (full left) to 32767 (full right) of the wheel's configured rotation.
    int steering = 0;
    // 0 (released) to 65535 (fully pressed).
    std::uint16_t throttle = 0;
    std::uint16_t brake = 0;
    std::uint16_t clutch = 0;
    std::uint32_t buttons = 0;
};

// Forces the title asks the wheel to play, decoded from its force-feedback reports.
struct WheelForces {
    bool active = false;    // the title drives force feedback (the startup spring is off)
    int constant = 0;       // -32767 .. 32767
    int spring = 0;         // 0 .. 32767
    int springCenter = 0;   // -32767 .. 32767
    int springLimit = 65535;  // most force the spring may apply, 0 .. 65535
    int damper = 0;         // 0 .. 32767
    int friction = 0;       // 0 .. 32767

    bool operator==(const WheelForces&) const = default;
};

// Racing wheels, pedal sets and other joysticks that SDL does not map as game controllers
// (Logitech, Moza, Fanatec, ...). Their axes are reported as a native steering wheel (State);
// their mapped buttons also reach the ordinary pad (Apply) so menus work. Update runs on one
// thread; Apply and State may be called from any other.
class WheelInput {
public:
    explicit WheelInput(const Pad::InputConfig& config);
    ~WheelInput();
    WheelInput(const WheelInput&) = delete;
    WheelInput& operator=(const WheelInput&) = delete;

    // True for a device index that should be read as a wheel even if SDL has a game-controller
    // mapping for it.
    static bool IsWheel(int deviceIndex);

    void HandleEvent(const SDL_Event& event);
    // Samples every open device; returns true when the merged state changed.
    bool Update();
    void Apply(PadInputState& state) const;
    WheelState State() const;
    // Called from any thread; the forces are played on the wheel by the next Update.
    void SetForces(const WheelForces& forces);
    void SetForces(const WheelForces& forces, bool drivingCommand);

private:
    struct Device {
        SDL_Joystick* joystick = nullptr;
        SDL_JoystickID instanceId = -1;
        std::string name;
        std::string lowerName;
        std::uint16_t vendor = 0;
        std::uint16_t product = 0;
        bool wheel = false;
        std::vector<std::int16_t> rest;
        bool restCaptured = false;
        std::chrono::steady_clock::time_point restAt{};
        std::vector<bool> axisLogged;
        std::vector<bool> buttonLogged;
        bool hatLogged = false;
        SDL_Haptic* haptic = nullptr;
        unsigned int hapticFeatures = 0;
        bool conditionNeedsDirection = false;
        int springEffect = -1;
        // Effects played for the title's force feedback.
        int constantEffect = -1;
        int gameSpringEffect = -1;
        int damperEffect = -1;
        int frictionEffect = -1;
        WheelForces played;
        float slowConstant = 0.0f;  // the title's constant force, smoothed (for the detail boost)
        // Damping: the steering a moment ago, its speed (units per second, smoothed) and how the
        // title's centering force relates to the steering (its sign gives the damping direction).
        int lastSteering = 0;
        std::chrono::steady_clock::time_point lastSteeringAt{};
        float steeringSpeed = 0.0f;
        float centering = 0.0f;
    };
    struct ActiveBinding {
        Pad::JoystickBinding binding;
        Device* device = nullptr;
    };
    struct Sample {
        std::uint32_t buttons = 0;
        bool hasSteering = false;
        int steering = 0;
        std::uint16_t throttle = 0;
        std::uint16_t brake = 0;
        std::uint16_t clutch = 0;
        std::array<bool, 4> negative{};
        std::array<bool, 4> positive{};
        bool touchLeft = false;
        bool touchRight = false;

        bool operator==(const Sample&) const = default;
    };

    void open(int deviceIndex);
    void close(SDL_JoystickID instanceId);
    void startSpring(Device& device);
    void playForces(Device& device, const WheelForces& forces, int steering);
    void rebuild();
    void addDefaults(Device& primary, std::vector<ActiveBinding>& out) const;
    bool userOverrides(const Pad::JoystickBinding& binding) const;
    void logActivity(Device& device);
    Device* primary() const;

    std::vector<Pad::JoystickBinding> userBindings;
    std::vector<Pad::InputControl> userControls;
    std::vector<Pad::PadButton> userButtons;
    std::vector<std::unique_ptr<Device>> devices;
    std::vector<ActiveBinding> active;
    Sample sample{};
    mutable std::mutex sampleMutex;
    std::mutex forcesMutex;
    WheelForces forces{};
    std::chrono::steady_clock::time_point lastDriveCommand{}, nextCheck{};
    int springStrength = 30;
};

#endif
