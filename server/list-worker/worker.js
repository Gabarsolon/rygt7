// The public server list. RyGTServers that chose to be listed POST /announce every minute; the
// launcher reads GET /servers. A server's address is the one the announcement came from, so a
// host can only list itself. Entries are kept in memory: hosts announce again after a restart.
const EXPIRES_MS = 3 * 60 * 1000;
const MAX_SERVERS = 200;

const json = (value, status = 200) =>
  new Response(JSON.stringify(value), { status, headers: { "content-type": "application/json" } });

export default {
  fetch(request, env) {
    const list = env.LIST.get(env.LIST.idFromName("all"));
    return list.fetch(request);
  },
};

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
