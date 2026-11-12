#include "prx/libc/include/AppOverlay.hpp"

#include <cstdlib>
#include <fstream>
#include <set>
#include <string_view>
#include <system_error>
#include <utility>

namespace Libc {

AppOverlay::AppOverlay(std::filesystem::path top, std::vector<std::filesystem::path> lower)
    : top(std::move(top)), lower(std::move(lower)) {}

AppOverlay::Key AppOverlay::Fold(Key text) {
    // Host folders are case-insensitive on Windows (and under Wine); guest names are ASCII.
    for (auto& character : text) {
        if (character >= 'A' && character <= 'Z') character = static_cast<Key::value_type>(character - 'A' + 'a');
    }
    return text;
}

std::vector<AppOverlay::Key> AppOverlay::Split(const std::string& relative) {
    std::vector<Key> parts;
    std::size_t start = 0;
    while (start <= relative.size()) {
        std::size_t end = relative.find('/', start);
        if (end == std::string::npos) end = relative.size();
        const auto part = relative.substr(start, end - start);
        if (!part.empty() && part != ".") parts.push_back(std::filesystem::path(part).native());
        start = end + 1;
    }
    return parts;
}

bool AppOverlay::OnlyInTop(const std::vector<Key>& parts) {
    static const Key module = std::filesystem::path("sce_module").native();
    static const Key executable = std::filesystem::path("eboot.bin").native();
    if (parts.empty()) return false;
    const auto first = Fold(parts[0]);
    return first == module || (parts.size() == 1 && first == executable);
}

const AppOverlay::Listing& AppOverlay::ListFolder(const std::filesystem::path& folder) {
    const auto key = Fold(folder.native());
    if (const auto found = listings.find(key); found != listings.end()) return found->second;
    Listing listing;
    std::error_code error;
    for (std::filesystem::directory_iterator it(folder, error), end; !error && it != end; it.increment(error)) {
        std::error_code typeError;
        const auto name = it->path().filename();
        listing.emplace(Fold(name.native()), Name{name, it->is_directory(typeError)});
    }
    return listings.emplace(key, std::move(listing)).first->second;
}

std::optional<std::filesystem::path> AppOverlay::Find(const std::filesystem::path& layer, const std::vector<Key>& parts,
                                                      bool& directory) {
    auto host = layer;
    directory = true;
    for (std::size_t i = 0; i < parts.size(); ++i) {
        if (parts[i] == std::filesystem::path("..").native()) return std::nullopt;
        const auto& listing = ListFolder(host);
        const auto found = listing.find(Fold(parts[i]));
        if (found == listing.end()) return std::nullopt;
        if (i + 1 < parts.size() && !found->second.directory) return std::nullopt;
        host /= found->second.actual;
        directory = found->second.directory;
    }
    return host;
}

std::filesystem::path AppOverlay::Resolve(const std::string& relative) {
    const auto parts = Split(relative);
    auto fallback = top;
    for (const auto& part : parts) fallback /= part;
    if (!Layered() || parts.empty()) return fallback.make_preferred();
    std::lock_guard lock(mutex);
    bool directory = false;
    if (auto found = Find(top, parts, directory)) return found->make_preferred();
    if (!OnlyInTop(parts)) {
        for (const auto& layer : lower) {
            if (auto found = Find(layer, parts, directory)) return found->make_preferred();
        }
    }
    return fallback.make_preferred();
}

std::optional<std::vector<AppOverlay::Entry>> AppOverlay::List(const std::string& relative) {
    if (!Layered()) return std::nullopt;
    const auto parts = Split(relative);
    std::lock_guard lock(mutex);
    std::vector<std::filesystem::path> layers{top};
    if (!OnlyInTop(parts)) layers.insert(layers.end(), lower.begin(), lower.end());
    std::vector<Entry> entries;
    std::set<Key> seen;
    bool any = false;
    for (std::size_t i = 0; i < layers.size(); ++i) {
        bool directory = false;
        const auto folder = Find(layers[i], parts, directory);
        if (!folder || !directory) continue;
        any = true;
        for (const auto& [key, name] : ListFolder(*folder)) {
            // At /app0 itself, the lower layers' eboot.bin and sce_module stay hidden (see OnlyInTop).
            if (i > 0 && parts.empty() && OnlyInTop({key})) continue;
            if (!seen.insert(key).second) continue;
            entries.push_back(Entry{(*folder / name.actual).make_preferred(), name.directory});
        }
    }
    if (!any) return std::nullopt;
    return entries;
}

bool AppOverlay::InGameFolder(const std::filesystem::path& host) const {
    if (lower.empty() || host.empty()) return false;
    const auto path = Fold(host.lexically_normal().make_preferred().native());
    for (const auto& layer : lower) {
        auto folder = Fold(layer.lexically_normal().make_preferred().native());
        while (!folder.empty() && (folder.back() == '\\' || folder.back() == '/')) folder.pop_back();
        if (path.size() < folder.size() || path.compare(0, folder.size(), folder) != 0) continue;
        if (path.size() == folder.size() || path[folder.size()] == '\\' || path[folder.size()] == '/') return true;
    }
    return false;
}

namespace {

std::filesystem::path FromUtf8(std::string_view text) {
    return std::filesystem::path(std::u8string(text.begin(), text.end()));
}

std::string_view Trim(std::string_view text) {
    while (!text.empty() && (text.front() == ' ' || text.front() == '\t')) text.remove_prefix(1);
    while (!text.empty() && (text.back() == ' ' || text.back() == '\t' || text.back() == '\r')) text.remove_suffix(1);
    return text;
}

}

std::vector<std::filesystem::path> ConfiguredApp0Layers(const std::filesystem::path& root) {
    std::vector<std::filesystem::path> candidates;
#ifdef _WIN32
    if (const wchar_t* value = _wgetenv(L"RYGT_APP0_LAYERS"); value && *value) {
        std::wstring list(value);
        std::size_t start = 0;
        while (start <= list.size()) {
            std::size_t end = list.find(L';', start);
            if (end == std::wstring::npos) end = list.size();
            if (end > start) candidates.emplace_back(list.substr(start, end - start));
            start = end + 1;
        }
    }
#else
    if (const char* value = std::getenv("RYGT_APP0_LAYERS"); value && *value) {
        std::string_view list(value);
        std::size_t start = 0;
        while (start <= list.size()) {
            std::size_t end = list.find(';', start);
            if (end == std::string_view::npos) end = list.size();
            if (end > start) candidates.push_back(FromUtf8(list.substr(start, end - start)));
            start = end + 1;
        }
    }
#endif
    else {
        // The launcher's settings (UTF-8): game= is the base game folder, update= its update.
        std::ifstream ini(root / "RyGT.ini", std::ios::binary);
        std::string line;
        std::filesystem::path game, update;
        while (std::getline(ini, line)) {
            std::string_view text(line);
            if (text.size() >= 3 && static_cast<unsigned char>(text[0]) == 0xEF) text.remove_prefix(3); // BOM
            const auto equals = text.find('=');
            if (equals == std::string_view::npos) continue;
            const auto key = Trim(text.substr(0, equals));
            const auto value = Trim(text.substr(equals + 1));
            if (value.empty()) continue;
            if (key == "game") game = FromUtf8(value);
            else if (key == "update") update = FromUtf8(value);
        }
        if (!update.empty()) candidates.push_back(update);
        if (!game.empty()) candidates.push_back(game);
    }
    std::vector<std::filesystem::path> layers;
    std::error_code error;
    const auto app0 = std::filesystem::weakly_canonical(root / "app0", error);
    for (const auto& candidate : candidates) {
        std::error_code folderError;
        if (!std::filesystem::is_directory(candidate, folderError)) continue;
        auto canonical = std::filesystem::weakly_canonical(candidate, folderError);
        if (folderError) canonical = candidate;
        if (canonical == app0) continue;
        bool duplicate = false;
        for (const auto& layer : layers) duplicate = duplicate || layer == canonical;
        if (!duplicate) layers.push_back(canonical);
    }
    return layers;
}

}
