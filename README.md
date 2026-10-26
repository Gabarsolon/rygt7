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
- To turn it on, open `user\custom_configs\CUSA03220.json` after the first Set up and set, inside `"GPU"`:

  ```json
  "aspect_ratio": "auto"
  ```

  `"21:9"` and `"32:9"` also work. With 16:9 or no setting, the game looks exactly as before.

### Performance
- On Intel 12th–14th gen and Core Ultra CPUs, the game's busiest threads are kept on the fast P-cores
  with Windows power throttling off. Set the environment variable `RYGT_PCORES=0` to disable this.
- The 16:9 HUD now records each frame's HUD in one pass instead of switching pictures on every draw,
  which uses roughly a sixth of the render passes.
- Races hold 60 FPS in quiet sections and run at about 35–48 FPS when the field is busy. Turning on
  **frame generation** in the launcher roughly doubles what you see on screen.

### DLSS frame generation (NVIDIA RTX 40 / 50)
The renderer can show generated frames between the game's own frames through NVIDIA DLSS Frame
Generation: 2× on RTX 40 cards, up to 4× on RTX 50 cards. NVIDIA's files can't be shipped in this
GPL release, so you add them yourself:

1. Download `streamline-sdk-v2.14.16.zip` from
   [NVIDIA's Streamline releases](https://github.com/NVIDIA-RTX/Streamline/releases/tag/v2.14.16).
   It must be this version or newer: older Streamline DLLs refuse to start.
2. Next to `GT7.exe`, make a folder named `dlss` and copy these files into it from the zip's
   `bin\x64` folder (not `bin\x64\development`):

   ```
   sl.interposer.dll  sl.common.dll  sl.dlss_g.dll  sl.reflex.dll  sl.pcl.dll
   nvngx_dlssg.dll  NvLowLatencyVk.dll
   ```

3. In `user\custom_configs\CUSA03220.json`, inside `"GPU"`, set:

   ```json
   "dlssg_enabled": true,
   "framegen_enabled": false
   ```

   `framegen_enabled` is AMD's frame generation; DLSS replaces it.
4. Choose how many frames to generate with the Windows environment variable `RYGT_DLSSG_FRAMES`
   (then close and reopen the launcher):

   | Value | Multiplier | Needs |
   |---|---|---|
   | `1` (default) | 2× | RTX 40 or 50 |
   | `2` | 3× | RTX 50 |
   | `3` | 4× | RTX 50 |

   For example, in a command prompt: `setx RYGT_DLSSG_FRAMES 1`

Tips:
- **Use 2× with the 60 FPS launcher.** Frame generation smooths what you see, not how the car
  responds: at a 30 FPS game rate, 4× shows 120 but still feels like 30. Real 40–60 FPS doubled
  to 80–120 feels much better.
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
| [`gt7-wheel-ultrawide`](../../tree/gt7-wheel-ultrawide) | runtime libraries: wheel bridge, ultrawide camera, P-core pinning | rygt-team/rygt7 |
| [`gt7-renderer`](../../tree/gt7-renderer) | renderer (`libSceVideoOut.prx`): 16:9 HUD split and F7 editor, single-pass HUD, DLSS multi-frame generation setting, GT7 job-thread hooks (off by default) | rygt-team/shadgt7 |

## Credits
RyGT7 and shadGT7 by the [rygt-team](https://gitlab.com/rygt-team), built on Ryty, shadGT and
[shadPS4](https://github.com/shadps4-emu/shadPS4). Wheel, ultrawide and performance changes in this
edition by dessierror.
