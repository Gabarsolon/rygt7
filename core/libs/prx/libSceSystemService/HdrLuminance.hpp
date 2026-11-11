// FIX-GT7-141: what sceSystemServiceGetHdrToneMapLuminance tells the game about the display.
//
// GT7 asks once, at start (its only call, eboot 0x2792330), and tone maps its scene to the answer:
// the display's peak, full-frame and minimum luminance in nits, on a console the result of the
// system's "Adjust HDR" screens. The answer was 100 / 100 / 0 whatever the output, so with HDR
// output on the scene stayed in the SDR range (measured in the game's picture: nothing above 100
// nits, sky and lit signs one flat value) under an interface drawn at 250 nits. When the call
// fails the game uses 1000 / 600 / 0.1 itself.
//
// With HDR output off (GPU.hdr_allowed in user\custom_configs\CUSA03220.json, the renderer's
// setting), or when Windows says no display runs in HDR, the answer is unchanged. Otherwise
// GPU.hdr_display_peak_nits decides:
//   - a peak, "<peak>[,<full frame>[,<min>]]" in nits: that display;
//   - absent: 1000 / 600, the game's own choice and the usual default of PC games. A monitor's own
//     data is not the default: it is not always there, and not always true;
//   - 0: the display Windows runs in HDR, as Windows describes it (its HDR calibration, or the
//     monitor's data), the primary display first; 1000 / 600 when Windows cannot say.
// RYGT_HDR_DISPLAY_NITS overrides all of it: the same text, or 0 for the answer of before.
//
// What this cannot see is the renderer's own decision: if it shows the game in SDR on a display
// Windows runs in HDR (no HDR10 surface from the driver, or a renderer without GT7's HDR
// support), the game is told an HDR display all the same. Not tried.
//
// The minimum stays 0 unless it is given: only 0 was tried in play.
#pragma once

#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <optional>
#include <string>

#ifdef _WIN32
#include <windows.h>
#include <dxgi1_6.h>
#endif

namespace HdrLuminance {
// Internal linkage: the library exports every external symbol it holds, and these are not for
// the game.
namespace {

struct Luminance {
    float peak;
    float full_frame;
    float min;
};

/// The answer of before, and of a game shown in SDR.
inline constexpr Luminance Sdr{100.0f, 100.0f, 0.0f};
/// An HDR display nothing is known of: the peak and full frame GT7 takes when the call fails (the
/// constants its caller blends in, with a minimum of 0.1).
inline constexpr Luminance Unset{1000.0f, 600.0f, 0.0f};

inline constexpr float LowestPeak = 100.0f;
inline constexpr float HighestPeak = 10000.0f;
inline constexpr float HighestMin = 5.0f;

/// A display known by its peak only: the full frame in the ratio of the game's fallback.
inline Luminance FromPeak(float peak) {
    peak = peak > HighestPeak ? HighestPeak : peak;
    const float full_frame = peak * 0.6f;
    return {peak, full_frame < LowestPeak ? LowestPeak : full_frame, 0.0f};
}

/// "<peak>[,<full frame>[,<min>]]" in nits. Nothing when there is no peak of 100 nits or more
/// ("0", "", text): the caller goes on to the next source.
inline std::optional<Luminance> Parse(const std::string& text) {
    const char* at = text.c_str();
    char* end = nullptr;
    const float peak = std::strtof(at, &end);
    if (end == at || !std::isfinite(peak) || peak < LowestPeak) return std::nullopt;
    Luminance result = FromPeak(peak);
    if (*end != ',') return result;
    at = end + 1;
    const float full_frame = std::strtof(at, &end);
    if (end != at && std::isfinite(full_frame) && full_frame >= LowestPeak) {
        result.full_frame = full_frame > result.peak ? result.peak : full_frame;
    }
    if (*end != ',') return result;
    at = end + 1;
    const float min = std::strtof(at, &end);
    if (end != at && std::isfinite(min) && min >= 0.0f) {
        result.min = min > HighestMin ? HighestMin : min;
    }
    return result;
}

/// What a display says of itself, made usable. Nothing when it names no peak of 100 nits or more
/// (some monitors report 0).
inline std::optional<Luminance> FromDisplay(float peak, float full_frame) {
    if (!std::isfinite(peak) || peak < LowestPeak) return std::nullopt;
    Luminance result = FromPeak(peak);
    if (std::isfinite(full_frame) && full_frame >= LowestPeak) {
        result.full_frame = full_frame > result.peak ? result.peak : full_frame;
    }
    return result;
}

/// The raw value after "key": in the launcher's settings (a small flat file whose keys are
/// unique), without its quotes, or "".
inline std::string ConfigValue(const std::string& config, const char* key) {
    const std::string quoted = std::string("\"") + key + "\"";
    const std::size_t at = config.find(quoted);
    if (at == std::string::npos) return {};
    const std::size_t begin = config.find_first_not_of(" \t\r\n:", at + quoted.size());
    if (begin == std::string::npos) return {};
    if (config[begin] == '"') {
        const std::size_t end = config.find('"', begin + 1);
        return end == std::string::npos ? std::string() : config.substr(begin + 1, end - begin - 1);
    }
    const std::size_t end = config.find_first_of(",}\r\n", begin);
    std::string value = config.substr(begin, end == std::string::npos ? std::string::npos : end - begin);
    while (!value.empty() && (value.back() == ' ' || value.back() == '\t')) value.pop_back();
    return value;
}

enum class DisplayState {
    Unknown,   // Windows could not be asked (or this is not Windows)
    NoHdr,     // Windows runs no display in HDR
    Hdr,       // a display in HDR, with usable numbers
    HdrSilent, // a display in HDR that names no peak
};

struct Display {
    DisplayState state = DisplayState::Unknown;
    Luminance luminance = Unset;
};

/// The setting asks for the display's own data: a number that is 0.
inline bool AsksDisplay(const std::string& text) {
    char* end = nullptr;
    const float value = std::strtof(text.c_str(), &end);
    return end != text.c_str() && value == 0.0f;
}

struct Answer {
    Luminance luminance;
    const char* source;
};

/// The answer for the game, from the override (RYGT_HDR_DISPLAY_NITS, null when unset), the
/// launcher's settings and the display, asked only when HDR output is allowed.
template <typename AskDisplay>
Answer Decide(const char* override_text, const std::string& config, AskDisplay&& ask_display) {
    if (override_text != nullptr && *override_text != '\0') {
        if (const auto given = Parse(override_text)) return {*given, "RYGT_HDR_DISPLAY_NITS"};
        return {Sdr, "RYGT_HDR_DISPLAY_NITS, the answer of before"};
    }
    if (ConfigValue(config, "hdr_allowed") != "true") return {Sdr, "HDR output is off"};
    // HDR output allowed in the launcher with Windows' HDR off: the game is shown in SDR.
    const Display display = ask_display();
    if (display.state == DisplayState::NoHdr) return {Sdr, "no display runs in HDR"};
    const std::string setting = ConfigValue(config, "hdr_display_peak_nits");
    if (const auto set = Parse(setting)) return {*set, "the launcher's display peak"};
    if (!AsksDisplay(setting)) return {Unset, "no display peak is set"};
    switch (display.state) {
    case DisplayState::Hdr:
        return {display.luminance, "the HDR display, as Windows describes it"};
    case DisplayState::NoHdr:
    case DisplayState::HdrSilent:
        return {Unset, "the HDR display names no peak"};
    case DisplayState::Unknown:
        break;
    }
    return {Unset, "Windows could not describe the display"};
}

#ifdef _WIN32
/// The display Windows runs in HDR (the primary one first, else the first found), through DXGI,
/// loaded here only: nothing else in this library needs it. Windows' own dxgi.dll, never one
/// placed beside the game (a wrapper would be started just to be asked this).
inline Display AskWindows() {
    // The two interfaces' identifiers, kept here: the headers' own (__uuidof) are symbols the
    // library would export.
    static const GUID factory_id{0x770aae78, 0xf26f, 0x4dba, {0xa8, 0x29, 0x25, 0x3c, 0x83, 0xd1, 0xb3, 0x87}};
    static const GUID output6_id{0x068346e8, 0xaaec, 0x4b84, {0xad, 0xd7, 0x13, 0x7f, 0x51, 0x3f, 0x77, 0xa1}};
    Display result;
    const HMODULE dxgi = LoadLibraryExW(L"dxgi.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (dxgi == nullptr) return result;
    using CreateFactory = HRESULT(WINAPI*)(REFIID, void**);
    const auto create = reinterpret_cast<CreateFactory>(
        reinterpret_cast<void*>(GetProcAddress(dxgi, "CreateDXGIFactory1")));
    IDXGIFactory1* factory = nullptr;
    if (create == nullptr ||
        FAILED(create(factory_id, reinterpret_cast<void**>(&factory))) ||
        factory == nullptr) {
        FreeLibrary(dxgi);
        return result;
    }
    bool asked = false; // an output answered GetDesc1 (Windows 10 1803 and later)
    bool found = false;
    bool found_primary = false;
    IDXGIAdapter1* adapter = nullptr;
    for (UINT a = 0; !found_primary && factory->EnumAdapters1(a, &adapter) == S_OK; ++a) {
        IDXGIOutput* output = nullptr;
        for (UINT o = 0; !found_primary && adapter->EnumOutputs(o, &output) == S_OK; ++o) {
            IDXGIOutput6* output6 = nullptr;
            DXGI_OUTPUT_DESC1 desc{};
            if (SUCCEEDED(output->QueryInterface(output6_id,
                                                 reinterpret_cast<void**>(&output6))) &&
                output6 != nullptr) {
                if (SUCCEEDED(output6->GetDesc1(&desc))) {
                    asked = true;
                    const bool hdr = desc.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020;
                    const bool primary =
                        desc.DesktopCoordinates.left == 0 && desc.DesktopCoordinates.top == 0;
                    if (hdr && (!found || primary)) {
                        found = true;
                        found_primary = primary;
                        const auto usable = FromDisplay(desc.MaxLuminance, desc.MaxFullFrameLuminance);
                        result.state = usable ? DisplayState::Hdr : DisplayState::HdrSilent;
                        result.luminance = usable ? *usable : Unset;
                    }
                }
                output6->Release();
            }
            output->Release();
        }
        adapter->Release();
    }
    factory->Release();
    FreeLibrary(dxgi);
    if (!found && asked) result.state = DisplayState::NoHdr;
    return result;
}

/// The launcher's settings next to the game's exe, or "".
inline std::string ReadConfig() {
    wchar_t exe[MAX_PATH * 2];
    const DWORD length = GetModuleFileNameW(nullptr, exe, MAX_PATH * 2);
    if (length == 0 || length >= MAX_PATH * 2) return {};
    std::wstring path(exe, length);
    path.resize(path.find_last_of(L"\\/") + 1);
    path += L"user\\custom_configs\\CUSA03220.json";
    std::string text;
    if (FILE* file = _wfopen(path.c_str(), L"rb")) {
        char buffer[4096];
        std::size_t read;
        while ((read = std::fread(buffer, 1, sizeof(buffer), file)) > 0) text.append(buffer, read);
        std::fclose(file);
    }
    return text;
}
#else
inline Display AskWindows() {
    return {};
}

inline std::string ReadConfig() {
    return {};
}
#endif

/// Decided at the game's first call and kept: the game asks once, and the display is not asked
/// again. Said once in stderr.log.
inline const Luminance& ForGame() {
    static const Luminance decided = [] {
        const Answer answer = Decide(std::getenv("RYGT_HDR_DISPLAY_NITS"), ReadConfig(), AskWindows);
        std::fprintf(stderr,
                     "[hdr] FIX-GT7-141: the game is told a display of peak %.0f, full frame %.0f, "
                     "min %.2f nits (%s)\n",
                     static_cast<double>(answer.luminance.peak),
                     static_cast<double>(answer.luminance.full_frame),
                     static_cast<double>(answer.luminance.min), answer.source);
        return answer.luminance;
    }();
    return decided;
}

} // namespace
} // namespace HdrLuminance
