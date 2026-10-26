// Ultrawide for GT7 (CUSA24769): the race camera's view is set up with a 16:9 aspect
// (eboot 0x1e890eb: mov dword [rbx+0x9e894], 1.7777778f), as GT Sport's is (0x5e717f, patched by
// the renderer as GTS-009). With GPU.aspect_ratio set (the renderer then stretches the picture over
// the window), that constant becomes the window's shape so the camera sees more to the sides
// instead of the picture being stretched. The bytes are checked first: any other game or version
// is left alone. The converted executable keeps the title's layout at ELF address + 0x10000.
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include "prx/libScePad/include/PadState.hpp"

namespace {

// Where GT7 1.00 stores the 16:9 aspect: the instruction's bytes, and where its float is in them.
struct AspectStore {
    std::uint64_t address;
    std::uint8_t code[10];
    std::size_t size;
    std::size_t immediate;
};
constexpr AspectStore AspectStores[] = {
    // The race camera's setup: mov dword [rbx+0x9e894], 1.7777778f
    {0x1e890eb, {0xc7, 0x83, 0x94, 0xe8, 0x09, 0x00, 0x39, 0x8e, 0xe3, 0x3f}, 10, 6},
    // The view's setup every frame (0x20a78f0), which sets it again: mov dword [rdi+0x34], 1.7777778f
    {0x20a7938, {0xc7, 0x47, 0x34, 0x39, 0x8e, 0xe3, 0x3f}, 7, 3},
    // The 16:9 constant the scene's projection takes its aspect from (0x1eee849, the camera
    // code at 0x1efa937-0x1efb7e4, the aspect getter at 0x1b4c3f0, 0x2086de2, 0x2148905).
    {0x3f4f510, {0x39, 0x8e, 0xe3, 0x3f}, 4, 0},
};

// A value of the runtime's settings (user\custom_configs\CUSA03220.json) as written, or "".
std::string ConfigValue(const char* key) {
    char exe[MAX_PATH];
    const DWORD length = GetModuleFileNameA(nullptr, exe, MAX_PATH);
    if (length == 0 || length >= MAX_PATH) return {};
    std::string path(exe, length);
    path = path.substr(0, path.find_last_of("\\/") + 1) + "user\\custom_configs\\CUSA03220.json";
    std::FILE* file = std::fopen(path.c_str(), "rb");
    if (file == nullptr) return {};
    std::string text;
    char chunk[4096];
    while (const std::size_t n = std::fread(chunk, 1, sizeof(chunk), file)) text.append(chunk, n);
    std::fclose(file);
    const std::string quoted = std::string("\"") + key + "\"";
    const std::size_t at = text.find(quoted);
    if (at == std::string::npos) return {};
    const std::size_t value = text.find_first_not_of(" \t\r\n:", at + quoted.size());
    if (value == std::string::npos) return {};
    if (text[value] == '"') {
        const std::size_t end = text.find('"', value + 1);
        return end == std::string::npos ? std::string() : text.substr(value + 1, end - value - 1);
    }
    const std::size_t end = text.find_first_of(",}\r\n", value);
    return text.substr(value, end == std::string::npos ? std::string::npos : end - value);
}

// The screen shape the renderer stretches the picture to (the same rules as its GTS-009): "auto"
// (the window's or screen's shape), "21:9", "32:9", width:height or a number; RYGT_ASPECT
// overrides it. 0 when the game's own 16:9 applies.
float DisplayAspect() {
    std::string value = ConfigValue("aspect_ratio");
    if (const char* env = std::getenv("RYGT_ASPECT")) value = env;
    double ratio = 0.0;
    if (value == "auto") {
        const bool full = ConfigValue("full_screen") == "true";
        const bool exclusive = ConfigValue("full_screen_mode") == "Fullscreen";
        double width = std::atof(ConfigValue("window_width").c_str());
        double height = std::atof(ConfigValue("window_height").c_str());
        if (full && !exclusive) {  // borderless: the desktop's resolution
            width = GetSystemMetrics(SM_CXSCREEN);
            height = GetSystemMetrics(SM_CYSCREEN);
        }
        ratio = height > 0.0 ? width / height : 0.0;
    } else if (const std::size_t colon = value.find(':'); colon != std::string::npos) {
        const double height = std::atof(value.substr(colon + 1).c_str());
        ratio = height > 0.0 ? std::atof(value.substr(0, colon).c_str()) / height : 0.0;
    } else {
        ratio = std::atof(value.c_str());
    }
    if (!(ratio > 0.5 && ratio < 6.0) || std::abs(ratio - 16.0 / 9.0) < 0.01) return 0.0f;
    return static_cast<float>(ratio);
}

}

namespace Pad {

void InstallWideCamera() {
    static bool done = false;
    if (done) return;
    done = true;
    const float aspect = DisplayAspect();
    if (aspect <= 0.0f) return;
    auto* base = reinterpret_cast<std::uint8_t*>(GetModuleHandleA(nullptr));
    if (base == nullptr) return;
    int patched = 0;
    for (const AspectStore& store : AspectStores) {
        std::uint8_t* code = base + 0x10000 + store.address;
        MEMORY_BASIC_INFORMATION info;
        if (VirtualQuery(code, &info, sizeof(info)) == 0 || info.State != MEM_COMMIT ||
            std::memcmp(code, store.code, store.size) != 0) {
            continue;  // not GT7 1.00 (GT Sport's camera is the renderer's GTS-009)
        }
        DWORD old = 0;
        if (!VirtualProtect(code + store.immediate, 4, PAGE_EXECUTE_READWRITE, &old)) continue;
        std::memcpy(code + store.immediate, &aspect, 4);
        VirtualProtect(code + store.immediate, 4, old, &old);
        FlushInstructionCache(GetCurrentProcess(), code, store.size);
        ++patched;
    }
    std::fprintf(stderr, "[patch] GT7 ultrawide: aspect %.3f at %d of %d places\n", aspect, patched,
        static_cast<int>(sizeof(AspectStores) / sizeof(AspectStores[0])));
    std::fflush(stderr);
}

}
