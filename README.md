# ryGT7

An experimental Windows porting runtime for **Gran Turismo 7 (PS4, CUSA24769)**, derived from
[ryGT](https://github.com/mlgprorektm8/RyGT) and [Ryty](https://github.com/aarvsn/Ryty).
Rendering uses [shadGT7](https://gitlab.com/rygt-team/shadgt7), derived from shadGT/shadPS4.
The converter prepares the player's executable locally; game code runs on the host CPU and graphics
are translated to Vulkan.

## Download and set up

Download the Windows archive from [Releases](https://gitlab.com/rygt-team/rygt7/-/releases).
Extract the whole archive to a writable folder and run **RyGT7.exe**.

You provide:

- Your decrypted **CUSA24769 base game**, with `eboot.bin`, `sce_sys`, `sce_module`, and `contents`.
- An extracted update, either already merged into the base game or in a separate folder.
- Your PS4 firmware's system modules and the fonts folder containing `font` and `font2`.

Select those folders, press **Set up**, wait for conversion, then press **Play**. Leave Update empty
when your game folder is already merged. Separate updates override matching files individually,
preserving base files the update does not replace. Keep the source folders after setup because
game data is linked rather than copied. Use the same drive, or Windows Developer Mode for cross-drive links.

The archive contains no game dump, converted game executable, firmware, fonts, saves, or shader cache.
Python is not required for player setup. Windows x64, a Vulkan-capable GPU, and current graphics
drivers are required. Read [the launcher guide](loader/README.md) for the exact folder layout.

## Alpha status

Verified locally: clean-folder conversion and launch to the GT7 main menu, and racing at Deep Forest.
The tested dump reports `APP_VER=01.00` in its metadata. Other updates and hardware are unverified;
folder-selection support does not guarantee that every update converts or plays correctly.
Crashes, rendering errors, and shader compilation stutter remain possible. Online functionality is
not established by these tests.

This first prerelease fixes crowd instances flickering black or jumping because GPU output ranges
overlapped, and black streamed textures on Deep Forest's pit building. Validation included 512
shader-lowering checks, GPU captures with no remaining overlapping crowd writes, and 60-frame
crowd recordings without the large black flashes.

The second prerelease adds a fix for flickering night shadows at Yas Marina: cached render targets
are no longer reused after the game remaps their memory, which left an empty shadow map on
alternate frames. Packed-float render targets are also cleared and filled correctly. This fix is
not yet confirmed in game.

The current source also carries the Alsace shadow band and rear-view mirror fixes, the optional
Offline mode patch step in Set up (see the release notes and [the launcher guide](loader/README.md)),
and the racing wheel, ultrawide and frame generation work described in the next section.

The launcher retains display, FSR, optional DLSS, input bindings, controller ports, wheel, shader
compilation, and logging controls. GT Sport-specific script preparation and graphics patches are
removed from its interface. DLSS's separate plugin and NVIDIA files are not bundled; see
[the optional plugin instructions](plugins/dlss/README.md). AMD FSR is available in the supplied renderer.

## Racing wheels, ultrawide and frame generation

These features come from the wheel + ultrawide edition by dessierror, merged into this repository.
They need the renderer named in [build-support/renderer.json](build-support/renderer.json); a release
built from an older renderer pin lacks the ultrawide HUD, DLSS frame multiplier and fast flips.

### Racing wheels

- Your wheel appears to GT7 as a Fanatec direct-drive wheel, so steering, pedals, handbrake, shifter,
  clutch and force feedback work in races.
- Tested with a **Moza R3**. Logitech, Thrustmaster and Simagic wheels are also detected; wheels with
  force feedback are picked first.
- Force feedback is tuned in `ryty-wheel.ini` (see [docs/user/INPUT_MAPPING.md](docs/user/INPUT_MAPPING.md)):

  | Setting | What it does |
  |---|---|
  | `FfbGain` | overall strength (lower it if the wheel feels too heavy) |
  | `FfbDamping` | steadiness on straights |
  | `FfbDetail` | kerbs and road texture |

- `tools\ryty-wheel-probe.exe` lists the wheels and buttons Windows can see, if yours isn't picked up.
  A prebuilt copy is kept in [tools](tools); its source is
  [core/libs/prx/libScePad/tools/WheelProbe.cpp](core/libs/prx/libScePad/tools/WheelProbe.cpp).

### Ultrawide

- The camera widens to your screen's shape instead of stretching a 16:9 picture.
- The HUD stays at 16:9 in the centre, so nothing is stretched or cut off.
- **Press F7 in a race** to open the HUD editor:
  - drag any piece (lap, lap time, track map, position, race time, dashboard, course map) anywhere on screen;
  - drag a corner to resize it;
  - **Save** keeps your layout; **Reset all** restores the default.

  Menus are left alone.
- To turn it on, open the launcher's **Display** page and pick an **Ultrawide** setting:

  | Ultrawide | Shape | Resolution it selects |
  |---|---|---|
  | Off (16:9) | 16:9, as the game was made | unchanged |
  | Auto (screen shape) | your window's or screen's shape | unchanged |
  | 21:9 – 2560 x 1080 | 2560:1080 | 2560 x 1080 |
  | 21:9 – 3440 x 1440 | 3440:1440 | 3440 x 1440 |
  | 24:10 – 3840 x 1600 | 3840:1600 | 3840 x 1600 |
  | 32:9 – 5120 x 1440 | 5120:1440 | 5120 x 1440 |

  With **Borderless fullscreen**, **Auto** fits any ultrawide screen. A preset is for a window
  of that exact size, or for forcing a shape. It is stored as `"aspect_ratio"` in
  `user\custom_configs\CUSA03220.json`, which also takes any `width:height`.

### Performance

- On Intel 12th–14th gen and Core Ultra CPUs, the game's busiest threads are kept on the fast P-cores
  with Windows power throttling off. Set the environment variable `RYGT_PCORES=0` to disable this.
- The 16:9 HUD records each frame's HUD in one pass instead of switching pictures on every draw,
  which uses roughly a sixth of the render passes.
- Races run up to 60 FPS (the first two prereleases locked them to 30). They hold 60 in quiet
  sections and run at about 35–48 FPS when the field is busy. Frame generation in the launcher
  multiplies what you see on screen.
- **Fast flips** (optional): set the Windows environment variable `RYGT_FAST_FLIPS=1` and each
  frame goes on screen as soon as it is ready instead of waiting for the next 60 Hz tick (about
  0.6 ms instead of up to 17 ms), which evens out frame times below 60 FPS. In a command prompt:
  `setx RYGT_FAST_FLIPS 1`, then reopen the launcher.

### DLSS frame generation (NVIDIA RTX 40 / 50)

The renderer can show generated frames between the game's own frames through NVIDIA DLSS Frame
Generation: 2× on RTX 40 cards, up to 6× on RTX 50 cards. NVIDIA's files can't be shipped in this
GPL release, so you add them yourself:

1. Download `streamline-sdk-v2.14.16.zip` from
   [NVIDIA's Streamline releases](https://github.com/NVIDIA-RTX/Streamline/releases/tag/v2.14.16).
   It must be this version or newer: older Streamline DLLs refuse to start.
2. Extract it, open the launcher's **Graphics** page, press **Add DLSS files…** and pick the
   extracted folder. The launcher finds and copies what it needs into a `dlss` folder next to
   `GT7.exe`: `sl.interposer.dll`, `sl.common.dll`, `sl.dlss_g.dll`, `sl.reflex.dll`,
   `sl.pcl.dll`, `nvngx_dlssg.dll` and `NvLowLatencyVk.dll` from `bin\x64`.
3. Tick **DLSS frame generation** (it turns AMD's frame generation off) and choose the
   **Frame multiplier**:

   | Frame multiplier | Generated frames per game frame | Needs |
   |---|---|---|
   | x1 | none: DLSS frame generation off | |
   | x2 | 1 | RTX 40 or 50 |
   | x3 – x6 | 2 – 5 | RTX 50 |

   If the GPU or driver refuses the multiplier, the game falls back to x2.

Tips:

- **Start with x2.** Frame generation smooths what you see, not how the car responds: a game
  running at 40 FPS still reacts 40 times a second at x6. Higher multipliers only help up to your
  monitor's refresh rate, and more of each second is generated, so fast motion and the HUD wobble
  more.
- **Turn off OptiScaler** if you installed it in the game folder (`dxgi.dll`, `fakenvapi.dll`,
  `dlssg_to_fsr3_amd_is_better.dll`): it hooks into Streamline and spoils the frame pacing.
- A G-Sync / variable refresh display hides the remaining frame-rate swings.
- The renderer gives DLSS no motion vectors or depth (it only has the finished picture), so
  generated frames can wobble on fast motion and the HUD.
- To check it's running, look in `logs\stdout.log` for `ryGT DLSS-G: frame generation on` (the
  log level must be Info).

### Known limits

- Busy race sections stay below 60 FPS. Profiling shows the game and the renderer take turns rather
  than overlap; fixing that needs changes deep in the renderer.
- The known problems in the release notes still apply (occasional crashes, NVIDIA device loss,
  replay shot glitches, no online features).

## Source and builds

- [Launcher and setup tests](loader): build with Windows' .NET Framework C# compiler.
- [Runtime libraries and converter](core): build with CMake, Ninja, and MSYS2 UCRT64 GCC.
- [GT7 compatibility stubs](runtime/gt7): source and definitions for the two supplemental libraries.
- [Renderer](https://gitlab.com/rygt-team/shadgt7): separate repository, pinned in
  [build-support/renderer.json](build-support/renderer.json). The pin currently names the wheel +
  ultrawide edition's `renderer` branch in
  [dessierror/rygt-7-online-build](https://gitlab.com/dessierror/rygt-7-online-build/-/tree/renderer),
  which is shadGT7 plus the 16:9 HUD split and F7 editor, single-pass HUD, DLSS multi-frame
  generation, fast flips and GT7 job-thread hooks (off by default), until those commits land in
  rygt-team/shadgt7.
- [Build and packaging instructions](build-support/README.md).

## Discord server

Join our server for more updates: https://discord.gg/8dz4U9Nd8d

## Credits and license

This repository preserves ryGT/Ryty's Git history and license notices. See [AUTHORS.md](AUTHORS.md)
for upstream credits. RyGT7 changes build on the work of the ryGT, Ryty, shadGT, shadPS4, and third-party
dependency contributors. The racing wheel bridge, ultrawide camera and HUD, P-core pinning, DLSS
frame multiplier and fast flips are by dessierror (the wheel + ultrawide edition). GPL version 2
applies to this project; individual files and dependencies retain their stated licenses.
[LICENSE](LICENSE) and the release's `licenses` folder contain notices.

Gran Turismo is a trademark of Sony Interactive Entertainment. This project is independent of Sony
and Polyphony Digital.
