#include <cstdint>
#include <cstddef>
#include <cstdlib>
#include <limits>
#include "SceTypes.hpp"
#include "prx/libkernel/Time/include/Time.hpp"
#include "prx/libc/include/General.hpp"
#include "prx/libkernel/File/include/File.hpp"
#include "prx/libkernel/File/include/ReadAhead.hpp"
#include "prx/libkernel/File/include/FileFlags.hpp"
#include "prx/libkernel/File/include/NativeStat.hpp"
#include "prx/libkernel/File/include/DirectoryDescriptor.hpp"
#include "prx/libkernel/KernelErrors.hpp"
#include "prx/libkernel/Socket/include/SocketRuntime.hpp"
#include <cerrno>
#include <cstring>
#include <atomic>
#include <cstdarg>
#include <filesystem>
#include <stdexcept>
#include <string>
#include <map>
#include <mutex>
#include <memory>
#ifdef _WIN32
#include <windows.h>
#include <io.h>
#include <direct.h>
#include <sys/stat.h>
#include <sys/utime.h>
namespace ReadAhead {
namespace {
constexpr DWORD Capacity = 256 * 1024;
struct Source {
    HANDLE handle = INVALID_HANDLE_VALUE;
    std::uint64_t generation = 0;
    ~Source() { if (handle != INVALID_HANDLE_VALUE) CloseHandle(handle); }
};
std::mutex source_mutex;
std::map<int, std::shared_ptr<Source>> sources;
std::atomic<std::uint64_t> next_generation{1};

bool Enabled() {
    static const bool enabled = [] {
        const char* value = std::getenv("RYGT_READ_AHEAD");
        return value && value[0] == '1';
    }();
    return enabled;
}

struct Buffer {
    void* data = nullptr;
    std::uint64_t generation = 0;
    std::uint64_t first = 0;
    std::uint64_t next = UINT64_MAX;
    DWORD valid = 0;
    ~Buffer() { if (data) VirtualFree(data, 0, MEM_RELEASE); }
};

std::shared_ptr<Source> Find(int descriptor) {
    if (!Enabled()) return {};
    std::lock_guard lock(source_mutex);
    const auto found = sources.find(descriptor);
    return found == sources.end() ? nullptr : found->second;
}

std::int64_t NativeRead(HANDLE handle, void* target, DWORD bytes, std::uint64_t offset) {
    OVERLAPPED at{};
    at.Offset = static_cast<DWORD>(offset);
    at.OffsetHigh = static_cast<DWORD>(offset >> 32);
    DWORD got = 0;
    DWORD error = ERROR_SUCCESS;
    {
        KernelDiagScope diag(static_cast<KernelDiagZone>(85), reinterpret_cast<const void*>(static_cast<std::uintptr_t>(bytes)));
        if (!::ReadFile(handle, target, bytes, &got, &at)) error = ::GetLastError();
    }
    if (error == ERROR_SUCCESS) return got;
    ::SetLastError(error);
    if (error == ERROR_HANDLE_EOF) return 0;
    errno = EIO;
    return -1;
}

std::int64_t Read(const std::shared_ptr<Source>& source, HANDLE original,
                  void* target, DWORD bytes, std::uint64_t offset) {
    if (!source) return NativeRead(original, target, bytes, offset);
    static thread_local Buffer buffer;
    if (buffer.generation != source->generation) {
        buffer.generation = source->generation;
        buffer.valid = 0;
        buffer.next = UINT64_MAX;
    }
    const bool sequential = buffer.next == offset && bytes >= 32768 && bytes <= 131072;
    buffer.next = bytes && offset <= UINT64_MAX - bytes ? offset + bytes : UINT64_MAX;
    if (bytes && offset >= buffer.first && offset - buffer.first < buffer.valid &&
        bytes <= buffer.valid - (offset - buffer.first)) {
        std::memcpy(target, static_cast<const char*>(buffer.data) + (offset - buffer.first), bytes);
        KernelDiagMark(static_cast<KernelDiagZone>(86), bytes);
        return bytes;
    }
    if (sequential) {
        if (!buffer.data) buffer.data = VirtualAlloc(nullptr, Capacity, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
        if (buffer.data) {
            buffer.valid = 0;
            const auto got = NativeRead(source->handle, buffer.data, Capacity, offset);
            if (got >= 0) {
                buffer.first = offset;
                buffer.valid = static_cast<DWORD>(got);
                const auto delivered = std::min(bytes, buffer.valid);
                std::memcpy(target, buffer.data, delivered);
                return delivered;
            }
        }
    }
    return NativeRead(source->handle, target, bytes, offset);
}
}

void Register(int descriptor, const char* path, int flags) noexcept {
    if (!Enabled() || !path || descriptor < 0 ||
        (flags & (SCE_KERNEL_O_ACCMODE | SCE_KERNEL_O_CREAT | SCE_KERNEL_O_TRUNC | SCE_KERNEL_O_APPEND | SCE_KERNEL_O_DIRECT))) return;
    try {
        const auto guest = std::filesystem::path(path).lexically_normal().generic_string();
        if (!guest.starts_with("/app0/contents/") || !guest.ends_with(".vol")) return;
        const HANDLE original = reinterpret_cast<HANDLE>(::_get_osfhandle(descriptor));
        if (original == INVALID_HANDLE_VALUE || GetFileType(original) != FILE_TYPE_DISK) return;
        auto source = std::make_shared<Source>();
        source->handle = ReOpenFile(original, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE, 0);
        if (source->handle == INVALID_HANDLE_VALUE) return;
        source->generation = next_generation.fetch_add(1, std::memory_order_relaxed);
        {
            std::lock_guard lock(source_mutex);
            sources[descriptor] = source;
        }
        std::fprintf(stderr, "[read-ahead] fd %d generation %llu: %s (256 KiB, read-only)\n",
                     descriptor, static_cast<unsigned long long>(source->generation), guest.c_str());
    } catch (...) {
    }
}

void Forget(int descriptor) {
    if (!Enabled()) return;
    std::lock_guard lock(source_mutex);
    sources.erase(descriptor);
}
}

static int NativeRmdir(const std::filesystem::path& path) {
    return ::_wrmdir(path.wstring().c_str());
}
static int NativeMkdir(const std::filesystem::path& path, std::uint16_t mode) {
    (void)mode;
    return ::_wmkdir(path.wstring().c_str());
}
static int NativeChmod(const std::filesystem::path& path, int mode) {
    return ::_wchmod(path.wstring().c_str(), mode);
}
static int NativeFtruncate(int descriptor, std::int64_t length) {
    return static_cast<int>(::_chsize_s(descriptor, length));
}
static int NativeUtimes(const std::filesystem::path& path, const KernelTimeval* times) {
    if (times == nullptr) return ::_wutime(path.wstring().c_str(), nullptr);
    struct _utimbuf values{static_cast<time_t>(times[0].tv_sec), static_cast<time_t>(times[1].tv_sec)};
    return ::_wutime(path.wstring().c_str(), &values);
}
static int NativeFlock(int descriptor, int operation) {
    HANDLE handle = reinterpret_cast<HANDLE>(::_get_osfhandle(descriptor));
    if (handle == INVALID_HANDLE_VALUE) {
        return -1;
    }
    OVERLAPPED overlapped{};
    if (operation & 8) {
        return ::UnlockFileEx(handle, 0, MAXDWORD, MAXDWORD, &overlapped) ? 0 : -1;
    }
    DWORD flags = 0;
    if (operation & 2) flags |= LOCKFILE_EXCLUSIVE_LOCK;
    if (operation & 4) flags |= LOCKFILE_FAIL_IMMEDIATELY;
    return ::LockFileEx(handle, flags, 0, MAXDWORD, MAXDWORD, &overlapped) ? 0 : -1;
}
static std::int64_t NativePositioned(int descriptor, void* buf, std::size_t nbytes, std::int64_t offset, bool write) {
    if (nbytes > static_cast<std::size_t>(std::numeric_limits<DWORD>::max())) {
        throw std::runtime_error("NativePositioned: nbytes exceeds platform limit");
    }
    const auto handle = reinterpret_cast<HANDLE>(::_get_osfhandle(descriptor));
    if (handle == INVALID_HANDLE_VALUE) {
        errno = EBADF;
        return -1;
    }
    OVERLAPPED overlapped{};
    overlapped.Offset = static_cast<DWORD>(offset);
    overlapped.OffsetHigh = static_cast<DWORD>(static_cast<std::uint64_t>(offset) >> 32u);
    DWORD done = 0;
    if (!write) {
        if (KernelDiagEnabled() && descriptor >= 0 && descriptor <= 0xffff &&
            offset >= 0 && static_cast<std::uint64_t>(offset) < (1ull << 48)) {
            KernelDiagMark(static_cast<KernelDiagZone>(83),
                           (static_cast<std::uint64_t>(descriptor) << 48) |
                           static_cast<std::uint64_t>(offset));
        }
        const auto source = ReadAhead::Find(descriptor);
        return ReadIntoGuest(buf, nbytes, [&](void* target) -> std::int64_t {
            return ReadAhead::Read(source, handle, target, static_cast<DWORD>(nbytes), offset);
        });
    }
    if (!::WriteFile(handle, buf, static_cast<DWORD>(nbytes), &done, &overlapped)) {
        errno = EIO;
        return -1;
    }
    return done;
}
static std::int64_t NativePread(int descriptor, void* buf, std::size_t nbytes, std::int64_t offset) {
    return NativePositioned(descriptor, buf, nbytes, offset, false);
}
static std::int64_t NativePwrite(int descriptor, const void* buf, std::size_t nbytes, std::int64_t offset) {
    return NativePositioned(descriptor, const_cast<void*>(buf), nbytes, offset, true);
}
#else
#include <unistd.h>
#include <sys/stat.h>
#include <sys/file.h>
#include <sys/time.h>
#include <dirent.h>
#include <sys/syscall.h>
#include <vector>
static int NativeRmdir(const std::filesystem::path& path) {
    return ::rmdir(path.c_str());
}
static int NativeMkdir(const std::filesystem::path& path, std::uint16_t mode) {
    return ::mkdir(path.c_str(), static_cast<mode_t>(mode));
}
static int NativeChmod(const std::filesystem::path& path, int mode) {
    return ::chmod(path.c_str(), static_cast<mode_t>(mode));
}
static int NativeFtruncate(int descriptor, std::int64_t length) {
    return ::ftruncate(descriptor, static_cast<off_t>(length));
}
static int NativeUtimes(const std::filesystem::path& path, const KernelTimeval* times) {
    if (times == nullptr) return ::utimes(path.c_str(), nullptr);
    struct timeval values[2]{{static_cast<time_t>(times[0].tv_sec), static_cast<suseconds_t>(times[0].tv_usec)},
        {static_cast<time_t>(times[1].tv_sec), static_cast<suseconds_t>(times[1].tv_usec)}};
    return ::utimes(path.c_str(), values);
}
static int NativeFlock(int descriptor, int operation) {
    return ::flock(descriptor, operation);
}
static std::int64_t NativePread(int descriptor, void* buf, std::size_t nbytes, std::int64_t offset) {
    return static_cast<std::int64_t>(::pread(descriptor, buf, nbytes, static_cast<off_t>(offset)));
}
static std::int64_t NativePwrite(int descriptor, const void* buf, std::size_t nbytes, std::int64_t offset) {
    return static_cast<std::int64_t>(::pwrite(descriptor, buf, nbytes, static_cast<off_t>(offset)));
}
#endif

static constexpr int GUEST_ENOENT = 2;
static constexpr int GUEST_EIO = 5;
static constexpr int GUEST_EFAULT = 14;
static constexpr int GUEST_EEXIST = 17;
static constexpr int GUEST_EINVAL = 22;
static constexpr int GUEST_ENAMETOOLONG = 63;
static constexpr int GUEST_ENOTDIR = 20;
static constexpr int GUEST_EROFS = 30;
static constexpr int GUEST_ENOTEMPTY = 66;

static int SceErrorFromErrno(int error) {
    return static_cast<int>(0x80020000u | static_cast<unsigned>(error > 0 && error <= 34 ? error : error == GUEST_ENOTEMPTY ? error : GUEST_EIO));
}

extern "C" {

int APS5_VABI chmod_nid_postfix(const char* path, int mode) {
    if (path == nullptr) {
        APS5_INVALID_ARG_EX;
    }
    auto native = ResolvePath_nid_no_patch(path);
    if (NativeChmod(native, mode) != 0) {
        throw std::runtime_error(std::string(__func__) + ": chmod failed for " + native.string() + ", errno=" + std::to_string(errno));
    }
    return 0;
}

int APS5_VABI close_nid_postfix(int d) {
    if (d >= GuestSockets::FirstDescriptor) return GuestSockets::Close(d);
#ifdef _WIN32
    ReadAhead::Forget(d);
    return _close(d);
#else
    return ::close(d);
#endif
}

int APS5_VABI _close_nid_postfix(int descriptor) {
    return close_nid_postfix(descriptor);
}

int APS5_VABI flock_nid_postfix(int d, int operation) {
    if (NativeFlock(d, operation) != 0) {
#ifdef _WIN32
        throw std::runtime_error(std::string(__func__) + ": flock failed, fd=" + std::to_string(d) + ", error=" + std::to_string(::GetLastError()));
#else
        throw std::runtime_error(std::string(__func__) + ": flock failed, fd=" + std::to_string(d) + ", errno=" + std::to_string(errno));
#endif
    }
    return 0;
}

int64_t APS5_VABI fstat_nid_disambig1_nid_postfix(int d, FileStat* sb) {
    if (sb == nullptr) {
        APS5_INVALID_ARG_EX;
    }
    File::FillFileStat(d, sb);
    return 0;
}

int APS5_VABI ftruncate_nid_postfix(int d, int64_t length) {
    if (length < 0) {
        APS5_INVALID_ARG_EX;
    }
#ifdef _WIN32
    int error = NativeFtruncate(d, length);
    if (error != 0) {
        throw std::runtime_error(std::string(__func__) + ": ftruncate failed, fd=" + std::to_string(d) + ", error=" + std::to_string(error));
    }
#else
    if (NativeFtruncate(d, length) != 0) {
        throw std::runtime_error(std::string(__func__) + ": ftruncate failed, fd=" + std::to_string(d) + ", errno=" + std::to_string(errno));
    }
#endif
    return 0;
}

int APS5_VABI sceKernelFtruncate(int d, int64_t length) {
    return ftruncate_nid_postfix(d, length);
}

int64_t APS5_VABI lseek_nid_postfix(int d, int64_t offset, int whence) {
    return static_cast<int64_t>(sceKernelLseek(d, offset, whence));
}

int APS5_VABI mkdir_nid_postfix(const char* path, uint16_t mode) {
    if (path == nullptr) {
        APS5_INVALID_ARG_EX;
    }
    auto native = ResolvePath_nid_no_patch(path);
    if (NativeMkdir(native, mode) != 0) {
        throw std::runtime_error(std::string(__func__) + ": mkdir failed for " + native.string() + ", errno=" + std::to_string(errno));
    }
    return 0;
}

int APS5_VABI open_nid_postfix(const char* path, int flags, int mode) {
    return sceKernelOpen(path, flags, static_cast<std::uint16_t>(mode));
}

int APS5_VABI _open_nid_postfix(const char* path, int flags, ...) {
    std::uint16_t mode = 0;
    if (flags & SCE_KERNEL_O_CREAT) {
#ifdef _WIN32
        __builtin_sysv_va_list arguments;
        __builtin_sysv_va_start(arguments, flags);
        mode = static_cast<std::uint16_t>(__builtin_va_arg(arguments, int));
        __builtin_sysv_va_end(arguments);
#else
        std::va_list arguments;
        va_start(arguments, flags);
        mode = static_cast<std::uint16_t>(va_arg(arguments, int));
        va_end(arguments);
#endif
    }
    return sceKernelOpen(path, flags, mode);
}

int64_t APS5_VABI pread_nid_postfix(int d, void* buf, size_t nbytes, int64_t offset) {
    if (buf == nullptr) {
        APS5_INVALID_ARG_EX;
    }
    if (offset < 0) {
        APS5_INVALID_ARG_EX;
    }
    KernelDiagScope diag(KernelDiagZone::FileRead, __builtin_return_address(0));
    auto n = NativePread(d, buf, nbytes, offset);
    if (n < 0) {
#ifdef _WIN32
        // GT7 reads from a file it has just opened write-only (CacheInfo.json); the PS4 answers
        // EBADF and the title carries on.
        const DWORD nativeError = ::GetLastError();
        const int guestError = nativeError == ERROR_ACCESS_DENIED || nativeError == ERROR_INVALID_HANDLE ? EBADF : GUEST_EIO;
        static std::atomic<unsigned> logged{0};
        if (logged.fetch_add(1) < 8) {
            wchar_t name[MAX_PATH] = L"?";
            const auto handle = reinterpret_cast<HANDLE>(::_get_osfhandle(d));
            if (handle != INVALID_HANDLE_VALUE) ::GetFinalPathNameByHandleW(handle, name, MAX_PATH, 0);
            std::fprintf(stderr, "[file] pread fd %d (%ls) offset 0x%llx, %zu bytes: error %lu, errno %d\n", d, name,
                         static_cast<unsigned long long>(offset), nbytes, nativeError, guestError);
        }
        errno = guestError;
        return -1;
#else
        throw std::runtime_error(std::string(__func__) + ": pread failed, fd=" + std::to_string(d) + ", errno=" + std::to_string(errno));
#endif
    }
    return n;
}

int64_t APS5_VABI pwrite_nid_disambig1_nid_postfix(int d, const void* buf, size_t nbytes, int64_t offset) {
    if (buf == nullptr) {
        APS5_INVALID_ARG_EX;
    }
    if (offset < 0) {
        APS5_INVALID_ARG_EX;
    }
    auto n = NativePwrite(d, buf, nbytes, offset);
    if (n < 0) {
        throw std::runtime_error(std::string(__func__) + ": pwrite failed, fd=" + std::to_string(d) + ", errno=" + std::to_string(errno));
    }
    return n;
}

int64_t APS5_VABI read_nid_postfix(int d, void* buf, uint64_t nbytes) {
    return sceKernelRead(d, buf, static_cast<size_t>(nbytes));
}

std::int64_t APS5_VABI _read_nid_postfix(int descriptor, void* buffer, std::size_t count) {
    return sceKernelRead(descriptor, buffer, count);
}

int64_t APS5_VABI write_nid_postfix(int d, const char* str, int64_t size) {
    if (size < 0) {
        APS5_INVALID_ARG_EX;
    }
    return sceKernelWrite(d, str, static_cast<size_t>(size));
}

std::int64_t APS5_VABI _write_nid_postfix(int descriptor, const void* buffer, std::size_t count) {
    return sceKernelWrite(descriptor, buffer, count);
}

int APS5_VABI stat_nid_postfix(const char* path, FileStat* sb) {
    return sceKernelStat(path, sb);
}

int APS5_VABI unlink_nid_postfix(const char* path) {
    return sceKernelUnlink(path);
}

int APS5_VABI sceKernelCheckReachability(const char* path) {
    if (path == nullptr) return SCE_KERNEL_ERROR_EINVAL;
    if (std::strlen(path) > 255) return SCE_KERNEL_ERROR_ENAMETOOLONG;
    std::error_code error;
    return std::filesystem::exists(ResolvePath_nid_no_patch(path), error) ? 0 : SCE_KERNEL_ERROR_ENOENT;
}

int APS5_VABI sceKernelFstat(int d, FileStat* sb) {
    if (sb == nullptr) throw std::invalid_argument("sceKernelFstat: sb is null");
    if (!File::FillFileStatFromDescriptor(d, sb)) return SceErrorFromErrno(errno);
    return 0;
}

int APS5_VABI sceKernelFsync(int fd) {
#ifdef _WIN32
 return ::_commit(fd);
#else
 return ::fsync(fd);
#endif
}

#ifdef _WIN32

int APS5_VABI sceKernelGetdirentries(int fd, char* buf, int nbytes, int64_t* basep) {
    if (buf == nullptr) return SceErrorFromErrno(GUEST_EFAULT);
    if (nbytes <= 0) return SceErrorFromErrno(GUEST_EINVAL);
    if (basep != nullptr) *basep = 0;
    const int written = File::ReadDirectoryDescriptor(fd, buf, nbytes);
    return written < 0 ? SceErrorFromErrno(GUEST_ENOTDIR) : written;
}

int APS5_VABI sceKernelGetdents(int fd, char* buf, int nbytes) {
    return sceKernelGetdirentries(fd, buf, nbytes, nullptr);
}

#else

int APS5_VABI sceKernelGetdirentries(int fd, char* buf, int nbytes, int64_t* basep) {
    constexpr std::size_t GuestHeaderBytes = 8;
    constexpr std::size_t GuestMaxName = 255;
    if (buf == nullptr) return SceErrorFromErrno(GUEST_EFAULT);
    if (nbytes <= 0) return SceErrorFromErrno(GUEST_EINVAL);
    const off_t base = ::lseek(fd, 0, SEEK_CUR);
    if (base < 0) return SceErrorFromErrno(errno);
    if (basep != nullptr) *basep = static_cast<int64_t>(base);
    std::vector<char> host(static_cast<std::size_t>(nbytes) < 4096 ? 4096 : static_cast<std::size_t>(nbytes));
    const auto read = ::syscall(SYS_getdents64, fd, host.data(), host.size());
    if (read < 0) return SceErrorFromErrno(errno);
    std::size_t written = 0;
    off_t resume = base;
    for (long offset = 0; offset < read;) {
        const auto* entry = reinterpret_cast<const struct dirent64*>(host.data() + offset);
        const auto nameLength = std::strlen(entry->d_name);
        const auto record = (GuestHeaderBytes + nameLength + 1 + 3) & ~std::size_t{3};
        if (nameLength > GuestMaxName || written + record > static_cast<std::size_t>(nbytes)) {
            if (::lseek(fd, resume, SEEK_SET) < 0) return SceErrorFromErrno(errno);
            if (written != 0) return static_cast<int>(written);
            return nameLength > GuestMaxName ? static_cast<int>(0x80020000u | GUEST_ENAMETOOLONG) : SceErrorFromErrno(GUEST_EINVAL);
        }
        char* out = buf + written;
        std::memset(out, 0, record);
        const auto fileNumber = static_cast<std::uint32_t>(entry->d_ino);
        const auto recordLength = static_cast<std::uint16_t>(record);
        const auto nameBytes = static_cast<std::uint8_t>(nameLength);
        std::memcpy(out, &fileNumber, sizeof(fileNumber));
        std::memcpy(out + 4, &recordLength, sizeof(recordLength));
        out[6] = static_cast<char>(entry->d_type);
        out[7] = static_cast<char>(nameBytes);
        std::memcpy(out + GuestHeaderBytes, entry->d_name, nameLength);
        written += record;
        resume = entry->d_off;
        offset += entry->d_reclen;
    }
    if (::lseek(fd, resume, SEEK_SET) < 0) return SceErrorFromErrno(errno);
    return static_cast<int>(written);
}

int APS5_VABI sceKernelGetdents(int fd, char* buf, int nbytes) {
    return sceKernelGetdirentries(fd, buf, nbytes, nullptr);
}

#endif

int APS5_VABI sceKernelMkdir(const char* path, uint16_t mode) {
    (void)mode;
    if (path == nullptr) throw std::invalid_argument("sceKernelMkdir: path is null");
    const auto native = ResolvePath_nid_no_patch(path);
    std::error_code error;
    if (std::filesystem::exists(native, error)) return SceErrorFromErrno(GUEST_EEXIST);
    if (!std::filesystem::exists(native.parent_path(), error)) return SceErrorFromErrno(GUEST_ENOENT);
    if (!std::filesystem::create_directory(native, error)) return SceErrorFromErrno(error.value() ? error.value() : GUEST_EIO);
    return 0;
}

#ifdef _WIN32

int64_t APS5_VABI sceKernelPread(int d, void* buf, size_t nbytes, int64_t offset) {
 const auto result = pread_nid_postfix(d, buf, nbytes, offset);
 return result < 0 ? SceErrorFromErrno(errno) : result;
}

int64_t APS5_VABI sceKernelPwrite(int d, const void* buf, size_t nbytes, int64_t offset) {
 return pwrite_nid_disambig1_nid_postfix(d, buf, nbytes, offset);
}

#else

int64_t APS5_VABI sceKernelPread(int d, void* buf, size_t nbytes, int64_t offset) {
    if (buf == nullptr && nbytes != 0) return SceErrorFromErrno(GUEST_EFAULT);
    if (offset < 0) return SceErrorFromErrno(GUEST_EINVAL);
    const auto result = NativePread(d, buf, nbytes, offset);
    return result < 0 ? SceErrorFromErrno(errno) : result;
}

int64_t APS5_VABI sceKernelPwrite(int d, const void* buf, size_t nbytes, int64_t offset) {
    if (buf == nullptr && nbytes != 0) return SceErrorFromErrno(GUEST_EFAULT);
    if (offset < 0) return SceErrorFromErrno(GUEST_EINVAL);
    const auto result = NativePwrite(d, buf, nbytes, offset);
    return result < 0 ? SceErrorFromErrno(errno) : result;
}

#endif

int APS5_VABI sceKernelRename(const char* from, const char* to) {
    if (from == nullptr || to == nullptr) throw std::invalid_argument("sceKernelRename: path is null");
    const auto source = ResolvePath_nid_no_patch(from);
    const auto destination = ResolvePath_nid_no_patch(to);
    if (IsReadOnlyGamePath(source, "rename", from) || IsReadOnlyGamePath(destination, "rename to", to)) return SceErrorFromErrno(GUEST_EROFS);
    std::error_code error;
    if (!std::filesystem::exists(source, error)) return SceErrorFromErrno(GUEST_ENOENT);
    std::filesystem::rename(source, destination, error);
    if (error) return SceErrorFromErrno(GUEST_EIO);
    return 0;
}

int APS5_VABI sceKernelRmdir(const char* path) {
    if (path == nullptr) throw std::invalid_argument("sceKernelRmdir: path is null");
    const auto native = ResolvePath_nid_no_patch(path);
    if (IsReadOnlyGamePath(native, "rmdir", path)) return SceErrorFromErrno(GUEST_EROFS);
    std::error_code error;
    if (!std::filesystem::is_directory(native, error)) return SceErrorFromErrno(std::filesystem::exists(native, error) ? GUEST_ENOTDIR : GUEST_ENOENT);
    if (!std::filesystem::is_empty(native, error)) return SceErrorFromErrno(GUEST_ENOTEMPTY);
    if (!std::filesystem::remove(native, error)) return SceErrorFromErrno(GUEST_EIO);
    return 0;
}

int APS5_VABI rmdir_nid_postfix(const char* path) {
    return sceKernelRmdir(path);
}

}

extern "C" {

int APS5_VABI sceKernelChmod_nid_postfix(const char* path, std::uint16_t mode) {
    return chmod_nid_postfix(path, mode);
}

int APS5_VABI sceKernelTruncate_nid_postfix(const char* path, std::int64_t length) {
    if (path == nullptr) throw std::invalid_argument("sceKernelTruncate: path is null");
    if (length < 0) return SceErrorFromErrno(GUEST_EINVAL);
    const auto native = ResolvePath_nid_no_patch(path);
    std::error_code error;
    if (!std::filesystem::exists(native, error)) return SceErrorFromErrno(GUEST_ENOENT);
    std::filesystem::resize_file(native, static_cast<std::uintmax_t>(length), error);
    if (error) return SceErrorFromErrno(GUEST_EIO);
    return 0;
}

int APS5_VABI sceKernelUtimes_nid_postfix(const char* path, const KernelTimeval* times) {
    if (path == nullptr) throw std::invalid_argument("sceKernelUtimes: path is null");
    const auto native = ResolvePath_nid_no_patch(path);
    if (NativeUtimes(native, times) != 0) return SceErrorFromErrno(errno);
    return 0;
}

}
