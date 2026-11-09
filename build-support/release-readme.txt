ryGT7: Gran Turismo 7 (CUSA24769) as a native Windows program
{name} - github.com/RyGT-Team/rygt7

ryGT7 is experimental software derived from ryGT (Gran Turismo Sport) and Ryty, with the
shadGT7 renderer (derived from shadGT and shadPS4). This is a prerelease: crashes,
rendering errors and shader compilation stutter happen. Nothing in this archive comes from
the game or a PS4: you provide your own decrypted dump, firmware modules and fonts.

Quick start
  1. Extract this folder to a writable place on the SAME DRIVE as your game dump (the game's
     files are linked into it, not copied; on another drive Windows Developer Mode must be
     on). Keep it separate from the game and update folders.
  2. Run RyGT7.exe and pick:
       Base game (CUSA24769)   your decrypted base game folder: eboot.bin, sce_sys\param.sfo,
                               sce_module and contents (gt.idx and the gt*.vol archives)
       Update                  your decrypted update folder (with its own eboot.bin and
                               sce_sys); leave it empty when the update is already merged
                               into the game folder. A separate update overrides the base
                               game file by file.
       System modules          a folder with your PS4 firmware's decrypted modules
                               (libSceFont, libSceFontFt, libSceFreeTypeOt, libSceJson2,
                               libSceNgs2, and libSceJpegDec and libScePngEnc if you have
                               them; .sprx or .prx, searched in subfolders too)
       Fonts                   a folder with your PS4 firmware's font and font2 folders
  3. Press Set up. It converts the game for Windows (a minute or two), gives GT7.exe the
     game's own icon, links the game files into app0 and copies the fonts. Its log is
     setup.log.
  4. Press Play. Logs are written under logs\.

Verified: the CUSA24769 dump whose metadata reports APP_VER 01.00, converted in a clean
folder, to the main menu, Music Rally and races at Deep Forest. Other updates can be
selected but have not been verified to convert or play. Keep the game and update folders in
place after Set up: their files are linked, never patched or copied.

State of this build ({date})
  - Menus, World Map, Music Rally and races run. Races are paced to an even 30 FPS by
    default; the launcher's Graphics page offers 60. Audio works.
  - New in 2026-10-30: a redesigned launcher (a home view and a settings view) and online play
    (hosting and joining rooms through the launcher's Online page, by WackyHacky; online play
    needs the host's ports forwarded); fewer race hitches from shader compiles (background
    compiles follow the race frame rate and draws on screen are built first, by WillScarlett);
    Ryty v1.1.0 (relinker checks, AMD and Intel CPU instruction coverage); Discord shows the
    official Gran Turismo 7 game; Set up says when the game dump or system modules must be
    dumped again; Play and Set up wait while the DLSS files download.
  - New in 2026-10-29b: GT7 always uses the native renderer; the launcher's settings are
    read from any folder path; a "protect region as write-only" crash during play is fixed;
    AMD GPUs get the BC6H textures they refused to create (they were missing); far less log
    spam; Set up no longer leaves a startable GT7.exe in convert\runtime-*.
  - New in 2026-10-29: the black car at the start of a race is fixed (the hull shaders now
    see the other patches' control points they read on PS4, and sample the occlusion
    pyramid's mip levels at their own size); the Upscaling page with an NVIDIA DLSS
    download button and quality list; cars' shaders compile first on a new shader cache.
  - Fixed in earlier builds: crowds flickering black or jumping (a shader compiler error made
    the crowd's GPU writes overlap), the black pit building and other missing streamed
    textures at Deep Forest, the title screen flicker, black cockpit interiors, the
    colour glare around the player's car, the green night sky at Yas Marina, the
    flickering dark shadow band at Alsace, the empty cockpit rear-view mirror, the seam
    in the sky, and frame stutter from GT7's per-frame memory being tracked page by page.
  - Known problems: occasional crashes, intermittent GPU device loss (NVIDIA driver reset),
    black wedges in some replay and flyover shots, cars black or missing for a few seconds in
    the first race on a new shader cache (a long load the first time a track is visited), background mountains missing from some angles at Deep Forest, busy race
    sections below 30 FPS. The AMD graphics fix can crash RX 6000 series GPUs when loading
    into a race: if the game crashes when a race loads, turn it off. Online play is new and was
    tested by its author only.

Options (RyGT7.exe)
  Display mode and resolution; Upscaling (Off, AMD FSR 1, AMD FSR 3.1 or NVIDIA DLSS, with a
  quality list that sets the size the 1920x1080 picture is scaled to) and Frame generation
  (AMD FSR 3.1, or NVIDIA DLSS on RTX 40/50). NVIDIA's DLSS files are not included: the
  Graphics page's "Download NVIDIA DLSS" button fetches NVIDIA's own signed files from
  NVIDIA's GitHub, checked against pinned SHA-256 hashes and NVIDIA's signature. Also
  texture sharpness, controller bindings and ports, steering wheels, shader compilation mode, Discord presence
  and logging. GT Sport's livery, shader-pack and renderer patch options do not apply to GT7
  and are gone. Paths are kept in RyGT7.ini next to the launcher; the runtime's settings in
  user\custom_configs\CUSA03220.json (the renderer's fixed configuration name).

Crash reports (GPU device lost)
  If the game stops with an NVIDIA device loss, start the launcher from a command prompt
  after "set RYGT_DEVICE_FAULT=1" (for that launch only). The log in user\log then
  ends with DIAG-243 lines (the driver's fault report and the last draws); please attach
  that log to the report. Without the variable nothing changes.

Offline mode patch (included)
  The Used Cars and Legendary Cars dealerships need the community "Offline mode" patch
  (Proper Offline Patch v1.0, ps-patch-system XML for CUSA24769). This release includes
  patches\CUSA24769.xml: Set up automatically applies the file's Offline mode group, and no
  other group, to the converted GT7.exe. The dealerships' catalog is then written to
  data\gt7offline. Set up only applies it to the verified dump's executable and stops
  otherwise. No separate patch download or copy is needed. Source and credits are in
  patches\README.md. Car thumbnails in the dealerships stay grey.

Files and licenses
  RyGT7.exe is the launcher, converter\ryty.exe the converter, libs\ the runtime libraries
  including the shadGT7 renderer (libSceVideoOut.prx); the GCC runtime DLLs and AMD
  FidelityFX (amd_fidelityfx_vk.dll) are redistributed with their notices. GPL version 2
  applies to ryGT7 and shadGT7: LICENSE and the licenses folder hold the notices of
  everything shipped, including the statically linked open-source dependencies.
  manifest.json lists every file with its SHA-256 and the source commits this build came from.
  Source: github.com/RyGT-Team/rygt7 and github.com/RyGT-Team/shadgt7.

  Gran Turismo is a trademark of Sony Interactive Entertainment. This project is independent
  of Sony and Polyphony Digital. No game data, firmware, fonts, saves or shader caches are
  included.
