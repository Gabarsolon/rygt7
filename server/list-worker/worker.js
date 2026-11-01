// The public server list. RyGTServers that chose to be listed POST /announce every minute; the
// launcher reads GET /servers. A server's address is the one the announcement came from, so a
// host can only list itself. Entries are kept in memory: hosts announce again after a restart.
const EXPIRES_MS = 3 * 60 * 1000;
const MAX_SERVERS = 200;

const json = (value, status = 200) =>
  new Response(JSON.stringify(value), { status, headers: { "content-type": "application/json" } });

export default {
  fetch(request, env) {
    const path = new URL(request.url).pathname;
    if (path === "/account/register" || path === "/account/check") {
      if (request.method === "OPTIONS") return new Response(null, { status: 204, headers: SITE });
      return env.ACCOUNTS.get(env.ACCOUNTS.idFromName("all")).fetch(request);
    }
    const list = env.LIST.get(env.LIST.idFromName("all"));
    return list.fetch(request);
  },
};

// Accounts for every ryGT7 server. The launcher and the website send POST /account/register
// {"online", "email", "password"} and POST /account/check {"online", "password"}; the password is
// a key made from what the player typed, never the text itself (OnlinePlay.AccountKey). The answer
// carries a pass, signed with PASS_KEY (a P-256 private key, JWK), which proves the online ID to
// any host: RyGTServer holds the public half (AccountPass).
const SITE = { "access-control-allow-origin": "*", "access-control-allow-headers": "content-type" };
const PASS_DAYS = 30;
const LIMITS = { register: 10, check: 30, wrong: 10 };  // an hour, each visitor; wrong is each online ID

const reply = (ok, message, status, extra = {}) =>
  new Response(JSON.stringify({ result: ok, message, ...extra }), { status, headers: { ...SITE, "content-type": "application/json" } });
const base64url = (bytes) => btoa(String.fromCharCode(...bytes)).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
const hex = (bytes) => [...bytes].map((b) => b.toString(16).padStart(2, "0")).join("");

export class Accounts {
  attempts = new Map();

  constructor(state, env) {
    this.sql = state.storage.sql;
    this.env = env;
    this.sql.exec(`CREATE TABLE IF NOT EXISTS accounts (name_key TEXT PRIMARY KEY, name TEXT NOT NULL, email TEXT NOT NULL UNIQUE,
                   salt TEXT NOT NULL, verifier TEXT NOT NULL, created INTEGER NOT NULL)`);
  }

  /// False once `who` has used up this hour's attempts of this kind.
  limited(kind, who) {
    const now = Date.now(), hour = 60 * 60 * 1000;
    for (const [key, times] of this.attempts) if (now - times.at(-1) > hour) this.attempts.delete(key);
    const key = `${kind} ${who}`;
    const times = (this.attempts.get(key) ?? []).filter((time) => now - time < hour);
    this.attempts.set(key, times);
    if (times.length >= LIMITS[kind]) return true;
    times.push(now);
    return false;
  }

  async verifier(salt, password) {
    return hex(new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(salt + password))));
  }

  async pass(online) {
    const key = await crypto.subtle.importKey("jwk", JSON.parse(this.env.PASS_KEY), { name: "ECDSA", namedCurve: "P-256" }, false, ["sign"]);
    const body = new TextEncoder().encode(JSON.stringify({ online, exp: Math.floor(Date.now() / 1000) + PASS_DAYS * 86400 }));
    const signature = await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, key, body);
    return `${base64url(body)}.${base64url(new Uint8Array(signature))}`;
  }

  async fetch(request) {
    const path = new URL(request.url).pathname;
    if (request.method !== "POST") return reply(false, "Send a POST.", 400);
    if (!this.env.PASS_KEY) return reply(false, "Sign-up is not open yet.", 503);
    const body = await request.json().catch(() => null);
    const online = String(body?.online ?? "").trim(), password = String(body?.password ?? "");
    const email = String(body?.email ?? "").trim().toLowerCase();
    if (!/^[A-Za-z0-9_-]{3,16}$/.test(online)) return reply(false, "An online ID is 3-16 letters, digits, - or _.", 400);
    if (!/^[0-9a-f]{32}$/.test(password)) return reply(false, "This version cannot sign in here. Update ryGT7.", 400);
    const visitor = request.headers.get("cf-connecting-ip") ?? "";
    const nameKey = online.toLowerCase();

    if (path === "/account/register") {
      if (email.length > 254 || !/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(email)) return reply(false, "That email does not look right.", 400);
      if (this.limited("register", visitor)) return reply(false, "Too many sign-ups from this address. Try again in an hour.", 429);
      const salt = crypto.randomUUID();
      const taken = this.sql.exec("SELECT 1 FROM accounts WHERE name_key = ? OR email = ?", nameKey, email).toArray().length > 0;
      if (taken) return reply(false, "That online ID or email already has an account.", 409);
      this.sql.exec("INSERT INTO accounts VALUES (?, ?, ?, ?, ?, ?)", nameKey, online, email, salt, await this.verifier(salt, password), Date.now());
      return reply(true, "Account made.", 200, { online, pass: await this.pass(online) });
    }

    if (this.limited("check", visitor)) return reply(false, "Too many tries from this address. Try again in an hour.", 429);
    const [account] = this.sql.exec("SELECT name, salt, verifier FROM accounts WHERE name_key = ?", nameKey).toArray();
    if (!account || (await this.verifier(account.salt, password)) !== account.verifier) {
      if (this.limited("wrong", nameKey)) return reply(false, "Too many wrong passwords for this account. Try again in an hour.", 429);
      return reply(false, "Wrong online ID or password.", 401);
    }
    return reply(true, "Signed in.", 200, { online: account.name, pass: await this.pass(account.name) });
  }
}

export class ServerList {
  servers = new Map();

  async fetch(request) {
    const path = new URL(request.url).pathname;
    const now = Date.now();
    for (const [key, server] of this.servers) if (now - server.seen > EXPIRES_MS) this.servers.delete(key);

    // For the website: names only, so hosts' addresses are not on the open web.
    if (request.method === "GET" && path === "/names")
      return new Response(JSON.stringify([...this.servers.values()].map(({ name, game }) => ({ name, game }))), {
        headers: { "content-type": "application/json", "access-control-allow-origin": "*" },
      });

    // ?game= picks the game. The GT7 launcher sends none, so it never sees another game's servers.
    if (request.method === "GET" && path === "/servers") {
      const game = new URL(request.url).searchParams.get("game") || "GT7";
      return json([...this.servers.values()].filter((server) => server.game === game).map(({ seen, ...server }) => server));
    }

    if (request.method === "POST" && path === "/announce") {
      const address = request.headers.get("cf-connecting-ip") || "";
      if (!/^\d+\.\d+\.\d+\.\d+$/.test(address)) return json({ error: "IPv4 only" }, 400);
      const body = await request.json().catch(() => null);
      const port = Number(body?.port);
      const name = String(body?.name ?? "").replace(/[^\x20-\x7e]/g, "").slice(0, 32).trim();
      const token = String(body?.token ?? "");
      const game = body?.game ?? "GT7";  // RyGTServers from before this field are GT7
      if (!Number.isInteger(port) || port < 1 || port > 65535 || !name || !/^[0-9a-fA-F]{12}$/.test(token) || !["GT7", "GTS"].includes(game))
        return json({ error: "bad announcement" }, 400);
      const key = `${address}:${port}`;
      if (!this.servers.has(key) && this.servers.size >= MAX_SERVERS) return json({ error: "list full" }, 503);
      this.servers.set(key, { address, port, name, token, game, seen: now });
      return json({ ok: true });
    }
    return json({ error: "not found" }, 404);
  }
}
