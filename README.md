# RyGT7 – wheel + ultrawide edition

A modified build of the [RyGT7](https://gitlab.com/rygt-team/rygt7) 2026-10-26b prerelease, which runs
Gran Turismo 7 (CUSA24769, version 1.00) as a native Windows program. This edition adds racing wheel
support, a proper ultrawide view with a movable HUD, and some race performance work.

> **No game files are included.** As with the official release, you need your own decrypted GT7 dump,
> PS4 firmware modules and fonts.

## Download

Get `RyGT7-2026-10-26b-wheel-ultrawide.zip` from the [Releases](../../releases) page. It is the complete
RyGT7 prerelease with the modified libraries already in place.

## Setup

1. Extract the zip somewhere writable on the **same drive** as your GT7 dump.
2. Run `RyGT7.exe` and choose:
   - **Base game**: your decrypted CUSA24769 folder (`eboot.bin`, `sce_sys`, `sce_module`, `contents`)
   - **Update**: your update folder, or leave it empty
   - **System modules**: your PS4 firmware's decrypted modules (`libSceFont`, `libSceFontFt`,
     `libSceFreeTypeOt`, `libSceJson2`, `libSceNgs2`, …)
   - **Fonts**: your firmware's `font` and `font2` folders
3. Press **Set up**, wait for the conversion to finish (a minute or two), then **Play**.

`README.txt` in the zip has the full RyGT7 instructions.

## What's new

### Racing wheels
- Your wheel appears to GT7 as a Fanatec direct-drive wheel, so steering, pedals, handbrake, shifter,
  clutch and force feedback work in races.
- Tested with a **Moza R3**. Logitech, Thrustmaster and Simagic wheels are also detected; wheels with
  force feedback are picked first.
- Force feedback is tuned in `ryty-wheel.ini`:

  | Setting | What it does |
  |---|---|
  | `FfbGain` | overall strength (lower it if the wheel feels too heavy) |
  | `FfbDamping` | steadiness on straights |
  | `FfbDetail` | kerbs and road texture |

- `tools\ryty-wheel-probe.exe` lists the wheels and buttons Windows can see, if yours isn't picked up.

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
- The 16:9 HUD now records each frame's HUD in one pass instead of switching pictures on every draw,
  which uses roughly a sixth of the render passes.
- Races run up to 60 FPS (the official prerelease locks them to 30). They hold 60 in quiet sections
  and run at about 35–48 FPS when the field is busy. Frame generation in the launcher multiplies
  what you see on screen.
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

## Known limits
- Busy race sections stay below 60 FPS. Profiling shows the game and the renderer take turns rather
  than overlap; fixing that needs changes deep in the renderer.
- Everything in the official prerelease's known-problems list still applies (occasional crashes,
  NVIDIA device loss, replay shot glitches, no online features).

## Source

Both projects are GPL-2.0; the licence notices are in `LICENSE` and `licenses/` inside the zip.

| Branch | Contents | Based on |
|---|---|---|
| [`gt7-wheel-ultrawide`](../../tree/gt7-wheel-ultrawide) | runtime libraries: wheel bridge, ultrawide camera, P-core pinning; launcher: Ultrawide presets, DLSS frame multiplier, 60 FPS races | rygt-team/rygt7 |
| [`gt7-renderer`](../../tree/gt7-renderer) | renderer (`libSceVideoOut.prx`): 16:9 HUD split and F7 editor, single-pass HUD, DLSS multi-frame generation (up to 6×), fast flips, GT7 job-thread hooks (off by default) | rygt-team/shadgt7 |

## Credits
RyGT7 and shadGT7 by the [rygt-team](https://gitlab.com/rygt-team), built on Ryty, shadGT and
[shadPS4](https://github.com/shadps4-emu/shadPS4). Wheel, ultrawide and performance changes in this
edition by dessierror.
