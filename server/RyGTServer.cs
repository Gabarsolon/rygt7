// RyGTServer: stands in for PSN and GT Sport's online servers. RyGT's libSceHttp sends every
// request the game makes here as plain HTTP, keeping the original Host header, so one server
// answers for all of the game's hosts. Run it on your own PC, or on one PC that your friends point
// their RyGT at.
//
// The server answers the online save, Sport Mode's events and the leaderboards itself. Everything
// else it records and answers from files you put in responses\, which is how GT Sport's protocol
// gets worked out one request at a time.
// Built with build.cmd (C# for .NET Framework 4.x, which Windows includes).
// SPDX-License-Identifier: GPL-2.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace RyGT.Server
{
    class Options
    {
        public int Port = 7890;
        public string DataDirectory;
        public int DefaultStatus = 404;
        public bool Capture = true;
        public string InviteToken = "";   // --invite: what friends' invite codes carry
        public string ShadNetDirectory;   // --shadnet: the shadNet folder, for creating accounts

        public static Options Parse(string[] args)
        {
            var options = new Options { DataDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "server-data") };
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                string next = i + 1 < args.Length ? args[i + 1] : null;
                if (arg == "--port" && next != null) { options.Port = int.Parse(next, CultureInfo.InvariantCulture); i++; }
                else if (arg == "--data" && next != null) { options.DataDirectory = Path.GetFullPath(next); i++; }
                else if (arg == "--default-status" && next != null) { options.DefaultStatus = int.Parse(next, CultureInfo.InvariantCulture); i++; }
                else if (arg == "--no-capture") options.Capture = false;
                else if (arg == "--invite" && next != null) { options.InviteToken = next; i++; }
                else if (arg == "--shadnet" && next != null) { options.ShadNetDirectory = Path.GetFullPath(next); i++; }
                else if (arg == "--help" || arg == "-h" || arg == "/?") return null;
                else throw new ArgumentException("Unknown option " + arg);
            }
            return options;
        }

        public const string Usage =
            "RyGTServer [--port 7890] [--data <folder>] [--default-status 404] [--no-capture]\n" +
            "           [--invite <token> --shadnet <folder>]\n" +
            "  --port            TCP port to listen on (all network interfaces).\n" +
            "  --data            Folder for responses\\ and captures\\ (default: server-data next to RyGTServer.exe).\n" +
            "  --default-status  Status for requests with no response file (default 404).\n" +
            "  --no-capture      Do not save each request and response under captures\\.\n" +
            "  --invite          Lets players with this token join: RyGT's launcher sends it from an invite\n" +
            "                    code, and the server creates their shadNet account (needs --shadnet).\n" +
            "  --shadnet         The folder of the shadNet server running on this PC (its shadnet.cfg).";
    }

    class HttpRequest
    {
        public string Method = "", Target = "/", Version = "HTTP/1.1";
        public readonly List<KeyValuePair<string, string>> Headers = new List<KeyValuePair<string, string>>();
        public byte[] Body = new byte[0];
        public IPAddress Remote;
        public IPAddress Local;   // this server's address on the connection the request came in on
        public string Decoded;    // the body as text when it was a GT7 (Vegas) message

        public string Path { get { int q = Target.IndexOf('?'); return q < 0 ? Target : Target.Substring(0, q); } }
        public string Query { get { int q = Target.IndexOf('?'); return q < 0 ? "" : Target.Substring(q + 1); } }

        public string Header(string name)
        {
            foreach (var header in Headers)
                if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase)) return header.Value;
            return null;
        }

        /// The host the game meant to reach, without a port.
        public string Host
        {
            get
            {
                string host = Header("Host") ?? "";
                int colon = host.LastIndexOf(':');
                if (colon > 0 && host.IndexOf(']') < 0) host = host.Substring(0, colon);
                return host.ToLowerInvariant();
            }
        }

        /// RyGT's libSceHttp marks the game's requests, and GT7's own HTTP client (sent here by RyGT's
        /// libSceNet and libSceSsl) names Polyphony's hosts; anything else is a browser or a tool.
        public bool FromGame
        {
            get
            {
                return Header("X-RyGT-Scheme") != null || Host.EndsWith("gran-turismo.com", StringComparison.Ordinal)
                    || Host.EndsWith("granturismo-online.net", StringComparison.Ordinal)
                    || Host.EndsWith("{base_domain}", StringComparison.Ordinal);  // GT7 hosts it never filled in
            }
        }

        string command;
        bool commandRead;

        /// The Grim command of a JSON request body ({"command": "stats.get", "params": [...]}), or null.
        public string Command
        {
            get
            {
                if (commandRead) return command;
                commandRead = true;
                // Commands with a binary body (save2.update) carry their JSON base64-encoded in x-gt-json.
                string text = null;
                string header = Header("x-gt-json");
                if (header != null)
                {
                    try { text = Encoding.UTF8.GetString(Convert.FromBase64String(header)); }
                    catch (FormatException) { }
                }
                if (text == null && Body.Length > 0 && Body[0] == (byte)'{') text = Encoding.UTF8.GetString(Body);
                if (text == null) return null;
                try
                {
                    var json = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
                    object value;
                    if (json != null && json.TryGetValue("command", out value)) command = value as string;
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                return command;
            }
        }

        Dictionary<string, object> parameters;

        /// The command's "params": [{"board_id": 1}, {"score": 2}] merged into one map.
        public Dictionary<string, object> Params()
        {
            if (parameters != null) return parameters;
            parameters = new Dictionary<string, object>();
            try
            {
                string header = Header("x-gt-json");
                string text = header != null ? Encoding.UTF8.GetString(Convert.FromBase64String(header)) : Encoding.UTF8.GetString(Body);
                var json = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
                object list;
                if (json != null && json.TryGetValue("params", out list) && list is object[])
                    foreach (var item in (object[])list)
                    {
                        var map = item as Dictionary<string, object>;
                        if (map != null) foreach (var pair in map) parameters[pair.Key] = pair.Value;
                    }
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (FormatException) { }
            return parameters;
        }

        public string Param(string name)
        {
            object value;
            return Params().TryGetValue(name, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        }

        public long ParamLong(string name, long fallback)
        {
            long value;
            return long.TryParse(Param(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        const int MaxBody = 64 << 20;

        public static HttpRequest Read(Stream stream)
        {
            var head = new MemoryStream();
            int matched = 0;
            while (matched < 4)
            {
                int b = stream.ReadByte();
                if (b < 0) return null;
                head.WriteByte((byte)b);
                matched = (b == (matched % 2 == 0 ? '\r' : '\n')) ? matched + 1 : (b == '\r' ? 1 : 0);
                if (head.Length > 1 << 20) throw new InvalidDataException("Request headers too large");
            }
            string[] lines = Encoding.ASCII.GetString(head.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] start = lines[0].Split(' ');
            if (start.Length < 3) throw new InvalidDataException("Bad request line: " + lines[0]);
            var request = new HttpRequest { Method = start[0], Target = start[1], Version = start[2] };
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                request.Headers.Add(new KeyValuePair<string, string>(lines[i].Substring(0, colon).Trim(), lines[i].Substring(colon + 1).Trim()));
            }
            long length;
            if (long.TryParse(request.Header("Content-Length"), NumberStyles.None, CultureInfo.InvariantCulture, out length) && length > 0)
            {
                if (length > MaxBody) throw new InvalidDataException("Request body too large");
                request.Body = new byte[length];
                int read = 0;
                while (read < length)
                {
                    int n = stream.Read(request.Body, read, (int)length - read);
                    if (n <= 0) throw new EndOfStreamException();
                    read += n;
                }
            }
            return request;
        }
    }

    class HttpResponse
    {
        public int Status = 200;
        public readonly List<KeyValuePair<string, string>> Headers = new List<KeyValuePair<string, string>>();
        public byte[] Body = new byte[0];
        public string Source = "";
        public string Decoded;    // the body as text when it is a GT7 (Vegas) message

        public static HttpResponse Text(int status, string contentType, string text)
        {
            var response = new HttpResponse { Status = status, Body = Encoding.UTF8.GetBytes(text) };
            response.Headers.Add(new KeyValuePair<string, string>("Content-Type", contentType));
            return response;
        }

        public void Write(Stream stream)
        {
            var head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(Status).Append(' ').Append(Reason(Status)).Append("\r\n");
            foreach (var header in Headers)
            {
                if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(header.Key, "Connection", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(header.Key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
                head.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            }
            head.Append("Content-Length: ").Append(Body.Length).Append("\r\n");
            head.Append("Connection: close\r\n\r\n");
            byte[] bytes = Encoding.ASCII.GetBytes(head.ToString());
            stream.Write(bytes, 0, bytes.Length);
            stream.Write(Body, 0, Body.Length);
            stream.Flush();
        }

        public string HeaderText()
        {
            var text = new StringBuilder();
            text.Append("HTTP/1.1 ").Append(Status).Append(' ').Append(Reason(Status)).Append("\r\n");
            foreach (var header in Headers) text.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            return text.ToString();
        }

        static string Reason(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 201: return "Created";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 500: return "Internal Server Error";
                case 503: return "Service Unavailable";
                default: return "Status";
            }
        }
    }

    /// Players seen on this server, by the online ID their RyGT sends with each request.
    class Players
    {
        public class Player
        {
            public string OnlineId;
            public string Address;
            public DateTime FirstSeen, LastSeen;
            public long Requests;
        }

        static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(5);
        readonly Dictionary<string, Player> players = new Dictionary<string, Player>(StringComparer.OrdinalIgnoreCase);

        /// Returns true the first time a player is seen (or seen again after going quiet).
        public bool Touch(string onlineId, IPAddress address)
        {
            if (string.IsNullOrEmpty(onlineId)) return false;
            lock (players)
            {
                Player player;
                DateTime now = DateTime.UtcNow;
                bool joined = !players.TryGetValue(onlineId, out player) || now - player.LastSeen > OnlineWindow;
                if (player == null)
                {
                    player = new Player { OnlineId = onlineId, FirstSeen = now };
                    players[onlineId] = player;
                }
                player.Address = address.ToString();
                player.LastSeen = now;
                player.Requests++;
                return joined;
            }
        }

        public List<Player> Online()
        {
            lock (players)
            {
                DateTime now = DateTime.UtcNow;
                return players.Values.Where(p => now - p.LastSeen <= OnlineWindow).OrderBy(p => p.FirstSeen).ToList();
            }
        }
    }

    /// Answers from files: responses\<host>\<path> holds the body to send back, and an optional
    /// <same name>.headers file holds header lines, with "Status: <code>" setting the status.
    /// "<path>@<query>" is tried before "<path>" so different queries can get different answers,
    /// and a path ending in "/" is looked up as "<path>/_index".
    class ResponseFiles
    {
        readonly string root;

        public ResponseFiles(string root) { this.root = root; }

        public HttpResponse Find(HttpRequest request)
        {
            var candidates = new List<string>();
            // Grim commands all POST to one endpoint ({"command": "stats.get", ...} to /scea/ap/profile/),
            // so they are answered from <endpoint folder>\<command>.
            if (request.Command != null)
                candidates.Add(Path.Combine(root, Sanitize(request.Host), SanitizePath(request.Path), Sanitize(request.Command)));
            string path = request.Path;
            if (path.EndsWith("/")) path += "_index";
            string basePath = Path.Combine(root, Sanitize(request.Host), SanitizePath(path));
            if (request.Query.Length > 0) candidates.Add(basePath + "@" + Sanitize(request.Query));
            candidates.Add(basePath);
            foreach (string candidate in candidates)
            {
                string file = File.Exists(candidate + "." + request.Method.ToUpperInvariant()) ? candidate + "." + request.Method.ToUpperInvariant() : candidate;
                if (!File.Exists(file)) continue;
                var response = new HttpResponse { Body = File.ReadAllBytes(file), Source = file };
                string headers = file + ".headers";
                if (File.Exists(headers))
                {
                    foreach (string line in File.ReadAllLines(headers))
                    {
                        int colon = line.IndexOf(':');
                        if (colon <= 0) continue;
                        string name = line.Substring(0, colon).Trim(), value = line.Substring(colon + 1).Trim();
                        if (name.Equals("Status", StringComparison.OrdinalIgnoreCase)) response.Status = int.Parse(value, CultureInfo.InvariantCulture);
                        else response.Headers.Add(new KeyValuePair<string, string>(name, value));
                    }
                }
                return response;
            }
            return null;
        }

        public static string Sanitize(string text)
        {
            var result = new StringBuilder(text.Length);
            foreach (char c in text)
                result.Append(c < 32 || "<>:\"/\\|?*".IndexOf(c) >= 0 ? '_' : c);
            string name = result.ToString().Trim('.', ' ');
            if (name.Length > 120) name = name.Substring(0, 120);
            return name.Length == 0 ? "_" : name;
        }

        public static string SanitizePath(string path)
        {
            return string.Join("\\", path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s == ".." ? "_" : Sanitize(s)));
        }
    }

    /// Saves every exchange with the game, so its protocol can be studied: one numbered .txt per
    /// request with both sides' headers and any text bodies, plus the raw bodies as .bin files.
    class Captures
    {
        readonly string directory;
        int sequence;

        public Captures(string root)
        {
            directory = Path.Combine(root, "captures", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(directory);
        }

        public string Directory_ { get { return directory; } }

        public void Save(HttpRequest request, HttpResponse response)
        {
            int number = Interlocked.Increment(ref sequence);
            string name = string.Format(CultureInfo.InvariantCulture, "{0:D5}_{1}_{2}{3}{4}", number, request.Method,
                ResponseFiles.Sanitize(request.Host), ResponseFiles.Sanitize(request.Path.Replace('/', '_')),
                request.Command != null ? "_" + ResponseFiles.Sanitize(request.Command) : "");
            if (name.Length > 150) name = name.Substring(0, 150);
            string stem = Path.Combine(directory, name);

            var text = new StringBuilder();
            text.Append("# from ").Append(request.Remote).Append(" at ").Append(DateTime.Now.ToString("o", CultureInfo.InvariantCulture)).Append("\r\n");
            text.Append(request.Method).Append(' ').Append(request.Target).Append(' ').Append(request.Version).Append("\r\n");
            foreach (var header in request.Headers) text.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            text.Append("\r\n");
            AppendBody(text, request.Body);
            text.Append("\r\n# response").Append(response.Source.Length > 0 ? " from " + response.Source : "").Append("\r\n");
            text.Append(response.HeaderText()).Append("\r\n");
            AppendBody(text, response.Body);
            File.WriteAllText(stem + ".txt", text.ToString(), Encoding.UTF8);
            if (request.Body.Length > 0) File.WriteAllBytes(stem + ".request.bin", request.Body);
            if (request.Decoded != null) File.WriteAllText(stem + ".request.json", request.Decoded, Encoding.UTF8);
            if (response.Decoded != null) File.WriteAllText(stem + ".response.json", response.Decoded, Encoding.UTF8);
            if (response.Body.Length > 0) File.WriteAllBytes(stem + ".response.bin", response.Body);
        }

        static void AppendBody(StringBuilder text, byte[] body)
        {
            if (body.Length == 0) return;
            if (LooksLikeText(body)) text.Append(Encoding.UTF8.GetString(body)).Append("\r\n");
            else text.Append("<").Append(body.Length).Append(" bytes of binary data>\r\n");
        }

        static bool LooksLikeText(byte[] body)
        {
            int limit = Math.Min(body.Length, 4096);
            for (int i = 0; i < limit; i++)
            {
                byte b = body[i];
                if (b < 9 || (b > 13 && b < 32) || b == 127) return false;
            }
            return true;
        }
    }

    /// GT Sport's online save (Grim's save2/savef commands), kept per player under
    /// server-data\players\<online id>\. Once online, the game will not save at all unless this
    /// answers. The game sends its own save's hash (save2.setHash) before uploading it
    /// (save2.update), and save2.getHash returns that stored hash, so after the first upload the
    /// save is "in sync" and nothing is ever downloaded over the local save. Every upload keeps the
    /// previous file as a backup.
    class OnlineSave
    {
        const int OnlineSaveCanUpdate = 1; // ONLINE_SAVE_CAN_UPDATE in the game's save scripts
        const int BackupsKept = 5;
        readonly string root;
        readonly object gate = new object();
        readonly Action<string> log;

        public OnlineSave(string dataDirectory, Action<string> log)
        {
            root = Path.Combine(dataDirectory, "players");
            this.log = log;
        }

        string PlayerDirectory(string onlineId)
        {
            string dir = Path.Combine(root, ResponseFiles.Sanitize(onlineId.Length > 0 ? onlineId : "_anonymous"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        static HttpResponse Json(object value)
        {
            return GrimReply.Json(value);
        }

        static Dictionary<string, object> Result(object value)
        {
            return new Dictionary<string, object> { { "result", value } };
        }

        public static void WriteAtomically(string path, byte[] bytes)
        {
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        /// Answers the online-save requests; null for anything else.
        public HttpResponse Handle(HttpRequest request, string onlineId)
        {
            string command = request.Command;
            if (command == null && request.Method == "GET" && request.Path.Contains("/save/") && request.Path.EndsWith(".dat"))
                command = "download";
            if (command == null) return null;

            lock (gate)
            {
                string dir = PlayerDirectory(onlineId);
                string hashFile = Path.Combine(dir, "save.hash"), saveFile = Path.Combine(dir, "save.dat"), savefFile = Path.Combine(dir, "savef.json");
                switch (command)
                {
                    case "save2.setHash":
                    {
                        File.WriteAllText(hashFile, request.Param("hash") ?? "");
                        return Json(Result(OnlineSaveCanUpdate));
                    }
                    case "save2.getHash":
                        return Json(Result(File.Exists(hashFile) ? File.ReadAllText(hashFile) : ""));
                    case "save2.update":
                    {
                        if (File.Exists(saveFile))
                        {
                            File.Copy(saveFile, Path.Combine(dir, "save." + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".dat"), true);
                            foreach (var old in Directory.GetFiles(dir, "save.2*.dat").OrderByDescending(f => f).Skip(BackupsKept)) File.Delete(old);
                        }
                        WriteAtomically(saveFile, request.Body);
                        log("online save of " + onlineId + " stored (" + request.Body.Length + " bytes)");
                        return Json(Result(OnlineSaveCanUpdate));
                    }
                    case "download":
                        if (!File.Exists(saveFile)) return new HttpResponse { Status = 404 };
                        return new HttpResponse { Body = File.ReadAllBytes(saveFile), Source = saveFile };
                    case "savef.get":
                        return Json(new Dictionary<string, object> {
                            { "savef", File.Exists(savefFile) ? new JavaScriptSerializer().DeserializeObject(File.ReadAllText(savefFile)) : new Dictionary<string, object>() } });
                    case "savef.update":
                        File.WriteAllText(savefFile, new JavaScriptSerializer().Serialize(request.Params()));
                        return Json(Result(OnlineSaveCanUpdate));
                    default:
                        return null;
                }
            }
        }
    }

    static class GrimReply
    {
        public static HttpResponse Json(object value)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            HttpResponse response = HttpResponse.Text(200, "application/json", serializer.Serialize(value));
            response.Source = "RyGTServer";
            return response;
        }

        public static HttpResponse Json(string key, object value)
        {
            return Json(new Dictionary<string, object> { { key, value } });
        }

        /// Grim's times, read by pdistd::MTime.setByISOformat.
        public static string Time(DateTime utc)
        {
            return utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        /// A stable positive number for an online ID, used where Grim wants a user number.
        public static long UserNumber(string onlineId)
        {
            uint hash = 2166136261;
            foreach (char c in onlineId.ToLowerInvariant()) hash = (hash ^ c) * 16777619;
            return (hash & 0x7fffffff) | 1;
        }
    }

    /// Sport Mode's events (Grim's /ap/event/ commands), from server-data\events\: one GameParameter
    /// file per event (named after its event_id) and schedule.json, which says which event each
    /// Daily Race channel runs and which events each event folder holds (folder 22 is Time Trial).
    /// Every scheduled event runs from midnight UTC today for a week, so it is always open.
    class OnlineEvents
    {
        readonly string root;

        public OnlineEvents(string dataDirectory)
        {
            root = Path.Combine(dataDirectory, "events");
            Directory.CreateDirectory(root);
        }

        Dictionary<string, object> Schedule()
        {
            string path = Path.Combine(root, "schedule.json");
            if (!File.Exists(path)) return new Dictionary<string, object>();
            return new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>
                ?? new Dictionary<string, object>();
        }

        static Dictionary<string, object> Section(Dictionary<string, object> schedule, string name)
        {
            object value;
            return schedule.TryGetValue(name, out value) ? value as Dictionary<string, object> ?? new Dictionary<string, object>() : new Dictionary<string, object>();
        }

        static IEnumerable<string> Csv(string text)
        {
            return (text ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0);
        }

        public HttpResponse Handle(HttpRequest request)
        {
            switch (request.Command)
            {
                case "event_calendar.get": return Calendar(request);
                case "event.get": return Events(request);
                case "event_folder.get": return Folder(request);
                default: return null;
            }
        }

        HttpResponse Calendar(HttpRequest request)
        {
            var channels = Section(Schedule(), "daily_channels");
            DateTime start = DateTime.UtcNow.Date;
            long day = (long)(start - new DateTime(2017, 1, 1)).TotalDays;
            var list = new List<object>();
            foreach (string channel in Csv(request.Param("channel_id_csv")))
            {
                object eventId;
                if (!channels.TryGetValue(channel, out eventId)) continue;
                list.Add(new Dictionary<string, object> {
                    { "channel_id", long.Parse(channel, CultureInfo.InvariantCulture) },
                    { "event_calendar_id", long.Parse(channel, CultureInfo.InvariantCulture) * 100000 + day },
                    { "event_id", eventId },
                    { "region_id", request.ParamLong("region_id", 0) },
                    { "start_time", GrimReply.Time(start) },
                    { "end_time", GrimReply.Time(start.AddDays(7)) } });
            }
            return GrimReply.Json("event_calendar", list);
        }

        HttpResponse Events(HttpRequest request)
        {
            var requiredTimes = Section(Schedule(), "required_time");
            var list = new List<object>();
            foreach (string id in Csv(request.Param("event_id_csv")))
            {
                string path = Path.Combine(root, ResponseFiles.Sanitize(id) + ".json");
                if (!File.Exists(path)) continue;
                object required;
                string modified = GrimReply.Time(File.GetLastWriteTimeUtc(path));
                list.Add(new Dictionary<string, object> {
                    { "event_id", long.Parse(id, CultureInfo.InvariantCulture) },
                    { "create_time", modified }, { "update_time", modified },
                    { "value", new object[] { WithDates(File.ReadAllText(path), DateFormat(id)), requiredTimes.TryGetValue(id, out required) ? required : 0 } } });
            }
            return GrimReply.Json("event", list);
        }

        /// Time Trial lists only events whose begin_date..end_date covers now, so an event file without
        /// its own dates gets this week's. schedule.json's "date_format" can set the text format per
        /// event ({"9100002": "yyyy/MM/dd HH:mm:ss"}); the default is ISO 8601.
        string DateFormat(string eventId)
        {
            object format;
            return Section(Schedule(), "date_format").TryGetValue(eventId, out format) && format is string
                ? (string)format : "yyyy-MM-dd'T'HH:mm:ss'Z'";
        }

        static string WithDates(string gameParameter, string format)
        {
            const string events = "\"events\":[{";
            int at = gameParameter.IndexOf(events, StringComparison.Ordinal);
            if (at < 0 || gameParameter.IndexOf("\"begin_date\"", StringComparison.Ordinal) >= 0) return gameParameter;
            DateTime start = DateTime.UtcNow.Date;
            string dates = string.Format(CultureInfo.InvariantCulture, "\"begin_date\":\"{0}\",\"end_date\":\"{1}\",",
                start.ToString(format, CultureInfo.InvariantCulture), start.AddDays(7).ToString(format, CultureInfo.InvariantCulture));
            return gameParameter.Insert(at + events.Length, dates);
        }

        HttpResponse Folder(HttpRequest request)
        {
            string folder = request.Param("folder_id") ?? "";
            object ids;
            var list = new List<object>();
            if (Section(Schedule(), "folders").TryGetValue(folder, out ids) && ids is object[])
            {
                int index = 0;
                foreach (object id in (object[])ids)
                    list.Add(new Dictionary<string, object> {
                        { "event_id", id }, { "folder_id", long.Parse(folder, CultureInfo.InvariantCulture) },
                        { "index", index++ }, { "region_id", request.ParamLong("region_id", 0) } });
            }
            return GrimReply.Json("event_folder", list);
        }
    }

    /// Leaderboards (Grim's /ap/ranking/ commands), one file per board under
    /// server-data\rankings\. Each player keeps their best score on a board; a lower score is
    /// better, as it is for lap times. Replays uploaded with a record are kept next to the boards.
    class Rankings
    {
        readonly string root;
        readonly object gate = new object();
        readonly Action<string> log;

        public Rankings(string dataDirectory, Action<string> log)
        {
            root = Path.Combine(dataDirectory, "rankings");
            Directory.CreateDirectory(Path.Combine(root, "replays"));
            this.log = log;
        }

        class Board
        {
            public long NextId = 1;
            public List<Dictionary<string, object>> Records = new List<Dictionary<string, object>>();
        }

        string BoardPath(long boardId)
        {
            return Path.Combine(root, boardId.ToString(CultureInfo.InvariantCulture) + ".json");
        }

        Board Load(long boardId)
        {
            var board = new Board();
            string path = BoardPath(boardId);
            if (!File.Exists(path)) return board;
            var json = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
            if (json == null) return board;
            object value;
            if (json.TryGetValue("next_id", out value)) board.NextId = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            if (json.TryGetValue("records", out value) && value is object[])
                board.Records = ((object[])value).OfType<Dictionary<string, object>>().ToList();
            return board;
        }

        void Save(long boardId, Board board)
        {
            var json = new Dictionary<string, object> { { "next_id", board.NextId }, { "records", board.Records } };
            OnlineSave.WriteAtomically(BoardPath(boardId), Encoding.UTF8.GetBytes(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(json)));
        }

        static long Score(Dictionary<string, object> record)
        {
            return Convert.ToInt64(record["score"], CultureInfo.InvariantCulture);
        }

        /// The board's records best first, each with its rank.
        static List<Dictionary<string, object>> Ranked(Board board)
        {
            var sorted = board.Records.OrderBy(Score).ThenBy(r => Convert.ToString(r["create_time"], CultureInfo.InvariantCulture)).ToList();
            for (int i = 0; i < sorted.Count; i++) sorted[i]["rank"] = i + 1;
            return sorted;
        }

        static Dictionary<string, object> Find(List<Dictionary<string, object>> records, long userNo, string onlineId)
        {
            return records.FirstOrDefault(r => Convert.ToInt64(r["user_no"], CultureInfo.InvariantCulture) == userNo)
                ?? records.FirstOrDefault(r => string.Equals((string)r["user_id"], onlineId, StringComparison.OrdinalIgnoreCase));
        }

        static IEnumerable<long> Boards(HttpRequest request)
        {
            string csv = request.Param("board_id_csv") ?? request.Param("board_id") ?? "";
            foreach (string id in csv.Split(','))
            {
                long value;
                if (long.TryParse(id.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) yield return value;
            }
        }

        /// Records from begin to end; the game counts from 1.
        static List<Dictionary<string, object>> Range(List<Dictionary<string, object>> records, HttpRequest request)
        {
            long begin = Math.Max(1, request.ParamLong("begin", 1)), end = request.ParamLong("end", begin + 99);
            return records.Skip((int)Math.Min(begin - 1, int.MaxValue)).Take((int)Math.Max(0, Math.Min(end - begin + 1, 1000))).ToList();
        }

        public HttpResponse Handle(HttpRequest request, string onlineId)
        {
            string command = request.Command;
            if (command == null && request.Method == "GET" && request.Path.Contains("/ranking/") && request.Path.EndsWith(".dat"))
            {
                string file = Path.Combine(root, "replays", ResponseFiles.Sanitize(Path.GetFileName(request.Path)));
                return File.Exists(file) ? new HttpResponse { Body = File.ReadAllBytes(file), Source = file } : null;
            }
            if (command == null || !command.StartsWith("ranking.", StringComparison.Ordinal)) return null;

            long requestedUser = request.ParamLong("user_no", 0);
            long me = GrimReply.UserNumber(onlineId);
            lock (gate)
            {
                switch (command)
                {
                    case "ranking.get":
                    case "ranking.get2":
                    case "ranking.get2MultiBoard":
                    {
                        var found = new List<object>();
                        foreach (long boardId in Boards(request))
                        {
                            // The game asks with its own user number, which is not always ours.
                            bool mine = requestedUser == 0 || requestedUser == me;
                            var record = Find(Ranked(Load(boardId)), mine ? me : requestedUser, mine ? onlineId : null);
                            if (record != null) found.Add(record);
                        }
                        return GrimReply.Json("ranking", found);
                    }
                    case "ranking.getList":
                    case "ranking.getListMultiBoard":
                    case "ranking.getListFriend":
                    {
                        var all = new List<object>();
                        foreach (long boardId in Boards(request))
                            all.AddRange(command == "ranking.getListFriend" ? Ranked(Load(boardId)) : Range(Ranked(Load(boardId)), request));
                        return GrimReply.Json("ranking", all);
                    }
                    case "ranking.getCount":
                        return GrimReply.Json("result", Boards(request).Sum(b => Load(b).Records.Count));
                    case "ranking.calcTemporary":
                    {
                        long score = request.ParamLong("score", long.MaxValue);
                        long boardId = Boards(request).FirstOrDefault();
                        return GrimReply.Json("result", Load(boardId).Records.Count(r => Score(r) < score) + 1);
                    }
                    case "ranking.update":
                    {
                        long boardId = Boards(request).FirstOrDefault();
                        long score = request.ParamLong("score", long.MaxValue);
                        Board board = Load(boardId);
                        var record = Find(board.Records, me, onlineId);
                        if (record == null)
                        {
                            record = new Dictionary<string, object> {
                                { "ranking_id", boardId * 1000000 + board.NextId++ }, { "board_id", boardId },
                                { "user_no", me }, { "user_id", onlineId }, { "account_id", me },
                                { "user_country", "us" }, { "driver_class", 0 }, { "manner_point", 0 }, { "driver_star", 0 },
                                { "comment", "" }, { "replay", 0 }, { "score", long.MaxValue }, { "stats", "" },
                                { "create_time", GrimReply.Time(DateTime.UtcNow) } };
                            board.Records.Add(record);
                        }
                        if (score < Score(record))
                        {
                            record["score"] = score;
                            record["stats"] = request.Param("stats") ?? "";
                            record["replay"] = 0;
                            record["create_time"] = GrimReply.Time(DateTime.UtcNow);
                            log(string.Format(CultureInfo.InvariantCulture, "new best on board {0} for {1}: {2}", boardId, onlineId, score));
                        }
                        Save(boardId, board);
                        return GrimReply.Json("result", Ranked(board).IndexOf(record) + 1);
                    }
                    case "ranking.uploadReplay":
                    {
                        long boardId = Boards(request).FirstOrDefault();
                        Board board = Load(boardId);
                        var record = Find(board.Records, me, onlineId);
                        if (record == null || request.Body.Length == 0) return GrimReply.Json("result", 0);
                        string name = string.Format(CultureInfo.InvariantCulture, "{0}_{1}.dat", record["ranking_id"], request.ParamLong("index", 0));
                        OnlineSave.WriteAtomically(Path.Combine(root, "replays", name), request.Body);
                        record["replay"] = 1;
                        Save(boardId, board);
                        return GrimReply.Json("result", 1);
                    }
                    default:
                        return null;
                }
            }
        }
    }

    /// Joining by invite code. A host's launcher starts this server with an invite token and puts the
    /// token in the invite code it shows; a player's launcher sends the token and the online ID they
    /// want (GET /rygt/invite/join?token=...&online=...), and gets back the password of a shadNet
    /// account made for them on this PC's shadNet (whose sign-up only answers this PC, with the
    /// MemberApiKey from shadnet.cfg). The passwords handed out are kept in server-data\accounts.json,
    /// so joining again with the same ID gives the same account.
    class Invites
    {
        readonly Options options;
        readonly Action<string> log;
        readonly object gate = new object();
        readonly string accountsPath;

        public Invites(Options options, Action<string> log)
        {
            this.options = options;
            this.log = log;
            accountsPath = Path.Combine(options.DataDirectory, "accounts.json");
        }

        static string Query(HttpRequest request, string name)
        {
            foreach (string part in request.Query.Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq > 0 && part.Substring(0, eq) == name) return Uri.UnescapeDataString(part.Substring(eq + 1));
            }
            return null;
        }

        static HttpResponse Reply(int status, bool ok, string message, Dictionary<string, object> extra = null)
        {
            var body = new Dictionary<string, object> { { "result", ok }, { "message", message } };
            if (extra != null) foreach (var pair in extra) body[pair.Key] = pair.Value;
            return HttpResponse.Text(status, "application/json", new JavaScriptSerializer().Serialize(body));
        }

        Dictionary<string, object> LoadAccounts()
        {
            try
            {
                return File.Exists(accountsPath)
                    ? new JavaScriptSerializer().DeserializeObject(File.ReadAllText(accountsPath)) as Dictionary<string, object> ?? new Dictionary<string, object>()
                    : new Dictionary<string, object>();
            }
            catch (ArgumentException) { return new Dictionary<string, object>(); }
        }

        string ShadNetSetting(string key)
        {
            if (options.ShadNetDirectory == null) return null;
            string cfg = Path.Combine(options.ShadNetDirectory, "shadnet.cfg");
            if (!File.Exists(cfg)) return null;
            foreach (string line in File.ReadAllLines(cfg))
            {
                int eq = line.IndexOf('=');
                if (eq > 0 && line.Substring(0, eq).Trim() == key) return line.Substring(eq + 1).Trim();
            }
            return null;
        }

        /// Creates the shadNet account; null when it worked, else why not.
        string Register(string onlineId, string password)
        {
            string port = ShadNetSetting("MemberApiPort") ?? "31360";
            string key = ShadNetSetting("MemberApiKey") ?? "";
            var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/member/v1/register");
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Timeout = 10000;
            if (key.Length > 0) request.Headers["X-Member-Api-Key"] = key;
            byte[] body = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(new Dictionary<string, object> {
                { "npid", onlineId }, { "email", onlineId + "@shadps4.local" }, { "password", password } }));
            try
            {
                using (var stream = request.GetRequestStream()) stream.Write(body, 0, body.Length);
                using (request.GetResponse()) { }
                return null;
            }
            catch (WebException e)
            {
                var response = e.Response as HttpWebResponse;
                if (response == null) return "shadNet is not running on the host";
                using (response)
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    string text = reader.ReadToEnd();
                    var json = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
                    object error, message;
                    if (json != null && json.TryGetValue("error", out error) && error is Dictionary<string, object> &&
                        ((Dictionary<string, object>)error).TryGetValue("message", out message))
                        return Convert.ToString(message, CultureInfo.InvariantCulture);
                    return "shadNet answered " + (int)response.StatusCode;
                }
            }
        }

        public HttpResponse Join(HttpRequest request)
        {
            if (options.InviteToken.Length == 0)
                return Reply(403, false, "This server is not taking invites (start it from RyGT's Start hosting).");
            string token = Query(request, "token") ?? "";
            string onlineId = (Query(request, "online") ?? "").Trim();
            if (!string.Equals(token, options.InviteToken, StringComparison.Ordinal))
                return Reply(403, false, "That invite code is not this server's current one. Ask the host for a new code.");
            if (onlineId.Length < 3 || onlineId.Length > 16 || onlineId.Any(c => !(char.IsLetterOrDigit(c) && c < 128) && c != '-' && c != '_'))
                return Reply(400, false, "An online ID is 3-16 letters, digits, - or _.");

            lock (gate)
            {
                var accounts = LoadAccounts();
                object known;
                if (accounts.TryGetValue(onlineId, out known) && known is string)
                {
                    log("invite: " + onlineId + " joined again from " + request.Remote);
                    return Reply(200, true, "Welcome back.", new Dictionary<string, object> { { "onlineId", onlineId }, { "password", known } });
                }
                // The host's launcher passes the password it already has, so a fresh server (new
                // shadNet database) makes the account with it instead of a new one.
                string offered = Query(request, "password") ?? "";
                bool reuse = offered.Length > 0 && IPAddress.IsLoopback(request.Remote);
                string password = reuse ? offered : Convert.ToBase64String(Guid.NewGuid().ToByteArray()).TrimEnd('=').Replace('+', 'x').Replace('/', 'y').Substring(0, 16);
                string problem = Register(onlineId, password);
                if (problem != null && reuse && (problem.IndexOf("exist", StringComparison.OrdinalIgnoreCase) >= 0 || problem.IndexOf("taken", StringComparison.OrdinalIgnoreCase) >= 0))
                    problem = null;
                if (problem != null)
                    return Reply(409, false, "Could not make the account '" + onlineId + "': " + problem +
                                             (problem.IndexOf("exist", StringComparison.OrdinalIgnoreCase) >= 0 || problem.IndexOf("taken", StringComparison.OrdinalIgnoreCase) >= 0
                                                 ? " Pick another online ID." : ""));
                accounts[onlineId] = password;
                Directory.CreateDirectory(options.DataDirectory);
                OnlineSave.WriteAtomically(accountsPath, Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(accounts)));
                log("invite: account " + onlineId + " made for " + request.Remote);
                return Reply(200, true, "Account made.", new Dictionary<string, object> { { "onlineId", onlineId }, { "password", password } });
            }
        }
    }

    /// The time in microseconds since 1970 (UTC) to the microsecond. DateTime.UtcNow on .NET
    /// Framework moves in steps of the system timer (about 15.6 ms), which would put every
    /// player's race clock up to that far off the host's.
    static class PreciseClock
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern void GetSystemTimePreciseAsFileTime(out long fileTime);

        public static long UnixMicroseconds()
        {
            long fileTime;
            GetSystemTimePreciseAsFileTime(out fileTime);  // 100 ns units since 1601
            return (fileTime - 116444736000000000L) / 10;
        }
    }

    /// Where each player's game can be reached for peer-to-peer play. RyGT's PSN signaling registers
    /// its player (online ID, PSN account ID, P2P port) when it starts, and looks other players up
    /// before connecting to them. The address handed out is the one the player was seen at, except
    /// that a player on this server's own PC (seen as 127.0.0.1) is given to remote players as the
    /// address they themselves reached this server at.
    /// A UDP relay on the server's port for P2P traffic between players who cannot reach each other
    /// directly (a router that does not forward the P2P port, or a carrier-grade NAT). Each game
    /// sends from its P2P port:
    ///   "RYK1" + its P2P port (2 bytes)          every 2 s, so the relay knows where it is;
    ///   "RYR1" + flags + address (4) + port (2) + datagram, for the player at that address:port.
    /// The relay passes the datagram on as "RYR1" + flags + the sender's address and P2P port +
    /// datagram. A player is known by the address the others have for it: the address it calls
    /// from, or for a player on this PC, this PC's public address.
    class Relay
    {
        class Entry { public IPEndPoint EndPoint; public DateTime Seen; }

        readonly int port;
        readonly Func<IPAddress> publicAddress;
        readonly Action<string> log;
        readonly Dictionary<string, Entry> players = new Dictionary<string, Entry>();
        readonly Dictionary<IPEndPoint, string> names = new Dictionary<IPEndPoint, string>();
        long forwarded, dropped;

        public Relay(int port, Func<IPAddress> publicAddress, Action<string> log)
        {
            this.port = port;
            this.publicAddress = publicAddress;
            this.log = log;
        }

        static string Key(IPAddress address, int port) { return address + ":" + port.ToString(CultureInfo.InvariantCulture); }

        IPAddress Known(IPAddress address)
        {
            if (!IPAddress.IsLoopback(address)) return address;
            return publicAddress() ?? address;
        }

        public void Run()
        {
            UdpClient udp;
            try { udp = new UdpClient(new IPEndPoint(IPAddress.Any, port)); }
            catch (SocketException e) { log("relay: cannot open UDP port " + port + ": " + e.Message); return; }
            // SIO_UDP_CONNRESET off: an ICMP "port unreachable" from a player who left would
            // otherwise fail the next receive.
            try { udp.Client.IOControl(-1744830452, new byte[] { 0, 0, 0, 0 }, null); } catch (Exception) { }
            log("relay: listening on UDP port " + port);
            DateTime report = DateTime.UtcNow;
            while (true)
            {
                IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                byte[] data;
                try { data = udp.Receive(ref from); }
                catch (SocketException) { continue; }
                if (data.Length < 6 || data[0] != 'R' || data[1] != 'Y' || data[3] != '1') continue;
                DateTime now = DateTime.UtcNow;
                if (data[2] == 'K')
                {
                    int p2pPort = (data[4] << 8) | data[5];
                    string key = Key(Known(from.Address), p2pPort);
                    Entry entry;
                    if (!players.TryGetValue(key, out entry) || !entry.EndPoint.Equals(from))
                        log("relay: " + key + " is at " + from);
                    players[key] = new Entry { EndPoint = from, Seen = now };
                    names[from] = key;
                }
                else if (data[2] == 'R' && data.Length >= 11)
                {
                    string source;
                    Entry target;
                    string destination = Key(new IPAddress(new[] { data[5], data[6], data[7], data[8] }), (data[9] << 8) | data[10]);
                    if (!names.TryGetValue(from, out source) || !players.TryGetValue(destination, out target) ||
                        now - target.Seen > TimeSpan.FromSeconds(30))
                    {
                        dropped++;
                        continue;
                    }
                    // The sender's address and P2P port in place of the destination's.
                    int colon = source.LastIndexOf(':');
                    byte[] address = IPAddress.Parse(source.Substring(0, colon)).GetAddressBytes();
                    int sourcePort = int.Parse(source.Substring(colon + 1), CultureInfo.InvariantCulture);
                    Array.Copy(address, 0, data, 5, 4);
                    data[9] = (byte)(sourcePort >> 8);
                    data[10] = (byte)sourcePort;
                    try { udp.Send(data, data.Length, target.EndPoint); forwarded++; } catch (SocketException) { dropped++; }
                }
                if (now - report > TimeSpan.FromSeconds(30) && forwarded + dropped > 0)
                {
                    log(string.Format(CultureInfo.InvariantCulture, "relay: last 30 s: {0} datagrams passed on, {1} dropped", forwarded, dropped));
                    forwarded = dropped = 0;
                    report = now;
                }
            }
        }
    }

    class Peers
    {
        class Peer
        {
            public string OnlineId = "";
            public long AccountId;
            public int Port;
            public IPAddress Address;
        }

        readonly object gate = new object();
        readonly Dictionary<string, Peer> byOnlineId = new Dictionary<string, Peer>(StringComparer.OrdinalIgnoreCase);
        // PSN account IDs, learnt from the players' PSN session calls (GT7's NpSessionSignaling
        // names peers by account ID).
        readonly Dictionary<string, long> accountOf = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        // PSN session ID -> its members (online ID -> when they last asked), for NpSessionSignaling.
        readonly Dictionary<string, Dictionary<string, DateTime>> sessions = new Dictionary<string, Dictionary<string, DateTime>>();
        // When each member joined each session (session -> online ID -> time), for OthersInSession.
        readonly Dictionary<string, Dictionary<string, DateTime>> joined = new Dictionary<string, Dictionary<string, DateTime>>();
        readonly Action<string> log;

        public Peers(Action<string> log)
        {
            this.log = log;
        }

        public void NoteAccount(string onlineId, long accountId)
        {
            if (string.IsNullOrEmpty(onlineId) || accountId <= 0) return;
            lock (gate)
            {
                long known;
                if (accountOf.TryGetValue(onlineId, out known) && known == accountId) return;
                accountOf[onlineId] = accountId;
                log(string.Format(CultureInfo.InvariantCulture, "p2p: {0} is PSN account {1}", onlineId, accountId));
            }
        }

        /// This server's address as the caller reached it, for handing out a player on this PC: the
        /// address in the Host header the caller used (with port forwarding, the public address;
        /// the PC itself only sees its LAN address), else the local end of the connection.
        static IPAddress AddressAsReached(HttpRequest request)
        {
            string host = request.Header("Host") ?? "";
            int colon = host.LastIndexOf(':');
            if (colon > 0 && host.IndexOf(':') == colon) host = host.Substring(0, colon);
            IPAddress named;
            if (IPAddress.TryParse(host.Trim('[', ']'), out named) && !IPAddress.IsLoopback(named)) return named;
            return request.Local;
        }

        /// This PC's address as the other players reach it (from the Host header of their requests):
        /// players on this PC are known to the others by it.
        public volatile IPAddress PublicAddress;

        /// Called (under the registry's lock) when a player joins a session, again after leaving too.
        public Action<string> Joined;

        /// The other players in the session a player is in now (the one it asked about last), in the
        /// order they joined.
        public List<string> OthersInSession(string onlineId)
        {
            lock (gate)
            {
                string current = null;
                DateTime latest = DateTime.MinValue;
                foreach (var session in sessions)
                {
                    DateTime seen;
                    if (session.Value.TryGetValue(onlineId, out seen) && seen > latest && DateTime.UtcNow - seen < TimeSpan.FromSeconds(15))
                    {
                        current = session.Key;
                        latest = seen;
                    }
                }
                if (current == null) return new List<string>();
                var times = joined.ContainsKey(current) ? joined[current] : new Dictionary<string, DateTime>();
                return sessions[current]
                    .Where(m => !m.Key.Equals(onlineId, StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow - m.Value < TimeSpan.FromSeconds(15))
                    .OrderBy(m => times.ContainsKey(m.Key) ? times[m.Key] : DateTime.MaxValue)
                    .Select(m => m.Key).ToList();
            }
        }

        /// /rygt/p2p/session?id=<PSN session>&online=<id>&port=<P2P port>: makes the caller a member
        /// of the session (seen at the address it called from) and answers the other members still
        /// asking (in the last 15 s), one per line: online ID, account ID, address, port.
        HttpResponse Session(HttpRequest request)
        {
            string session = Query(request, "id") ?? "", onlineId = Query(request, "online") ?? "";
            int port;
            if (session.Length == 0 || onlineId.Length == 0 || !int.TryParse(Query(request, "port"), out port))
                return HttpResponse.Text(400, "text/plain", "id, online and port are required");
            long account;
            accountOf.TryGetValue(onlineId, out account);
            if (!IPAddress.IsLoopback(request.Remote))
            {
                IPAddress reached = AddressAsReached(request);
                if (reached != null && !IPAddress.IsLoopback(reached)) PublicAddress = reached;
            }
            Peer existing;
            if (!byOnlineId.TryGetValue(onlineId, out existing) || existing.Port != port || !existing.Address.Equals(request.Remote))
                log(string.Format(CultureInfo.InvariantCulture, "p2p: {0} (account {1}) at {2}, port {3}", onlineId, account, request.Remote, port));
            byOnlineId[onlineId] = new Peer { OnlineId = onlineId, AccountId = account, Port = port, Address = request.Remote };

            Dictionary<string, DateTime> members;
            if (!sessions.TryGetValue(session, out members)) sessions[session] = members = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            if (!members.ContainsKey(onlineId))
            {
                log("p2p: " + onlineId + " is in session " + session);
                if (Joined != null) Joined(onlineId);
                Dictionary<string, DateTime> times;
                if (!joined.TryGetValue(session, out times)) joined[session] = times = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
                times[onlineId] = DateTime.UtcNow;
            }
            members[onlineId] = DateTime.UtcNow;
            if (Query(request, "leave") == "1")
            {
                members.Remove(onlineId);
                log("p2p: " + onlineId + " left session " + session);
            }

            var text = new StringBuilder();
            foreach (var member in members.Where(m => !m.Key.Equals(onlineId, StringComparison.OrdinalIgnoreCase)
                                                      && DateTime.UtcNow - m.Value < TimeSpan.FromSeconds(15)))
            {
                Peer peer;
                if (!byOnlineId.TryGetValue(member.Key, out peer)) continue;
                IPAddress address = peer.Address;
                if (IPAddress.IsLoopback(address) && !IPAddress.IsLoopback(request.Remote))
                    address = AddressAsReached(request) ?? address;
                long peerAccount;
                accountOf.TryGetValue(peer.OnlineId, out peerAccount);
                text.Append(peer.OnlineId).Append(' ').Append(peerAccount.ToString(CultureInfo.InvariantCulture)).Append(' ')
                    .Append(address).Append(' ').Append(peer.Port.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
            return HttpResponse.Text(200, "text/plain", text.ToString());
        }

        static string Query(HttpRequest request, string name)
        {
            foreach (string part in request.Query.Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq > 0 && part.Substring(0, eq) == name) return Uri.UnescapeDataString(part.Substring(eq + 1));
            }
            return null;
        }

        public HttpResponse Handle(HttpRequest request)
        {
            string action = request.Path.Substring("/rygt/p2p/".Length);
            lock (gate)
            {
                if (action == "register")
                {
                    string onlineId = Query(request, "online") ?? "";
                    long account;
                    int port;
                    if (onlineId.Length == 0 || !long.TryParse(Query(request, "account"), out account) ||
                        !int.TryParse(Query(request, "port"), out port))
                        return HttpResponse.Text(400, "text/plain", "online, account and port are required");
                    Peer existing;
                    bool changed = !byOnlineId.TryGetValue(onlineId, out existing) || existing.Port != port ||
                                   !existing.Address.Equals(request.Remote) || existing.AccountId != account;
                    byOnlineId[onlineId] = new Peer { OnlineId = onlineId, AccountId = account, Port = port, Address = request.Remote };
                    if (changed)
                        log(string.Format(CultureInfo.InvariantCulture, "p2p: {0} (account {1}) at {2}, port {3}", onlineId, account, request.Remote, port));
                    return GrimReply.Json(new Dictionary<string, object> { { "result", true } });
                }
                if (action == "session")
                    return Session(request);
                // The caller's address as this server sees it (its public address over the internet).
                if (action == "whoami")
                    return HttpResponse.Text(200, "text/plain", (IPAddress.IsLoopback(request.Remote)
                        ? AddressAsReached(request) ?? request.Remote : request.Remote).ToString());
                if (action == "peer")
                {
                    Peer peer = null;
                    string onlineId = Query(request, "online");
                    long account;
                    if (onlineId != null) byOnlineId.TryGetValue(onlineId, out peer);
                    else if (long.TryParse(Query(request, "account"), out account))
                        peer = byOnlineId.Values.FirstOrDefault(p => p.AccountId == account);
                    if (peer == null) return HttpResponse.Text(404, "application/json", "{\"result\":false}");
                    IPAddress address = peer.Address;
                    if (IPAddress.IsLoopback(address) && !IPAddress.IsLoopback(request.Remote))
                        address = AddressAsReached(request) ?? address;
                    return GrimReply.Json(new Dictionary<string, object> {
                        { "result", true }, { "onlineId", peer.OnlineId }, { "accountId", peer.AccountId },
                        { "address", address.ToString() }, { "port", peer.Port } });
                }
            }
            return null;
        }
    }

    /// The Lobby's room directory (GT Sport's GSS, reached at /<region>/gss/v1/...). Lobby rooms are
    /// peer to peer: players race directly against each other, and this only keeps the list of rooms
    /// and who is in each, which is what the game needs to find a room and its members' addresses.
    /// Every reply carries "result": true, which the game's GSS client requires. Rooms whose members
    /// all stop sending heartbeats are dropped.
    class Lobby
    {
        const int RoomVersion = 12360;     // the version this build of the game asks for
        const int DefaultMemberMax = 16;
        static readonly TimeSpan MemberTimeout = TimeSpan.FromSeconds(90);

        class Room
        {
            public long Id;
            public Dictionary<string, object> Data = new Dictionary<string, object>();
            public Dictionary<string, object> Owner;
            public string OwnerId = "";
            public readonly Dictionary<string, Dictionary<string, object>> Members = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, DateTime> Seen = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            public readonly List<object> Chat = new List<object>();
            public DateTime Created = DateTime.UtcNow;
        }

        readonly object gate = new object();
        readonly Dictionary<long, Room> rooms = new Dictionary<long, Room>();
        readonly Action<string> log;
        long nextRoomId = 1001;

        public Lobby(Action<string> log)
        {
            this.log = log;
        }

        static Dictionary<string, object> Ok()
        {
            return new Dictionary<string, object> { { "result", true } };
        }

        static Dictionary<string, object> Body(HttpRequest request)
        {
            if (request.Body.Length == 0) return new Dictionary<string, object>();
            try
            {
                return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(Encoding.UTF8.GetString(request.Body))
                    as Dictionary<string, object> ?? new Dictionary<string, object>();
            }
            catch (ArgumentException) { return new Dictionary<string, object>(); }
            catch (InvalidOperationException) { return new Dictionary<string, object>(); }
        }

        static object Get(Dictionary<string, object> map, string key)
        {
            object value;
            return map != null && map.TryGetValue(key, out value) ? value : null;
        }

        /// A member's key: their online ID when the game sends it, else the player making the request.
        static string MemberKey(Dictionary<string, object> member, string onlineId)
        {
            string name = Get(member, "userName") as string;
            return string.IsNullOrEmpty(name) ? onlineId : name;
        }

        void Expire()
        {
            DateTime now = DateTime.UtcNow;
            foreach (var room in rooms.Values.ToList())
            {
                foreach (var key in room.Seen.Where(s => now - s.Value > MemberTimeout).Select(s => s.Key).ToList())
                {
                    room.Seen.Remove(key);
                    room.Members.Remove(key);
                }
                if (room.Members.Count == 0)
                {
                    rooms.Remove(room.Id);
                    log("lobby room " + room.Id + " closed (empty)");
                }
            }
        }

        int MemberMax(Room room)
        {
            object criteria = Get(room.Data, "searchCriteria");
            foreach (string key in new[] { "memberMax", "roomMax", "room_max" })
            {
                object value = Get(criteria as Dictionary<string, object>, key) ?? Get(room.Data, key);
                if (value != null) return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            return DefaultMemberMax;
        }

        Dictionary<string, object> Describe(Room room)
        {
            var result = new Dictionary<string, object>(room.Data);
            result["roomId"] = room.Id;
            result["owner"] = room.Owner;
            result["roomVersion"] = Get(room.Data, "roomVersion") ?? RoomVersion;
            result["memberCount"] = room.Members.Count;
            result["memberMax"] = MemberMax(room);
            // The host reports "full" itself (it knows the room's real capacity); fall back to a count.
            if (!result.ContainsKey("full")) result["full"] = room.Members.Count >= MemberMax(room);
            result["createdAt"] = GrimReply.Time(room.Created);
            return result;
        }

        Dictionary<string, object> MemberList(Room room)
        {
            var result = Ok();
            result["roomId"] = room.Id;
            result["owner"] = room.Owner;
            result["members"] = room.Members.Values.ToList();
            result["memberMax"] = MemberMax(room);
            return result;
        }

        /// The simple parts of the game's room query ({"query": {"roomId": 5}, "limit": 20, ...}).
        static bool Matches(Room room, Dictionary<string, object> query)
        {
            if (query == null) return true;
            object roomId = Get(query, "roomId");
            if (roomId is Dictionary<string, object>)
            {
                object list = Get((Dictionary<string, object>)roomId, "$in");
                if (list is object[] && !((object[])list).Any(id => Convert.ToInt64(id, CultureInfo.InvariantCulture) == room.Id)) return false;
            }
            else if (roomId != null && Convert.ToInt64(roomId, CultureInfo.InvariantCulture) != room.Id) return false;
            return true;
        }

        public HttpResponse Handle(HttpRequest request, string onlineId)
        {
            string path = request.Path;
            int gss = path.IndexOf("/gss", StringComparison.Ordinal);
            if (gss < 0) return null;
            int version = path.IndexOf("/v1/", gss, StringComparison.Ordinal);
            if (version < 0) return null;
            string[] parts = path.Substring(version + 4).Trim('/').Split('/');
            string method = request.Method;
            var body = Body(request);

            lock (gate)
            {
                Expire();
                switch (parts[0])
                {
                    case "service":
                    {
                        var result = Ok();
                        result["serviceStatus"] = "Ready";
                        result["roomVersion"] = RoomVersion;
                        return GrimReply.Json(result);
                    }
                    case "mps":
                    {
                        var result = Ok();
                        result["mrttsList"] = new object[0];
                        result["mpsList"] = new object[0];
                        return GrimReply.Json(result);
                    }
                    case "room":
                        return HandleRoom(parts, method, body, request, onlineId);
                }
            }
            return null;
        }

        HttpResponse HandleRoom(string[] parts, string method, Dictionary<string, object> body, HttpRequest request, string onlineId)
        {
            if (parts.Length == 1 && method == "POST")
            {
                var room = new Room { Id = nextRoomId++, Data = body, OwnerId = onlineId };
                room.Owner = Get(body, "owner") as Dictionary<string, object> ?? new Dictionary<string, object>();
                room.Data.Remove("owner");
                string key = MemberKey(room.Owner, onlineId);
                room.Members[key] = room.Owner;
                room.Seen[key] = DateTime.UtcNow;
                rooms[room.Id] = room;
                log(string.Format(CultureInfo.InvariantCulture, "lobby room {0} created by {1}", room.Id, onlineId));
                var result = Ok();
                result["roomId"] = room.Id;
                return GrimReply.Json(result);
            }
            if (parts.Length >= 2 && parts[1] == "status")
            {
                var result = Ok();
                result["roomCount"] = rooms.Count;
                result["memberCount"] = rooms.Values.Sum(r => r.Members.Count);
                return GrimReply.Json(result);
            }
            if (parts.Length >= 2 && parts[1] == "list")
            {
                var query = Get(body, "query") as Dictionary<string, object>;
                var found = rooms.Values.Where(r => Matches(r, query)).OrderBy(r => r.Id).ToList();
                long offset = Convert.ToInt64(Get(body, "offset") ?? 0, CultureInfo.InvariantCulture);
                long limit = Convert.ToInt64(Get(body, "limit") ?? 50, CultureInfo.InvariantCulture);
                var result = Ok();
                result["rooms"] = found.Skip((int)offset).Take((int)Math.Max(0, limit)).Select(Describe).ToList();
                result["roomCount"] = found.Count;
                return GrimReply.Json(result);
            }
            if (parts.Length >= 3 && parts[1] == "owner")
            {
                var owned = rooms.Values.Where(r => string.Equals(r.OwnerId, parts[2], StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Convert.ToString(Get(r.Owner, "npAccountId"), CultureInfo.InvariantCulture), parts[2], StringComparison.OrdinalIgnoreCase)).ToList();
                if (method == "DELETE")
                {
                    foreach (var r in owned) rooms.Remove(r.Id);
                    return GrimReply.Json(Ok());
                }
                var result = owned.Count > 0 ? Describe(owned[0]) : new Dictionary<string, object>();
                result["result"] = true;
                result["rooms"] = owned.Select(Describe).ToList();
                return GrimReply.Json(result);
            }

            long id;
            if (parts.Length < 2 || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) return null;
            Room found_;
            if (!rooms.TryGetValue(id, out found_))
            {
                var missing = new Dictionary<string, object> { { "result", false }, { "errorCode", 404 } };
                var response = GrimReply.Json(missing);
                return response;
            }
            Room room_ = found_;
            string sub = parts.Length > 2 ? parts[2] : "";
            string me = onlineId;
            switch (sub)
            {
                case "":
                    if (method == "DELETE")
                    {
                        rooms.Remove(room_.Id);
                        log("lobby room " + room_.Id + " deleted by " + onlineId);
                        return GrimReply.Json(Ok());
                    }
                    if (method == "PUT" || method == "POST")
                        foreach (var pair in body) if (pair.Key != "owner") room_.Data[pair.Key] = pair.Value;
                    var described = Describe(room_);
                    described["result"] = true;
                    return GrimReply.Json(described);
                case "ticket":
                    if (method != "GET")
                    {
                        object ticket = Get(body, "ticketData");
                        if (ticket != null) room_.Data["ticketData"] = ticket;
                    }
                    var ticketReply = Ok();
                    ticketReply["ticketData"] = Get(room_.Data, "ticketData");
                    return GrimReply.Json(ticketReply);
                case "heartbeat":
                    room_.Seen[me] = DateTime.UtcNow;
                    foreach (var key in room_.Members.Keys.Where(k => string.Equals(k, me, StringComparison.OrdinalIgnoreCase)).ToList())
                        room_.Seen[key] = DateTime.UtcNow;
                    return GrimReply.Json(Ok());
                case "member":
                    if (method == "DELETE")
                    {
                        string who = parts.Length > 3 ? parts[3] : me;
                        room_.Members.Remove(who);
                        room_.Seen.Remove(who);
                        log("lobby room " + room_.Id + ": " + who + " left");
                        if (room_.Members.Count == 0) rooms.Remove(room_.Id);
                        return GrimReply.Json(Ok());
                    }
                    if (method == "POST" || method == "PUT")
                    {
                        var member = Get(body, "member") as Dictionary<string, object> ?? body;
                        string key = MemberKey(member, me);
                        if (!room_.Members.ContainsKey(key) && room_.Members.Count >= MemberMax(room_))
                            return GrimReply.Json(new Dictionary<string, object> { { "result", false }, { "errorCode", 409 } });
                        bool joined = !room_.Members.ContainsKey(key);
                        room_.Members[key] = member;
                        room_.Seen[key] = DateTime.UtcNow;
                        if (joined) log("lobby room " + room_.Id + ": " + key + " joined");
                    }
                    return GrimReply.Json(MemberList(room_));
                case "info":
                    // A partial update of the room: {"attributes": {...}} when the host opens it once
                    // its race scene has loaded, {"full": ..., "sessionStats": ...} as players come and go.
                    if (method != "GET")
                        foreach (var pair in body)
                        {
                            var update = pair.Value as Dictionary<string, object>;
                            var current = Get(room_.Data, pair.Key) as Dictionary<string, object>;
                            if (update != null && current != null)
                                foreach (var field in update) current[field.Key] = field.Value;
                            else if (pair.Key != "owner")
                                room_.Data[pair.Key] = pair.Value;
                        }
                    var info = Describe(room_);
                    info["result"] = true;
                    return GrimReply.Json(info);
                case "chat":
                    if (method != "GET")
                    {
                        body["from"] = me;
                        body["time"] = GrimReply.Time(DateTime.UtcNow);
                        room_.Chat.Add(body);
                        while (room_.Chat.Count > 100) room_.Chat.RemoveAt(0);
                    }
                    var chat = Ok();
                    chat["chat"] = room_.Chat;
                    return GrimReply.Json(chat);
                default:
                    return GrimReply.Json(Ok());
            }
        }

        public int RoomCount { get { lock (gate) return rooms.Count; } }
    }

    class Server
    {
        readonly Options options;
        readonly Players players = new Players();
        readonly ResponseFiles responses;
        readonly OnlineSave onlineSave;
        readonly OnlineEvents events;
        readonly Rankings rankings;
        readonly Lobby lobby;
        readonly Peers peers;
        readonly Relay relay;
        readonly Invites invites;
        readonly Gt7 gt7;
        readonly Captures captures;
        readonly object consoleLock = new object();
        readonly LinkedList<string> recent = new LinkedList<string>();
        readonly DateTime started = DateTime.UtcNow;
        long requestCount;

        public Server(Options options)
        {
            this.options = options;
            string responseRoot = Path.Combine(options.DataDirectory, "responses");
            Directory.CreateDirectory(responseRoot);
            responses = new ResponseFiles(responseRoot);
            onlineSave = new OnlineSave(options.DataDirectory, text => Log(ConsoleColor.Cyan, text));
            events = new OnlineEvents(options.DataDirectory);
            rankings = new Rankings(options.DataDirectory, text => Log(ConsoleColor.Cyan, text));
            lobby = new Lobby(text => Log(ConsoleColor.Magenta, text));
            peers = new Peers(text => Log(ConsoleColor.Magenta, text));
            invites = new Invites(options, text => Log(ConsoleColor.Green, text));
            gt7 = new Gt7(options.DataDirectory, text => Log(ConsoleColor.Cyan, text), peers.OthersInSession);
            peers.Joined = gt7.PlayerJoined;
            relay = new Relay(options.Port, () => peers.PublicAddress, text => Log(ConsoleColor.Magenta, text));
            if (options.Capture) captures = new Captures(options.DataDirectory);
        }

        public void Run()
        {
            var listener = new TcpListener(IPAddress.IPv6Any, options.Port);
            listener.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
            listener.Start();
            PrintBanner();
            new Thread(relay.Run) { IsBackground = true, Name = "relay" }.Start();
            while (true)
            {
                TcpClient client = listener.AcceptTcpClient();
                ThreadPool.QueueUserWorkItem(_ => Handle(client));
            }
        }

        void PrintBanner()
        {
            Console.WriteLine("RyGTServer listening on port " + options.Port);
            Console.WriteLine("  Data:      " + options.DataDirectory);
            if (captures != null) Console.WriteLine("  Captures:  " + captures.Directory_);
            Console.WriteLine();
            Console.WriteLine("Players on this PC use online_server=127.0.0.1:" + options.Port);
            var addresses = LocalAddresses();
            if (addresses.Count > 0)
            {
                Console.WriteLine("Friends use one of these, whichever their PC can reach:");
                foreach (string address in addresses) Console.WriteLine("  online_server=" + address + ":" + options.Port);
                Console.WriteLine("(Over the internet: forward TCP and UDP port " + options.Port + " to this PC, or share a VPN such as Tailscale.)");
            }
            Console.WriteLine("Status page: http://127.0.0.1:" + options.Port + "/");
            Console.WriteLine();
        }

        static List<string> LocalAddresses()
        {
            try
            {
                return Dns.GetHostAddresses(Dns.GetHostName())
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                    .Select(a => a.ToString()).Distinct().ToList();
            }
            catch (SocketException)
            {
                return new List<string>();
            }
        }

        void Handle(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 30000;
                    client.SendTimeout = 30000;
                    NetworkStream stream = client.GetStream();
                    HttpRequest request = HttpRequest.Read(stream);
                    if (request == null) return;
                    var remote = (IPEndPoint)client.Client.RemoteEndPoint;
                    request.Remote = remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address;
                    var local = (IPEndPoint)client.Client.LocalEndPoint;
                    request.Local = local.Address.IsIPv4MappedToIPv6 ? local.Address.MapToIPv4() : local.Address;
                    HttpResponse response = request.FromGame ? HandleGame(request) : HandleAdmin(request);
                    response.Write(stream);
                }
                catch (Exception e)
                {
                    if (e is IOException || e is SocketException) return;
                    Log(ConsoleColor.Red, "error: " + e.Message);
                }
            }
        }

        HttpResponse HandleGame(HttpRequest request)
        {
            Interlocked.Increment(ref requestCount);
            string onlineId = request.Header("X-RyGT-Online-Id") ?? "";
            if (players.Touch(onlineId, request.Remote))
                Log(ConsoleColor.Green, "+ " + onlineId + " connected from " + request.Remote);

            HttpResponse response = (request.Path.StartsWith("/rygt/psn/", StringComparison.Ordinal) ? HandlePsn(request) : null)
                ?? (Gt7.IsGt7Host(request.Host) ? gt7.Handle(request) : null)
                ?? lobby.Handle(request, onlineId) ?? onlineSave.Handle(request, onlineId)
                ?? events.Handle(request) ?? rankings.Handle(request, onlineId) ?? responses.Find(request);
            if (response == null)
            {
                response = new HttpResponse { Status = options.DefaultStatus };
            }
            if (captures != null)
            {
                try { captures.Save(request, response); }
                catch (IOException e) { Log(ConsoleColor.Red, "capture failed: " + e.Message); }
            }

            string line = string.Format(CultureInfo.InvariantCulture, "{0,-12} {1,-6} {2}://{3}{4} -> {5}{6}",
                onlineId, request.Method, request.Header("X-RyGT-Scheme") ?? "https", request.Header("Host"), request.Target,
                response.Status, request.Command != null ? "  [" + request.Command + "]" :
                request.Body.Length > 0 ? " (sent " + request.Body.Length + " bytes)" : "");
            Log(response.Source.Length > 0 ? ConsoleColor.Gray : ConsoleColor.Yellow, line);
            lock (recent)
            {
                recent.AddFirst(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line);
                while (recent.Count > 100) recent.RemoveLast();
            }
            return response;
        }

        /// The first value named `key` anywhere in a parsed JSON document.
        static object FindValue(object node, string key)
        {
            var map = node as Dictionary<string, object>;
            if (map != null)
            {
                if (map.ContainsKey(key)) return map[key];
                foreach (object child in map.Values)
                {
                    object found = FindValue(child, key);
                    if (found != null) return found;
                }
            }
            var list = node as object[];
            if (list != null)
                foreach (object child in list)
                {
                    object found = FindValue(child, key);
                    if (found != null) return found;
                }
            return null;
        }

        // PSN web API calls that shadNet does not answer; the NP library sends them here as
        // /rygt/psn/<PSN path>.
        HttpResponse HandlePsn(HttpRequest request)
        {
            string path = request.Path.Substring("/rygt/psn".Length);
            var json = new JavaScriptSerializer();
            HttpResponse response;
            // Creating or joining a session names the player's PSN account ("players": [{"accountId"}]).
            if (request.Method == "POST" && (path.StartsWith("/v1/gameSessions", StringComparison.Ordinal) || path.StartsWith("/v1/playerSessions", StringComparison.Ordinal)))
            {
                try
                {
                    string accountId = FindValue(json.DeserializeObject(Encoding.UTF8.GetString(request.Body)), "accountId") as string;
                    long account;
                    if (accountId != null && long.TryParse(accountId, NumberStyles.None, CultureInfo.InvariantCulture, out account))
                        peers.NoteAccount(request.Header("X-RyGT-Online-Id"), account);
                }
                catch (ArgumentException) { }
            }
            if (path == "/v3/users/me/communication/restriction/status")
                response = HttpResponse.Text(200, "application/json", "{\"restricted\":false}");
            else if (path.StartsWith("/v2/profanity/filter/", StringComparison.Ordinal))
            {
                // Nothing is filtered: the text comes back as it was sent.
                string text = "";
                try
                {
                    var body = json.DeserializeObject(Encoding.UTF8.GetString(request.Body)) as Dictionary<string, object>;
                    object value;
                    if (body != null && body.TryGetValue("text", out value) && value is string) text = (string)value;
                }
                catch (ArgumentException) { }
                response = HttpResponse.Text(200, "application/json", json.Serialize(new Dictionary<string, object> { { "message", text } }));
            }
            // Session and match calls check PSN's exact status: 201 for creating, 204 for changing
            // or leaving, 200 for reading.
            else if (request.Method == "POST" && (path == "/v1/gameSessions" || path == "/v1/playerSessions"))
            {
                // A new PSN session: GT7 needs only its ID ({"gameSessions": [{"sessionId"}]}).
                string list = path.Substring("/v1/".Length);
                response = HttpResponse.Text(201, "application/json", json.Serialize(new Dictionary<string, object> {
                    { list, new object[] { new Dictionary<string, object> { { "sessionId", Guid.NewGuid().ToString() } } } } }));
            }
            else if (request.Method == "POST" && path == "/v1/matches")
                response = HttpResponse.Text(201, "application/json", json.Serialize(new Dictionary<string, object> {
                    { "matchId", Guid.NewGuid().ToString() } }));
            else if (path.StartsWith("/v1/gameSessions/", StringComparison.Ordinal) || path.StartsWith("/v1/playerSessions/", StringComparison.Ordinal)
                || path.StartsWith("/v1/matches/", StringComparison.Ordinal))
                // Joining, leaving and updating sessions and matches: accepted, nothing kept.
                response = request.Method == "POST" ? HttpResponse.Text(201, "application/json", "{}")
                    : request.Method == "GET" ? HttpResponse.Text(200, "application/json", "{}")
                    : HttpResponse.Text(204, "application/json", "");
            else
                response = HttpResponse.Text(404, "application/json", "{}");
            Log(response.Status < 300 ? ConsoleColor.Gray : ConsoleColor.Yellow,
                "PSN " + request.Method + " " + path + " -> " + response.Status);
            return response;
        }

        HttpResponse HandleAdmin(HttpRequest request)
        {
            // The host's clock in microseconds since 1970 (UTC). Games set the PS4 network clock from
            // it, so every player's race timeline matches the host's whatever their PC's clock says.
            if (request.Path == "/rygt/time")
                return HttpResponse.Text(200, "text/plain", PreciseClock.UnixMicroseconds().ToString(CultureInfo.InvariantCulture));

            var players = this.players.Online().Select(p => new Dictionary<string, object> {
                { "online_id", p.OnlineId }, { "address", p.Address },
                { "connected", p.FirstSeen.ToString("o", CultureInfo.InvariantCulture) },
                { "last_seen", p.LastSeen.ToString("o", CultureInfo.InvariantCulture) }, { "requests", p.Requests } }).ToList();
            List<string> lines;
            lock (recent) lines = recent.ToList();

            if (request.Path == "/rygt/invite/join")
                return invites.Join(request);
            if (request.Path.StartsWith("/rygt/p2p/", StringComparison.Ordinal))
                return peers.Handle(request) ?? HttpResponse.Text(404, "text/plain", "Not found");
            if (request.Path.StartsWith("/rygt/psn/", StringComparison.Ordinal))
                return HandlePsn(request);
            if (request.Path == "/rygt/status")
            {
                var status = new Dictionary<string, object> {
                    { "server", "RyGTServer" }, { "port", options.Port },
                    { "uptime_seconds", (long)(DateTime.UtcNow - started).TotalSeconds },
                    { "requests", Interlocked.Read(ref requestCount) }, { "players", players } };
                return HttpResponse.Text(200, "application/json", new JavaScriptSerializer().Serialize(status));
            }
            if (request.Path != "/") return HttpResponse.Text(404, "text/plain", "Not found");

            var html = new StringBuilder();
            html.Append("<!doctype html><html><head><meta charset=\"utf-8\"><meta http-equiv=\"refresh\" content=\"5\">");
            html.Append("<title>RyGTServer</title><style>body{font:14px Segoe UI,sans-serif;background:#111;color:#ddd;margin:24px}");
            html.Append("h1{font-size:20px}table{border-collapse:collapse}td,th{padding:4px 12px;text-align:left;border-bottom:1px solid #333}");
            html.Append("pre{background:#1b1b1b;padding:12px;overflow:auto}</style></head><body>");
            html.Append("<h1>RyGTServer</h1><p>Port ").Append(options.Port).Append(" &middot; ")
                .Append(Interlocked.Read(ref requestCount)).Append(" game requests since start</p>");
            html.Append("<h2>Players online (").Append(players.Count).Append(")</h2><table><tr><th>Online ID</th><th>Address</th><th>Requests</th></tr>");
            foreach (var p in players)
                html.Append("<tr><td>").Append(WebUtility.HtmlEncode((string)p["online_id"])).Append("</td><td>")
                    .Append(WebUtility.HtmlEncode((string)p["address"])).Append("</td><td>").Append(p["requests"]).Append("</td></tr>");
            html.Append("</table><h2>Recent requests</h2><pre>");
            foreach (string line in lines) html.Append(WebUtility.HtmlEncode(line)).Append('\n');
            html.Append("</pre></body></html>");
            return HttpResponse.Text(200, "text/html; charset=utf-8", html.ToString());
        }

        void Log(ConsoleColor color, string text)
        {
            lock (consoleLock)
            {
                Console.ForegroundColor = color;
                Console.WriteLine(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + text);
                Console.ResetColor();
            }
        }
    }

    static class Program
    {
        static int Main(string[] args)
        {
            Console.Title = "RyGTServer";
            Options options;
            try
            {
                options = Options.Parse(args);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e.Message);
                Console.Error.WriteLine(Options.Usage);
                return 2;
            }
            if (options == null)
            {
                Console.WriteLine(Options.Usage);
                return 0;
            }
            try
            {
                new Server(options).Run();
                return 0;
            }
            catch (SocketException e)
            {
                Console.Error.WriteLine("Could not listen on port " + options.Port + ": " + e.Message);
                Console.Error.WriteLine("Another RyGTServer may already be running, or pick another port with --port.");
                return 1;
            }
        }
    }
}
