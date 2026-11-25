"""GTA 5 Story Co-op v0.9.1 — Modern Centered Mod GUI, Lobby Browser & Dual-Stack Relay Client (UDP + HTTPS Tunnel)."""
from collections import deque
import http.client
import json
import math
import os
import socket
import ssl
import threading
import time
import tkinter as tk
from tkinter import ttk
import urllib.parse
import urllib.request

VERSION = "0.9.2"
GAME_PORT_OUT = 27510   # GUI -> GTA5 (CoopBridge.dll)
GAME_PORT_IN = 27511    # GTA5 -> GUI
DEFAULT_SERVER_PORT = 27530

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
LOG_DIR = os.path.join(BASE_DIR, "logs")
os.makedirs(LOG_DIR, exist_ok=True)
PC_NAME = socket.gethostname()
LOG_PATH = os.path.join(LOG_DIR, f"{PC_NAME}.log")
CFG_PATH = os.path.join(BASE_DIR, "config.json")

GUEST_MODELS = {
    "Lamar": "ig_lamardavis",
    "Franklin": "player_one",
    "Michael": "player_zero",
    "Trevor": "player_two",
    "Lester": "ig_lestercrest",
    "Jimmy": "ig_jimmydisanto",
    "Tracey": "ig_tracydisanto",
    "SWAT": "s_m_y_swat_01",
    "Police": "s_m_y_cop_01",
    "Bodyguard": "s_m_m_highsec_01",
}

# Modern Neon-Dark GTA V Palette
BG = "#0d1017"
CARD_BG = "#151a24"
CARD_BORDER = "#252f42"
CARD_BORDER_GLOW = "#2e8b57"
FIELD_BG = "#0f131c"
FG = "#edf2f7"
MUTED = "#8b98ad"
ACCENT = "#2ecc71"
ACCENT_HOVER = "#3ee082"
GOLD = "#f5b942"
BLUE = "#3b82f6"
BLUE_HOVER = "#5496ff"
DANGER = "#ef4444"
DANGER_HOVER = "#f87171"
SLATE_BTN = "#242c3d"
SLATE_HOVER = "#323d54"


def write_log(msg: str):
    ts = time.strftime("%Y-%m-%d %H:%M:%S")
    line = f"{ts} [{PC_NAME}] v{VERSION} {msg}"
    try:
        if os.path.exists(LOG_PATH) and os.path.getsize(LOG_PATH) > (10 << 20):
            os.replace(LOG_PATH, LOG_PATH + ".1")
        with open(LOG_PATH, "a", encoding="utf-8") as f:
            f.write(line + "\n")
    except OSError:
        pass
    return line


def load_cfg() -> dict:
    try:
        with open(CFG_PATH, "r", encoding="utf-8-sig") as f:
            return json.load(f)
    except (OSError, ValueError):
        return {}


def save_cfg(cfg: dict):
    try:
        with open(CFG_PATH, "w", encoding="utf-8") as f:
            json.dump(cfg, f, indent=2, ensure_ascii=False)
    except OSError:
        pass


def normalize_role(r: str) -> str:
    if r in ("Host", "Vert"):
        return "Host"
    return "Guest"



def install_guest_save(log):
    r"""v0.9.2: Gjest: kopier lagringen i coop\saves\guest\ (f.eks. en 100 %-lagring) inn som gjestens autolagring
    (SGTA50015) i GTA V-profilen (Documents\Rockstar Games\GTA V\Profiles\<id>), slik at gjesten aldri starter egne story-oppdrag. Bare før GTA V kjører.
    Gjestens gamle lagring tas vare på i coop\saves\backup_<tid>\."""
    import glob
    import hashlib
    import shutil
    import subprocess
    try:
        src_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "saves", "guest")
        srcs = sorted(glob.glob(os.path.join(src_dir, "SGTA5*")))
        srcs = [p for p in srcs if not p.lower().endswith(".bak")]
        if not srcs:
            log(r"Guest save: no save in coop\saves\guest - skipped.")
            return
        src = srcs[0]
        running = b"GTA5.exe" in subprocess.run(["tasklist", "/FI", "IMAGENAME eq GTA5.exe"],
                                                 capture_output=True, creationflags=0x08000000).stdout
        if running:
            log("Guest save: GTA V is already running - start the GUI before the game to install it.")
            return
        root = os.path.join(os.path.expanduser("~"), "Documents", "Rockstar Games", "GTA V", "Profiles")
        profiles = [d for d in glob.glob(os.path.join(root, "*")) if os.path.isdir(d)]
        if not profiles:
            log("Guest save: no GTA V profile folder found yet (start GTA V once first).")
            return
        want = hashlib.sha1(open(src, "rb").read()).hexdigest()
        for prof in profiles:
            dst = os.path.join(prof, "SGTA50015")
            if os.path.isfile(dst) and hashlib.sha1(open(dst, "rb").read()).hexdigest() == want:
                continue
            backup = os.path.join(os.path.dirname(src_dir), f"backup_{time.strftime('%Y%m%d_%H%M%S')}", os.path.basename(prof))
            os.makedirs(backup, exist_ok=True)
            for old_f in glob.glob(os.path.join(prof, "SGTA5*")):
                shutil.copy2(old_f, backup)
            shutil.copy2(src, dst)
            shutil.copy2(src, dst + ".bak")
            log(f"Guest save installed: {os.path.basename(src)} -> {prof} (old saves in {backup})")
    except Exception as e:  # noqa: BLE001
        log(f"Guest save: failed ({e})")


class FancyButton(tk.Label):
    """Flat modern button with smooth hover glow, click feedback, and centered text."""
    def __init__(self, parent, text, command, bg=ACCENT, hover_bg=ACCENT_HOVER, fg="#0b0f14",
                 width=16, font=("Segoe UI Semibold", 9), pady=6):
        super().__init__(
            parent, text=text, bg=bg, fg=fg, font=font, width=width,
            padx=10, pady=pady, cursor="hand2", justify="center", anchor="center",
            highlightthickness=1, highlightbackground=CARD_BORDER
        )
        self._cmd = command
        self._bg = bg
        self._hover = hover_bg
        self._fg = fg
        self.bind("<Enter>", self._on_enter)
        self.bind("<Leave>", self._on_leave)
        self.bind("<ButtonRelease-1>", self._on_click)

    def set_style(self, text=None, bg=None, hover_bg=None, fg=None):
        if text is not None:
            self.config(text=text)
        if bg is not None:
            self._bg = bg
            self.config(bg=bg)
        if hover_bg is not None:
            self._hover = hover_bg
        if fg is not None:
            self._fg = fg
            self.config(fg=fg)

    def _on_enter(self, _e):
        self.config(bg=self._hover, highlightbackground=GOLD)

    def _on_leave(self, _e):
        self.config(bg=self._bg, highlightbackground=CARD_BORDER)

    def _on_click(self, _e):
        if callable(self._cmd):
            self._cmd()


def make_card(parent, title: str, subtitle: str = ""):
    outer = tk.Frame(parent, bg=BG)
    outer.pack(fill="x", padx=16, pady=5)
    frame = tk.Frame(outer, bg=CARD_BG, highlightthickness=1, highlightbackground=CARD_BORDER, padx=14, pady=9)
    frame.pack(fill="x")
    tk.Label(
        frame, text=title.upper(), fg=GOLD, bg=CARD_BG,
        font=("Segoe UI", 9, "bold"), justify="center", anchor="center"
    ).pack(fill="x", pady=(0, 2 if subtitle else 5))
    if subtitle:
        tk.Label(
            frame, text=subtitle, fg=MUTED, bg=CARD_BG,
            font=("Segoe UI", 8), justify="center", anchor="center"
        ).pack(fill="x", pady=(0, 5))
    return outer, frame


class CoopApp:
    def __init__(self, root: tk.Tk):
        self.root = root
        self.root.title(f"GTA V Story Co-op v{VERSION}")
        self.root.geometry("720x850")
        self.root.minsize(660, 760)
        self.root.configure(bg=BG)
        self.root.protocol("WM_DELETE_WINDOW", self.on_close)

        self.cfg = load_cfg()
        self.running = True
        self.connected = False
        self.active_transport = "none"  # "udp" or "http"
        self.net_sock = None
        self.server_addr = None
        self.client_id = f"{PC_NAME}-{int(time.time()) % 100000}"

        self.peer_name = ""
        self.peer_model = ""
        self.peer_state = None
        self.peer_last_rx = 0.0
        self.server_last_rx = 0.0
        self.udp_last_rx = 0.0
        self.connect_started_at = 0.0

        self.room_peers = []
        self.online_players = []
        self.rooms_list = []
        self._room_names_index = []
        self.ping_ms = None
        self._ping_sent = {}
        self._self_model_applied = None
        self._wanted_stars = 0
        self._anim_phase = 0.0

        self._http_out_batch = deque(maxlen=64)
        self._http_lock = threading.Lock()

        # Localhost UDP bridge to CoopBridge.dll
        self.game_rx = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.game_rx.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try:
            self.game_rx.bind(("127.0.0.1", GAME_PORT_IN))
        except OSError as e:
            write_log(f"Failed to bind game_rx {GAME_PORT_IN}: {e}")
        self.game_rx.settimeout(0.2)
        self.game_tx = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

        self.game_seen = 0.0
        self.my_state = {
            "x": 0.0, "y": 0.0, "z": 0.0, "h": 0.0, "cut": 0, "hp": 200,
            "spd": 0.0, "wp": 0, "outfit": "", "aim": 0, "shoot": 0,
            "ax": 0.0, "ay": 0.0, "az": 0.0,
        }

        self._configure_ttk()
        self._build_ui()
        write_log(f"Mod GUI v{VERSION} started.")
        if self.role_var.get() == "Guest":
            self._install_guest_save()

        threading.Thread(target=self._game_rx_loop, daemon=True).start()
        threading.Thread(target=self._net_tx_loop, daemon=True).start()
        threading.Thread(target=self._http_tunnel_loop, daemon=True).start()
        threading.Thread(target=self._trigger_watch_loop, daemon=True).start()
        self.root.after(80, self._anim_tick)
        self.root.after(250, self._ui_tick)

        if self.ip_var.get().strip() or self.tunnel_var.get().strip():
            self.root.after(450, self.toggle_network)

    def _install_guest_save(self):
        install_guest_save(self.ui_log)

    def _configure_ttk(self):
        style = ttk.Style(self.root)
        try:
            style.theme_use("clam")
        except tk.TclError:
            pass
        style.configure(
            "Dark.TCombobox",
            fieldbackground=FIELD_BG,
            background=SLATE_BTN,
            foreground=FG,
            arrowcolor=GOLD,
            bordercolor=CARD_BORDER,
            lightcolor=CARD_BORDER,
            darkcolor=CARD_BORDER,
        )
        style.map(
            "Dark.TCombobox",
            fieldbackground=[("readonly", FIELD_BG), ("disabled", "#12151e")],
            foreground=[("readonly", FG), ("disabled", "#556070")],
            selectbackground=[("readonly", FIELD_BG)],
            selectforeground=[("readonly", FG)],
        )

    def _build_ui(self):
        # 1. Animated Neon Banner Header (Centered)
        self.banner = tk.Canvas(self.root, height=76, bg="#0a0d13", highlightthickness=0)
        self.banner.pack(fill="x")
        self._glow_line_bg = self.banner.create_line(0, 74, 2000, 74, fill="#1e293b", width=2)
        self._glow_bar = self.banner.create_line(0, 74, 180, 74, fill=ACCENT, width=3)
        self._orb_halo = self.banner.create_oval(20, 20, 44, 44, fill="#16231c", outline="")
        self._orb_dot = self.banner.create_oval(26, 26, 38, 38, fill="#64748b", outline="")
        self._title_id = self.banner.create_text(
            360, 27, text=f"GTA V  STORY CO-OP   ·   v{VERSION}",
            fill=FG, font=("Segoe UI", 16, "bold"), anchor="center"
        )
        self._sub_id = self.banner.create_text(
            360, 52, text="",
            fill=GOLD, font=("Segoe UI", 8, "bold"), anchor="center"
        )
        self.banner.bind("<Configure>", self._on_banner_resize)

        # 2. Telemetry Pill Bar (Centered)
        tele_wrap = tk.Frame(self.root, bg=BG)
        tele_wrap.pack(fill="x", padx=16, pady=(8, 3))
        tele_inner = tk.Frame(tele_wrap, bg=BG)
        tele_inner.pack(anchor="center")

        self.lbl_net_pill = tk.Label(
            tele_inner, text="● SERVER: OFFLINE", fg=MUTED, bg=CARD_BG,
            font=("Segoe UI Semibold", 9), padx=14, pady=5, justify="center",
            highlightthickness=1, highlightbackground=CARD_BORDER
        )
        self.lbl_net_pill.pack(side="left", padx=5)

        self.lbl_game_pill = tk.Label(
            tele_inner, text="● GTA V: WAITING", fg=MUTED, bg=CARD_BG,
            font=("Segoe UI Semibold", 9), padx=14, pady=5, justify="center",
            highlightthickness=1, highlightbackground=CARD_BORDER
        )
        self.lbl_game_pill.pack(side="left", padx=5)

        self.lbl_world_pill = tk.Label(
            tele_inner, text="WANTED: ☆☆☆☆☆", fg=GOLD, bg=CARD_BG,
            font=("Segoe UI Semibold", 9), padx=14, pady=5, justify="center",
            highlightthickness=1, highlightbackground=CARD_BORDER
        )
        self.lbl_world_pill.pack(side="left", padx=5)

        # Variables (loaded from config / env — zero hardcoded personal endpoints)
        default_srv = os.getenv("GTA5_COOP_SERVER", self.cfg.get("server", ""))
        default_tun = os.getenv("GTA5_COOP_TUNNEL_URL", self.cfg.get("tunnel_url", ""))
        self.role_var = tk.StringVar(value=normalize_role(self.cfg.get("role", "Host")))
        self.name_var = tk.StringVar(value=self.cfg.get("name", PC_NAME))
        self.ip_var = tk.StringVar(value=default_srv)
        self.port_var = tk.StringVar(value=str(self.cfg.get("port", DEFAULT_SERVER_PORT)))
        self.tunnel_var = tk.StringVar(value=default_tun)
        self.transport_var = tk.StringVar(value=self.cfg.get("transport", "Auto (LAN + Tunnel)"))
        self.room_var = tk.StringVar(value=self.cfg.get("room", "Public"))
        self.room_pwd_var = tk.StringVar(value=self.cfg.get("room_password", ""))
        self.new_room_var = tk.StringVar(value="")
        self.model_var = tk.StringVar(value=self.cfg.get("model", "Lamar"))

        # 3. Setup & Connection Card (100% Centered)
        _, c_setup = make_card(self.root, "Player & Server Setup")

        row_a = tk.Frame(c_setup, bg=CARD_BG)
        row_a.pack(anchor="center", pady=3)
        tk.Label(row_a, text="Role", fg=MUTED, bg=CARD_BG, font=("Segoe UI", 9, "bold")).pack(side="left", padx=(4, 5))
        self.cb_role = ttk.Combobox(
            row_a, textvariable=self.role_var, values=["Host", "Guest"],
            width=9, state="readonly", style="Dark.TCombobox", justify="center"
        )
        self.cb_role.pack(side="left", padx=(0, 14))
        self.cb_role.bind("<<ComboboxSelected>>", lambda _e: self._on_role_change())

        tk.Label(row_a, text="Player Name", fg=MUTED, bg=CARD_BG, font=("Segoe UI", 9, "bold")).pack(side="left", padx=(0, 5))
        tk.Entry(
            row_a, textvariable=self.name_var, width=14, bg=FIELD_BG, fg=FG,
            insertbackground=FG, justify="center", relief="flat",
            highlightthickness=1, highlightbackground=CARD_BORDER
        ).pack(side="left", padx=(0, 14), ipady=3)

        self.lbl_guest_char = tk.Label(row_a, text="Guest Avatar", fg=MUTED, bg=CARD_BG, font=("Segoe UI", 9, "bold"))
        self.lbl_guest_char.pack(side="left", padx=(0, 5))
        self.cb_model = ttk.Combobox(
            row_a, textvariable=self.model_var, values=list(GUEST_MODELS.keys()),
            width=12, state="readonly", style="Dark.TCombobox", justify="center"
        )
        self.cb_model.pack(side="left")
        self.cb_model.bind("<<ComboboxSelected>>", lambda _e: self._on_model_change())

        row_b = tk.Frame(c_setup, bg=CARD_BG)
        row_b.pack(anchor="center", pady=3)
        tk.Label(row_b, text="Server IP / Host", fg=MUTED, bg=CARD_BG, font=("Segoe UI", 9, "bold")).pack(side="left", padx=(4, 5))
        tk.Entry(
            row_b, textvariable=self.ip_var, width=16, bg=FIELD_BG, fg=FG,
            insertbackground=FG, justify="center", relief="flat",
            highlightthickness=1, highlightbackground=CARD_BORDER
        ).pack(side="left", padx=(0, 10), ipady=3)

        tk.Label(row_b, text="Port", fg=MUTED, bg=CARD_BG, font=("Segoe UI", 9, "bold")).pack(side="left", padx=(0, 5))
        tk.Entry(
            row_b, textvariable=self.port_var, width=6, bg=FIELD_BG, fg=FG,
            insertbackground=FG, justify="center", relief="flat",
            highlightthickness=1, highlightbackground=CARD_BORDER
        ).pack(side="left", padx=(0, 12), ipady=3)

        tk.Label(row_b, text="Mode", fg=MUTED, bg=CARD_BG, font=("Segoe UI", 9, "bold")).pack(side="left", padx=(0, 5))
        self.cb_transport = ttk.Combobox(
            row_b, textvariable=self.transport_var,
            values=["Auto (LAN + Tunnel)", "Direct UDP", "HTTPS Tunnel"],
            width=18, state="readonly", style="Dark.TCombobox", justify="center"
        )
        self.cb_transport.pack(side="left")

        row_c = tk.Frame(c_setup, bg=CARD_BG)
        row_c.pack(anchor="center", pady=3)
        tk.Label(row_c, text="Tunnel URL (OoT Integration)", fg=MUTED, bg=CARD_BG, font=("Segoe UI", 8, "bold")).pack(side="left", padx=(4, 6))
        tk.Entry(
            row_c, textvariable=self.tunnel_var, width=38, bg=FIELD_BG, fg=FG,
            insertbackground=FG, justify="center", relief="flat",
            highlightthickness=1, highlightbackground=CARD_BORDER
        ).pack(side="left", ipady=2)

        # Action Buttons Row (Centered — Teleport to Host ONLY shown when Role == Guest!)
        self.btn_row = tk.Frame(c_setup, bg=CARD_BG)
        self.btn_row.pack(anchor="center", pady=(8, 2))
        self.btn_conn = FancyButton(
            self.btn_row, text="CONNECT SERVER", command=self.toggle_network,
            bg=ACCENT, hover_bg=ACCENT_HOVER, fg="#08120c", width=20
        )
        self.btn_conn.pack(side="left", padx=6)

        self.btn_tp = FancyButton(
            self.btn_row, text="TELEPORT TO HOST", command=self.tp_to_host,
            bg=BLUE, hover_bg=BLUE_HOVER, fg="#ffffff", width=20
        )

        self.lbl_pos = tk.Label(
            c_setup, text="Position: Waiting for GTA V Story Mode...",
            fg=MUTED, bg=CARD_BG, font=("Consolas", 8), justify="center", anchor="center"
        )
        self.lbl_pos.pack(fill="x", pady=(5, 0))

        # 4. Dual Middle Section: Lobby Rooms (Create & Join) + Connected Players (like Zelda OoT Coop Mod!)
        mid_wrap = tk.Frame(self.root, bg=BG)
        mid_wrap.pack(fill="x", padx=16, pady=5)
        mid_wrap.columnconfigure(0, weight=1)
        mid_wrap.columnconfigure(1, weight=1)

        # Left Card: Lobby Room Create & Join
        c_lobby = tk.Frame(mid_wrap, bg=CARD_BG, highlightthickness=1, highlightbackground=CARD_BORDER, padx=12, pady=9)
        c_lobby.grid(row=0, column=0, sticky="nsew", padx=(0, 5))
        tk.Label(
            c_lobby, text="CO-OP LOBBY ROOMS", fg=GOLD, bg=CARD_BG,
            font=("Segoe UI", 9, "bold"), justify="center"
        ).pack(fill="x")
        self.lbl_active_room = tk.Label(
            c_lobby, text=f"Active Room: {self.room_var.get()}", fg=ACCENT, bg=CARD_BG,
            font=("Segoe UI Semibold", 9), justify="center"
        )
        self.lbl_active_room.pack(fill="x", pady=(2, 4))

        lb_frame = tk.Frame(c_lobby, bg=CARD_BG)
        lb_frame.pack(fill="x", pady=2)
        self.rooms_listbox = tk.Listbox(
            lb_frame, height=4, bg=FIELD_BG, fg=FG, selectbackground="#1e3a5f",
            selectforeground="#ffffff", highlightthickness=1, highlightbackground=CARD_BORDER,
            borderwidth=0, font=("Segoe UI", 9), justify="center", cursor="hand2", exportselection=False
        )
        self.rooms_listbox.pack(fill="x", expand=True)
        self.rooms_listbox.bind("<Double-Button-1>", lambda _e: self.join_selected_room())

        r_inputs = tk.Frame(c_lobby, bg=CARD_BG)
        r_inputs.pack(anchor="center", pady=(6, 3))
        tk.Label(r_inputs, text="Room", fg=MUTED, bg=CARD_BG, font=("Segoe UI", 8, "bold")).pack(side="left", padx=(0, 4))
        tk.Entry(
            r_inputs, textvariable=self.new_room_var, width=12, bg=FIELD_BG, fg=FG,
            insertbackground=FG, justify="center", relief="flat",
            highlightthickness=1, highlightbackground=CARD_BORDER
        ).pack(side="left", padx=(0, 8), ipady=2)
        tk.Label(r_inputs, text="Pass", fg=MUTED, bg=CARD_BG, font=("Segoe UI", 8, "bold")).pack(side="left", padx=(0, 4))
        tk.Entry(
            r_inputs, textvariable=self.room_pwd_var, width=9, show="•", bg=FIELD_BG, fg=FG,
            insertbackground=FG, justify="center", relief="flat",
            highlightthickness=1, highlightbackground=CARD_BORDER
        ).pack(side="left", ipady=2)

        r_btns = tk.Frame(c_lobby, bg=CARD_BG)
        r_btns.pack(anchor="center", pady=(4, 0))
        FancyButton(
            r_btns, text="CREATE ROOM", command=self.create_room_action,
            bg=ACCENT, hover_bg=ACCENT_HOVER, fg="#08120c", width=13, font=("Segoe UI Semibold", 8), pady=4
        ).pack(side="left", padx=4)
        FancyButton(
            r_btns, text="JOIN SELECTED", command=self.join_selected_room,
            bg=SLATE_BTN, hover_bg=SLATE_HOVER, fg=FG, width=13, font=("Segoe UI Semibold", 8), pady=4
        ).pack(side="left", padx=4)

        # Right Card: Connected Players Roster (like Zelda OoT Coop Mod!)
        c_conn = tk.Frame(mid_wrap, bg=CARD_BG, highlightthickness=1, highlightbackground=CARD_BORDER, padx=12, pady=9)
        c_conn.grid(row=0, column=1, sticky="nsew", padx=(5, 0))
        self.lbl_conn_header = tk.Label(
            c_conn, text="CONNECTED PLAYERS (0)", fg=GOLD, bg=CARD_BG,
            font=("Segoe UI", 9, "bold"), justify="center"
        )
        self.lbl_conn_header.pack(fill="x")
        self.lbl_conn_sub = tk.Label(
            c_conn, text="Live Server Roster", fg=MUTED, bg=CARD_BG,
            font=("Segoe UI", 8), justify="center"
        )
        self.lbl_conn_sub.pack(fill="x", pady=(2, 4))

        pl_frame = tk.Frame(c_conn, bg=CARD_BG)
        pl_frame.pack(fill="both", expand=True, pady=2)
        self.connected_listbox = tk.Listbox(
            pl_frame, height=6, bg=FIELD_BG, fg=FG, selectbackground="#1e3a5f",
            selectforeground="#ffffff", highlightthickness=1, highlightbackground=CARD_BORDER,
            borderwidth=0, font=("Segoe UI", 9), justify="center", exportselection=False
        )
        sb_pl = ttk.Scrollbar(pl_frame, orient="vertical", command=self.connected_listbox.yview)
        self.connected_listbox.config(yscrollcommand=sb_pl.set)
        sb_pl.pack(side="right", fill="y")
        self.connected_listbox.pack(side="left", fill="both", expand=True)

        # 5. Lobby Chat Card (Centered)
        _, c_chat = make_card(self.root, "Lobby Chat")
        self.chat_box = tk.Text(
            c_chat, height=5, bg=FIELD_BG, fg=FG, state="disabled", wrap="word",
            font=("Segoe UI", 9), relief="flat", highlightthickness=1, highlightbackground=CARD_BORDER
        )
        self.chat_box.tag_configure("center", justify="center")
        self.chat_box.pack(fill="x")
        cr = tk.Frame(c_chat, bg=CARD_BG)
        cr.pack(anchor="center", pady=(6, 0))
        self.chat_entry = tk.Entry(
            cr, width=44, bg=FIELD_BG, fg=FG, insertbackground=FG, justify="center",
            relief="flat", highlightthickness=1, highlightbackground=CARD_BORDER
        )
        self.chat_entry.pack(side="left", padx=(0, 8), ipady=4)
        self.chat_entry.bind("<Return>", lambda _e: self.send_chat())
        FancyButton(
            cr, text="SEND", command=self.send_chat,
            bg=SLATE_BTN, hover_bg=SLATE_HOVER, fg=FG, width=10, font=("Segoe UI Semibold", 8), pady=4
        ).pack(side="left")

        # 6. Compact Activity Log Card (Centered)
        f_log_outer = tk.Frame(self.root, bg=BG)
        f_log_outer.pack(fill="both", expand=True, padx=16, pady=(4, 12))
        f_log = tk.Frame(f_log_outer, bg=CARD_BG, highlightthickness=1, highlightbackground=CARD_BORDER, padx=12, pady=6)
        f_log.pack(fill="both", expand=True)
        tk.Label(
            f_log, text="SYSTEM & BRIDGE ACTIVITY LOG", fg=MUTED, bg=CARD_BG,
            font=("Segoe UI", 8, "bold"), justify="center"
        ).pack(fill="x", pady=(0, 3))
        self.log_box = tk.Text(
            f_log, height=4, bg="#0b0e14", fg="#8fa3bf", state="disabled", wrap="word",
            font=("Consolas", 8), relief="flat", highlightthickness=0
        )
        self.log_box.tag_configure("center", justify="center")
        self.log_box.pack(fill="both", expand=True)

        self._on_role_change()

    def _on_banner_resize(self, event):
        w = max(320, event.width)
        cx = w // 2
        self.banner.coords(self._title_id, cx, 27)
        self.banner.coords(self._sub_id, cx, 52)
        self.banner.coords(self._glow_line_bg, 0, 74, w, 74)

    def _anim_tick(self):
        if not self.running:
            return
        self._anim_phase = (self._anim_phase + 0.08) % (2.0 * math.pi)
        w = max(320, self.banner.winfo_width())
        span = 180
        cx = int((w + span) * (0.5 + 0.5 * math.sin(self._anim_phase * 0.7))) - (span // 2)
        self.banner.coords(self._glow_bar, max(0, cx - span // 2), 74, min(w, cx + span // 2), 74)

        now = time.monotonic()
        pulse = 0.5 + 0.5 * math.sin(self._anim_phase * 2.2)
        r_halo = int(11 + 3 * pulse)
        self.banner.coords(self._orb_halo, 32 - r_halo, 32 - r_halo, 32 + r_halo, 32 + r_halo)
        if self.connected and (now - self.server_last_rx < 4.0):
            self.banner.itemconfig(self._orb_dot, fill=ACCENT)
            self.banner.itemconfig(self._orb_halo, fill="#143824" if pulse > 0.5 else "#0f291b")
        elif self.connected:
            self.banner.itemconfig(self._orb_dot, fill=GOLD)
            self.banner.itemconfig(self._orb_halo, fill="#382c14")
        else:
            self.banner.itemconfig(self._orb_dot, fill="#64748b")
            self.banner.itemconfig(self._orb_halo, fill="#161d29")

        self.root.after(75, self._anim_tick)

    def _on_role_change(self):
        is_guest = (self.role_var.get() == "Guest")
        # Show "Guest Avatar" and "Teleport to Host" ONLY when Guest is selected!
        if is_guest:
            self.cb_model.config(state="readonly")
            self.lbl_guest_char.pack(side="left", padx=(0, 5))
            self.cb_model.pack(side="left")
            self.btn_tp.pack(side="left", padx=6)
        else:
            self.lbl_guest_char.pack_forget()
            self.cb_model.pack_forget()
            self.btn_tp.pack_forget()
        self.cfg["role"] = self.role_var.get()
        save_cfg(self.cfg)
        self._send_game(f"ROLE|{self.role_var.get()}")
        if is_guest:
            self._apply_guest_self_model()

    def _on_model_change(self):
        self.cfg["model"] = self.model_var.get()
        save_cfg(self.cfg)
        self.ui_log(f"Guest avatar selected: {self.model_var.get()}")
        if self.role_var.get() == "Guest":
            self._self_model_applied = None
            self._apply_guest_self_model()

    def _apply_guest_self_model(self):
        mdl_key = self.model_var.get()
        mdl = GUEST_MODELS.get(mdl_key, "ig_lamardavis")
        if self._self_model_applied != mdl and time.monotonic() - self.game_seen < 5.0:
            self._self_model_applied = mdl
            self._send_game(f"SELF_MODEL|{mdl}")
            self.ui_log(f"Applied guest avatar in GTA V: {mdl_key} ({mdl})")

    def ui_log(self, msg: str):
        line = write_log(msg)
        self.root.after(0, self._append_text, self.log_box, line)

    def ui_chat(self, msg: str):
        ts = time.strftime("%H:%M:%S")
        self.root.after(0, self._append_text, self.chat_box, f"[{ts}]  {msg}")

    @staticmethod
    def _append_text(widget: tk.Text, line: str):
        widget.config(state="normal")
        widget.insert("end", line + "\n", "center")
        widget.see("end")
        widget.config(state="disabled")

    def _send_game(self, line: str):
        try:
            self.game_tx.sendto(line.encode("utf-8"), ("127.0.0.1", GAME_PORT_OUT))
        except OSError:
            pass

    def tp_to_host(self):
        if self.role_var.get() != "Guest":
            return
        if self.peer_state:
            s = self.peer_state
            self._send_game(f"TP|{s['x']:.3f}|{s['y']:.3f}|{s['z']:.3f}|{s['h']:.2f}")
            self.ui_log("Teleported to Host position.")
        else:
            self.ui_log("Waiting for Host coordinates...")

    def create_room_action(self):
        rm = self.new_room_var.get().strip() or self.room_var.get().strip() or "Public"
        rm = rm[:32]
        self.room_var.set(rm)
        self.new_room_var.set("")
        self.cfg["room"] = rm
        self.cfg["room_password"] = self.room_pwd_var.get()
        save_cfg(self.cfg)
        self.lbl_active_room.config(text=f"Active Room: {rm}")
        if not self.connected:
            self.toggle_network()
        self._send_server({"t": "create_room", "room": rm, "password": self.room_pwd_var.get()})
        self.ui_chat(f"Created & joined room '{rm}'")
        self.ui_log(f"Created lobby room: {rm}")

    def join_selected_room(self):
        sel = self.rooms_listbox.curselection()
        rm = ""
        if sel and sel[0] < len(self._room_names_index):
            rm = self._room_names_index[sel[0]]
        elif self.new_room_var.get().strip():
            rm = self.new_room_var.get().strip()[:32]
        if not rm:
            return
        self.room_var.set(rm)
        self.cfg["room"] = rm
        self.cfg["room_password"] = self.room_pwd_var.get()
        save_cfg(self.cfg)
        self.lbl_active_room.config(text=f"Active Room: {rm}")
        self.peer_state = None
        self._send_game("DESPAWN")
        if not self.connected:
            self.toggle_network()
        self._send_server({"t": "join_room", "room": rm, "password": self.room_pwd_var.get()})
        self.ui_chat(f"Joined room '{rm}'")
        self.ui_log(f"Joined lobby room: {rm}")

    def toggle_network(self):
        if self.connected:
            self._stop_network()
            return
        srv = self.ip_var.get().strip()
        tun = self.tunnel_var.get().strip()
        mode = self.transport_var.get()
        if not srv and not tun:
            self.ui_log("Please enter a Server IP or Tunnel URL first.")
            return
        try:
            port = int(self.port_var.get() or DEFAULT_SERVER_PORT)
        except ValueError:
            port = DEFAULT_SERVER_PORT

        self.cfg.update({
            "role": self.role_var.get(),
            "name": self.name_var.get().strip() or PC_NAME,
            "server": srv,
            "port": port,
            "tunnel_url": tun,
            "transport": mode,
            "room": self.room_var.get().strip() or "Public",
            "room_password": self.room_pwd_var.get(),
            "model": self.model_var.get(),
        })
        save_cfg(self.cfg)

        self.server_addr = None
        if mode != "HTTPS Tunnel" and srv:
            sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            try:
                sock.bind(("0.0.0.0", 0))
                self.server_addr = (socket.gethostbyname(srv), port)
                sock.settimeout(0.2)
                self.net_sock = sock
            except OSError as e:
                self.ui_log(f"UDP setup note ({srv}:{port}): {e}")
                sock.close()
                self.net_sock = None

        if mode == "Direct UDP" and not self.net_sock:
            self.ui_log("Direct UDP failed to initialize.")
            return
        if mode == "HTTPS Tunnel" and not tun:
            self.ui_log("Please provide a Tunnel URL for HTTPS Tunnel mode.")
            return

        self.connect_started_at = time.monotonic()
        self.udp_last_rx = 0.0
        self.active_transport = "http" if (mode == "HTTPS Tunnel" or not self.net_sock) else "udp"
        self.connected = True
        self.btn_conn.set_style(text="DISCONNECT", bg=DANGER, hover_bg=DANGER_HOVER, fg="#ffffff")
        self.ui_log(f"Connecting (mode={mode}, room={self.cfg['room']}, role={self.cfg['role']})")
        if self.net_sock:
            threading.Thread(target=self._net_rx_loop, daemon=True).start()

    def _stop_network(self):
        was_http = (self.active_transport == "http")
        tun = self.tunnel_var.get().strip()
        nm = self.name_var.get().strip() or PC_NAME
        if self.connected:
            try:
                if self.net_sock and self.server_addr:
                    self.net_sock.sendto(json.dumps({"t": "bye", "name": nm}).encode("utf-8"), self.server_addr)
                elif was_http and tun:
                    req = urllib.request.Request(
                        tun,
                        data=json.dumps({"t": "bye", "cid": self.client_id, "name": nm}).encode("utf-8"),
                        headers={"Content-Type": "application/json", "User-Agent": f"GTA5-Coop/{VERSION}"},
                        method="POST",
                    )
                    threading.Thread(target=lambda: urllib.request.urlopen(req, timeout=1.5).read(), daemon=True).start()
            except Exception:
                pass
        self.connected = False
        self.active_transport = "none"
        if self.net_sock:
            try:
                self.net_sock.close()
            except OSError:
                pass
            self.net_sock = None
        self.server_addr = None
        self.peer_state = None
        self.room_peers = []
        self.online_players = []
        self.ping_ms = None
        self._send_game("DESPAWN")
        self.btn_conn.set_style(text="CONNECT SERVER", bg=ACCENT, hover_bg=ACCENT_HOVER, fg="#08120c")
        self.ui_log("Disconnected from server.")

    def _set_transport(self, tr: str):
        if getattr(self, "active_transport", None) != tr:
            old = getattr(self, "active_transport", None)
            self.active_transport = tr
            if old in ("udp", "http"):
                self.ui_log(f"Network: switched {old.upper()} -> {tr.upper()}")

    def _use_http_fallback(self) -> bool:
        mode = self.transport_var.get()
        tun = self.tunnel_var.get().strip()
        if not tun:
            return False
        if mode == "HTTPS Tunnel":
            return True
        if mode == "Direct UDP":
            return False
        # Auto (LAN + Tunnel): prefer fast UDP if alive within last 2.2s, else seamlessly switch to HTTPS tunnel!
        now = time.monotonic()
        if self.net_sock and self.server_addr:
            if now - self.connect_started_at < 1.4:
                return False
            if self.udp_last_rx > 0 and (now - self.udp_last_rx < 2.5):
                return False
        return True

    def _current_display_model(self) -> str:
        if self.role_var.get() == "Host":
            hmh = int(self.my_state.get("mh", 0))
            if hmh == -1686040670:
                return "Trevor"
            if hmh == -1692214353:
                return "Franklin"
            return "Michael"
        return self.model_var.get()

    def _send_server(self, obj: dict):
        if not self.connected:
            return
        obj.setdefault("room", self.room_var.get().strip() or "Public")
        obj.setdefault("name", self.name_var.get().strip() or PC_NAME)
        obj.setdefault("role", self.role_var.get())
        obj.setdefault("model", self._current_display_model())
        if self.room_pwd_var.get():
            obj.setdefault("password", self.room_pwd_var.get())

        if self._use_http_fallback():
            self._set_transport("http")
            with self._http_lock:
                # Coalesce high-rate state packets in HTTP batch
                if obj.get("t") == "state":
                    self._http_out_batch = deque(
                        (x for x in self._http_out_batch if x.get("t") != "state"), maxlen=64
                    )
                self._http_out_batch.append(obj)
            return

        self._set_transport("udp")
        if self.net_sock and self.server_addr:
            obj.setdefault("cid", self.client_id)
            try:
                self.net_sock.sendto(json.dumps(obj).encode("utf-8"), self.server_addr)
            except OSError:
                pass

    def send_chat(self):
        txt = self.chat_entry.get().strip()
        if not txt:
            return
        self.chat_entry.delete(0, "end")
        nm = self.name_var.get().strip() or PC_NAME
        self.ui_chat(f"{nm}: {txt}")
        self._send_server({"t": "chat", "text": txt})

    def _game_rx_loop(self):
        while self.running:
            try:
                data, _ = self.game_rx.recvfrom(65535)
            except socket.timeout:
                continue
            except OSError:
                break
            line = data.decode("utf-8", errors="ignore").strip()
            if not line:
                continue
            parts = line.split("|")
            cmd = parts[0]
            first_seen = (time.monotonic() - self.game_seen > 5.0)
            self.game_seen = time.monotonic()
            if first_seen and self.role_var.get() == "Guest":
                self._apply_guest_self_model()
            if cmd in ("HELLO", "HELLO_ACK"):
                self.ui_log(f"GTA V Bridge: {line}")
                self._send_game(f"ROLE|{self.role_var.get()}")
                if self.role_var.get() == "Guest":
                    self._self_model_applied = None
                    self._apply_guest_self_model()
            elif cmd == "EVENT":
                self.ui_log(f"GTA V Event: {line}")
            elif cmd == "ERR":
                self.ui_log(f"GTA V Error: {line}")
            elif cmd == "STATE" and len(parts) >= 7:
                try:
                    self.my_state = {
                        "x": float(parts[1]), "y": float(parts[2]), "z": float(parts[3]),
                        "h": float(parts[4]), "cut": int(parts[5]), "hp": int(parts[6]),
                        "spd": float(parts[7]) if len(parts) >= 8 else 0.0,
                        "wp": int(parts[8]) if len(parts) >= 9 else 0,
                        "outfit": parts[9] if len(parts) >= 10 else "",
                        "aim": int(parts[10]) if len(parts) >= 11 else 0,
                        "shoot": int(parts[11]) if len(parts) >= 12 else 0,
                        "ax": float(parts[12]) if len(parts) >= 13 else 0.0,
                        "ay": float(parts[13]) if len(parts) >= 14 else 0.0,
                        "az": float(parts[14]) if len(parts) >= 15 else 0.0,
                        "vm": int(parts[15]) if len(parts) >= 16 else 0,
                        "vx": float(parts[16]) if len(parts) >= 17 else 0.0,
                        "vy": float(parts[17]) if len(parts) >= 18 else 0.0,
                        "vz": float(parts[18]) if len(parts) >= 19 else 0.0,
                        "vh": float(parts[19]) if len(parts) >= 20 else 0.0,
                        "vs": int(parts[20]) if len(parts) >= 21 else -1,
                        "vc1": int(parts[21]) if len(parts) >= 22 else 0,
                        "vc2": int(parts[22]) if len(parts) >= 23 else 0,
                        "qx": float(parts[23]) if len(parts) >= 24 else 0.0,
                        "qy": float(parts[24]) if len(parts) >= 25 else 0.0,
                        "qz": float(parts[25]) if len(parts) >= 26 else 0.0,
                        "qw": float(parts[26]) if len(parts) >= 27 else 1.0,
                        "vvx": float(parts[27]) if len(parts) >= 28 else 0.0,
                        "vvy": float(parts[28]) if len(parts) >= 29 else 0.0,
                        "vvz": float(parts[29]) if len(parts) >= 30 else 0.0,
                        "str": float(parts[30]) if len(parts) >= 31 else 0.0,
                        "srn": int(parts[31]) if len(parts) >= 32 else 0,
                        "jmp": int(parts[32]) if len(parts) >= 33 else 0,
                        "rag": int(parts[33]) if len(parts) >= 34 else 0,
                        "stl": int(parts[34]) if len(parts) >= 35 else 0,
                        "rld": int(parts[35]) if len(parts) >= 36 else 0,
                        "pf": int(parts[36]) if len(parts) >= 37 else 0,
                        "mh": int(parts[37]) if len(parts) >= 38 else 0,
                        "vhid": int(parts[38]) if len(parts) >= 39 else 0,
                    }
                    # Send STATE immediately upon receipt from CoopBridge (0 ms sleep buffering -> crisp 40 Hz vehicle sync!)
                    now_tx = time.monotonic()
                    if self.connected and (now_tx - getattr(self, "_last_imm_state_tx", 0.0) >= 0.020):
                        self._last_imm_state_tx = now_tx
                        self._send_server({"t": "state", "s": self.my_state})
                except ValueError:
                    pass
            elif cmd == "PVP_HIT" and len(parts) >= 2:
                try:
                    self._send_server({"t": "pvp_hit", "dmg": int(parts[1]), "k": parts[2] if len(parts) >= 3 else ""})
                except ValueError:
                    pass
            elif cmd == "WORLD" and len(parts) >= 5:
                try:
                    wl = int(parts[4])
                    self._wanted_stars = wl
                    if self.role_var.get() == "Host":
                        hw = {
                            "t": "host_world",
                            "hr": int(parts[1]), "mn": int(parts[2]),
                            "wt": int(parts[3]), "wl": wl,
                        }
                        if len(parts) >= 9:   # v0.9.2: værovergang + sekunder
                            hw.update(wp=int(parts[5]), wn=int(parts[6]), wpc=float(parts[7]), sc=int(parts[8]))
                        if len(parts) >= 10:   # v0.9.2: vertens interiør
                            hw["ii"] = int(parts[9])
                        self._send_server(hw)
                except ValueError:
                    pass
            elif cmd == "WANTED_REQ" and len(parts) >= 5:
                if self.role_var.get() == "Guest":
                    try:
                        req_wl = int(parts[1])
                        self._wanted_stars = max(self._wanted_stars, req_wl)
                        self._send_server({
                            "t": "wanted_req",
                            "wl": req_wl,
                            "x": float(parts[2]), "y": float(parts[3]), "z": float(parts[4]),
                        })
                    except ValueError:
                        pass
            elif cmd == "CUTSCENE" and len(parts) >= 3:
                if self.role_var.get() == "Host":
                    self._send_server({"t": "host_cutscene", "act": parts[1], "cs": parts[2]})
                    self.ui_log(f"Cutscene sync ({parts[1]}): {parts[2]}")
            elif cmd == "NPCS" and len(parts) >= 2:
                if self.role_var.get() == "Host":
                    self._send_server({"t": "host_npcs", "d": parts[1]})
            elif cmd == "CAM" and len(parts) >= 2:
                if self.role_var.get() == "Host":
                    self._send_server({"t": "host_cam", "d": "|".join(parts[1:])})
            elif cmd == "PROPS" and len(parts) >= 2:
                if self.role_var.get() == "Host":
                    self._send_server({"t": "host_props", "d": parts[1]})
            elif cmd == "MELEE" and len(parts) >= 2:   # v0.9.2: slag som hendelse
                self._send_server({"t": "melee", "k": parts[1]})
            elif cmd == "VDMG" and len(parts) >= 2:   # v0.9.2: skadesynk for biler (begge roller)
                self._send_server({"t": "vdmg", "d": parts[1]})
            elif cmd == "VEHS" and len(parts) >= 2:
                if self.role_var.get() == "Host":
                    self._send_server({"t": "host_vehs", "d": parts[1]})
            elif cmd == "DIAG_RES" and len(parts) >= 2:
                self._send_server({"t": "diag_res", "d": "|".join(parts[1:])})
            elif cmd == "NPC_DEAD" and len(parts) >= 5:
                try:
                    self._send_server({
                        "t": "npc_dead",
                        "mh": int(parts[1]),
                        "x": float(parts[2]), "y": float(parts[3]), "z": float(parts[4]),
                    })
                except ValueError:
                    pass
            elif cmd == "INV" and len(parts) >= 3:
                if self.role_var.get() == "Host":
                    try:
                        self._send_server({
                            "t": "host_inv",
                            "money": int(parts[1]),
                            "weapons": parts[2],
                        })
                    except ValueError:
                        pass

    def _process_server_msg(self, msg: dict, now: float):
        self.server_last_rx = now
        t = msg.get("t")
        if t == "bundle":
            if isinstance(msg.get("pong"), dict):
                self._process_server_msg(msg["pong"], now)
            for item in (msg.get("msgs") or []):
                if isinstance(item, dict):
                    self._process_server_msg(item, now)
            return
        if t == "pong":
            pid = msg.get("id")
            t0 = self._ping_sent.pop(pid, None)
            if t0 is not None:
                self.ping_ms = (now - t0) * 1000.0
            if msg.get("room") and msg.get("room") != self.room_var.get():
                self.room_var.set(msg["room"])
            if isinstance(msg.get("peers"), list):
                self.room_peers = msg["peers"]
            if isinstance(msg.get("online"), list):
                self.online_players = msg["online"]
            if isinstance(msg.get("rooms"), list):
                self.rooms_list = msg["rooms"]
            if msg.get("error"):
                self.ui_log(f"Lobby notice: {msg['error']}")
        elif t == "chat":
            self.ui_chat(f"{msg.get('name', '?')}: {msg.get('text', '')}")
        elif t == "npc_dead":
            self._send_game(f"KILL_NEAR|{msg.get('mh', 0)}|{msg.get('x', 0.0):.2f}|{msg.get('y', 0.0):.2f}|{msg.get('z', 0.0):.2f}")
        elif t == "pvp_hit":
            self._send_game(f"PVP_HIT|{int(msg.get('dmg', 15))}|{msg.get('k', '')}")
        elif t == "wanted_req":
            if self.role_var.get() == "Host":
                self._send_game(
                    f"WANTED_REQ|{int(msg.get('wl', 1))}|{float(msg.get('x', 0.0)):.1f}|"
                    f"{float(msg.get('y', 0.0)):.1f}|{float(msg.get('z', 0.0)):.1f}"
                )
        elif t == "host_inv":
            if self.role_var.get() == "Guest":
                self._send_game(f"SYNC_INV|{int(msg.get('money', 0))}|{msg.get('weapons', '')}")
        elif t == "host_world":
            if self.role_var.get() == "Guest":
                wl = int(msg.get("wl", 0))
                self._wanted_stars = wl
                line = f"SYNC_WORLD|{int(msg.get('hr', 12))}|{int(msg.get('mn', 0))}|{int(msg.get('wt', 0))}|{wl}"
                if "wp" in msg:   # v0.9.2
                    line += f"|{int(msg['wp'])}|{int(msg['wn'])}|{float(msg.get('wpc', 0.0)):.3f}|{int(msg.get('sc', 0))}"
                    line += f"|{int(msg.get('ii', 0))}"
                self._send_game(line)
        elif t == "host_cutscene":
            if self.role_var.get() == "Guest":
                act = msg.get("act", "")
                cs = msg.get("cs", "")
                if act in ("start", "sync") and cs:
                    self._send_game(f"PLAY_CUTSCENE|{cs}")
                    if act == "start":
                        self.ui_log(f"Playing 1:1 synced cutscene: {cs}")
                elif act == "stop":
                    self._send_game(f"STOP_CUTSCENE|{cs}")
        elif t == "host_npcs":
            if self.role_var.get() == "Guest" and msg.get("d"):
                self._send_game(f"SYNC_NPCS|{msg['d']}")
        elif t == "host_cam":
            if self.role_var.get() == "Guest" and msg.get("d"):
                self._send_game(f"SYNC_CAM|{msg['d']}")
        elif t == "host_props":
            if self.role_var.get() == "Guest":
                self._send_game(f"SYNC_PROPS|{msg.get('d', '')}")
        elif t == "melee":
            self._send_game(f"PEER_MELEE|{1 if str(msg.get('k')) == '1' else 0}")
        elif t == "vdmg":
            if msg.get("d"):
                self._send_game(f"SYNC_VDMG|{msg['d']}")
        elif t == "host_vehs":
            if self.role_var.get() == "Guest" and msg.get("d"):
                self._send_game(f"SYNC_VEHS|{msg['d']}")
        elif t == "diag_req":
            self._send_game("DIAG")
        elif t == "state":
            self.peer_last_rx = now
            self.peer_name = msg.get("name", self.peer_name)
            peer_mdl_key = msg.get("model", "Lamar")
            self.peer_model = peer_mdl_key
            st = msg.get("s")
            if isinstance(st, dict):
                self.peer_state = st
                if self.role_var.get() == "Host":
                    mdl = GUEST_MODELS.get(peer_mdl_key, "ig_lamardavis")
                else:
                    hmh = int(st.get("mh", 0))
                    if hmh == -1686040670:
                        mdl = "player_two"
                    elif hmh == -1692214353:
                        mdl = "player_one"
                    else:
                        mdl = "player_zero"
                self._send_game(
                    f"PEER|{mdl}|{st.get('x', 0.0):.3f}|{st.get('y', 0.0):.3f}|"
                    f"{st.get('z', 0.0):.3f}|{st.get('h', 0.0):.2f}|"
                    f"{st.get('spd', 0.0):.2f}|{st.get('wp', 0)}|{st.get('outfit', '')}|"
                    f"{st.get('aim', 0)}|{st.get('shoot', 0)}|"
                    f"{st.get('ax', 0.0):.2f}|{st.get('ay', 0.0):.2f}|{st.get('az', 0.0):.2f}|"
                    f"{st.get('vm', 0)}|{st.get('vx', 0.0):.3f}|{st.get('vy', 0.0):.3f}|"
                    f"{st.get('vz', 0.0):.3f}|{st.get('vh', 0.0):.2f}|{st.get('vs', -1)}|"
                    f"{st.get('vc1', 0)}|{st.get('vc2', 0)}|"
                    f"{st.get('qx', 0.0):.4f}|{st.get('qy', 0.0):.4f}|{st.get('qz', 0.0):.4f}|{st.get('qw', 1.0):.4f}|"
                    f"{st.get('vvx', 0.0):.2f}|{st.get('vvy', 0.0):.2f}|{st.get('vvz', 0.0):.2f}|"
                    f"{st.get('str', 0.0):.2f}|{st.get('srn', 0)}|{st.get('jmp', 0)}|{st.get('rag', 0)}|"
                    f"{st.get('stl', 0)}|{st.get('rld', 0)}|{st.get('pf', 0)}|{st.get('vhid', 0)}|{st.get('hp', 200)}"
                )

    def _net_rx_loop(self):
        sock = self.net_sock
        while self.running and self.connected and sock and self.net_sock is sock:
            try:
                data, _ = sock.recvfrom(65535)
            except socket.timeout:
                continue
            except ConnectionResetError:
                # v0.9.2: Windows melder ICMP «port unreachable» (f.eks. mens serveren startet på nytt) som en
                # feil på NESTE recvfrom. Før avsluttet dette mottakstråden for godt -> evig HTTP-fallback.
                continue
            except OSError:
                if self.net_sock is sock and self.connected:
                    time.sleep(0.2)
                    continue
                break
            try:
                msg = json.loads(data.decode("utf-8"))
            except ValueError:
                continue
            now = time.monotonic()
            self.udp_last_rx = now
            self._set_transport("udp")
            self._process_server_msg(msg, now)

    def _http_tunnel_loop(self):
        """Persistent HTTP/1.1 keep-alive worker for Cloudflare Tunnel (just like Zelda OoT Coop Mod)."""
        conn = None
        last_target = None
        ctx = ssl.create_default_context()
        seq = 0
        while self.running:
            time.sleep(0.08)
            if not self.connected or not self._use_http_fallback():
                continue
            url = self.tunnel_var.get().strip()
            if not url:
                continue
            with self._http_lock:
                batch = list(self._http_out_batch)
                self._http_out_batch.clear()
            seq += 1
            now = time.monotonic()
            self._ping_sent[f"h{seq}"] = now
            batch.append({"t": "poll", "id": f"h{seq}"})
            payload = json.dumps({
                "cid": self.client_id,
                "name": self.name_var.get().strip() or PC_NAME,
                "role": self.role_var.get(),
                "room": self.room_var.get().strip() or "Public",
                "model": self.model_var.get(),
                "password": self.room_pwd_var.get(),
                "batch": batch,
            }).encode("utf-8")
            try:
                parsed = urllib.parse.urlsplit(url)
                scheme = parsed.scheme or "https"
                host = parsed.netloc or parsed.path
                path = parsed.path or "/gta5"
                if conn is None or last_target != (scheme, host):
                    if conn is not None:
                        try:
                            conn.close()
                        except Exception:
                            pass
                    if scheme == "https":
                        conn = http.client.HTTPSConnection(host, timeout=3.5, context=ctx)
                    else:
                        conn = http.client.HTTPConnection(host, timeout=3.5)
                    last_target = (scheme, host)
                conn.request("POST", path, body=payload, headers={
                    "Content-Type": "application/json",
                    "User-Agent": f"GTA5-Coop/{VERSION}",
                    "Connection": "keep-alive",
                })
                resp = conn.getresponse()
                raw = resp.read()
                if resp.status == 200 and raw:
                    msg = json.loads(raw.decode("utf-8"))
                    self._process_server_msg(msg, time.monotonic())
            except Exception:
                if conn is not None:
                    try:
                        conn.close()
                    except Exception:
                        pass
                    conn = None
                time.sleep(0.4)

    def _net_tx_loop(self):
        seq = 0
        last_ping = 0.0
        last_udp_probe = 0.0
        while self.running:
            time.sleep(0.05)  # 20 Hz
            if not self.connected:
                continue
            now = time.monotonic()
            # In Auto mode while using HTTP fallback, periodically probe UDP in case LAN comes online
            if self.transport_var.get() == "Auto (LAN + Tunnel)" and self.active_transport == "http":
                if self.net_sock and self.server_addr and now - last_udp_probe >= 5.0:
                    last_udp_probe = now
                    seq += 1
                    self._ping_sent[seq] = now
                    try:
                        probe = {
                            "t": "ping", "id": seq, "cid": self.client_id,
                            "room": self.room_var.get().strip() or "Public",
                            "name": self.name_var.get().strip() or PC_NAME,
                            "role": self.role_var.get(),
                            "model": self.model_var.get(),
                        }
                        self.net_sock.sendto(json.dumps(probe).encode("utf-8"), self.server_addr)
                    except OSError:
                        pass

            if not self._use_http_fallback() and now - last_ping >= 1.0:
                last_ping = now
                seq += 1
                self._ping_sent[seq] = now
                if len(self._ping_sent) > 30:
                    self._ping_sent.clear()
                self._send_server({"t": "ping", "id": seq})

            if now - self.game_seen < 3.0 and (now - getattr(self, "_last_imm_state_tx", 0.0) >= 0.25):
                self._send_server({"t": "state", "s": self.my_state})

    def _send_directinput_insert(self):
        import ctypes
        from ctypes import wintypes

        class KEYBDINPUT(ctypes.Structure):
            _fields_ = [("wVk", wintypes.WORD), ("wScan", wintypes.WORD), ("dwFlags", wintypes.DWORD),
                        ("time", wintypes.DWORD), ("dwExtraInfo", ctypes.c_size_t)]

        class INPUT(ctypes.Structure):
            _fields_ = [("type", wintypes.DWORD), ("ki", KEYBDINPUT)]

        u = ctypes.windll.user32
        found = []

        @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
        def enum_cb(hwnd, _lp):
            if u.IsWindowVisible(hwnd):
                buf = ctypes.create_unicode_buffer(128)
                u.GetWindowTextW(hwnd, buf, 128)
                if "Grand Theft Auto V" in buf.value:
                    found.append(hwnd)
            return True

        u.EnumWindows(enum_cb, 0)
        if not found:
            self.ui_log("Auto-reload: GTA V window not found.")
            return False
        hwnd = found[0]
        u.ShowWindow(hwnd, 9)
        u.keybd_event(0x12, 0x38, 0, 0)
        u.SetForegroundWindow(hwnd)
        u.keybd_event(0x12, 0x38, 0x0002, 0)
        time.sleep(0.20)
        u.PostMessageW(hwnd, 0x0100, 0x2D, 0x01520001)
        dn = INPUT(1, KEYBDINPUT(0x2D, 0x52, 0x0001 | 0x0008, 0, 0))
        up = INPUT(1, KEYBDINPUT(0x2D, 0x52, 0x0001 | 0x0008 | 0x0002, 0, 0))
        u.SendInput(1, ctypes.byref(dn), ctypes.sizeof(INPUT))
        time.sleep(0.09)
        u.SendInput(1, ctypes.byref(up), ctypes.sizeof(INPUT))
        u.PostMessageW(hwnd, 0x0101, 0x2D, -1051590655)
        self.ui_log(f"Auto-reload: sent DirectInput Insert to GTA V (hwnd=0x{hwnd:X}).")
        return True

    def _trigger_watch_loop(self):
        trig = os.path.join(BASE_DIR, "reload.trigger")
        while self.running:
            time.sleep(0.5)
            if os.path.isfile(trig):
                try:
                    os.remove(trig)
                except OSError:
                    pass
                try:
                    self._send_directinput_insert()
                    self._send_game("RELOAD")
                except Exception as e:
                    self.ui_log(f"Auto-reload error: {e}")

    def _refresh_lists(self):
        # 1. Refresh Lobby Rooms Listbox
        cur_room = self.room_var.get().strip() or "Public"
        self.lbl_active_room.config(text=f"Active Room: {cur_room}")
        rooms_data = self.rooms_list or [{"name": "Public", "count": len(self.room_peers), "host": "", "locked": False}]
        new_room_lines = []
        new_room_names = []
        sel_idx = None
        prev_sel = self.rooms_listbox.curselection()
        prev_name = self._room_names_index[prev_sel[0]] if (prev_sel and prev_sel[0] < len(self._room_names_index)) else None
        for idx, r in enumerate(rooms_data):
            rn = r.get("name", "Public")
            cnt = int(r.get("count", 0))
            lock = "🔒 " if r.get("locked") else ""
            marker = "▸ " if rn == cur_room else ""
            host_s = f" · Host: {r['host']}" if r.get("host") else ""
            new_room_lines.append(f"{marker}{lock}{rn}  ({cnt} online){host_s}")
            new_room_names.append(rn)
            if rn == prev_name:
                sel_idx = idx
        if list(self.rooms_listbox.get(0, "end")) != new_room_lines:
            self.rooms_listbox.delete(0, "end")
            for ln in new_room_lines:
                self.rooms_listbox.insert("end", ln)
            if sel_idx is not None:
                self.rooms_listbox.selection_set(sel_idx)
        self._room_names_index = new_room_names

        # 2. Refresh Connected Players Listbox (like Zelda OoT Coop Mod!)
        my_name = self.name_var.get().strip() or PC_NAME
        players = self.online_players if self.online_players else [
            {**p, "room": cur_room} for p in self.room_peers
        ]
        self.lbl_conn_header.config(text=f"CONNECTED PLAYERS ({len(players)})")
        conn_lines = []
        for p in players:
            nm = p.get("name", "?")
            rl = normalize_role(p.get("role", "Guest"))
            rm = p.get("room", cur_room)
            mdl = p.get("model", "")
            badge = (f"★ HOST · {mdl}" if mdl else "★ HOST") if rl == "Host" else f"● GUEST · {mdl}"
            you_tag = " (You)" if nm == my_name else ""
            conn_lines.append(f"{nm}{you_tag}   [{badge}]   ·   {rm}")
        if not conn_lines:
            conn_lines = ["No players connected"]
        if list(self.connected_listbox.get(0, "end")) != conn_lines:
            self.connected_listbox.delete(0, "end")
            for ln in conn_lines:
                self.connected_listbox.insert("end", ln)

    def _ui_tick(self):
        if not self.running:
            return
        now = time.monotonic()

        if not self.connected:
            self.lbl_net_pill.config(text="● SERVER: DISCONNECTED", fg=MUTED, highlightbackground=CARD_BORDER)
            self.lbl_conn_header.config(text="CONNECTED PLAYERS (0)")
        elif now - self.server_last_rx < 4.0:
            tr = "UDP" if self.active_transport == "udp" else "TUNNEL"
            p_txt = f"{self.ping_ms:.0f} ms" if self.ping_ms is not None else "OK"
            self.lbl_net_pill.config(
                text=f"● {tr} CONNECTED  ·  {p_txt}",
                fg=ACCENT, highlightbackground=CARD_BORDER_GLOW
            )
            self._refresh_lists()
        else:
            self.lbl_net_pill.config(text="● SERVER: CONNECTING...", fg=GOLD, highlightbackground=GOLD)

        stars_on = max(0, min(5, int(self._wanted_stars)))
        star_str = ("★" * stars_on) + ("☆" * (5 - stars_on))
        self.lbl_world_pill.config(
            text=f"POLICE WANTED: {star_str}",
            fg=DANGER if stars_on > 0 else GOLD,
            highlightbackground=DANGER if stars_on > 0 else CARD_BORDER
        )

        if now - self.game_seen < 3.0:
            self.lbl_game_pill.config(text="● GTA V BRIDGE: ACTIVE", fg=ACCENT, highlightbackground=CARD_BORDER_GLOW)
            s = self.my_state
            self.lbl_pos.config(
                text=f"Coords: ({s['x']:.1f}, {s['y']:.1f}, {s['z']:.1f})   ·   Heading: {s['h']:.0f}°   ·   HP: {s['hp']}   ·   Room: {self.room_var.get()}",
                fg=FG
            )
        else:
            self.lbl_game_pill.config(text="● GTA V: WAITING FOR GAME", fg=MUTED, highlightbackground=CARD_BORDER)
            self.lbl_pos.config(text="Waiting for GTA V Story Mode (CoopBridge.dll)...", fg=MUTED)

        self.root.after(250, self._ui_tick)

    def on_close(self):
        self.running = False
        self._stop_network()
        try:
            self.game_rx.close()
            self.game_tx.close()
        except OSError:
            pass
        write_log("Mod GUI closed.")
        self.root.destroy()


def main():
    try:
        root = tk.Tk()
        CoopApp(root)
        root.mainloop()
    except Exception as exc:
        import traceback
        write_log("FATAL GUI ERROR: " + "".join(traceback.format_exception(exc)))
        raise


if __name__ == "__main__":
    import sys
    if "--install-guest-save" in sys.argv:   # v0.9.2: kjøres av Start_GTA5_Coop.bat før spillet startes
        if (load_cfg().get("role") or "Guest") == "Guest":
            install_guest_save(write_log)
        sys.exit(0)
    main()
