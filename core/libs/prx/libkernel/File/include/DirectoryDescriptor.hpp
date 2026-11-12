#ifndef CORE_LIBS_PRX_LIBKERNEL_FILE_DIRECTORYDESCRIPTOR_HPP
#define CORE_LIBS_PRX_LIBKERNEL_FILE_DIRECTORYDESCRIPTOR_HPP

#include <filesystem>
#include <optional>
#include <utility>
#include <vector>

namespace File {

int OpenDirectoryDescriptor(const std::filesystem::path& path);
// FEAT-GTS-251: a folder under /app0 made of several host folders, listed from `entries`
// (host path, is a folder) instead of from `path`.
int OpenDirectoryDescriptor(const std::filesystem::path& path,
                            std::vector<std::pair<std::filesystem::path, bool>> entries);
std::optional<std::filesystem::path> DirectoryDescriptorPath(int fd);
void ForgetDirectoryDescriptor(int fd);
int ReadDirectoryDescriptor(int fd, char* buf, int nbytes);

}

#endif
