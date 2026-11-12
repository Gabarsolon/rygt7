#include "prx/libc/include/GuestDirectory.hpp"
#include "prx/libc/include/General.hpp"
#include <dirent.h>
#include <cerrno>
#include <cstring>
#include <memory>
#include <mutex>
#include <utility>
#include <vector>

namespace {
int DirectoryError(int error) {
    switch (error) {
    case ENOENT: return 2;
    case EACCES: return 13;
    case ENOTDIR: return 20;
    case EINVAL: return 22;
    case EMFILE: return 24;
    case ENFILE: return 23;
    case ENOMEM: return 12;
    case ENAMETOOLONG: return 63;
    default: return 5;
    }
}
struct Directory {
    DIR* native = nullptr;
    std::filesystem::path path;
    // FEAT-GTS-251: a folder under /app0 made of several host folders is listed from here instead.
    bool layered = false;
    std::vector<std::filesystem::path> layeredEntries;
    std::size_t cursor = 0;
    GuestDirectoryEntry entry{};
    std::mutex mutex;
    ~Directory() { if (native) ::closedir(native); }
};
std::uint8_t DirectoryType(const std::filesystem::path& path) {
    std::error_code error;
    switch (std::filesystem::symlink_status(path, error).type()) {
    case std::filesystem::file_type::regular: return 8;
    case std::filesystem::file_type::directory: return 4;
    case std::filesystem::file_type::symlink: return 10;
    case std::filesystem::file_type::block: return 6;
    case std::filesystem::file_type::character: return 2;
    case std::filesystem::file_type::fifo: return 1;
    case std::filesystem::file_type::socket: return 12;
    default: return 0;
    }
}
}

extern "C" {
void* APS5_VABI opendir_nid_postfix(const char* path) {
    if (!path) { errno = 14; return nullptr; }
    if (!*path) { errno = 2; return nullptr; }
    try {
        auto directory = std::make_unique<Directory>();
        directory->path = ResolvePath_nid_no_patch(path);
        std::vector<std::pair<std::filesystem::path, bool>> layered;
        if (ListLayeredDirectory_nid_no_patch(path, &layered)) {
            directory->layered = true;
            directory->layeredEntries = {directory->path / ".", directory->path / ".."};
            for (auto& [host, isDirectory] : layered) directory->layeredEntries.push_back(std::move(host));
            return directory.release();
        }
        directory->native = ::opendir(directory->path.string().c_str());
        if (!directory->native) { errno = DirectoryError(errno); return nullptr; }
        return directory.release();
    } catch (const std::bad_alloc&) { errno = 12; return nullptr; }
      catch (const std::filesystem::filesystem_error&) { errno = 5; return nullptr; }
}

GuestDirectoryEntry* APS5_VABI readdir_nid_postfix(void* handle) {
    if (!handle) { errno = 9; return nullptr; }
    auto& directory = *static_cast<Directory*>(handle);
    std::lock_guard lock(directory.mutex);
    if (directory.layered) {
        if (directory.cursor >= directory.layeredEntries.size()) return nullptr; // end: errno unchanged
        const auto& host = directory.layeredEntries[directory.cursor++];
        const auto name = host.filename().string();
        if (name.size() > 255) { errno = 63; return nullptr; }
        directory.entry = {};
        directory.entry.recordLength = static_cast<std::uint16_t>(8 + ((name.size() + 1 + 3) & ~std::size_t{3}));
        directory.entry.nameLength = static_cast<std::uint8_t>(name.size());
        directory.entry.type = name == "." || name == ".." ? 4 : DirectoryType(host);
        std::memcpy(directory.entry.name, name.c_str(), name.size() + 1);
        return &directory.entry;
    }
    const int savedError = errno;
    errno = 0;
    auto* entry = ::readdir(directory.native);
    if (!entry) {
        errno = errno ? DirectoryError(errno) : savedError;
        return nullptr;
    }
    const auto length = std::strlen(entry->d_name);
    if (length > 255) { errno = 63; return nullptr; }
    try {
        directory.entry = {};
        // MinGW reports no inode identity (zero); do not fabricate one.
        const auto inode = static_cast<std::uint64_t>(entry->d_ino);
        directory.entry.fileNumber = inode <= UINT32_MAX ? static_cast<std::uint32_t>(inode) : 0;
        directory.entry.recordLength = static_cast<std::uint16_t>(8 + ((length + 1 + 3) & ~3));
        directory.entry.nameLength = static_cast<std::uint8_t>(length);
        directory.entry.type = DirectoryType(directory.path / entry->d_name);
        std::memcpy(directory.entry.name, entry->d_name, length + 1);
        errno = savedError;
        return &directory.entry;
    } catch (const std::bad_alloc&) { errno = 12; return nullptr; }
      catch (const std::filesystem::filesystem_error&) { errno = 5; return nullptr; }
}

int APS5_VABI closedir_nid_postfix(void* handle) {
    if (!handle) { errno = 9; return -1; }
    std::unique_ptr<Directory> directory(static_cast<Directory*>(handle));
    if (directory->layered) return 0;
    const int result = ::closedir(directory->native);
    directory->native = nullptr;
    if (result) errno = DirectoryError(errno);
    return result;
}

void APS5_VABI rewinddir_nid_postfix(void* handle) {
    if (!handle) { errno = 9; return; }
    auto& directory = *static_cast<Directory*>(handle);
    std::lock_guard lock(directory.mutex);
    if (directory.layered) { directory.cursor = 0; return; }
    ::rewinddir(directory.native);
}
}
