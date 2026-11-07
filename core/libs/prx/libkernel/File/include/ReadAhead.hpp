#pragma once
#ifdef _WIN32
namespace ReadAhead {
void Register(int descriptor, const char* path, int flags) noexcept;
void Forget(int descriptor);
}
#endif
