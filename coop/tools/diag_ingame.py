"""Live in-game entity/NPC/weapon diagnostic tool for GTA 5 Story Co-op (queries both PCs via relay)."""
import json
import os
import socket
import time

CFG_PATH = os.path.join(os.path.dirname(os.path.dirname(__file__)), "config.json")
with open(CFG_PATH, "r", encoding="utf-8") as f:
    cfg = json.load(f)

srv = (cfg["server"], int(cfg.get("port", 27530)))
room = cfg.get("room", "Public")

s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
s.settimeout(0.4)
# Register in room and request diagnostics from all peers
s.sendto(json.dumps({"t": "ping", "id": 1, "room": room, "name": "diag_tool", "role": "Observer"}).encode(), srv)
time.sleep(0.05)
s.sendto(json.dumps({"t": "diag_req", "room": room, "name": "diag_tool", "role": "Observer"}).encode(), srv)

t0 = time.time()
results = {}
while time.time() - t0 < 2.2 and len(results) < 2:
    try:
        data, _ = s.recvfrom(8192)
        msg = json.loads(data.decode("utf-8"))
        if msg.get("t") == "diag_res":
            results[msg.get("name", "?")] = msg.get("d", "")
    except Exception:
        pass

if not results:
    print("No DIAG_RES received from any PC.")
else:
    for pc in sorted(results.keys()):
        print(f"=== {pc} ===")
        for item in results[pc].split(";"):
            print("  " + item)
