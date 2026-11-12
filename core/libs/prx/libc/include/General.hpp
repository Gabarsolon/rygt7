#ifndef CORE_LIBS_PRX_LIBC_INCLUDE_GENERAL_HPP
#define CORE_LIBS_PRX_LIBC_INCLUDE_GENERAL_HPP

#include <cstdio>
#include <cwctype>
#include <stdexcept>
#include <filesystem>
#include <string>
#include <utility>
#include <vector>

#include "general/LogMacros.hpp"
#include "general/VabiMacros.hpp"
#include "general/ExportMacros.hpp"

extern "C" void NotImplemented_nid_no_patch(const char* funcName);
extern "C" void CxaFinalize_nid_no_patch(void* dsoHandle);

extern "C" std::filesystem::path ResolvePath_nid_no_patch(const char* path);
// FEAT-GTS-251: a folder under /app0 is the update's and the base game's folders together. Fills
// `entries` (host path, is a folder) with the merged contents and returns true for such a folder;
// false when `path` is not one (list the folder ResolvePath gives, as before).
extern "C" bool ListLayeredDirectory_nid_no_patch(const char* path, std::vector<std::pair<std::filesystem::path, bool>>* entries);
// Guest path aliases: a guest prefix such as "/savedata0" (a save-data mount point) resolves to a host
// directory instead of the run directory; the prefix matches whole path components only.
extern "C" void AddPathAlias_nid_no_patch(const char* guestPrefix, const char* hostPath);
extern "C" void RemovePathAlias_nid_no_patch(const char* guestPrefix);

// FEAT-GTS-251: whether a resolved host path is in the player's update or base game folder.
extern "C" bool IsGameFolderPath_nid_no_patch(const std::filesystem::path& host);

// /app0 is read-only on the PS4. Its files are the player's own game files: read straight from their
// game folders (FEAT-GTS-251), or links to them in the run folder from earlier set-ups. Changing one
// would change the dump itself, so those calls fail as on the PS4 (EROFS) and are reported.
inline bool IsReadOnlyGamePath(const std::filesystem::path& resolved, const char* operation,
                               const char* guestPath) {
    if (IsGameFolderPath_nid_no_patch(resolved)) {
        std::fprintf(stderr, "[file] %s %s refused: /app0 is read-only\n", operation, guestPath ? guestPath : "");
        return true;
    }
    static const std::wstring root = [] {
        std::wstring text = ResolvePath_nid_no_patch("/app0").lexically_normal().wstring();
        for (auto& c : text) c = static_cast<wchar_t>(towlower(c));
        while (!text.empty() && (text.back() == L'\\' || text.back() == L'/')) text.pop_back();
        return text;
    }();
    std::wstring path = resolved.lexically_normal().wstring();
    for (auto& c : path) c = static_cast<wchar_t>(towlower(c));
    const bool inside = path.size() >= root.size() && path.compare(0, root.size(), root) == 0 &&
                        (path.size() == root.size() || path[root.size()] == L'\\' || path[root.size()] == L'/');
    if (inside) std::fprintf(stderr, "[file] %s %s refused: /app0 is read-only\n", operation, guestPath);
    return inside;
}

#define APS5_INVALID_ARG_EX throw std::invalid_argument(std::string(__func__) + ": invalid argument")

#define APS5_DUMMY_FUN \
int DummyFunction_nid_no_patch() { \
    NotImplemented_nid_no_patch(__func__); \
    return 0; \
}

#endif
