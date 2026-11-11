// FEAT-GTS-251: /app0 searched through ryGT7\app0, the update and the base game (AppOverlay.hpp).
// Standalone: g++ -std=c++20 -I<core/libs> AppOverlayTests.cpp src/AppOverlay.cpp
#include "prx/libc/include/AppOverlay.hpp"

#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <string>

namespace fs = std::filesystem;

static int failures = 0;

static void Check(bool condition, const char* what) {
    if (!condition) {
        std::fprintf(stderr, "FAILED: %s\n", what);
        ++failures;
    }
}

static void Touch(const fs::path& path, const std::string& text = "x") {
    fs::create_directories(path.parent_path());
    std::ofstream(path, std::ios::binary) << text;
}

static bool Lists(const std::vector<Libc::AppOverlay::Entry>& entries, const fs::path& host) {
    return std::any_of(entries.begin(), entries.end(), [&](const auto& entry) { return entry.host == host; });
}

static std::size_t Count(const std::vector<Libc::AppOverlay::Entry>& entries, const std::string& name) {
    return static_cast<std::size_t>(std::count_if(entries.begin(), entries.end(),
        [&](const auto& entry) { return entry.host.filename() == name; }));
}

int main() {
    const auto root = fs::temp_directory_path() / "rygt-app0-overlay-tests";
    fs::remove_all(root);
    const auto rygts = root / "ryGT7";
    const auto top = rygts / "app0";
    const auto update = root / u8"Gämes" / "CUSA03220-patch"; // a non-ASCII folder name
    const auto base = root / "Games" / "CUSA03220";

    Touch(top / "sce_module" / "libc.prx", "converted");
    Touch(update / "gt.idx", "update");
    Touch(update / "sce_sys" / "param.sfo", "update");
    Touch(update / "sce_module" / "libc.prx", "ps4");
    Touch(update / "eboot.bin", "ps4");
    Touch(base / "gt.idx", "base");
    Touch(base / "gt00.vol", "base");
    Touch(base / "sce_sys" / "param.sfo", "base");
    Touch(base / "sce_sys" / "pic1.png", "base");
    Touch(base / "sce_module" / "libSceOnlyPs4.prx", "ps4");
    Touch(base / "eboot.bin", "ps4");
    Touch(base / "Data" / "Deep" / "file.bin", "base");

    Libc::AppOverlay overlay(top, {update, base});
    Check(overlay.Layered(), "layered with an update and a base game");
    Check(overlay.Resolve("") == top, "/app0 itself is ryGT7\\app0");
    Check(overlay.Resolve("gt.idx") == update / "gt.idx", "the update's file wins over the base game's");
    Check(overlay.Resolve("gt00.vol") == base / "gt00.vol", "a file only in the base game comes from it");
    Check(overlay.Resolve("sce_sys/param.sfo") == update / "sce_sys" / "param.sfo", "update wins inside folders");
    Check(overlay.Resolve("sce_sys/pic1.png") == base / "sce_sys" / "pic1.png",
          "folders are merged per file (the old folder junction hid the base game's other files)");
    Check(overlay.Resolve("SCE_SYS/PARAM.SFO") == update / "sce_sys" / "param.sfo", "names match without case");
    Check(overlay.Resolve("Data/Deep/file.bin") == base / "Data" / "Deep" / "file.bin", "deep paths");
    Check(overlay.Resolve("sce_module/libc.prx") == top / "sce_module" / "libc.prx", "converted modules win");
    Check(overlay.Resolve("sce_module/libSceOnlyPs4.prx") == top / "sce_module" / "libSceOnlyPs4.prx",
          "the game's own PS4 modules are never used");
    Check(overlay.Resolve("eboot.bin") == top / "eboot.bin", "the game's eboot.bin is never used");
    Check(overlay.Resolve("missing.txt") == top / "missing.txt", "a missing file is looked for in ryGT7\\app0");
    Check(overlay.Resolve("gt.idx/x") == top / "gt.idx" / "x", "a file is not a folder");

    const auto rootListing = overlay.List("");
    Check(rootListing.has_value(), "/app0 can be listed");
    if (rootListing) {
        Check(Count(*rootListing, "gt.idx") == 1, "a name in two layers is listed once");
        Check(Lists(*rootListing, update / "gt.idx"), "and from the layer that wins");
        Check(Lists(*rootListing, base / "gt00.vol"), "base game files are listed");
        Check(Lists(*rootListing, top / "sce_module"), "sce_module is listed from ryGT7\\app0");
        Check(Count(*rootListing, "sce_module") == 1, "the game's sce_module stays hidden");
        Check(Count(*rootListing, "eboot.bin") == 0, "the game's eboot.bin stays hidden");
    }
    const auto sceSys = overlay.List("sce_sys");
    Check(sceSys && sceSys->size() == 2 && Lists(*sceSys, update / "sce_sys" / "param.sfo") &&
              Lists(*sceSys, base / "sce_sys" / "pic1.png"), "folder listings are merged");
    const auto modules = overlay.List("sce_module");
    Check(modules && modules->size() == 1 && Lists(*modules, top / "sce_module" / "libc.prx"),
          "sce_module lists only the converted modules");
    Check(!overlay.List("nothing-here"), "a folder no layer has can't be listed");
    Check(!overlay.List("gt.idx"), "a file can't be listed");

    Check(overlay.InGameFolder(overlay.Resolve("gt00.vol")), "a base game file is the player's");
    Check(overlay.InGameFolder(overlay.Resolve("GT.IDX")), "an update file is the player's");
    Check(overlay.InGameFolder(base), "the base game folder itself is the player's");
    Check(!overlay.InGameFolder(overlay.Resolve("sce_module/libc.prx")), "ryGT7\\app0 is not the player's");
    Check(!overlay.InGameFolder(overlay.Resolve("new-file.txt")), "a new file goes to ryGT7\\app0");
    Check(!overlay.InGameFolder(root / "Games" / "CUSA03220-other" / "gt.idx"), "a sibling folder with a longer name is not inside");

    Libc::AppOverlay single(top, {});
    Check(!single.Layered() && single.Resolve("gt.idx") == top / "gt.idx" && !single.List(""),
          "without game folders /app0 is ryGT7\\app0 alone, as before");

    // The folders come from RyGT.ini, as the launcher writes it (UTF-8).
    {
        std::ofstream ini(rygts / "RyGT.ini", std::ios::binary);
        const auto updateText = update.u8string();
        const auto baseText = base.u8string();
        ini << "game=" << std::string(baseText.begin(), baseText.end()) << "\r\n"
            << "update=" << std::string(updateText.begin(), updateText.end()) << "\r\n"
            << "show_advanced=1\r\n";
    }
#ifdef _WIN32
    _wputenv_s(L"RYGT_APP0_LAYERS", L"");
#else
    unsetenv("RYGT_APP0_LAYERS");
#endif
    const auto fromIni = Libc::ConfiguredApp0Layers(rygts);
    Check(fromIni.size() == 2 && fromIni[0] == fs::weakly_canonical(update) && fromIni[1] == fs::weakly_canonical(base),
          "RyGT.ini gives the update, then the base game");
    {
        std::ofstream ini(rygts / "RyGT.ini", std::ios::binary);
        ini << "game=" << (root / "gone").string() << "\nupdate=\n";
    }
    Check(Libc::ConfiguredApp0Layers(rygts).empty(), "missing or empty folders are left out");
#ifdef _WIN32
    _wputenv_s(L"RYGT_APP0_LAYERS", (base.wstring() + L";" + top.wstring()).c_str());
#else
    setenv("RYGT_APP0_LAYERS", (base.string() + ";" + top.string()).c_str(), 1);
#endif
    const auto fromEnvironment = Libc::ConfiguredApp0Layers(rygts);
    Check(fromEnvironment.size() == 1 && fromEnvironment[0] == fs::weakly_canonical(base),
          "RYGT_APP0_LAYERS overrides RyGT.ini, and ryGT7\\app0 itself is not a lower layer");

    fs::remove_all(root);
    if (failures == 0) std::puts("app0 overlay tests passed");
    return failures == 0 ? 0 : 1;
}
