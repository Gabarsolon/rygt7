#include <cstdint>
#include <cstddef>
#include <stdexcept>
#include <string>
#include "SceTypes.hpp"
#include "prx/libc/include/General.hpp"

constexpr int REMOTEPLAY_CONNECTION_STATUS_DISCONNECT = 0;

extern "C" {

int APS5_VABI sceRemoteplayGetConnectionStatus(int user_id, int* status) {
 (void)user_id;
 if (!status) APS5_INVALID_ARG_EX;
 *status = REMOTEPLAY_CONNECTION_STATUS_DISCONNECT;
 return 0;
}

int APS5_VABI sceRemoteplayInitialize(void* heap, size_t heap_size) {
 // GT7 (a newer SDK) initializes without a heap; Remote Play is not provided either way.
 (void)heap;
 (void)heap_size;
 return 0;
}

int APS5_VABI sceRemoteplayTerminate(void) {
 return 0;
}

}
