#include <mutex>
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>
#include <filesystem>
#include <optional>
#include <cerrno>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <set>
#include "prx/libc/include/General.hpp"
#include "prx/libc/include/GuestHeap.hpp"
#include "prx/libc/include/AppOverlay.hpp"

namespace {
// Guest prefixes (without leading slashes) mapped to host directories, e.g. save-data mount points:
// the PS5 hands the title a short mount point ("/savedata0") whose files live under _sd/<dir name>.
struct PathAliases {
    std::mutex mutex;
    std::vector<std::pair<std::string, std::string>> entries;
};

PathAliases& Aliases() {
    static PathAliases aliases;
    return aliases;
}

std::string TrimSlashes(const char* path) {
    std::string s(path);
    std::size_t start = 0;
    while (start < s.size() && (s[start] == '/' || s[start] == '\\')) {
        ++start;
    }
    std::size_t end = s.size();
    while (end > start && (s[end - 1] == '/' || s[end - 1] == '\\')) {
        --end;
    }
    return s.substr(start, end - start);
}

std::optional<std::filesystem::path> ResolveAlias(const std::string& guestPath) {
    const auto relative = TrimSlashes(guestPath.c_str());
    auto& aliases = Aliases();
    std::lock_guard lock(aliases.mutex);
    for (const auto& [prefix, host] : aliases.entries) {
        if (relative.size() < prefix.size() || relative.compare(0, prefix.size(), prefix) != 0) continue;
        if (relative.size() == prefix.size()) return std::filesystem::path(host).make_preferred();
        if (relative[prefix.size()] != '/') continue;
        std::filesystem::path result = std::filesystem::path(host) / std::filesystem::path(relative.substr(prefix.size() + 1));
        return result.make_preferred();
    }
    return std::nullopt;
}

struct WorkingDirectory {
    std::mutex mutex;
    const std::filesystem::path root = std::filesystem::canonical(std::filesystem::current_path());
    // The guest's current directory as a path under root. Under /app0 it may name a folder that only
    // exists in the update or base game folder (FEAT-GTS-251), so it is not always a real folder.
    std::filesystem::path current = root;
};
WorkingDirectory& Directories() { static WorkingDirectory state; return state; }

// FEAT-GTS-251: /app0 searched through ryGT7\app0, the update and the base game (AppOverlay.hpp).
Libc::AppOverlay& App0() {
    static Libc::AppOverlay overlay = [] {
        const auto& root = Directories().root;
        return Libc::AppOverlay(root / "app0", Libc::ConfiguredApp0Layers(root));
    }();
    return overlay;
}

// The part of a guest path (relative, '/'-separated) under /app0, if it is under /app0.
std::optional<std::string> UnderApp0(const std::string& relative) {
    if (relative.size() < 4 || relative.compare(0, 4, "app0") != 0) return std::nullopt;
    if (relative.size() == 4) return std::string();
    if (relative[4] != '/') return std::nullopt;
    return relative.substr(5);
}

// The guest path `path` names, made absolute against the current directory and normalized; nothing
// for an explicit host drive path (Windows), which is used as it is.
std::optional<std::filesystem::path> GuestPath(WorkingDirectory& state, const char* path) {
    std::string text(path);
    for (auto& character : text) if (character == '\\') character = '/';
    std::filesystem::path input(text);
#ifdef _WIN32
    // Preserve the existing ability to pass explicit native drive paths.
    if (input.has_root_name()) return std::nullopt;
#endif
    auto guest = (std::filesystem::path("/") / state.current.lexically_relative(state.root));
    return (input.is_absolute() ? input : guest / input).lexically_normal();
}

std::filesystem::path ResolveGuest(WorkingDirectory& state, const std::filesystem::path& guest) {
    const auto relative = guest.relative_path().generic_string();
    if (auto aliased = ResolveAlias(relative)) return *aliased;
    if (auto app0 = UnderApp0(relative)) return App0().Resolve(*app0);
    return (state.root / guest.relative_path()).make_preferred();
}

std::filesystem::path Resolve(WorkingDirectory& state, const char* path) {
    const auto guest = GuestPath(state, path);
    if (!guest) {
        std::string text(path);
        for (auto& character : text) if (character == '\\') character = '/';
        return std::filesystem::path(text);
    }
    return ResolveGuest(state, *guest);
}
int DirectoryFailure(const std::error_code& error) {
    if (error == std::errc::permission_denied) return 13;
    if (error == std::errc::not_a_directory) return 20;
    if (error == std::errc::no_such_file_or_directory) return 2;
    if (error == std::errc::filename_too_long) return 63;
    if (error == std::errc::too_many_symbolic_link_levels) return 62;
    return 5;
}
}

extern "C" void AddPathAlias_nid_no_patch(const char* guestPrefix, const char* hostPath) {
    if (guestPrefix == nullptr || hostPath == nullptr) {
        APS5_INVALID_ARG_EX;
    }
    auto& aliases = Aliases();
    std::lock_guard lock(aliases.mutex);
    const auto prefix = TrimSlashes(guestPrefix);
    for (auto& entry : aliases.entries) {
        if (entry.first == prefix) {
            entry.second = hostPath;
            return;
        }
    }
    aliases.entries.emplace_back(prefix, hostPath);
}

extern "C" void RemovePathAlias_nid_no_patch(const char* guestPrefix) {
    if (guestPrefix == nullptr) {
        APS5_INVALID_ARG_EX;
    }
    auto& aliases = Aliases();
    std::lock_guard lock(aliases.mutex);
    const auto prefix = TrimSlashes(guestPrefix);
    std::erase_if(aliases.entries, [&](const auto& entry) { return entry.first == prefix; });
}

extern "C" std::filesystem::path ResolvePath_nid_no_patch(const char* path) {
    if (!path) { APS5_INVALID_ARG_EX; }
    auto& state = Directories();
    std::lock_guard lock(state.mutex);
    return Resolve(state, path);
}

extern "C" bool IsGameFolderPath_nid_no_patch(const std::filesystem::path& host) {
    return App0().InGameFolder(host);
}

extern "C" bool ListLayeredDirectory_nid_no_patch(const char* path, std::vector<std::pair<std::filesystem::path, bool>>* entries) {
    if (!path || !entries) { APS5_INVALID_ARG_EX; }
    auto& state = Directories();
    std::lock_guard lock(state.mutex);
    const auto guest = GuestPath(state, path);
    if (!guest) return false;
    const auto relative = guest->relative_path().generic_string();
    if (ResolveAlias(relative)) return false;
    const auto app0 = UnderApp0(relative);
    if (!app0) return false;
    auto listed = App0().List(*app0);
    if (!listed) return false;
    entries->clear();
    for (auto& entry : *listed) entries->emplace_back(std::move(entry.host), entry.directory);
    return true;
}

extern "C" int APS5_VABI chdir_nid_postfix(const char* path) {
    if (!path) { errno = 14; return -1; }
    if (!*path) { errno = 2; return -1; }
    try {
        auto& state = Directories();
        std::lock_guard lock(state.mutex);
        std::error_code error;
        // A folder under /app0 can be in the update or base game folder, outside root: the current
        // directory is kept as the guest path under root and resolved again on each use.
        if (const auto guest = GuestPath(state, path)) {
            const auto relative = guest->relative_path().generic_string();
            if (!ResolveAlias(relative) && UnderApp0(relative) && App0().Layered()) {
                if (!std::filesystem::is_directory(ResolveGuest(state, *guest), error)) {
                    errno = error ? DirectoryFailure(error) : 20; return -1;
                }
                state.current = (state.root / guest->relative_path()).lexically_normal();
                return 0;
            }
        }
        const auto resolved = std::filesystem::canonical(Resolve(state, path), error);
        if (error) { errno = DirectoryFailure(error); return -1; }
        if (!std::filesystem::is_directory(resolved, error)) {
            errno = error ? DirectoryFailure(error) : 20; return -1;
        }
        const auto relative = resolved.lexically_relative(state.root);
        if (relative.empty() || *relative.begin() == "..") { errno = 45; return -1; }
        state.current = resolved;
        return 0;
    } catch (const std::bad_alloc&) { errno = 12; return -1; }
      catch (const std::filesystem::filesystem_error& error) { errno = DirectoryFailure(error.code()); return -1; }
}

extern "C" char* APS5_VABI getcwd_nid_postfix(char* buffer, std::size_t size) {
    if (buffer && size == 0) { errno = 22; return nullptr; }
    try {
        auto& state = Directories();
        std::lock_guard lock(state.mutex);
        std::error_code error;
        const auto relative = state.current.lexically_relative(state.root);
        const auto guest = std::filesystem::path("/") / (relative == "." ? std::filesystem::path() : relative);
        if (!std::filesystem::is_directory(ResolveGuest(state, guest), error)) {
            errno = error ? DirectoryFailure(error) : 2; return nullptr;
        }
        const auto path = relative == "." ? std::string("/") : "/" + relative.generic_string();
        const auto required = path.size() + 1;
        if ((buffer || size) && size < required) { errno = 34; return nullptr; }
        if (!buffer) buffer = static_cast<char*>(GuestHeap::GuestHeapAllocate_nid_postfix(size ? size : required));
        std::memcpy(buffer, path.c_str(), required);
        return buffer;
    } catch (const std::bad_alloc&) { errno = 12; return nullptr; }
      catch (const std::filesystem::filesystem_error& error) { errno = DirectoryFailure(error.code()); return nullptr; }
}

extern "C" void NotImplemented_nid_no_patch(const char* funcName) {
    // Each missing function is logged once and the caller gets its default (usually 0), so titles
    // that only probe optional services keep running. RyGT makes this the default, so GTSport.exe
    // runs without a launcher; RTY_LENIENT_UNIMPLEMENTED=0 throws instead.
    static const bool lenient = [] {
        const char* value = std::getenv("RTY_LENIENT_UNIMPLEMENTED");
        return value == nullptr || std::strcmp(value, "0") != 0;
    }();
    if (!lenient) throw std::runtime_error(std::string(funcName) + " not implemented");
    static std::mutex lock;
    static std::set<std::string> reported;
    std::lock_guard guard(lock);
    if (reported.insert(funcName).second) std::fprintf(stderr, "[unimplemented] %s\n", funcName);
}
