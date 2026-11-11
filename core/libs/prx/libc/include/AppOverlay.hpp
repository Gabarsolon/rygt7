#ifndef CORE_LIBS_PRX_LIBC_INCLUDE_APPOVERLAY_HPP
#define CORE_LIBS_PRX_LIBC_INCLUDE_APPOVERLAY_HPP

// FEAT-GTS-251: /app0 read straight from the game's folders. Before, Set up linked every game file
// into ryGT7\app0 (hard links, or symbolic links across drives), which Explorer counts as a second
// copy of the game. Now /app0 is a stack of folders searched in order, like the console lays an update
// over the base game:
//   1. ryGT7\app0          - what Set up made: the converted sce_module (and eboot.bin's place)
//   2. the update folder    (RyGT.ini update=)
//   3. the base game folder (RyGT.ini game=)
// sce_module and eboot.bin are only ever taken from the first folder, as the links never covered
// them either: the game's own PS4 modules must not stand in for the converted ones.
// The folders' contents are listed once and kept: /app0 is read-only on the console.

#include <filesystem>
#include <mutex>
#include <optional>
#include <string>
#include <unordered_map>
#include <vector>

namespace Libc {

class AppOverlay {
public:
    struct Entry {
        std::filesystem::path host; // the file or folder in the layer it was found in
        bool directory = false;
    };

    AppOverlay(std::filesystem::path top, std::vector<std::filesystem::path> lower);

    /// Whether there is more than ryGT7\app0 to search.
    bool Layered() const { return !lower.empty(); }

    /// The host path of `relative` (a path under /app0, '/'-separated; "" for /app0 itself): the
    /// first layer that has it, else where it would be in ryGT7\app0.
    std::filesystem::path Resolve(const std::string& relative);

    /// The merged contents of the folder `relative` (a name in an earlier layer hides the same name
    /// in later ones), or nothing when no layer has that folder.
    std::optional<std::vector<Entry>> List(const std::string& relative);

    /// Whether `host` is in the update or base game folder (or is one of them): the player's own
    /// game files, which the game must never change. /app0 is read-only on the console anyway.
    bool InGameFolder(const std::filesystem::path& host) const;

private:
    struct Name {
        std::filesystem::path actual; // as spelled on disk
        bool directory = false;
    };
    using Key = std::filesystem::path::string_type;
    using Listing = std::unordered_map<Key, Name>;

    static Key Fold(Key text);
    static std::vector<Key> Split(const std::string& relative);
    static bool OnlyInTop(const std::vector<Key>& parts);
    const Listing& ListFolder(const std::filesystem::path& folder);
    std::optional<std::filesystem::path> Find(const std::filesystem::path& layer, const std::vector<Key>& parts,
                                              bool& directory);

    std::filesystem::path top;
    std::vector<std::filesystem::path> lower;
    std::mutex mutex;
    std::unordered_map<Key, Listing> listings;
};

/// The update and base game folders for this run, update first: RYGT_APP0_LAYERS (';'-separated)
/// when set, else update= and game= from RyGT.ini in `root`. Missing folders, and ryGT7\app0
/// itself, are left out; empty when there is nothing to layer (older set-ups with linked files).
std::vector<std::filesystem::path> ConfiguredApp0Layers(const std::filesystem::path& root);

}

#endif
