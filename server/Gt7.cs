// RyGTServer's Gran Turismo 7 side: GT7's online service ("Vegas") and its file servers.
//
// RyGT's libSceNet sends GT7's connections to Polyphony's hosts here, and its libSceSsl passes
// them through without TLS, so GT7 talks plain HTTP/1.1 to this server:
//   api.<stage>.vegas.granturismo-online.net      the API: POST /<service>/<operation>
//   static.<stage>.vegas.granturismo-online.net   GET /server/config/all.json, the client settings
//   asset.gt7.game.gran-turismo.com and asset.{stage}.{base_domain}   GET /delivery/data/<file>
//   channel.<stage>.vegas.granturismo-online.net  a WebSocket for server messages (not answered yet)
// API bodies and delivered data files are Vegas messages (see VegasCodec). An API reply is
// {"result": <response>}.
//
// Files under server-data\gt7\ override or extend what is built in:
//   server_config.json       the client settings (plain JSON or a Vegas message)
//   api\<service>\<operation>.json   the "result" of an API call this file does not answer itself
//   files\<path below /delivery/data/>   a delivered file (plain, or already a Vegas message)
//   users.json, secret.key   the players who have signed in, and the key their tokens are signed with
// SPDX-License-Identifier: GPL-2.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace RyGT.Server
{
    /// A Vegas message: 01 01 | flags (0x80: not compressed) | 00 | 12-byte nonce |
    /// HMAC-SHA256 of the ciphertext | ChaCha20 (RFC 8439, counter 0) of the payload, which is a
    /// zstd frame unless flagged. Both keys are in GT7's executable.
    static class VegasCodec
    {
        static readonly byte[] CipherKey = Convert.FromBase64String("FojitMgHIaZ4ZV+QrNZvv7OaGvxQJxJvlzZPuTM02X8=");
        static readonly byte[] HmacKey = Convert.FromBase64String(
            "2CinLKRqDQC3ScL8QkCuvSD+34OJiPM1EkOpYM3z7d8Au8lzMEaiZYEQ929VjnJGkA2cQeZvfbKmGN8NN/VhHg==");
        const int HeaderSize = 48;
        static readonly RandomNumberGenerator Random = RandomNumberGenerator.Create();

        public static bool IsMessage(byte[] data)
        {
            return data.Length >= HeaderSize && data[0] == 1 && data[1] == 1;
        }

        public static byte[] Decode(byte[] message)
        {
            if (!IsMessage(message)) throw new InvalidDataException("not a Vegas message");
            var nonce = new byte[12];
            Array.Copy(message, 4, nonce, 0, 12);
            var ciphertext = new byte[message.Length - HeaderSize];
            Array.Copy(message, HeaderSize, ciphertext, 0, ciphertext.Length);
            byte[] tag;
            using (var hmac = new HMACSHA256(HmacKey)) tag = hmac.ComputeHash(ciphertext);
            for (int i = 0; i < 32; i++)
                if (tag[i] != message[16 + i]) throw new InvalidDataException("Vegas message signature does not match");
            byte[] payload = ChaCha20(nonce, ciphertext);
            return (message[2] & 0x80) != 0 ? payload : Zstd.Decompress(payload);
        }

        public static byte[] Encode(byte[] payload)
        {
            var nonce = new byte[12];
            lock (Random) Random.GetBytes(nonce);
            byte[] ciphertext = ChaCha20(nonce, Zstd.Compress(payload));
            var message = new byte[HeaderSize + ciphertext.Length];
            message[0] = 1;
            message[1] = 1;
            Array.Copy(nonce, 0, message, 4, 12);
            using (var hmac = new HMACSHA256(HmacKey)) Array.Copy(hmac.ComputeHash(ciphertext), 0, message, 16, 32);
            Array.Copy(ciphertext, 0, message, HeaderSize, ciphertext.Length);
            return message;
        }

        static byte[] ChaCha20(byte[] nonce, byte[] input)
        {
            var state = new uint[16];
            state[0] = 0x61707865; state[1] = 0x3320646e; state[2] = 0x79622d32; state[3] = 0x6b206574;
            for (int i = 0; i < 8; i++) state[4 + i] = BitConverter.ToUInt32(CipherKey, i * 4);
            for (int i = 0; i < 3; i++) state[13 + i] = BitConverter.ToUInt32(nonce, i * 4);
            var output = new byte[input.Length];
            var block = new uint[16];
            for (int offset = 0, counter = 0; offset < input.Length; offset += 64, counter++)
            {
                state[12] = (uint)counter;
                Array.Copy(state, block, 16);
                for (int round = 0; round < 10; round++)
                {
                    Quarter(block, 0, 4, 8, 12); Quarter(block, 1, 5, 9, 13); Quarter(block, 2, 6, 10, 14); Quarter(block, 3, 7, 11, 15);
                    Quarter(block, 0, 5, 10, 15); Quarter(block, 1, 6, 11, 12); Quarter(block, 2, 7, 8, 13); Quarter(block, 3, 4, 9, 14);
                }
                for (int i = 0; i < 64 && offset + i < input.Length; i++)
                {
                    uint word = block[i / 4] + state[i / 4];
                    output[offset + i] = (byte)(input[offset + i] ^ (byte)(word >> (8 * (i % 4))));
                }
            }
            return output;
        }

        static void Quarter(uint[] x, int a, int b, int c, int d)
        {
            x[a] += x[b]; x[d] = Rotate(x[d] ^ x[a], 16);
            x[c] += x[d]; x[b] = Rotate(x[b] ^ x[c], 12);
            x[a] += x[b]; x[d] = Rotate(x[d] ^ x[a], 8);
            x[c] += x[d]; x[b] = Rotate(x[b] ^ x[c], 7);
        }

        static uint Rotate(uint value, int bits) { return (value << bits) | (value >> (32 - bits)); }
    }

    /// zstd through libzstd.dll next to RyGTServer.exe (from MSYS2's UCRT64 zstd package).
    static class Zstd
    {
        [DllImport("libzstd.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern UIntPtr ZSTD_decompress(byte[] destination, UIntPtr capacity, byte[] source, UIntPtr size);
        [DllImport("libzstd.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern UIntPtr ZSTD_compress(byte[] destination, UIntPtr capacity, byte[] source, UIntPtr size, int level);
        [DllImport("libzstd.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern UIntPtr ZSTD_compressBound(UIntPtr size);
        [DllImport("libzstd.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern uint ZSTD_isError(UIntPtr code);
        [DllImport("libzstd.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern ulong ZSTD_getFrameContentSize(byte[] source, UIntPtr size);

        public static byte[] Decompress(byte[] frame)
        {
            ulong known = ZSTD_getFrameContentSize(frame, (UIntPtr)frame.Length);
            // Unknown (-1) or error (-2): decompress into a growing buffer.
            long capacity = known < 0xFFFFFFFFFFFFFFFEUL ? (long)known : Math.Max(frame.Length * 16L, 1 << 20);
            for (int attempt = 0; attempt < 6; attempt++, capacity *= 4)
            {
                var output = new byte[capacity];
                UIntPtr written = ZSTD_decompress(output, (UIntPtr)output.Length, frame, (UIntPtr)frame.Length);
                if (ZSTD_isError(written) != 0) continue;
                Array.Resize(ref output, (int)written);
                return output;
            }
            throw new InvalidDataException("zstd frame does not decompress");
        }

        public static byte[] Compress(byte[] data)
        {
            var output = new byte[(long)ZSTD_compressBound((UIntPtr)data.Length)];
            UIntPtr written = ZSTD_compress(output, (UIntPtr)output.Length, data, (UIntPtr)data.Length, 3);
            if (ZSTD_isError(written) != 0) throw new InvalidDataException("zstd compression failed");
            Array.Resize(ref output, (int)written);
            return output;
        }
    }

    class Gt7
    {
        readonly string directory;
        readonly Action<string> log;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        readonly byte[] secret;
        readonly Dictionary<string, Dictionary<string, object>> users;  // by online ID
        readonly object usersLock = new object();
        readonly HashSet<string> unanswered = new HashSet<string>();
        // The other players in a player's PSN session (from RyGT's session signaling), and which of
        // them each player has been sent by get_sport_users (see SportUsers).
        readonly Func<string, List<string>> othersInSession;
        readonly Dictionary<string, HashSet<string>> sportUsersSent = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        // The open rooms (by room_id), with what their owners sent; kept in memory only.
        readonly Dictionary<long, Dictionary<string, object>> rooms = new Dictionary<long, Dictionary<string, object>>();
        long nextRoomId = 1;

        public Gt7(string dataDirectory, Action<string> log, Func<string, List<string>> othersInSession)
        {
            this.othersInSession = othersInSession;
            directory = Path.Combine(dataDirectory, "gt7");
            Directory.CreateDirectory(directory);
            this.log = log;
            string secretFile = Path.Combine(directory, "secret.key");
            if (!File.Exists(secretFile))
            {
                var key = new byte[32];
                using (var random = RandomNumberGenerator.Create()) random.GetBytes(key);
                File.WriteAllBytes(secretFile, key);
            }
            secret = File.ReadAllBytes(secretFile);
            LoadRooms();
            string usersFile = Path.Combine(directory, "users.json");
            users = File.Exists(usersFile)
                ? json.Deserialize<Dictionary<string, Dictionary<string, object>>>(File.ReadAllText(usersFile))
                : new Dictionary<string, Dictionary<string, object>>();
            users = new Dictionary<string, Dictionary<string, object>>(users, StringComparer.OrdinalIgnoreCase);
        }

        public static bool IsGt7Host(string host)
        {
            return host.EndsWith("granturismo-online.net", StringComparison.Ordinal) || host.StartsWith("asset.", StringComparison.Ordinal);
        }

        /// The answer to one of GT7's requests, or null to fall back to the response files. The
        /// decoded request and reply go into the request's and response's Decoded fields.
        public HttpResponse Handle(HttpRequest request)
        {
            string host = request.Host;
            if (host.StartsWith("api.", StringComparison.Ordinal)) return HandleApi(request);
            if (host.StartsWith("static.", StringComparison.Ordinal) && request.Path == "/server/config/all.json") return ServerConfig();
            if (host.StartsWith("asset.", StringComparison.Ordinal) && request.Path.StartsWith("/delivery/data/", StringComparison.Ordinal))
                return DeliveryFile(request.Path.Substring("/delivery/data/".Length));
            return null;
        }

        // ---- the API ----

        HttpResponse HandleApi(HttpRequest request)
        {
            Dictionary<string, object> call = null;
            if (request.Body.Length > 0)
            {
                try
                {
                    string text = Encoding.UTF8.GetString(VegasCodec.Decode(request.Body));
                    request.Decoded = text;
                    call = json.DeserializeObject(text) as Dictionary<string, object>;
                }
                catch (Exception e)
                {
                    log("GT7: unreadable request to " + request.Path + ": " + e.Message);
                    return new HttpResponse { Status = 400 };
                }
            }
            object body = call != null && call.ContainsKey("body") ? call["body"] : null;
            Dictionary<string, object> user = UserFromToken(request.Header("Authorization"));

            object result;
            switch (request.Path)
            {
                case "/auth/sign_in": result = SignIn(body as Dictionary<string, object>); break;
                case "/auth/sign_out": result = new Dictionary<string, object>(); break;
                case "/analytics/report_error_code":
                    log("GT7 reports error " + Field(body, "code") + (user != null ? " (" + user["np_online_id"] + ")" : ""));
                    result = new Dictionary<string, object>();
                    break;
                case "/user/get_user_profile":
                    result = user == null ? null : UserProfile(user);
                    break;
                case "/user/get_sport_profile":
                    // The required fields (from api\user\get_sport_profile.json) with the player's own values.
                    var sport = ApiFile(request.Path) as Dictionary<string, object> ?? new Dictionary<string, object>();
                    if (user != null)
                        foreach (string key in new[] { "user_id", "driver_rating", "sportsmanship_rating", "dr_point", "sr_point", "manufacturer_id", "is_star_player" })
                            if (user.ContainsKey(key)) sport[key] = user[key];
                    result = sport;
                    break;
                case "/user/get_sport_users":
                    object headers = call != null && call.ContainsKey("headers") ? call["headers"] : null;
                    result = SportUsers(Field(body, "user_ids") as object[], user, Convert.ToString(Field(headers, "sequence_no"), CultureInfo.InvariantCulture),
                        Convert.ToString(Field(headers, "window_name"), CultureInfo.InvariantCulture));
                    break;
                case "/room/create":
                    result = user == null ? null : CreateRoom(body as Dictionary<string, object>, user);
                    break;
                case "/room/search":
                    result = SearchRooms(body as Dictionary<string, object>);
                    break;
                case "/room/get_detail":
                    result = RoomDetail(Field(body, "room_id"));
                    break;
                case "/room/delete":
                    result = user == null ? null : new Dictionary<string, object> { { "deleted_count", DeleteRooms(user) } };
                    break;
                case "/room/update_info":
                case "/room/update_ticket":
                case "/room/heartbeat":
                    // These replies carry no result: GT7 only needs the call to succeed.
                    if (user != null) UpdateRoom(request.Path, body as Dictionary<string, object>, user);
                    return Reply(new Dictionary<string, object>(), request);
                default:
                    if (request.Path.StartsWith("/analytics/", StringComparison.Ordinal)) { result = new Dictionary<string, object>(); break; }
                    result = ApiFile(request.Path);
                    if (result == null)
                    {
                        // "result" is optional in every response, while what it holds has required
                        // fields: a reply without one is GT7's "nothing here" and always parses.
                        lock (unanswered)
                            if (unanswered.Add(request.Path)) log("GT7: " + request.Path + " answered with no result");
                        return Reply(new Dictionary<string, object>(), request);
                    }
                    break;
            }
            if (result == null) return new HttpResponse { Status = 404 };
            return Reply(new Dictionary<string, object> { { "result", result } }, request);
        }

        object ApiFile(string path)
        {
            string file = Path.Combine(directory, "api", ResponseFiles.SanitizePath(path) + ".json");
            if (!File.Exists(file)) return null;
            return json.DeserializeObject(File.ReadAllText(file));
        }

        HttpResponse Reply(object value, HttpRequest request)
        {
            string text = json.Serialize(value);
            var response = new HttpResponse { Body = VegasCodec.Encode(Encoding.UTF8.GetBytes(text)), Decoded = text, Source = "GT7" };
            response.Headers.Add(new KeyValuePair<string, string>("Content-Type", "application/octet-stream"));
            return response;
        }

        // ---- signing in ----

        /// POST /auth/sign_in: {"code", "id_token", "runtime_info": {..., "user_info": {"np_online_id"}}}.
        /// The PSN code and ID token are not checked (RyGT's PSN is shadNet). The reply holds the
        /// player's tokens and Vegas user; GT7 reads the tokens' claims but not their signatures.
        object SignIn(Dictionary<string, object> body)
        {
            string onlineId = Field(Field(Field(body, "runtime_info"), "user_info"), "np_online_id") as string;
            if (string.IsNullOrEmpty(onlineId)) return null;
            Dictionary<string, object> user;
            lock (usersLock)
            {
                if (!users.TryGetValue(onlineId, out user))
                {
                    user = new Dictionary<string, object> {
                        { "user_id", Guid.NewGuid().ToString("N") }, { "np_account_id", AccountId(onlineId) },
                        { "np_online_id", onlineId }, { "nick_name", onlineId }, { "country_code", "us" },
                        { "driver_rating", 1 }, { "dr_point", 1000 }, { "sportsmanship_rating", 4 }, { "sr_point", 50000 },
                        { "manufacturer_id", 0 }, { "is_star_player", false }, { "is_valid_user", true } };
                    users[onlineId] = user;
                    File.WriteAllText(Path.Combine(directory, "users.json"), json.Serialize(users));
                    log("GT7: new player " + onlineId);
                }
            }
            log("GT7: " + onlineId + " signed in");
            long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
            const int AccessLifetime = 30 * 86400, RefreshLifetime = 365 * 86400;
            string userId = (string)user["user_id"];
            return new Dictionary<string, object> {
                { "notices", new object[0] },
                { "token", new Dictionary<string, object> {
                    { "access_token", Token(new Dictionary<string, object> { { "sub", userId }, { "np_online_id", onlineId },
                        { "iat", now }, { "exp", now + AccessLifetime }, { "token_use", "access" } }) },
                    { "expires_in", AccessLifetime },
                    { "refresh_token", Token(new Dictionary<string, object> { { "sub", userId }, { "np_online_id", onlineId },
                        { "iat", now }, { "exp", now + RefreshLifetime }, { "token_use", "refresh" } }) },
                    { "refresh_token_expires_in", RefreshLifetime },
                    { "token_type", "bearer" } } },
                { "user", user } };
        }

        /// user/get_user_profile: the player's user fields and the profile's own required fields.
        static Dictionary<string, object> UserProfile(Dictionary<string, object> user)
        {
            var profile = new Dictionary<string, object>(user);
            foreach (string text in new[] { "about_me", "greeting", "greeting_post_race", "instagram_user_name", "twitter_user_name" })
                if (!profile.ContainsKey(text)) profile[text] = "";
            if (!profile.ContainsKey("hidden_list")) profile["hidden_list"] = new object[0];
            return profile;
        }

        /// user/get_sport_users {"user_ids": [...]}: an array with each player's sport fields, all
        /// required; an unknown ID gets a beginner's values.
        /// GT7 here never learns any Vegas user ID (its own login step does not run; RyGT signs it in),
        /// so it asks for "" both for itself and for each player who connects to it, and a joining
        /// game asks for itself and the others in one burst, over connections that can arrive in any
        /// order. See EmptyId for how each request is told apart.
        /// A player joined a PSN session (or joined again after a failed try): every game is sent it
        /// again as a new player, and its own game starts over (otherwise a player sent on a failed try
        /// is skipped, and the asker's own profile is sent in its place).
        public void PlayerJoined(string onlineId)
        {
            lock (sportUsersSent)
            {
                foreach (var sent in sportUsersSent.Values) sent.Remove(onlineId);
                sportUsersSent.Remove(onlineId);
                emptyIdBursts.Remove(onlineId);
            }
        }

        class EmptyIdBurst
        {
            public DateTime Started;
            public readonly List<long> Sequences = new List<long>();
            public readonly Dictionary<long, string> Answers = new Dictionary<long, string>();
        }

        readonly Dictionary<string, EmptyIdBurst> emptyIdBursts = new Dictionary<string, EmptyIdBurst>(StringComparer.OrdinalIgnoreCase);
        const int EmptyIdBurstMs = 250;

        /// Who an empty ID means, as an online ID. Alone, it is the asker. From the race screen it is
        /// another player, never the asker, who only asks for itself from the room list. From the
        /// room list with others in the session, the requests of one burst are held for a moment
        /// and go by sequence number: the first is the asker, then the others in the order they
        /// joined. A request sent again keeps its answer.
        string EmptyId(string askingId, List<string> others, string sequence, string window)
        {
            if (askingId.Length == 0) return askingId;
            long number;
            bool ranked = long.TryParse(sequence, NumberStyles.None, CultureInfo.InvariantCulture, out number);
            EmptyIdBurst burst;
            lock (sportUsersSent)
            {
                HashSet<string> sent;
                if (!sportUsersSent.TryGetValue(askingId, out sent)) sportUsersSent[askingId] = sent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (others.Count == 0)
                {
                    sent.Clear();
                    return askingId;
                }
                if ((window ?? "").IndexOf("RoomSelection", StringComparison.Ordinal) < 0 || !ranked)
                {
                    string next = others.FirstOrDefault(o => !sent.Contains(o)) ?? others[0];
                    sent.Add(next);
                    return next;
                }
                if (!emptyIdBursts.TryGetValue(askingId, out burst) || DateTime.UtcNow - burst.Started > TimeSpan.FromSeconds(30))
                    emptyIdBursts[askingId] = burst = new EmptyIdBurst { Started = DateTime.UtcNow };
                string known;
                if (burst.Answers.TryGetValue(number, out known)) return known;
                if (!burst.Sequences.Contains(number)) burst.Sequences.Add(number);
            }
            System.Threading.Thread.Sleep(EmptyIdBurstMs);
            lock (sportUsersSent)
            {
                string known;
                if (burst.Answers.TryGetValue(number, out known)) return known;
                // The first of a burst is the asker. Requests that came too late for it continue with
                // the players it has not been sent yet.
                var waiting = burst.Sequences.Where(s => !burst.Answers.ContainsKey(s)).OrderBy(s => s).ToList();
                HashSet<string> sent = sportUsersSent[askingId];
                foreach (long sequenceNumber in waiting)
                {
                    string answer = burst.Answers.Count == 0 ? askingId : others.FirstOrDefault(o => !sent.Contains(o)) ?? askingId;
                    if (answer != askingId) sent.Add(answer);
                    burst.Answers[sequenceNumber] = answer;
                }
                return burst.Answers[number];
            }
        }

        object[] SportUsers(object[] ids, Dictionary<string, object> asking, string sequence, string window)
        {
            var list = new List<object>();
            string askingId = asking == null ? "" : asking["np_online_id"] as string ?? "";
            foreach (object id in ids ?? new object[0])
            {
                string userId = id as string ?? "";
                Dictionary<string, object> player = null;
                if (userId.Length == 0)
                {
                    player = asking;
                    List<string> others = askingId.Length > 0 ? othersInSession(askingId) : new List<string>();
                    string answer = EmptyId(askingId, others, sequence, window);
                    if (!string.Equals(answer, askingId, StringComparison.OrdinalIgnoreCase))
                        lock (usersLock) users.TryGetValue(answer, out player);
                    if (others.Count > 0) log("GT7: get_sport_users(\"\") from " + askingId + " (sequence " + sequence + ") answered with " + answer);
                }
                else
                {
                    lock (usersLock) player = users.Values.FirstOrDefault(u => (u["user_id"] as string) == userId);
                }
                list.Add(SportUser(player, player != null ? player["user_id"] as string ?? userId : userId));
            }
            return list.ToArray();
        }

        /// A sport user: the user fields GT7 requires (user_id, np_account_id, np_online_id,
        /// nick_name, country_code, is_hidden_nickname, is_valid_user; without them it shows no
        /// name) and the sport fields.
        static Dictionary<string, object> SportUser(Dictionary<string, object> player, string userId)
        {
            var sport = new Dictionary<string, object> {
                { "user_id", userId }, { "np_account_id", "0" }, { "np_online_id", "" }, { "nick_name", "" },
                { "country_code", "us" }, { "avatar_photo_id", 0 }, { "is_hidden_avatar", false },
                { "is_hidden_nickname", false }, { "is_valid_user", true },
                { "real_name", "" }, { "driver_rating", 1 }, { "dr_point", 1000 },
                { "sportsmanship_rating", 4 }, { "sr_point", 50000 }, { "manufacturer_id", 0 }, { "is_star_player", false } };
            if (player != null)
                foreach (string key in new[] { "user_id", "np_account_id", "np_online_id", "nick_name", "country_code", "is_valid_user",
                    "driver_rating", "sportsmanship_rating", "dr_point", "sr_point", "manufacturer_id", "is_star_player" })
                    if (player.ContainsKey(key)) sport[key] = player[key];
            return sport;
        }

        // ---- rooms ----

        /// room/create: the new vegas::room::Room. Its reader requires code, game_ticket (base64),
        /// hidden_list, last_updated, name, owner.user (a sport user) and room_type; the rest are
        /// filled in too. What the owner sent (app_data, search_criteria, np_session_id) is kept
        /// with the room for searches.
        object CreateRoom(Dictionary<string, object> body, Dictionary<string, object> owner)
        {
            if (body == null) return null;
            long roomId;
            lock (rooms) roomId = nextRoomId++;
            var room = new Dictionary<string, object> {
                { "room_id", roomId },
                { "code", roomId.ToString("D6", CultureInfo.InvariantCulture) },
                { "name", Field(body, "name") as string ?? "" },
                { "room_type", Field(body, "room_type") as string ?? "OpenLobby" },
                { "room_version", Field(body, "room_version") ?? 0 },
                { "capacity", Field(body, "capacity") ?? 16 },
                { "attributes", Field(body, "attributes") ?? new Dictionary<string, object>() },
                { "full", false },
                { "game_ticket", Field(body, "game_ticket") as string ?? "" },
                { "session_stats", "" },
                { "hidden_list", new object[0] },
                { "rtt_samples", Field(body, "rtt_samples") ?? new object[0] },
                { "last_updated", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) },
                { "owner", new Dictionary<string, object> { { "user", SportUser(owner, owner["user_id"] as string ?? "") } } } };
            lock (rooms)
            {
                // A player owns one room at a time.
                foreach (long old in rooms.Where(r => r.Value["owner_id"] as string == owner["user_id"] as string).Select(r => r.Key).ToList())
                    rooms.Remove(old);
                rooms[roomId] = new Dictionary<string, object> {
                    { "room", room }, { "owner_id", owner["user_id"] }, { "app_data", Field(body, "app_data") },
                    { "search_criteria", Field(body, "search_criteria") }, { "np_session_id", Field(body, "np_session_id") },
                    { "seen", DateTime.UtcNow } };
                SaveRooms();
            }
            log("GT7: " + owner["np_online_id"] + " opened room " + roomId + " \"" + room["name"] + "\"");
            return room;
        }

        /// The open rooms are kept in gt7\rooms.json too, so restarting this server does not empty
        /// the lobby: a room is listed again as long as its owner's game still sends heartbeats.
        string RoomsFile { get { return Path.Combine(directory, "rooms.json"); } }

        void SaveRooms()
        {
            var list = rooms.Values.Select(entry => entry.Where(f => f.Key != "seen").ToDictionary(f => f.Key, f => f.Value)).ToList();
            try { File.WriteAllText(RoomsFile, json.Serialize(list)); }
            catch (IOException e) { log("GT7: could not save rooms: " + e.Message); }
        }

        void LoadRooms()
        {
            if (!File.Exists(RoomsFile)) return;
            try
            {
                foreach (object item in json.DeserializeObject(File.ReadAllText(RoomsFile)) as object[] ?? new object[0])
                {
                    var entry = item as Dictionary<string, object>;
                    var room = entry == null ? null : entry["room"] as Dictionary<string, object>;
                    if (room == null) continue;
                    long id = Convert.ToInt64(room["room_id"], CultureInfo.InvariantCulture);
                    room["room_id"] = id;
                    entry["seen"] = DateTime.UtcNow;
                    rooms[id] = entry;
                    nextRoomId = Math.Max(nextRoomId, id + 1);
                }
                if (rooms.Count > 0) log("GT7: " + rooms.Count + " room(s) kept from before the restart");
            }
            catch (Exception e)
            {
                // A damaged file only loses the rooms it held.
                log("GT7: could not read rooms.json: " + e.Message);
            }
        }

        /// The room a call names (body.room_id), if the player owns it; otherwise the player's room.
        Dictionary<string, object> OwnedRoom(Dictionary<string, object> body, Dictionary<string, object> owner)
        {
            object id = Field(body, "room_id");
            foreach (var entry in rooms.Values)
                if (entry["owner_id"] as string == owner["user_id"] as string
                    && (id == null || Convert.ToInt64(id, CultureInfo.InvariantCulture) == (long)((Dictionary<string, object>)entry["room"])["room_id"]))
                    return entry;
            return null;
        }

        /// room/update_info merges the new settings (e.g. attributes {closed, hidden}) into the room,
        /// room/update_ticket replaces its game_ticket, and every call (heartbeat too) keeps it listed.
        void UpdateRoom(string path, Dictionary<string, object> body, Dictionary<string, object> owner)
        {
            lock (rooms)
            {
                var entry = OwnedRoom(body, owner);
                if (entry == null) return;
                var room = (Dictionary<string, object>)entry["room"];
                if (path == "/room/update_info" || path == "/room/update_ticket")
                {
                    foreach (var field in body)
                        if (field.Key != "room_id") room[field.Key] = field.Value;
                    room["last_updated"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
                    SaveRooms();
                }
                entry["seen"] = DateTime.UtcNow;
            }
        }

        /// room/get_detail {"room_id"}: a vegas::room::RoomDetail, {"room", "members": [{"user":
        /// sport user}], "app_data", "has_friend"} (room and members required). The owner is the
        /// only member kept so far.
        object RoomDetail(object roomId)
        {
            if (roomId == null) return null;
            lock (rooms)
            {
                Dictionary<string, object> entry;
                if (!rooms.TryGetValue(Convert.ToInt64(roomId, CultureInfo.InvariantCulture), out entry)) return null;
                var room = (Dictionary<string, object>)entry["room"];
                return new Dictionary<string, object> {
                    { "room", room },
                    { "members", new object[] { new Dictionary<string, object> { { "user", Field(room["owner"], "user") } } } },
                    { "app_data", entry["app_data"] ?? new Dictionary<string, object>() },
                    { "has_friend", false } };
            }
        }

        int DeleteRooms(Dictionary<string, object> owner)
        {
            lock (rooms)
            {
                var mine = rooms.Where(r => r.Value["owner_id"] as string == owner["user_id"] as string).Select(r => r.Key).ToList();
                foreach (long id in mine) rooms.Remove(id);
                if (mine.Count > 0) SaveRooms();
                return mine.Count;
            }
        }

        /// room/search {"query": {"$and": [{"room_version": n}, {"search_criteria.region": "scea"},
        /// {"room_type": "OpenLobby"}], ...}, "offset", "limit"}: the open rooms (not hidden or
        /// closed, heard from in the last 90 seconds) whose fields equal the query's.
        object SearchRooms(Dictionary<string, object> body)
        {
            var conditions = new List<KeyValuePair<string, object>>();
            var and = Field(Field(body, "query"), "$and") as object[];
            foreach (object term in and ?? new object[0])
                if (term is Dictionary<string, object>)
                    foreach (var field in (Dictionary<string, object>)term)
                        if (field.Value is string || field.Value is int || field.Value is long || field.Value is bool)
                            conditions.Add(field);
            int offset = Convert.ToInt32(Field(body, "offset") ?? 0, CultureInfo.InvariantCulture);
            int limit = Convert.ToInt32(Field(body, "limit") ?? 20, CultureInfo.InvariantCulture);
            List<object> found;
            lock (rooms)
            {
                foreach (long stale in rooms.Where(r => DateTime.UtcNow - (DateTime)r.Value["seen"] > TimeSpan.FromSeconds(90)).Select(r => r.Key).ToList())
                    rooms.Remove(stale);
                found = rooms.Values.Where(entry =>
                {
                    var room = (Dictionary<string, object>)entry["room"];
                    if (Field(room["attributes"], "hidden") as bool? == true || Field(room["attributes"], "closed") as bool? == true) return false;
                    foreach (var condition in conditions)
                    {
                        object value = condition.Key.StartsWith("search_criteria.", StringComparison.Ordinal)
                            ? Field(entry["search_criteria"], condition.Key.Substring("search_criteria.".Length))
                            : room.ContainsKey(condition.Key) ? room[condition.Key] : null;
                        // room_version's low 16 bits are the game's network version; the bits above
                        // differ between players of the same build (459858 = 0x70452 vs 1106 =
                        // 0x452), so only the low bits have to match.
                        if (condition.Key == "room_version" && value != null)
                        {
                            long have = Convert.ToInt64(value, CultureInfo.InvariantCulture), want = Convert.ToInt64(condition.Value, CultureInfo.InvariantCulture);
                            if ((have & 0xFFFF) != (want & 0xFFFF)) return false;
                            if (have != want) log("GT7: room " + room["room_id"] + " has room_version " + have + ", the search asked for " + want + " (listed anyway)");
                            continue;
                        }
                        if (value == null || !string.Equals(Convert.ToString(value, CultureInfo.InvariantCulture),
                                Convert.ToString(condition.Value, CultureInfo.InvariantCulture), StringComparison.Ordinal))
                            return false;
                    }
                    return true;
                }).Select(entry => entry["room"]).ToList();
            }
            return new Dictionary<string, object> {
                { "matched_count", found.Count }, { "rooms", found.Skip(offset).Take(limit).ToArray() } };
        }

        static string AccountId(string onlineId)
        {
            ulong hash = 14695981039346656037UL;  // the same FNV-1a as RyGT's NP libraries
            foreach (char c in onlineId) { hash ^= c; hash *= 1099511628211UL; }
            return (hash & 0x7FFFFFFFFFFFFFFFUL).ToString(CultureInfo.InvariantCulture);
        }

        string Token(Dictionary<string, object> claims)
        {
            string header = Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
            string payload = Base64Url(Encoding.UTF8.GetBytes(json.Serialize(claims)));
            using (var hmac = new HMACSHA256(secret))
                return header + "." + payload + "." + Base64Url(hmac.ComputeHash(Encoding.ASCII.GetBytes(header + "." + payload)));
        }

        /// The player a "Bearer <token>" header belongs to, if the token is one this server signed.
        Dictionary<string, object> UserFromToken(string authorization)
        {
            if (authorization == null || !authorization.StartsWith("Bearer ", StringComparison.Ordinal)) return null;
            string[] parts = authorization.Substring(7).Trim().Split('.');
            if (parts.Length != 3) return null;
            using (var hmac = new HMACSHA256(secret))
                if (Base64Url(hmac.ComputeHash(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]))) != parts[2]) return null;
            var claims = json.DeserializeObject(Encoding.UTF8.GetString(FromBase64Url(parts[1]))) as Dictionary<string, object>;
            string onlineId = Field(claims, "np_online_id") as string;
            if (onlineId == null) return null;
            lock (usersLock)
            {
                Dictionary<string, object> user;
                return users.TryGetValue(onlineId, out user) ? user : null;
            }
        }

        static string Base64Url(byte[] data) { return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }

        static byte[] FromBase64Url(string text)
        {
            text = text.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(text + new string('=', (4 - text.Length % 4) % 4));
        }

        static object Field(object map, string key)
        {
            var dictionary = map as Dictionary<string, object>;
            object value;
            return dictionary != null && dictionary.TryGetValue(key, out value) ? value : null;
        }

        // ---- files ----

        HttpResponse ServerConfig()
        {
            string file = Path.Combine(directory, "server_config.json");
            byte[] data = File.Exists(file) ? File.ReadAllBytes(file)
                : FromPolyphony(StaticHost, "/server/config/all.json")
                  ?? Encoding.UTF8.GetBytes("{\"result\":{\"default\":{\"body\":{\"prefs\":{}}},\"version\":1}}");
            return FileReply(data, "application/json", "rygt-config");
        }

        // Polyphony's public file servers still serve GT7's delivered files (text, event lists,
        // style data) and its client settings. A file this server does not have or make is fetched
        // from them once and kept in gt7\cdn-cache\, so none of Polyphony's files ship with RyGT.
        const string AssetHost = "asset.gt7.game.gran-turismo.com", StaticHost = "static.gt7.game.gran-turismo.com";
        readonly Dictionary<string, DateTime> polyphonyMisses = new Dictionary<string, DateTime>();

        byte[] FromPolyphony(string host, string path)
        {
            string cached = Path.Combine(directory, "cdn-cache", host, ResponseFiles.SanitizePath(path.TrimStart('/')));
            if (File.Exists(cached)) return File.ReadAllBytes(cached);
            string url = "https://" + host + path;
            lock (polyphonyMisses)
            {
                DateTime missed;
                if (polyphonyMisses.TryGetValue(url, out missed) && DateTime.UtcNow - missed < TimeSpan.FromMinutes(10)) return null;
            }
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Timeout = 10000;
                request.ReadWriteTimeout = 10000;
                request.UserAgent = "TinyWeb";
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    byte[] data = buffer.ToArray();
                    Directory.CreateDirectory(Path.GetDirectoryName(cached));
                    File.WriteAllBytes(cached, data);
                    log("GT7: fetched " + url + " (" + data.Length + " bytes) into cdn-cache");
                    return data;
                }
            }
            catch (Exception e)
            {
                if (!(e is WebException || e is IOException || e is NotSupportedException)) throw;
                lock (polyphonyMisses) polyphonyMisses[url] = DateTime.UtcNow;
                log("GT7: " + url + " could not be fetched: " + e.Message);
                return null;
            }
        }

        static readonly Regex DatedFile = new Regex(@"^(shop_info|trade_in)/(\d{8})\.json$");
        static readonly Regex NewsFile = new Regex(@"^news/[^/]*/?gtnow\.json$");

        /// GET /delivery/data/<path>. Car lists and news are sent empty: today's real lists name
        /// cars and events that GT7 1.1.0 does not have.
        HttpResponse DeliveryFile(string path)
        {
            string file = Path.Combine(directory, "files", ResponseFiles.SanitizePath(path));
            if (File.Exists(file)) return FileReply(File.ReadAllBytes(file), path.EndsWith(".json") ? "application/json" : "binary/octet-stream", "rygt-file");

            // The car database for this version is gone from Polyphony's servers too, which GT7 accepts.
            if (path.StartsWith("dmsdata/", StringComparison.Ordinal))
            {
                var denied = HttpResponse.Text(403, "application/xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Error><Code>AccessDenied</Code><Message>Access Denied</Message></Error>");
                denied.Source = "GT7";
                return denied;
            }
            Match dated = DatedFile.Match(path);
            if (dated.Success && dated.Groups[1].Value == "shop_info")
            {
                var shop = new Dictionary<string, object> {
                    { "brand_central_dealer", new Dictionary<string, object> { { "online_car_shop", EmptyCars() }, { "date", dated.Groups[2].Value } } },
                    { "trade_in", new Dictionary<string, object> { { "trade_in", EmptyCars() } } },
                    { "legend_car_dealer", new Dictionary<string, object> { { "online_car_shop", EmptyCars() } } },
                    { "used_car_dealer", new Dictionary<string, object> { { "online_car_shop", EmptyCars() } } } };
                return FileReply(Encoding.UTF8.GetBytes(json.Serialize(shop)), "application/json", "rygt-shop-" + dated.Groups[2].Value);
            }
            if (dated.Success)
                return FileReply(Encoding.UTF8.GetBytes(json.Serialize(new Dictionary<string, object> { { "trade_in", EmptyCars() } })),
                    "application/json", "rygt-trade-" + dated.Groups[2].Value);
            if (NewsFile.IsMatch(path))
                return FileReply(Encoding.UTF8.GetBytes("{\"gtnow\":[],\"live\":[],\"topic\":[]}"), "application/json", "rygt-news");
            if (path == "seasonalcc/schedule.json")
                return FileReply(Encoding.UTF8.GetBytes("[]"), "application/json", "rygt-seasonal");
            // Everything else (the text, World Circuit event lists, style data) is Polyphony's own file.
            byte[] original = FromPolyphony(AssetHost, "/delivery/data/" + path);
            if (original != null)
                return FileReply(original, path.EndsWith(".json") ? "application/json" : "binary/octet-stream", "pdi-" + original.Length);
            return null;
        }

        static Dictionary<string, object> EmptyCars() { return new Dictionary<string, object> { { "cars", new object[0] } }; }

        static HttpResponse FileReply(byte[] data, string contentType, string etag)
        {
            var response = new HttpResponse { Body = VegasCodec.IsMessage(data) ? data : VegasCodec.Encode(data), Source = "GT7" };
            if (!VegasCodec.IsMessage(data)) response.Decoded = Encoding.UTF8.GetString(data);
            response.Headers.Add(new KeyValuePair<string, string>("Content-Type", contentType));
            response.Headers.Add(new KeyValuePair<string, string>("ETag", "\"" + etag + "\""));
            return response;
        }
    }
}
