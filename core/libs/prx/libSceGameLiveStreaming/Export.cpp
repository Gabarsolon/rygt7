#include <cstdint>
#include <cstddef>
#include "SceTypes.hpp"
#include "prx/libc/include/General.hpp"

extern "C" {

int APS5_VABI sceGameLiveStreamingGetCurrentStatus2() {
    return 0;
}

int APS5_VABI sceGameLiveStreamingSetMaxBitrate() {
    return 0;
}

int APS5_VABI sceGameLiveStreamingLaunchLiveViewerA() {
    return 0;
}

int APS5_VABI sceGameLiveStreamingInitialize(size_t heap_size) {
    // Newer SDKs may pass no heap size; live streaming is not provided either way.
    (void)heap_size;
    return 0;
}

int APS5_VABI sceGameLiveStreamingTerminate(void) {
    return 0;
}

}

// Offline-only APIs: live streaming and viewer services are unavailable.
extern "C" {
int APS5_VABI sceGameLiveStreamingApplySocialFeedbackMessageFilter() { return 0; }
int APS5_VABI sceGameLiveStreamingCheckCallback() { return 0; }
int APS5_VABI sceGameLiveStreamingClearPresetSocialFeedbackCommands() { return 0; }
int APS5_VABI sceGameLiveStreamingClearSocialFeedbackMessages() { return 0; }
int APS5_VABI sceGameLiveStreamingClearSpoilerTag() { return 0; }
int APS5_VABI sceGameLiveStreamingEnableLiveStreaming() { return 0; }
int APS5_VABI sceGameLiveStreamingEnableSocialFeedback() { return 0; }
int APS5_VABI sceGameLiveStreamingGetCurrentBroadcastScreenLayout() { return 0; }
int APS5_VABI sceGameLiveStreamingGetCurrentStatus() { return 0; }
int APS5_VABI sceGameLiveStreamingGetProgramInfo() { return 0; }
int APS5_VABI sceGameLiveStreamingGetSocialFeedbackMessages() { return 0; }
int APS5_VABI sceGameLiveStreamingGetSocialFeedbackMessagesCount() { return 0; }
int APS5_VABI sceGameLiveStreamingLaunchLiveViewer() { return 0; }
int APS5_VABI sceGameLiveStreamingPermitLiveStreaming() { return 0; }
int APS5_VABI sceGameLiveStreamingPermitServerSideRecording() { return 0; }
int APS5_VABI sceGameLiveStreamingPostSocialMessage() { return 0; }
int APS5_VABI sceGameLiveStreamingRegisterCallback() { return 0; }
int APS5_VABI sceGameLiveStreamingScreenCloseSeparateMode() { return 0; }
int APS5_VABI sceGameLiveStreamingScreenConfigureSeparateMode() { return 0; }
int APS5_VABI sceGameLiveStreamingScreenInitialize() { return 0; }
int APS5_VABI sceGameLiveStreamingScreenInitializeSeparateModeParameter() { return 0; }
int APS5_VABI sceGameLiveStreamingScreenOpenSeparateMode() { return 0; }
int APS5_VABI sceGameLiveStreamingScreenSetMode() { return 0; }
int APS5_VABI sceGameLiveStreamingScreenTerminate() { return 0; }
int APS5_VABI sceGameLiveStreamingSetCameraFrameSetting() { return 0; }
int APS5_VABI sceGameLiveStreamingSetDefaultServiceProviderPermission() { return 0; }
int APS5_VABI sceGameLiveStreamingSetGuardAreas() { return 0; }
int APS5_VABI sceGameLiveStreamingSetInvitationSessionId() { return 0; }
int APS5_VABI sceGameLiveStreamingSetLinkCommentPreset() { return 0; }
int APS5_VABI sceGameLiveStreamingSetMetadata() { return 0; }
int APS5_VABI sceGameLiveStreamingSetPresetSocialFeedbackCommands() { return 0; }
int APS5_VABI sceGameLiveStreamingSetPresetSocialFeedbackCommandsDescription() { return 0; }
int APS5_VABI sceGameLiveStreamingSetServiceProviderPermission() { return 0; }
int APS5_VABI sceGameLiveStreamingSetSpoilerTag() { return 0; }
int APS5_VABI sceGameLiveStreamingSetStandbyScreenResource() { return 0; }
int APS5_VABI sceGameLiveStreamingStartDebugBroadcast() { return 0; }
int APS5_VABI sceGameLiveStreamingStartGenerateStandbyScreenResource() { return 0; }
int APS5_VABI sceGameLiveStreamingStartSocialFeedbackMessageFiltering() { return 0; }
int APS5_VABI sceGameLiveStreamingStopDebugBroadcast() { return 0; }
int APS5_VABI sceGameLiveStreamingStopGenerateStandbyScreenResource() { return 0; }
int APS5_VABI sceGameLiveStreamingStopSocialFeedbackMessageFiltering() { return 0; }
int APS5_VABI sceGameLiveStreamingUnregisterCallback() { return 0; }

}

