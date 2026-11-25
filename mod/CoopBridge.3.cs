// GTA 5 Story Co-op v0.9.2 - In-game bridge (ScriptHookVDotNet v3, kompilert til CoopBridge.dll)
// Ingen in-game meny. Kommuniserer over localhost UDP med Mod-GUI.
// Støtter: posisjon, retning, ekte gå-/løpe-bevegelse, klær-synk (0..11), våpen-synk, politi-synk og verdenssynk.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using GTA;
using GTA.Math;
using GTA.Native;

public class CoopBridge : Script
{
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private const int PORT_IN = 27510;   // GUI -> GTA5
    private const int PORT_OUT = 27511;  // GTA5 -> GUI
    private UdpClient _rx;
    private UdpClient _tx;
    private IPEndPoint _guiEp;

    private Ped _guestPed;
    private Vehicle _guestVeh;
    private bool _guestVehCreatedByUs = false;
    private int _guestVehModel = 0;
    private string _guestModelName = "";
    private string _lastAppliedOutfit = "";
    private uint _lastAppliedWeapon = 0;

    private string _wantedSelfModel = "";
    private int _wantedSelfHash = 0;
    private int _lastSelfCheckMs = 0;

    private Vector3 _targetPos;
    private float _targetHeading;
    private float _targetSpeed;
    private uint _targetWeapon;
    private string _targetOutfit = "";
    private bool _targetAiming;
    private bool _targetShooting;
    private bool _wasAiming;
    private Vector3 _targetAimPos;
    private Vector3 _lastAimSentPos;
    private int _targetVehModel;
    private Vector3 _targetVehPos;
    private float _targetVehHeading;
    private float _targetVehQx, _targetVehQy, _targetVehQz, _targetVehQw = 1f;
    private Vector3 _targetVehVel;
    private float _targetVehSteer;
    private int _targetVehFlags; // 1=Siren, 2=Horn, 4=Lights, 8=HighBeams, 16=BrakeLights
    private bool _targetJumping;
    private bool _targetRagdoll;
    private bool _targetStealth;
    private bool _targetReloading;
    private int _targetPedFlags; // 1=InCover, 2=HighCover, 4=CoverLeft, 8=AimFromCover, 16=Climb/Vault, 32=Ducking, 64=Melee, 128=EnteringVeh
    private int _targetVehHostId = 0;
    private bool _wasJumping;
    private bool _wasReloading;
    private bool _wasInCover;
    private int _lastCoverMask = -1;
    private bool _wasClimbing;
    private bool _wasPeerMoving;
    private bool _wasMelee;
    private bool _wasEnteringVeh;
    private int _lastMeleeTaskMs;
    private int _lastEnterTaskMs;
    private int _leavingVehUntilMs;
    private int _peerRelGroup = 0;
    private bool _peerRelGroupInit = false;
    private int _hostFreeRoamDeathMs = 0;
    private bool _hostFreeRoamGuards = false;
    private int _lastPvpHitSentMs = 0;
    private int _targetPeerHp = 200;
    private bool _punchAlt = false;
    private bool _startupTasksCleared = false;
    private bool _hostWasInCutscene = false;
    private int _lastHostCsSyncMs = 0;
    private int _guestCsState = 0; // 0=None, 1=Loading, 2=Playing
    private string _guestActiveCsName = "";
    private int _guestCsReqStartMs = 0;
    private int _guestCsPlayStartMs = 0;
    private bool _guestCsEntitiesRegistered = false;
    private int _lastSentWanted = -1;
    private int _hostAuthoritativeWanted = 0;
    private int _lastWantedAcceptMs = 0;
    private int _lastGuestWantedReqMs = 0;
    private Dictionary<int, int> _syncedNpcBulletMs = new Dictionary<int, int>();
    private int _lastShootTaskMs;
    private int _targetVehSeat = -1;
    private int _targetVehC1;
    private int _targetVehC2;
    private int _lastShotMs;
    private int _lastAimTaskMs;
    private int _lastWorldSendMs;
    private int _strayCsSinceMs = 0;
    public static bool Idle = false;             // v0.9.2: IDLE|1 = modden gjør ingenting (feilsøking)
    private int _myMeleeStartMs = 0;             // v0.9.2: slag som hendelser
    private bool _myMeleeHeavy = false;
    private bool _myMeleeHitChecked = true;
    private bool _wasPerformingMelee = false;
    private int _hostCsUnknownSinceMs = 0;      // v0.9.2: vertens mellomsekvens uten gjenkjent navn
    private bool _hostCsUnknownSent = false;
    private int _lastIntApplied = 0;            // v0.9.2: interiør verten står i, slått på hos gjesten
    private int _lastIntApplyMs = 0;
    // v0.9.2: skadesynk for biler
    private struct DmgState { public int Eng, Body, Tank, Doors, Wins, Tyres, Dirt; }
    private Dictionary<int, DmgState> _dmgSent = new Dictionary<int, DmgState>();
    private Dictionary<int, int> _dmgSentMs = new Dictionary<int, int>();
    private Dictionary<int, float> _dmgLastBody = new Dictionary<int, float>();
    private Dictionary<int, DmgState> _dmgApplied = new Dictionary<int, DmgState>();
    private int _lastDmgTickMs = 0;
    private static readonly int[] TYRE_IDX = new int[] { 0, 1, 2, 3, 4, 5, 45, 47 };
    private List<Entity> _csHiddenEntities = new List<Entity>();   // v0.9.2: skjult under gjestens mellomsekvens
    private bool _hostCsHidGuest = false;
    private int _strayCsTries = 0;
    private int _lastSyncedWeather = -1;
    private string[] _allCutscenes = new string[0];
    private string _activeCutscene = "";
    private bool _isGuestRole = false;
    private int _guestDeathStartMs = 0;
    private bool _deathGuardsApplied = false;
    private int _lastTrafficPurgeMs = 0;
    private Vector3 _hostCutsceneCenter;
    private bool _safeTpWaitingGround = false;
    private Vector3 _safeTpPos;
    private int _safeTpStartMs = 0;
    private bool _hasTarget;
    private bool _syncEnabled = true;
    private int _lastSendMs;
    private int _lastOutfitSendMs;
    private int _lastRxMs;
    private int _lastMoveTaskMs;
    private int _lastNpcScanMs;
    private int _lastNpcSyncMs;
    private Camera _hostCam;
    private bool _hostCamFocusSet = false;
    private Vector3 _hostCamVel;
    private int _lastAutoRegionTpMs = 0;
    private int _lastPeerVehRxMs = 0;
    private struct NpcTarget
    {
        public Vector3 Pos;
        public Vector3 Vel;
        public float Heading;
        public float Speed;
        public int Health;
        public uint Weapon;
        public int Flags; // bit0=aim/combat, bit1=shoot, bit2=cover, bit3=cower/handsup, bit4=actionMode
        public int VehHandle;
        public int VehSeat;
        public int SeenMs;
    }

    private Dictionary<int, Ped> _syncedNpcs = new Dictionary<int, Ped>();
    private Dictionary<int, NpcTarget> _npcTargets = new Dictionary<int, NpcTarget>();
    private HashSet<int> _spawnedNpcHandles = new HashSet<int>();
    private Dictionary<int, int> _syncedNpcMoveMs = new Dictionary<int, int>();
    private Dictionary<int, int> _syncedNpcAnimFlags = new Dictionary<int, int>();
    private Dictionary<int, float> _syncedNpcAimHeading = new Dictionary<int, float>();
    private Dictionary<int, string> _npcWantedOutfits = new Dictionary<int, string>();
    private Dictionary<int, string> _npcAppliedOutfits = new Dictionary<int, string>();
    private int _lastNpcStyleSendMs;
    private struct VehTarget
    {
        public int ModelHash;
        public Vector3 Pos;
        public float Qx, Qy, Qz, Qw;
        public Vector3 Vel;
        public float Steer;
        public int C1, C2;
        public int VFlags;
        public bool Dead;
        public int DrvModel;
        public int PassModel;
        public int SeenMs;
    }

    private int _lastVehSyncMs;
    private int _vehFarCursor = 10;
    private int _npcFarCursor = 10;
    private Dictionary<int, Vehicle> _syncedVehs = new Dictionary<int, Vehicle>();
    private Dictionary<int, VehTarget> _vehTargets = new Dictionary<int, VehTarget>();
    private Dictionary<int, Ped> _syncedVehDrivers = new Dictionary<int, Ped>();
    private Dictionary<int, Ped> _syncedVehPass = new Dictionary<int, Ped>();
    private static readonly string[] SYNCED_IPLS = new string[] {
        "prologue01", "prologue01c", "prologue01d", "prologue01e", "prologue01f",
        "prologue01g", "prologue01h", "prologue01i", "prologue01j", "prologue01k",
        "prologue01z", "prologue02", "prologue03", "prologue03_grv_dug", "prologue04",
        "prologue05", "prologue06", "prologuerd", "des_pro_tree_crash"
    };
    private int _lastHiddenSweepMs;
    private int _lastHiddenMaintMs;
    private int _lastPropSyncMs;
    private Dictionary<int, Prop> _hiddenProps = new Dictionary<int, Prop>();
    private Dictionary<int, Ped> _hiddenLocalNpcs = new Dictionary<int, Ped>();
    private int _lastInvSendMs;
    private string _lastSyncedInv = "";
    private bool _initialWeaponEquipped = false;
    private HashSet<int> _reportedDead = new HashSet<int>();
    private string _cachedMyOutfit = "";

    public CoopBridge()
    {
        Tick += OnTick;
        Aborted += OnAborted;
        Interval = 16; // ~60 Hz

        try
        {
            string csPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "cutscenes.txt");
            if (File.Exists(csPath))
            {
                _allCutscenes = File.ReadAllLines(csPath);
            }
        }
        catch { }

        try
        {
            _rx = new UdpClient(new IPEndPoint(IPAddress.Loopback, PORT_IN));
            _rx.Client.Blocking = false;
            _tx = new UdpClient();
            _guiEp = new IPEndPoint(IPAddress.Loopback, PORT_OUT);
            SendRaw("HELLO|v0.9.2|ready");
        }
        catch (Exception)
        {
        }
    }

    private void OnAborted(object sender, EventArgs e)
    {
        if (_isGuestRole && _guestCsState != 0)
        {
            StopGuestCutscene(Vector3.Zero, 0f);
        }
        CleanupGuest();
        CleanupHostCam();
        CleanupSyncedNpcs();
        CleanupSyncedVehs();
        RestoreHiddenProps();
        try { if (_rx != null) _rx.Close(); } catch { }
        try { if (_tx != null) _tx.Close(); } catch { }
    }

    private void CleanupSyncedVehs()
    {
        Ped me = null;
        try { me = Game.Player.Character; } catch { }
        foreach (Ped dp in _syncedVehDrivers.Values)
        {
            try { if (dp != null && dp.Exists()) dp.Delete(); } catch { }
        }
        foreach (Ped pp in _syncedVehPass.Values)
        {
            try { if (pp != null && pp.Exists()) pp.Delete(); } catch { }
        }
        _syncedVehDrivers.Clear();
        _syncedVehPass.Clear();
        foreach (KeyValuePair<int, Vehicle> kv in _syncedVehs)
        {
            try
            {
                if (kv.Value != null && kv.Value.Exists() && (me == null || !me.IsInVehicle(kv.Value)))
                {
                    kv.Value.Delete();
                }
            }
            catch { }
        }
        _syncedVehs.Clear();
        _vehTargets.Clear();
    }

    private void RestoreHiddenProps()
    {
        foreach (KeyValuePair<int, Prop> kv in _hiddenProps)
        {
            try
            {
                if (kv.Value != null && kv.Value.Exists())
                {
                    kv.Value.IsVisible = true;
                    Function.Call(Hash.SET_ENTITY_COLLISION, kv.Value.Handle, true, true);
                }
            }
            catch { }
        }
        _hiddenProps.Clear();
    }

    private void CleanupHostCam()
    {
        try
        {
            if (_hostCamFocusSet)
            {
                _hostCamFocusSet = false;
                Function.Call(Hash.CLEAR_FOCUS);
            }
            if (_hostCam != null)
            {
                World.RenderingCamera = null;
                _hostCam.Delete();
                _hostCam = null;
            }
            _hostCamVel = Vector3.Zero;
        }
        catch { _hostCam = null; }
    }

    private void CleanupSyncedNpcs()
    {
        foreach (KeyValuePair<int, Ped> kv in _syncedNpcs)
        {
            try
            {
                if (kv.Value != null && kv.Value.Exists())
                {
                    kv.Value.IsPositionFrozen = false;
                    if (_spawnedNpcHandles.Contains(kv.Key))
                    {
                        kv.Value.Delete();
                    }
                }
            }
            catch { }
        }
        foreach (KeyValuePair<int, Ped> kv in _hiddenLocalNpcs)
        {
            try
            {
                if (kv.Value != null && kv.Value.Exists())
                {
                    kv.Value.IsPositionFrozen = false;
                    kv.Value.IsVisible = true;
                    Function.Call(Hash.SET_ENTITY_COLLISION, kv.Value.Handle, true, true);
                }
            }
            catch { }
        }
        _hiddenLocalNpcs.Clear();
        _syncedNpcs.Clear();
        _npcTargets.Clear();
        _spawnedNpcHandles.Clear();
        _syncedNpcMoveMs.Clear();
        _syncedNpcAnimFlags.Clear();
        _syncedNpcAimHeading.Clear();
        _syncedNpcBulletMs.Clear();
        _npcWantedOutfits.Clear();
        _npcAppliedOutfits.Clear();
    }

    private void CleanupGuest()
    {
        try
        {
            if (_guestPed != null && _guestPed.Exists())
            {
                _guestPed.Delete();
            }
        }
        catch { }
        try
        {
            if (_guestVeh != null && _guestVeh.Exists() && _guestVehCreatedByUs)
            {
                Ped me = Game.Player.Character;
                if (me == null || !me.IsInVehicle(_guestVeh))
                {
                    _guestVeh.Delete();
                }
            }
        }
        catch { }
        _guestPed = null;
        _guestVeh = null;
        _guestVehCreatedByUs = false;
        _guestVehModel = 0;
        _guestModelName = "";
        _lastAppliedOutfit = "";
        _lastAppliedWeapon = 0;
        _hasTarget = false;
    }

    private void SendRaw(string msg)
    {
        if (_tx == null || _guiEp == null) return;
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(msg);
            _tx.Send(data, data.Length, _guiEp);
        }
        catch { }
    }

    private void PollGui()
    {
        if (_rx == null) return;
        try
        {
            while (_rx.Available > 0)
            {
                IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                byte[] data = _rx.Receive(ref ep);
                string line = Encoding.UTF8.GetString(data).Trim();
                HandleCommand(line);
            }
        }
        catch { }
    }

    private void HandleCommand(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        string[] parts = line.Split('|');
        string cmd = parts[0];

        if (cmd == "HELLO")
        {
            string gs = (_guestPed != null && _guestPed.Exists())
                ? string.Format(CultureInfo.InvariantCulture, "yes|vis={0}|pos={1:F1},{2:F1},{3:F1}",
                    _guestPed.IsVisible ? 1 : 0, _guestPed.Position.X, _guestPed.Position.Y, _guestPed.Position.Z)
                : "none";
            SendRaw("HELLO_ACK|v0.9.2|guest=" + gs);
        }
        else if (cmd == "RELOAD")
        {
            // Sett eget GTA5-vindu i forgrunnen (samme Admin-prosess -> 0 UIPI!) og send både WM_KEYDOWN og keybd_event med 70ms hold!
            SendRaw("EVENT|reloading|v0.9.2");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    IntPtr h = Process.GetCurrentProcess().MainWindowHandle;
                    if (h != IntPtr.Zero)
                    {
                        PostMessage(h, 0x0100, (IntPtr)0x2D, (IntPtr)0x01520001);
                        ShowWindow(h, 9);
                        keybd_event(0x12, 0x38, 0, UIntPtr.Zero); // Alt-unlock for SetForegroundWindow
                        SetForegroundWindow(h);
                        keybd_event(0x12, 0x38, 0x0002, UIntPtr.Zero);
                        Thread.Sleep(90);
                        keybd_event(0x2D, 0x52, 0x0001, UIntPtr.Zero);
                        Thread.Sleep(90);
                        keybd_event(0x2D, 0x52, 0x0001 | 0x0002, UIntPtr.Zero);
                        PostMessage(h, 0x0101, (IntPtr)0x2D, unchecked((IntPtr)0xC1520001));
                    }
                }
                catch { }
            });
        }
        else if (cmd == "QUIT")
        {
            // v0.9.2: avslutt GTA V (spillet kjører som administrator, så verktøy utenfra får ikke lukket det)
            SendRaw("EVENT|quitting");
            try { Process.GetCurrentProcess().Kill(); } catch { }
        }
        else if (cmd == "SYNC" && parts.Length >= 2)
        {
            _syncEnabled = (parts[1] == "1");
            if (!_syncEnabled) CleanupGuest();
        }
        else if (cmd == "DESPAWN")
        {
            CleanupGuest();
        }
        else if (cmd == "SELF_MODEL" && parts.Length >= 2)
        {
            string mdl = parts[1];
            _wantedSelfModel = mdl;
            _wantedSelfHash = Function.Call<int>(Hash.GET_HASH_KEY, mdl);
            ApplySelfModel(mdl);
        }
        else if (cmd == "TP" && parts.Length >= 5)
        {
            float x, y, z, h;
            if (TryF(parts[1], out x) && TryF(parts[2], out y) && TryF(parts[3], out z) && TryF(parts[4], out h))
            {
                SafeTeleportLocalPlayer(new Vector3(x + 2.0f, y, z + 0.5f), h);
            }
        }
        else if (cmd == "PEER" && parts.Length >= 6)
        {
            // PEER|model|x|y|z|heading|speed|weaponHash|outfit|aiming|shooting|aimX|aimY|aimZ
            string mdl = parts[1];
            float x, y, z, h;
            if (TryF(parts[2], out x) && TryF(parts[3], out y) && TryF(parts[4], out z) && TryF(parts[5], out h))
            {
                _targetPos = new Vector3(x, y, z);
                _targetHeading = h;
                float spd = 0f;
                if (parts.Length >= 7) TryF(parts[6], out spd);
                _targetSpeed = spd;
                uint wp = 0;
                if (parts.Length >= 8) uint.TryParse(parts[7], out wp);
                _targetWeapon = wp;
                if (parts.Length >= 9 && !string.IsNullOrEmpty(parts[8]))
                {
                    _targetOutfit = parts[8];
                }
                _targetAiming = (parts.Length >= 10 && parts[9] == "1");
                _targetShooting = (parts.Length >= 11 && parts[10] == "1");
                float ax = x, ay = y, az = z + 0.5f;
                if (parts.Length >= 14)
                {
                    TryF(parts[11], out ax);
                    TryF(parts[12], out ay);
                    TryF(parts[13], out az);
                }
                _targetAimPos = new Vector3(ax, ay, az);
                int vm = 0, vs = -1, vc1 = 0, vc2 = 0, vfl = 0;
                float vx = 0f, vy = 0f, vz = 0f, vh = 0f;
                float qx = 0f, qy = 0f, qz = 0f, qw = 1f;
                float vvx = 0f, vvy = 0f, vvz = 0f, steer = 0f;
                bool jmp = false, rag = false, stl = false, rld = false;
                if (parts.Length >= 22)
                {
                    int.TryParse(parts[14], out vm);
                    TryF(parts[15], out vx);
                    TryF(parts[16], out vy);
                    TryF(parts[17], out vz);
                    TryF(parts[18], out vh);
                    int.TryParse(parts[19], out vs);
                    int.TryParse(parts[20], out vc1);
                    int.TryParse(parts[21], out vc2);
                }
                if (parts.Length >= 33)
                {
                    TryF(parts[22], out qx);
                    TryF(parts[23], out qy);
                    TryF(parts[24], out qz);
                    TryF(parts[25], out qw);
                    TryF(parts[26], out vvx);
                    TryF(parts[27], out vvy);
                    TryF(parts[28], out vvz);
                    TryF(parts[29], out steer);
                    int.TryParse(parts[30], out vfl);
                    jmp = (parts[31] == "1");
                    rag = (parts[32] == "1");
                }
                int pflags = 0, vhid = 0, php = 200;
                if (parts.Length >= 35)
                {
                    stl = (parts[33] == "1");
                    rld = (parts[34] == "1");
                }
                if (parts.Length >= 36)
                {
                    int.TryParse(parts[35], out pflags);
                }
                if (parts.Length >= 37)
                {
                    int.TryParse(parts[36], out vhid);
                }
                if (parts.Length >= 38)
                {
                    int.TryParse(parts[37], out php);
                }
                _targetVehModel = vm;
                _targetVehHostId = vhid;
                _targetPeerHp = php;
                _targetVehPos = new Vector3(vx, vy, vz);
                _targetVehHeading = vh;
                _targetVehQx = qx; _targetVehQy = qy; _targetVehQz = qz; _targetVehQw = qw;
                _targetVehVel = new Vector3(vvx, vvy, vvz);
                _targetVehSteer = steer;
                _targetVehFlags = vfl;
                _targetJumping = jmp;
                _targetRagdoll = rag;
                _targetStealth = stl;
                _targetReloading = rld;
                _targetPedFlags = pflags;
                _targetVehSeat = vs;
                _targetVehC1 = vc1;
                _targetVehC2 = vc2;
                if (vm != 0) _lastPeerVehRxMs = Game.GameTime;
                _hasTarget = true;
                _lastRxMs = Game.GameTime;
                if (_syncEnabled && !string.IsNullOrEmpty(mdl) && (mdl != _guestModelName || _guestPed == null || !_guestPed.Exists()))
                {
                    SpawnGuest(mdl, _targetPos, _targetHeading);
                }
            }
        }
        else if (cmd == "KILL_NEAR" && parts.Length >= 5)
        {
            // KILL_NEAR|modelHash|x|y|z — synk drepte NPC-er fra verten
            int mh;
            float kx, ky, kz;
            if (int.TryParse(parts[1], out mh) && TryF(parts[2], out kx) && TryF(parts[3], out ky) && TryF(parts[4], out kz))
            {
                Vector3 kp = new Vector3(kx, ky, kz);
                Ped me = Game.Player.Character;
                Ped[] near = World.GetNearbyPeds(kp, 4.5f);
                foreach (Ped np in near)
                {
                    if (np != null && np.Exists() && np != me && np != _guestPed && np.IsAlive && np.Model.Hash == mh)
                    {
                        np.Kill();
                        break;
                    }
                }
            }
        }
        else if (cmd == "ROLE" && parts.Length >= 2)
        {
            _isGuestRole = (parts[1] == "Guest");
        }
        else if (cmd == "PLAY_CUTSCENE" && parts.Length >= 2)
        {
            CleanupHostCam();
            if (_isGuestRole)
            {
                StartOrSyncGuestCutscene(parts[1]);
            }
        }
        else if (cmd == "STOP_CUTSCENE")
        {
            CleanupHostCam();
            if (_isGuestRole)
            {
                Vector3 endPos = _targetPos;
                float endH = _targetHeading;
                if (parts.Length >= 2 && !string.IsNullOrEmpty(parts[1]) && parts[1] != "end")
                {
                    string[] ef = parts[1].Split(',');
                    float ex, ey, ez, eh;
                    if (ef.Length >= 4 && TryF(ef[0], out ex) && TryF(ef[1], out ey) && TryF(ef[2], out ez) && TryF(ef[3], out eh))
                    {
                        endPos = new Vector3(ex, ey, ez);
                        endH = eh;
                    }
                }
                StopGuestCutscene(endPos, endH);
            }
        }
        else if (cmd == "SYNC_CAM" && parts.Length >= 2)
        {
            CleanupHostCam();
        }
        else if (cmd == "SYNC_NPCS" && parts.Length >= 2)
        {
            // Ignorer speil-NPC-er mens 1:1 cutscene spiller på Gjesten -> 0 % doble karakterer i cutscenen!
            if (_guestCsState == 0) HandleSyncNpcs(parts[1]);
        }
        else if (cmd == "SYNC_PROPS" && parts.Length >= 2)
        {
            // SYNC_PROPS|m,x,y,z,dState,ratio;... (ekte dører + porter + IPL fra Vert -> Gjest)
            HandleSyncProps(parts[1]);
        }
        else if (cmd == "IDLE" && parts.Length >= 2)
        {
            Idle = (parts[1] == "1");
            SendRaw("EVENT|idle|" + (Idle ? 1 : 0));
        }
        else if (cmd == "PEER_MELEE" && parts.Length >= 2)
        {
            // v0.9.2: medspilleren slo akkurat nå – spill samme slag med en gang (lett/tungt)
            if (_guestPed != null && _guestPed.Exists() && !_guestPed.IsInVehicle())
            {
                _guestPed.Heading = _targetHeading;
                _lastMeleeTaskMs = Game.GameTime;
                PlayMeleeAnim(_guestPed, _targetWeapon, parts[1] == "1");
            }
        }
        else if (cmd == "SYNC_VDMG" && parts.Length >= 2)
        {
            HandleVehicleDamage(parts[1]);
        }
        else if (cmd == "SYNC_VEHS" && parts.Length >= 2)
        {
            // Ignorer speil-kjøretøy mens 1:1 cutscene spiller på Gjesten -> 0 % doble biler i cutscenen!
            if (_guestCsState == 0) HandleSyncVehs(parts[1]);
        }
        else if (cmd == "DIAG")
        {
            SendDiag();
        }
        else if (cmd == "PVP_HIT" && parts.Length >= 2)
        {
            int dmg;
            if (int.TryParse(parts[1], out dmg) && dmg > 0)
            {
                Ped me = Game.Player.Character;
                if (me != null && me.Exists() && me.IsAlive)
                {
                    bool alreadyHitByBullet = (_guestPed != null && _guestPed.Exists()
                        && Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY, me.Handle, _guestPed.Handle, true));
                    if (alreadyHitByBullet)
                    {
                        Function.Call(Hash.CLEAR_ENTITY_LAST_DAMAGE_ENTITY, me.Handle);
                    }
                    else
                    {
                        Function.Call(Hash.APPLY_DAMAGE_TO_PED, me.Handle, dmg, true);
                        // v0.9.2: reaksjon på slag – tungt slag/våpen = kort i bakken, lett slag = smertelyd
                        string kind = parts.Length >= 3 ? parts[2] : "";
                        if (kind == "heavy" && !me.IsInVehicle())
                            Function.Call(Hash.SET_PED_TO_RAGDOLL, me.Handle, 700, 900, 0, false, false, false);
                        else if (kind == "light")
                            Function.Call(Hash.PLAY_PAIN, me.Handle, 6, 0f, 0);
                    }
                }
            }
        }
        else if (cmd == "WANTED_REQ" && parts.Length >= 5 && !_isGuestRole)
        {
            // Gjesten begikk lovbrudd eller skjøt politi -> eskaler WantedLevel på Verten så politiet jakter begge!
            int reqWl;
            float wx, wy, wz;
            if (int.TryParse(parts[1], out reqWl) && TryF(parts[2], out wx) && TryF(parts[3], out wy) && TryF(parts[4], out wz))
            {
                reqWl = Math.Min(5, Math.Max(1, reqWl));
                if (Game.Player.WantedLevel < reqWl)
                {
                    Game.Player.WantedLevel = reqWl;
                    Function.Call(Hash.SET_PLAYER_WANTED_CENTRE_POSITION, Game.Player.Handle, wx, wy, wz);
                    _lastWorldSendMs = 0;
                }
            }
        }
        else if (cmd == "SYNC_WORLD" && parts.Length >= 5)
        {
            // SYNC_WORLD|hour|minute|weatherEnum|wantedLevel (fra Vert -> Gjest)
            int hr, mn, wt, wl;
            if (int.TryParse(parts[1], out hr) && int.TryParse(parts[2], out mn) &&
                int.TryParse(parts[3], out wt) && int.TryParse(parts[4], out wl))
            {
                try
                {
                    int sc = 0;
                    if (parts.Length >= 9) int.TryParse(parts[8], out sc);
                    int curH = Function.Call<int>(Hash.GET_CLOCK_HOURS);
                    int curM = Function.Call<int>(Hash.GET_CLOCK_MINUTES);
                    int curS = Function.Call<int>(Hash.GET_CLOCK_SECONDS);
                    // v0.9.2: avvik i spillsekunder, regnet riktig over midnatt; rett hvis mer enn 1 spillminutt
                    int diff = Math.Abs((curH * 3600 + curM * 60 + curS) - (hr * 3600 + mn * 60 + sc));
                    diff = Math.Min(diff, 86400 - diff);
                    if (diff > 60)
                    {
                        Function.Call(Hash.SET_CLOCK_TIME, hr, mn, sc);
                    }
                    int hostInt = 0;
                    if (parts.Length >= 10 && int.TryParse(parts[9], out hostInt) && hostInt != 0)
                    {
                        // v0.9.2: gjesten (100 %-lagring) har ofte story-interiører slått av (f.eks. Franklins tantes hus).
                        // Slå på og last interiøret verten står i.
                        if (hostInt != _lastIntApplied || Game.GameTime - _lastIntApplyMs > 10000)
                        {
                            _lastIntApplied = hostInt;
                            _lastIntApplyMs = Game.GameTime;
                            Function.Call(Hash.DISABLE_INTERIOR, hostInt, false);
                            Function.Call(Hash.CAP_INTERIOR, hostInt, false);
                            Function.Call(Hash.PIN_INTERIOR_IN_MEMORY, hostInt);
                            if (Function.Call<bool>(Hash.IS_INTERIOR_READY, hostInt))
                                Function.Call(Hash.REFRESH_INTERIOR, hostInt);
                            SendRaw("EVENT|interior_enabled|" + hostInt);
                        }
                    }
                    uint wPrev = 0, wNext = 0; float wPct = 0f;
                    bool hasState = parts.Length >= 8 && uint.TryParse(parts[5], out wPrev) && uint.TryParse(parts[6], out wNext)
                        && TryF(parts[7], out wPct) && wPrev != 0 && wNext != 0;
                    if (hasState)
                    {
                        // v0.9.2: samme værovergang som verten, satt hver gang (gjestens egen værsyklus overstyres)
                        Function.Call((Hash)0x578C752848ECFA0CUL, wPrev, wNext, Math.Max(0f, Math.Min(1f, wPct)));
                        _lastSyncedWeather = wt;
                    }
                    else if (Enum.IsDefined(typeof(Weather), wt) && wt >= 0 && (wt != _lastSyncedWeather || (int)World.Weather != wt))
                    {
                        // eldre vert (v0.9.2): sett værtypen igjen også når gjestens eget vær har drevet bort
                        _lastSyncedWeather = wt;
                        World.Weather = (Weather)wt;
                    }
                    if (wl >= 0 && wl <= 5)
                    {
                        _hostAuthoritativeWanted = wl;
                        _lastWantedAcceptMs = Game.GameTime;
                        if (Game.Player.WantedLevel != wl)
                        {
                            Game.Player.WantedLevel = wl;
                        }
                    }
                }
                catch { }
            }
        }
        else if (cmd == "SYNC_INV" && parts.Length >= 3)
        {
            // SYNC_INV|money|hash:ammo,hash:ammo,... (fra Vert -> Gjest)
            string sig = parts[1] + "|" + parts[2];
            if (sig == _lastSyncedInv) return;
            _lastSyncedInv = sig;
            int money;
            if (int.TryParse(parts[1], out money) && Game.Player.Money != money)
            {
                Game.Player.Money = money;
            }
            Ped me = Game.Player.Character;
            if (me != null && me.Exists() && !string.IsNullOrEmpty(parts[2]))
            {
                uint firstGiven = 0;
                foreach (string item in parts[2].Split(','))
                {
                    string[] ha = item.Split(':');
                    uint wh;
                    int ammo;
                    if (ha.Length == 2 && uint.TryParse(ha[0], out wh) && int.TryParse(ha[1], out ammo))
                    {
                        if (firstGiven == 0) firstGiven = wh;
                        if (!Function.Call<bool>(Hash.HAS_PED_GOT_WEAPON, me.Handle, wh, false))
                        {
                            Function.Call(Hash.GIVE_WEAPON_TO_PED, me.Handle, wh, Math.Max(ammo, 120), false, false);
                        }
                        else if (ammo > 0)
                        {
                            Function.Call(Hash.SET_PED_AMMO, me.Handle, wh, ammo);
                        }
                    }
                }
                // Gi kun våpnene i inventaret – aldri tving gjesten til å bytte våpen når verten bytter!
                if (!_initialWeaponEquipped)
                {
                    uint curWp = 0;
                    try { curWp = (uint)me.Weapons.Current.Hash; } catch { }
                    if ((curWp == 0 || curWp == (uint)WeaponHash.Unarmed) && firstGiven != 0)
                    {
                        Function.Call(Hash.SET_CURRENT_PED_WEAPON, me.Handle, firstGiven, true);
                        _initialWeaponEquipped = true;
                    }
                    else if (curWp != 0 && curWp != (uint)WeaponHash.Unarmed)
                    {
                        _initialWeaponEquipped = true;
                    }
                }
            }
        }
    }

    private void ApplySelfModel(string mdl)
    {
        if (string.IsNullOrEmpty(mdl)) return;
        try
        {
            Ped me = Game.Player.Character;
            if (me != null && me.Exists() && me.Model.Hash == _wantedSelfHash)
            {
                Function.Call(Hash.SET_PED_DEFAULT_COMPONENT_VARIATION, me.Handle);
                me.IsVisible = true;
                me.Opacity = 255;
                SendRaw("EVENT|self_model_already|" + mdl + "|vis=" + (me.IsVisible ? "1" : "0"));
                return;
            }
            Model m = new Model(mdl);
            if (!m.IsValid)
            {
                SendRaw("ERR|self_model|invalid_" + mdl);
                return;
            }
            m.Request(2500);
            if (!m.IsLoaded)
            {
                SendRaw("ERR|self_model|not_loaded_" + mdl);
                return;
            }
            Game.Player.ChangeModel(m);
            m.MarkAsNoLongerNeeded();
            Ped newMe = Game.Player.Character;
            if (newMe != null && newMe.Exists())
            {
                Function.Call(Hash.SET_PED_DEFAULT_COMPONENT_VARIATION, newMe.Handle);
                newMe.IsVisible = true;
                newMe.Opacity = 255;
            }
            _lastSyncedInv = "";
            SendRaw("EVENT|self_model|" + mdl);
        }
        catch (Exception ex)
        {
            SendRaw("ERR|self_model|" + ex.Message);
        }
    }

    private static bool TryF(string s, out float v)
    {
        return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    private static float AngleDiff(float a, float b)
    {
        float d = (b - a) % 360.0f;
        if (d > 180.0f) d -= 360.0f;
        else if (d < -180.0f) d += 360.0f;
        return d;
    }

    private static float LerpAngle(float a, float b, float t)
    {
        float res = a + AngleDiff(a, b) * t;
        if (res < 0.0f) res += 360.0f;
        else if (res >= 360.0f) res -= 360.0f;
        return res;
    }

    private static bool IsPolicePed(Ped p)
    {
        try
        {
            int pt = Function.Call<int>(Hash.GET_PED_TYPE, p.Handle);
            return (pt == 6 || pt == 27 || pt == 29); // COP, SWAT, ARMY
        }
        catch { return false; }
    }

    private static bool IsEmergencyOrHeli(Vehicle v)
    {
        try
        {
            return v.IsSirenActive || v.ClassType == VehicleClass.Emergency || v.Model.IsHelicopter;
        }
        catch { return false; }
    }

    private static int ReadVehicleFlags(Vehicle v, bool checkBrakeControl)
    {
        int vf = v.IsSirenActive ? 1 : 0;
        try
        {
            if (Function.Call<bool>(Hash.IS_HORN_ACTIVE, v.Handle)) vf |= 2;
            OutputArgument oLo = new OutputArgument(), oHb = new OutputArgument();
            if (Function.Call<bool>(Hash.GET_VEHICLE_LIGHTS_STATE, v.Handle, oLo, oHb))
            {
                if (oLo.GetResult<int>() != 0) vf |= 4;
                if (oHb.GetResult<int>() != 0) vf |= 8;
            }
            if (checkBrakeControl && Game.IsControlPressed(Control.VehicleBrake)) vf |= 16;
        }
        catch { }
        return vf;
    }

    private static void ApplyVehicleFlags(Vehicle v, int vf)
    {
        try
        {
            bool wantSiren = (vf & 1) != 0;
            if (v.IsSirenActive != wantSiren) v.IsSirenActive = wantSiren;
            if ((vf & 2) != 0)
            {
                Function.Call(Hash.START_VEHICLE_HORN, v.Handle, 120, 0, false);
            }
            Function.Call(Hash.SET_VEHICLE_BRAKE_LIGHTS, v.Handle, (vf & 16) != 0);
            CoopFrameOverrides.SetBrake(v.Handle, (vf & 16) != 0);   // v0.9.2: holdes hvert bilde av CoopFrameOverrides
            if (v.Model.IsHelicopter && (v.HeightAboveGround > 1.2f || v.Velocity.Length() > 0.5f))
            {
                Function.Call(Hash.SET_HELI_BLADES_FULL_SPEED, v.Handle);
            }
        }
        catch { }
    }

    private static string ReadOutfit(Ped p)
    {
        StringBuilder sb = new StringBuilder(128);
        for (int slot = 0; slot < 12; slot++)
        {
            int d = Function.Call<int>(Hash.GET_PED_DRAWABLE_VARIATION, p.Handle, slot);
            int t = Function.Call<int>(Hash.GET_PED_TEXTURE_VARIATION, p.Handle, slot);
            if (slot > 0) sb.Append(',');
            sb.Append(d).Append(':').Append(t);
        }
        // Inkluder også Prop 0 (hatt/hjelm/maske) og Prop 1 (briller/maske)
        for (int ps = 0; ps < 2; ps++)
        {
            int pd = Function.Call<int>(Hash.GET_PED_PROP_INDEX, p.Handle, ps);
            int pt = pd >= 0 ? Function.Call<int>(Hash.GET_PED_PROP_TEXTURE_INDEX, p.Handle, ps) : 0;
            sb.Append(',').Append(pd).Append(':').Append(pt);
        }
        return sb.ToString();
    }

    private static void ApplyOutfit(Ped p, string outfit)
    {
        if (string.IsNullOrEmpty(outfit) || outfit.StartsWith("0:0,0:0,0:0,0:0,0:0,0:0,0:0,0:0,0:0,0:0,0:0,0:0")) return;
        string[] items = outfit.Split(',');
        for (int slot = 0; slot < items.Length && slot < 12; slot++)
        {
            string[] dt = items[slot].Split(':');
            int d, t;
            if (dt.Length == 2 && int.TryParse(dt[0], out d) && int.TryParse(dt[1], out t))
            {
                Function.Call(Hash.SET_PED_COMPONENT_VARIATION, p.Handle, slot, d, t, 0);
            }
        }
        for (int ps = 0; ps < 2 && (12 + ps) < items.Length; ps++)
        {
            string[] dt = items[12 + ps].Split(':');
            int pd, pt;
            if (dt.Length == 2 && int.TryParse(dt[0], out pd) && int.TryParse(dt[1], out pt))
            {
                if (pd >= 0)
                {
                    Function.Call(Hash.SET_PED_PROP_INDEX, p.Handle, ps, pd, pt, true);
                }
                else
                {
                    Function.Call(Hash.CLEAR_PED_PROP, p.Handle, ps);
                }
            }
        }
    }

    private static void PlayCoverAnim(Ped p, bool highCover, bool faceLeft, uint weaponHash)
    {
        bool twoHand = (weaponHash != 0 && weaponHash != (uint)WeaponHash.Unarmed
                        && weaponHash != (uint)WeaponHash.Pistol && weaponHash != (uint)WeaponHash.CombatPistol
                        && weaponHash != (uint)WeaponHash.MicroSMG);
        string anim = faceLeft ? "idle_l_corner" : "idle_r_corner";
        string[] dicts = highCover
            ? (twoHand
                ? new string[] { "cover@idles@2h@high@_a", "cover@idles@1h@high@_a", "cover@idles@unarmed@high@_a" }
                : new string[] { "cover@idles@1h@high@_a", "cover@idles@unarmed@high@_a" })
            : (twoHand
                ? new string[] { "cover@idles@2h@low@_a", "cover@idles@1h@low@_a", "cover@idles@unarmed@low@_a" }
                : new string[] { "cover@idles@1h@low@_a", "cover@idles@unarmed@low@_a" });

        foreach (string dc in dicts)
        {
            if (Function.Call<bool>(Hash.DOES_ANIM_DICT_EXIST, dc))
            {
                Function.Call(Hash.REQUEST_ANIM_DICT, dc);
                int t0 = Game.GameTime;
                while (!Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, dc) && Game.GameTime - t0 < 250)
                {
                    Script.Yield();
                }
                if (Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, dc))
                {
                    Function.Call(Hash.TASK_PLAY_ANIM, p.Handle, dc, anim, 4.0f, -4.0f, -1, 1, 0.0f, false, false, false);
                    return;
                }
            }
        }
        if (!highCover && Function.Call<bool>(Hash.DOES_ANIM_DICT_EXIST, "amb@medic@standing@kneel@base"))
        {
            Function.Call(Hash.REQUEST_ANIM_DICT, "amb@medic@standing@kneel@base");
            if (Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, "amb@medic@standing@kneel@base"))
            {
                Function.Call(Hash.TASK_PLAY_ANIM, p.Handle, "amb@medic@standing@kneel@base", "base", 4.0f, -4.0f, -1, 1, 0.0f, false, false, false);
            }
        }
    }

    private static bool IsBarrierOrDoorProp(Prop p)
    {
        try
        {
            OutputArgument omin = new OutputArgument();
            OutputArgument omax = new OutputArgument();
            Function.Call(Hash.GET_MODEL_DIMENSIONS, p.Model.Hash, omin, omax);
            Vector3 sz = omax.GetResult<Vector3>() - omin.GetResult<Vector3>();
            return (sz.Z >= 1.20f || sz.X >= 1.20f || sz.Y >= 1.20f);
        }
        catch { return false; }
    }

    private static bool TryGetDoorInfo(Prop p, out int doorState, out float openRatio)
    {
        doorState = 0;
        openRatio = 0f;
        try
        {
            Vector3 pos = p.Position;
            OutputArgument outDoorHash = new OutputArgument();
            if (Function.Call<bool>(Hash.DOOR_SYSTEM_FIND_EXISTING_DOOR, pos.X, pos.Y, pos.Z, p.Model.Hash, outDoorHash))
            {
                int dh = outDoorHash.GetResult<int>();
                doorState = Function.Call<int>(Hash.DOOR_SYSTEM_GET_DOOR_STATE, dh);
                openRatio = Function.Call<float>(Hash.DOOR_SYSTEM_GET_OPEN_RATIO, dh);
                return true;
            }
        }
        catch { }
        return false;
    }

    private void HandleSyncProps(string payload)
    {
        // 1) Synk ekte DOOR_SYSTEM-dører  2) Fjern sprengte/opprullede porter (>=1.65m) innen 7.5 m fra Verten som ikke lenger står på samme plass hos Verten!
        if (!_isGuestRole || !_hasTarget || string.IsNullOrEmpty(payload)) return;
        Ped me = Game.Player.Character;
        if (me == null || !me.Exists() || me.Position.DistanceTo(_targetPos) > 35.0f) return;

        List<int> barHashes = new List<int>();
        List<Vector3> barPos = new List<Vector3>();
        List<Vector3> barRots = new List<Vector3>();

        string[] rows = payload.Split(';');
        for (int i = 0; i < rows.Length; i++)
        {
            string row = rows[i];
            if (string.IsNullOrEmpty(row)) continue;
            if (row.StartsWith("IPL:"))
            {
                int mask;
                if (int.TryParse(row.Substring(4), out mask))
                {
                    for (int k = 0; k < SYNCED_IPLS.Length; k++)
                    {
                        bool wantOn = ((mask >> k) & 1) != 0;
                        bool curOn = Function.Call<bool>(Hash.IS_IPL_ACTIVE, SYNCED_IPLS[k]);
                        if (wantOn != curOn)
                        {
                            if (wantOn) Function.Call(Hash.REQUEST_IPL, SYNCED_IPLS[k]);
                            else Function.Call(Hash.REMOVE_IPL, SYNCED_IPLS[k]);
                        }
                    }
                }
                continue;
            }
            if (row.StartsWith("RF:"))
            {
                // Rayfire sprengbare kart-objekter (f.eks. des_vaultdoor001 i Prologue)
                string[] rf = row.Substring(3).Split(',');
                float rx, ry, rz;
                int rState;
                if (rf.Length >= 5 && TryF(rf[1], out rx) && TryF(rf[2], out ry) && TryF(rf[3], out rz) && int.TryParse(rf[4], out rState))
                {
                    int rfObj = Function.Call<int>(Hash.GET_RAYFIRE_MAP_OBJECT, rx, ry, rz, 10.0f, rf[0]);
                    if (rfObj != 0 && Function.Call<bool>(Hash.DOES_RAYFIRE_MAP_OBJECT_EXIST, rfObj))
                    {
                        int curRf = Function.Call<int>(Hash.GET_STATE_OF_RAYFIRE_MAP_OBJECT, rfObj);
                        if (curRf != rState && rState >= 6)
                        {
                            Function.Call(Hash.SET_STATE_OF_RAYFIRE_MAP_OBJECT, rfObj, rState);
                        }
                    }
                }
                continue;
            }
            if (row.StartsWith("B:"))
            {
                string[] bf = row.Substring(2).Split(',');
                int bmh;
                float bx, by, bz, brx = 0f, bry = 0f, brz = 0f;
                if (bf.Length >= 4 && int.TryParse(bf[0], out bmh) && TryF(bf[1], out bx) && TryF(bf[2], out by) && TryF(bf[3], out bz))
                {
                    if (bf.Length >= 7)
                    {
                        TryF(bf[4], out brx);
                        TryF(bf[5], out bry);
                        TryF(bf[6], out brz);
                    }
                    else if (bf.Length >= 5)
                    {
                        TryF(bf[4], out brz);
                    }
                    barHashes.Add(bmh);
                    barPos.Add(new Vector3(bx, by, bz));
                    barRots.Add(new Vector3(brx, bry, brz));
                }
                continue;
            }
            string[] f = row.Split(',');
            if (f.Length < 6) continue;
            int mh, dState;
            float dx, dy, dz, ratio;
            if (!int.TryParse(f[0], out mh) ||
                !TryF(f[1], out dx) || !TryF(f[2], out dy) || !TryF(f[3], out dz) ||
                !int.TryParse(f[4], out dState) || !TryF(f[5], out ratio))
            {
                continue;
            }
            try
            {
                bool hostUnlocked = (dState == 0 || dState == 2 || Math.Abs(ratio) > 0.08f);
                OutputArgument outDh = new OutputArgument();
                if (Function.Call<bool>(Hash.DOOR_SYSTEM_FIND_EXISTING_DOOR, dx, dy, dz, mh, outDh))
                {
                    int dh = outDh.GetResult<int>();
                    int curState = Function.Call<int>(Hash.DOOR_SYSTEM_GET_DOOR_STATE, dh);
                    float curRatio = Function.Call<float>(Hash.DOOR_SYSTEM_GET_OPEN_RATIO, dh);
                    if (curState != dState)
                    {
                        Function.Call(Hash.DOOR_SYSTEM_SET_DOOR_STATE, dh, dState, false, true);
                    }
                    if (Math.Abs(curRatio - ratio) > 0.15f && Math.Abs(ratio) > 0.10f)
                    {
                        Function.Call(Hash.DOOR_SYSTEM_SET_OPEN_RATIO, dh, ratio, false, true);
                    }
                }
                else if (hostUnlocked)
                {
                    Function.Call(Hash.SET_STATE_OF_CLOSEST_DOOR_OF_TYPE, mh, dx, dy, dz, false, ratio, false);
                }
            }
            catch { }
        }

        // Sjekk dører/porter (>=1.20m) innen 15.0 m fra Verten (mot Vertens 25 m liste = 10 m sikkerhetsmargin -> 0 % flimring!)
        if (barHashes.Count > 0)
        {
            Prop[] nearLocal = World.GetNearbyProps(_targetPos, 15.0f);
            foreach (Prop lp in nearLocal)
            {
                if (lp == null || !lp.Exists() || _hiddenProps.ContainsKey(lp.Handle) || !IsBarrierOrDoorProp(lp)) continue;
                int lmh = lp.Model.Hash;
                Vector3 lpos = lp.Position;
                bool stillBlockingOnHost = false;
                for (int j = 0; j < barHashes.Count; j++)
                {
                    if (barHashes[j] == lmh && lpos.DistanceTo(barPos[j]) < 1.15f)
                    {
                        stillBlockingOnHost = true;
                        Vector3 lr = lp.Rotation;
                        Vector3 hr = barRots[j];
                        float dx = Math.Abs(lr.X - hr.X); if (dx > 180f) dx = 360f - dx;
                        float dy = Math.Abs(lr.Y - hr.Y); if (dy > 180f) dy = 360f - dy;
                        float dz = Math.Abs(lr.Z - hr.Z); if (dz > 180f) dz = 360f - dz;
                        if (dx > 15.0f || dy > 15.0f || dz > 15.0f)
                        {
                            // Svingdør eller vippeport (X/Y/Z-rotasjon endret på Verten): sett eksakt samme 3D-rotasjon!
                            Function.Call(Hash.SET_STATE_OF_CLOSEST_DOOR_OF_TYPE, lmh, lpos.X, lpos.Y, lpos.Z, false, 0f, false);
                            lp.IsPositionFrozen = false;
                            lp.Rotation = hr;
                            lp.IsPositionFrozen = true;
                        }
                        break;
                    }
                }
                if (!stillBlockingOnHost)
                {
                    // Porten/døren er sprengt bort, rullet opp i taket eller svingt åpen på Verten -> fjern blokkeringen permanent på Gjesten!
                    lp.IsVisible = false;
                    Function.Call(Hash.SET_ENTITY_COLLISION, lp.Handle, false, false);
                    Function.Call(Hash.SET_ENTITY_COMPLETELY_DISABLE_COLLISION, lp.Handle, false, false);
                    _hiddenProps[lp.Handle] = lp;
                }
            }
        }
    }

    private void HandleSyncVehs(string payload)
    {
        if (!_isGuestRole || string.IsNullOrEmpty(payload)) return;
        if (_guestCsState != 0) return;   // v0.9.2: ingen speilbiler under mellomsekvens
        Ped me = Game.Player.Character;
        if (me == null || !me.Exists()) return;
        if (!_startupTasksCleared)
        {
            _startupTasksCleared = true;
            Function.Call(Hash.SET_PLAYER_CONTROL, Game.Player.Handle, true, 0);
            if (!me.IsInVehicle())
            {
                Function.Call(Hash.CLEAR_PED_TASKS_IMMEDIATELY, me.Handle);
            }
        }

        // Fullfør kollisjonssikker teleportering: hold spiller frosset over bakken til bakkekollisjonen er lastet!
        if (_safeTpWaitingGround)
        {
            Function.Call(Hash.REQUEST_COLLISION_AT_COORD, _safeTpPos.X, _safeTpPos.Y, _safeTpPos.Z);
            bool loaded = Function.Call<bool>(Hash.HAS_COLLISION_LOADED_AROUND_ENTITY, me.Handle);
            if (loaded || Game.GameTime - _safeTpStartMs > 1800)
            {
                OutputArgument outGz = new OutputArgument();
                if (Function.Call<bool>(Hash.GET_GROUND_Z_FOR_3D_COORD, _safeTpPos.X, _safeTpPos.Y, _safeTpPos.Z + 8.0f, outGz, false))
                {
                    float gz = outGz.GetResult<float>();
                    if (gz > -150.0f) me.PositionNoOffset = new Vector3(_safeTpPos.X, _safeTpPos.Y, gz + 1.0f);
                }
                me.IsPositionFrozen = false;
                _safeTpWaitingGround = false;
            }
            else
            {
                me.PositionNoOffset = _safeTpPos;
            }
        }
        int now = Game.GameTime;

        string[] rows = payload.Split(';');
        for (int i = 0; i < rows.Length; i++)
        {
            string row = rows[i];
            if (string.IsNullOrEmpty(row)) continue;
            string[] f = row.Split(',');
            if (f.Length < 17) continue;

            int vId, mHash, c1, c2, srn, dead, drvMdl = 0, passMdl = 0;
            float x, y, z, qx, qy, qz, qw, vx, vy, vz, steer;
            if (!int.TryParse(f[0], out vId) || !int.TryParse(f[1], out mHash) ||
                !TryF(f[2], out x) || !TryF(f[3], out y) || !TryF(f[4], out z) ||
                !TryF(f[5], out qx) || !TryF(f[6], out qy) || !TryF(f[7], out qz) || !TryF(f[8], out qw) ||
                !TryF(f[9], out vx) || !TryF(f[10], out vy) || !TryF(f[11], out vz) ||
                !TryF(f[12], out steer) || !int.TryParse(f[13], out c1) || !int.TryParse(f[14], out c2) ||
                !int.TryParse(f[15], out srn) || !int.TryParse(f[16], out dead))
            {
                continue;
            }
            if (f.Length >= 19)
            {
                int.TryParse(f[17], out drvMdl);
                int.TryParse(f[18], out passMdl);
            }

            Vector3 vpos = new Vector3(x, y, z);
            Vehicle sv = null;
            if (_syncedVehs.TryGetValue(vId, out sv) && (sv == null || !sv.Exists()))
            {
                _syncedVehs.Remove(vId);
                sv = null;
            }

            if (sv == null && dead == 0 && me.Position.DistanceTo(vpos) < 550.0f)
            {
                if (_syncedVehs.Count >= 95)
                {
                    int farthestId = 0;
                    float farthestD = -1f;
                    foreach (KeyValuePair<int, Vehicle> ekv in _syncedVehs)
                    {
                        if (ekv.Value != null && ekv.Value.Exists() && ekv.Value != _guestVeh && !me.IsInVehicle(ekv.Value))
                        {
                            float fd = ekv.Value.Position.DistanceTo(me.Position);
                            if (fd > farthestD) { farthestD = fd; farthestId = ekv.Key; }
                        }
                    }
                    if (farthestId != 0 && farthestD > me.Position.DistanceTo(vpos))
                    {
                        DeleteSyncedVehicleAndCrew(farthestId);
                    }
                }
                // v0.9.2: ta over en eksisterende lik bil i stedet for å lage en ny. Etter mod-omstart blir bilen spilleren sitter i
                // stående igjen (kan ikke slettes med spilleren inni), og da ble det to like biler (to Franklin-biler).
                Vehicle adopt = null;
                if (me.IsInVehicle() && me.CurrentVehicle != null && me.CurrentVehicle.Exists()
                    && me.CurrentVehicle.Model.Hash == mHash && me.CurrentVehicle.Position.DistanceTo(vpos) < 15.0f
                    && !_syncedVehs.ContainsValue(me.CurrentVehicle))
                {
                    adopt = me.CurrentVehicle;
                }
                if (adopt == null)
                {
                    foreach (Vehicle lv in World.GetNearbyVehicles(vpos, 5.0f))
                    {
                        if (lv != null && lv.Exists() && lv.Model.Hash == mHash && !_syncedVehs.ContainsValue(lv) && lv != _guestVeh)
                        {
                            adopt = lv;
                            break;
                        }
                    }
                }
                if (adopt != null)
                {
                    adopt.IsPersistent = true;
                    Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED, adopt.Handle, 1);
                    _syncedVehs[vId] = adopt;
                    SendRaw("EVENT|veh_adopted|" + vId);
                }
                else if (_syncedVehs.Count < 95)
                {
                    try
                    {
                        Model vm = new Model(mHash);
                        if (vm.IsValid)
                        {
                            if (!vm.IsLoaded)
                            {
                                vm.Request();
                            }
                            else
                            {
                                float hd = (float)(Math.Atan2(2.0 * (qw * qz + qx * qy), 1.0 - 2.0 * (qy * qy + qz * qz)) * (180.0 / Math.PI));
                                if (hd < 0f) hd += 360f;
                                sv = World.CreateVehicle(vm, vpos, hd);
                                vm.MarkAsNoLongerNeeded();
                                if (sv != null && sv.Exists())
                                {
                                    sv.IsPersistent = true;
                                    Function.Call(Hash.SET_VEHICLE_COLOURS, sv.Handle, c1, c2);
                                    Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED, sv.Handle, 1);
                                    Function.Call(Hash.SET_ENTITY_QUATERNION, sv.Handle, qx, qy, qz, qw);
                                    if (drvMdl != 0 || (vx * vx + vy * vy) > 0.2f)
                                    {
                                        Function.Call(Hash.SET_VEHICLE_ENGINE_ON, sv.Handle, true, true, false);
                                    }
                                    _syncedVehs[vId] = sv;
                                }
                            }
                        }
                    }
                    catch { }
                }
            }

            if (sv == null || !sv.Exists()) continue;

            // Opprett sjåfør/passasjer ATOMISK RETT INNI BILEN med CREATE_PED_INSIDE_VEHICLE -> 0 % sjanse for å spawne oppå taket!
            EnsureVehicleSeatPed(vId, sv, VehicleSeat.Driver, -1, drvMdl, _syncedVehDrivers, me);
            EnsureVehicleSeatPed(vId, sv, VehicleSeat.Passenger, 0, passMdl, _syncedVehPass, me);

            VehTarget vt;
            vt.ModelHash = mHash;
            vt.Pos = vpos;
            vt.Qx = qx; vt.Qy = qy; vt.Qz = qz; vt.Qw = qw;
            vt.Vel = new Vector3(vx, vy, vz);
            vt.Steer = steer;
            vt.C1 = c1; vt.C2 = c2;
            vt.VFlags = srn;
            vt.Dead = (dead == 1);
            vt.DrvModel = drvMdl;
            vt.PassModel = passMdl;
            vt.SeenMs = now;
            _vehTargets[vId] = vt;
        }
    }

    private void EnsureVehicleSeatPed(int vId, Vehicle sv, VehicleSeat seatEnum, int seatIdx, int wantMdl, Dictionary<int, Ped> dict, Ped me)
    {
        Ped cur = null;
        dict.TryGetValue(vId, out cur);
        if (wantMdl == 0)
        {
            if (cur != null)
            {
                try { if (cur.Exists()) cur.Delete(); } catch { }
                dict.Remove(vId);
            }
            return;
        }
        if (cur != null && cur.Exists() && cur.IsInVehicle(sv) && cur.Model.Hash == wantMdl)
        {
            return; // Allerede riktig sjåfør/passasjer i setet!
        }
        if (cur != null)
        {
            try { if (cur.Exists()) cur.Delete(); } catch { }
            dict.Remove(vId);
        }
        Ped existingSeat = sv.GetPedOnSeat(seatEnum);
        if (existingSeat != null && existingSeat.Exists())
        {
            if (existingSeat == me || existingSeat == _guestPed || _syncedNpcs.ContainsValue(existingSeat))
            {
                return; // Setet brukes av spiller eller oppdrags-NPC
            }
            try { existingSeat.Delete(); } catch { }
        }
        try
        {
            Model dm = new Model(wantMdl);
            if (!dm.IsValid) return;
            if (!dm.IsLoaded)
            {
                dm.Request();
                return;
            }
            int ph = Function.Call<int>(Hash.CREATE_PED_INSIDE_VEHICLE, sv.Handle, 26, wantMdl, seatIdx, false, false);
            dm.MarkAsNoLongerNeeded();
            if (ph != 0)
            {
                Ped np = Entity.FromHandle(ph) as Ped;
                if (np != null && np.Exists())
                {
                    np.BlockPermanentEvents = true;
                    np.CanRagdoll = false;
                    np.IsPersistent = true;
                    Function.Call(Hash.TASK_SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, np.Handle, true);
                    dict[vId] = np;
                }
            }
        }
        catch { }
    }

    private void DeleteSyncedVehicleAndCrew(int vId)
    {
        Ped dp;
        if (_syncedVehDrivers.TryGetValue(vId, out dp))
        {
            try { if (dp != null && dp.Exists()) dp.Delete(); } catch { }
            _syncedVehDrivers.Remove(vId);
        }
        Ped pp;
        if (_syncedVehPass.TryGetValue(vId, out pp))
        {
            try { if (pp != null && pp.Exists()) pp.Delete(); } catch { }
            _syncedVehPass.Remove(vId);
        }
        Vehicle dv;
        Ped me = null;
        try { me = Game.Player.Character; } catch { }
        if (_syncedVehs.TryGetValue(vId, out dv) && dv != null && dv.Exists()
            && dv != _guestVeh && (me == null || !me.IsInVehicle(dv)))
        {
            try { dv.Delete(); } catch { }
        }
        _syncedVehs.Remove(vId);
        _vehTargets.Remove(vId);
    }

    private void UpdateSyncedVehsTick()
    {
        if (!_isGuestRole || _syncedVehs.Count == 0) return;
        Ped me = Game.Player.Character;
        int now = Game.GameTime;
        List<int> stale = null;

        foreach (KeyValuePair<int, Vehicle> kv in _syncedVehs)
        {
            int vId = kv.Key;
            Vehicle sv = kv.Value;
            VehTarget vt;
            // Hvis dette er bilen vi eller motspilleren bruker akkurat nå, styrer PEER-kanalen (40 Hz) den eksklusivt og den skal ALDRI slettes som stale!
            if (sv != null && sv.Exists() && (sv == _guestVeh || (me != null && me.IsInVehicle(sv))))
            {
                if (_vehTargets.TryGetValue(vId, out vt))
                {
                    vt.SeenMs = now;
                    _vehTargets[vId] = vt;
                }
                continue;
            }
            // Romslig 6000 ms timeout for rullerende horisont-biler -> 0 % blinking/pop-in, men slett hvis > 560 m unna!
            if (sv == null || !sv.Exists() || !_vehTargets.TryGetValue(vId, out vt)
                || now - vt.SeenMs > 6000
                || (me != null && sv.Position.DistanceTo(me.Position) > 560.0f))
            {
                if (stale == null) stale = new List<int>();
                stale.Add(vId);
                continue;
            }
            if (vt.Dead)
            {
                if (sv.IsAlive) sv.Explode();
                continue;
            }
            Function.Call(Hash.REQUEST_COLLISION_AT_COORD, vt.Pos.X, vt.Pos.Y, vt.Pos.Z);
            if (!Function.Call<bool>(Hash.HAS_COLLISION_LOADED_AROUND_ENTITY, sv.Handle))
            {
                sv.IsPositionFrozen = true;
                sv.PositionNoOffset = vt.Pos;
                Function.Call(Hash.SET_ENTITY_QUATERNION, sv.Handle, vt.Qx, vt.Qy, vt.Qz, vt.Qw);
                continue;
            }
            if (sv.IsPositionFrozen) sv.IsPositionFrozen = false;

            float dt = Math.Min(0.45f, Math.Max(0f, (now - vt.SeenMs) / 1000.0f));
            Vector3 predPos = vt.Pos + vt.Vel * dt;
            float d = sv.Position.DistanceTo(predPos);
            float spd = vt.Vel.Length();
            Quaternion targetQ = new Quaternion(vt.Qx, vt.Qy, vt.Qz, vt.Qw);
            if (d > 9.0f)
            {
                sv.PositionNoOffset = predPos;
                sv.Velocity = vt.Vel;
                sv.Quaternion = targetQ;
            }
            else if (spd > 0.8f)
            {
                // Silkemyk Dead-Reckoning med Quaternion.Slerp + Velocity -> rullende hjul, motorlyd og 0 % rykking!
                sv.Velocity = vt.Vel + (predPos - sv.Position) * 3.8f;
                sv.Quaternion = Quaternion.Slerp(sv.Quaternion, targetQ, 0.28f);
            }
            else
            {
                if (d > 0.25f) sv.PositionNoOffset = Vector3.Lerp(sv.Position, vt.Pos, 0.25f);
                sv.Velocity = Vector3.Zero;
                sv.Quaternion = Quaternion.Slerp(sv.Quaternion, targetQ, 0.25f);
            }
            sv.SteeringAngle = vt.Steer;
            ApplyVehicleFlags(sv, vt.VFlags);
        }

        if (stale != null)
        {
            foreach (int id in stale)
            {
                DeleteSyncedVehicleAndCrew(id);
            }
        }
    }

    private void HandleSyncNpcs(string payload)
    {
        if (!_isGuestRole || string.IsNullOrEmpty(payload)) return;
        if (_guestCsState != 0) return;   // v0.9.2: ingen speilfigurer under mellomsekvens
        if (payload.StartsWith("STYLE:"))
        {
            string[] srows = payload.Substring(6).Split(';');
            for (int i = 0; i < srows.Length; i++)
            {
                string sr = srows[i];
                int eq = sr.IndexOf('=');
                if (eq > 0)
                {
                    int shId;
                    if (int.TryParse(sr.Substring(0, eq), out shId))
                    {
                        string outf = sr.Substring(eq + 1);
                        _npcWantedOutfits[shId] = outf;
                        Ped sp;
                        string prevOutf;
                        if (_syncedNpcs.TryGetValue(shId, out sp) && sp != null && sp.Exists()
                            && (!_npcAppliedOutfits.TryGetValue(shId, out prevOutf) || prevOutf != outf))
                        {
                            ApplyOutfit(sp, outf);
                            _npcAppliedOutfits[shId] = outf;
                            // Re-vis våpen etter klær-/maske-endring (SET_PED_COMPONENT_VARIATION kan hylstre våpenet!)
                            NpcTarget snt;
                            if (_npcTargets.TryGetValue(shId, out snt) && snt.Weapon != 0 && snt.Weapon != (uint)WeaponHash.Unarmed)
                            {
                                Function.Call(Hash.GIVE_WEAPON_TO_PED, sp.Handle, snt.Weapon, 200, false, true);
                                Function.Call(Hash.SET_CURRENT_PED_WEAPON, sp.Handle, snt.Weapon, true);
                                Function.Call(Hash.SET_PED_CURRENT_WEAPON_VISIBLE, sp.Handle, true, false, true, true);
                            }
                        }
                    }
                }
            }
            return;
        }
        Ped me = Game.Player.Character;
        if (me == null || !me.Exists()) return;
        if (!_startupTasksCleared)
        {
            _startupTasksCleared = true;
            Function.Call(Hash.SET_PLAYER_CONTROL, Game.Player.Handle, true, 0);
            if (!me.IsInVehicle())
            {
                Function.Call(Hash.CLEAR_PED_TASKS_IMMEDIATELY, me.Handle);
            }
        }

        // Fullfør kollisjonssikker teleportering: hold spiller frosset over bakken til bakkekollisjonen er lastet!
        if (_safeTpWaitingGround)
        {
            Function.Call(Hash.REQUEST_COLLISION_AT_COORD, _safeTpPos.X, _safeTpPos.Y, _safeTpPos.Z);
            bool loaded = Function.Call<bool>(Hash.HAS_COLLISION_LOADED_AROUND_ENTITY, me.Handle);
            if (loaded || Game.GameTime - _safeTpStartMs > 1800)
            {
                OutputArgument outGz = new OutputArgument();
                if (Function.Call<bool>(Hash.GET_GROUND_Z_FOR_3D_COORD, _safeTpPos.X, _safeTpPos.Y, _safeTpPos.Z + 8.0f, outGz, false))
                {
                    float gz = outGz.GetResult<float>();
                    if (gz > -150.0f) me.PositionNoOffset = new Vector3(_safeTpPos.X, _safeTpPos.Y, gz + 1.0f);
                }
                me.IsPositionFrozen = false;
                _safeTpWaitingGround = false;
            }
            else
            {
                me.PositionNoOffset = _safeTpPos;
            }
        }
        int now = Game.GameTime;

        string[] rows = payload.Split(';');
        HashSet<int> usedLocalHandles = new HashSet<int>();
        foreach (Ped existing in _syncedNpcs.Values)
        {
            if (existing != null && existing.Exists()) usedLocalHandles.Add(existing.Handle);
        }
        foreach (Ped drv in _syncedVehDrivers.Values)
        {
            if (drv != null && drv.Exists()) usedLocalHandles.Add(drv.Handle);
        }
        foreach (Ped pas in _syncedVehPass.Values)
        {
            if (pas != null && pas.Exists()) usedLocalHandles.Add(pas.Handle);
        }

        // Kjør kun én enkelt GetNearbyPeds hvert 800 ms (unngår 130 tunge Ped[]-allokeringer/sek som ga hakking hos gjesten!)
        Ped[] nearOnce = null;
        if (now - _lastHiddenSweepMs >= 800)
        {
            _lastHiddenSweepMs = now;
            nearOnce = World.GetNearbyPeds(me.Position, 35.0f);
        }

        for (int i = 0; i < rows.Length; i++)
        {
            string row = rows[i];
            if (string.IsNullOrEmpty(row)) continue;
            string[] f = row.Split(',');
            if (f.Length < 9) continue;

            int hId, mHash, hp, nflags = 0, nvh = 0, nvs = -2;
            float nx, ny, nz, nh, nspd, nvx = 0f, nvy = 0f;
            uint nwp;
            if (!int.TryParse(f[0], out hId) || !int.TryParse(f[1], out mHash) ||
                !TryF(f[2], out nx) || !TryF(f[3], out ny) || !TryF(f[4], out nz) ||
                !TryF(f[5], out nh) || !TryF(f[6], out nspd) || !int.TryParse(f[7], out hp) ||
                !uint.TryParse(f[8], out nwp))
            {
                continue;
            }
            if (f.Length >= 10) int.TryParse(f[9], out nflags);
            if (f.Length >= 12)
            {
                int.TryParse(f[10], out nvh);
                int.TryParse(f[11], out nvs);
            }
            if (f.Length >= 14)
            {
                TryF(f[12], out nvx);
                TryF(f[13], out nvy);
            }

            Vector3 tpos = new Vector3(nx, ny, nz);
            Ped np = null;
            if (_syncedNpcs.TryGetValue(hId, out np) && (np == null || !np.Exists()))
            {
                _syncedNpcs.Remove(hId);
                np = null;
            }

            if (nearOnce != null)
            {
                foreach (Ped cand in nearOnce)
                {
                    if (cand != null && cand.Exists() && cand != me && cand != _guestPed && cand != np
                        && !cand.IsPlayer && !usedLocalHandles.Contains(cand.Handle)
                        && !_hiddenLocalNpcs.ContainsKey(cand.Handle) && cand.Model.Hash == mHash
                        && cand.Position.DistanceTo(tpos) < 24.0f)
                    {
                        cand.IsVisible = false;
                        cand.IsPositionFrozen = true;
                        Function.Call(Hash.SET_ENTITY_COLLISION, cand.Handle, false, false);
                        Function.Call(Hash.SET_ENTITY_COMPLETELY_DISABLE_COLLISION, cand.Handle, false, false);
                        _hiddenLocalNpcs[cand.Handle] = cand;
                    }
                }
            }

            if (np == null && hp > 0 && _syncedNpcs.Count < 65 && me.Position.DistanceTo(tpos) < 450.0f)
            {
                // VIKTIG: Hvis en oppdrags-NPC sitter i en bil (nvh != 0), spawn den ALDRI på bakken oppå bilen!
                // Vent til speil-bilen eksisterer og bruk CREATE_PED_INSIDE_VEHICLE direkte i riktig sete!
                Vehicle spawnInVeh = null;
                if (nvh != 0)
                {
                    _syncedVehs.TryGetValue(nvh, out spawnInVeh);
                    if (spawnInVeh == null || !spawnInVeh.Exists()) continue;
                }
                try
                {
                    Model m = new Model(mHash);
                    if (m.IsValid)
                    {
                        if (!m.IsLoaded)
                        {
                            m.Request();
                        }
                        else
                        {
                            if (spawnInVeh != null)
                            {
                                int st = nvs < -1 ? 0 : nvs;
                                if (!spawnInVeh.IsSeatFree((VehicleSeat)st))
                                {
                                    Ped oldCrew = spawnInVeh.GetPedOnSeat((VehicleSeat)st);
                                    if (oldCrew != null && oldCrew != me && oldCrew != _guestPed && !_syncedNpcs.ContainsValue(oldCrew))
                                    {
                                        try { oldCrew.Delete(); } catch { }
                                    }
                                    else if (spawnInVeh.IsSeatFree(VehicleSeat.Passenger)) st = 0;
                                    else if (spawnInVeh.IsSeatFree(VehicleSeat.LeftRear)) st = 1;
                                    else st = 2;
                                }
                                int ph = Function.Call<int>(Hash.CREATE_PED_INSIDE_VEHICLE, spawnInVeh.Handle, 26, mHash, st, false, false);
                                if (ph != 0) np = Entity.FromHandle(ph) as Ped;
                            }
                            else
                            {
                                np = World.CreatePed(m, tpos, nh);
                            }
                            m.MarkAsNoLongerNeeded();
                            if (np != null && np.Exists())
                            {
                                Function.Call(Hash.SET_PED_DEFAULT_COMPONENT_VARIATION, np.Handle);
                                np.RelationshipGroup = me.RelationshipGroup;
                                np.BlockPermanentEvents = true;
                                np.CanRagdoll = false;
                                bool protectMissionAlly = ((nflags & 64) != 0) && !IsPolicePed(np);
                                np.IsInvincible = protectMissionAlly;
                                np.IsPersistent = true;
                                Function.Call(Hash.SET_PED_CAN_RAGDOLL_FROM_PLAYER_IMPACT, np.Handle, false);
                                Function.Call(Hash.TASK_SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, np.Handle, true);
                                string wOutf;
                                if (_npcWantedOutfits.TryGetValue(hId, out wOutf))
                                {
                                    ApplyOutfit(np, wOutf);
                                    _npcAppliedOutfits[hId] = wOutf;
                                }
                                _syncedNpcs[hId] = np;
                                _spawnedNpcHandles.Add(hId);
                                usedLocalHandles.Add(np.Handle);
                            }
                        }
                    }
                }
                catch { }
            }

            if (np == null || !np.Exists()) continue;

            NpcTarget nt;
            nt.Pos = tpos;
            nt.Vel = new Vector3(nvx, nvy, 0f);
            nt.Heading = nh;
            nt.Speed = nspd;
            nt.Health = hp;
            nt.Weapon = nwp;
            nt.Flags = nflags;
            nt.VehHandle = nvh;
            nt.VehSeat = nvs;
            nt.SeenMs = now;
            _npcTargets[hId] = nt;

            if (!np.BlockPermanentEvents)
            {
                np.BlockPermanentEvents = true;
            }
            if (nwp != 0 && nwp != (uint)WeaponHash.Unarmed)
            {
                uint selWp = (uint)Function.Call<int>(Hash.GET_SELECTED_PED_WEAPON, np.Handle);
                if (selWp != nwp || !Function.Call<bool>(Hash.HAS_PED_GOT_WEAPON, np.Handle, nwp, false))
                {
                    Function.Call(Hash.GIVE_WEAPON_TO_PED, np.Handle, nwp, 200, false, true);
                    Function.Call(Hash.SET_CURRENT_PED_WEAPON, np.Handle, nwp, true);
                    Function.Call(Hash.SET_PED_CURRENT_WEAPON_VISIBLE, np.Handle, true, false, true, true);
                    Function.Call(Hash.SET_PED_CAN_SWITCH_WEAPON, np.Handle, false);
                }
            }
        }

        // Skjul ekstra lokale NPC-er innen 18 m fra Verten kun ved nearOnce-sveipet (hvert 800 ms)
        if (nearOnce != null && _hasTarget && me.Position.DistanceTo(_targetPos) < 30.0f)
        {
            foreach (Ped ex in nearOnce)
            {
                if (ex != null && ex.Exists() && ex != me && ex != _guestPed && !ex.IsPlayer
                    && !usedLocalHandles.Contains(ex.Handle) && !_hiddenLocalNpcs.ContainsKey(ex.Handle)
                    && ex.Position.DistanceTo(_targetPos) < 18.0f)
                {
                    ex.IsVisible = false;
                    ex.IsPositionFrozen = true;
                    Function.Call(Hash.SET_ENTITY_COLLISION, ex.Handle, false, false);
                    Function.Call(Hash.SET_ENTITY_COMPLETELY_DISABLE_COLLISION, ex.Handle, false, false);
                    _hiddenLocalNpcs[ex.Handle] = ex;
                }
            }
        }
    }

    private void UpdateSyncedNpcsTick()
    {
        if (!_isGuestRole) return;
        int now = Game.GameTime;

        // Vedlikehold skjulte lokale NPC-er kun 2 ganger i sekundet (ikke inne i 60 Hz per-NPC-løkken!)
        if (_hiddenLocalNpcs.Count > 0 && now - _lastHiddenMaintMs >= 500)
        {
            _lastHiddenMaintMs = now;
            foreach (Ped hp in _hiddenLocalNpcs.Values)
            {
                if (hp != null && hp.Exists())
                {
                    if (hp.IsVisible) hp.IsVisible = false;
                    if (!hp.IsPositionFrozen) hp.IsPositionFrozen = true;
                    Function.Call(Hash.SET_ENTITY_COLLISION, hp.Handle, false, false);
                }
            }
        }

        if (_syncedNpcs.Count == 0) return;
        List<int> stale = null;

        foreach (KeyValuePair<int, Ped> kv in _syncedNpcs)
        {
            int hId = kv.Key;
            Ped np = kv.Value;
            NpcTarget nt;
            if (np == null || !np.Exists() || !_npcTargets.TryGetValue(hId, out nt)
                || now - nt.SeenMs > 5500
                || (Game.Player.Character != null && np.Position.DistanceTo(Game.Player.Character.Position) > 480.0f))
            {
                if (stale == null) stale = new List<int>();
                stale.Add(hId);
                continue;
            }

            if (nt.Health <= 0)
            {
                np.IsPositionFrozen = false;
                np.IsInvincible = false;
                np.CanRagdoll = true;
                if (np.IsAlive) np.Kill();
                continue;
            }

            // Hvis NPC-en sitter INNI en bil på Verten (f.eks. Trevor/Brad i fluktbilen eller politi i politibil!):
            if (nt.VehHandle != 0)
            {
                if (np.IsPositionFrozen) np.IsPositionFrozen = false;
                Vehicle npcVeh = null;
                _syncedVehs.TryGetValue(nt.VehHandle, out npcVeh);
                if ((npcVeh == null || !npcVeh.Exists()) && _guestVeh != null && _guestVeh.Exists()
                    && _guestVeh.Position.DistanceTo(nt.Pos) < 8.0f)
                {
                    npcVeh = _guestVeh;
                }
                Ped localMe = Game.Player.Character;
                if ((npcVeh == null || !npcVeh.Exists()) && localMe != null && localMe.IsInVehicle()
                    && localMe.CurrentVehicle != null && localMe.CurrentVehicle.Position.DistanceTo(nt.Pos) < 8.0f)
                {
                    npcVeh = localMe.CurrentVehicle;
                }
                if (npcVeh != null && npcVeh.Exists() && !np.IsInVehicle(npcVeh))
                {
                    int seat = nt.VehSeat;
                    if (seat < -1) seat = 0;
                    if (!npcVeh.IsSeatFree((VehicleSeat)seat))
                    {
                        if (npcVeh.IsSeatFree(VehicleSeat.Passenger)) seat = 0;
                        else if (npcVeh.IsSeatFree(VehicleSeat.LeftRear)) seat = 1;
                        else if (npcVeh.IsSeatFree(VehicleSeat.RightRear)) seat = 2;
                    }
                    Function.Call(Hash.TASK_WARP_PED_INTO_VEHICLE, np.Handle, npcVeh.Handle, seat);
                }
                continue;
            }
            else if (np.IsInVehicle())
            {
                // NPC-en gikk ut av bilen på Verten -> ta speil-NPC-en ut av bilen!
                Function.Call(Hash.TASK_LEAVE_ANY_VEHICLE, np.Handle, 0, 16);
                np.PositionNoOffset = nt.Pos;
            }

            // KOLLISJONSVAKT: Frys speil-NPC på Vertens Z-høyde inntil bakkekollisjonen rundt den er lastet -> faller ALDRI gjennom bakken!
            Function.Call(Hash.REQUEST_COLLISION_AT_COORD, nt.Pos.X, nt.Pos.Y, nt.Pos.Z);
            if (!Function.Call<bool>(Hash.HAS_COLLISION_LOADED_AROUND_ENTITY, np.Handle))
            {
                np.IsPositionFrozen = true;
                np.PositionNoOffset = nt.Pos;
                np.Heading = nt.Heading;
                continue;
            }

            float dt = Math.Min(0.20f, Math.Max(0f, (now - nt.SeenMs) / 1000.0f));
            Vector3 predPos = nt.Pos + nt.Vel * dt;
            float dx = predPos.X - np.Position.X;
            float dy = predPos.Y - np.Position.Y;
            float dz = Math.Abs(np.Position.Z - predPos.Z);
            float dist2d = (float)Math.Sqrt(dx * dx + dy * dy);
            bool npcAim = (nt.Flags & 1) != 0;
            bool npcShoot = (nt.Flags & 2) != 0;
            bool npcCower = (nt.Flags & 8) != 0;
            bool npcAction = (nt.Flags & 16) != 0 || (nt.Weapon != 0 && nt.Weapon != (uint)WeaponHash.Unarmed);
            int prevFlags = -1;
            _syncedNpcAnimFlags.TryGetValue(hId, out prevFlags);

            bool curAct = Function.Call<bool>(Hash.IS_PED_USING_ACTION_MODE, np.Handle);
            if (curAct != npcAction)
            {
                Function.Call(Hash.SET_PED_USING_ACTION_MODE, np.Handle, npcAction, -1, "DEFAULT_ACTION");
            }

            if (nt.Speed <= 0.45f && dist2d <= 2.2f && dz <= 1.5f)
            {
                if (np.IsPositionFrozen) np.IsPositionFrozen = false;
                if (dist2d > 1.25f)
                {
                    int lastIdleMv = 0;
                    _syncedNpcMoveMs.TryGetValue(hId, out lastIdleMv);
                    if (now - lastIdleMv >= 800)
                    {
                        _syncedNpcMoveMs[hId] = now;
                        Function.Call(Hash.TASK_GO_STRAIGHT_TO_COORD, np.Handle, nt.Pos.X, nt.Pos.Y, nt.Pos.Z, 1.0f, 800, nt.Heading, 0.20f);
                    }
                }
                else
                {
                    int lastMv = 0;
                    _syncedNpcMoveMs.TryGetValue(hId, out lastMv);
                    float lastAimH = -999f;
                    _syncedNpcAimHeading.TryGetValue(hId, out lastAimH);
                    float aimHDiff = lastAimH < -900f ? 999f : Math.Abs(AngleDiff(lastAimH, nt.Heading));
                    bool targetGuest = (nt.Flags & 32) != 0;
                    Ped localMe = Game.Player.Character;
                    float rad = nt.Heading * (float)(Math.PI / 180.0);
                    Vector3 aimPt = nt.Pos + new Vector3(-(float)Math.Sin(rad), (float)Math.Cos(rad), 0.15f) * 18.0f;
                    if (targetGuest && localMe != null && localMe.Exists())
                    {
                        float jx = ((now % 7) - 3) * 0.18f;
                        float jy = (((now / 7) % 7) - 3) * 0.18f;
                        aimPt = localMe.Position + new Vector3(jx, jy, 0.35f);
                    }
                    else if (IsPolicePed(np) && _guestPed != null && _guestPed.Exists() && np.Position.DistanceTo(_guestPed.Position) < 45.0f)
                    {
                        aimPt = _guestPed.Position + new Vector3(0f, 0f, 0.35f);
                    }

                    if (npcShoot && (now - lastMv >= 380 || prevFlags != nt.Flags))
                    {
                        _syncedNpcMoveMs[hId] = now;
                        _syncedNpcAnimFlags[hId] = nt.Flags;
                        _syncedNpcAimHeading[hId] = nt.Heading;
                        Function.Call(Hash.TASK_SHOOT_AT_COORD, np.Handle, aimPt.X, aimPt.Y, aimPt.Z, 450, unchecked((int)0xC6EE6B4C));
                        int lastBul = 0;
                        _syncedNpcBulletMs.TryGetValue(hId, out lastBul);
                        if (nt.Weapon != 0 && nt.Weapon != (uint)WeaponHash.Unarmed && now - lastBul >= 480)
                        {
                            _syncedNpcBulletMs[hId] = now;
                            Vector3 muz = np.Position + new Vector3(0f, 0f, 0.55f) + np.ForwardVector * 0.65f;
                            int dmg = targetGuest ? 7 : 0;
                            Function.Call(Hash.SHOOT_SINGLE_BULLET_BETWEEN_COORDS,
                                muz.X, muz.Y, muz.Z, aimPt.X, aimPt.Y, aimPt.Z,
                                dmg, true, nt.Weapon, np.Handle, true, false, -1.0f);
                        }
                    }
                    else if (npcAim && (prevFlags != nt.Flags || aimHDiff > 12.0f))
                    {
                        _syncedNpcMoveMs[hId] = now;
                        _syncedNpcAnimFlags[hId] = nt.Flags;
                        _syncedNpcAimHeading[hId] = nt.Heading;
                        Function.Call(Hash.TASK_AIM_GUN_AT_COORD, np.Handle, aimPt.X, aimPt.Y, aimPt.Z, -1, false, false);
                    }
                    else if (npcCower && prevFlags != nt.Flags)
                    {
                        _syncedNpcAnimFlags[hId] = nt.Flags;
                        Function.Call(Hash.TASK_COWER, np.Handle, -1);
                    }
                    else if (!npcShoot && !npcAim && !npcCower)
                    {
                        if (prevFlags > 0 && (prevFlags & 11) != 0)
                        {
                            Function.Call(Hash.CLEAR_PED_TASKS, np.Handle);
                        }
                        _syncedNpcAnimFlags[hId] = nt.Flags;
                        if (Math.Abs(AngleDiff(np.Heading, nt.Heading)) > 6.0f)
                        {
                            np.Heading = LerpAngle(np.Heading, nt.Heading, 0.25f);
                        }
                    }
                }
            }
            else
            {
                // Velocity Dead-Reckoning + Lead-Goal: sikt 2–3,5 m FREM i fartsretningen så NPC aldri småbremser mellom pakker!
                if (np.IsPositionFrozen) np.IsPositionFrozen = false;
                _syncedNpcAnimFlags[hId] = -1;
                if (dist2d > 6.5f || dz > 2.8f)
                {
                    Function.Call(Hash.CLEAR_PED_TASKS_IMMEDIATELY, np.Handle);
                    np.PositionNoOffset = predPos;
                    np.Heading = nt.Heading;
                }
                else
                {
                    Vector3 velDir = nt.Vel.Length() > 0.3f ? Vector3.Normalize(nt.Vel)
                        : new Vector3(-(float)Math.Sin(nt.Heading * Math.PI / 180.0), (float)Math.Cos(nt.Heading * Math.PI / 180.0), 0f);
                    Vector3 walkGoal = predPos + velDir * Math.Max(1.8f, nt.Speed * 0.65f);
                    int lastMv = 0;
                    _syncedNpcMoveMs.TryGetValue(hId, out lastMv);
                    if (now - lastMv >= 240)
                    {
                        _syncedNpcMoveMs[hId] = now;
                        float blend = nt.Speed > 4.0f ? 3.0f : (nt.Speed > 1.8f ? 2.0f : 1.0f);
                        Function.Call(Hash.TASK_GO_STRAIGHT_TO_COORD, np.Handle, walkGoal.X, walkGoal.Y, walkGoal.Z, blend, 900, nt.Heading, 0.25f);
                    }
                    // Myk hastighets-korreksjon (ALDRI PositionNoOffset under 6,5 m -> bevarer 100 % naturlig ben-animasjon!)
                    if (dist2d > 1.2f)
                    {
                        Vector3 curV = np.Velocity;
                        np.Velocity = new Vector3(
                            nt.Vel.X + dx * 2.4f,
                            nt.Vel.Y + dy * 2.4f,
                            curV.Z);
                    }
                }
            }
        }

        if (stale != null)
        {
            foreach (int id in stale)
            {
                Ped sp;
                if (_syncedNpcs.TryGetValue(id, out sp) && sp != null && sp.Exists())
                {
                    try
                    {
                        sp.IsPositionFrozen = false;
                        if (_spawnedNpcHandles.Contains(id)) sp.Delete();
                    }
                    catch { }
                }
                _syncedNpcs.Remove(id);
                _npcTargets.Remove(id);
                _spawnedNpcHandles.Remove(id);
                _syncedNpcMoveMs.Remove(id);
                _syncedNpcAnimFlags.Remove(id);
                _syncedNpcAimHeading.Remove(id);
                _syncedNpcBulletMs.Remove(id);
            }
        }
    }

    private void SendVehicleBatch(List<Vehicle> vcands, int startIdx, int endIdx, Ped me)
    {
        if (endIdx <= startIdx) return;
        StringBuilder vsb = new StringBuilder(1100);
        for (int i = startIdx; i < endIdx; i++)
        {
            Vehicle v = vcands[i];
            int drvMdl = 0, passMdl = 0;
            Ped drv = v.GetPedOnSeat(VehicleSeat.Driver);
            if (drv != null && drv.Exists() && drv != me && drv != _guestPed && !drv.IsPlayer
                && !Function.Call<bool>(Hash.IS_ENTITY_A_MISSION_ENTITY, drv.Handle))
            {
                drvMdl = drv.Model.Hash;
            }
            Ped pas = v.GetPedOnSeat(VehicleSeat.Passenger);
            if (pas != null && pas.Exists() && pas != me && pas != _guestPed && !pas.IsPlayer
                && !Function.Call<bool>(Hash.IS_ENTITY_A_MISSION_ENTITY, pas.Handle))
            {
                passMdl = pas.Model.Hash;
            }
            Vector3 vp = v.Position;
            Vector3 vv = v.Velocity;
            OutputArgument oqx = new OutputArgument(), oqy = new OutputArgument(), oqz = new OutputArgument(), oqw = new OutputArgument();
            Function.Call(Hash.GET_ENTITY_QUATERNION, v.Handle, oqx, oqy, oqz, oqw);
            OutputArgument oc1 = new OutputArgument(), oc2 = new OutputArgument();
            Function.Call(Hash.GET_VEHICLE_COLOURS, v.Handle, oc1, oc2);
            if (i > startIdx) vsb.Append(';');
            vsb.Append(v.Handle).Append(',')
               .Append(v.Model.Hash).Append(',')
               .Append(vp.X.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
               .Append(vp.Y.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
               .Append(vp.Z.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
               .Append(oqx.GetResult<float>().ToString("F4", CultureInfo.InvariantCulture)).Append(',')
               .Append(oqy.GetResult<float>().ToString("F4", CultureInfo.InvariantCulture)).Append(',')
               .Append(oqz.GetResult<float>().ToString("F4", CultureInfo.InvariantCulture)).Append(',')
               .Append(oqw.GetResult<float>().ToString("F4", CultureInfo.InvariantCulture)).Append(',')
               .Append(vv.X.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
               .Append(vv.Y.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
               .Append(vv.Z.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
               .Append(v.SteeringAngle.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                .Append(oc1.GetResult<int>()).Append(',')
               .Append(oc2.GetResult<int>()).Append(',')
               .Append(ReadVehicleFlags(v, false)).Append(',')
               .Append(v.IsDead ? 1 : 0).Append(',')
               .Append(drvMdl).Append(',')
               .Append(passMdl);
        }
        SendRaw("VEHS|" + vsb.ToString());
    }

    private void SendNpcBatch(List<Ped> cands, int startIdx, int endIdx)
    {
        if (endIdx <= startIdx) return;
        StringBuilder sb = new StringBuilder(1024);
        for (int i = startIdx; i < endIdx; i++)
        {
            Ped np = cands[i];
            Vector3 p = np.Position;
            uint nwp = (uint)Function.Call<int>(Hash.GET_SELECTED_PED_WEAPON, np.Handle);
            if (nwp == 0 || nwp == (uint)WeaponHash.Unarmed)
            {
                try { nwp = (uint)np.Weapons.Current.Hash; } catch { }
            }
            if (nwp == 0 || nwp == (uint)WeaponHash.Unarmed)
            {
                uint bestWp = (uint)Function.Call<int>(Hash.GET_BEST_PED_WEAPON, np.Handle, false);
                if (bestWp != (uint)WeaponHash.Unarmed) nwp = bestWp;
            }
            int nfl = ComputeNpcFlags(np, _guestPed);
            int nvh = 0, nvs = -2;
            if (np.IsInVehicle() && np.CurrentVehicle != null && np.CurrentVehicle.Exists())
            {
                nvh = np.CurrentVehicle.Handle;
                nvs = (int)np.SeatIndex;
            }
            bool npcMoving = np.IsWalking || np.IsRunning || np.IsSprinting || np.IsJumping || np.IsFalling || np.IsClimbing;
            float nspd = npcMoving ? np.Velocity.Length() : 0.0f;
            if (i > startIdx) sb.Append(';');
            sb.Append(np.Handle).Append(',')
              .Append(np.Model.Hash).Append(',')
              .Append(p.X.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
              .Append(p.Y.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
              .Append(p.Z.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
              .Append(np.Heading.ToString("F1", CultureInfo.InvariantCulture)).Append(',')
              .Append(nspd.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
              .Append(np.IsDead ? 0 : np.Health).Append(',')
              .Append(nwp).Append(',')
              .Append(nfl).Append(',')
              .Append(nvh).Append(',')
              .Append(nvs).Append(',')
              .Append(np.Velocity.X.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
              .Append(np.Velocity.Y.ToString("F2", CultureInfo.InvariantCulture));
        }
        SendRaw("NPCS|" + sb.ToString());
    }

    private static int ComputeNpcFlags(Ped np, Ped guestPed = null)
    {
        int nfl = 0;
        try
        {
            bool hasWeapon = false;
            int wEnt = Function.Call<int>(Hash.GET_CURRENT_PED_WEAPON_ENTITY_INDEX, np.Handle);
            float wz = -9f;
            if (wEnt != 0)
            {
                hasWeapon = true;
                Vector3 wpPos = Function.Call<Vector3>(Hash.GET_ENTITY_COORDS, wEnt, true);
                wz = wpPos.Z - np.Position.Z;
            }
            bool aimTask = Function.Call<int>(Hash.GET_SCRIPT_TASK_STATUS, np.Handle, Function.Call<int>(Hash.GET_HASH_KEY, "SCRIPT_TASK_AIM_GUN_AT_COORD")) < 2
                        || Function.Call<int>(Hash.GET_SCRIPT_TASK_STATUS, np.Handle, Function.Call<int>(Hash.GET_HASH_KEY, "SCRIPT_TASK_AIM_GUN_AT_ENTITY")) < 2
                        || Function.Call<int>(Hash.GET_SCRIPT_TASK_STATUS, np.Handle, Function.Call<int>(Hash.GET_HASH_KEY, "SCRIPT_TASK_AIM_GUN_SCRIPTED")) < 2;
            // wz >= 0.20f betyr at 3D-våpenet er løftet i skulder-/siktehøyde (fanger også spesial-animasjoner i oppdrag som Prologue!)
            if (Function.Call<bool>(Hash.GET_PED_CONFIG_FLAG, np.Handle, 78, true) || np.IsInCombat || aimTask || (hasWeapon && wz >= 0.20f))
            {
                nfl |= 1;
            }
            if (Function.Call<bool>(Hash.IS_PED_SHOOTING, np.Handle)) nfl |= 2;
            if (Function.Call<bool>(Hash.IS_PED_IN_COVER, np.Handle, false)) nfl |= 4;
            if (Function.Call<bool>(Hash.GET_PED_STEALTH_MOVEMENT, np.Handle)
                || Function.Call<bool>(Hash.IS_PED_DUCKING, np.Handle)
                || Function.Call<bool>(Hash.IS_PED_FLEEING, np.Handle)) nfl |= 8;
            if (Function.Call<bool>(Hash.IS_PED_USING_ACTION_MODE, np.Handle) || hasWeapon) nfl |= 16;
            if ((nfl & 3) != 0 && guestPed != null && guestPed.Exists())
            {
                Vector3 toGuest = guestPed.Position - np.Position;
                float gd = toGuest.Length();
                if (gd < 55.0f && gd > 0.5f)
                {
                    float wantH = (float)(Math.Atan2(-toGuest.X, toGuest.Y) * (180.0 / Math.PI));
                    if (wantH < 0f) wantH += 360f;
                    if (Math.Abs(AngleDiff(np.Heading, wantH)) < 28.0f) nfl |= 32;
                }
            }
            if (Function.Call<bool>(Hash.IS_ENTITY_A_MISSION_ENTITY, np.Handle)) nfl |= 64;
        }
        catch { }
        return nfl;
    }

    private void SendDiag()
    {
        try
        {
            Ped me = Game.Player.Character;
            if (me == null || !me.Exists())
            {
                SendRaw("DIAG_RES|no_player");
                return;
            }
            HashSet<int> mirrorSet = new HashSet<int>();
            foreach (Ped mp in _syncedNpcs.Values)
            {
                if (mp != null && mp.Exists()) mirrorSet.Add(mp.Handle);
            }
            StringBuilder sb = new StringBuilder(1536);
            uint myWp = (uint)Function.Call<int>(Hash.GET_SELECTED_PED_WEAPON, me.Handle);
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "role={0};me={1}@({2:F1},{3:F1},{4:F1})wp={5};synced={6};hidden={7}",
                _isGuestRole ? "Guest" : "Host", me.Model.Hash,
                me.Position.X, me.Position.Y, me.Position.Z, myWp,
                _syncedNpcs.Count, _hiddenLocalNpcs.Count);
            try   // v0.9.2: klokke og værtilstand i diagnosen
            {
                OutputArgument dPrev = new OutputArgument(), dNext = new OutputArgument(), dPct = new OutputArgument();
                Function.Call((Hash)0xF3BBE884A14BB413UL, dPrev, dNext, dPct);
                sb.AppendFormat(CultureInfo.InvariantCulture, ";clock={0:D2}:{1:D2}:{2:D2};weather={3}->{4}@{5:F2};wenum={6};cs={7}",
                    Function.Call<int>(Hash.GET_CLOCK_HOURS), Function.Call<int>(Hash.GET_CLOCK_MINUTES), Function.Call<int>(Hash.GET_CLOCK_SECONDS),
                    dPrev.GetResult<uint>(), dNext.GetResult<uint>(), dPct.GetResult<float>(), (int)World.Weather, _guestCsState);
                sb.AppendFormat(CultureInfo.InvariantCulture, ";msPerMin={0};switch={1};mission={2};cutActive={3};paused={4}",
                    Function.Call<int>((Hash)0x2F8B4D1C595B11DBUL),               // GET_MILLISECONDS_PER_GAME_MINUTE
                    Function.Call<bool>((Hash)0xD9D2CFFF49FAB35FUL) ? 1 : 0,       // IS_PLAYER_SWITCH_IN_PROGRESS
                    Function.Call<bool>(Hash.GET_MISSION_FLAG) ? 1 : 0,
                    Function.Call<bool>(Hash.IS_CUTSCENE_ACTIVE) ? 1 : 0,
                    Game.IsPaused ? 1 : 0);
            }
            catch { }
            if (_guestPed != null && _guestPed.Exists())
            {
                uint gWp = (uint)Function.Call<int>(Hash.GET_SELECTED_PED_WEAPON, _guestPed.Handle);
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    ";peer={0}@({1:F1},{2:F1},{3:F1})vis={4},wp={5}",
                    _guestPed.Model.Hash, _guestPed.Position.X, _guestPed.Position.Y, _guestPed.Position.Z,
                    _guestPed.IsVisible ? 1 : 0, gWp);
            }
            Ped[] near = World.GetNearbyPeds(me, 28.0f);
            int c = 0;
            foreach (Ped p in near)
            {
                if (p == null || !p.Exists() || p == me || p == _guestPed) continue;
                if (c >= 14) break;
                string tag = mirrorSet.Contains(p.Handle) ? "MIRROR"
                           : (_hiddenLocalNpcs.ContainsKey(p.Handle) ? "HIDDEN" : "LOCAL");
                uint pw = (uint)Function.Call<int>(Hash.GET_SELECTED_PED_WEAPON, p.Handle);
                float wz = -9f;
                int wEnt = Function.Call<int>(Hash.GET_CURRENT_PED_WEAPON_ENTITY_INDEX, p.Handle);
                if (wEnt != 0)
                {
                    Vector3 wpPos = Function.Call<Vector3>(Hash.GET_ENTITY_COORDS, wEnt, true);
                    wz = wpPos.Z - p.Position.Z;
                }
                int pfl = ComputeNpcFlags(p);
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    ";npc[{0}]={1}:{2}@({3:F1},{4:F1},{5:F1})vis={6},spd={7:F2},wp={8},wz={9:F2},fl={10}",
                    p.Handle, tag, p.Model.Hash, p.Position.X, p.Position.Y, p.Position.Z,
                    p.IsVisible ? 1 : 0, p.Velocity.Length(), pw, wz, pfl);
                c++;
            }
            int intId = Function.Call<int>(Hash.GET_INTERIOR_FROM_ENTITY, me.Handle);
            sb.AppendFormat(CultureInfo.InvariantCulture, ";int={0}", intId);
            if (_hasTarget)   // v0.9.2: interiøret der den andre står, og om det er slått av / klart
            {
                int ti = Function.Call<int>(Hash.GET_INTERIOR_AT_COORDS, _targetPos.X, _targetPos.Y, _targetPos.Z);
                sb.AppendFormat(CultureInfo.InvariantCulture, ";peerInt={0},disabled={1},ready={2}", ti,
                    ti != 0 && Function.Call<bool>(Hash.IS_INTERIOR_DISABLED, ti) ? 1 : 0,
                    ti != 0 && Function.Call<bool>(Hash.IS_INTERIOR_READY, ti) ? 1 : 0);
            }
            // 3D Raycast 6 m rett frem i kameraets/spillerens retning for å se eksakt hva som blokkerer veien!
            Vector3 rFrom = me.Position + new Vector3(0f, 0f, 0.4f);
            Vector3 rTo = rFrom + GameplayCamera.Direction * 8.0f;
            int ray = Function.Call<int>(Hash.START_EXPENSIVE_SYNCHRONOUS_SHAPE_TEST_LOS_PROBE,
                rFrom.X, rFrom.Y, rFrom.Z, rTo.X, rTo.Y, rTo.Z, -1, me.Handle, 7);
            OutputArgument oHit = new OutputArgument(), oEnd = new OutputArgument(), oNorm = new OutputArgument(), oEnt = new OutputArgument();
            Function.Call<int>(Hash.GET_SHAPE_TEST_RESULT, ray, oHit, oEnd, oNorm, oEnt);
            if (oHit.GetResult<bool>())
            {
                int hEnt = oEnt.GetResult<int>();
                Vector3 hPos = oEnd.GetResult<Vector3>();
                int eType = hEnt != 0 ? Function.Call<int>(Hash.GET_ENTITY_TYPE, hEnt) : 0;
                int eMdl = (hEnt != 0 && eType > 0) ? Function.Call<int>(Hash.GET_ENTITY_MODEL, hEnt) : 0;
                sb.AppendFormat(CultureInfo.InvariantCulture, ";rayHit=type:{0},ent:{1},mdl:{2}@({3:F1},{4:F1},{5:F1})",
                    eType, hEnt, eMdl, hPos.X, hPos.Y, hPos.Z);
            }
            Prop[] nprops = World.GetNearbyProps(me.Position, 7.5f);
            int pc = 0;
            foreach (Prop pr in nprops)
            {
                if (pr == null || !pr.Exists()) continue;
                if (pc >= 10) break;
                Vector3 r = pr.Rotation;
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    ";prop={0}@({1:F1},{2:F1},{3:F1})rot=({4:F0},{5:F0},{6:F0}),vis={7}",
                    pr.Model.Hash, pr.Position.X, pr.Position.Y, pr.Position.Z, r.X, r.Y, r.Z, pr.IsVisible ? 1 : 0);
                pc++;
            }
            Vehicle[] nvehs = World.GetNearbyVehicles(me.Position, 40.0f);
            int vc = 0;
            foreach (Vehicle v in nvehs)
            {
                if (v == null || !v.Exists()) continue;
                if (vc >= 5) break;
                string vtag = (v == _guestVeh) ? "PEERVEH" : (_syncedVehs.ContainsValue(v) ? "MIRRORVEH" : "LOCALVEH");
                Ped d0 = v.GetPedOnSeat(VehicleSeat.Driver);
                Ped p0 = v.GetPedOnSeat(VehicleSeat.Passenger);
                Ped r1 = v.GetPedOnSeat(VehicleSeat.LeftRear);
                Ped r2 = v.GetPedOnSeat(VehicleSeat.RightRear);
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    ";veh[{0}]={1}:{2}@({3:F1},{4:F1},{5:F1})seats=[{6},{7},{8},{9}]",
                    v.Handle, vtag, v.Model.Hash, v.Position.X, v.Position.Y, v.Position.Z,
                    d0 != null ? d0.Handle : 0, p0 != null ? p0.Handle : 0,
                    r1 != null ? r1.Handle : 0, r2 != null ? r2.Handle : 0);
                vc++;
            }
            SendRaw("DIAG_RES|" + sb.ToString());
        }
        catch (Exception ex)
        {
            SendRaw("DIAG_RES|err=" + ex.Message);
        }
    }

    private void PlayPassiveMeleeAnim(Ped p, uint wpHash)
    {
        PlayMeleeAnim(p, wpHash, _punchAlt);
        _punchAlt = !_punchAlt;
    }

    private void PlayMeleeAnim(Ped p, uint wpHash, bool heavy)
    {
        // 100 % passiv slag-animasjon UTEN AI (TASK_PLAY_ANIM): figuren slår kun én gang, og tar ALDRI over styringen!
        string dict = "melee@unarmed@streamed_core";
        string anim = heavy ? "heavy_punch_a" : "light_punch_a";
        if (wpHash == (uint)WeaponHash.Knife || wpHash == (uint)WeaponHash.Dagger || wpHash == (uint)WeaponHash.SwitchBlade)
        {
            dict = "melee@knife@streamed_core";
            anim = "ground_attack_on_spot";
        }
        else if (wpHash != 0 && wpHash != (uint)WeaponHash.Unarmed)
        {
            dict = "melee@large_wpn@streamed_core";
            anim = "ground_attack_on_spot";
        }
        if (Function.Call<bool>(Hash.DOES_ANIM_DICT_EXIST, dict))
        {
            Function.Call(Hash.REQUEST_ANIM_DICT, dict);
            if (Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, dict))
            {
                Function.Call(Hash.TASK_PLAY_ANIM, p.Handle, dict, anim, 4.0f, -4.0f, 520, 0, 0.0f, false, false, false);
            }
        }
    }

    // ===== v0.9.2: SKADESYNK FOR BILER =====
    // Eieren av en bil (verten for trafikk, sjåføren for bilen man deler) leser skaden og sender endringer:
    // VDMG|id,motor,karosseri,tank,dører,vinduer,dekk,skitt,bulker   (bulker = x:y:z:kraft/...)
    // id = vertens bil-handle; -1 = «bilen avsenderen sitter i» (mottakerens _guestVeh).
    private static DmgState ReadDamage(Vehicle v)
    {
        DmgState d = new DmgState();
        d.Eng = (int)Function.Call<float>(Hash.GET_VEHICLE_ENGINE_HEALTH, v.Handle);
        d.Body = (int)Function.Call<float>(Hash.GET_VEHICLE_BODY_HEALTH, v.Handle);
        d.Tank = (int)Function.Call<float>(Hash.GET_VEHICLE_PETROL_TANK_HEALTH, v.Handle);
        for (int i = 0; i < 6; i++)
            if (Function.Call<bool>(Hash.IS_VEHICLE_DOOR_DAMAGED, v.Handle, i)) d.Doors |= 1 << i;
        for (int i = 0; i < 8; i++)
            if (!Function.Call<bool>(Hash.IS_VEHICLE_WINDOW_INTACT, v.Handle, i)) d.Wins |= 1 << i;
        for (int i = 0; i < TYRE_IDX.Length; i++)
            if (Function.Call<bool>(Hash.IS_VEHICLE_TYRE_BURST, v.Handle, TYRE_IDX[i], false)) d.Tyres |= 1 << i;
        d.Dirt = (int)(Function.Call<float>(Hash.GET_VEHICLE_DIRT_LEVEL, v.Handle) * 10f);
        return d;
    }

    private static bool IsPristine(DmgState d)
    {
        return d.Eng >= 990 && d.Body >= 990 && d.Doors == 0 && d.Wins == 0 && d.Tyres == 0;
    }

    private static bool SameDamage(DmgState a, DmgState b)
    {
        return Math.Abs(a.Eng - b.Eng) < 15 && Math.Abs(a.Body - b.Body) < 15 && Math.Abs(a.Tank - b.Tank) < 15
            && a.Doors == b.Doors && a.Wins == b.Wins && a.Tyres == b.Tyres && Math.Abs(a.Dirt - b.Dirt) < 2;
    }

    // Bulk: retning fra bilens senter mot støtet (motsatt kollisjonsnormalen), skalert til bilens størrelse
    private static string DentFromCollision(Vehicle v, float drop)
    {
        Vector3 n = Function.Call<Vector3>(Hash.GET_COLLISION_NORMAL_OF_LAST_HIT_FOR_ENTITY, v.Handle);
        Vector3 pos = v.Position;
        Vector3 worldDir = (n.Length() > 0.1f) ? -n : v.Velocity;
        if (worldDir.Length() < 0.1f) return null;
        worldDir.Normalize();
        Vector3 local = Function.Call<Vector3>(Hash.GET_OFFSET_FROM_ENTITY_GIVEN_WORLD_COORDS, v.Handle,
            pos.X + worldDir.X, pos.Y + worldDir.Y, pos.Z + worldDir.Z);
        OutputArgument oMin = new OutputArgument(), oMax = new OutputArgument();
        Function.Call(Hash.GET_MODEL_DIMENSIONS, v.Model.Hash, oMin, oMax);
        Vector3 mx = oMax.GetResult<Vector3>();
        float ax = Math.Max(0.5f, Math.Abs(mx.X)), ay = Math.Max(1.0f, Math.Abs(mx.Y)), az = Math.Max(0.4f, Math.Abs(mx.Z));
        float px = local.X * ax, py = local.Y * ay, pz = local.Z * az * 0.5f;
        float force = Math.Max(60f, Math.Min(600f, drop * 3f));
        return string.Format(CultureInfo.InvariantCulture, "{0:F2}:{1:F2}:{2:F2}:{3:F0}", px, py, pz, force);
    }

    private void DamageSyncTick(Ped me)
    {
        if (Game.GameTime - _lastDmgTickMs < (_isGuestRole ? 120 : 250)) return;
        _lastDmgTickMs = Game.GameTime;
        // Hvilke biler eier jeg? Bilen jeg kjører + (vert) nær trafikk som ikke kjøres av medspilleren.
        List<KeyValuePair<int, Vehicle>> owned = new List<KeyValuePair<int, Vehicle>>();
        Vehicle mine = (me.IsInVehicle() && me.CurrentVehicle != null && me.CurrentVehicle.Exists()
            && me.SeatIndex == VehicleSeat.Driver) ? me.CurrentVehicle : null;
        if (mine != null)
        {
            int id = mine.Handle;
            if (_isGuestRole)
            {
                id = -1;
                foreach (KeyValuePair<int, Vehicle> kv in _syncedVehs) if (kv.Value == mine) { id = kv.Key; break; }
            }
            owned.Add(new KeyValuePair<int, Vehicle>(id, mine));
        }
        if (!_isGuestRole)
        {
            int n = 0;
            foreach (Vehicle v in World.GetNearbyVehicles(me.Position, 150.0f))
            {
                if (v == null || !v.Exists() || v == mine) continue;
                Ped drv = v.GetPedOnSeat(VehicleSeat.Driver);
                if (drv != null && drv.Exists() && drv == _guestPed) continue;   // medspilleren kjører -> han eier den
                owned.Add(new KeyValuePair<int, Vehicle>(v.Handle, v));
                if (++n >= 10) break;   // v0.9.2: var 25
            }
        }
        foreach (KeyValuePair<int, Vehicle> kv in owned)
        {
            Vehicle v = kv.Value;
            int h = v.Handle;
            float body = Function.Call<float>(Hash.GET_VEHICLE_BODY_HEALTH, h);
            string dent = null;
            float lastBody;
            if (_dmgLastBody.TryGetValue(h, out lastBody) && lastBody - body >= 8f)
            {
                dent = DentFromCollision(v, lastBody - body);
            }
            _dmgLastBody[h] = body;
            DmgState d = ReadDamage(v);
            DmgState prev;
            bool had = _dmgSent.TryGetValue(h, out prev);
            int lastMs;
            _dmgSentMs.TryGetValue(h, out lastMs);
            bool changed = !had ? !IsPristine(d) : !SameDamage(d, prev);
            bool refresh = !IsPristine(d) && Game.GameTime - lastMs > 3000;   // nye speilbiler / tapte pakker
            if (dent == null && !changed && !refresh) continue;
            _dmgSent[h] = d;
            _dmgSentMs[h] = Game.GameTime;
            SendRaw(string.Format(CultureInfo.InvariantCulture, "VDMG|{0},{1},{2},{3},{4},{5},{6},{7},{8}",
                kv.Key, d.Eng, d.Body, d.Tank, d.Doors, d.Wins, d.Tyres, d.Dirt, dent ?? ""));
        }
        if (_dmgLastBody.Count > 400) { _dmgLastBody.Clear(); _dmgSent.Clear(); _dmgSentMs.Clear(); }
    }

    private void HandleVehicleDamage(string payload)
    {
        string[] f = payload.Split(',');
        if (f.Length < 8) return;
        int id; DmgState d = new DmgState();
        if (!int.TryParse(f[0], out id) || !int.TryParse(f[1], out d.Eng) || !int.TryParse(f[2], out d.Body)
            || !int.TryParse(f[3], out d.Tank) || !int.TryParse(f[4], out d.Doors) || !int.TryParse(f[5], out d.Wins)
            || !int.TryParse(f[6], out d.Tyres) || !int.TryParse(f[7], out d.Dirt)) return;
        Vehicle v = null;
        if (id == -1) v = _guestVeh;
        else if (_isGuestRole)
        {
            _syncedVehs.TryGetValue(id, out v);
            if ((v == null || !v.Exists()) && id == _targetVehHostId) v = _guestVeh;
        }
        else
        {
            try { v = Entity.FromHandle(id) as Vehicle; } catch { v = null; }
        }
        if (v == null || !v.Exists()) return;
        int h = v.Handle;
        DmgState had;
        bool hadAny = _dmgApplied.TryGetValue(h, out had);
        if (IsPristine(d))
        {
            if (hadAny && !IsPristine(had))
            {
                Function.Call(Hash.SET_VEHICLE_FIXED, h);
                Function.Call(Hash.SET_VEHICLE_DEFORMATION_FIXED, h);
            }
        }
        else
        {
            if (v.IsInvincible) v.IsInvincible = false;
            Function.Call(Hash.SET_VEHICLE_CAN_BE_VISIBLY_DAMAGED, h, true);
            Function.Call(Hash.SET_VEHICLE_ENGINE_HEALTH, h, (float)d.Eng);
            Function.Call(Hash.SET_VEHICLE_BODY_HEALTH, h, (float)d.Body);
            Function.Call(Hash.SET_VEHICLE_PETROL_TANK_HEALTH, h, (float)d.Tank);
            for (int i = 0; i < 6; i++)
                if ((d.Doors & (1 << i)) != 0 && (!hadAny || (had.Doors & (1 << i)) == 0))
                    Function.Call(Hash.SET_VEHICLE_DOOR_BROKEN, h, i, false);
            for (int i = 0; i < 8; i++)
                if ((d.Wins & (1 << i)) != 0 && (!hadAny || (had.Wins & (1 << i)) == 0))
                    Function.Call(Hash.SMASH_VEHICLE_WINDOW, h, i);
            for (int i = 0; i < TYRE_IDX.Length; i++)
                if ((d.Tyres & (1 << i)) != 0 && (!hadAny || (had.Tyres & (1 << i)) == 0))
                    Function.Call(Hash.SET_VEHICLE_TYRE_BURST, h, TYRE_IDX[i], true, 1000f);
        }
        Function.Call(Hash.SET_VEHICLE_DIRT_LEVEL, h, Math.Max(0f, Math.Min(15f, d.Dirt / 10f)));
        if (f.Length >= 9 && !string.IsNullOrEmpty(f[8]))
        {
            foreach (string dent in f[8].Split('/'))
            {
                string[] p = dent.Split(':');
                float x, y, z, force;
                if (p.Length == 4 && TryF(p[0], out x) && TryF(p[1], out y) && TryF(p[2], out z) && TryF(p[3], out force))
                    Function.Call(Hash.SET_VEHICLE_DAMAGE, h, x, y, z, force, 0.8f, true);
            }
        }
        _dmgApplied[h] = d;
        if (_dmgApplied.Count > 400) _dmgApplied.Clear();
    }

    // v0.9.2: skjul alt som allerede står rundt mellomsekvensen hos gjesten (gjestens egne oppdragsfigurer/-biler, rester),
    // slik at bare figurene mellomsekvensen selv lager vises. Det var de doble modellene.
    private void HideEntitiesForGuestCutscene(Vector3 center)
    {
        Ped me = Game.Player.Character;
        foreach (Ped p in World.GetNearbyPeds(center, 150.0f))
        {
            if (p == null || !p.Exists() || p == me || p.IsPlayer || !p.IsVisible) continue;
            p.IsVisible = false;
            Function.Call(Hash.SET_ENTITY_COLLISION, p.Handle, false, false);
            p.IsPositionFrozen = true;
            _csHiddenEntities.Add(p);
        }
        foreach (Vehicle v in World.GetNearbyVehicles(center, 120.0f))
        {
            if (v == null || !v.Exists() || !v.IsVisible) continue;
            if (me != null && me.Exists() && me.IsInVehicle(v)) continue;
            v.IsVisible = false;
            Function.Call(Hash.SET_ENTITY_COLLISION, v.Handle, false, false);
            v.IsPositionFrozen = true;
            _csHiddenEntities.Add(v);
        }
        SendRaw("EVENT|guest_cutscene_hidden|" + _csHiddenEntities.Count);
    }

    private void RestoreEntitiesAfterGuestCutscene()
    {
        foreach (Entity e in _csHiddenEntities)
        {
            try
            {
                if (e == null || !e.Exists()) continue;
                e.IsVisible = true;
                Function.Call(Hash.SET_ENTITY_COLLISION, e.Handle, true, true);
                e.IsPositionFrozen = false;
            }
            catch { }
        }
        _csHiddenEntities.Clear();
    }

    private static void RegisterCutsceneProtagonist(string csHandle, string modelName)
    {
        try
        {
            if (Function.Call<bool>(Hash.DOES_CUTSCENE_HANDLE_EXIST, csHandle))
            {
                int mh = Function.Call<int>(Hash.GET_HASH_KEY, modelName);
                Function.Call(Hash.SET_CUTSCENE_ENTITY_STREAMING_FLAGS, csHandle, 0, 1);
                // 3 = CU_CREATE_AND_ANIMATE_NEW_SCRIPT_ENTITY: GTA V oppretter og animerer karakteren i cutscenen uten lokale duplikater!
                Function.Call(Hash.REGISTER_ENTITY_FOR_CUTSCENE, 0, csHandle, 3, mh, 0);
            }
        }
        catch { }
    }

    private void StartOrSyncGuestCutscene(string payload)
    {
        // Format: csName,cx,cy,cz
        if (string.IsNullOrEmpty(payload)) return;
        string[] cf = payload.Split(',');
        if (cf.Length < 4) return;
        string csName = cf[0].Trim();
        float cx, cy, cz;
        if (string.IsNullOrEmpty(csName) || !TryF(cf[1], out cx) || !TryF(cf[2], out cy) || !TryF(cf[3], out cz)) return;
        if (_guestCsState != 0 && _guestActiveCsName == csName) return;

        try
        {
            if (_guestCsState != 0)
            {
                Function.Call(Hash.STOP_CUTSCENE_IMMEDIATELY);
                Function.Call(Hash.REMOVE_CUTSCENE);
            }
            _guestActiveCsName = csName;
            _guestCsState = 1;
            _guestCsReqStartMs = Game.GameTime;
            _guestCsEntitiesRegistered = false;
            _hostCutsceneCenter = new Vector3(cx, cy, cz);

            // Fjern alle co-op speil-NPC-er og speil-biler midlertidig + skjul Gjestens egen ped og motspiller-ped -> 0 % doble karakterer!
            CleanupSyncedNpcs();
            CleanupSyncedVehs();
            if (_guestPed != null && _guestPed.Exists())
            {
                _guestPed.IsVisible = false;
            }
            Ped me = Game.Player.Character;
            if (me != null && me.Exists())
            {
                if (me.IsInVehicle()) Function.Call(Hash.CLEAR_PED_TASKS_IMMEDIATELY, me.Handle);
                Function.Call(Hash.REQUEST_COLLISION_AT_COORD, cx, cy, cz);
                me.IsPositionFrozen = true;
                me.IsVisible = false;
                Function.Call(Hash.SET_ENTITY_COLLISION, me.Handle, false, false);
                me.PositionNoOffset = _hostCutsceneCenter;
            }

            // Pre-request hovedmodellene så de er klare i minnet
            new Model("player_zero").Request();
            new Model("player_one").Request();
            new Model("player_two").Request();
            new Model("ig_lamardavis").Request();

            RestoreEntitiesAfterGuestCutscene();
            HideEntitiesForGuestCutscene(_hostCutsceneCenter);
            if (csName == "?")
            {
                // v0.9.2: verten vet ikke navnet -> ingen avspilling her, men skjul speilfigurene og vertens figur (ingen flyting)
                _guestCsState = 3;
                SendRaw("EVENT|guest_cutscene_unknown_hidden");
                return;
            }
            Function.Call(Hash.REQUEST_CUTSCENE, csName, 8);
            SendRaw("EVENT|guest_cutscene_loading|" + csName);
        }
        catch (Exception ex)
        {
            SendRaw("ERR|guest_cutscene|" + ex.Message);
            _guestCsState = 0;
            _guestActiveCsName = "";
        }
    }

    private void StopGuestCutscene(Vector3 endPos, float endHeading)
    {
        bool wasActive = (_guestCsState != 0);
        RestoreEntitiesAfterGuestCutscene();
        _guestCsState = 0;
        _guestActiveCsName = "";
        _guestCsEntitiesRegistered = false;
        try
        {
            if (Function.Call<bool>(Hash.IS_CUTSCENE_ACTIVE) || Function.Call<bool>(Hash.IS_CUTSCENE_PLAYING))
            {
                Function.Call(Hash.STOP_CUTSCENE_IMMEDIATELY);
            }
            Function.Call(Hash.REMOVE_CUTSCENE);
        }
        catch { }

        Ped me = Game.Player.Character;
        if (me != null && me.Exists())
        {
            me.IsVisible = true;
            me.Opacity = 255;
            Function.Call(Hash.SET_ENTITY_COLLISION, me.Handle, true, true);
            if (wasActive && endPos.Length() > 1.0f)
            {
                SafeTeleportLocalPlayer(endPos + new Vector3(2.2f, 0f, 0.5f), endHeading);
            }
            else
            {
                me.IsPositionFrozen = false;
            }
        }
        if (_guestPed != null && _guestPed.Exists())
        {
            _guestPed.IsVisible = true;
        }
        if (wasActive)
        {
            SendRaw("EVENT|guest_cutscene_stopped");
        }
    }

    private void UpdateGuestCutsceneTick(Ped me)
    {
        if (!_isGuestRole || _guestCsState == 0) return;

        // Hold co-op-figurer 100 % skjult mens 1:1 cutscenen pågår -> 0 % duplikater i rommet!
        if (me != null && me.Exists() && me.IsVisible) me.IsVisible = false;
        if (_guestPed != null && _guestPed.Exists() && _guestPed.IsVisible) _guestPed.IsVisible = false;
        Function.Call(Hash.SET_LOCAL_PLAYER_VISIBLE_IN_CUTSCENE, false, false);

        if (_guestCsState == 1)
        {
            if (!_guestCsEntitiesRegistered && Function.Call<bool>(Hash.CAN_REQUEST_ASSETS_FOR_CUTSCENE_ENTITY))
            {
                RegisterCutsceneProtagonist("Michael", "player_zero");
                RegisterCutsceneProtagonist("Franklin", "player_one");
                RegisterCutsceneProtagonist("Trevor", "player_two");
                RegisterCutsceneProtagonist("Lamar", "ig_lamardavis");
                RegisterCutsceneProtagonist("Simeon", "ig_siemonyetarian");
                RegisterCutsceneProtagonist("Lester", "ig_lestercrest");
                RegisterCutsceneProtagonist("Amanda", "ig_amandatownley");
                RegisterCutsceneProtagonist("Jimmy", "ig_jimmydisanto");
                RegisterCutsceneProtagonist("Tracey", "ig_tracydisanto");
                RegisterCutsceneProtagonist("Dave", "ig_davenorton");
                RegisterCutsceneProtagonist("Steve", "ig_stevehains");
                RegisterCutsceneProtagonist("Devin", "ig_devin");
                RegisterCutsceneProtagonist("Stretch", "ig_stretch");
                RegisterCutsceneProtagonist("Tonya", "ig_tonya");
                _guestCsEntitiesRegistered = true;
            }
            if (Function.Call<bool>(Hash.HAS_CUTSCENE_LOADED))
            {
                Function.Call(Hash.SET_LOCAL_PLAYER_VISIBLE_IN_CUTSCENE, false, false);
                Function.Call(Hash.START_CUTSCENE, 0);
                _guestCsState = 2;
                _guestCsPlayStartMs = Game.GameTime;
                SendRaw("EVENT|guest_cutscene_playing|" + _guestActiveCsName);
            }
            else if (Game.GameTime - _guestCsReqStartMs > 6500)
            {
                StopGuestCutscene(_targetPos, _targetHeading);
            }
        }
        else if (_guestCsState == 2)
        {
            bool stillPlaying = Function.Call<bool>(Hash.IS_CUTSCENE_ACTIVE) || Function.Call<bool>(Hash.IS_CUTSCENE_PLAYING);
            if (Function.Call<bool>(Hash.HAS_CUTSCENE_FINISHED)
                || (Game.GameTime - _guestCsPlayStartMs > 1200 && !stillPlaying))
            {
                StopGuestCutscene(_targetPos, _targetHeading);
            }
        }
    }

    private void SafeTeleportLocalPlayer(Vector3 dest, float heading)
    {
        try
        {
            Ped me = Game.Player.Character;
            if (me == null || !me.Exists()) return;
            if (me.IsInVehicle())
            {
                Function.Call(Hash.CLEAR_PED_TASKS_IMMEDIATELY, me.Handle);
            }
            Function.Call(Hash.REQUEST_COLLISION_AT_COORD, dest.X, dest.Y, dest.Z);
            me.IsPositionFrozen = true;
            me.PositionNoOffset = dest;
            me.Heading = heading;
            _safeTpPos = dest;
            _safeTpStartMs = Game.GameTime;
            _safeTpWaitingGround = true;
        }
        catch { }
    }

    private void SpawnGuest(string modelName, Vector3 pos, float heading)
    {
        CleanupGuest();
        try
        {
            Model m = new Model(modelName);
            if (!m.IsValid) return;
            m.Request(1500);
            if (!m.IsLoaded) return;

            Ped me = Game.Player.Character;
            Vector3 spawnPos = pos;
            if (me != null && me.Exists() && spawnPos.Length() < 0.1f)
            {
                spawnPos = me.Position + me.ForwardVector * 2.0f;
            }

            Ped p = World.CreatePed(m, spawnPos, heading);
            m.MarkAsNoLongerNeeded();
            if (p == null || !p.Exists())
            {
                SendRaw("ERR|spawn|create_failed_" + modelName);
                return;
            }

            Function.Call(Hash.SET_PED_DEFAULT_COMPONENT_VARIATION, p.Handle);
            p.PositionNoOffset = spawnPos;
            p.Heading = heading;
            p.IsVisible = (_guestCsState == 0);
            p.Opacity = 255;
            // VIKTIG: IsInvincible = true + SuffersCriticalHits = false hindrer GTA Vs 1-slags NPC-knockout (som ga lagg/RESURRECT-loop)!
            // Skade mellom spillere detekteres rent via skuddlinje/slag-kjegle og påføres kun den ekte spilleren via PVP_HIT!
            p.IsInvincible = true;
            p.BlockPermanentEvents = true;
            p.CanRagdoll = false;
            p.IsPersistent = true;
            Function.Call(Hash.SET_ENTITY_COLLISION, p.Handle, true, true);
            Function.Call(Hash.SET_PED_SUFFERS_CRITICAL_HITS, p.Handle, false);
            Function.Call(Hash.SET_PED_CAN_RAGDOLL_FROM_PLAYER_IMPACT, p.Handle, false);
            Function.Call(Hash.SET_PED_CAN_EVASIVE_DIVE, p.Handle, false);
            Function.Call(Hash.SET_PED_CAN_BE_KNOCKED_OFF_VEHICLE, p.Handle, 1);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, p.Handle, 5, false);  // AlwaysFight = false (0 % AI-slåssing!)
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, p.Handle, 17, false); // AlwaysFlee = false
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, p.Handle, 46, false); // FightArmedWhenUnarmed = false
            Function.Call(Hash.SET_PED_CONFIG_FLAG, p.Handle, 43, false);  // Tillat spiller-sikting!
            Function.Call(Hash.SET_PED_CONFIG_FLAG, p.Handle, 128, false); // CanBeAgitated = false
            Function.Call(Hash.SET_PED_CONFIG_FLAG, p.Handle, 208, true);  // DisableExplosionReactions
            Function.Call(Hash.SET_PED_CONFIG_FLAG, p.Handle, 294, true);  // DisableShockingEvents
            Function.Call(Hash.TASK_SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, p.Handle, true);
            if (me != null && me.Exists())
            {
                if (!_peerRelGroupInit)
                {
                    OutputArgument outRg = new OutputArgument();
                    Function.Call(Hash.ADD_RELATIONSHIP_GROUP, "_COOP_PEER", outRg);
                    _peerRelGroup = outRg.GetResult<int>();
                    _peerRelGroupInit = true;
                }
                int myRg = Function.Call<int>(Hash.GET_PED_RELATIONSHIP_GROUP_HASH, me.Handle);
                Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, 3, _peerRelGroup, myRg); // 3 = Neutral (kan skyte og slåss fritt, uten auto-AI!)
                Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, 3, myRg, _peerRelGroup);
                Function.Call(Hash.SET_CAN_ATTACK_FRIENDLY, me.Handle, true, false);
                Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH, p.Handle, _peerRelGroup);
            }

            _guestPed = p;
            _guestModelName = modelName;
            _lastAppliedOutfit = "";
            _lastAppliedWeapon = 0;
            SendRaw("EVENT|spawned|" + modelName);
        }
        catch (Exception ex)
        {
            SendRaw("ERR|spawn|" + ex.Message);
        }
    }

    private void OnTick(object sender, EventArgs e)
    {
        PollGui();
        if (Idle) return;   // v0.9.2: feilsøking – modden gjør ingenting

        Ped me = Game.Player.Character;
        if (me == null || !me.Exists()) return;
        if (!_startupTasksCleared)
        {
            _startupTasksCleared = true;
            Function.Call(Hash.SET_PLAYER_CONTROL, Game.Player.Handle, true, 0);
            if (!me.IsInVehicle())
            {
                Function.Call(Hash.CLEAR_PED_TASKS_IMMEDIATELY, me.Handle);
            }
        }

        // Fullfør kollisjonssikker teleportering: hold spiller frosset over bakken til bakkekollisjonen er lastet!
        if (_safeTpWaitingGround)
        {
            Function.Call(Hash.REQUEST_COLLISION_AT_COORD, _safeTpPos.X, _safeTpPos.Y, _safeTpPos.Z);
            bool loaded = Function.Call<bool>(Hash.HAS_COLLISION_LOADED_AROUND_ENTITY, me.Handle);
            if (loaded || Game.GameTime - _safeTpStartMs > 1800)
            {
                OutputArgument outGz = new OutputArgument();
                if (Function.Call<bool>(Hash.GET_GROUND_Z_FOR_3D_COORD, _safeTpPos.X, _safeTpPos.Y, _safeTpPos.Z + 8.0f, outGz, false))
                {
                    float gz = outGz.GetResult<float>();
                    if (gz > -150.0f) me.PositionNoOffset = new Vector3(_safeTpPos.X, _safeTpPos.Y, gz + 1.0f);
                }
                me.IsPositionFrozen = false;
                _safeTpWaitingGround = false;
            }
            else
            {
                me.PositionNoOffset = _safeTpPos;
            }
        }

        // v0.9.2: Gjest – en mellomsekvens som ikke kommer fra Verten (henger igjen fra en tidligere synk eller gjestens eget
        // oppdrag) styrer klokka (hopper fra klipp til klipp) og lyset. Stopp den hvis den varer over 3 s uten Vertens CUTSCENE.
        if (_isGuestRole && _guestCsState == 0 && Function.Call<bool>(Hash.IS_CUTSCENE_ACTIVE))
        {
            if (_strayCsSinceMs == 0) _strayCsSinceMs = Game.GameTime;
            else if (Game.GameTime - _strayCsSinceMs > 3000 && _strayCsTries < 2)
            {
                _strayCsSinceMs = 0;
                _strayCsTries++;
                Function.Call(Hash.STOP_CUTSCENE_IMMEDIATELY);
                Function.Call(Hash.REMOVE_CUTSCENE);
                SendRaw("EVENT|stray_cutscene_stopped|try=" + _strayCsTries);
                if (_strayCsTries == 2 && Function.Call<bool>(Hash.GET_MISSION_FLAG))
                    SendRaw("ERR|guest_own_mission_active|Gjestens eget oppdrag holder en mellomsekvens aktiv – last en lagring utenfor oppdrag");
            }
        }
        else
        {
            _strayCsSinceMs = 0;
        }

        // Gjest: 0 % lokal tilfeldig trafikk/fotgjengere (alt kommer fra Verten, akkurat som RAGECOOP-V / FiveM!) + 5s Co-op respawn
        if (_isGuestRole)
        {
            Function.Call(Hash.SET_VEHICLE_DENSITY_MULTIPLIER_THIS_FRAME, 0.0f);
            Function.Call(Hash.SET_RANDOM_VEHICLE_DENSITY_MULTIPLIER_THIS_FRAME, 0.0f);
            Function.Call(Hash.SET_PARKED_VEHICLE_DENSITY_MULTIPLIER_THIS_FRAME, 0.0f);
            Function.Call(Hash.SET_PED_DENSITY_MULTIPLIER_THIS_FRAME, 0.0f);
            Function.Call(Hash.SET_SCENARIO_PED_DENSITY_MULTIPLIER_THIS_FRAME, 0.0f, 0.0f);

            if (!_deathGuardsApplied)
            {
                _deathGuardsApplied = true;
                Function.Call(Hash.SET_FADE_OUT_AFTER_DEATH, false);
                Function.Call(Hash.PAUSE_DEATH_ARREST_RESTART, true);
                Function.Call(Hash.SET_GARBAGE_TRUCKS, false);
                Function.Call(Hash.SET_CREATE_RANDOM_COPS, false);
                Function.Call(Hash.SET_DISPATCH_COPS_FOR_PLAYER, Game.Player.Handle, false);
                for (int ds = 1; ds <= 15; ds++)
                {
                    Function.Call(Hash.ENABLE_DISPATCH_SERVICE, ds, false);
                }
            }
            // Hvis Gjesten begår et lovbrudd som gir flere stjerner lokalt, send WANTED_REQ til Verten så politiet kommer!
            if (Game.Player.WantedLevel > _hostAuthoritativeWanted
                && Game.GameTime - _lastWantedAcceptMs > 1200
                && Game.GameTime - _lastGuestWantedReqMs > 800)
            {
                _lastGuestWantedReqMs = Game.GameTime;
                _hostAuthoritativeWanted = Game.Player.WantedLevel;
                SendRaw(string.Format(CultureInfo.InvariantCulture, "WANTED_REQ|{0}|{1:F1}|{2:F1}|{3:F1}",
                    Game.Player.WantedLevel, me.Position.X, me.Position.Y, me.Position.Z));
            }
            // Fjern eventuelle gjenværende lokale tilfeldige gatebiler på Gjesten (så kun Vertens trafikk eksisterer!)
            if (Game.GameTime - _lastTrafficPurgeMs >= 1000)
            {
                _lastTrafficPurgeMs = Game.GameTime;
                HashSet<int> syncedVehHandles = new HashSet<int>();
                foreach (Vehicle sv in _syncedVehs.Values)
                {
                    if (sv != null && sv.Exists()) syncedVehHandles.Add(sv.Handle);
                }
                Vehicle[] allLocalVehs = World.GetAllVehicles();
                foreach (Vehicle lv in allLocalVehs)
                {
                    if (lv != null && lv.Exists() && lv != _guestVeh && !syncedVehHandles.Contains(lv.Handle)
                        && !me.IsInVehicle(lv) && !Function.Call<bool>(Hash.IS_ENTITY_A_MISSION_ENTITY, lv.Handle))
                    {
                        try { lv.Delete(); } catch { }
                    }
                }
                // Rens også bort ikke-synkede sivile fotgjengere/foreldreløse kloner på Gjesten!
                HashSet<int> validPedHandles = new HashSet<int>();
                foreach (Ped sp in _syncedNpcs.Values) if (sp != null && sp.Exists()) validPedHandles.Add(sp.Handle);
                foreach (Ped dp in _syncedVehDrivers.Values) if (dp != null && dp.Exists()) validPedHandles.Add(dp.Handle);
                foreach (Ped pp in _syncedVehPass.Values) if (pp != null && pp.Exists()) validPedHandles.Add(pp.Handle);
                Ped[] allLocalPeds = World.GetAllPeds();
                foreach (Ped lp in allLocalPeds)
                {
                    if (lp != null && lp.Exists() && lp != me && lp != _guestPed && !lp.IsPlayer
                        && !validPedHandles.Contains(lp.Handle) && !_hiddenLocalNpcs.ContainsKey(lp.Handle)
                        && !Function.Call<bool>(Hash.IS_ENTITY_A_MISSION_ENTITY, lp.Handle))
                    {
                        try { lp.Delete(); } catch { }
                    }
                }
            }
            if (me.IsDead || Game.Player.IsDead)
            {
                if (_guestDeathStartMs == 0)
                {
                    _guestDeathStartMs = Game.GameTime;
                    SendRaw("EVENT|guest_died_waiting_5s");
                }
                else if (Game.GameTime - _guestDeathStartMs >= 5000)
                {
                    _guestDeathStartMs = 0;
                    Vector3 rp = me.Position;
                    float rh = me.Heading;
                    if (_hasTarget)
                    {
                        rp = (_targetVehModel != 0)
                            ? (_targetVehPos + new Vector3(2.5f, 0f, 0.5f))
                            : (_targetPos + new Vector3(1.8f, 0f, 0.2f));
                        rh = _targetHeading;
                    }
                    Function.Call(Hash.IGNORE_NEXT_RESTART, true);
                    Function.Call(Hash.NETWORK_RESURRECT_LOCAL_PLAYER, rp.X, rp.Y, rp.Z, rh, true, false);
                    Function.Call(Hash.ANIMPOSTFX_STOP_ALL);
                    Function.Call(Hash.DISPLAY_HUD, true);
                    Function.Call(Hash.DISPLAY_RADAR, true);
                    Ped rMe = Game.Player.Character;
                    if (rMe != null && rMe.Exists())
                    {
                        Function.Call(Hash.CLEAR_PED_BLOOD_DAMAGE, rMe.Handle);
                        rMe.Health = rMe.MaxHealth;
                        // Hvis Verten sitter i en bil med ledig sete, plasser gjesten rett inn i Vertens bil!
                        if (_targetVehModel != 0 && _guestVeh != null && _guestVeh.Exists())
                        {
                            int sIdx = -2;
                            if (_guestVeh.IsSeatFree(VehicleSeat.Passenger)) sIdx = 0;
                            else if (_guestVeh.IsSeatFree(VehicleSeat.LeftRear)) sIdx = 1;
                            else if (_guestVeh.IsSeatFree(VehicleSeat.RightRear)) sIdx = 2;
                            if (sIdx >= 0)
                            {
                                Function.Call(Hash.TASK_WARP_PED_INTO_VEHICLE, rMe.Handle, _guestVeh.Handle, sIdx);
                            }
                        }
                    }
                    _lastSyncedInv = ""; // tving våpen-synk på nytt etter respawn
                    _lastSelfCheckMs = 0;
                    SendRaw("EVENT|guest_respawned_on_host");
                    return;
                }
            }
            else
            {
                _guestDeathStartMs = 0;
            }
        }
        else if (_deathGuardsApplied)
        {
            _deathGuardsApplied = false;
            Function.Call(Hash.SET_FADE_OUT_AFTER_DEATH, true);
            Function.Call(Hash.PAUSE_DEATH_ARREST_RESTART, false);
            Function.Call(Hash.SET_CREATE_RANDOM_COPS, true);
            Function.Call(Hash.SET_DISPATCH_COPS_FOR_PLAYER, Game.Player.Handle, true);
            for (int ds = 1; ds <= 15; ds++)
            {
                Function.Call(Hash.ENABLE_DISPATCH_SERVICE, ds, true);
            }
        }
        // Vert i Free Roam (utenfor aktivt oppdrag): bruk 5-sekunders Co-op Respawn ved PvP-død så man ikke sendes på sykehuset!
        if (!_isGuestRole)
        {
            bool inMission = Function.Call<bool>(Hash.GET_MISSION_FLAG);
            if (!inMission && _hasTarget && _guestPed != null && _guestPed.Exists())
            {
                if (!_hostFreeRoamGuards)
                {
                    _hostFreeRoamGuards = true;
                    Function.Call(Hash.SET_FADE_OUT_AFTER_DEATH, false);
                    Function.Call(Hash.PAUSE_DEATH_ARREST_RESTART, true);
                }
                if (me.IsDead || Game.Player.IsDead)
                {
                    if (_hostFreeRoamDeathMs == 0) _hostFreeRoamDeathMs = Game.GameTime;
                    else if (Game.GameTime - _hostFreeRoamDeathMs >= 5000)
                    {
                        _hostFreeRoamDeathMs = 0;
                        Vector3 rp = me.Position + new Vector3(3.0f, 3.0f, 0.5f);
                        Function.Call(Hash.IGNORE_NEXT_RESTART, true);
                        Function.Call(Hash.NETWORK_RESURRECT_LOCAL_PLAYER, rp.X, rp.Y, rp.Z, me.Heading, true, false);
                        Function.Call(Hash.ANIMPOSTFX_STOP_ALL);
                        Function.Call(Hash.DISPLAY_HUD, true);
                        Function.Call(Hash.DISPLAY_RADAR, true);
                        Ped rMe = Game.Player.Character;
                        if (rMe != null && rMe.Exists())
                        {
                            Function.Call(Hash.CLEAR_PED_BLOOD_DAMAGE, rMe.Handle);
                            rMe.Health = rMe.MaxHealth;
                        }
                        return;
                    }
                }
                else
                {
                    _hostFreeRoamDeathMs = 0;
                }
            }
            else if (_hostFreeRoamGuards)
            {
                _hostFreeRoamGuards = false;
                Function.Call(Hash.SET_FADE_OUT_AFTER_DEATH, true);
                Function.Call(Hash.PAUSE_DEATH_ARREST_RESTART, false);
            }
        }
        Function.Call(Hash.SET_PED_CAN_RAGDOLL_FROM_PLAYER_IMPACT, me.Handle, false);
        Function.Call(Hash.SET_CAN_ATTACK_FRIENDLY, me.Handle, true, false);

        // Hvis spillet/Prologue byttet gjesten tilbake til Michael, sett tilbake til ønsket gjestefigur hvert 3. sek
        if (!string.IsNullOrEmpty(_wantedSelfModel) && Game.GameTime - _lastSelfCheckMs >= 3000)
        {
            _lastSelfCheckMs = Game.GameTime;
            if (me.Model.Hash != _wantedSelfHash)
            {
                ApplySelfModel(_wantedSelfModel);
            }
        }

        // v0.9.2: Ved motspillerens bil: spillets egen «gå inn i bil» (F) sperres HVER frame, ellers rakk spillet å starte sin
        // egen kapring (dra ut sjåføren) i samme frame som vi sjekket tasten -> verten ble kastet ut hos gjesten, men satt fortsatt
        // i bilen hos verten. Vi sender i stedet spilleren til første ledige passasjersete. Andre biler (NPC) virker som vanlig.
        if (_guestPed != null && _guestPed.Exists())
        {
            Function.Call(Hash.SET_PED_CAN_BE_DRAGGED_OUT, _guestPed.Handle, false);
        }
        if (!me.IsInVehicle() && _targetVehModel != 0 && _guestVeh != null && _guestVeh.Exists()
            && me.Position.DistanceTo(_guestVeh.Position) < 9.0f)
        {
            Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 23, true);   // INPUT_ENTER
            Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 75, true);   // INPUT_VEH_EXIT (samme tast)
            if (Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, 23)
                && !Function.Call<bool>(Hash.IS_PED_GETTING_INTO_A_VEHICLE, me.Handle))
            {
                int freeSeat = -2;
                int peerSeat = _targetVehSeat;
                if (peerSeat != -1 && _guestVeh.IsSeatFree(VehicleSeat.Driver)) freeSeat = -1;   // verten er passasjer -> vi kjører
                else if (peerSeat != 0 && _guestVeh.IsSeatFree(VehicleSeat.Passenger)) freeSeat = 0;
                else if (peerSeat != 1 && _guestVeh.IsSeatFree(VehicleSeat.LeftRear)) freeSeat = 1;
                else if (peerSeat != 2 && _guestVeh.IsSeatFree(VehicleSeat.RightRear)) freeSeat = 2;
                if (freeSeat != -2)
                {
                    Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED, _guestVeh.Handle, 1);
                    Function.Call(Hash.TASK_ENTER_VEHICLE, me.Handle, _guestVeh.Handle, 5000, freeSeat, 2.0f, 1, 0);
                    SendRaw("EVENT|enter_peer_vehicle|seat=" + freeSeat);
                }
            }
        }

        // Oppdater motspillerens ped (synlighet, klær, våpen, ekte gå-/løpe-bevegelse)
        if (_guestPed != null && _guestPed.Exists())
        {
            if (_syncEnabled && _hasTarget)
            {
                if (Game.GameTime - _lastRxMs > 10000)
                {
                    CleanupGuest();
                }
                else
                {
                    _guestPed.IsVisible = (_guestCsState == 0);
                    Function.Call(Hash.REQUEST_COLLISION_AT_COORD, _targetPos.X, _targetPos.Y, _targetPos.Z);
                    if (_targetVehModel == 0 && !Function.Call<bool>(Hash.HAS_COLLISION_LOADED_AROUND_ENTITY, _guestPed.Handle))
                    {
                        // Kollisjonsvakt på motspiller: aldri fall gjennom bakken før terrenget er lastet!
                        _guestPed.IsPositionFrozen = true;
                        _guestPed.PositionNoOffset = _targetPos;
                    }
                    else
                    {
                        _guestPed.IsPositionFrozen = false;
                    }

                    // Klær-synk
                    if (!string.IsNullOrEmpty(_targetOutfit) && _targetOutfit != _lastAppliedOutfit)
                    {
                        ApplyOutfit(_guestPed, _targetOutfit);
                        _lastAppliedOutfit = _targetOutfit;
                    }

                    // Våpen-synk (også tilbake til Unarmed/knyttnever når motspilleren tar bort våpenet for å slåss!)
                    uint wantWp = _targetWeapon == 0 ? (uint)WeaponHash.Unarmed : _targetWeapon;
                    if (wantWp != _lastAppliedWeapon)
                    {
                        if (wantWp != (uint)WeaponHash.Unarmed)
                        {
                            Function.Call(Hash.GIVE_WEAPON_TO_PED, _guestPed.Handle, wantWp, 999, false, true);
                        }
                        Function.Call(Hash.SET_CURRENT_PED_WEAPON, _guestPed.Handle, wantWp, true);
                        _lastAppliedWeapon = wantWp;
                    }

                    // Null-lagg PvP treff-deteksjon (ingen NPC-knockout eller RESURRECT-stutter!):
                    // 1) Slag / nærkamp mot medspiller (innen 2.2 m foran oss når vi faktisk trykker slag/angrep)
                    uint myCurWp = 0;
                    try { myCurWp = (uint)me.Weapons.Current.Hash; } catch { }
                    bool myHasFirearm = (myCurWp != 0 && myCurWp != (uint)WeaponHash.Unarmed
                        && myCurWp != (uint)WeaponHash.Knife && myCurWp != (uint)WeaponHash.Bat
                        && myCurWp != (uint)WeaponHash.Crowbar && myCurWp != (uint)WeaponHash.Nightstick
                        && myCurWp != (uint)WeaponHash.Hammer && myCurWp != (uint)WeaponHash.GolfClub
                        && myCurWp != (uint)WeaponHash.Dagger && myCurWp != (uint)WeaponHash.Hatchet
                        && myCurWp != (uint)WeaponHash.Machete && myCurWp != (uint)WeaponHash.SwitchBlade);
                    bool myMeleeClick = (!myHasFirearm && Game.IsControlPressed(Control.Attack))
                        || Game.IsControlPressed(Control.MeleeAttackLight)
                        || Game.IsControlPressed(Control.MeleeAttackHeavy)
                        || Game.IsControlPressed(Control.MeleeAttackAlternate);
                    // v0.9.2: slag som HENDELSER. Start = knappen nettopp trykket eller spillet starter et slag (IS_PED_PERFORMING_MELEE_ACTION).
                    // Medspilleren får MELEE|lett/tungt straks, og treffet sjekkes ~0.22 s inn i slaget (når neven treffer).
                    bool performingMelee = Function.Call<bool>(Hash.IS_PED_PERFORMING_MELEE_ACTION, me.Handle);
                    bool meleeJust = Game.IsControlJustPressed(Control.MeleeAttackLight) || Game.IsControlJustPressed(Control.MeleeAttackHeavy)
                        || Game.IsControlJustPressed(Control.MeleeAttackAlternate) || (!myHasFirearm && Game.IsControlJustPressed(Control.Attack))
                        || (performingMelee && !_wasPerformingMelee);
                    _wasPerformingMelee = performingMelee;
                    if (!me.IsInVehicle() && meleeJust && Game.GameTime - _myMeleeStartMs >= 300)
                    {
                        _myMeleeStartMs = Game.GameTime;
                        _myMeleeHeavy = Game.IsControlPressed(Control.MeleeAttackHeavy) || Game.IsControlPressed(Control.MeleeAttackAlternate);
                        _myMeleeHitChecked = false;
                        SendRaw("MELEE|" + (_myMeleeHeavy ? 1 : 0));
                    }
                    if (!_myMeleeHitChecked && Game.GameTime - _myMeleeStartMs >= 220)
                    {
                        _myMeleeHitChecked = true;
                        Vector3 toG = _guestPed.Position - me.Position;
                        float d2g = (float)Math.Sqrt(toG.X * toG.X + toG.Y * toG.Y);
                        if (d2g <= 2.0f && Math.Abs(toG.Z) <= 1.6f)
                        {
                            float angToG = (float)(Math.Atan2(-toG.X, toG.Y) * (180.0 / Math.PI));
                            if (angToG < 0f) angToG += 360f;
                            if (Math.Abs(AngleDiff(me.Heading, angToG)) <= 65.0f)
                            {
                                _lastPvpHitSentMs = Game.GameTime;
                                bool unarmed = (myCurWp == 0 || myCurWp == (uint)WeaponHash.Unarmed);
                                int mDmg = unarmed ? (_myMeleeHeavy ? 25 : 15) : 35;
                                SendRaw("PVP_HIT|" + mDmg + "|" + (_myMeleeHeavy || !unarmed ? "heavy" : "light"));
                                // Vis treffet lokalt også: tungt slag slår medspillerens figur kort i bakken her
                                if (_myMeleeHeavy || !unarmed)
                                    Function.Call(Hash.SET_PED_TO_RAGDOLL, _guestPed.Handle, 700, 900, 0, false, false, false);
                            }
                        }
                    }
                    // 2) Skudd mot medspiller (sjekker om vi sikter rett på motspilleren når skuddet avfyres)
                    if (myHasFirearm && (me.IsShooting || Function.Call<bool>(Hash.IS_PED_SHOOTING, me.Handle))
                             && Game.GameTime - _lastPvpHitSentMs >= 140)
                    {
                        bool hitPeer = Function.Call<bool>(Hash.IS_PLAYER_FREE_AIMING_AT_ENTITY, Game.Player.Handle, _guestPed.Handle);
                        if (!hitPeer)
                        {
                            Vector3 camP = GameplayCamera.Position;
                            Vector3 camD = GameplayCamera.Direction;
                            Vector3 toCenter = (_guestPed.Position + new Vector3(0f, 0f, 0.25f)) - camP;
                            float proj = Vector3.Dot(toCenter, camD);
                            if (proj > 0.5f && proj < 95.0f)
                            {
                                Vector3 closest = camP + camD * proj;
                                if (closest.DistanceTo(_guestPed.Position + new Vector3(0f, 0f, 0.25f)) < 0.70f)
                                {
                                    hitPeer = true;
                                }
                            }
                        }
                        if (hitPeer)
                        {
                            _lastPvpHitSentMs = Game.GameTime;
                            SendRaw("PVP_HIT|24");
                        }
                    }
                    // 3) Påkjørsel med bil i fart
                    else if (me.IsInVehicle() && me.CurrentVehicle != null && me.CurrentVehicle.Velocity.Length() > 6.0f
                             && Game.GameTime - _lastPvpHitSentMs >= 850 && _guestPed.IsTouching(me.CurrentVehicle))
                    {
                        _lastPvpHitSentMs = Game.GameTime;
                        SendRaw("PVP_HIT|35");
                    }

                    // Eksakt kjøretøy-oppslag via vHostId + 40 Hz felles kjøring + ekte inn-/utstigningsanimasjoner!
                    bool peerEnteringVeh = (_targetPedFlags & 128) != 0;
                    if (_targetVehModel != 0)
                    {
                        _leavingVehUntilMs = 0;
                        Vehicle targetVeh = null;

                        // 1) Eksakt match på Vertens bil-handle (100 % treffsikkert selv med 10 identiske biler side om side!)
                        if (_targetVehHostId != 0)
                        {
                            if (_isGuestRole)
                            {
                                _syncedVehs.TryGetValue(_targetVehHostId, out targetVeh);
                            }
                            else
                            {
                                try { targetVeh = Entity.FromHandle(_targetVehHostId) as Vehicle; } catch { targetVeh = null; }
                            }
                            if (targetVeh != null && (!targetVeh.Exists() || targetVeh.Model.Hash != _targetVehModel))
                            {
                                targetVeh = null;
                            }
                        }

                        // 2) Lås til vår egen bil hvis vi allerede deler samme bilmodell
                        if (targetVeh == null && me.IsInVehicle() && me.CurrentVehicle != null && me.CurrentVehicle.Exists()
                            && me.CurrentVehicle.Model.Hash == _targetVehModel
                            && (me.CurrentVehicle == _guestVeh || _guestPed.IsInVehicle(me.CurrentVehicle)
                                || me.CurrentVehicle.Position.DistanceTo(_targetVehPos) < 45.0f))
                        {
                            targetVeh = me.CurrentVehicle;
                        }

                        if (targetVeh != null)
                        {
                            if (_guestVeh != null && _guestVeh.Exists() && _guestVeh != targetVeh && _guestVehCreatedByUs)
                            {
                                try { _guestVeh.Delete(); } catch { }
                            }
                            _guestVeh = targetVeh;
                            _guestVehCreatedByUs = false;
                            _guestVehModel = _targetVehModel;
                        }
                        else
                        {
                            if (_guestVeh == null || !_guestVeh.Exists() || _guestVehModel != _targetVehModel
                                || (!_guestVehCreatedByUs && !me.IsInVehicle(_guestVeh) && _guestVeh.Position.DistanceTo(_targetVehPos) > 40.0f))
                            {
                                Vehicle reuse = null;
                                float bestSyncD = 30.0f;
                                foreach (Vehicle sv in _syncedVehs.Values)
                                {
                                    if (sv != null && sv.Exists() && sv.Model.Hash == _targetVehModel)
                                    {
                                        float sd = sv.Position.DistanceTo(_targetVehPos);
                                        if (sd < bestSyncD) { bestSyncD = sd; reuse = sv; }
                                    }
                                }
                                if (reuse == null)
                                {
                                    Vehicle[] nearLocalVehs = World.GetNearbyVehicles(_targetVehPos, 28.0f);
                                    float bestVD = 999f;
                                    foreach (Vehicle lv in nearLocalVehs)
                                    {
                                        if (lv != null && lv.Exists() && lv.Model.Hash == _targetVehModel)
                                        {
                                            float d = lv.Position.DistanceTo(_targetVehPos);
                                            if (d < bestVD) { bestVD = d; reuse = lv; }
                                        }
                                    }
                                }
                                if (reuse != null)
                                {
                                    if (_guestVeh != null && _guestVeh.Exists() && _guestVeh != reuse && _guestVehCreatedByUs)
                                    {
                                        try { _guestVeh.Delete(); } catch { }
                                    }
                                    _guestVeh = reuse;
                                    _guestVehCreatedByUs = false;
                                    _guestVehModel = _targetVehModel;
                                }
                                else if (_targetVehSeat == -1 && !peerEnteringVeh)
                                {
                                    if (_guestVeh != null && _guestVeh.Exists() && _guestVehCreatedByUs)
                                    {
                                        try { _guestVeh.Delete(); } catch { }
                                    }
                                    Model vm = new Model(_targetVehModel);
                                    if (vm.IsValid)
                                    {
                                        if (!vm.IsLoaded)
                                        {
                                            vm.Request();
                                        }
                                        else
                                        {
                                            _guestVeh = World.CreateVehicle(vm, _targetVehPos, _targetVehHeading);
                                            vm.MarkAsNoLongerNeeded();
                                            if (_guestVeh != null && _guestVeh.Exists())
                                            {
                                                _guestVehCreatedByUs = true;
                                                _guestVeh.IsInvincible = true;
                                                _guestVeh.IsPersistent = true;
                                                Function.Call(Hash.SET_VEHICLE_COLOURS, _guestVeh.Handle, _targetVehC1, _targetVehC2);
                                                _guestVehModel = _targetVehModel;
                                            }
                                        }
                                    }
                                }
                            }
                            targetVeh = _guestVeh;
                        }

                        // VIKTIG: Ligger UTENFOR if/else slik at bilfysikken alltid kjører på 40 Hz når passasjeren sitter på i samme bil!
                        bool peerDrivesThis = !peerEnteringVeh && (_targetVehSeat == -1)
                            && !(targetVeh != null && me.IsInVehicle(targetVeh) && me.SeatIndex == VehicleSeat.Driver);
                        if (targetVeh != null && targetVeh.Exists() && peerDrivesThis)
                        {
                            if (targetVeh.IsPositionFrozen) targetVeh.IsPositionFrozen = false;
                            Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED, targetVeh.Handle, 1);
                            Function.Call(Hash.SET_VEHICLE_ENGINE_ON, targetVeh.Handle, true, true, false);
                            // Når vi sitter på som passasjer, deaktiver kollisjon mot speil-trafikkbiler så ingen spøkelseskollisjon dytter oss ut av kurs!
                            if (me.IsInVehicle(targetVeh))
                            {
                                foreach (Vehicle sv in _syncedVehs.Values)
                                {
                                    if (sv != null && sv.Exists() && sv != targetVeh && sv.Position.DistanceTo(targetVeh.Position) < 20.0f)
                                    {
                                        Function.Call(Hash.SET_ENTITY_NO_COLLISION_ENTITY, targetVeh.Handle, sv.Handle, false);
                                    }
                                }
                            }
                            float dt = Math.Min(0.15f, Math.Max(0f, (Game.GameTime - _lastPeerVehRxMs) / 1000.0f));
                            Vector3 predPos = _targetVehPos + _targetVehVel * (dt + 0.015f);
                            Vector3 vc = targetVeh.Position;
                            float vd = vc.DistanceTo(predPos);
                            float vspd = _targetVehVel.Length();
                            Quaternion targetQ = new Quaternion(_targetVehQx, _targetVehQy, _targetVehQz, _targetVehQw);
                            bool validQ = (Math.Abs(_targetVehQx) + Math.Abs(_targetVehQy) + Math.Abs(_targetVehQz) + Math.Abs(_targetVehQw) > 0.01f);
                            if (vd > 6.0f)
                            {
                                targetVeh.PositionNoOffset = predPos;
                                targetVeh.Velocity = _targetVehVel;
                                if (validQ) targetVeh.Quaternion = targetQ;
                                else targetVeh.Heading = _targetVehHeading;
                            }
                            else if (vspd > 0.4f)
                            {
                                targetVeh.Velocity = _targetVehVel + (predPos - vc) * 8.0f;
                                if (validQ) targetVeh.Quaternion = Quaternion.Slerp(targetVeh.Quaternion, targetQ, 0.45f);
                                else targetVeh.Heading = LerpAngle(targetVeh.Heading, _targetVehHeading, 0.45f);
                            }
                            else
                            {
                                if (vd > 0.20f) targetVeh.PositionNoOffset = Vector3.Lerp(vc, _targetVehPos, 0.30f);
                                targetVeh.Velocity = Vector3.Zero;
                                if (validQ) targetVeh.Quaternion = Quaternion.Slerp(targetVeh.Quaternion, targetQ, 0.30f);
                                else targetVeh.Heading = LerpAngle(targetVeh.Heading, _targetVehHeading, 0.30f);
                            }
                            targetVeh.SteeringAngle = _targetVehSteer;
                            ApplyVehicleFlags(targetVeh, _targetVehFlags);
                        }

                        if (targetVeh != null && targetVeh.Exists())
                        {
                            Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED, targetVeh.Handle, 1);
                            int wantSeat = _targetVehSeat;
                            Ped curOcc = targetVeh.GetPedOnSeat((VehicleSeat)wantSeat);
                            if (curOcc != null && curOcc.Exists() && curOcc != _guestPed)
                            {
                                if (curOcc != me && !_syncedNpcs.ContainsValue(curOcc))
                                {
                                    // Fjern sivil speil-sjåfør/passasjer hvis motspilleren tar setet!
                                    try { curOcc.Delete(); } catch { }
                                }
                                else
                                {
                                    if (targetVeh.IsSeatFree(VehicleSeat.Passenger)) wantSeat = 0;
                                    else if (targetVeh.IsSeatFree(VehicleSeat.LeftRear)) wantSeat = 1;
                                    else if (targetVeh.IsSeatFree(VehicleSeat.RightRear)) wantSeat = 2;
                                    else if (targetVeh.IsSeatFree(VehicleSeat.Driver)) wantSeat = -1;
                                    else wantSeat = 3;
                                }
                            }

                            if (peerEnteringVeh)
                            {
                                // Ekte dør-åpning og innstigning når motspilleren er på vei inn i bilen!
                                bool alreadyEntering = Function.Call<bool>(Hash.IS_PED_GETTING_INTO_A_VEHICLE, _guestPed.Handle);
                                if (!_guestPed.IsInVehicle(targetVeh) && !alreadyEntering
                                    && (!_wasEnteringVeh || Game.GameTime - _lastEnterTaskMs >= 3000))   // v0.9.2: var 1600 ms og avbrøt animasjonen
                                {
                                    _lastEnterTaskMs = Game.GameTime;
                                    if (_guestPed.Position.DistanceTo(targetVeh.Position) > 7.5f)
                                    {
                                        _guestPed.PositionNoOffset = _targetPos;
                                    }
                                    Function.Call(Hash.TASK_ENTER_VEHICLE, _guestPed.Handle, targetVeh.Handle, 4500, wantSeat, 2.0f, 1, 0);
                                }
                            }
                            else
                            {
                                // Motspilleren sitter nå ferdig inne i setet: lås til eksakt riktig sete!
                                // v0.9.2: er figuren fortsatt midt i inn-animasjonen, la den fullføre (inntil 3 s) i stedet for å warpe
                                bool stillEntering = Function.Call<bool>(Hash.IS_PED_GETTING_INTO_A_VEHICLE, _guestPed.Handle)
                                    && Game.GameTime - _lastEnterTaskMs < 3000;
                                if (!stillEntering && (!_guestPed.IsInVehicle(targetVeh) || (int)_guestPed.SeatIndex != wantSeat))
                                {
                                    Function.Call(Hash.TASK_WARP_PED_INTO_VEHICLE, _guestPed.Handle, targetVeh.Handle, wantSeat);
                                }
                                // Drive-by skyting fra bil uten AI (kun ren kule fra bilvinduet mot siktepunktet!)
                                if (_targetShooting && _targetWeapon != 0 && Game.GameTime - _lastShotMs >= 140)
                                {
                                    _lastShotMs = Game.GameTime;
                                    Vector3 dMuz = _guestPed.Position + new Vector3(0f, 0f, 0.45f) + GameplayCamera.Direction * 0.4f;
                                    Function.Call(Hash.SHOOT_SINGLE_BULLET_BETWEEN_COORDS,
                                        dMuz.X, dMuz.Y, dMuz.Z,
                                        _targetAimPos.X, _targetAimPos.Y, _targetAimPos.Z,
                                        22, true, _targetWeapon, _guestPed.Handle, true, false, -1.0f);
                                }
                            }
                        }
                        _wasEnteringVeh = peerEnteringVeh;
                    }
                    else
                    {
                        _wasEnteringVeh = false;
                        bool targetMelee = (_targetPedFlags & 64) != 0;
                        // Naturlig utstigning av bil (åpner døren og går ut, eller kaster seg ut i høy fart!)
                        if (_guestPed.IsInVehicle())
                        {
                            if (_leavingVehUntilMs == 0)
                            {
                                float lvSpd = (_guestPed.CurrentVehicle != null && _guestPed.CurrentVehicle.Exists())
                                    ? _guestPed.CurrentVehicle.Velocity.Length() : 0f;
                                int leaveFlags = lvSpd > 7.5f ? 4160 : 0; // 4160 = rull ut i fart, 0 = åpne dør naturlig
                                Function.Call(Hash.TASK_LEAVE_ANY_VEHICLE, _guestPed.Handle, 0, leaveFlags);
                                _leavingVehUntilMs = Game.GameTime + 1500;
                            }
                            else if (Game.GameTime >= _leavingVehUntilMs)
                            {
                                Function.Call(Hash.TASK_LEAVE_ANY_VEHICLE, _guestPed.Handle, 0, 16);
                                _guestPed.PositionNoOffset = _targetPos;
                                _leavingVehUntilMs = 0;
                            }
                        }
                        else
                        {
                            _leavingVehUntilMs = 0;
                        }

                        if (!_guestPed.IsInVehicle())
                        {
                            // Deaktiver KUN ped-mot-ped kapsel-dytting når begge står helt fredelig tett på hverandre (< 0.85m),
                            // slik at knyttnever, spark og skudd treffer med 100 % ekte fysikk når man slåss eller skyter!
                            bool localCombat = Game.Player.IsAiming || me.IsShooting || me.IsInMeleeCombat
                                || Game.IsControlPressed(Control.Attack)
                                || Game.IsControlPressed(Control.MeleeAttackLight)
                                || Game.IsControlPressed(Control.MeleeAttackHeavy)
                                || Game.IsControlPressed(Control.MeleeAttackAlternate);
                            bool anyCombat = localCombat || _targetAiming || _targetShooting || targetMelee;
                            float pairDist2d = (float)Math.Sqrt(
                                Math.Pow(me.Position.X - _guestPed.Position.X, 2) +
                                Math.Pow(me.Position.Y - _guestPed.Position.Y, 2));
                            if (!me.IsInVehicle() && !anyCombat && _targetSpeed < 0.25f && pairDist2d < 0.90f)
                            {
                                Function.Call(Hash.SET_ENTITY_NO_COLLISION_ENTITY, me.Handle, _guestPed.Handle, false);
                                Function.Call(Hash.SET_ENTITY_NO_COLLISION_ENTITY, _guestPed.Handle, me.Handle, false);
                            }
                        bool targetInCover = (_targetPedFlags & 1) != 0;
                        bool targetHighCover = (_targetPedFlags & 2) != 0;
                        bool targetCoverLeft = (_targetPedFlags & 4) != 0;
                        bool targetAimCover = (_targetPedFlags & 8) != 0;
                        bool targetClimbing = (_targetPedFlags & 16) != 0;
                        bool targetDucking = (_targetPedFlags & 32) != 0;

                        // Klatring / hoppe over hinder (vault/climb)
                        if (targetClimbing && !_wasClimbing)
                        {
                            _guestPed.Heading = _targetHeading;
                            Function.Call(Hash.TASK_CLIMB, _guestPed.Handle, true);
                        }
                        _wasClimbing = targetClimbing;

                        // Hopp og Ragdoll-synk (inkl. ragdoll når motspillerens faktiske HP <= 0 mens de venter på 5s respawn!)
                        if (_targetJumping && !_wasJumping && !targetClimbing)
                        {
                            Function.Call(Hash.TASK_JUMP, _guestPed.Handle, false, false, false);
                        }
                        _wasJumping = _targetJumping;
                        bool wantRagdoll = _targetRagdoll || (_targetPeerHp <= 0);
                        if (wantRagdoll && !_guestPed.IsRagdoll)
                        {
                            _guestPed.CanRagdoll = true;
                            Function.Call(Hash.SET_PED_TO_RAGDOLL, _guestPed.Handle, 1200, 1200, 0, false, false, false);
                        }
                        else if (!wantRagdoll && _guestPed.IsRagdoll)
                        {
                            _guestPed.CanRagdoll = false;
                        }
                        // Stealth (huking) og reload-animasjon (ALDRI kall SET_PED_DUCKING på 60 Hz eller SET_PED_TO_LOAD_COVER!)
                        bool curStl = Function.Call<bool>(Hash.GET_PED_STEALTH_MOVEMENT, _guestPed.Handle);
                        if (curStl != _targetStealth)
                        {
                            Function.Call(Hash.SET_PED_STEALTH_MOVEMENT, _guestPed.Handle, _targetStealth, "DEFAULT_ACTION");
                        }
                        if (_targetReloading && !_wasReloading)
                        {
                            Function.Call(Hash.TASK_RELOAD_WEAPON, _guestPed.Handle, true);
                        }
                        _wasReloading = _targetReloading;

                        // Hvis motspilleren nettopp gikk UT av dekning (cover), avslutt cover-animasjonen én gang
                        if (_wasInCover && !targetInCover)
                        {
                            Function.Call(Hash.CLEAR_PED_TASKS, _guestPed.Handle);
                            _lastCoverMask = -1;
                        }

                        // Sikting/skyting/cover vs vanlig gange: bruk 2D horisontal avstand (dist2d) så ulik bakkehøyde/modellhøyde ALDRI utløser evig gange!
                        Vector3 cur = _guestPed.Position;
                        float dxp = _targetPos.X - cur.X;
                        float dyp = _targetPos.Y - cur.Y;
                        float dist2d = (float)Math.Sqrt(dxp * dxp + dyp * dyp);
                        float dzp = Math.Abs(cur.Z - _targetPos.Z);
                        if (dist2d > 6.0f || dzp > 3.2f)
                        {
                            _guestPed.PositionNoOffset = _targetPos;
                            _guestPed.Heading = _targetHeading;
                            Function.Call(Hash.CLEAR_PED_TASKS, _guestPed.Handle);
                            _wasPeerMoving = false;
                            _lastCoverMask = -1;
                        }
                        else if (targetInCover && !_targetShooting && !_targetAiming && !targetAimCover)
                        {
                            // Ren animasjonsbasert Cover (0 % AI-skyting, 0 % AI-reloading, 0 % jitter!)
                            if (dist2d > 0.25f)
                            {
                                _guestPed.PositionNoOffset = new Vector3(cur.X + dxp * 0.35f, cur.Y + dyp * 0.35f, dzp > 1.3f ? _targetPos.Z : cur.Z);
                            }
                            if (Math.Abs(AngleDiff(_guestPed.Heading, _targetHeading)) > 4.0f)
                            {
                                _guestPed.Heading = LerpAngle(_guestPed.Heading, _targetHeading, 0.30f);
                            }
                            int covMask = (targetHighCover ? 2 : 0) | (targetCoverLeft ? 4 : 0) | (int)(_targetWeapon & 0xFFFF);
                            if (!_wasInCover || _lastCoverMask != covMask)
                            {
                                _lastCoverMask = covMask;
                                PlayCoverAnim(_guestPed, targetHighCover, targetCoverLeft, _targetWeapon);
                            }
                        }
                        else if (_targetShooting)
                        {
                            // Ekte skyte-animasjon (munningsflamme, lyd, rekyl) + presis kule mot siktepunktet
                            if (Game.GameTime - _lastShootTaskMs >= 260)
                            {
                                _lastShootTaskMs = Game.GameTime;
                                Function.Call(Hash.TASK_SHOOT_AT_COORD, _guestPed.Handle,
                                    _targetAimPos.X, _targetAimPos.Y, _targetAimPos.Z, 400, unchecked((int)0xC6EE6B4C));
                            }
                            if (_targetWeapon != 0 && Game.GameTime - _lastShotMs >= 130)
                            {
                                _lastShotMs = Game.GameTime;
                                Vector3 muzzle = _guestPed.Position + new Vector3(0f, 0f, 0.55f) + _guestPed.ForwardVector * 0.6f;
                                Function.Call(Hash.SHOOT_SINGLE_BULLET_BETWEEN_COORDS,
                                    muzzle.X, muzzle.Y, muzzle.Z,
                                    _targetAimPos.X, _targetAimPos.Y, _targetAimPos.Z,
                                    25, true, _targetWeapon, _guestPed.Handle, true, false, -1.0f);
                            }
                        }
                        else if (targetMelee)
                        {
                            // 100 % passiv slag-synk uten AI (ALDRI kall TASK_COMBAT_PED som lar GTA-AI ta over figuren!)
                            if (dist2d > 0.35f)
                            {
                                _guestPed.PositionNoOffset = new Vector3(cur.X + dxp * 0.30f, cur.Y + dyp * 0.30f, dzp > 1.3f ? _targetPos.Z : cur.Z);
                            }
                            _guestPed.Heading = _targetHeading;
                            // v0.9.2: slagene kommer nå som PEER_MELEE-hendelser; flagget brukes bare som reserve hvis ingen hendelse kom
                            if (Game.GameTime - _lastMeleeTaskMs >= 900)
                            {
                                _lastMeleeTaskMs = Game.GameTime;
                                PlayPassiveMeleeAnim(_guestPed, _targetWeapon);
                            }
                        }
                        else if (_targetAiming)
                        {
                            if (dist2d > 0.45f && _targetSpeed >= 0.25f)
                            {
                                if (Game.GameTime - _lastAimTaskMs >= 300)
                                {
                                    _lastAimTaskMs = Game.GameTime;
                                    Function.Call(Hash.TASK_GO_TO_COORD_WHILE_AIMING_AT_COORD, _guestPed.Handle,
                                        _targetPos.X, _targetPos.Y, _targetPos.Z,
                                        _targetAimPos.X, _targetAimPos.Y, _targetAimPos.Z,
                                        1.5f, false, 0.3f, 2.0f, true, 0, false, 0);
                                }
                            }
                            else if (!_wasAiming || Game.GameTime - _lastAimTaskMs >= 3500 || _lastAimSentPos.DistanceTo(_targetAimPos) > 2.5f)
                            {
                                _lastAimTaskMs = Game.GameTime;
                                _lastAimSentPos = _targetAimPos;
                                Function.Call(Hash.TASK_AIM_GUN_AT_COORD, _guestPed.Handle,
                                    _targetAimPos.X, _targetAimPos.Y, _targetAimPos.Z, 5000, false, false);
                            }
                        }
                        else
                        {
                            if (_wasAiming || _wasMelee)
                            {
                                Function.Call(Hash.CLEAR_PED_TASKS, _guestPed.Handle);
                            }
                            bool peerMoving = (_targetSpeed >= 0.25f);
                            if (peerMoving)
                            {
                                _wasPeerMoving = true;
                                float rad = _targetHeading * (float)(Math.PI / 180.0);
                                Vector3 fwd = new Vector3(-(float)Math.Sin(rad), (float)Math.Cos(rad), 0f);
                                // Lead-Goal brukes KUN når motspilleren faktisk går/løper!
                                Vector3 aimTarget = _targetPos + fwd * Math.Max(1.3f, Math.Min(_targetSpeed * 0.55f, 3.0f));
                                float moveBlend = _targetSpeed > 4.2f ? 3.0f : (_targetSpeed > 1.8f ? 2.0f : 1.0f);
                                if (Game.GameTime - _lastMoveTaskMs >= 180)
                                {
                                    _lastMoveTaskMs = Game.GameTime;
                                    Function.Call(Hash.TASK_GO_STRAIGHT_TO_COORD, _guestPed.Handle,
                                        aimTarget.X, aimTarget.Y, aimTarget.Z, moveBlend, 800, _targetHeading, 0.2f);
                                }
                                if (dist2d > 1.3f && dist2d <= 6.0f)
                                {
                                    Vector3 pv = _guestPed.Velocity;
                                    _guestPed.Velocity = new Vector3(
                                        fwd.X * _targetSpeed + dxp * 2.2f,
                                        fwd.Y * _targetSpeed + dyp * 2.2f,
                                        pv.Z);
                                }
                            }
                            else
                            {
                                // Motspilleren står helt i ro (_targetSpeed < 0.25f): ALDRI kall TASK_GO_STRAIGHT_TO_COORD!
                                if (_wasPeerMoving)
                                {
                                    _wasPeerMoving = false;
                                    Function.Call(Hash.CLEAR_PED_TASKS, _guestPed.Handle);
                                }
                                if (dist2d > 0.28f)
                                {
                                    float nx = cur.X + dxp * 0.25f;
                                    float ny = cur.Y + dyp * 0.25f;
                                    float nz = (dzp > 1.3f) ? _targetPos.Z : cur.Z;
                                    _guestPed.PositionNoOffset = new Vector3(nx, ny, nz);
                                }
                                if (Math.Abs(AngleDiff(_guestPed.Heading, _targetHeading)) > 2.5f)
                                {
                                    _guestPed.Heading = LerpAngle(_guestPed.Heading, _targetHeading, 0.25f);
                                }
                            }
                        }
                        _wasAiming = (_targetShooting || _targetAiming);
                        _wasMelee = targetMelee;
                        if ((_targetShooting || _targetAiming || targetMelee) && _wasInCover)
                        {
                            _lastCoverMask = -1;
                        }
                        _wasInCover = targetInCover;
                        }
                    }
                }
            }
        }

        // Les egne klær hvert 1. sekund (lett)
        if (Game.GameTime - _lastOutfitSendMs >= 1000)
        {
            _lastOutfitSendMs = Game.GameTime;
            _cachedMyOutfit = ReadOutfit(me);
        }

        // Skanne nylig drepte NPC-er innen 45 m hvert 600 ms og synk dødsfall til motspilleren
        if (Game.GameTime - _lastNpcScanMs >= 600)
        {
            _lastNpcScanMs = Game.GameTime;
            if (_reportedDead.Count > 200) _reportedDead.Clear();
            Ped[] near = World.GetNearbyPeds(me, 45.0f);
            if ( near != null )
            {
                foreach (Ped np in near)
                {
                    if (np != null && np.Exists() && np != me && np != _guestPed && np.IsDead && !_reportedDead.Contains(np.Handle))
                    {
                        _reportedDead.Add(np.Handle);
                        Vector3 dp = np.Position;
                        SendRaw(string.Format(CultureInfo.InvariantCulture, "NPC_DEAD|{0}|{1:F2}|{2:F2}|{3:F2}",
                            np.Model.Hash, dp.X, dp.Y, dp.Z));
                        if (_isGuestRole && IsPolicePed(np) && _hostAuthoritativeWanted < 3)
                        {
                            _hostAuthoritativeWanted = 3;
                            _lastGuestWantedReqMs = Game.GameTime;
                            SendRaw(string.Format(CultureInfo.InvariantCulture, "WANTED_REQ|3|{0:F1}|{1:F1}|{2:F1}",
                                dp.X, dp.Y, dp.Z));
                        }
                    }
                }
            }
        }

        // Send penger + våpen-inventar hvert 2. sekund (brukes av Vert -> Gjest)
        if (Game.GameTime - _lastInvSendMs >= 2000)
        {
            _lastInvSendMs = Game.GameTime;
            StringBuilder inv = new StringBuilder(256);
            foreach (WeaponHash wh in Enum.GetValues(typeof(WeaponHash)))
            {
                if (wh == WeaponHash.Unarmed) continue;
                uint h = (uint)wh;
                if (Function.Call<bool>(Hash.HAS_PED_GOT_WEAPON, me.Handle, h, false))
                {
                    int ammo = Function.Call<int>(Hash.GET_AMMO_IN_PED_WEAPON, me.Handle, h);
                    if (inv.Length > 0) inv.Append(',');
                    inv.Append(h).Append(':').Append(ammo);
                }
            }
            SendRaw("INV|" + Game.Player.Money + "|" + inv.ToString());
        }

        // 1:1 Cutscene-synk mellom Vert og Gjest (med 0 % doble karakterer!)
        bool csPlaying = Function.Call<bool>(Hash.IS_CUTSCENE_ACTIVE) || Function.Call<bool>(Hash.IS_CUTSCENE_PLAYING);
        if (_isGuestRole)
        {
            if (_guestCsState != 0)
            {
                UpdateGuestCutsceneTick(me);
            }
            else if (csPlaying)
            {
                // Avbryt kun uønskede lokale singleplayer-cutscenes som IKKE ble startet av Verten!
                Function.Call(Hash.STOP_CUTSCENE_IMMEDIATELY);
            }
            if (_hostCam != null)
            {
                CleanupHostCam();
            }
        }
        else
        {
            if (csPlaying)
            {
                _hostWasInCutscene = true;
                if (_guestPed != null && _guestPed.Exists() && _guestPed.IsVisible)   // v0.9.2: gjestens figur ikke med i vertens film
                {
                    _guestPed.IsVisible = false;
                    _hostCsHidGuest = true;
                }
                if (string.IsNullOrEmpty(_activeCutscene) && !_hostCsUnknownSent && _hostCsUnknownSinceMs != 0
                    && Game.GameTime - _hostCsUnknownSinceMs > 1200)
                {
                    // v0.9.2: navnet ble ikke funnet (HAS_THIS_CUTSCENE_LOADED svarer trolig bare for egne forespørsler).
                    // Logg det spillet kan fortelle, og be gjesten skjule speilfigurene i stedet for å la dem flyte rundt.
                    _hostCsUnknownSent = true;
                    Vector3 uc = Function.Call<Vector3>(Hash.GET_FINAL_RENDERED_CAM_COORD);
                    if (uc.Length() < 1.0f) uc = me.Position;
                    SendRaw(string.Format(CultureInfo.InvariantCulture,
                        "EVENT|cs_unknown|dur={0}|time={1}|active={2}|playing={3}|loaded={4}|cam={5:F1},{6:F1},{7:F1}",
                        Function.Call<int>(Hash.GET_CUTSCENE_TOTAL_DURATION), Function.Call<int>(Hash.GET_CUTSCENE_TIME),
                        Function.Call<bool>(Hash.IS_CUTSCENE_ACTIVE) ? 1 : 0, Function.Call<bool>(Hash.IS_CUTSCENE_PLAYING) ? 1 : 0,
                        Function.Call<bool>(Hash.HAS_CUTSCENE_LOADED) ? 1 : 0, uc.X, uc.Y, uc.Z));
                    SendRaw(string.Format(CultureInfo.InvariantCulture, "CUTSCENE|start|?,{0:F1},{1:F1},{2:F1}", uc.X, uc.Y, uc.Z));
                    _lastHostCsSyncMs = Game.GameTime;
                }
                if (string.IsNullOrEmpty(_activeCutscene) && _hostCsUnknownSinceMs == 0) _hostCsUnknownSinceMs = Game.GameTime;
                if (string.IsNullOrEmpty(_activeCutscene))
                {
                    for (int i = 0; i < _allCutscenes.Length; i++)
                    {
                        string cn = _allCutscenes[i];
                        if (!string.IsNullOrEmpty(cn) && Function.Call<bool>(Hash.HAS_THIS_CUTSCENE_LOADED, cn))
                        {
                            _activeCutscene = cn;
                            Vector3 cc = Function.Call<Vector3>(Hash.GET_FINAL_RENDERED_CAM_COORD);
                            if (cc.Length() < 1.0f) cc = me.Position;
                            string startData = string.Format(CultureInfo.InvariantCulture, "{0},{1:F1},{2:F1},{3:F1}",
                                _activeCutscene, cc.X, cc.Y, cc.Z);
                            _lastHostCsSyncMs = Game.GameTime;
                            SendRaw("CUTSCENE|start|" + startData);
                            break;
                        }
                    }
                }
                else if (Game.GameTime - _lastHostCsSyncMs >= 1500)
                {
                    _lastHostCsSyncMs = Game.GameTime;
                    Vector3 cc = Function.Call<Vector3>(Hash.GET_FINAL_RENDERED_CAM_COORD);
                    if (cc.Length() < 1.0f) cc = me.Position;
                    string syncData = string.Format(CultureInfo.InvariantCulture, "{0},{1:F1},{2:F1},{3:F1}",
                        _activeCutscene, cc.X, cc.Y, cc.Z);
                    SendRaw("CUTSCENE|sync|" + syncData);
                }
            }
            else if (_hostWasInCutscene || !string.IsNullOrEmpty(_activeCutscene))
            {
                if (_hostCsHidGuest && _guestPed != null && _guestPed.Exists())
                {
                    _guestPed.IsVisible = true;
                }
                _hostCsHidGuest = false;
                string endData = string.Format(CultureInfo.InvariantCulture, "{0:F1},{1:F1},{2:F1},{3:F1}",
                    me.Position.X, me.Position.Y, me.Position.Z, me.Heading);
                SendRaw("CUTSCENE|stop|" + endData);
                _activeCutscene = "";
                _hostWasInCutscene = false;
                _hostCsUnknownSinceMs = 0;
                _hostCsUnknownSent = false;
            }
        }

        // Gjest: kollisjonssikker auto-overgang ved store kart-hopp + NPC/bil-synk (pauses under 1:1 cutscene så 0 duplikater spawner!)
        if (_isGuestRole)
        {
            if (_guestCsState == 0)
            {
                if (_hasTarget && Game.GameTime - _lastAutoRegionTpMs > 8000)
                {
                    if (me.Position.DistanceTo(_targetPos) > 600.0f && _targetPos.Length() > 10.0f)
                    {
                        _lastAutoRegionTpMs = Game.GameTime;
                        SafeTeleportLocalPlayer(_targetPos + new Vector3(10.0f, 0f, 1.0f), _targetHeading);
                        SendRaw("EVENT|auto_tp_to_host_region");
                    }
                }
                UpdateSyncedNpcsTick();
                UpdateSyncedVehsTick();
            }
        }
        else
        {
            // Under cutscene på Verten skanner vi rundt cutscene-kameraet så alle skuespillere og biler i cutscenen synkes til Gjesten!
            Vector3 scanCenter = csPlaying ? Function.Call<Vector3>(Hash.GET_FINAL_RENDERED_CAM_COORD) : me.Position;

            // Vert: MTU-sikker chunket verdenssynk (< 1050 bytes per UDP-pakke = 0 % IP-fragmentering og 0 % pakketap!):
            // 1) De 10 nærmeste bilene sendes HVER gang (14 Hz), pluss en rullende batch på 10 fjerne biler (#10..85) ut til 500 m!
            // 2) Trafikk-sjåfører og passasjerer er innebygd i selve bilraden (drvMdl,passMdl) -> 0 % kloner oppå biltak!
            if (Game.GameTime - _lastVehSyncMs >= 70)
            {
                _lastVehSyncMs = Game.GameTime;
                bool hasPeerTarget = (_hasTarget && Game.GameTime - _lastRxMs < 8000);
                Vehicle[] nearV = World.GetAllVehicles();
                List<Vehicle> vcands = new List<Vehicle>(nearV.Length);
                foreach (Vehicle v in nearV)
                {
                    if (v != null && v.Exists() && (v != _guestVeh || me.IsInVehicle(v)))
                    {
                        float dMin = v.Position.DistanceTo(scanCenter);
                        if (hasPeerTarget) dMin = Math.Min(dMin, v.Position.DistanceTo(_targetPos));
                        if (dMin <= 500.0f) vcands.Add(v);
                    }
                }
                // v0.9.2: bilen jeg sitter i / er på vei inn i, og den andres bil, skal alltid med i nærpakken (14 Hz)
                Vehicle myVehNow = null;
                if (me.IsInVehicle()) myVehNow = me.CurrentVehicle;
                else if (Function.Call<bool>(Hash.IS_PED_GETTING_INTO_A_VEHICLE, me.Handle))
                {
                    int tvh = Function.Call<int>(Hash.GET_VEHICLE_PED_IS_TRYING_TO_ENTER, me.Handle);
                    if (tvh != 0) { try { myVehNow = Entity.FromHandle(tvh) as Vehicle; } catch { myVehNow = null; } }
                }
                vcands.Sort((a, b) =>
                {
                    if (a == b) return 0;
                    if (myVehNow != null && a == myVehNow) return -1;
                    if (myVehNow != null && b == myVehNow) return 1;
                    float da = a.Position.DistanceTo(scanCenter);
                    float db = b.Position.DistanceTo(scanCenter);
                    if (hasPeerTarget)
                    {
                        da = Math.Min(da, a.Position.DistanceTo(_targetPos));
                        db = Math.Min(db, b.Position.DistanceTo(_targetPos));
                    }
                    float sa = da - (a.Velocity.Length() > 0.8f ? 90.0f : 0f) - (IsEmergencyOrHeli(a) ? 200.0f : 0f);
                    float sb2 = db - (b.Velocity.Length() > 0.8f ? 90.0f : 0f) - (IsEmergencyOrHeli(b) ? 200.0f : 0f);
                    return sa.CompareTo(sb2);
                });
                int vTotal = Math.Min(vcands.Count, 85);
                if (vTotal > 0)
                {
                    // Pakke 1: De 10 nærmeste / viktigste bilene inkl. politi & helikopter (< 1000 bytes, null fragmentering!)
                    SendVehicleBatch(vcands, 0, Math.Min(vTotal, 10), me);
                    // Pakke 2: Rullerende batch på 10 fjerne horisont-biler (#10..vTotal)
                    if (vTotal > 10)
                    {
                        if (_vehFarCursor >= vTotal || _vehFarCursor < 10) _vehFarCursor = 10;
                        int endIdx = Math.Min(_vehFarCursor + 10, vTotal);
                        SendVehicleBatch(vcands, _vehFarCursor, endIdx, me);
                        _vehFarCursor = (endIdx >= vTotal) ? 10 : endIdx;
                    }
                }

                // NPC-synk: til-fots NPC-er + politi + ekte oppdrags-NPC-er rundt både Vert og Gjest!
                Ped[] near = World.GetAllPeds();
                List<Ped> cands = new List<Ped>(near.Length);
                foreach (Ped np in near)
                {
                    if (np == null || !np.Exists() || np == me || np == _guestPed || np.IsPlayer) continue;
                    float pdMin = np.Position.DistanceTo(scanCenter);
                    if (hasPeerTarget) pdMin = Math.Min(pdMin, np.Position.DistanceTo(_targetPos));
                    if (pdMin > 400.0f) continue;
                    if (np.IsInVehicle() && !Function.Call<bool>(Hash.IS_ENTITY_A_MISSION_ENTITY, np.Handle))
                    {
                        continue; // Trafikk-/politisjåfører og passasjerer er allerede bakt inn i VEHS!
                    }
                    cands.Add(np);
                }
                cands.Sort((a, b) =>
                {
                    float pa = a.Position.DistanceTo(scanCenter);
                    float pb = b.Position.DistanceTo(scanCenter);
                    if (hasPeerTarget)
                    {
                        pa = Math.Min(pa, a.Position.DistanceTo(_targetPos));
                        pb = Math.Min(pb, b.Position.DistanceTo(_targetPos));
                    }
                    pa -= IsPolicePed(a) ? 180.0f : 0f;
                    pb -= IsPolicePed(b) ? 180.0f : 0f;
                    return pa.CompareTo(pb);
                });
                int pTotal = Math.Min(cands.Count, 55);
                if (pTotal > 0)
                {
                    // Pakke 1: De 10 nærmeste NPC-ene (< 900 bytes, null fragmentering!)
                    SendNpcBatch(cands, 0, Math.Min(pTotal, 10));
                    // Pakke 2: Rullerende batch på 10 fjerne fotgjengere (#10..pTotal)
                    if (pTotal > 10)
                    {
                        if (_npcFarCursor >= pTotal || _npcFarCursor < 10) _npcFarCursor = 10;
                        int pEnd = Math.Min(_npcFarCursor + 10, pTotal);
                        SendNpcBatch(cands, _npcFarCursor, pEnd);
                        _npcFarCursor = (pEnd >= pTotal) ? 10 : pEnd;
                    }
                    if (Game.GameTime - _lastNpcStyleSendMs >= 2000)
                    {
                        _lastNpcStyleSendMs = Game.GameTime;
                        int slim = Math.Min(pTotal, 10);
                        StringBuilder ssb = new StringBuilder(1024);
                        ssb.Append("NPCS|STYLE:");
                        for (int i = 0; i < slim; i++)
                        {
                            Ped np = cands[i];
                            if (i > 0) ssb.Append(';');
                            ssb.Append(np.Handle).Append('=').Append(ReadOutfit(np));
                        }
                        SendRaw(ssb.ToString());
                    }
                }
            }

            // Vert: synk dører (DOOR_SYSTEM) + alle synlige dør-/port-props (>=1.20m) innen 25 m hvert 1,2 sek
            if (Game.GameTime - _lastPropSyncMs >= 1200)
            {
                _lastPropSyncMs = Game.GameTime;
                Prop[] props = World.GetNearbyProps(me.Position, 25.0f);
                StringBuilder psb = new StringBuilder(1536);
                int itemCount = 0;
                int barCount = 0;
                for (int i = 0; i < props.Length; i++)
                {
                    Prop pr = props[i];
                    if (pr == null || !pr.Exists()) continue;
                    int dState;
                    float ratio;
                    if (itemCount < 20 && TryGetDoorInfo(pr, out dState, out ratio))
                    {
                        Vector3 pp = pr.Position;
                        if (psb.Length > 0) psb.Append(';');
                        psb.Append(pr.Model.Hash).Append(',')
                           .Append(pp.X.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                           .Append(pp.Y.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                           .Append(pp.Z.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                           .Append(dState).Append(',')
                           .Append(ratio.ToString("F2", CultureInfo.InvariantCulture));
                        itemCount++;
                    }
                    if (barCount < 100 && pr.IsVisible && IsBarrierOrDoorProp(pr))
                    {
                        Vector3 bp = pr.Position;
                        Vector3 br = pr.Rotation;
                        if (psb.Length > 0) psb.Append(';');
                        psb.Append("B:").Append(pr.Model.Hash).Append(',')
                           .Append(bp.X.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                           .Append(bp.Y.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                           .Append(bp.Z.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                           .Append(br.X.ToString("F1", CultureInfo.InvariantCulture)).Append(',')
                           .Append(br.Y.ToString("F1", CultureInfo.InvariantCulture)).Append(',')
                           .Append(br.Z.ToString("F1", CultureInfo.InvariantCulture));
                        barCount++;
                    }
                }
                // Sjekk også Prologue bankhvelv Rayfire ("des_vaultdoor001") hvis vi er i nærheten
                int rfVault = Function.Call<int>(Hash.GET_RAYFIRE_MAP_OBJECT, me.Position.X, me.Position.Y, me.Position.Z, 25.0f, "des_vaultdoor001");
                if (rfVault != 0 && Function.Call<bool>(Hash.DOES_RAYFIRE_MAP_OBJECT_EXIST, rfVault))
                {
                    int rfs = Function.Call<int>(Hash.GET_STATE_OF_RAYFIRE_MAP_OBJECT, rfVault);
                    if (psb.Length > 0) psb.Append(';');
                    psb.AppendFormat(CultureInfo.InvariantCulture, "RF:des_vaultdoor001,{0:F1},{1:F1},{2:F1},{3}",
                        me.Position.X, me.Position.Y, me.Position.Z, rfs);
                }
                int iplMask = 0;
                for (int k = 0; k < SYNCED_IPLS.Length; k++)
                {
                    if (Function.Call<bool>(Hash.IS_IPL_ACTIVE, SYNCED_IPLS[k])) iplMask |= (1 << k);
                }
                if (psb.Length > 0) psb.Append(';');
                psb.Append("IPL:").Append(iplMask);
                if (psb.Length > 0)
                {
                    SendRaw("PROPS|" + psb.ToString());
                }
            }
        }

        try { DamageSyncTick(me); } catch { }   // v0.9.2: skadesynk for biler

        // Send verdenstid, vær og wanted level straks ved stjerne-endring eller hvert 1.5 sekund (Vert -> Gjest)
        int curWanted = Game.Player.WantedLevel;
        if (!_isGuestRole && (curWanted != _lastSentWanted || Game.GameTime - _lastWorldSendMs >= 1500))
        {
            _lastWorldSendMs = Game.GameTime;
            _lastSentWanted = curWanted;
            int hr = Function.Call<int>(Hash.GET_CLOCK_HOURS);
            int mn = Function.Call<int>(Hash.GET_CLOCK_MINUTES);
            int sc = Function.Call<int>(Hash.GET_CLOCK_SECONDS);
            int wt = (int)World.Weather;
            // v0.9.2: hele værtilstanden (GET_CURR_WEATHER_STATE): forrige vær, neste vær og overgangsprosent
            uint wPrev = 0, wNext = 0; float wPct = 0f;
            try
            {
                OutputArgument oPrev = new OutputArgument(), oNext = new OutputArgument(), oPct = new OutputArgument();
                Function.Call((Hash)0xF3BBE884A14BB413UL, oPrev, oNext, oPct);
                wPrev = oPrev.GetResult<uint>(); wNext = oNext.GetResult<uint>(); wPct = oPct.GetResult<float>();
            }
            catch { }
            int hostInt = Function.Call<int>(Hash.GET_INTERIOR_FROM_ENTITY, me.Handle);   // v0.9.2
            SendRaw("WORLD|" + hr + "|" + mn + "|" + wt + "|" + curWanted + "|" + wPrev + "|" + wNext + "|"
                + wPct.ToString("F3", CultureInfo.InvariantCulture) + "|" + sc + "|" + hostInt);
        }

        // Send egen tilstand til GUI: 40 Hz (hvert 25 ms) i kjøretøy for null etterslep ved samkjøring, ~22 Hz til fots!
        int stateIntervalMs = me.IsInVehicle() ? 25 : 45;
        if (Game.GameTime - _lastSendMs >= stateIntervalMs)
        {
            _lastSendMs = Game.GameTime;
            Vector3 pos = me.Position;
            bool localMoving = me.IsWalking || me.IsRunning || me.IsSprinting || me.IsJumping || me.IsFalling || me.IsClimbing;
            float spd = localMoving ? me.Velocity.Length() : 0.0f;
            uint wpHash = 0;
            try
            {
                wpHash = (uint)me.Weapons.Current.Hash;
            }
            catch { }
            int inCut = Function.Call<bool>(Hash.IS_CUTSCENE_ACTIVE) ? 1 : 0;
            int isAim = (Game.Player.IsAiming || Function.Call<bool>(Hash.IS_PLAYER_FREE_AIMING, Game.Player.Handle)) ? 1 : 0;
            bool hasGun = (wpHash != 0 && wpHash != (uint)WeaponHash.Unarmed);
            int isShoot = (me.IsShooting || Function.Call<bool>(Hash.IS_PED_SHOOTING, me.Handle) || (hasGun && Game.IsControlPressed(Control.Attack))) ? 1 : 0;
            Vector3 aimPos = GameplayCamera.Position + GameplayCamera.Direction * 45.0f;

            int vm = 0, vs = -1, vc1 = 0, vc2 = 0, siren = 0, vhid = 0;
            bool enteringVehNow = false;
            Vector3 vp = Vector3.Zero, vvel = Vector3.Zero;
            float vh = 0f, qx = 0f, qy = 0f, qz = 0f, qw = 1f, steer = 0f;
            Vehicle cv = null;
            if (me.IsInVehicle() && me.CurrentVehicle != null && me.CurrentVehicle.Exists())
            {
                cv = me.CurrentVehicle;
                vs = (int)me.SeatIndex;
            }
            else if (Function.Call<bool>(Hash.IS_PED_GETTING_INTO_A_VEHICLE, me.Handle))
            {
                int tryVh = Function.Call<int>(Hash.GET_VEHICLE_PED_IS_TRYING_TO_ENTER, me.Handle);
                if (tryVh != 0)
                {
                    try { cv = Entity.FromHandle(tryVh) as Vehicle; } catch { cv = null; }
                    if (cv != null && cv.Exists())
                    {
                        enteringVehNow = true;
                        vs = Function.Call<int>(Hash.GET_SEAT_PED_IS_TRYING_TO_ENTER, me.Handle);
                    }
                }
            }
            if (cv != null && cv.Exists())
            {
                vm = cv.Model.Hash;
                vp = cv.Position;
                vh = cv.Heading;
                vvel = cv.Velocity;
                steer = cv.SteeringAngle;
                siren = ReadVehicleFlags(cv, !enteringVehNow && vs == -1);
                if (!_isGuestRole)
                {
                    vhid = cv.Handle;
                }
                else
                {
                    foreach (KeyValuePair<int, Vehicle> kv in _syncedVehs)
                    {
                        if (kv.Value == cv) { vhid = kv.Key; break; }
                    }
                }
                OutputArgument oa1 = new OutputArgument();
                OutputArgument oa2 = new OutputArgument();
                Function.Call(Hash.GET_VEHICLE_COLOURS, cv.Handle, oa1, oa2);
                vc1 = oa1.GetResult<int>();
                vc2 = oa2.GetResult<int>();
                OutputArgument oqx = new OutputArgument(), oqy = new OutputArgument(), oqz = new OutputArgument(), oqw = new OutputArgument();
                Function.Call(Hash.GET_ENTITY_QUATERNION, cv.Handle, oqx, oqy, oqz, oqw);
                qx = oqx.GetResult<float>(); qy = oqy.GetResult<float>();
                qz = oqz.GetResult<float>(); qw = oqw.GetResult<float>();
            }
            int jmp = me.IsJumping ? 1 : 0;
            int rag = me.IsRagdoll ? 1 : 0;
            int stl = Function.Call<bool>(Hash.GET_PED_STEALTH_MOVEMENT, me.Handle) ? 1 : 0;
            int rld = me.IsReloading ? 1 : 0;
            int pflags = 0;
            if (Function.Call<bool>(Hash.IS_PED_IN_COVER, me.Handle, false)
                || Function.Call<bool>(Hash.IS_PED_GOING_INTO_COVER, me.Handle)) pflags |= 1;
            if (Function.Call<bool>(Hash.IS_PED_IN_HIGH_COVER, me.Handle)) pflags |= 2;
            if (Function.Call<bool>(Hash.IS_PED_IN_COVER_FACING_LEFT, me.Handle)) pflags |= 4;
            if (Function.Call<bool>(Hash.IS_PED_AIMING_FROM_COVER, me.Handle)) pflags |= 8;
            if (Function.Call<bool>(Hash.IS_PED_CLIMBING, me.Handle)
                || Function.Call<bool>(Hash.IS_PED_VAULTING, me.Handle)) pflags |= 16;
            if (Function.Call<bool>(Hash.IS_PED_DUCKING, me.Handle)) pflags |= 32;
            // ALDRI les IS_PED_IN_MELEE_COMBAT her (den blir stående true passivt etter å ha blitt slått -> ga evig AI-slåssing!).
            // Kun ekte knappetrykk fra menneskelig spiller sender slag-flagg!
            bool isMeleeFirearm = (wpHash != 0 && wpHash != (uint)WeaponHash.Unarmed
                && wpHash != (uint)WeaponHash.Knife && wpHash != (uint)WeaponHash.Bat
                && wpHash != (uint)WeaponHash.Crowbar && wpHash != (uint)WeaponHash.Nightstick
                && wpHash != (uint)WeaponHash.Hammer && wpHash != (uint)WeaponHash.GolfClub
                && wpHash != (uint)WeaponHash.Dagger && wpHash != (uint)WeaponHash.Hatchet
                && wpHash != (uint)WeaponHash.Machete && wpHash != (uint)WeaponHash.SwitchBlade);
            bool isMelee = (!isMeleeFirearm && (Game.IsControlPressed(Control.Attack)
                    || Game.IsControlPressed(Control.MeleeAttackLight)
                    || Game.IsControlPressed(Control.MeleeAttackHeavy)
                    || Game.IsControlPressed(Control.MeleeAttackAlternate)))
                || (isMeleeFirearm && (Game.IsControlPressed(Control.MeleeAttackLight) || Game.IsControlPressed(Control.MeleeAttackHeavy)));
            if (isMelee) pflags |= 64;
            if (enteringVehNow) pflags |= 128;

            string msg = string.Format(
                CultureInfo.InvariantCulture,
                "STATE|{0:F3}|{1:F3}|{2:F3}|{3:F2}|{4}|{5}|{6:F2}|{7}|{8}|{9}|{10}|{11:F2}|{12:F2}|{13:F2}|{14}|{15:F3}|{16:F3}|{17:F3}|{18:F2}|{19}|{20}|{21}|{22:F4}|{23:F4}|{24:F4}|{25:F4}|{26:F2}|{27:F2}|{28:F2}|{29:F2}|{30}|{31}|{32}|{33}|{34}|{35}|{36}|{37}",
                pos.X, pos.Y, pos.Z, me.Heading, inCut, me.Health, spd, wpHash, _cachedMyOutfit,
                isAim, isShoot, aimPos.X, aimPos.Y, aimPos.Z,
                vm, vp.X, vp.Y, vp.Z, vh, vs, vc1, vc2,
                qx, qy, qz, qw, vvel.X, vvel.Y, vvel.Z, steer, siren, jmp, rag, stl, rld, pflags, me.Model.Hash, vhid
            );
            SendRaw(msg);
        }
    }
}

// v0.9.2: Lett skript som kjører HVERT bilde (Interval = 0). Hovedskriptet tikker hvert 16 ms; ved høyere bildefrekvens satte spillet
// selv bremselysene i bildene imellom (speilbilens sjåfør uten oppgave «bremser») -> baklysene flimret. Her holdes ønsket tilstand.
public class CoopFrameOverrides : Script
{
    private static readonly Dictionary<int, bool> _brake = new Dictionary<int, bool>();
    private static readonly Dictionary<int, int> _seen = new Dictionary<int, int>();
    private static readonly object _lk = new object();
    private static CoopFrameOverrides _inst;

    public static void SetBrake(int vehHandle, bool on)
    {
        lock (_lk)
        {
            _brake[vehHandle] = on;
            _seen[vehHandle] = Environment.TickCount;
        }
        if (_inst != null && _inst.Interval != 0) _inst.Interval = 0;   // hvert bilde bare når det finnes en bil å passe på
    }

    public CoopFrameOverrides()
    {
        Interval = 250;   // v0.9.2: rolig til en bil trenger bremselys-overstyring
        _inst = this;
        Tick += OnFrame;
    }

    private void OnFrame(object sender, EventArgs e)
    {
        if (CoopBridge.Idle) return;
        List<int> drop = null;
        lock (_lk)
        {
            int now = Environment.TickCount;
            foreach (KeyValuePair<int, bool> kv in _brake)
            {
                int seen;
                if (!_seen.TryGetValue(kv.Key, out seen) || now - seen > 1000
                    || !Function.Call<bool>(Hash.DOES_ENTITY_EXIST, kv.Key))
                {
                    if (drop == null) drop = new List<int>();
                    drop.Add(kv.Key);
                    continue;
                }
                Function.Call(Hash.SET_VEHICLE_BRAKE_LIGHTS, kv.Key, kv.Value);
            }
            if (drop != null)
            {
                foreach (int h in drop) { _brake.Remove(h); _seen.Remove(h); }
            }
            if (_brake.Count == 0 && Interval == 0) Interval = 250;
        }
    }
}
