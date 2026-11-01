# ryGT7

An experimental Windows porting runtime for **Gran Turismo 7 (PS4, CUSA24769)**, derived from
[ryGT](https://github.com/mlgprorektm8/RyGT) and [Ryty](https://github.com/aarvsn/Ryty).
Rendering uses [shadGT7](https://gitlab.com/rygt-team/shadgt7), derived from shadGT/shadPS4.
The converter prepares the player's executable locally; game code runs on the host CPU and graphics
are translated to Vulkan. GT7's online servers can be replaced by one a player hosts on their own PC,
so friends can sign in, open lobby rooms and race each other (see [Online play](#online-play)).

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
Crashes, rendering errors, and shader compilation stutter remain possible. Online play has been
tested between two players (Australia and the UK, and with a player on Linux): see
[Online play](#online-play) for what works.

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

## Online play

GT7's own online servers are replaced by **RyGTServer** (GT7's online service: sign-in, profiles and
lobby rooms) and **shadNet** (PSN sign-in), which one player hosts on their PC from the launcher.
Everyone else joins with an invite code. Lobby races are peer to peer: every player's game connects
straight to the others, as on a PS4. Online play needs one free [ryGT7 account](server/README.md#accounts),
which works with every host: make it in the launcher or on the website's Account page.

### What works

- Signing in to GT7's online service with your own online ID (no PSN account).
- The **Lobby**: creating rooms, finding other players' rooms, joining them, room chat, and racing.
- Player names, Driver and Sportsmanship ratings in rooms.
- The **World Circuits** event lists, news and other online menus (their files come from
  Polyphony's public servers, which RyGTServer asks the first time GT7 needs them).

Not yet: Sport mode, daily races, the Brand Central / Legend cars dealerships' online stock,
friends lists and messages.

### Host

1. Sign in, or make your account, under **Settings > Account**, then open the **Online** page.
2. Your online ID comes from your account (3-16 letters, digits, `-` or `_`).
3. Pick the address friends reach your PC at. For friends over the internet, press
   **Find my internet address** and forward these ports on your router to your PC:

   | Port | Protocol | For |
   |---|---|---|
   | 7890 | TCP | RyGTServer (sign-in, rooms) |
   | 31313 | TCP | shadNet (PSN sign-in) |
   | 31315 | TCP | shadNet web API (friends, blocks) |
   | 3658 | TCP and UDP | the game itself (players connect here) |

   Give your PC a fixed local address in the router (a DHCP reservation) so the forwarding keeps
   pointing at it. If forwarding does nothing, your provider may share one public address between
   customers (CGNAT); a VPN such as Tailscale or Radmin VPN works instead, and its address appears
   in the same list.
4. Press **Start hosting**. It starts `server\shadnet\shadnet.exe` and `server\RyGTServer.exe`,
   makes your account, and shows an **invite code** (`RYGT-XXXXX-XXXXX-XXXXX-XXXXX`). Send it to
   your friends. **New code** stops older codes working for anyone who has not joined yet.
5. Press **Play online**, open the **Lobby** and create a room.

### Join

1. Sign in, or make your account, under **Settings > Account**.
2. On the same page, enter the host's invite code and press **Join friend**. It points GT7 at the
   host's server.
3. Forward **TCP and UDP 3658** on your own router to your PC too, so the other players' games
   can reach yours.
4. Press **Play online**, open the **Lobby** and pick the host's room.

### Everyone

- Press **Allow through firewall** once (Windows asks for administrator rights). It lets
  `GT7.exe` and the server ports through Windows Firewall on every network type. Note that the
  game is `GT7.exe`; `RyGT7.exe` is only the launcher.
- **Play offline instead** goes back to playing offline; your online settings are kept.
- **Linux (Wine / Proton):** online play itself works under Wine (tested); the launcher's Online page
  under Wine is not tested yet. Also open port 3658 in your firewall if you run one (for example
  `sudo ufw allow 3658`).
- **Distance matters.** Everyone's game sends its car to everyone else's, so the round trip between
  players decides how smooth other cars look. Close to each other (under about 100 ms) races feel
  normal; GT7 itself rates above 200 ms as poor, and between far continents (Australia and the UK
  is about 260 ms) other cars jump. Turning on ghosting in the room settings helps.

### How it works

| Part | What it does |
|---|---|
| `libSceNet`, `libSceSsl`, `libSceHttp` | send GT7's traffic for Polyphony's servers to RyGTServer, as plain HTTP |
| shadNet + `libSceNpAuth` (patch 0062) | PSN sign-in, PSN web API, and the PSN calls shadNet lacks forwarded to RyGTServer |
| RyGTServer (`server/`) | GT7's online service ("Vegas"): sign-in, profiles, rooms, PSN game sessions |
| `libSceNpSessionSignaling` | players in a room find each other's addresses through RyGTServer |
| `libSceNet` P2P transport | the games' direct connections, on UDP and TCP port 3658 |
| `libSceRtc` | the race clock follows the host's, so every player's race timeline matches |

The settings are in `RyGT.ini` (`online`, `online_id`, `online_server`, `game_log=1`), the shadNet
account in `user\users.json` and the shadNet addresses in `user\custom_configs\CUSA03220.json`. See
[the launcher guide](loader/README.md#online) and [the server README](server/README.md#gt7).

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
- Races are locked to an even 30 FPS by default. The launcher's Graphics page offers 60 FPS
  (Race frame rate), experimental: races then hold 60 in quiet sections and run at about 35–48 FPS
  when the field is busy, and those uneven frame times can look less smooth than an even 30.
  Frame generation in the launcher multiplies what you see on screen.
- **Fast flips** (optional): set the Windows environment variable `RYGT_FAST_FLIPS=1` and each
  frame goes on screen as soon as it is ready instead of waiting for the next 60 Hz tick (about
  0.6 ms instead of up to 17 ms), which evens out frame times below 60 FPS. In a command prompt:
  `setx RYGT_FAST_FLIPS 1`, then reopen the launcher.

### DLSS frame generation (NVIDIA RTX 40 / 50)

The renderer can upscale with NVIDIA DLSS (any RTX card) and show generated frames between the
game's own frames through NVIDIA DLSS Frame Generation: 2× on RTX 40 cards, up to 6× on RTX 50
cards. NVIDIA's files can't be shipped in this GPL release, so the launcher fetches them:

1. On the launcher's **Graphics** page, press **Download NVIDIA DLSS** in the Upscaling section.
   It downloads NVIDIA DLSS 310.9.16 and Streamline 2.14.16 (about 70 MB) from NVIDIA's own
   GitHub repositories into a `dlss` folder next to `GT7.exe`, and uses a file only if it matches
   a pinned SHA-256 hash and carries NVIDIA Corporation's signature (`dlss\NVIDIA-NOTICE.txt`
   lists the files and NVIDIA's licenses). **Add from a folder…** takes your own copies instead.
2. Upscaling: pick **NVIDIA DLSS** as the method and a **Quality**. The game always draws
   1920×1080, so the quality sets the size DLSS scales to (Native 1×, Quality 1.5×, Balanced
   1.72×, Performance 2×, Ultra Performance 3×), which is then fitted to the window; Auto scales
   to the window size.
3. Frame generation: pick **NVIDIA DLSS (RTX 40+)** (it replaces AMD's) and choose the
   **Multiplier**:

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
  replay shot glitches). Online play's limits are under [Online play](#online-play). For a device
  loss report, launch once with the environment variable `RYGT_DEVICE_FAULT=1` (in a command prompt:
  `set RYGT_DEVICE_FAULT=1`, then start `RyGT7.exe` from it) and attach the log; its DIAG-243 lines
  show the driver's fault report and the last draws.

## Source and builds

- [Launcher and setup tests](loader): build with Windows' .NET Framework C# compiler.
- [Runtime libraries and converter](core): build with CMake, Ninja, and MSYS2 UCRT64 GCC.
- [GT7 compatibility stubs](runtime/gt7): source and definitions for the two supplemental libraries.
- [RyGTServer](server): GT7's online service for online play; build with `server\build.cmd`
  (Windows' .NET Framework C# compiler). It needs `libzstd.dll` (MSYS2 UCRT64's) next to it.
- [PSN library for online play](renderer/shadGT-patches/0062-online-psn-shadnet-rygtserver.patch):
  a shadGT patch, built with the renderer's `ryty_np` target.
- [Renderer](https://gitlab.com/rygt-team/shadgt7): separate repository, pinned in
  [build-support/renderer.json](build-support/renderer.json). Since 2026-10-27 the pin names
  rygt-team/shadgt7 `main` again: it now carries the wheel + ultrawide edition's renderer work (the
  16:9 HUD split and F7 editor, single-pass HUD, DLSS multi-frame generation, fast flips and GT7
  job-thread hooks, off by default).
- [Build and packaging instructions](build-support/README.md).

## Discord server

Join our server for more updates: https://discord.gg/8dz4U9Nd8d

## Credits and license

This repository preserves ryGT/Ryty's Git history and license notices. See [AUTHORS.md](AUTHORS.md)
for upstream credits. RyGT7 changes build on the work of the ryGT, Ryty, shadGT, shadPS4, and third-party
dependency contributors. The racing wheel bridge, ultrawide camera and HUD, P-core pinning, DLSS
frame multiplier and fast flips are by dessierror (the wheel + ultrawide edition), as is GT7 online
play (RyGTServer's GT7 service, PSN session signaling, the launcher's Online page). GPL version 2
applies to this project; individual files and dependencies retain their stated licenses.
[LICENSE](LICENSE) and the release's `licenses` folder contain notices.

Gran Turismo is a trademark of Sony Interactive Entertainment. This project is independent of Sony
and Polyphony Digital.
