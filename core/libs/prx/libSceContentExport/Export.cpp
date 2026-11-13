#include <cstdint>
#include <cstddef>
#include "SceTypes.hpp"
#include "prx/libc/include/General.hpp"
#include <algorithm>
#include <atomic>
#include <cerrno>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <ctime>
#include <exception>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <set>
#include <stdexcept>
#include <string>
#include <string_view>
#include <system_error>
#include <thread>
#include <vector>

// FEAT-GT7-161: on a PS4, sceContentExportFromData puts a picture in the system's Capture Gallery.
// GT7's gallery "Export" converts a Scapes or race photo to an SDR JPEG (or PNG) and hands it over
// this way. Here the picture is written to the Photos folder next to the game, where players can
// open it; RYGT_PHOTO_EXPORT=0 refuses the export instead (the game then reports a failure).

namespace {

// This library's own error codes are not known; kernel errno codes are used. GT7 only tests for 0.
constexpr int ERROR_BAD_HANDLE = static_cast<int>(0x80020009);   // EBADF
constexpr int ERROR_IO = static_cast<int>(0x80020005);           // EIO
constexpr int ERROR_NOT_SUPPORTED = static_cast<int>(0x8002002D); // EOPNOTSUPP

constexpr char EXPORT_FOLDER[] = "/Photos";
constexpr std::size_t MAX_TITLE_BYTES = 80;
constexpr std::size_t MAX_EXPORT_BYTES = std::size_t{1} << 30;
// GT7.exe does not opt into long paths, so Windows refuses a file path of 260 characters or more.
constexpr std::size_t MAX_PATH_CHARS = 259;
constexpr char LONGEST_COPY_SUFFIX[] = " (999)";
constexpr char PART_SUFFIX[] = ".part";
constexpr int RENAME_ATTEMPTS = 5;

std::atomic<bool> g_initialized{false};
std::mutex g_lock;       // the handles
std::mutex g_file_lock;  // from choosing a free name to renaming into it, so two exports never share one
std::set<int> g_handles;
int g_next_handle = 1;

bool ExportEnabled() {
    static const bool enabled = [] {
        const char* value = std::getenv("RYGT_PHOTO_EXPORT");
        return value == nullptr || std::strcmp(value, "0") != 0;
    }();
    return enabled;
}

// Length of the valid UTF-8 sequence at text[i], or 0: no overlong forms, surrogates or code points
// above U+10FFFF, which libstdc++ refuses to turn into a Windows path.
std::size_t Utf8Length(std::string_view text, std::size_t i) {
    const auto lead = static_cast<unsigned char>(text[i]);
    std::size_t length = 0;
    if (lead < 0x80) return 1;
    if (lead >= 0xC2 && lead <= 0xDF) length = 2;
    else if (lead >= 0xE0 && lead <= 0xEF) length = 3;
    else if (lead >= 0xF0 && lead <= 0xF4) length = 4;
    else return 0;
    if (i + length > text.size()) return 0;
    for (std::size_t k = 1; k < length; ++k)
        if ((static_cast<unsigned char>(text[i + k]) & 0xC0) != 0x80) return 0;
    const auto second = static_cast<unsigned char>(text[i + 1]);
    if ((lead == 0xE0 && second < 0xA0) || (lead == 0xED && second > 0x9F) ||
        (lead == 0xF0 && second < 0x90) || (lead == 0xF4 && second > 0x8F)) return 0;
    return length;
}

// The title as a Windows-safe file name part: UTF-8 kept, characters Windows refuses and invalid
// bytes become spaces, runs of spaces are merged, at most max_bytes bytes, no trailing dots.
std::string FileTitle(const char* title, std::size_t capacity, std::size_t max_bytes) {
    const std::string_view text(title, title ? strnlen(title, capacity) : 0);
    std::string out;
    for (std::size_t i = 0; i < text.size();) {
        const std::size_t length = Utf8Length(text, i);
        const auto c = static_cast<unsigned char>(text[i]);
        const bool refused = length == 0 || c < 0x20 || c == 0x7F || (length == 1 && std::strchr("<>:\"/\\|?*", c));
        if (refused) {
            if (!out.empty() && out.back() != ' ') out += ' ';
            i += length == 0 ? 1 : length;
            continue;
        }
        if (c == ' ' && (out.empty() || out.back() == ' ')) { ++i; continue; }
        if (out.size() + length > max_bytes) break;
        out.append(text.substr(i, length));
        i += length;
    }
    while (!out.empty() && (out.back() == ' ' || out.back() == '.')) out.pop_back();
    return out;
}

// How many bytes of title fit so that the longest path this export can use, "<folder>\<stem>
// <title> (999)<ext>.part", stays under MAX_PATH. A UTF-8 byte count is never below the UTF-16 count
// Windows checks, so the title may come out shorter than needed, never longer.
std::size_t TitleBudget(const std::filesystem::path& folder, std::size_t stem, std::size_t extension) {
    std::error_code error;
    const auto absolute = std::filesystem::absolute(folder, error);
    const std::size_t used = (error ? folder : absolute).native().size() + 1 + stem + 1 +
                             (sizeof(LONGEST_COPY_SUFFIX) - 1) + extension + (sizeof(PART_SUFFIX) - 1);
    return used >= MAX_PATH_CHARS ? 0 : std::min(MAX_TITLE_BYTES, MAX_PATH_CHARS - used);
}

std::string Extension(const char* content_type, std::size_t capacity) {
    const std::string_view type(content_type, content_type ? strnlen(content_type, capacity) : 0);
    if (type == "image/jpeg") return ".jpg";
    if (type == "image/png") return ".png";
    return ".bin";
}

std::string Timestamp() {
    const std::time_t now = std::time(nullptr);
    std::tm local{};
#ifdef _WIN32
    if (localtime_s(&local, &now) != 0) return "export";
#else
    if (localtime_r(&now, &local) == nullptr) return "export";
#endif
    char text[32];
    if (std::strftime(text, sizeof(text), "%Y-%m-%d %H-%M-%S", &local) == 0) return "export";
    return text;
}

std::filesystem::path Utf8Path(const std::string& text) {
    return std::filesystem::path(std::u8string(reinterpret_cast<const char8_t*>(text.data()), text.size()));
}

std::string Utf8Text(const std::filesystem::path& path) {
    const auto text = path.u8string();
    return std::string(reinterpret_cast<const char*>(text.data()), text.size());
}

// "<stem><ext>", or "<stem> (2)<ext>", " (3)"... when that name or its .part file is taken.
std::string FreeName(const std::filesystem::path& folder, const std::string& stem, const std::string& extension) {
    std::error_code error;
    for (int copy = 1; copy < 1000; ++copy) {
        const std::string name = copy == 1 ? stem + extension : stem + " (" + std::to_string(copy) + ")" + extension;
        if (std::filesystem::exists(folder / Utf8Path(name), error) || error) continue;
        if (std::filesystem::exists(folder / Utf8Path(name + PART_SUFFIX), error) || error) continue;
        return name;
    }
    return {};
}

// Reads exactly `size` bytes through the caller's function (GT7's hands over its whole buffer on
// every call). False on an error from the function or when it stops giving data before the end.
bool ReadAll(ContentExportReadFunc read, void* user_data, std::size_t size, std::vector<char>& data) {
    data.reserve(size);
    while (data.size() < size) {
        void* piece = nullptr;
        std::size_t length = 0;
        if (read(&piece, &length, user_data) < 0 || piece == nullptr || length == 0) return false;
        const std::size_t take = std::min(length, size - data.size());
        const auto* bytes = static_cast<const char*>(piece);
        data.insert(data.end(), bytes, bytes + take);
    }
    return true;
}

int Fail(const char* what, const std::filesystem::path& path, int code, const std::error_code& reason = {}) {
    if (reason)
        std::fprintf(stderr, "[content export] FEAT-GT7-161: %s %s (%s)\n", what, Utf8Text(path).c_str(), reason.message().c_str());
    else
        std::fprintf(stderr, "[content export] FEAT-GT7-161: %s %s\n", what, Utf8Text(path).c_str());
    return code;
}

int Export(const ContentExportParam& param, std::size_t size, ContentExportReadFunc read, void* user_data,
           char* exported_path, std::size_t exported_path_size) {
    const std::filesystem::path folder = ResolvePath_nid_no_patch(EXPORT_FOLDER);
    if (!ExportEnabled()) return Fail("export refused (RYGT_PHOTO_EXPORT=0) for", folder, ERROR_NOT_SUPPORTED);
    if (size == 0 || size > MAX_EXPORT_BYTES) return Fail("the game gave no usable size for", folder, ERROR_IO);

    std::vector<char> data;
    if (!ReadAll(read, user_data, size, data)) return Fail("the game handed over incomplete data for", folder, ERROR_IO);

    std::lock_guard guard(g_file_lock);
    std::error_code error;
    std::filesystem::create_directories(folder, error);
    if (error) return Fail("cannot create", folder, ERROR_IO, error);

    const std::string extension = Extension(param.content_type, sizeof(param.content_type));
    std::string stem = Timestamp();
    const std::string title = FileTitle(param.title, sizeof(param.title), TitleBudget(folder, stem.size(), extension.size()));
    if (!title.empty()) stem += " " + title;
    const std::string name = FreeName(folder, stem, extension);
    if (name.empty()) return Fail("no free file name in", folder, ERROR_IO);
    const auto target = folder / Utf8Path(name);
    auto partial = target;
    partial += PART_SUFFIX;

    {
        errno = 0;
        std::ofstream file(partial, std::ios::binary | std::ios::trunc);
        if (file) file.write(data.data(), static_cast<std::streamsize>(data.size()));
        if (file) file.close();
        if (!file) {
            const std::error_code reason(errno, std::generic_category());
            std::error_code ignored;
            std::filesystem::remove(partial, ignored);
            return Fail("cannot write", partial, ERROR_IO, reason);
        }
    }
    // An antivirus or indexer may still hold the new file for a moment: try again before giving up,
    // and then keep the picture as the .part file rather than lose it.
    for (int attempt = 1;; ++attempt) {
        std::filesystem::rename(partial, target, error);
        if (!error) break;
        if (attempt == RENAME_ATTEMPTS) return Fail("cannot rename, the picture is kept as", partial, ERROR_IO, error);
        std::this_thread::sleep_for(std::chrono::milliseconds(50));
    }

    const std::string guest = std::string(EXPORT_FOLDER) + "/" + name;
    if (exported_path != nullptr && exported_path_size != 0) {
        const std::size_t length = std::min(guest.size(), exported_path_size - 1);
        std::memcpy(exported_path, guest.data(), length);
        exported_path[length] = '\0';
    }
    std::fprintf(stderr, "[content export] FEAT-GT7-161: %zu bytes saved as %s\n", data.size(), Utf8Text(target).c_str());
    return 0;
}

} // namespace

extern "C" {

int APS5_VABI sceContentExportInit2(const ContentExportInitParam2* init_param) {
    if (init_param == nullptr) APS5_INVALID_ARG_EX;
    if (init_param->malloc_func == nullptr || init_param->free_func == nullptr) throw std::invalid_argument(std::string(__func__) + ": missing allocator functions");
    if (init_param->reserved0 != 0 || init_param->reserved1 != 0) throw std::invalid_argument(std::string(__func__) + ": reserved fields are not zero");
    bool expected = false;
    if (!g_initialized.compare_exchange_strong(expected, true)) throw std::logic_error(std::string(__func__) + ": already initialized");
    return 0;
}

// Returns the handle (GT7 calls it without arguments and passes the result on).
int APS5_VABI sceContentExportStart(void) {
    if (!g_initialized.load()) throw std::logic_error(std::string(__func__) + ": not initialized");
    std::lock_guard guard(g_lock);
    const int handle = g_next_handle;
    g_next_handle = g_next_handle == 0x7FFFFFFF ? 1 : g_next_handle + 1;
    g_handles.insert(handle);
    return handle;
}

int APS5_VABI sceContentExportFromData(int handle, const ContentExportParam* param, std::size_t size,
                                       ContentExportReadFunc read, void* user_data, char* exported_path,
                                       std::size_t exported_path_size) {
    if (param == nullptr || read == nullptr) APS5_INVALID_ARG_EX;
    {
        std::lock_guard guard(g_lock);
        if (!g_handles.contains(handle)) return ERROR_BAD_HANDLE;
    }
    if (exported_path != nullptr && exported_path_size != 0) exported_path[0] = '\0';
    // A C++ exception cannot be unwound through the game's code: any failure becomes an error code.
    try {
        return Export(*param, size, read, user_data, exported_path, exported_path_size);
    } catch (const std::exception& e) {
        std::fprintf(stderr, "[content export] FEAT-GT7-161: export failed: %s\n", e.what());
    } catch (...) {
        std::fprintf(stderr, "[content export] FEAT-GT7-161: export failed\n");
    }
    return ERROR_IO;
}

int APS5_VABI sceContentExportFinish(int handle) {
    std::lock_guard guard(g_lock);
    return g_handles.erase(handle) != 0 ? 0 : ERROR_BAD_HANDLE;
}

int APS5_VABI sceContentExportFromFile(void) {
 NotImplemented_nid_no_patch(__func__);
 return 0;
}

int APS5_VABI sceContentExportFromFileWithThumbnail(void) {
 NotImplemented_nid_no_patch(__func__);
 return 0;
}

int APS5_VABI sceContentExportTerm(void) {
    bool expected = true;
    if (!g_initialized.compare_exchange_strong(expected, false)) throw std::logic_error(std::string(__func__) + ": not initialized");
    std::lock_guard guard(g_lock);
    g_handles.clear();
    return 0;
}

}
