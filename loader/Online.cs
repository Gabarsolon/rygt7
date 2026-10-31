// ryGT7 launcher: online play. GT7's own servers are replaced by ones a player hosts on their PC
// (RyGTServer for GT7's online service, "Vegas", and its rooms; shadNet for PSN sign-in, both in
// server\ next to RyGT7.exe), and friends join with an invite code. Room races are peer to peer:
// every player's game talks straight to the others on port 3658.
// SPDX-License-Identifier: GPL-2.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace RyGT
{
    /// Online play's files and settings: RyGT.ini (online, online_id, online_server, online_address,
    /// game_log, read by ryGT7's network libraries), user\users.json (the shadNet account player 1
    /// signs in with) and the shadNet server address in GT7's config.
    static class OnlinePlay
    {
        public const int ServerPort = 7890;
        public const int ShadNetPort = 31313, ShadNetWebApiPort = 31315, P2PPort = 3658;

        static string IniPath(string root) { return Path.Combine(root, "RyGT.ini"); }

        public static Dictionary<string, string> ReadIni(string root)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(IniPath(root))) return values;
            foreach (string line in File.ReadAllLines(IniPath(root)))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return values;
        }

        /// Sets keys in RyGT.ini (a null value removes the key), keeping every other line.
        public static void WriteIni(string root, IDictionary<string, string> changes)
        {
            var lines = File.Exists(IniPath(root)) ? File.ReadAllLines(IniPath(root)).ToList() : new List<string>();
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = lines.Count - 1; i >= 0; --i)
            {
                int eq = lines[i].IndexOf('=');
                if (eq <= 0) continue;
                string key = lines[i].Substring(0, eq).Trim();
                if (!changes.ContainsKey(key)) continue;
                if (changes[key] == null || done.Contains(key)) lines.RemoveAt(i);
                else { lines[i] = key + "=" + changes[key]; done.Add(key); }
            }
            foreach (var change in changes)
                if (change.Value != null && !done.Contains(change.Key)) lines.Add(change.Key + "=" + change.Value);
            File.WriteAllLines(IniPath(root), lines.ToArray());
        }

        public static string Get(Dictionary<string, string> ini, string key)
        {
            string value;
            return ini.TryGetValue(key, out value) ? value : "";
        }

        public static bool Enabled(string root)
        {
            string value = Get(ReadIni(root), "online");
            return value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        // Invite codes: the host's IPv4 address, RyGTServer's port and the invite token (6 bytes),
        // in Crockford base 32: RYGT-XXXXX-XXXXX-XXXXX-XXXXX (the same codes as ryGT's).
        const string Base32 = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        /// RyGTServers on this PC's networks (or at `address` when one is given), as invite code
        /// and PC name. Each network is asked separately: a broadcast from one socket leaves
        /// through a VPN adapter as easily as the home network.
        public static List<KeyValuePair<string, string>> FindServers(string address)
        {
            var found = new Dictionary<string, KeyValuePair<string, string>>();
            var sockets = new List<UdpClient>();
            byte[] ask = Encoding.ASCII.GetBytes("RYD1");
            var sources = LocalAddresses().Select(a => a.Key).Concat(new[] { "127.0.0.1" });
            foreach (string source in sources)
            {
                try
                {
                    var udp = new UdpClient(new IPEndPoint(IPAddress.Parse(source), 0)) { EnableBroadcast = true };
                    sockets.Add(udp);
                    udp.Send(ask, ask.Length, new IPEndPoint(address != null ? IPAddress.Parse(address) : source == "127.0.0.1" ? IPAddress.Loopback : IPAddress.Broadcast, ServerPort));
                }
                catch (Exception) { }
            }
            DateTime end = DateTime.UtcNow.AddMilliseconds(1500);
            while (DateTime.UtcNow < end)
            {
                foreach (var udp in sockets.Where(u => u.Available > 0))
                {
                    try
                    {
                        var from = new IPEndPoint(IPAddress.Any, 0);
                        string reply = Encoding.UTF8.GetString(udp.Receive(ref from));
                        if (!reply.StartsWith("RYS1") || reply.Length < 16) continue;
                        string where = IPAddress.IsLoopback(from.Address) ? ((IPEndPoint)udp.Client.LocalEndPoint).Address.ToString() : from.Address.ToString();
                        if (where == "127.0.0.1" && sockets.Count > 1) continue;  // this PC answers on its network address too
                        found[where] = new KeyValuePair<string, string>(MakeCode(IPAddress.Parse(where), ServerPort, reply.Substring(4, 12)),
                                                                        reply.Substring(16) + " (" + where + ")");
                    }
                    catch (Exception) { }
                }
                Thread.Sleep(50);
            }
            foreach (var udp in sockets) udp.Close();
            return found.Values.ToList();
        }

        /// The round trip to a RyGTServer in milliseconds, the best of three; -1 without an answer.
        public static int Ping(string host, int port)
        {
            int best = -1;
            try
            {
                using (var udp = new UdpClient())
                {
                    udp.Client.ReceiveTimeout = 800;
                    udp.Connect(host, port);
                    byte[] ask = Encoding.ASCII.GetBytes("RYP1");
                    for (int i = 0; i < 3; ++i)
                    {
                        var clock = Stopwatch.StartNew();
                        udp.Send(ask, ask.Length);
                        var from = new IPEndPoint(IPAddress.Any, 0);
                        try { udp.Receive(ref from); } catch (SocketException) { continue; }
                        int ms = (int)clock.ElapsedMilliseconds;
                        if (best < 0 || ms < best) best = ms;
                    }
                }
            }
            catch (Exception) { }
            return best;
        }

        /// The servers hosts chose to list publicly, as invite code and name.
        public static List<KeyValuePair<string, string>> PublicServers()
        {
            var found = new List<KeyValuePair<string, string>>();
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using (var client = new WebClient())
                {
                    var servers = new JavaScriptSerializer().DeserializeObject(client.DownloadString("https://rygt-server-list.rygt7.workers.dev/servers")) as object[];
                    foreach (var server in (servers ?? new object[0]).OfType<Dictionary<string, object>>())
                    {
                        try { found.Add(new KeyValuePair<string, string>(MakeCode(IPAddress.Parse(Text(server, "address")), Convert.ToInt32(server["port"]), Text(server, "token")), Text(server, "name") + " (public)")); }
                        catch (Exception) { }  // one bad entry does not hide the others
                    }
                }
            }
            catch (Exception) { }
            return found;
        }

        /// What says this PC owns its online IDs on the servers it joins (kept in RyGT.ini).
        public static string Secret(string root)
        {
            string secret = Get(ReadIni(root), "online_secret");
            if (secret.Length > 0) return secret;
            secret = NewToken();
            WriteIni(root, new Dictionary<string, string> { { "online_secret", secret } });
            return secret;
        }

        public static string MakeCode(IPAddress address, int port, string token)
        {
            var bytes = new List<byte>(address.GetAddressBytes());
            bytes.Add((byte)(port >> 8));
            bytes.Add((byte)port);
            for (int i = 0; i < 12; i += 2) bytes.Add(Convert.ToByte(token.Substring(i, 2), 16));
            var text = new StringBuilder();
            int buffer = 0, bits = 0;
            foreach (byte b in bytes)
            {
                buffer = (buffer << 8) | b;
                bits += 8;
                while (bits >= 5) { text.Append(Base32[(buffer >> (bits - 5)) & 31]); bits -= 5; }
            }
            if (bits > 0) text.Append(Base32[(buffer << (5 - bits)) & 31]);
            string code = text.ToString();
            return "RYGT-" + string.Join("-", Enumerable.Range(0, code.Length / 5).Select(i => code.Substring(i * 5, 5)));
        }

        public static bool ReadCode(string code, out string host, out int port, out string token)
        {
            host = null; port = 0; token = null;
            string text = (code ?? "").ToUpperInvariant().Replace("-", "").Replace(" ", "");
            if (text.StartsWith("RYGT")) text = text.Substring(4);
            text = text.Replace('O', '0').Replace('I', '1').Replace('L', '1');
            if (text.Length != 20) return false;
            var bytes = new List<byte>();
            int buffer = 0, bits = 0;
            foreach (char c in text)
            {
                int value = Base32.IndexOf(c);
                if (value < 0) return false;
                buffer = (buffer << 5) | value;
                bits += 5;
                if (bits >= 8) { bytes.Add((byte)(buffer >> (bits - 8))); bits -= 8; }
            }
            if (bytes.Count < 12) return false;
            host = new IPAddress(bytes.Take(4).ToArray()).ToString();
            port = (bytes[4] << 8) | bytes[5];
            token = string.Concat(bytes.Skip(6).Take(6).Select(b => b.ToString("x2")));
            return port > 0;
        }

        public static string NewToken()
        {
            var bytes = new byte[6];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return string.Concat(bytes.Select(b => b.ToString("x2")));
        }

        /// This PC's IPv4 addresses for invite codes, as (address, what network it is on).
        public static List<KeyValuePair<string, string>> LocalAddresses()
        {
            var found = new List<KeyValuePair<string, string>>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    string ip = unicast.Address.ToString();
                    if (ip.StartsWith("169.254.")) continue;
                    string label = nic.Name;
                    string what = (nic.Name + " " + nic.Description).ToLowerInvariant();
                    if (what.Contains("radmin")) label = "Radmin VPN";
                    else if (what.Contains("tailscale")) label = "Tailscale";
                    else if (what.Contains("zerotier")) label = "ZeroTier";
                    else if (what.Contains("hamachi")) label = "Hamachi";
                    else label = nic.Name + ", this network only";
                    found.Add(new KeyValuePair<string, string>(ip, label));
                }
            }
            return found;
        }

        /// This PC's address on the internet, from a public "what is my IP" service; null if it
        /// cannot be reached.
        public static string PublicAddress()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var request = (HttpWebRequest)WebRequest.Create("https://api.ipify.org");
                request.Timeout = 8000;
                using (var response = request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    string text = reader.ReadToEnd().Trim();
                    IPAddress parsed;
                    return IPAddress.TryParse(text, out parsed) && parsed.AddressFamily == AddressFamily.InterNetwork ? text : null;
                }
            }
            catch (Exception) { return null; }
        }

        static string UsersPath(string root) { return Path.Combine(root, "user", "users.json"); }

        static Dictionary<string, object> FirstPlayer(Dictionary<string, object> users)
        {
            object node;
            var group = users.TryGetValue("Users", out node) ? node as Dictionary<string, object> : null;
            var list = group != null && group.TryGetValue("user", out node) ? node as object[] : null;
            if (list == null) return null;
            return list.OfType<Dictionary<string, object>>().OrderBy(u => Convert.ToInt32(u.ContainsKey("player_index") ? u["player_index"] : 9)).FirstOrDefault();
        }

        /// The password user\users.json has for player 1 when they sign in as this online ID.
        public static string StoredPassword(string root, string onlineId)
        {
            try
            {
                var users = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(UsersPath(root))) as Dictionary<string, object>;
                var player = users == null ? null : FirstPlayer(users);
                if (player == null) return null;
                string npid = Convert.ToString(player.ContainsKey("shadnet_npid") ? player["shadnet_npid"] : "");
                string password = Convert.ToString(player.ContainsKey("shadnet_password") ? player["shadnet_password"] : "");
                return string.Equals(npid, onlineId, StringComparison.OrdinalIgnoreCase) && password.Length > 0 ? password : null;
            }
            catch (Exception) { return null; }
        }

        /// Signs player 1 in to shadNet with this account (user\users.json, made by GT7's first start;
        /// the file before the first change is kept as users.json.before-online).
        public static void SetAccount(string root, string onlineId, string password)
        {
            // Before GT7's first start: made here, the way GT7 makes it, so the first start is online too.
            if (!File.Exists(UsersPath(root))) WriteDefaultUsers(root);
            var serializer = new JavaScriptSerializer();
            var users = serializer.DeserializeObject(File.ReadAllText(UsersPath(root))) as Dictionary<string, object>;
            var player = users == null ? null : FirstPlayer(users);
            if (player == null) throw new InvalidOperationException("user\\users.json has no players.");
            string backup = UsersPath(root) + ".before-online";
            if (!File.Exists(backup)) File.Copy(UsersPath(root), backup);
            player["shadnet_enabled"] = true;
            player["shadnet_npid"] = onlineId;
            player["shadnet_email"] = onlineId + "@shadps4.local";
            player["shadnet_password"] = password;
            player["shadnet_token"] = "";
            File.WriteAllText(UsersPath(root), serializer.Serialize(users), new UTF8Encoding(false));
        }

        static void WriteDefaultUsers(string root)
        {
            var list = Enumerable.Range(1, 4).Select(i => new Dictionary<string, object> {
                { "np_age", 30 }, { "np_country", "us" }, { "np_date_of_birth", "1994-01-01" }, { "np_language", "en" },
                { "player_index", i }, { "shadnet_email", "" }, { "shadnet_enabled", false }, { "shadnet_npid", "" },
                { "shadnet_password", "" }, { "shadnet_token", "" }, { "user_color", i }, { "user_id", 999 + i },
                { "user_name", i == 1 ? "shadGT" : "shadGT-" + i } }).ToArray();
            var users = new Dictionary<string, object> { { "Users", new Dictionary<string, object> { { "commit_hash", "" }, { "user", list } } } };
            Directory.CreateDirectory(Path.GetDirectoryName(UsersPath(root)));
            File.WriteAllText(UsersPath(root), new JavaScriptSerializer().Serialize(users), new UTF8Encoding(false));
        }

        /// GET on a RyGTServer; the JSON reply, also for errors it answers with a message.
        public static Dictionary<string, object> Get(string url, int timeoutMs)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            try
            {
                using (var response = request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                    return new JavaScriptSerializer().DeserializeObject(reader.ReadToEnd()) as Dictionary<string, object>;
            }
            catch (WebException e)
            {
                if (e.Response == null) throw;
                using (var reader = new StreamReader(e.Response.GetResponseStream()))
                {
                    try { return new JavaScriptSerializer().DeserializeObject(reader.ReadToEnd()) as Dictionary<string, object>; }
                    catch (ArgumentException) { throw e; }
                }
            }
        }

        public static string Text(Dictionary<string, object> json, string key)
        {
            object value;
            return json != null && json.TryGetValue(key, out value) ? Convert.ToString(value) : "";
        }

        public static bool PortOpen(string host, int port)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var attempt = client.BeginConnect(host, port, null, null);
                    return attempt.AsyncWaitHandle.WaitOne(500) && client.Connected;
                }
            }
            catch (Exception) { return false; }
        }

        public static bool WaitForPort(int port, int seconds)
        {
            for (int i = 0; i < seconds * 4; ++i)
            {
                if (PortOpen("127.0.0.1", port)) return true;
                Thread.Sleep(250);
            }
            return false;
        }
    }

    partial class LoaderForm
    {
        TextBox hostId, joinId, joinCode;
        ComboBox hostAddress;
        Label hostState, inviteCode, joinState, onlineState;
        Button startHosting, stopHosting;
        bool preparingServer;
        CheckBox listPublicly;
        FlowLayoutPanel serverList;
        TextBox serverAddress;
        GlowButton modeOnline, modeOffline;
        System.Windows.Forms.Timer modeTimer;
        List<KeyValuePair<string, string>> addresses = new List<KeyValuePair<string, string>>();

        string ServerFolder { get { return Path.Combine(root, "server"); } }
        string ShadNetFolder { get { return Path.Combine(ServerFolder, "shadnet"); } }
        string ServerData { get { return Path.Combine(ServerFolder, "server-data"); } }
        string TokenPath { get { return Path.Combine(ServerData, "invite.token"); } }

        /// The Online page: host (start the servers, show the invite code), join (enter a code), play.
        void BuildOnlinePage()
        {
            var page = NewPage("Online", "Online",
                "Host GT7's online service on this PC and invite friends with a code, or join a friend with theirs. " +
                "Room races are peer to peer, so every player needs to reach the others: the same network, a VPN " +
                "(Radmin VPN, Tailscale), or the internet with port 3658 forwarded on every player's router.");
            var ini = OnlinePlay.ReadIni(root);
            string savedId = OnlinePlay.Get(ini, "online_id");

            // Host.
            var host = Section(page, "Host");
            host.Controls.Add(Hint("Runs GT7's online service here (RyGTServer and shadNet, in the server folder). " +
                                   "Friends join with the invite code below; you play too."));
            hostId = OnlineField(host, "Your online ID", savedId);
            host.Controls.Add(FieldLabel("Friends reach this PC at"));
            var addressRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), BackColor = Color.Transparent };
            // Editable: an internet address can be typed in when Find my internet address cannot get it.
            hostAddress = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 420, Margin = new Padding(0, 0, 8, 0) };
            addresses = OnlinePlay.LocalAddresses();
            string savedAddress = OnlinePlay.Get(ini, "invite_address");
            if (savedAddress.Length > 0 && !addresses.Any(a => a.Key == savedAddress))
                addresses.Insert(0, new KeyValuePair<string, string>(savedAddress, "internet, ports forwarded"));
            foreach (var address in addresses) hostAddress.Items.Add(address.Key + "   (" + address.Value + ")");
            if (hostAddress.Items.Count > 0) hostAddress.SelectedIndex = Math.Max(0, addresses.FindIndex(a => a.Key == savedAddress));
            hostAddress.SelectedIndexChanged += delegate { ShowInviteCode(); };
            hostAddress.TextChanged += delegate { ShowInviteCode(); };
            var findPublic = new Button { Text = "Find my internet address", AutoSize = true, Margin = new Padding(0) };
            findPublic.Click += delegate { FindPublicAddress(); };
            addressRow.Controls.Add(hostAddress);
            addressRow.Controls.Add(findPublic);
            host.Controls.Add(addressRow);
            host.Controls.Add(Hint("For friends over the internet, forward these ports on your router to this PC: TCP and UDP " +
                                   OnlinePlay.ServerPort + " (RyGTServer and its relay for players who cannot reach each other), TCP " +
                                   OnlinePlay.ShadNetPort + " and " + OnlinePlay.ShadNetWebApiPort +
                                   " (shadNet) and TCP and UDP " + OnlinePlay.P2PPort + " (the game), then use your internet address."));
            var hostButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 10, 0, 0), BackColor = Color.Transparent };
            startHosting = new Button { Text = "Start hosting", AutoSize = true, Tag = "accent", Margin = new Padding(0, 0, 10, 0) };
            stopHosting = new Button { Text = "Stop hosting", AutoSize = true, Margin = new Padding(0, 0, 10, 0) };
            var serverStatus = new Button { Text = "Server status", AutoSize = true, Margin = new Padding(0) };
            startHosting.Click += delegate { StartHosting(); };
            stopHosting.Click += delegate { StopHosting(); };
            serverStatus.Click += delegate { try { Process.Start("http://127.0.0.1:" + OnlinePlay.ServerPort + "/"); } catch (Exception) { } };
            hostButtons.Controls.AddRange(new Control[] { startHosting, stopHosting, serverStatus });
            host.Controls.Add(hostButtons);
            listPublicly = new CheckBox { Text = "List my server publicly (anyone can join, and everyone can see this PC's internet address)", AutoSize = true,
                                          Checked = OnlinePlay.Get(ini, "public_server") == "1", Margin = new Padding(0, 8, 0, 0) };
            listPublicly.CheckedChanged += delegate { OnlinePlay.WriteIni(root, new Dictionary<string, string> { { "public_server", listPublicly.Checked ? "1" : "0" } }); };
            host.Controls.Add(listPublicly);
            hostState = Hint("");
            host.Controls.Add(hostState);
            host.Controls.Add(FieldLabel("Invite code"));
            inviteCode = new Label { Text = "-", AutoSize = true, Font = new Font("Consolas", 13f, FontStyle.Bold), Margin = new Padding(0, 2, 0, 4) };
            host.Controls.Add(inviteCode);
            var codeRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), BackColor = Color.Transparent };
            var copyCode = new Button { Text = "Copy", AutoSize = true, Margin = new Padding(0, 0, 10, 0) };
            var newCode = new Button { Text = "New code", AutoSize = true, Margin = new Padding(0) };
            copyCode.Click += delegate { if (inviteCode.Text.StartsWith("RYGT")) { Clipboard.SetText(inviteCode.Text); SetStatus("Invite code copied."); } };
            newCode.Click += delegate { NewInviteCode(); };
            codeRow.Controls.AddRange(new Control[] { copyCode, newCode });
            host.Controls.Add(codeRow);
            host.Controls.Add(Hint("A new code stops older ones working for friends who have not joined yet."));

            // Servers on this network.
            var servers = Section(page, "Servers");
            servers.Controls.Add(Hint("Finds public servers and servers on your home network, or type a local address. No code needed."));
            serverList = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 6, 0, 0), BackColor = Color.Transparent };
            servers.Controls.Add(serverList);
            serverAddress = OnlineField(servers, "Server address (optional)", "");
            var serverButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 10, 0, 0), BackColor = Color.Transparent };
            var findServers = new Button { Text = "Find servers", AutoSize = true, Margin = new Padding(0, 0, 10, 0) };
            findServers.Click += delegate { FindServers(null); };
            serverButtons.Controls.AddRange(new Control[] { findServers });
            servers.Controls.Add(serverButtons);

            // Join.
            var join = Section(page, "Join a friend");
            join.Controls.Add(Hint("Enter the invite code your friend's ryGT7 shows. It makes your account on their server and " +
                                   "points GT7 at it."));
            joinId = OnlineField(join, "Your online ID", savedId);
            joinCode = OnlineField(join, "Invite code", "");
            var joinButton = new Button { Text = "Join", AutoSize = true, Tag = "accent", Margin = new Padding(0, 10, 0, 0) };
            joinButton.Click += delegate { Join(); };
            join.Controls.Add(joinButton);
            joinState = Hint("");
            join.Controls.Add(joinState);

            // Play.
            var play = Section(page, "Play");
            onlineState = Hint("");
            play.Controls.Add(onlineState);
            var playButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 10, 0, 0), BackColor = Color.Transparent };
            var playOnline = new Button { Text = "Play online", AutoSize = true, Tag = "accent", Margin = new Padding(0, 0, 10, 6) };
            var offline = new Button { Text = "Play offline instead", AutoSize = true, Margin = new Padding(0, 0, 10, 6) };
            var firewall = new Button { Text = "Allow through firewall", AutoSize = true, Margin = new Padding(0, 0, 0, 6) };
            playOnline.Click += delegate
            {
                if (OnlinePlay.Get(OnlinePlay.ReadIni(root), "online_server").Length == 0) { MessageBox.Show(this, "Start hosting or join a friend first."); return; }
                OnlinePlay.WriteIni(root, new Dictionary<string, string> { { "online", "1" } });
                RefreshOnline();
                PlayWithServer();
            };
            offline.Click += delegate { GoOffline(); };
            firewall.Click += delegate { AllowThroughFirewall(); };
            playButtons.Controls.AddRange(new Control[] { playOnline, offline, firewall });
            play.Controls.Add(playButtons);
            play.Controls.Add(Hint("Allow through firewall lets the other players' games in (GT7.exe, and the host's server ports) " +
                                   "on every network type. Windows asks for administrator rights."));

            ShowInviteCode();
            RefreshOnline();
        }

        static Label FieldLabel(string text)
        {
            return new Label { Text = text, AutoSize = true, ForeColor = Dark.DimText, Tag = "heading", Margin = new Padding(0, 10, 0, 3) };
        }

        TextBox OnlineField(TableLayoutPanel parent, string label, string value)
        {
            parent.Controls.Add(FieldLabel(label));
            var field = new Card(Dark.Field) { AutoSize = false, Size = new Size(420, 32), Radius = 3, Edge = Color.Empty, Padding = new Padding(10, 7, 10, 6), Margin = new Padding(0, 0, 0, 2) };
            var box = new TextBox { Text = value, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Margin = new Padding(0) };
            field.Controls.Add(box);
            parent.Controls.Add(field);
            return box;
        }

        static bool ValidOnlineId(string id)
        {
            return id.Length >= 3 && id.Length <= 16 && id.All(c => (char.IsLetterOrDigit(c) && c < 128) || c == '-' || c == '_');
        }

        static Process[] Running(string name, string folder)
        {
            return Process.GetProcessesByName(name).Where(p =>
            {
                try { return p.MainModule.FileName.StartsWith(folder, StringComparison.OrdinalIgnoreCase); }
                catch (Exception) { return true; }
            }).ToArray();
        }

        bool Hosting { get { return Running("RyGTServer", ServerFolder).Length > 0 && Running("shadnet", ShadNetFolder).Length > 0; } }

        string InviteToken()
        {
            if (File.Exists(TokenPath))
            {
                string saved = File.ReadAllText(TokenPath).Trim();
                if (saved.Length == 12) return saved;
            }
            string token = OnlinePlay.NewToken();
            Directory.CreateDirectory(Path.GetDirectoryName(TokenPath));
            File.WriteAllText(TokenPath, token);
            return token;
        }

        /// The address in the box: a listed one, or one typed in (an IPv4 address).
        string SelectedAddress
        {
            get
            {
                if (hostAddress.SelectedIndex >= 0) return addresses[hostAddress.SelectedIndex].Key;
                string typed = hostAddress.Text.Trim().Split(' ')[0];
                IPAddress parsed;
                return IPAddress.TryParse(typed, out parsed) && parsed.AddressFamily == AddressFamily.InterNetwork ? typed : null;
            }
        }

        void ShowInviteCode()
        {
            IPAddress address;
            if (SelectedAddress == null || !IPAddress.TryParse(SelectedAddress, out address))
            {
                inviteCode.Text = "-";
                return;
            }
            inviteCode.Text = OnlinePlay.MakeCode(address, OnlinePlay.ServerPort, InviteToken());
            OnlinePlay.WriteIni(root, new Dictionary<string, string> { { "invite_address", address.ToString() } });
        }

        void NewInviteCode()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TokenPath));
            File.WriteAllText(TokenPath, OnlinePlay.NewToken());
            ShowInviteCode();
            if (Hosting) StartHosting();  // RyGTServer takes the new token on a restart
        }

        /// Adds this PC's internet address (for friends reaching it through forwarded ports) to the list.
        void FindPublicAddress()
        {
            SetStatus("Asking api.ipify.org for this PC's internet address...");
            var thread = new Thread(delegate ()
            {
                string found = OnlinePlay.PublicAddress();
                BeginInvoke(new Action(delegate
                {
                    if (found == null) { SetStatus("Could not find this PC's internet address."); return; }
                    int index = addresses.FindIndex(a => a.Key == found);
                    if (index < 0)
                    {
                        addresses.Insert(0, new KeyValuePair<string, string>(found, "internet, ports forwarded"));
                        hostAddress.Items.Insert(0, found + "   (internet, ports forwarded)");
                        index = 0;
                    }
                    hostAddress.SelectedIndex = index;
                    SetStatus("This PC's internet address is " + found + ". Friends reach it once the router forwards the ports.");
                }));
            });
            thread.IsBackground = true;
            thread.Start();
        }

        void RefreshOnline()
        {
            if (InvokeRequired) { BeginInvoke(new Action(RefreshOnline)); return; }
            var ini = OnlinePlay.ReadIni(root);
            bool hosting = Hosting;
            hostState.Text = hosting ? "Hosting: RyGTServer and shadNet are running." : "Not hosting.";
            startHosting.Text = hosting ? "Restart hosting" : "Start hosting";
            stopHosting.Enabled = hosting;
            string server = OnlinePlay.Get(ini, "online_server");
            string id = OnlinePlay.Get(ini, "online_id");
            if (!OnlinePlay.Enabled(root) || server.Length == 0)
                onlineState.Text = "Online is off: Play starts GT7 offline.";
            else if (server.StartsWith("127.0.0.1"))
                onlineState.Text = "Online as " + id + " on this PC's server" + (hosting ? "." : " - start hosting first, or GT7 finds no server.");
            else
            {
                string text = "Online as " + id + " on the server at " + server + ".";
                onlineState.Text = text;
                int colon = server.LastIndexOf(':'), port;
                if (colon > 0 && int.TryParse(server.Substring(colon + 1), out port))
                {
                    var thread = new Thread(delegate ()
                    {
                        int ms = OnlinePlay.Ping(server.Substring(0, colon), port);
                        BeginInvoke(new Action(delegate
                        {
                            if (onlineState.Text == text) onlineState.Text = text + (ms < 0 ? " Ping: no answer." : " Ping: " + ms + " ms.");
                        }));
                    });
                    thread.IsBackground = true;
                    thread.Start();
                }
            }
        }

        /// RyGT.ini's online settings. game_log=1 turns on the GT7 hooks that also sign GT7 in to
        /// the server; online_address is this PC's address for other players (a VPN address);
        /// without it the game uses the one on its route to the server.
        void UseServer(string id, string server, string ownAddress)
        {
            OnlinePlay.WriteIni(root, new Dictionary<string, string> {
                { "online", "1" }, { "online_id", id }, { "online_server", server }, { "online_address", ownAddress }, { "game_log", "1" } });
        }

        /// Starts shadNet and RyGTServer (with the invite token), makes this player's account and
        /// points the game at this PC.
        void StartHosting(Action done = null)
        {
            string id = hostId.Text.Trim();
            if (!ValidOnlineId(id)) { MessageBox.Show(this, "An online ID is 3-16 letters, digits, - or _."); return; }
            string shadnetExe = Path.Combine(ShadNetFolder, "shadnet.exe"), serverExe = Path.Combine(ServerFolder, "RyGTServer.exe");
            if (!File.Exists(shadnetExe) || !File.Exists(serverExe))
            {
                MessageBox.Show(this, "Hosting needs server\\RyGTServer.exe and server\\shadnet\\shadnet.exe next to RyGT7.exe.");
                return;
            }
            // A VPN address is this PC's address for the other players too; an internet or LAN one
            // is not (the game finds its own on the route to the server).
            string address = SelectedAddress;
            bool vpn = hostAddress.SelectedIndex >= 0 && new[] { "Radmin VPN", "Tailscale", "ZeroTier", "Hamachi" }.Contains(addresses[hostAddress.SelectedIndex].Value);
            startHosting.Enabled = false;
            hostState.Text = "Starting...";
            var thread = new Thread(delegate ()
            {
                string problem = null;
                try
                {
                    // shadNet: left running if it already is (it keeps players signed in).
                    if (Running("shadnet", ShadNetFolder).Length == 0)
                    {
                        EnsureShadNetKeys();
                        // A shadNet that was not closed normally leaves a lock that stops the next one starting.
                        string stale = Path.Combine(ShadNetFolder, "worlds.cfg.running");
                        if (File.Exists(stale)) File.Delete(stale);
                        Process.Start(new ProcessStartInfo(shadnetExe) { WorkingDirectory = ShadNetFolder, WindowStyle = ProcessWindowStyle.Minimized });
                    }
                    if (!OnlinePlay.WaitForPort(OnlinePlay.ShadNetPort, 20)) throw new InvalidOperationException("shadNet did not start (see its window).");
                    // RyGTServer: restarted so it has the current invite token (rooms are kept in
                    // server-data\gt7\rooms.json across the restart).
                    foreach (var old in Running("RyGTServer", ServerFolder)) { try { old.Kill(); old.WaitForExit(3000); } catch (Exception) { } }
                    Process.Start(new ProcessStartInfo(serverExe, "--data \"" + ServerData + "\" --invite " + InviteToken() + " --shadnet \"" + ShadNetFolder + "\"" +
                                                                  (listPublicly.Checked ? " --list \"" + id + "\"" : ""))
                    { WorkingDirectory = ServerFolder, WindowStyle = ProcessWindowStyle.Minimized });
                    if (!OnlinePlay.WaitForPort(OnlinePlay.ServerPort, 15)) throw new InvalidOperationException("RyGTServer did not start (see its window).");

                    // This player's account, made the same way as a friend's. Always asked for: a
                    // new server (fresh shadNet database) has no account yet even when users.json
                    // has a password, so that password is offered for it.
                    string stored = OnlinePlay.StoredPassword(root, id);
                    var reply = OnlinePlay.Get("http://127.0.0.1:" + OnlinePlay.ServerPort + "/rygt/invite/join?token=" + InviteToken() +
                                               "&online=" + Uri.EscapeDataString(id) + "&secret=" + OnlinePlay.Secret(root) +
                                               (stored != null ? "&password=" + Uri.EscapeDataString(stored) : ""), 15000);
                    if (OnlinePlay.Text(reply, "result") != "True") throw new InvalidOperationException(OnlinePlay.Text(reply, "message"));
                    OnlinePlay.SetAccount(root, id, OnlinePlay.Text(reply, "password"));
                    UseShadNet("127.0.0.1");
                    UseServer(id, "127.0.0.1:" + OnlinePlay.ServerPort, vpn ? address : null);
                }
                catch (Exception e) { problem = e.Message; }
                BeginInvoke(new Action(delegate
                {
                    startHosting.Enabled = true;
                    RefreshOnline();
                    ShowInviteCode();
                    preparingServer = false;
                    if (problem != null)
                    {
                        hostState.Text = "Could not start hosting: " + problem;
                        if (done != null) MessageBox.Show(this, hostState.Text);
                        return;
                    }
                    SetStatus("Hosting. Send friends the invite code, then press Play online.");
                    if (done != null) done();
                }));
            });
            thread.IsBackground = true;
            thread.Start();
        }

        /// A released shadnet.cfg has its keys blank (each host's must be its own): the key
        /// RyGTServer makes accounts with and the registration key get random values here, once.
        void EnsureShadNetKeys()
        {
            string config = Path.Combine(ShadNetFolder, "shadnet.cfg");
            if (!File.Exists(config)) return;
            var lines = File.ReadAllLines(config);
            bool changed = false;
            for (int i = 0; i < lines.Length; ++i)
            {
                foreach (string key in new[] { "MemberApiKey", "RegistrationSecretKey" })
                {
                    if (lines[i].Trim() != key + "=") continue;
                    var bytes = new byte[24];
                    using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
                    lines[i] = key + "=" + string.Concat(bytes.Select(b => b.ToString("x2")));
                    changed = true;
                }
            }
            if (changed) File.WriteAllLines(config, lines);
        }

        void StopHosting()
        {
            foreach (var process in Running("RyGTServer", ServerFolder).Concat(Running("shadnet", ShadNetFolder)))
            {
                try { process.Kill(); process.WaitForExit(3000); } catch (Exception) { }
            }
            // A killed shadNet leaves its lock behind; the next start removes it.
            RefreshOnline();
            SetStatus("Stopped hosting.");
        }

        /// GT7's PSN sign-in goes to the shadNet at this address.
        void UseShadNet(string host)
        {
            WriteSetting("General", "shad_net_enabled", true);
            WriteSetting("General", "connected_to_network", true);
            WriteSetting("General", "shadnet_server", host + ":" + OnlinePlay.ShadNetPort);
            WriteSetting("General", "shadnet_webapi_server", "http://" + host + ":" + OnlinePlay.ShadNetWebApiPort);
        }

        /// A server in the list: its name and a Join button, which joins it and starts the game.
        Control ServerRow(string code, string name)
        {
            var row = new Card(Dark.Field) { AutoSize = false, Size = new Size(520, 52), Radius = 3, Edge = Color.Empty, Padding = new Padding(14, 8, 8, 8), Margin = new Padding(0, 0, 0, 6) };
            var title = new Label { Text = name, AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI Semibold", 10f) };
            var join = new Button { Text = "Join", Tag = "accent", Dock = DockStyle.Right, Width = 96 };
            var ping = new Label { Text = "...", Dock = DockStyle.Right, Width = 90, TextAlign = ContentAlignment.MiddleRight, ForeColor = Dark.DimText };
            string host, token;
            int port;
            if (OnlinePlay.ReadCode(code, out host, out port, out token))
            {
                var pinging = new Thread(delegate ()
                {
                    int ms = OnlinePlay.Ping(host, port);
                    if (ping.IsHandleCreated) ping.BeginInvoke(new Action(delegate { ping.Text = ms < 0 ? "no answer" : ms + " ms"; }));
                });
                pinging.IsBackground = true;
                pinging.Start();
            }
            join.Click += delegate
            {
                joinCode.Text = code;
                Join(PlayWithServer);
            };
            row.Controls.Add(title);
            row.Controls.Add(ping);
            row.Controls.Add(join);
            return row;
        }

        /// Lists the servers found; `then`, when given, joins the first one.
        void FindServers(Action then)
        {
            string typed = serverAddress.Text.Trim();
            IPAddress parsed;
            string address = IPAddress.TryParse(typed, out parsed) ? typed : null;
            if (typed.Length > 0 && address == null) { MessageBox.Show(this, "A server address looks like 192.168.1.20."); return; }
            SetStatus("Looking for servers...");
            var thread = new Thread(delegate ()
            {
                var found = OnlinePlay.FindServers(address);
                if (then == null) found.AddRange(OnlinePlay.PublicServers());
                BeginInvoke(new Action(delegate
                {
                    serverList.Controls.Clear();
                    foreach (var server in found) serverList.Controls.Add(ServerRow(server.Key, server.Value));
                    Dark.Apply(serverList);
                    SetStatus(found.Count == 0 ? "No servers found on your network." : found.Count + " server(s) found.");
                    if (then == null) return;
                    if (found.Count == 0) { MessageBox.Show(this, "No servers found. Is the host's server running, on the same network, and allowed through their firewall?"); return; }
                    joinCode.Text = found[0].Key;
                    Join(then);
                }));
            });
            thread.IsBackground = true;
            thread.Start();
        }

        void Join(Action done = null)
        {
            string id = joinId.Text.Trim(), host, token;
            int port;
            if (!ValidOnlineId(id)) { MessageBox.Show(this, "An online ID is 3-16 letters, digits, - or _."); return; }
            if (!OnlinePlay.ReadCode(joinCode.Text, out host, out port, out token))
            {
                MessageBox.Show(this, "That is not an invite code. It looks like RYGT-XXXXX-XXXXX-XXXXX-XXXXX.");
                return;
            }
            // Over a VPN, this PC's VPN address is its address for the other players.
            string vpnAddress = OnlinePlay.LocalAddresses()
                .Where(a => new[] { "Radmin VPN", "Tailscale", "ZeroTier", "Hamachi" }.Contains(a.Value))
                .Select(a => a.Key).FirstOrDefault(a => a.Split('.')[0] == host.Split('.')[0]);
            joinState.Text = "Contacting " + host + "...";
            var thread = new Thread(delegate ()
            {
                string result;
                bool joined = false;
                try
                {
                    var reply = OnlinePlay.Get("http://" + host + ":" + port + "/rygt/invite/join?token=" + token + "&online=" + Uri.EscapeDataString(id) + "&secret=" + OnlinePlay.Secret(root), 15000);
                    if (OnlinePlay.Text(reply, "result") != "True") throw new InvalidOperationException(OnlinePlay.Text(reply, "message"));
                    OnlinePlay.SetAccount(root, id, OnlinePlay.Text(reply, "password"));
                    UseShadNet(host);
                    UseServer(id, host + ":" + port, vpnAddress);
                    result = "Joined " + host + " as " + id + ". Press Play online.";
                    joined = true;
                }
                catch (WebException e)
                {
                    result = "Could not reach " + host + ":" + port + " (" + e.Message + "). Is your friend hosting, with the ports " +
                             "forwarded or on the same VPN as you?";
                }
                catch (Exception e) { result = "Could not join: " + e.Message; }
                BeginInvoke(new Action(delegate
                {
                    joinState.Text = result;
                    RefreshOnline();
                    SetStatus(result);
                    if (done == null) return;
                    if (joined) done(); else MessageBox.Show(this, result);
                }));
            });
            thread.IsBackground = true;
            thread.Start();
        }

        /// Offline needs no account or server: GT7.exe has the Offline mode patch either way, and
        /// without a network GT7 does not wait for a sign-in.
        void GoOffline()
        {
            OnlinePlay.WriteIni(root, new Dictionary<string, string> { { "online", "0" } });
            WriteSetting("General", "shad_net_enabled", false);
            WriteSetting("General", "connected_to_network", false);
            RefreshOnline();
        }

        /// A player who chose offline is never asked; a new one is, and so is one going online.
        bool NeedsAccount { get { return OnlinePlay.Get(OnlinePlay.ReadIni(root), "online") != "0" && !HasAccount; } }

        bool HasAccount
        {
            get
            {
                var ini = OnlinePlay.ReadIni(root);
                string id = OnlinePlay.Get(ini, "online_id");
                return id.Length > 0 && OnlinePlay.Get(ini, "online_server").Length > 0 && OnlinePlay.StoredPassword(root, id) != null;
            }
        }

        /// community.txt next to RyGT7.exe holds the invite code of a shared server: players then
        /// only pick a name. Without the file the window offers joining and hosting.
        string CommunityCode
        {
            get
            {
                string path = Path.Combine(root, "community.txt");
                if (!File.Exists(path)) return null;
                string code = File.ReadAllText(path).Trim(), host, token;
                int port;
                return OnlinePlay.ReadCode(code, out host, out port, out token) ? code : null;
            }
        }

        /// The first-start window: every player makes an account, on a friend's server or their own.
        void AskForAccount(Action done)
        {
            string community = CommunityCode;
            using (var form = new Form { Text = "Create your ryGT7 account", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                                         StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(600, 270), BackColor = Dark.Panel })
            {
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(16), BackColor = Color.Transparent };
                var intro = Hint(community != null ? "Pick a name for online play." :
                                 "Your account is made on the server you play on. Join a friend with their invite code, or host your own server.");
                intro.AutoSize = true;
                intro.MaximumSize = new Size(420, 0);
                layout.Controls.Add(intro);
                var id = OnlineField(layout, "Your online ID", OnlinePlay.Get(OnlinePlay.ReadIni(root), "online_id"));
                var code = community != null ? null : OnlineField(layout, "Invite code (to join a friend)", "");
                var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 12, 0, 0), BackColor = Color.Transparent };
                var joinFriend = new Button { Text = community != null ? "Play online" : "Join friend", AutoSize = true, Tag = "accent", Margin = new Padding(0, 0, 10, 0), DialogResult = DialogResult.Yes };
                var hostOwn = new Button { Text = "Host my own server", AutoSize = true, Margin = new Padding(0, 0, 10, 0), DialogResult = DialogResult.No };
                var findLocal = new Button { Text = "Find local server", AutoSize = true, Margin = new Padding(0, 0, 10, 0), DialogResult = DialogResult.Retry };
                var playOffline = new Button { Text = "Play offline", AutoSize = true, Margin = new Padding(0), DialogResult = DialogResult.Ignore };
                buttons.Controls.AddRange(new Control[] { joinFriend, findLocal, hostOwn, playOffline });
                layout.Controls.Add(buttons);
                form.Controls.Add(layout);
                Dark.Apply(form);
                var choice = form.ShowDialog(this);
                if (choice == DialogResult.Yes)
                {
                    joinId.Text = id.Text;
                    joinCode.Text = community ?? code.Text;
                    Join(done);
                }
                else if (choice == DialogResult.No)
                {
                    hostId.Text = id.Text;
                    StartHosting(done);
                }
                else if (choice == DialogResult.Retry)
                {
                    joinId.Text = id.Text;
                    FindServers(done);
                }
                else if (choice == DialogResult.Ignore)
                {
                    GoOffline();
                    done();
                }
            }
        }

        /// The Play button: it opens into Online and Offline in its own place, and closes again
        /// by itself if neither is picked.
        void ChooseMode()
        {
            if (preparingServer) return;
            if (modeOnline == null)
            {
                modeOnline = new GlowButton { Text = "Online", Font = play.Font, Visible = false };
                modeOffline = new GlowButton { Text = "Offline", Font = play.Font, Visible = false };
                modeOnline.Click += delegate
                {
                    CloseModes();
                    OnlinePlay.WriteIni(root, new Dictionary<string, string> { { "online", "1" } });
                    RefreshOnline();
                    PlayWithServer();
                };
                modeOffline.Click += delegate { CloseModes(); GoOffline(); Play(); };
                play.Parent.Controls.Add(modeOnline);
                play.Parent.Controls.Add(modeOffline);
                play.Parent.Resize += delegate { CloseModes(); };
                modeTimer = new System.Windows.Forms.Timer { Interval = 8000 };
                modeTimer.Tick += delegate { CloseModes(); };
            }
            const int gap = 8;
            int width = (play.Width - gap) / 2;
            modeOnline.SetBounds(play.Left, play.Top, width, play.Height);
            modeOffline.SetBounds(play.Right - width, play.Top, width, play.Height);
            play.Visible = false;
            modeOnline.Visible = modeOffline.Visible = true;
            modeOnline.BringToFront();
            modeOffline.BringToFront();
            modeOnline.Focus();
            modeTimer.Start();
        }

        void CloseModes()
        {
            modeTimer.Stop();
            modeOnline.Visible = modeOffline.Visible = false;
            play.Visible = true;
        }

        /// Play: asks for an account first, starts this PC's server if the game uses it, and warns
        /// when a friend's server cannot be reached (GT7 takes ages to load without one).
        void PlayWithServer()
        {
            if (preparingServer) return;
            if (NeedsAccount) { AskForAccount(PlayWithServer); return; }
            string server = OnlinePlay.Get(OnlinePlay.ReadIni(root), "online_server");
            if (!OnlinePlay.Enabled(root)) { Play(); return; }
            UseShadNet(server.Substring(0, Math.Max(0, server.LastIndexOf(':'))));
            if (server.StartsWith("127.0.0.1"))
            {
                if (Hosting) { Play(); return; }
                preparingServer = true;
                SetStatus("Starting your server...");
                StartHosting(Play);
                return;
            }
            preparingServer = true;
            SetStatus("Checking the server at " + server + "...");
            var thread = new Thread(delegate ()
            {
                int colon = server.LastIndexOf(':'), port;
                bool open = colon > 0 && int.TryParse(server.Substring(colon + 1), out port) && OnlinePlay.PortOpen(server.Substring(0, colon), port);
                BeginInvoke(new Action(delegate
                {
                    preparingServer = false;
                    if (open || MessageBox.Show(this, "The server at " + server + " is not answering, so GT7 will take a long time to load. Play anyway?",
                                                "ryGT7", MessageBoxButtons.YesNo) == DialogResult.Yes) Play();
                }));
            });
            thread.IsBackground = true;
            thread.Start();
        }

        /// Windows Firewall rules for GT7.exe (the game itself: the other players' games connect to it)
        /// and the host's server ports, on every network type (a VPN often counts as public).
        void AllowThroughFirewall()
        {
            string rules =
                "netsh advfirewall firewall delete rule name=\"ryGT7 online\" >nul 2>&1 & " +
                "netsh advfirewall firewall add rule name=\"ryGT7 online\" dir=in action=allow program=\"" + Exe + "\" profile=any & " +
                "netsh advfirewall firewall add rule name=\"ryGT7 online\" dir=in action=allow protocol=TCP localport=" + OnlinePlay.ServerPort + "," +
                OnlinePlay.ShadNetPort + "," + OnlinePlay.ShadNetWebApiPort + " profile=any & " +
                "netsh advfirewall firewall add rule name=\"ryGT7 online\" dir=in action=allow protocol=UDP localport=" + OnlinePlay.ServerPort + " profile=any";
            try
            {
                using (var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + rules) { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden }))
                    process.WaitForExit(15000);
                SetStatus("Windows Firewall now lets ryGT7 online play in (rules named \"ryGT7 online\").");
            }
            catch (Exception e) { SetStatus("Firewall not changed: " + e.Message); }
        }
    }
}
