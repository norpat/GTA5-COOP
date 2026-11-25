"""GTA 5 Story Co-op Relay & Lobby Server v0.9.2 — Dual-stack UDP + HTTP on port 27530.
Standard library only (Python 3).
Supports:
- Fast UDP relay (<1 ms on LAN/VPN)
- HTTP/1.1 JSON tunnel endpoint (/gta5) compatible with Cloudflare Tunnel alongside Zelda OoT Coop Mod
- Lobby Room Create, Join, Password lock, and live Connected Players roster
"""
from collections import deque
import http.server
import json
import socket
import sys
import threading
import time

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 27530
TIMEOUT_S = 15.0
ROOM_TTL_S = 300.0

RELAY_TYPES = {
    "state", "chat", "npc_dead", "pvp_hit", "host_inv", "host_world", "host_cutscene",
    "host_npcs", "host_cam", "host_props", "host_vehs", "wanted_req",
    "diag_req", "diag_res", "vdmg", "melee",
}
COALESCE_TYPES = {"state", "host_world", "host_inv", "host_props"}


class CoopRelayServer:
    def __init__(self, udp_sock: socket.socket):
        self.udp_sock = udp_sock
        self.lock = threading.Lock()
        # client_key -> {"name", "role", "room", "model", "seen", "udp_addr", "inbox", "coalesced", "wl"}
        self.clients = {}
        # room_name -> {"password": str, "creator": str, "created": float, "touched": float}
        self.rooms = {
            "Public": {"password": "", "creator": "Server", "created": time.monotonic(), "touched": time.monotonic()}
        }
        self.last_clean = 0.0

    def cleanup(self, now: float):
        if now - self.last_clean < 3.0:
            return
        self.last_clean = now
        dead = [k for k, c in self.clients.items() if now - c["seen"] > TIMEOUT_S]
        for k in dead:
            c = self.clients.pop(k, None)
            if c:
                print(f"[{time.strftime('%H:%M:%S')}] Timeout: {c['name']} (room={c['room']})", flush=True)
        active_rooms = {c["room"] for c in self.clients.values()}
        stale_rooms = [
            r for r, meta in self.rooms.items()
            if r != "Public" and r not in active_rooms and (now - meta["touched"] > ROOM_TTL_S)
        ]
        for r in stale_rooms:
            self.rooms.pop(r, None)

    def _rooms_snapshot(self, now: float):
        counts = {}
        hosts = {}
        for c in self.clients.values():
            if now - c["seen"] <= TIMEOUT_S and c["role"] != "Observer":
                rm = c["room"]
                counts[rm] = counts.get(rm, 0) + 1
                if c["role"] in ("Host", "Vert"):
                    hosts[rm] = c["name"]
        all_names = ["Public"] + sorted(r for r in (set(self.rooms.keys()) | set(counts.keys())) if r != "Public")
        out = []
        for r in all_names:
            meta = self.rooms.get(r, {"password": "", "creator": ""})
            out.append({
                "name": r,
                "count": counts.get(r, 0),
                "host": hosts.get(r, meta.get("creator", "")),
                "locked": bool(meta.get("password")),
            })
        return out

    def _online_snapshot(self, now: float):
        rows = []
        for c in sorted(self.clients.values(), key=lambda x: (x["room"] != "Public", x["room"], x["role"] != "Host", x["name"].lower())):
            if now - c["seen"] <= TIMEOUT_S and c["role"] != "Observer":
                rows.append({
                    "name": c["name"],
                    "role": c["role"],
                    "room": c["room"],
                    "model": c["model"],
                    "wl": c.get("wl", 0),
                })
        return rows

    def _peers_in_room(self, room: str, now: float):
        return [
            {"name": c["name"], "role": c["role"], "model": c["model"], "wl": c.get("wl", 0)}
            for c in self.clients.values()
            if c["room"] == room and now - c["seen"] <= TIMEOUT_S and c["role"] != "Observer"
        ]

    def handle_packet(self, msg: dict, client_key: str, udp_addr=None) -> dict:
        now = time.monotonic()
        with self.lock:
            self.cleanup(now)
            t = str(msg.get("t") or "")
            name = str(msg.get("name") or "Player").strip()[:24] or "Player"
            role = str(msg.get("role") or "Guest").strip()[:12]
            model = str(msg.get("model") or "Lamar").strip()[:32]
            req_room = str(msg.get("room") or "Public").strip()[:32] or "Public"
            pwd = str(msg.get("password") or "")[:32]

            if t == "bye":
                old = self.clients.pop(client_key, None)
                if old:
                    print(f"[{time.strftime('%H:%M:%S')}] Disconnected: {old['name']} (room={old['room']})", flush=True)
                return {"t": "bye_ack", "ok": True}

            c = self.clients.get(client_key)
            is_new = c is None
            if is_new:
                # Evict stale ephemeral socket entries for the same player name when GUI restarts
                if role != "Observer":
                    for ok_key in [k for k, oc in self.clients.items() if oc["name"] == name and k != client_key]:
                        self.clients.pop(ok_key, None)
                c = {
                    "name": name,
                    "role": role,
                    "room": "Public",
                    "model": model,
                    "seen": now,
                    "udp_addr": udp_addr,
                    "inbox": deque(maxlen=96),
                    "coalesced": {},
                    "wl": 0,
                }
                self.clients[client_key] = c

            c["name"] = name
            c["role"] = role
            c["model"] = model
            c["seen"] = now
            if udp_addr is not None:
                c["udp_addr"] = udp_addr
                c["udp_seen"] = now
            if t == "host_world" and "wl" in msg:
                try:
                    c["wl"] = int(msg["wl"])
                except (ValueError, TypeError):
                    pass

            err = ""
            if t == "create_room":
                new_room = req_room
                if new_room != "Public":
                    existing = self.rooms.get(new_room)
                    active_in_room = any(
                        oc["room"] == new_room and now - oc["seen"] <= TIMEOUT_S
                        for oc in self.clients.values() if oc is not c
                    )
                    if existing and active_in_room and existing.get("password") and existing.get("password") != pwd:
                        err = "Room already exists and is password-protected."
                    else:
                        self.rooms[new_room] = {
                            "password": pwd,
                            "creator": name,
                            "created": now,
                            "touched": now,
                        }
                        c["room"] = new_room
                        print(f"[{time.strftime('%H:%M:%S')}] Room created: '{new_room}' by {name}", flush=True)
                else:
                    c["room"] = "Public"
            elif t == "join_room":
                meta = self.rooms.get(req_room)
                if meta and meta.get("password") and meta.get("password") != pwd:
                    err = "Incorrect room password."
                else:
                    if req_room not in self.rooms:
                        self.rooms[req_room] = {"password": "", "creator": name, "created": now, "touched": now}
                    else:
                        self.rooms[req_room]["touched"] = now
                    c["room"] = req_room
            else:
                # Normal packet: allow switching room unless locked with mismatching password
                meta = self.rooms.get(req_room)
                if meta and meta.get("password") and meta.get("password") != pwd and c["room"] != req_room:
                    err = "Room is password-protected."
                else:
                    if req_room not in self.rooms:
                        self.rooms[req_room] = {"password": "", "creator": name, "created": now, "touched": now}
                    else:
                        self.rooms[req_room]["touched"] = now
                    c["room"] = req_room

            if is_new and role != "Observer":
                print(f"[{time.strftime('%H:%M:%S')}] Connected: {name} ({role}, room={c['room']}, via={'UDP' if udp_addr else 'HTTP'})", flush=True)

            room = c["room"]
            if t in RELAY_TYPES and not err:
                raw_out = json.dumps(msg).encode("utf-8")
                for other_key, oc in list(self.clients.items()):
                    if other_key == client_key or oc["room"] != room or (now - oc["seen"] > TIMEOUT_S):
                        continue
                    if oc["udp_addr"] is not None and now - oc.get("udp_seen", 0.0) < 3.0:   # v0.9.2: bruker UDP nå
                        try:
                            self.udp_sock.sendto(raw_out, oc["udp_addr"])
                        except OSError:
                            pass
                    else:
                        if t in COALESCE_TYPES:
                            oc["coalesced"][f"{t}:{name}"] = msg
                        else:
                            oc["inbox"].append(msg)

            if t in ("ping", "poll", "create_room", "join_room") or udp_addr is None:
                pong = {
                    "t": "pong",
                    "v": "0.9.2",
                    "id": msg.get("id"),
                    "room": room,
                    "peers": self._peers_in_room(room, now),
                    "online": self._online_snapshot(now),
                    "rooms": self._rooms_snapshot(now),
                    "error": err,
                }
                if udp_addr is None:
                    queued = list(c["coalesced"].values()) + list(c["inbox"])
                    c["coalesced"].clear()
                    c["inbox"].clear()
                    return {"t": "bundle", "pong": pong, "msgs": queued}
                return pong
            return {}


def make_http_handler(relay: CoopRelayServer):
    class Gta5HttpHandler(http.server.BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, fmt, *args):
            return

        def _send_json(self, payload: dict, status: int = 200):
            body = json.dumps(payload).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Connection", "keep-alive")
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self):
            now = time.monotonic()
            with relay.lock:
                relay.cleanup(now)
                info = {
                    "ok": True,
                    "service": "gta5-coop-server",
                    "version": "0.9.2",
                    "port": PORT,
                    "rooms": relay._rooms_snapshot(now),
                    "online": relay._online_snapshot(now),
                }
            self._send_json(info)

        def do_POST(self):
            try:
                length = int(self.headers.get("Content-Length") or 0)
            except ValueError:
                length = 0
            if not (0 < length <= 65535):
                self._send_json({"t": "error", "error": "Invalid payload size"}, 400)
                return
            try:
                raw = self.rfile.read(length)
                data = json.loads(raw.decode("utf-8"))
                fwd = self.headers.get("CF-Connecting-IP") or self.headers.get("X-Forwarded-For") or self.client_address[0]
                ip = fwd.split(",")[0].strip()
                cid = str(data.get("cid") or f"http:{ip}:{data.get('name', 'Player')}")[:64]
                if isinstance(data.get("batch"), list):
                    last_resp = {}
                    for item in data["batch"][:40]:
                        if isinstance(item, dict):
                            item.setdefault("name", data.get("name"))
                            item.setdefault("role", data.get("role"))
                            item.setdefault("room", data.get("room"))
                            item.setdefault("model", data.get("model"))
                            item.setdefault("password", data.get("password", ""))
                            last_resp = relay.handle_packet(item, cid, udp_addr=None)
                    self._send_json(last_resp or {"t": "bundle", "msgs": []})
                else:
                    resp = relay.handle_packet(data, cid, udp_addr=None)
                    self._send_json(resp or {"t": "ok"})
            except Exception as exc:
                self._send_json({"t": "error", "error": str(exc)}, 400)

    return Gta5HttpHandler


def main():
    udp_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    udp_sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    udp_sock.bind(("0.0.0.0", PORT))
    udp_sock.settimeout(0.5)

    relay = CoopRelayServer(udp_sock)
    http_srv = http.server.ThreadingHTTPServer(("0.0.0.0", PORT), make_http_handler(relay))
    threading.Thread(target=http_srv.serve_forever, daemon=True, name="Gta5HttpServer").start()

    print(f"GTA 5 Story Co-op Server v0.9.2 listening on UDP & HTTP 0.0.0.0:{PORT}", flush=True)

    while True:
        try:
            data, addr = udp_sock.recvfrom(65535)
        except (socket.timeout, ConnectionResetError):
            with relay.lock:
                relay.cleanup(time.monotonic())
            continue
        except OSError:
            continue

        try:
            msg = json.loads(data.decode("utf-8"))
        except ValueError:
            continue

        cid = msg.get("cid") if isinstance(msg, dict) else None
        client_key = str(cid)[:64] if cid else f"udp:{addr[0]}:{addr[1]}"   # v0.9.2: samme nøkkel som HTTP
        reply = relay.handle_packet(msg, client_key, udp_addr=addr)
        if reply:
            try:
                udp_sock.sendto(json.dumps(reply).encode("utf-8"), addr)
            except OSError:
                pass


if __name__ == "__main__":
    main()
