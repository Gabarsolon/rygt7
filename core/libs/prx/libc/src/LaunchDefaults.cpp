// GT7.exe started without the launcher (a double-click, a shortcut, Steam): the launcher's Play sets
// environment variables the runtime and the renderer need (where guest memory goes, which ranges
// stay in guest memory) and some of its settings. Without them GT7 stopped at start ("fixed guest
// mapping overlaps a guest arena range"). The ones missing are set here the way Play sets them,
// from the launcher's settings next to GT7.exe, before anything reads them. A variable already set
// (by the launcher, or by hand) is left alone, so Play works as before.
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>

#ifdef _WIN32
#include <windows.h>
#endif

namespace LaunchDefaults {
namespace {

#ifdef _WIN32
std::string ExeFolder() {
    wchar_t path[MAX_PATH * 2];
    const DWORD length = GetModuleFileNameW(nullptr, path, MAX_PATH * 2);
    if (length == 0 || length >= MAX_PATH * 2) return {};
    std::wstring wide(path, length);
    wide.resize(wide.find_last_of(L"\\/") + 1);
    const int bytes = WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), -1, nullptr, 0, nullptr, nullptr);
    std::string narrow(bytes > 0 ? bytes - 1 : 0, '\0');
    if (bytes > 0) WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), -1, narrow.data(), bytes, nullptr, nullptr);
    return narrow;
}

std::string ReadFile(const std::string& path) {
    std::string text;
    const int length = MultiByteToWideChar(CP_UTF8, 0, path.c_str(), -1, nullptr, 0);
    std::wstring wide(length > 0 ? length - 1 : 0, L'\0');
    if (length > 0) MultiByteToWideChar(CP_UTF8, 0, path.c_str(), -1, wide.data(), length);
    if (FILE* file = _wfopen(wide.c_str(), L"rb")) {
        char buffer[4096];
        size_t read;
        while ((read = std::fread(buffer, 1, sizeof(buffer), file)) > 0) text.append(buffer, read);
        std::fclose(file);
    }
    return text;
}
#endif

/// The raw value after "key": in the GPU section of the launcher's settings, or "" (the file is
/// small and flat; the keys are unique to GPU).
std::string Setting(const std::string& config, const char* key) {
    const std::string quoted = std::string("\"") + key + "\"";
    const size_t at = config.find(quoted);
    if (at == std::string::npos) return {};
    size_t begin = config.find_first_not_of(" \t\r\n:", at + quoted.size());
    if (begin == std::string::npos) return {};
    size_t end = config.find_first_of(",}\r\n", begin);
    std::string value = config.substr(begin, end == std::string::npos ? std::string::npos : end - begin);
    while (!value.empty() && (value.back() == ' ' || value.back() == '\t')) value.pop_back();
    if (value.size() >= 2 && value.front() == '"' && value.back() == '"') value = value.substr(1, value.size() - 2);
    return value;
}

void SetIfMissing(const char* name, const std::string& value) {
    const char* current = std::getenv(name);
    if (current != nullptr && *current != '\0') return;
#ifdef _WIN32
    // The process block for the libraries loaded later (each reads its environment from it), and
    // this C runtime's copy.
    SetEnvironmentVariableA(name, value.c_str());
    _putenv_s(name, value.c_str());
#else
    setenv(name, value.c_str(), 0);
#endif
}

int Clamp(const std::string& text, int fallback, int low, int high) {
    if (text.empty()) return fallback;
    char* end = nullptr;
    const long value = std::strtol(text.c_str(), &end, 10);
    if (end == text.c_str()) return fallback;
    return static_cast<int>(value < low ? low : value > high ? high : value);
}

} // namespace

void Apply() {
    static bool applied = false;
    if (applied) return;
    applied = true;
    // Started by the launcher: it set all of these.
    const char* fromLauncher = std::getenv("RYTY_INTEROP_BASE");
    if (fromLauncher != nullptr && *fromLauncher != '\0') return;
#ifdef _WIN32
    const std::string root = ExeFolder();
    const std::string config = ReadFile(root + "user\\custom_configs\\CUSA03220.json");
#else
    const std::string root;
    const std::string config;
#endif
    // As the launcher's StartGame (loader/RyGTLoader.cs) sets them; keep the two in step.
    SetIfMissing("RYTY_INTEROP_BASE", "8000000000");
    SetIfMissing("RYGT_NATIVE_INPLACE_RANGES", "1020000000+1de000,1020235000+1dd000,1020407000+b000,200000000+8000000");
    SetIfMissing("RYGT_NATIVE_INPLACE_FROM", "e000000000");
    if (!root.empty()) SetIfMissing("RYGT_LOG_DIRECTORY", root + "logs");
    SetIfMissing("SHADGT_FPS_LOCK", Setting(config, "race_fps_lock") == "60" ? "60" : "30");
    const long cacheMb = Setting(config, "driver_cache_unlimited") == "true"
                             ? -1
                             : Clamp(Setting(config, "driver_cache_gb"), 5, 0, 100) * 1024L;
    SetIfMissing("RYGT_DRIVER_CACHE_MAX_MB", std::to_string(cacheMb));
    SetIfMissing("RYGT_AMD_SINGLE_SAMPLE_BARYCENTRIC", Setting(config, "amd_graphics_fix") == "true" ? "1" : "0");
    SetIfMissing("RYGT_DLSSG_FRAMES", std::to_string(Clamp(Setting(config, "dlssg_frames"), 1, 1, 5)));
    const std::string quality = Setting(config, "upscale_quality");
    SetIfMissing("RYGT_UPSCALE_QUALITY", quality.empty() ? "auto" : quality);
    const std::string bias = Setting(config, "texture_lod_bias");
    SetIfMissing("RYGT_TEXTURE_LOD_BIAS", bias.empty() ? "0" : bias);
    std::fprintf(stderr, "[launch] started without the launcher: its settings applied from %s\n",
                 config.empty() ? "defaults" : "user\\custom_configs\\CUSA03220.json");
}

namespace {
// At libc's load, before the other libraries load and read their environment.
const int applied_at_load = (Apply(), 0);
} // namespace

} // namespace LaunchDefaults
