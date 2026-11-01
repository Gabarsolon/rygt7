# RyGTServer

RyGTServer stands in for PSN and GT Sport's online servers, which closed in January 2024. You can run
it on your own PC, or on one PC that you and your friends all connect to.

**Status: early.** The game signs in and its online traffic reaches the server. The server records
every request and answers from files you provide. No online mode works yet. Each one depends on
working out what the game's servers used to send back (see [Working out the protocol](#working-out-the-protocol)).

## GT7

For GT7 (ryGT7), RyGTServer also answers GT7's online service ("Vegas", `server/Gt7.cs`) and the PSN
calls shadNet does not (`/rygt/psn/...`): signing in, profiles, rooms (create, search, details,
heartbeats; kept in `server-data\gt7\rooms.json` across restarts) and PSN game sessions. Players in a
room find each other through RyGTServer (`/rygt/p2p/session`, used by ryGT7's
`libSceNpSessionSignaling`), then their games connect directly on port 3658.

- Host from the launcher's **Online** page; friends join with the invite code.
- `server-data\gt7\api\` holds RyGTServer's own replies (from this repository's `server/data`).
  Polyphony's own files (the client settings, texts, World Circuit event lists) are fetched from
  Polyphony's public servers the first time GT7 asks and kept in `server-data\gt7\cdn-cache\`.
- `tools/vegas_decode.py` decodes (and encodes) GT7's encrypted messages, for studying captures.

Status: signing in, the lobby, creating rooms, finding them and joining them work. The race feels
smooth when the players are close to each other; GT7 itself rates round trips above about 200 ms as
poor.

## How it works

```
GTSport.exe
  libSceNpManager / libSceNpAuth  signed in as your online ID (no PSN account)
  libSceHttp                      every HTTP and HTTPS request, sent as plain HTTP
        |                         with the original Host header kept
        v
RyGTServer (your PC or a friend's, TCP port 7890)
  responses\<host>\<path>         answers you provide
  captures\<date>\                every request and response, for study
```

Online is off by default. With it off, RyGT behaves exactly as before: you're signed out and every
network request fails.

## Playing

1. **Host:** in RyGT.exe under **Online**, press **Host a server on this PC**. This starts
   RyGTServer and points your game at it. To start it yourself instead, run `RyGTServer.exe`.
2. **Everyone:** tick **Play online through a RyGTServer** and pick an **Online ID** (3-16 letters,
   digits, `-` or `_`). The ID names you on the server. Friends set **Server** to an address from
   the host's RyGTServer window, for example `192.168.1.20:7890`.
3. Press **Play**.

To reach a host over the internet, either forward TCP port 7890 on the host's router to the host's
PC, or put everyone on a shared VPN such as Tailscale and use the host's VPN address. Windows may ask
the host to let RyGTServer through the firewall: allow it.

The settings live in `RyGT.ini` next to `GTSport.exe`. Environment variables override them:

| RyGT.ini | Environment | Default |
| --- | --- | --- |
| `online=1` | `RYGT_ONLINE` | off |
| `online_id=Name` | `RYGT_ONLINE_ID` | `RyGTPlayer` |
| `online_server=host:port` | `RYGT_ONLINE_SERVER` | `127.0.0.1:7890` |

## Server options

```
RyGTServer [--port 7890] [--data <folder>] [--default-status 404] [--no-capture]
```

- `--data`: the folder that holds `responses\` and `captures\`. Defaults to `server-data` next to
  RyGTServer.exe.
- `--default-status`: the status for requests with no response file.
- Status page: `http://<server>:7890/` shows who is connected and the latest requests. The same
  information is available as JSON at `/rygt/status`.

## Accounts

ryGT7 accounts live in the list worker's `Accounts` object on Cloudflare (`server/list-worker`), so
they work with every host and need no server of their own. Signing in gives the launcher a pass, valid
for 30 days, that proves the online ID. A host started with `--require-pass` (the launcher does this)
checks the pass and then makes that player's shadNet account on its own PC, as for any invite.

- The signing key is the worker secret `PASS_KEY`. Its public half is in `AccountPass` in
  `RyGTServer.cs`, so changing the key needs a new RyGTServer too. The private key is kept nowhere else.
- Passwords never leave the player's PC or browser: both send a key made from them (`AccountKey` in
  `loader\Online.cs`, and the same in `site\index.html`).
- There is no password reset yet. Cloudflare's dashboard shows the worker's `Accounts` object.

## What the server answers itself

- **Online save** (`save2.*`, `savef.*`): kept per player in `server-data\players\<online id>\`,
  with the last 5 uploads as backups.
- **Sport Mode events** (`event_calendar.get`, `event.get`, `event_folder.get`): from
  `server-data\events\`. Each event is one GameParameter file named `<event_id>.json`, in the same
  format as the game's own `GAME_PARAMETER\GT7SP\EVENT` files. `schedule.json` says what runs where:

  ```json
  {
    "daily_channels": { "1": 9000001, "2": 9000002, "3": 9000003 },
    "folders": { "22": [9100001, 9100002, 9100003] },
    "required_time": { "9000001": 600 }
  }
  ```

  Channels 1–3 are Daily Races A–C, and folder 22 is the Time Trial list. Scheduled events run from
  midnight UTC today for a week, so they are always open: an event file without its own
  `begin_date`/`end_date` is sent with this week's. `tools\make_events.py` builds a starter set from
  the game's own files.
- **Downloads Sport Mode needs** live in `responses\gt7sp\<region>\event\`: `bopdb.dat` (Balance of
  Performance, `tools\make_bopdb.py`) and `gazetter.dat` (regions for the player's location,
  `tools\make_gazetter.py`). Both are SQLite databases.
- **Leaderboards** (`ranking.*`): one file per board in `server-data\rankings\`, holding each player's
  best (lowest) score, with uploaded replays in `rankings\replays\`. An event's board is its
  `ranking.board_id`.

## Working out the protocol

1. Enable online, start the server and play. Open the menus that use the network (Sport Mode,
   Lobby, Brand Central, Scapes, decals and liveries…).
2. Each request is saved as `server-data\captures\<date>\NNNNN_<METHOD>_<host>_<path>.txt`. Each file
   holds the request with its headers and body, plus the answer the server gave. Raw bodies are saved
   next to them as `.request.bin` and `.response.bin`. Requests with no answer show in yellow in the
   server window.
3. Write an answer as a file under `server-data\responses\<host>\<path>`. For example, a request to
   `https://example.gran-turismo.com/api/profile` is answered from
   `responses\example.gran-turismo.com\api\profile`.
   - `<path>@<query>` is tried before `<path>`, for answers that depend on the query.
   - `<path>.POST` (or `.GET`…) is tried before `<path>`, for answers that depend on the method.
   - A path ending in `/` is answered from `<path>\_index`.
   - An optional `<file>.headers` holds header lines. `Status: 500` in it sets the status code.
4. Play again and see how far the game gets.

Answers that work can then become built-in server logic, such as profiles, lobbies and race
results, so every RyGTServer gets them.

## Building

`build.cmd [output folder]` builds RyGTServer.exe with the C# compiler included with Windows (.NET
Framework 4.x), the same way as the loader. The runtime side lives in `core/libs/OnlineConfig.hpp`,
`core/libs/prx/libSceNpManager`, `libSceNpAuth` and `libSceHttp/src/Bridge.cpp`.
