// Escape-From-Duckov-Coop-Mod-Preview
// Copyright (C) 2025 Mr.sans and InitLoader's team
//
// This program is not a free software.
// It's distributed under a license based on AGPL-3.0,
// with strict additional restrictions:
// YOU MUST NOT use this software for commercial purposes.
// YOU MUST NOT use this software to run a headless game server.
// YOU MUST include a conspicuous notice of attribution to
// Mr-sans-and-InitLoader-s-team/Escape-From-Duckov-Coop-Mod-Preview as the original author.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU Affero General Public License for more details.

using Cysharp.Threading.Tasks;
using Duckov;
using Duckov.Scenes;
using Duckov.UI;
using Duckov.Utilities;
using Duckov.Weathers;
using Eflatun.SceneReference;
using HarmonyLib;
using ItemStatsSystem;
using ItemStatsSystem.Items;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using static Unity.Burst.Intrinsics.X86.Avx;
using Object = UnityEngine.Object;

/*
 Mod.cs - Core multiplayer implementation for Escape From Duckov (Co-op)

 Overview
 - Network bootstrap and lifecycle (server/client start/stop, discovery, connect, disconnect)
 - Protocol definitions (Op enum) and packet serialization helpers (NetPack/NetDataExtensions)
 - Player state sync (position, rotation, health, equipment, inventory/loot interactions)
 - Scene vote and gate: host coordinates map transitions; clients vote/ready; host releases gate
 - Environment sync (time, weather), door state sync, AI seed sync and spawning
 - FX helpers for local hit/kill feedback; spectate flow when dead

 Key concepts
 - Host authority: the host validates requests and broadcasts authoritative state
 - Client requests: clients send compact requests; server broadcasts minimal snapshots/events
 - Late-joiners: host maintains snapshots to quickly synchronize new clients (loot, doors, AI)
 - Bandwidth: binary serialization via LiteNetLib; careful with large lists (chunk/batch when needed)

 Notes for maintainers
 - Keep user-visible strings in English; avoid non-ASCII glyphs in UI/log messages
 - When adding new ops, define a compact serialization layout in NetPack and mirror in handlers
 - Prefer try/catch around reflection/decompiled fields to avoid runtime crashes across game updates
 - Any UI changes should remain optional/safe even when components are missing in certain scenes
*/
namespace DuckovCoopMod
{
    

    public static class LocalHitKillFx
    {
        static System.Reflection.FieldInfo _fiHurtVisual;              // CharacterModel.hurtVisual (private global::HurtVisual)
        static System.Reflection.MethodInfo _miHvOnHurt, _miHvOnDead;  // HurtVisual.OnHurt / OnDead (private)
        static System.Reflection.MethodInfo _miHmOnHit, _miHmOnKill;   // HitMarker.OnHit / OnKill (private)

        static void EnsureHurtVisualBindings(object characterModel, object hv)
        {
            if (_fiHurtVisual == null && characterModel != null)
                _fiHurtVisual = characterModel.GetType()
                    .GetField("hurtVisual", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            if (hv != null)
            {
                var t = hv.GetType();
                if (_miHvOnHurt == null)
                    _miHvOnHurt = t.GetMethod("OnHurt", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (_miHvOnDead == null)
                    _miHvOnDead = t.GetMethod("OnDead", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            }
        }

        static float _lastBaseDamageForPop = 0f;
        public static void RememberLastBaseDamage(float v)
        {
            if (v > 0.01f) _lastBaseDamageForPop = v;
        }

        static object FindHurtVisualOn(global::CharacterMainControl cmc)
        {
            if (!cmc) return null;
            var model = cmc.characterModel; // CharacterModel
            if (model == null) return null;

            object hv = null;
            try
            {
                EnsureHurtVisualBindings(model, null);
                if (_fiHurtVisual != null)
                    hv = _fiHurtVisual.GetValue(model);
            }
            catch { }

            // 
            if (hv == null)
            {
                try { hv = model.GetComponentInChildren(typeof(global::HurtVisual), true); } catch { }
            }
            return hv;
        }

        static object FindHitMarkerSingleton()
        {
            try { return UnityEngine.Object.FindObjectOfType(typeof(global::HitMarker), true); }
            catch { return null; }
        }

        static void PlayHurtVisual(object hv, global::DamageInfo di, bool predictedDead)
        {
            if (hv == null) return;
            EnsureHurtVisualBindings(null, hv);

            try { _miHvOnHurt?.Invoke(hv, new object[] { di }); } catch { }
            if (predictedDead)
            {
                try { _miHvOnDead?.Invoke(hv, new object[] { di }); } catch { }
            }
        }
        public static void PopDamageText(Vector3 hintPos, global::DamageInfo di)
        {
            try
            {
                if (global::FX.PopText.instance)
                {
                    var look = GameplayDataSettings.UIStyle.GetElementDamagePopTextLook(global::ElementTypes.physics);
                    float size = (di.crit > 0) ? look.critSize : look.normalSize;
                    var sprite = (di.crit > 0) ? GameplayDataSettings.UIStyle.CritPopSprite : null;
                    Debug.Log(di.damageValue +" "+di.finalDamage);
                    float _display = di.damageValue;
                    // DamageInfo.damageValue 1 1.0 
                    if (_display <= 1.001f && _lastBaseDamageForPop > 0f)
                    {
                        float critMul = (di.crit > 0 && di.critDamageFactor > 0f) ? di.critDamageFactor : 1f;
                        _display = Mathf.Max(_display, _lastBaseDamageForPop * critMul);
                    }
                    string text = (_display > 0f) ? _display.ToString("F1") : "HIT";
                    global::FX.PopText.Pop(text, hintPos, look.color, size, sprite);
                }
            }
            catch { }
        }

        // fromCharacter Main HitMarker 
        static void PlayUiHitKill(global::DamageInfo di, bool predictedDead, bool forceLocalMain)
        {
            var hm = FindHitMarkerSingleton();
            if (hm == null) return;

            if (_miHmOnHit == null)
                _miHmOnHit = hm.GetType().GetMethod("OnHit", BindingFlags.Instance | BindingFlags.NonPublic);
            if (_miHmOnKill == null)
                _miHmOnKill = hm.GetType().GetMethod("OnKill", BindingFlags.Instance | BindingFlags.NonPublic);

            if (forceLocalMain)
            {
                try
                {
                    if (di.fromCharacter == null || di.fromCharacter != global::CharacterMainControl.Main)
                        di.fromCharacter = global::CharacterMainControl.Main;
                }
                catch { }
            }

            try { _miHmOnHit?.Invoke(hm, new object[] { di }); } catch { }
            if (predictedDead)
            {
                try { _miHmOnKill?.Invoke(hm, new object[] { di }); } catch { }
            }
        }

        /// <summary>
        /// Client Player AI / 
        /// </summary>
        public static void ClientPlayForAI(global::CharacterMainControl victim, global::DamageInfo di, bool predictedDead)
        {
            // 1) AI / OnHurt/OnDead 
            var hv = FindHurtVisualOn(victim);
            PlayHurtVisual(hv, di, predictedDead);

            // 2) UI / OnHit/OnKill fromCharacter=Main
            PlayUiHitKill(di, predictedDead, forceLocalMain: true);

            // 3) 
            var pos = (di.damagePoint.sqrMagnitude > 1e-6f ? di.damagePoint : victim.transform.position) + Vector3.up * 2f;
            PopDamageText(pos, di);
        }

        /// <summary>
        /// Client Player HSB 
        /// </summary>
        public static void ClientPlayForDestructible(global::HealthSimpleBase hs, global::DamageInfo di, bool predictedDead)
        {
            // UI / 
            PlayUiHitKill(di, predictedDead, forceLocalMain: true);

            // 
            var basePos = hs ? hs.transform.position : Vector3.zero;
            var pos = (di.damagePoint.sqrMagnitude > 1e-6f ? di.damagePoint : basePos) + Vector3.up * 2f;
            PopDamageText(pos, di);
        }
    }

    public static class LootboxDetectUtil
    {
        public static bool IsPrivateInventory(ItemStatsSystem.Inventory inv)
        {
            if (inv == null) return false;
            if (ReferenceEquals(inv, PlayerStorage.Inventory)) return true;  // 
            if (ReferenceEquals(inv, PetProxy.PetInventory)) return true;    // 
            return false;
        }

        public static bool IsLootboxInventory(Inventory inv)
        {
            if (inv == null) return false;
            // ignore player/private inventories
            if (IsPrivateInventory(inv)) return false;

            // Some games lazily initialize lootbox registries; accessing early can throw.
            // Be defensive and fall back to scene scan if registry getters are not ready.
            try
            {
                var dict = InteractableLootbox.Inventories; // may touch LevelManager.LootBoxInventories
                if (dict != null)
                {
                    foreach (var kv in dict)
                        if (kv.Value == inv) return true;
                }
            }
            catch { /* registry not ready; fall back to scene query */ }

            try
            {
                var boxes = Object.FindObjectsOfType<InteractableLootbox>(true);
                foreach (var b in boxes)
                    if (b && b.Inventory == inv) return true;
            }
            catch { /* scene query failed; treat as not a lootbox */ }

            return false;
        }
    }

    public static class NetPack_Projectile
    {
        public static void PutProjectilePayload(this LiteNetLib.Utils.NetDataWriter w, in ProjectileContext c)
        {
            w.Put(true); // hasPayload
                         // 
            w.Put(c.damage); w.Put(c.critRate); w.Put(c.critDamageFactor);
            w.Put(c.armorPiercing); w.Put(c.armorBreak);
            // 
            w.Put(c.element_Physics); w.Put(c.element_Fire);
            w.Put(c.element_Poison); w.Put(c.element_Electricity); w.Put(c.element_Space);
            // /Status
            w.Put(c.explosionRange); w.Put(c.explosionDamage);
            w.Put(c.buffChance); w.Put(c.bleedChance);
            // 
            w.Put(c.penetrate);
            w.Put(c.fromWeaponItemID);
        }

        // Host/Client ProjectileContext 
        public static bool TryGetProjectilePayload(NetPacketReader r, ref ProjectileContext c)
        {
            if (r.AvailableBytes < 1) return false;
            if (!r.GetBool()) return false; // hasPayload
                                            // 14 float + 2 int = 64 
            if (r.AvailableBytes < 64) return false;

            c.damage = r.GetFloat(); c.critRate = r.GetFloat(); c.critDamageFactor = r.GetFloat();
            c.armorPiercing = r.GetFloat(); c.armorBreak = r.GetFloat();

            c.element_Physics = r.GetFloat(); c.element_Fire = r.GetFloat();
            c.element_Poison = r.GetFloat(); c.element_Electricity = r.GetFloat(); c.element_Space = r.GetFloat();

            c.explosionRange = r.GetFloat(); c.explosionDamage = r.GetFloat();
            c.buffChance = r.GetFloat(); c.bleedChance = r.GetFloat();

            c.penetrate = r.GetInt();
            c.fromWeaponItemID = r.GetInt();
            return true;
        }
    }

    

    // 
    

    

    public class MeleeFxStamp : MonoBehaviour { public float lastFxTime; }

    public static class MeleeFx
    {
        public static void SpawnSlashFx(CharacterModel ctrl)
        {
            if (!ctrl) return;

            // 1) Agent 
            ItemAgent_MeleeWeapon melee = null;

            // 
            Transform[] sockets =
            {
        ctrl.MeleeWeaponSocket,
        // 
        // / null 
        ctrl.GetType().GetField("RightHandSocket") != null ? (Transform)ctrl.GetType().GetField("RightHandSocket").GetValue(ctrl) : null,
        ctrl.GetType().GetField("LefthandSocket")  != null ? (Transform)ctrl.GetType().GetField("LefthandSocket").GetValue(ctrl)  : null,
            };

            foreach (var s in sockets)
            {
                if (melee) break;
                if (!s) continue;
                melee = s.GetComponentInChildren<ItemAgent_MeleeWeapon>(true);
            }

            // / 
            if (!melee)
                melee = ctrl.GetComponentInChildren<ItemAgent_MeleeWeapon>(true);

            if (!melee || !melee.slashFx) return;

            // 2) 
            var stamp = ctrl.GetComponent<MeleeFxStamp>() ?? ctrl.gameObject.AddComponent<MeleeFxStamp>();
            if (Time.time - stamp.lastFxTime < 0.01f) return; // 
            stamp.lastFxTime = Time.time;

            // 3) + / 
            float delay = Mathf.Max(0f, melee.slashFxDelayTime);

            var t = ctrl.transform;
            float forward = Mathf.Clamp(melee.AttackRange * 0.6f, 0.2f, 2.5f);
            Vector3 pos = t.position + t.forward * forward + Vector3.up * 0.6f;
            Quaternion rot = Quaternion.LookRotation(t.forward, Vector3.up);

            Cysharp.Threading.Tasks.UniTask.Void(async () =>
            {
                try
                {
                    await Cysharp.Threading.Tasks.UniTask.Delay(TimeSpan.FromSeconds(delay));
                    UnityEngine.Object.Instantiate(melee.slashFx, pos, rot);
                }
                catch { }
            });
        }

    }

// ===== 2025/10/27 =====
public partial class ModBehaviour : Duckov.Modding.ModBehaviour, INetEventListener
{
    private float _steamLogTimer = 0f;
        private string _cliLobbyIdInput = string.Empty;
    public bool useSteamLobby = false;
        static ModBehaviour()
        {
            try
            {
                AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
                {
                    try
                    {
                        var asmName = new System.Reflection.AssemblyName(e.Name).Name + ".dll";
                        var baseDir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                        var candidate = System.IO.Path.Combine(baseDir, asmName);
                        if (System.IO.File.Exists(candidate))
                            return System.Reflection.Assembly.LoadFrom(candidate);
                    }
                    catch { }
                    return null;
                };
            }
            catch { }
    }
        public static ModBehaviour Instance; // Hello World!
        public bool IsServer { get; private set; } = false;

        public NetManager netManager;
        internal ITransport transport;

        
        public NetDataWriter writer;
        public int port = 9050;
        private List<string> hostList = new List<string>();
        private HashSet<string> hostSet = new HashSet<string>();
        private bool isConnecting = false;
        private string status = "Disconnected";
        private string manualIP = "127.0.0.1";
        private string manualPort = "9050"; // GTX 5090 
        public NetPeer connectedPeer;
        public bool networkStarted = false;
        private float broadcastTimer = 0f;
        private float broadcastInterval = 5f;
        private float syncTimer = 0f;
        private float syncInterval = 0.015f; // =========== Mod TI 0.03 ~33ms ===================
        public Harmony Harmony;

        // Steam transport bookkeeping
        private string _currentTransportSenderId;
        private string _serverPeerId; // client-side: server peer id to target sends
        private readonly Dictionary<string, PlayerStatus> _tPlayerStatuses = new Dictionary<string, PlayerStatus>();

        public bool Pausebool;

        // Server Press NetPeer 
        public readonly HashSet<int> _dedupeShotFrame = new HashSet<int>(); // 
        public PlayerStatus localPlayerStatus;
        public readonly Dictionary<NetPeer, PlayerStatus> playerStatuses = new Dictionary<NetPeer, PlayerStatus>();
        public readonly Dictionary<NetPeer, GameObject> remoteCharacters = new Dictionary<NetPeer, GameObject>();

        // Client Press endPoint(PlayerID) 
        public readonly Dictionary<string, PlayerStatus> clientPlayerStatuses = new Dictionary<string, PlayerStatus>();
        public readonly Dictionary<string, GameObject> clientRemoteCharacters = new Dictionary<string, GameObject>();

        // weaponTypeId(= Item.TypeID) -> projectile prefab
        private readonly Dictionary<int, Projectile> _projCacheByWeaponType = new Dictionary<int, Projectile>();
        // TypeID -> Prefab null 
        private readonly Dictionary<int, GameObject> _muzzleFxCacheByWeaponType = new Dictionary<int, GameObject>();
        // FX muzzleFxPfb Inspector 
        public GameObject defaultMuzzleFx;
       
        public readonly HashSet<Projectile> _serverSpawnedFromClient = new HashSet<Projectile>();

        private readonly Dictionary<int, float> _speedCacheByWeaponType = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _distCacheByWeaponType = new Dictionary<int, float>();

        // ---------------- Grenade caches ----------------
        private readonly Dictionary<int, Grenade> prefabByTypeId = new Dictionary<int, Grenade>();
        private readonly Dictionary<uint, Grenade> serverGrenades = new Dictionary<uint, Grenade>();
        private readonly Dictionary<uint, GameObject> clientGrenades = new Dictionary<uint, GameObject>();
        private uint nextGrenadeId = 1;

        // Removed legacy pending-grenade resolve path (now resolved immediately per spawn)

        public readonly HashSet<Item> _clientSpawnByServerItems = new HashSet<Item>();     // Client Host Prefix 
        public readonly HashSet<Item> _serverSpawnedFromClientItems = new HashSet<Item>(); // Host Client Postfix 

        public readonly Dictionary<uint, Item> serverDroppedItems = new Dictionary<uint, Item>(); // Host 
        public readonly Dictionary<uint, Item> clientDroppedItems = new Dictionary<uint, Item>(); // Client 
        public uint nextDropId = 1;

        public uint nextLocalDropToken = 1;                 // Client token echo SPAWN 
        public readonly HashSet<uint> pendingLocalDropTokens = new HashSet<uint>();
        public readonly Dictionary<uint, Item> pendingTokenItems = new Dictionary<uint, Item>(); // Client token -> item

        // Destructible registry: id -> HealthSimpleBase
        private readonly Dictionary<uint, HealthSimpleBase> _serverDestructibles = new Dictionary<uint, HealthSimpleBase>();
        private readonly Dictionary<uint, HealthSimpleBase> _clientDestructibles = new Dictionary<uint, HealthSimpleBase>();

        private readonly HashSet<uint> _deadDestructibleIds = new HashSet<uint>();
        private readonly Dictionary<string, System.Collections.Generic.List<(int weaponTypeId, int buffId)>> _cliPendingProxyBuffs
    = new Dictionary<string, System.Collections.Generic.List<(int, int)>>();

        // =============== + + ===============
        public struct ItemSnapshot
        {
            public int typeId;
            public int stack;
            public float durability;
            public float durabilityLoss;
            public bool inspected;
            public List<(string key, ItemSnapshot child)> slots;     // 
            public List<ItemSnapshot> inventory;                     // 
        }



        public class PlayerStatus
        {
            public int Latency { get; set; }
            public bool IsInGame { get; set; }
            public string EndPoint { get; set; }
            public string PlayerName { get; set; }
            public bool LastIsInGame { get; set; }
            public Vector3 Position { get; set; }
            public Quaternion Rotation { get; set; }
            public string CustomFaceJson { get; set; }
            public List<EquipmentSyncData> EquipmentList { get; set; } = new List<EquipmentSyncData>();
            public List<WeaponSyncData> WeaponList { get; set; } = new List<WeaponSyncData>();

            public string SceneId;
        }

        private Rect mainWindowRect = new Rect(10, 10, 400, 700);
        private Rect playerStatusWindowRect = new Rect(420, 10, 300, 400);
        private bool showPlayerStatusWindow = false;
        private Vector2 playerStatusScrollPos = Vector2.zero;
        private KeyCode toggleWindowKey = KeyCode.P;

        private bool isinit; // Player slot 

        public static CustomFaceSettingData localPlayerCustomFace;

        // Health 20 
        static readonly System.Reflection.FieldInfo FI_defaultMax =
            typeof(Health).GetField("defaultMaxHealth", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        static readonly System.Reflection.FieldInfo FI_lastMax =
            typeof(Health).GetField("lastMaxHealth", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        static readonly System.Reflection.FieldInfo FI__current =
            typeof(Health).GetField("_currentHealth", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        static readonly System.Reflection.FieldInfo FI_characterCached =
            typeof(Health).GetField("characterCached", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        static readonly System.Reflection.FieldInfo FI_hasCharacter =
            typeof(Health).GetField("hasCharacter", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Host Health -> Peer host null 
        private readonly Dictionary<Health, NetPeer> _srvHealthOwner = new Dictionary<Health, NetPeer>();
        private readonly HashSet<Health> _srvHooked = new HashSet<Health>();

        // Host 
        private readonly Dictionary<Health, (float max, float cur)> _srvLastSent = new Dictionary<Health, (float max, float cur)>();
        private readonly Dictionary<Health, float> _srvNextSend = new Dictionary<Health, float>();
        private const float SRV_HP_SEND_COOLDOWN = 0.05f; // 20Hz

        // Client SELF 
        private bool _cliSelfHpPending;
        private float _cliSelfHpMax, _cliSelfHpCur;

        // Client HP 
        private readonly Dictionary<string, (float max, float cur)> _cliPendingRemoteHp = new Dictionary<string, (float max, float cur)>();

        private bool _cliInitHpReported = false;
        private bool isinit2;

        private string _envReqSid;

        

        private readonly Dictionary<string, string> _cliLastSceneIdByPlayer = new Dictionary<string, string>();
        // Removed unused: _ensureRemoteTick
        private const float EnsureRemoteInterval = 1.0f; // 
        

        private readonly Dictionary<NetPeer, string> _srvPeerScene = new Dictionary<NetPeer, string>();
        private readonly Dictionary<string, string> _tPeerScene = new Dictionary<string, string>();

        private float _envSyncTimer = 0f;
        private const float ENV_SYNC_INTERVAL = 1.0f; // 1 0.5~2 
        // Removed unused: _envReqOnce

        // ====== Lootbox /Status ======
        public bool _applyingLootState = false;         // Client Host Prefix
        public bool _serverApplyingLoot = false;        // Host Client Postfix 

        // Client put token -> Item put Succeeded Player 
        public uint _nextLootToken = 1;
        public readonly Dictionary<uint, Item> _cliPendingPut = new Dictionary<uint, Item>();

        public int _clientLootSetupDepth = 0;
        public bool ClientLootSetupActive => networkStarted && !IsServer && _clientLootSetupDepth > 0;

        public readonly Dictionary<int, int> aiRootSeeds = new Dictionary<int, int>(); // rootId -> seed
        public int sceneSeed = 0;
        public bool freezeAI = true;  // 

        public readonly Dictionary<int, CharacterMainControl> aiById = new Dictionary<int, CharacterMainControl>();
        // aiId -> 

        private float _aiTfTimer;
        private const float AI_TF_INTERVAL = 0.05f;

        // 
        private readonly Dictionary<int, (Vector3 pos, Vector3 dir)> _lastAiSent = new Dictionary<int, (Vector3 pos, Vector3 dir)>();

        private readonly Dictionary<int, int> _aiSerialPerRoot = new Dictionary<int, int>();

        bool _aiSceneReady;
        readonly Queue<(int id, Vector3 p, Vector3 f)> _pendingAiTrans = new Queue<(int id, Vector3 p, Vector3 f)>();


        // AutoBind / 
        private readonly Dictionary<int, float> _lastAutoBindTryTime = new Dictionary<int, float>();
        private const float AUTOBIND_COOLDOWN = 0.20f; // 200ms aiId 
        private const float AUTOBIND_RADIUS = 35f;   // 25~40f
        private const QueryTriggerInteraction AUTOBIND_QTI = QueryTriggerInteraction.Collide;
        private const int AUTOBIND_LAYERMASK = ~0;    // Layer ~~~~~~oi

        private readonly Dictionary<uint, ItemStatsSystem.Item> _cliPendingSlotPlug = new Dictionary<uint, ItemStatsSystem.Item>();

       struct AiAnimState
        {
            public float speed, dirX, dirY;
            public int hand;
            public bool gunReady, dashing;
        }

        // Client 
        private readonly Dictionary<int, AiAnimState> _pendingAiAnims = new Dictionary<int, AiAnimState>();

        // Host 
        private float _aiAnimTimer = 0f;
        private const float AI_ANIM_INTERVAL = 0.10f; // 10Hz 

        public GameObject aiTelegraphFx;

        private readonly Dictionary<int, float> _cliPendingAiHealth = new Dictionary<int, float>();

        public static bool LogAiHpDebug = false; // true [AI-HP] 

        private float _aiNameIconTimer = 0f;
        private const float AI_NAMEICON_INTERVAL = 10f;

        const byte AI_LOADOUT_VER = 5;
        public static bool LogAiLoadoutDebug = true;

        // --- ---
        static readonly AccessTools.FieldRef<CharacterRandomPreset, bool>
            FR_UsePlayerPreset = AccessTools.FieldRefAccess<CharacterRandomPreset, bool>("usePlayerPreset");
        static readonly AccessTools.FieldRef<CharacterRandomPreset, CustomFacePreset>
            FR_FacePreset = AccessTools.FieldRefAccess<CharacterRandomPreset, CustomFacePreset>("facePreset");
        static readonly AccessTools.FieldRef<CharacterRandomPreset, CharacterModel>
            FR_CharacterModel = AccessTools.FieldRefAccess<CharacterRandomPreset, CharacterModel>("characterModel");
        static readonly AccessTools.FieldRef<CharacterRandomPreset, global::CharacterIconTypes>
            FR_IconType = AccessTools.FieldRefAccess<CharacterRandomPreset, global::CharacterIconTypes>("characterIconType");

        private readonly Dictionary<int, string> _aiFaceJsonById = new Dictionary<int, string>();

        // --- pending / / / / / AI---
        private readonly Dictionary<int, (
       List<(int slot, int tid)> equips,
       List<(int slot, int tid)> weapons,
       string faceJson,
       string modelName,
       int iconType,
       bool showName,
       string displayName
       )> pendingAiLoadouts
       = new Dictionary<int, (
           List<(int slot, int tid)> equips,
           List<(int slot, int tid)> weapons,
           string faceJson,
           string modelName,
           int iconType,
           bool showName,
           string displayName
       )>();

        private readonly Dictionary<int, (int capacity, List<(int pos, ItemSnapshot snap)>)> _pendingLootStates
        = new Dictionary<int, (int, List<(int, ItemSnapshot)>)>();

        private int _nextLootUid = 1; // Server 
                                      // Server uid -> inv
        private readonly Dictionary<int, Inventory> _srvLootByUid = new Dictionary<int, Inventory>();
        // Client uid -> inv
        private readonly Dictionary<int, Inventory> _cliLootByUid = new Dictionary<int, Inventory>();

        // Client 
        private readonly Dictionary<int, (int capacity, List<(int pos, ItemSnapshot snap)>)> _pendingLootStatesByUid
            = new Dictionary<int, (int, List<(int, ItemSnapshot)>)>();

        private bool _spectatorEndOnVotePending = false;

        internal bool _skipSpectatorForNextClosure = false;

        // TAKE_OK 
        private struct PendingTakeDest
        {
            // 
            public ItemStatsSystem.Inventory inv;
            public int pos;
            public ItemStatsSystem.Items.Slot slot;

            // 
            public ItemStatsSystem.Inventory srcLoot;
            public int srcPos;
        }

        // token -> 
        private readonly System.Collections.Generic.Dictionary<uint, PendingTakeDest> _cliPendingTake
            = new System.Collections.Generic.Dictionary<uint, PendingTakeDest>();

        // Status //
        public bool IsClient => networkStarted && !IsServer;

        // Client Server 
        public bool ApplyingLootState => _applyingLootState;

        private readonly Dictionary<ItemStatsSystem.Item, (ItemStatsSystem.Item newItem,
                                                ItemStatsSystem.Inventory destInv, int destPos,
                                                ItemStatsSystem.Items.Slot destSlot)>
  _cliSwapByVictim = new Dictionary<ItemStatsSystem.Item, (ItemStatsSystem.Item, ItemStatsSystem.Inventory, int, ItemStatsSystem.Items.Slot)>();

      
        private bool _cliHookedSelf = false;
        private UnityEngine.Events.UnityAction<Health> _cbSelfHpChanged, _cbSelfMaxChanged;
        private UnityEngine.Events.UnityAction<DamageInfo> _cbSelfHurt, _cbSelfDead;
        private float _cliNextSendHp = 0f;
        private (float max, float cur) _cliLastSentHp = (0f, 0f);

        private readonly Dictionary<NetPeer, (float max, float cur)> _srvPendingHp = new Dictionary<NetPeer, (float max, float cur)>();

        bool _cliApplyingSelfSnap = false;
        float _cliEchoMuteUntil = 0f;
        const float SELF_MUTE_SEC = 0.10f;

        private bool showUI = true;

        public struct Pending
        {
            public Inventory inv;
            public int srcPos;
            public int count;
        }

        public static readonly Dictionary<int, Pending> map = new Dictionary<int, Pending>();

        // & 
        private string _lastGoodFaceJson = null;

        // Client Player 
        private readonly Dictionary<string, string> _cliPendingFace = new Dictionary<string, string>();

        private bool _hasPayloadHint;
        private ProjectileContext _payloadHint;

        // Host / 
        private readonly Dictionary<int, float> _explRangeCacheByWeaponType = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _explDamageCacheByWeaponType = new Dictionary<int, float>();

        private bool _cliSelfDeathFired = false;

        public bool _spectatorActive = false;
        public List<CharacterMainControl> _spectateList = new List<CharacterMainControl>();
        private int _spectateIdx = -1;
        private float _spectateNextSwitchTime = 0f;
        public global::DamageInfo _lastDeathInfo;

        const float SELF_ACCEPT_WINDOW = 0.30f;   // 0.3 / 
        float _cliLastSelfHurtAt = -999f;         // 
        float _cliLastSelfHpLocal = -1f;          // 

        // Removed legacy eager loot broadcast constant (no longer used)

        readonly Dictionary<ItemAgent_Gun, GameObject> _muzzleFxByGun = new Dictionary<ItemAgent_Gun, GameObject>();
        readonly Dictionary<ItemAgent_Gun, ParticleSystem> _shellPsByGun = new Dictionary<ItemAgent_Gun, ParticleSystem>();

        // Traverse 
        static readonly System.Reflection.MethodInfo MI_StartVisualRecoil =
            HarmonyLib.AccessTools.Method(typeof(ItemAgent_Gun), "StartVisualRecoil");
        static readonly System.Reflection.FieldInfo FI_RecoilBack =
            HarmonyLib.AccessTools.Field(typeof(ItemAgent_Gun), "_recoilBack");
        static readonly System.Reflection.FieldInfo FI_ShellParticle =
            HarmonyLib.AccessTools.Field(typeof(ItemAgent_Gun), "shellParticle");

        // Removed unused: _fallbackMuzzleAnchor

        readonly Dictionary<string, (ItemAgent_Gun gun, Transform muzzle)> _gunCacheByShooter = new Dictionary<string, (ItemAgent_Gun gun, Transform muzzle)>();

    
  

        void Awake()
        {            
            Debug.Log("ModBehaviour Awake");
            Instance = this;
        }

        private void OnEnable()
        {
            Harmony = new Harmony("DETF_COOP");
            Harmony.PatchAll();
            SceneManager.sceneLoaded += OnSceneLoaded_IndexDestructibles;
            LevelManager.OnAfterLevelInitialized += LevelManager_OnAfterLevelInitialized; 
            LevelManager.OnLevelInitialized += OnLevelInitialized_IndexDestructibles;


            SceneManager.sceneLoaded += SceneManager_sceneLoaded;
            LevelManager.OnLevelInitialized += LevelManager_OnLevelInitialized;
        }

        private void LevelManager_OnAfterLevelInitialized()
        {
            Client_ArmSpawnProtection(15f);
            if (IsServer && networkStarted)
                Server_SceneGateAsync().Forget();
        }

        private void LevelManager_OnLevelInitialized()
        {

            ResetAiSerials();
            if(!IsServer) Client_ReportSelfHealth_IfReadyOnce();
            TrySendSceneReadyOnce();
            if (!IsServer) Client_RequestEnvSync();

            if (IsServer) Server_SendAiSeeds();
            Client_ResetNameIconSeal_OnLevelInit();

        }
        //arg!!!!!!!!!!!
        private void SceneManager_sceneLoaded(Scene arg0, LoadSceneMode arg1)
        {
            TrySendSceneReadyOnce();
            if (!IsServer) Client_RequestEnvSync();

        }

        void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded_IndexDestructibles;
            LevelManager.OnLevelInitialized -= OnLevelInitialized_IndexDestructibles;
         // LevelManager.OnAfterLevelInitialized -= _OnAfterLevelInitialized_ServerGate;

            SceneManager.sceneLoaded -= SceneManager_sceneLoaded;
            LevelManager.OnLevelInitialized -= LevelManager_OnLevelInitialized;
        }

        public void StartNetwork(bool isServer)
        {
            StopNetwork();
            freezeAI = !isServer; IsServer = isServer; writer = new NetDataWriter();
            if (useSteamLobby)
            {
                transport = new SteamSocketsTransport();
                if (IsServer) transport.StartServer(port); else transport.StartClient();
                HookTransportEvents();
                networkStarted = true; status = "Network started";
                hostList.Clear(); hostSet.Clear(); isConnecting = false; connectedPeer = null;
                playerStatuses.Clear(); remoteCharacters.Clear(); clientPlayerStatuses.Clear(); clientRemoteCharacters.Clear();
                InitializeLocalPlayer();
                if (IsServer)
                {
                    ItemAgent_Gun.OnMainCharacterShootEvent -= Host_OnMainCharacterShoot;
                    ItemAgent_Gun.OnMainCharacterShootEvent += Host_OnMainCharacterShoot;
                }
                return;
            }

            netManager = new NetManager(this) { BroadcastReceiveEnabled = true };
            if (IsServer)
            {
                bool started = netManager.Start(port);
                if (started) Debug.Log($"Server started, listening on port {port}");
                else Debug.LogError("Server start failed; check if the port is in use");
            }
            else
            {
                bool started = netManager.Start();
                if (started)
                {
                    Debug.Log("Client started");
                    SendBroadcastDiscovery();
                }
                else Debug.LogError("Client start failed");
            }

            networkStarted = true; status = "Network started";
            hostList.Clear(); hostSet.Clear(); isConnecting = false; connectedPeer = null;
            playerStatuses.Clear(); remoteCharacters.Clear(); clientPlayerStatuses.Clear(); clientRemoteCharacters.Clear();
            InitializeLocalPlayer();
            if (IsServer)
            {
                ItemAgent_Gun.OnMainCharacterShootEvent -= Host_OnMainCharacterShoot;
                ItemAgent_Gun.OnMainCharacterShootEvent += Host_OnMainCharacterShoot;
            }
        }

        private void HookTransportEvents()
        {
            if (transport == null) return;
            transport.OnPeerConnected += id => { if (!IsServer) _serverPeerId = id; };
            transport.OnPeerDisconnected += id => { /* map if needed */ };
            transport.OnData += (id, data) =>
            {
                try
                {
                    _currentTransportSenderId = id;
                    var r = (NetPacketReader)System.Activator.CreateInstance(typeof(NetPacketReader), true);
                    r.SetSource(data, 0, data.Length);
                    HandleNetworkReceive(null, r);
                }
                catch { }
                finally { _currentTransportSenderId = null; }
            };
        }


        private void InitializeLocalPlayer()
        {
            var bool1 = ComputeIsInGame(out var ids);
            localPlayerStatus = new PlayerStatus
            {

                EndPoint = IsServer ? $"Host:{port}" : $"Client:{Guid.NewGuid().ToString().Substring(0, 8)}",
                PlayerName = IsServer ? "Host" : "Client",
                Latency = 0,
                IsInGame = bool1,
                LastIsInGame = bool1,
                Position = Vector3.zero,
                Rotation = Quaternion.identity,
                SceneId = ids,
                CustomFaceJson = LoadLocalCustomFaceJson()
            };
        }

        private string LoadLocalCustomFaceJson()
        {
            try
            {
                string json = null;

                // 1) LevelManager struct null 
                var lm = LevelManager.Instance;
                if (lm != null && lm.CustomFaceManager != null)
                {
                    try
                    {
                        var data1 = lm.CustomFaceManager.LoadMainCharacterSetting(); // struct
                        json = JsonUtility.ToJson(data1);
                    }
                    catch { }
                }

                // 2) ConvertToSaveData 
                if (string.IsNullOrEmpty(json) || json == "{}")
                {
                    try
                    {
                        var main = CharacterMainControl.Main;
                        var model = main != null ? main.characterModel : null;
                        var cf = model != null ? model.CustomFace : null;
                        if (cf != null)
                        {
                            var data2 = cf.ConvertToSaveData(); // struct
                            var j2 = JsonUtility.ToJson(data2);
                            if (!string.IsNullOrEmpty(j2) && j2 != "{}")
                                json = j2;
                        }
                    }
                    catch { }
                }

                // 3) 
                if (!string.IsNullOrEmpty(json) && json != "{}")
                    _lastGoodFaceJson = json;

                // 4) 
                return (!string.IsNullOrEmpty(json) && json != "{}") ? json : (_lastGoodFaceJson ?? "");
            }
            catch
            {
                return _lastGoodFaceJson ?? "";
            }
        }

        public void StopNetwork()
        {
            if (netManager != null && netManager.IsRunning)
            {
                netManager.Stop();
                Debug.Log("Network stopped");
            }
            networkStarted = false;
            connectedPeer = null;

            playerStatuses.Clear();
            clientPlayerStatuses.Clear();

            localPlayerStatus = null;

            foreach (var kvp in remoteCharacters)
                if (kvp.Value != null) Destroy(kvp.Value);
            remoteCharacters.Clear();

            foreach (var kvp in clientRemoteCharacters)
                if (kvp.Value != null) Destroy(kvp.Value);
            clientRemoteCharacters.Clear();

            ItemAgent_Gun.OnMainCharacterShootEvent -= Host_OnMainCharacterShoot;
        }

        private bool IsSelfId(string id)
        {
            var mine = localPlayerStatus?.EndPoint;
            return !string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(mine) && id == mine;
        }

        void Update()
        {
            // Steam transport status logging (guarded)
            if (useSteamLobby && transport is DuckovCoopMod.SteamSocketsTransport st2)
            {
                _steamLogTimer -= Time.deltaTime; if (_steamLogTimer <= 0f) { _steamLogTimer = 5f;
                    try { var list = st2.GetQuickStatuses();
                        #if DEBUG
                        var summary = $"[SteamNet] peers={list.Count}";
                        foreach (var s in list) summary += $" | {s.id} ping={s.ping} state={s.state}";
                        UnityEngine.Debug.Log(summary);
                        #endif
                    } catch { } }
            }
            // Defer equipment-slot event hookup until controller is ready
            if (CharacterMainControl.Main != null && !isinit)
            {
                var eq = CharacterMainControl.Main.EquipmentController;
                if (eq != null)
                {
                    isinit = true;
                    try { Traverse.Create(eq).Field<Slot>("armorSlot").Value.onSlotContentChanged += ModBehaviour_onSlotContentChanged; } catch { }
                    try { Traverse.Create(eq).Field<Slot>("helmatSlot").Value.onSlotContentChanged += ModBehaviour_onSlotContentChanged; } catch { }
                    try { Traverse.Create(eq).Field<Slot>("faceMaskSlot").Value.onSlotContentChanged += ModBehaviour_onSlotContentChanged; } catch { }
                    try { Traverse.Create(eq).Field<Slot>("backpackSlot").Value.onSlotContentChanged += ModBehaviour_onSlotContentChanged; } catch { }
                    try { Traverse.Create(eq).Field<Slot>("headsetSlot").Value.onSlotContentChanged += ModBehaviour_onSlotContentChanged; } catch { }
                    try { CharacterMainControl.Main.OnHoldAgentChanged += Main_OnHoldAgentChanged; } catch { }
                }
            }

          

            // 
            if (Pausebool)
            {
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
            }

            if (CharacterMainControl.Main == null)
            {
                isinit = false;
            }

            if (Input.GetKeyDown(KeyCode.Home))
            {
                showUI = !showUI;
            }

            if (networkStarted)
            {
                if (transport == null) netManager?.PollEvents();
                TrySendSceneReadyOnce();
                if (!isinit2)
                {
                    isinit2 = true;
                    if (!IsServer) Client_ReportSelfHealth_IfReadyOnce();
                }

               // if (IsServer) Server_EnsureAllHealthHooks();

                if (!IsServer && !isConnecting)
                {
                    broadcastTimer += Time.deltaTime;
                    if (broadcastTimer >= broadcastInterval)
                    {
                        // In Steam transport mode, skip UDP broadcast discovery
                        if (transport == null)
                            SendBroadcastDiscovery();
                        broadcastTimer = 0f;
                    }
                }

                syncTimer += Time.deltaTime;
                if (syncTimer >= syncInterval)
                {
                    SendPositionUpdate();
                    SendAnimationStatus();
                    syncTimer = 0f;

                    //if (!IsServer)
                    //{
                    // if (MultiSceneCore.Instance != null && MultiSceneCore.MainSceneID != "Base")
                    // {
                    // if (LevelManager.Instance.MainCharacter != null && LevelManager.Instance.MainCharacter.Health.MaxHealth > 0f)
                    // {
                    // // Debug.Log(LevelManager.Instance.MainCharacter.Health.CurrentHealth);
                    // if (LevelManager.Instance.MainCharacter.Health.CurrentHealth <= 0f && Client_IsSpawnProtected())
                    // {
                    // // Debug.Log(LevelManager.Instance.MainCharacter.Health.CurrentHealth);
                    // Client_EnsureSelfDeathEvent(LevelManager.Instance.MainCharacter.Health, LevelManager.Instance.MainCharacter);
                    // }
                    // }
                    // }
                    //}
                }

                if (!IsServer && !string.IsNullOrEmpty(_sceneReadySidSent) && _envReqSid != _sceneReadySidSent)
                {
                    _envReqSid = _sceneReadySidSent;   // 
                    Client_RequestEnvSync();           // Host / 
                }

                if (IsServer)
                {
                    _aiNameIconTimer += Time.deltaTime;
                    if (_aiNameIconTimer >= AI_NAMEICON_INTERVAL)
                    {
                        _aiNameIconTimer = 0f;

                        foreach (var kv in aiById)
                        {
                            int id = kv.Key;
                            var cmc = kv.Value;
                            if (!cmc) continue;

                            var pr = cmc.characterPreset;
                            if (!pr) continue;

                            int iconType = 0;
                            bool showName = false;
                            try
                            {
                                iconType = (int)FR_IconType(pr);
                                showName = pr.showName;
                                // 
                                if (iconType == 0 && pr.GetCharacterIcon() != null)
                                    iconType = (int)FR_IconType(pr);
                            }
                            catch { }

                            // or AI 
                            if (iconType != 0 || showName)
                                Server_BroadcastAiNameIcon(id, cmc);
                        }
                    }
                }

                // Host 
                if (IsServer)
                {
                    _envSyncTimer += Time.deltaTime;
                    if (_envSyncTimer >= ENV_SYNC_INTERVAL)
                    {
                        _envSyncTimer = 0f;
                        Server_BroadcastEnvSync();
                    }

                    _aiAnimTimer += Time.deltaTime;
                    if (_aiAnimTimer >= AI_ANIM_INTERVAL)
                    {
                        _aiAnimTimer = 0f;
                        Server_BroadcastAiAnimations();
                    }

                }

                int burst = 64; // 
                while (_aiSceneReady && _pendingAiTrans.Count > 0 && burst-- > 0)
                {
                    var (id, p, f) = _pendingAiTrans.Dequeue();
                    ApplyAiTransform(id, p, f);
                }

            }

            if (networkStarted && IsServer)
            {
                _aiTfTimer += Time.deltaTime;
                if (_aiTfTimer >= AI_TF_INTERVAL)
                {
                    _aiTfTimer = 0f;
                    Server_BroadcastAiTransforms();
                }
            }

            UpdatePlayerStatuses();
            UpdateRemoteCharacters();

            if (Input.GetKeyDown(toggleWindowKey))
            {
                showPlayerStatusWindow = !showPlayerStatusWindow;
            }

            // Removed: legacy pending-grenade resolver no longer used

            if (!IsServer)
            {
                if (_cliSelfHpPending && CharacterMainControl.Main != null)
                {
                    ApplyHealthAndEnsureBar(CharacterMainControl.Main.gameObject, _cliSelfHpMax, _cliSelfHpCur);
                    _cliSelfHpPending = false;
                }
            }


            if (IsServer) Server_EnsureAllHealthHooks();
            if (!IsServer) Client_ApplyPendingSelfIfReady();
            if (!IsServer) Client_ReportSelfHealth_IfReadyOnce();

            // Press J SwitchReady
            if (sceneVoteActive && Input.GetKeyDown(readyKey))
            {
                localReady = !localReady;
                if (IsServer) Server_OnSceneReadySet(null, localReady);  // Host 
                else Client_SendReadySet(localReady);           // Client Host
            }

            if (networkStarted)
            {
                TrySendSceneReadyOnce();
                if (_envReqSid != _sceneReadySidSent)
                {
                    _envReqSid = _sceneReadySidSent;
                    Client_RequestEnvSync();
                }

                // Host Health / 
                if (IsServer) Server_EnsureAllHealthHooks();

                // Client Succeeded Succeeded
                if (!IsServer && !_cliInitHpReported) Client_ReportSelfHealth_IfReadyOnce();

                // Client Health 
                if (!IsServer) Client_HookSelfHealth();
            }

            if (_spectatorActive)
            {
                ClosureView.Instance.gameObject.SetActive(false);
                // / / 
                _spectateList = _spectateList.Where(c =>
                {
                    if (!IsAlive(c)) return false;

                    string mySceneId = localPlayerStatus != null ? localPlayerStatus.SceneId : null;
                    if (string.IsNullOrEmpty(mySceneId))
                        ComputeIsInGame(out mySceneId);

                    // CMC peer SceneId
                    string peerScene = null;
                    if (IsServer)
                    {
                        foreach (var kv in remoteCharacters)
                            if (kv.Value != null && kv.Value.GetComponent<CharacterMainControl>() == c)
                            { if (!_srvPeerScene.TryGetValue(kv.Key, out peerScene) && playerStatuses.TryGetValue(kv.Key, out var pst)) peerScene = pst?.SceneId; break; }
                    }
                    else
                    {
                        foreach (var kv in clientRemoteCharacters)
                            if (kv.Value != null && kv.Value.GetComponent<CharacterMainControl>() == c)
                            { if (clientPlayerStatuses.TryGetValue(kv.Key, out var pst)) peerScene = pst?.SceneId; break; }
                    }

                    return AreSameMap(mySceneId, peerScene);
                }).ToList();


                // 
                if (_spectateList.Count == 0 || AllPlayersDead())
                {
                    EndSpectatorAndShowClosure();
                    return;
                }

                if (_spectateIdx < 0 || _spectateIdx >= _spectateList.Count)
                    _spectateIdx = 0;

                // 
                if (!IsAlive(_spectateList[_spectateIdx]))
                    SpectateNext();

                // / Switch 
                if (Time.unscaledTime >= _spectateNextSwitchTime)
                {
                    if (Input.GetMouseButtonDown(0)) { SpectateNext(); _spectateNextSwitchTime = Time.unscaledTime + 0.15f; }
                    if (Input.GetMouseButtonDown(1)) { SpectatePrev(); _spectateNextSwitchTime = Time.unscaledTime + 0.15f; }
                }
            }




        }

        private void Main_OnHoldAgentChanged(DuckovItemAgent obj)
        {
            if (obj == null) return;

            string itemId = obj.Item?.TypeID.ToString() ?? "";
            HandheldSocketTypes slotHash = obj.handheldSocket;

            // / 
            var gunAgent = obj as ItemAgent_Gun;
            if (gunAgent != null)
            {
                int typeId;
                if (int.TryParse(itemId, out typeId))
                {
                    // Agent ItemSetting_XXX 
                    var setting = gunAgent.GunItemSetting; // 
                    Projectile pfb = (setting != null && setting.bulletPfb != null)
                        ? setting.bulletPfb
                        : Duckov.Utilities.GameplayDataSettings.Prefabs.DefaultBullet;

                    _projCacheByWeaponType[typeId] = pfb;
                    _muzzleFxCacheByWeaponType[typeId] = (setting != null) ? setting.muzzleFxPfb : null;
                }
            }

            // Player 
            var weaponData = new WeaponSyncData
            {
                SlotHash = (int)slotHash,
                ItemId = itemId
            };
            SendWeaponUpdate(weaponData);
        }



        // moved to Mod.Player.cs: SendAnimationStatus



        // Host Client Client PlayerID 
        // moved to Mod.Player.cs: HandleClientAnimationStatus


        // Host Press NetPeer 
        // moved to Mod.Player.cs: HandleRemoteAnimationStatus

        static Animator ResolveRemoteAnimator(GameObject remoteObj)
        {
            var cmc = remoteObj.GetComponent<CharacterMainControl>();
            if (cmc == null || cmc.characterModel == null) return null;
            var model = cmc.characterModel;

            var mb = model.GetComponent<CharacterAnimationControl_MagicBlend>();
            if (mb != null && mb.animator != null) return mb.animator;

            var cac = model.GetComponent<CharacterAnimationControl>();
            if (cac != null && cac.animator != null) return cac.animator;

            // Animator
            return model.GetComponent<Animator>();
        }


        private void UpdatePlayerStatuses()
        {
            if (netManager == null || !netManager.IsRunning || localPlayerStatus == null)
                return;
            var bool1 = ComputeIsInGame(out var ids);
            bool currentIsInGame = bool1;
            var levelManager = LevelManager.Instance;

            if (localPlayerStatus.IsInGame != currentIsInGame)
            {
                localPlayerStatus.IsInGame = currentIsInGame;
                localPlayerStatus.LastIsInGame = currentIsInGame;

                if (levelManager != null && levelManager.MainCharacter != null)
                {
                    localPlayerStatus.Position = levelManager.MainCharacter.transform.position;
                    localPlayerStatus.Rotation = levelManager.MainCharacter.modelRoot.transform.rotation;
                    localPlayerStatus.CustomFaceJson = LoadLocalCustomFaceJson();
                }

                if (currentIsInGame && levelManager != null)
                {
                    // Scene Host 
                    TrySendSceneReadyOnce();

                }


                if (!IsServer) SendClientStatusUpdate();
                else SendPlayerStatusUpdate();
            }
            else if (currentIsInGame && levelManager != null && levelManager.MainCharacter != null)
            {
                localPlayerStatus.Position = levelManager.MainCharacter.transform.position;
                localPlayerStatus.Rotation = levelManager.MainCharacter.modelRoot.transform.rotation;
            }

            if (currentIsInGame)
            {
                localPlayerStatus.CustomFaceJson = LoadLocalCustomFaceJson();
            }
        }

        private void UpdateRemoteCharacters()
        {
            if (IsServer)
            {
                foreach (var kvp in remoteCharacters)
                {
                    var go = kvp.Value;
                    if (!go) continue;
                    NetInterpUtil.Attach(go); // NetInterpolator 
                }
            }
            else
            {
                foreach (var kvp in clientRemoteCharacters)
                {
                    var go = kvp.Value;
                    if (!go) continue;
                    NetInterpUtil.Attach(go);
                }
            }
        }


        // moved to Mod.Player.cs: CreateRemoteCharacterAsync
        // moved to Mod.Player.cs: CreateRemoteCharacterForClient

        // moved to Mod.Player.cs: ModBehaviour_onSlotContentChanged

        // moved to Mod.Player.cs: SendEquipmentUpdate

            writer.Reset();
            writer.Put((byte)Op.EQUIPMENT_UPDATE);      
            writer.Put(localPlayerStatus.EndPoint);
            writer.Put(equipmentData.SlotHash);
            writer.Put(equipmentData.ItemId ?? "");

            if (IsServer) TransportBroadcast(writer, true);
            else TransportSendToServer(writer, true);
        }


        // moved to Mod.Player.cs: SendWeaponUpdate


        // moved to Mod.Player.cs: HandleEquipmentUpdate

        // moved to Mod.Player.cs: HandleWeaponUpdate

        // Host Press NetPeer 
        // moved to Mod.Player.cs: ApplyEquipmentUpdate



        // moved to Mod.Player.cs: ApplyEquipmentUpdate_Client

        private readonly Dictionary<string, string> _lastWeaponAppliedByPeer = new Dictionary<string, string>();
        private readonly Dictionary<string, float> _lastWeaponAppliedTimeByPeer = new Dictionary<string, float>();
        private readonly Dictionary<string, string> _lastWeaponAppliedByPlayer = new Dictionary<string, string>();
        private readonly Dictionary<string, float> _lastWeaponAppliedTimeByPlayer = new Dictionary<string, float>();

        // moved to Mod.Player.cs: SafeKillItemAgent

        // / 
        // moved to Mod.Player.cs: ClearWeaponSlot

        // slotHash HandheldSocketTypes 
        // moved to Mod.Player.cs: ResolveSocketOrDefault

        // moved to Mod.Player.cs: WeaponApplyDebounce

        // Host Press NetPeer 
        // moved to Mod.Player.cs: ApplyWeaponUpdate


        // Client Press PlayerID + + agent + + 
        // moved to Mod.Player.cs: ApplyWeaponUpdate_Client

        public void OnConnectionRequest(ConnectionRequest request)
        {
            if (IsServer)
            {
                if (request.Data != null && request.Data.GetString() == "gameKey") request.Accept();
                else request.Reject();
            }
            else request.Reject();
        }

        public void OnPeerConnected(NetPeer peer)
        {
            Debug.Log($"ConnectSucceeded: {peer.EndPoint}");
            connectedPeer = peer;

            if (!IsServer)
            {
                status = $"Connected to {peer.EndPoint}";
                isConnecting = false;
                SendClientStatusUpdate();
            }

            if (!playerStatuses.ContainsKey(peer))
            {
                playerStatuses[peer] = new PlayerStatus
                {
                    EndPoint = peer.EndPoint.ToString(),
                    PlayerName = IsServer ? $"Player_{peer.Id}" : "Host",
                    Latency = peer.Ping,
                    IsInGame = false,
                    LastIsInGame = false,
                    Position = Vector3.zero,
                    Rotation = Quaternion.identity,
                    CustomFaceJson = null
                };
            }

            if (IsServer) SendPlayerStatusUpdate();

            if (IsServer)
            {
                // 1) Host 
                var hostMain = CharacterMainControl.Main;
                var hostH = hostMain ? hostMain.GetComponentInChildren<Health>(true) : null;
                if (hostH)
                {
                    var w = new NetDataWriter();
                    w.Put((byte)Op.AUTH_HEALTH_REMOTE);
                    w.Put(GetPlayerId(null)); // Host playerId
                    try { w.Put(hostH.MaxHealth); } catch { w.Put(0f); }
                    try { w.Put(hostH.CurrentHealth); } catch { w.Put(0f); }
                    TransportSend(peer, w, true);
                }

                if (remoteCharacters != null)
                {
                    foreach (var kv in remoteCharacters)
                    {
                        var owner = kv.Key;
                        var go = kv.Value;

                        if (owner == null || go == null) continue;

                        var h = go.GetComponentInChildren<Health>(true);
                        if (!h) continue;

                        var w = new NetDataWriter();
                        w.Put((byte)Op.AUTH_HEALTH_REMOTE);
                        w.Put(GetPlayerId(owner)); // playerId
                        try { w.Put(h.MaxHealth); } catch { w.Put(0f); }
                        try { w.Put(h.CurrentHealth); } catch { w.Put(0f); }
                        TransportSend(peer, w, true);
                    }
                }
            }

        }

        // Q 
        private void HandlePositionUpdate(NetPeer sender, NetPacketReader reader)
        {
            string endPoint = reader.GetString();
            Vector3 position = reader.GetV3cm(); // 
            Vector3 dir = reader.GetDir();
            Quaternion rotation = Quaternion.LookRotation(dir, Vector3.up);

            if (transport == null)
            {
                foreach (var p in netManager.ConnectedPeerList)
                {
                    if (p == sender) continue;
                    var w = new NetDataWriter();
                    w.Put((byte)Op.POSITION_UPDATE);
                    w.Put(endPoint);
                    NetPack.PutV3cm(w, position);
                    NetPack.PutDir(w, dir);
                    TransportSend(p, w, false);
                }
            }
            else
            {
                // Forward to all Steam peers except the sender
                var senderId = _currentTransportSenderId;
                if (transport is SteamSocketsTransport sst)
                {
                    foreach (var pid in sst.Peers)
                    {
                        if (!string.IsNullOrEmpty(senderId) && pid == senderId) continue;
                        var w = new NetDataWriter();
                        w.Put((byte)Op.POSITION_UPDATE);
                        w.Put(endPoint);
                        NetPack.PutV3cm(w, position);
                        NetPack.PutDir(w, dir);
                        sst.Send(pid, w.CopyData(), false);
                    }
                }
            }
        }


        private void HandlePositionUpdate_Q(NetPeer peer, string endPoint, Vector3 position, Quaternion rotation)
        {
            if (peer != null && playerStatuses.TryGetValue(peer, out var pst))
            {
                pst.Position = position;
                pst.Rotation = rotation;

                if (remoteCharacters.TryGetValue(peer, out var go) && go != null)
                {
                    var ni = NetInterpUtil.Attach(go);
                    ni?.Push(position, rotation);
                }

                if (transport == null)
                {
                    foreach (var p in netManager.ConnectedPeerList)
                    {
                        if (p == peer) continue;
                        writer.Reset();
                        writer.Put((byte)Op.POSITION_UPDATE);
                        writer.Put(pst.EndPoint ?? endPoint);
                        writer.PutV3cm(position);
                        Vector3 fwd = rotation * Vector3.forward;
                        writer.PutDir(fwd);
                        TransportSend(p, writer, false);
                    }
                }
                else if (transport is SteamSocketsTransport sst)
                {
                    var senderId = _currentTransportSenderId;
                    foreach (var pid in sst.Peers)
                    {
                        if (!string.IsNullOrEmpty(senderId) && pid == senderId) continue;
                        var wtmp = new NetDataWriter();
                        wtmp.Put((byte)Op.POSITION_UPDATE);
                        wtmp.Put(pst.EndPoint ?? endPoint);
                        wtmp.PutV3cm(position);
                        Vector3 fwd2 = rotation * Vector3.forward;
                        wtmp.PutDir(fwd2);
                        sst.Send(pid, wtmp.CopyData(), false);
                    }
                }
            }
        }


        private void HandleClientStatusUpdate(NetPeer peer, NetPacketReader reader)
        {
            string endPoint = reader.GetString();
            string playerName = reader.GetString();
            bool isInGame = reader.GetBool();
            Vector3 position = reader.GetVector3();
            Quaternion rotation = reader.GetQuaternion();
            string sceneId = reader.GetString();
            string customFaceJson = reader.GetString();

            int equipmentCount = reader.GetInt();
            var equipmentList = new List<EquipmentSyncData>();
            for (int i = 0; i < equipmentCount; i++)
                equipmentList.Add(EquipmentSyncData.Deserialize(reader));

            int weaponCount = reader.GetInt();
            var weaponList = new List<WeaponSyncData>();
            for (int i = 0; i < weaponCount; i++)
                weaponList.Add(WeaponSyncData.Deserialize(reader));

            PlayerStatus st2;
            if (transport != null && peer == null)
            {
                var key = _currentTransportSenderId;
                if (string.IsNullOrEmpty(key)) return;
                if (!_tPlayerStatuses.TryGetValue(key, out st2))
                {
                    st2 = new PlayerStatus();
                    _tPlayerStatuses[key] = st2;
                }
                // Try to get ping/state from transport
                try
                {
                    if (transport is SteamSocketsTransport sst)
                    {
                        var qs = sst.GetQuickStatuses();
                        var me = qs.FirstOrDefault(q => q.id == key);
                        st2.Latency = me.ping;
                    }
                }
                catch { }
            }
            else
            {
                if (!playerStatuses.ContainsKey(peer))
                    playerStatuses[peer] = new PlayerStatus();
                st2 = playerStatuses[peer];
                try { st2.Latency = peer?.Ping ?? -1; } catch { st2.Latency = -1; }
            }
            st2.EndPoint = endPoint;
            st2.PlayerName = playerName;
            st2.IsInGame = isInGame;
            st2.LastIsInGame = isInGame;
            st2.Position = position;
            st2.Rotation = rotation;
            if (!string.IsNullOrEmpty(customFaceJson))
                st2.CustomFaceJson = customFaceJson;
            st2.EquipmentList = equipmentList;
            st2.WeaponList = weaponList;
            st2.SceneId = sceneId;

            if (isInGame && !remoteCharacters.ContainsKey(peer))
            {
                CreateRemoteCharacterAsync(peer, position, rotation, customFaceJson).Forget();
                foreach (var e in equipmentList) ApplyEquipmentUpdate(peer, e.SlotHash, e.ItemId).Forget();
                foreach (var w in weaponList) ApplyWeaponUpdate(peer, w.SlotHash, w.ItemId).Forget();
            }
            else if (isInGame)
            {
                if (remoteCharacters.TryGetValue(peer, out var go) && go != null)
                {
                    go.transform.position = position;
                    go.GetComponentInChildren<CharacterMainControl>().modelRoot.transform.rotation = rotation;
                }
                foreach (var e in equipmentList) ApplyEquipmentUpdate(peer, e.SlotHash, e.ItemId).Forget();
                foreach (var w in weaponList) ApplyWeaponUpdate(peer, w.SlotHash, w.ItemId).Forget();
            }

            playerStatuses[peer] = st2;

            SendPlayerStatusUpdate();

        }

        // Client Host clientScatter / ads01 
        public void Net_OnClientShoot(ItemAgent_Gun gun, Vector3 muzzle, Vector3 baseDir, Vector3 firstCheckStart)
        {
            if (IsServer || connectedPeer == null) return;

            if (baseDir.sqrMagnitude < 1e-8f)
            {
                var fallback = (gun != null && gun.muzzle != null) ? gun.muzzle.forward : Vector3.forward;
                baseDir = fallback.sqrMagnitude < 1e-8f ? Vector3.forward : fallback.normalized;
            }

            if (gun && gun.muzzle)
            {
                int weaponType = (gun.Item != null) ? gun.Item.TypeID : 0;
                Client_PlayLocalShotFx(gun, gun.muzzle, weaponType);
            }

            writer.Reset();
            writer.Put((byte)Op.FIRE_REQUEST);        // opcode
            writer.Put(localPlayerStatus.EndPoint);   // shooterId
            writer.Put(gun.Item.TypeID);              // weaponType
            writer.PutV3cm(muzzle);
            writer.PutDir(baseDir);
            writer.PutV3cm(firstCheckStart);

            // === ADSStatus Host ===
            float clientScatter = 0f;
            float ads01 = 0f;
            try
            {
                clientScatter = Mathf.Max(0f, gun.CurrentScatter); // Client ADS 
                ads01 = (gun.IsInAds ? 1f : 0f);
            }
            catch { }
            writer.Put(clientScatter);
            writer.Put(ads01);

            // 
            var hint = new ProjectileContext();
            try
            {
                bool hasBulletItem = (gun.BulletItem != null);

                // 
                float charMul = gun.CharacterDamageMultiplier;
                float bulletMul = hasBulletItem ? Mathf.Max(0.0001f, gun.BulletDamageMultiplier) : 1f;
                int shots = Mathf.Max(1, gun.ShotCount);
                hint.damage = gun.Damage * bulletMul * charMul / shots;
                if (gun.Damage > 1f && hint.damage < 1f) hint.damage = 1f;

                // 
                float bulletCritRateGain = hasBulletItem ? gun.bulletCritRateGain : 0f;
                float bulletCritDmgGain = hasBulletItem ? gun.BulletCritDamageFactorGain : 0f;
                hint.critDamageFactor = (gun.CritDamageFactor + bulletCritDmgGain) * (1f + gun.CharacterGunCritDamageGain);
                hint.critRate = gun.CritRate * (1f + gun.CharacterGunCritRateGain + bulletCritRateGain);

                // / / / 
                switch (gun.GunItemSetting.element)
                {
                    case ElementTypes.physics: hint.element_Physics = 1f; break;
                    case ElementTypes.fire: hint.element_Fire = 1f; break;
                    case ElementTypes.poison: hint.element_Poison = 1f; break;
                    case ElementTypes.electricity: hint.element_Electricity = 1f; break;
                    case ElementTypes.space: hint.element_Space = 1f; break;
                }

                hint.armorPiercing = gun.ArmorPiercing + (hasBulletItem ? gun.BulletArmorPiercingGain : 0f);
                hint.armorBreak = gun.ArmorBreak + (hasBulletItem ? gun.BulletArmorBreakGain : 0f);
                hint.explosionRange = gun.BulletExplosionRange;
                hint.explosionDamage = gun.BulletExplosionDamage * gun.ExplosionDamageMultiplier;
                if (hasBulletItem)
                {
                    hint.buffChance = gun.BulletBuffChanceMultiplier * gun.BuffChance;
                    hint.bleedChance = gun.BulletBleedChance;
                }
                hint.penetrate = gun.Penetrate;
                hint.fromWeaponItemID = (gun.Item != null ? gun.Item.TypeID : 0);
            }
            catch { /* ignore */ }

            writer.PutProjectilePayload(hint);  // Host
            TransportSendToServer(writer, true);
        }


        private void HandleFireRequest(NetPeer peer, NetPacketReader r)
        {
            string shooterId = r.GetString();
            int weaponType = r.GetInt();
            Vector3 muzzle = r.GetV3cm();
            Vector3 baseDir = r.GetDir();
            Vector3 firstCheckStart = r.GetV3cm();

            // === Client & ADS ===
            float clientScatter = 0f;
            float ads01 = 0f;
            try
            {
                clientScatter = r.GetFloat();
                ads01 = r.GetFloat();
            }
            catch
            {
                clientScatter = 0f; ads01 = 0f; // 
            }

            // Client Try 
            _payloadHint = default;
            _hasPayloadHint = NetPack_Projectile.TryGetProjectilePayload(r, ref _payloadHint);

            if (!remoteCharacters.TryGetValue(peer, out var who) || !who) { _hasPayloadHint = false; return; }

            var cm = who.GetComponent<CharacterMainControl>().characterModel;

            // Player 
            ItemAgent_Gun gun = null;
            if (cm)
            {
                try
                {
                    gun = who.GetComponent<CharacterMainControl>()?.GetGun();
                    if (!gun && cm.RightHandSocket) gun = cm.RightHandSocket.GetComponentInChildren<ItemAgent_Gun>(true);
                    if (!gun && cm.LefthandSocket) gun = cm.LefthandSocket.GetComponentInChildren<ItemAgent_Gun>(true);
                    if (!gun && cm.MeleeWeaponSocket) gun = cm.MeleeWeaponSocket.GetComponentInChildren<ItemAgent_Gun>(true);
                }
                catch { }
            }

            // muzzle 
            if (muzzle == default || muzzle.sqrMagnitude < 1e-8f)
            {
                Transform mz = null;
                if (cm)
                {
                    if (!mz && cm.RightHandSocket) mz = cm.RightHandSocket.Find("Muzzle");
                    if (!mz && cm.LefthandSocket) mz = cm.LefthandSocket.Find("Muzzle");
                    if (!mz && cm.MeleeWeaponSocket) mz = cm.MeleeWeaponSocket.Find("Muzzle");
                }
                if (!mz) mz = who.transform.Find("Muzzle");
                if (mz) muzzle = mz.position;
            }

            // gun gun 
            Vector3 finalDir;
            float speed, distance;

            if (gun) // Host 
            {
                if (!Server_SpawnProjectile(gun, muzzle, baseDir, firstCheckStart, out finalDir, clientScatter, ads01))
                { _hasPayloadHint = false; return; }

                speed = gun.BulletSpeed * (gun.Holder ? gun.Holder.GunBulletSpeedMultiplier : 1f);
                distance = gun.BulletDistance + 0.4f;
            }
            else
            {
                // gun 
                finalDir = (baseDir.sqrMagnitude > 1e-8f ? baseDir.normalized : Vector3.forward);
                speed = _speedCacheByWeaponType.TryGetValue(weaponType, out var sp) ? sp : 60f;
                distance = _distCacheByWeaponType.TryGetValue(weaponType, out var dist) ? dist : 50f;
                // Server holder Projectile 
            }

            // FIRE_EVENT Host ctx 
            writer.Reset();
            writer.Put((byte)Op.FIRE_EVENT);
            writer.Put(shooterId);
            writer.Put(weaponType);
            writer.PutV3cm(muzzle);
            writer.PutDir(finalDir);
            writer.Put(speed);
            writer.Put(distance);

            var payloadCtx = new ProjectileContext();
            if (gun != null)
            {
                bool hasBulletItem = false;
                try { hasBulletItem = (gun.BulletItem != null); } catch { }

                // payload 
                try
                {
                    float charMul = gun.CharacterDamageMultiplier;
                    float bulletMul = hasBulletItem ? Mathf.Max(0.0001f, gun.BulletDamageMultiplier) : 1f;
                    int shots = Mathf.Max(1, gun.ShotCount);
                    payloadCtx.damage = gun.Damage * bulletMul * charMul / shots;
                    if (gun.Damage > 1f && payloadCtx.damage < 1f) payloadCtx.damage = 1f;
                }
                catch { if (payloadCtx.damage <= 0f) payloadCtx.damage = 1f; }

                try
                {
                    float bulletCritRateGain = hasBulletItem ? gun.bulletCritRateGain : 0f;
                    float bulletCritDmgGain = hasBulletItem ? gun.BulletCritDamageFactorGain : 0f;
                    payloadCtx.critDamageFactor = (gun.CritDamageFactor + bulletCritDmgGain) * (1f + gun.CharacterGunCritDamageGain);
                    payloadCtx.critRate = gun.CritRate * (1f + gun.CharacterGunCritRateGain + bulletCritRateGain);
                }
                catch { }

                try
                {
                    float apGain = hasBulletItem ? gun.BulletArmorPiercingGain : 0f;
                    float abGain = hasBulletItem ? gun.BulletArmorBreakGain : 0f;
                    payloadCtx.armorPiercing = gun.ArmorPiercing + apGain;
                    payloadCtx.armorBreak = gun.ArmorBreak + abGain;
                }
                catch { }

                try
                {
                    var setting = gun.GunItemSetting;
                    if (setting != null)
                    {
                        switch (setting.element)
                        {
                            case ElementTypes.physics: payloadCtx.element_Physics = 1f; break;
                            case ElementTypes.fire: payloadCtx.element_Fire = 1f; break;
                            case ElementTypes.poison: payloadCtx.element_Poison = 1f; break;
                            case ElementTypes.electricity: payloadCtx.element_Electricity = 1f; break;
                            case ElementTypes.space: payloadCtx.element_Space = 1f; break;
                        }
                    }

                    payloadCtx.explosionRange = gun.BulletExplosionRange;
                    payloadCtx.explosionDamage = gun.BulletExplosionDamage * gun.ExplosionDamageMultiplier;

                    if (hasBulletItem)
                    {
                        payloadCtx.buffChance = gun.BulletBuffChanceMultiplier * gun.BuffChance;
                        payloadCtx.bleedChance = gun.BulletBleedChance;
                    }

                    payloadCtx.penetrate = gun.Penetrate;
                    payloadCtx.fromWeaponItemID = (gun.Item != null ? gun.Item.TypeID : 0);
                }
                catch { }
            }

            writer.PutProjectilePayload(payloadCtx);
            TransportBroadcast(writer, true);

            PlayMuzzleFxAndShell(shooterId, weaponType, muzzle, finalDir);
            PlayShootAnimOnServerPeer(peer);

            // hint Status
            _hasPayloadHint = false;
        }




        private void PlayShootAnimOnServerPeer(NetPeer peer)
        {
            if (!remoteCharacters.TryGetValue(peer, out var who) || !who) return;
            var animCtrl = who.GetComponent<CharacterMainControl>().characterModel.GetComponentInParent<CharacterAnimationControl_MagicBlend>();
            if (animCtrl && animCtrl.animator)
            {
                animCtrl.OnAttack();  // Attack trigger + 
            }
        }

        private void PlayMuzzleFxAndShell(string shooterId, int weaponType, Vector3 muzzlePos, Vector3 finalDir)
        {
            try
            {
                // 1) shooter GameObject
                GameObject shooterGo = null;
                if (IsSelfId(shooterId))
                {
                    var cmSelf = LevelManager.Instance?.MainCharacter?.GetComponent<CharacterMainControl>();
                    if (cmSelf) shooterGo = cmSelf.gameObject;
                }
                else if (!string.IsNullOrEmpty(shooterId) && shooterId.StartsWith("AI:"))
                {
                    if (int.TryParse(shooterId.Substring(3), out var aiId))
                    {
                        if (aiById.TryGetValue(aiId, out var cmc) && cmc)
                            shooterGo = cmc.gameObject;
                    }
                }
                else
                {
                    if (IsServer)
                    {
                        // Server EndPoint -> NetPeer -> remoteCharacters
                        NetPeer foundPeer = null;
                        foreach (var kv in playerStatuses)
                        {
                            if (kv.Value != null && kv.Value.EndPoint == shooterId) { foundPeer = kv.Key; break; }
                        }
                        if (foundPeer != null) remoteCharacters.TryGetValue(foundPeer, out shooterGo);
                    }
                    else
                    {
                        // Client shooterId 
                        clientRemoteCharacters.TryGetValue(shooterId, out shooterGo);
                    }
                }

                // 2) GetComponentInChildren 
                ItemAgent_Gun gun = null;
                Transform muzzleTf = null;
                if (!string.IsNullOrEmpty(shooterId))
                {
                    if (_gunCacheByShooter.TryGetValue(shooterId, out var cached) && cached.gun)
                    {
                        gun = cached.gun;
                        muzzleTf = cached.muzzle;
                    }
                }

                // 3) 
                if (shooterGo && (!gun || !muzzleTf))
                {
                    var cmc = shooterGo.GetComponent<CharacterMainControl>();
                    var model = cmc ? cmc.characterModel : null;

                    if (!gun && model)
                    {
                        if (model.RightHandSocket && !gun) gun = model.RightHandSocket.GetComponentInChildren<ItemAgent_Gun>(true);
                        if (model.LefthandSocket && !gun) gun = model.LefthandSocket.GetComponentInChildren<ItemAgent_Gun>(true);
                        if (model.MeleeWeaponSocket && !gun) gun = model.MeleeWeaponSocket.GetComponentInChildren<ItemAgent_Gun>(true);
                    }
                    if (!gun) gun = cmc ? (cmc.CurrentHoldItemAgent as ItemAgent_Gun) : null;

                    if (gun && gun.muzzle && !muzzleTf) muzzleTf = gun.muzzle;

                    if (!string.IsNullOrEmpty(shooterId) && gun)
                    {
                        _gunCacheByShooter[shooterId] = (gun, muzzleTf);
                    }
                }

                // 4) muzzle / 
                GameObject tmp = null;
                if (!muzzleTf)
                {
                    tmp = new GameObject("TempMuzzleFX");
                    tmp.transform.position = muzzlePos;
                    tmp.transform.rotation = Quaternion.LookRotation(finalDir, Vector3.up);
                    muzzleTf = tmp.transform;
                }

                // 5) + + gun==null 
                Client_PlayLocalShotFx(gun, muzzleTf, weaponType);

                if (tmp) Destroy(tmp, 0.2f);

                // 6) Host 
                if (!IsServer && shooterGo)
                {
                    var anim = shooterGo.GetComponentInChildren<CharacterAnimationControl_MagicBlend>(true);
                    if (anim && anim.animator) anim.OnAttack();
                }
            }
            catch
            {
                // Network 
            }
        }



        private void TryStartVisualRecoil(ItemAgent_Gun gun)
        {
            if (!gun) return;
            try
            {
                Traverse.Create(gun).Method("StartVisualRecoil").GetValue();
                return;
            }
            catch { }

            try
            {
                // StartVisualRecoil() _recoilBack=true
                Traverse.Create(gun).Field<bool>("_recoilBack").Value = true;
            }
            catch { }
        }


        private void Host_OnMainCharacterShoot(ItemAgent_Gun gun)
        {
            if (!networkStarted || !IsServer) return;
            if (gun == null || gun.Holder == null || !gun.Holder.IsMainCharacter) return;

            var proj = Traverse.Create(gun).Field<Projectile>("projInst").Value;
            if (proj == null) return;

            Vector3 finalDir = proj.transform.forward;
            if (finalDir.sqrMagnitude < 1e-8f) finalDir = (gun.muzzle ? gun.muzzle.forward : Vector3.forward);
            finalDir.Normalize();

            Vector3 muzzleWorld = proj.transform.position;
            float speed = gun.BulletSpeed * (gun.Holder ? gun.Holder.GunBulletSpeedMultiplier : 1f);
            float distance = gun.BulletDistance + 0.4f;

            var w = writer;
            w.Reset();
            w.Put((byte)Op.FIRE_EVENT);
            w.Put(localPlayerStatus.EndPoint);
            w.Put(gun.Item.TypeID);
            w.PutV3cm(muzzleWorld);
            w.PutDir(finalDir);
            w.Put(speed);
            w.Put(distance);

            var payloadCtx = new ProjectileContext();

            bool hasBulletItem = false;
            try { hasBulletItem = (gun.BulletItem != null); } catch { }

            float charMul = 1f, bulletMul = 1f;
            int shots = 1;
            try
            {
                charMul = gun.CharacterDamageMultiplier;
                bulletMul = hasBulletItem ? Mathf.Max(0.0001f, gun.BulletDamageMultiplier) : 1f;
                shots = Mathf.Max(1, gun.ShotCount);
            }
            catch { }

            try
            {
                payloadCtx.damage = gun.Damage * bulletMul * charMul / shots;
                if (gun.Damage > 1f && payloadCtx.damage < 1f) payloadCtx.damage = 1f;
            }
            catch { if (payloadCtx.damage <= 0f) payloadCtx.damage = 1f; }

            try
            {
                float bulletCritRateGain = hasBulletItem ? gun.bulletCritRateGain : 0f;
                float bulletCritDmgGain = hasBulletItem ? gun.BulletCritDamageFactorGain : 0f;
                payloadCtx.critDamageFactor = (gun.CritDamageFactor + bulletCritDmgGain) * (1f + gun.CharacterGunCritDamageGain);
                payloadCtx.critRate = gun.CritRate * (1f + gun.CharacterGunCritRateGain + bulletCritRateGain);
            }
            catch { }

            try
            {
                float apGain = hasBulletItem ? gun.BulletArmorPiercingGain : 0f;
                float abGain = hasBulletItem ? gun.BulletArmorBreakGain : 0f;
                payloadCtx.armorPiercing = gun.ArmorPiercing + apGain;
                payloadCtx.armorBreak = gun.ArmorBreak + abGain;
            }
            catch { }

            try
            {
                var setting = gun.GunItemSetting;
                if (setting != null)
                {
                    switch (setting.element)
                    {
                        case ElementTypes.physics: payloadCtx.element_Physics = 1f; break;
                        case ElementTypes.fire: payloadCtx.element_Fire = 1f; break;
                        case ElementTypes.poison: payloadCtx.element_Poison = 1f; break;
                        case ElementTypes.electricity: payloadCtx.element_Electricity = 1f; break;
                        case ElementTypes.space: payloadCtx.element_Space = 1f; break;
                    }
                }

                payloadCtx.explosionRange = gun.BulletExplosionRange;
                payloadCtx.explosionDamage = gun.BulletExplosionDamage * gun.ExplosionDamageMultiplier;

                if (hasBulletItem)
                {
                    payloadCtx.buffChance = gun.BulletBuffChanceMultiplier * gun.BuffChance;
                    payloadCtx.bleedChance = gun.BulletBleedChance;
                }

                payloadCtx.penetrate = gun.Penetrate;
                payloadCtx.fromWeaponItemID = gun.Item.TypeID;
            }
            catch { }

            w.PutProjectilePayload(payloadCtx);
            TransportBroadcast(w, true);

            PlayMuzzleFxAndShell(localPlayerStatus.EndPoint, gun.Item.TypeID, muzzleWorld, finalDir);
        }

        // Host clientScatter gun.CurrentScatter 
        bool Server_SpawnProjectile(ItemAgent_Gun gun, Vector3 muzzle, Vector3 baseDir, Vector3 firstCheckStart, out Vector3 finalDir, float clientScatter, float ads01)
        {
            finalDir = baseDir.sqrMagnitude < 1e-8f ? Vector3.forward : baseDir.normalized;

            // ====== Host Client ======
            bool isMain = (gun.Holder && gun.Holder.IsMainCharacter);
            float extra = 0f;
            if (isMain)
            {
                // 
                extra = Mathf.Max(1f, gun.CurrentScatter) * Mathf.Lerp(1.5f, 0f, Mathf.InverseLerp(0f, 0.5f, gun.durabilityPercent));
            }

            // Client ADS CurrentScatter 
            float usedScatter = (clientScatter > 0f ? clientScatter : gun.CurrentScatter);

            // 
            float yaw = UnityEngine.Random.Range(-0.5f, 0.5f) * (usedScatter + extra);
            finalDir = (Quaternion.Euler(0f, yaw, 0f) * finalDir).normalized;

            // ====== Projectile ======
            var projectile = (gun.GunItemSetting && gun.GunItemSetting.bulletPfb)
                ? gun.GunItemSetting.bulletPfb
                : Duckov.Utilities.GameplayDataSettings.Prefabs.DefaultBullet;

            var projInst = LevelManager.Instance.BulletPool.GetABullet(projectile);
            projInst.transform.position = muzzle;
            if (finalDir.sqrMagnitude < 1e-8f) finalDir = Vector3.forward;
            projInst.transform.rotation = Quaternion.LookRotation(finalDir, Vector3.up);

            // ====== Holder/ ======
            float characterDamageMultiplier = (gun.Holder != null) ? gun.CharacterDamageMultiplier : 1f;
            float gunBulletSpeedMul = (gun.Holder != null) ? gun.Holder.GunBulletSpeedMultiplier : 1f;

            bool hasBulletItem = (gun.BulletItem != null);
            float bulletDamageMul = hasBulletItem ? gun.BulletDamageMultiplier : 1f;
            float bulletCritRateGain = hasBulletItem ? gun.bulletCritRateGain : 0f;
            float bulletCritDmgGain = hasBulletItem ? gun.BulletCritDamageFactorGain : 0f;
            float bulletArmorPiercingGain = hasBulletItem ? gun.BulletArmorPiercingGain : 0f;
            float bulletArmorBreakGain = hasBulletItem ? gun.BulletArmorBreakGain : 0f;
            float bulletExplosionRange = hasBulletItem ? gun.BulletExplosionRange : 0f;
            float bulletExplosionDamage = hasBulletItem ? gun.BulletExplosionDamage : 0f;
            float bulletBuffChanceMul = hasBulletItem ? gun.BulletBuffChanceMultiplier : 0f;
            float bulletBleedChance = hasBulletItem ? gun.BulletBleedChance : 0f;

            // === BulletItem Client / ===
            try
            {
                if (bulletExplosionRange <= 0f)
                {
                    if (_hasPayloadHint && _payloadHint.fromWeaponItemID == gun.Item.TypeID && _payloadHint.explosionRange > 0f)
                        bulletExplosionRange = _payloadHint.explosionRange;
                    else if (_explRangeCacheByWeaponType.TryGetValue(gun.Item.TypeID, out var cachedR))
                        bulletExplosionRange = cachedR;
                }
                if (bulletExplosionDamage <= 0f)
                {
                    if (_hasPayloadHint && _payloadHint.fromWeaponItemID == gun.Item.TypeID && _payloadHint.explosionDamage > 0f)
                        bulletExplosionDamage = _payloadHint.explosionDamage;
                    else if (_explDamageCacheByWeaponType.TryGetValue(gun.Item.TypeID, out var cachedD))
                        bulletExplosionDamage = cachedD;
                }
                if (bulletExplosionRange > 0f) _explRangeCacheByWeaponType[gun.Item.TypeID] = bulletExplosionRange;
                if (bulletExplosionDamage > 0f) _explDamageCacheByWeaponType[gun.Item.TypeID] = bulletExplosionDamage;
            }
            catch { }

            var ctx = new ProjectileContext
            {
                firstFrameCheck = true,
                firstFrameCheckStartPoint = firstCheckStart,
                direction = finalDir,
                speed = gun.BulletSpeed * gunBulletSpeedMul,
                distance = gun.BulletDistance + 0.4f,
                halfDamageDistance = (gun.BulletDistance + 0.4f) * 0.5f,
                critDamageFactor = (gun.CritDamageFactor + bulletCritDmgGain) * (1f + gun.CharacterGunCritDamageGain),
                critRate = gun.CritRate * (1f + gun.CharacterGunCritRateGain + bulletCritRateGain),
                armorPiercing = gun.ArmorPiercing + bulletArmorPiercingGain,
                armorBreak = gun.ArmorBreak + bulletArmorBreakGain,
                explosionRange = bulletExplosionRange,
                explosionDamage = bulletExplosionDamage * gun.ExplosionDamageMultiplier,
                bleedChance = bulletBleedChance,
                fromWeaponItemID = gun.Item.TypeID
            };

            // ShotCount 
            int perShotDiv = Mathf.Max(1, gun.ShotCount);
            ctx.damage = gun.Damage * bulletDamageMul * characterDamageMultiplier / perShotDiv;
            if (gun.Damage > 1f && ctx.damage < 1f) ctx.damage = 1f;

            // 
            switch (gun.GunItemSetting.element)
            {
                case ElementTypes.physics: ctx.element_Physics = 1f; break;
                case ElementTypes.fire: ctx.element_Fire = 1f; break;
                case ElementTypes.poison: ctx.element_Poison = 1f; break;
                case ElementTypes.electricity: ctx.element_Electricity = 1f; break;
                case ElementTypes.space: ctx.element_Space = 1f; break;
            }

            if (bulletBuffChanceMul > 0f)
            {
                ctx.buffChance = bulletBuffChanceMul * gun.BuffChance;
            }

            // fromCharacter / team 
            if (gun.Holder)
            {
                ctx.fromCharacter = gun.Holder;
                ctx.team = gun.Holder.Team;
                if (gun.Holder.HasNearByHalfObsticle()) ctx.ignoreHalfObsticle = true;
            }
            else
            {
                var hostChar = LevelManager.Instance?.MainCharacter;
                if (hostChar != null)
                {
                    ctx.team = hostChar.Team;
                    ctx.fromCharacter = hostChar;
                }
            }
            if (ctx.critRate > 0.99f) ctx.ignoreHalfObsticle = true;

            projInst.Init(ctx);
            _serverSpawnedFromClient.Add(projInst);
            return true;
        }



        private void HandleFireEvent(NetPacketReader r)
        {
            // Host 
            string shooterId = r.GetString();
            int weaponType = r.GetInt();
            Vector3 muzzle = r.GetV3cm();     
            Vector3 dir = r.GetDir();     
            float speed = r.GetFloat();
            float distance = r.GetFloat();

            // / 
            CharacterMainControl shooterCMC = null;
            if (IsSelfId(shooterId)) shooterCMC = CharacterMainControl.Main;
            else if (clientRemoteCharacters.TryGetValue(shooterId, out var shooterGo) && shooterGo)
                shooterCMC = shooterGo.GetComponent<CharacterMainControl>();

            ItemAgent_Gun gun = null; Transform muzzleTf = null;
            if (shooterCMC && shooterCMC.characterModel)
            {
                gun = shooterCMC.GetGun();
                var model = shooterCMC.characterModel;
                if (!gun && model.RightHandSocket) gun = model.RightHandSocket.GetComponentInChildren<ItemAgent_Gun>(true);
                if (!gun && model.LefthandSocket) gun = model.LefthandSocket.GetComponentInChildren<ItemAgent_Gun>(true);
                if (!gun && model.MeleeWeaponSocket) gun = model.MeleeWeaponSocket.GetComponentInChildren<ItemAgent_Gun>(true);
                if (gun) muzzleTf = gun.muzzle;
            }

            // Network muzzle Failed / 
            Vector3 spawnPos = muzzleTf ? muzzleTf.position : muzzle;

            // Host ctx explosionRange / explosionDamage 
            var ctx = new ProjectileContext
            {
                direction = dir,
                speed = speed,
                distance = distance,
                halfDamageDistance = distance * 0.5f,
                firstFrameCheck = true,
                firstFrameCheckStartPoint = muzzle,
                team = (shooterCMC && shooterCMC) ? shooterCMC.Team :
                       (LevelManager.Instance?.MainCharacter ? LevelManager.Instance.MainCharacter.Team : Teams.player)
            };

            bool gotPayload = (r.AvailableBytes > 0) && NetPack_Projectile.TryGetProjectilePayload(r, ref ctx);

            // / 
            if (!gotPayload && gun != null)
            {
                bool hasBulletItem = false;
                try { hasBulletItem = (gun.BulletItem != null); } catch { }

                // 
                try
                {
                    float charMul = Mathf.Max(0.0001f, gun.CharacterDamageMultiplier);
                    float bulletMul = hasBulletItem ? Mathf.Max(0.0001f, gun.BulletDamageMultiplier) : 1f;
                    int shots = Mathf.Max(1, gun.ShotCount);
                    ctx.damage = gun.Damage * bulletMul * charMul / shots;
                    if (gun.Damage > 1f && ctx.damage < 1f) ctx.damage = 1f;
                }
                catch { if (ctx.damage <= 0f) ctx.damage = 1f; }

                // 
                try
                {
                    ctx.critDamageFactor = (gun.CritDamageFactor + gun.BulletCritDamageFactorGain) * (1f + gun.CharacterGunCritDamageGain);
                    ctx.critRate = gun.CritRate * (1f + gun.CharacterGunCritRateGain + gun.bulletCritRateGain);
                }
                catch { }

                // 
                try
                {
                    float apGain = hasBulletItem ? gun.BulletArmorPiercingGain : 0f;
                    float abGain = hasBulletItem ? gun.BulletArmorBreakGain : 0f;
                    ctx.armorPiercing = gun.ArmorPiercing + apGain;
                    ctx.armorBreak = gun.ArmorBreak + abGain;
                }
                catch { }

                // 
                try
                {
                    var setting = gun.GunItemSetting;
                    if (setting != null)
                    {
                        switch (setting.element)
                        {
                            case ElementTypes.physics: ctx.element_Physics = 1f; break;
                            case ElementTypes.fire: ctx.element_Fire = 1f; break;
                            case ElementTypes.poison: ctx.element_Poison = 1f; break;
                            case ElementTypes.electricity: ctx.element_Electricity = 1f; break;
                            case ElementTypes.space: ctx.element_Space = 1f; break;
                        }
                    }
                }
                catch { }

                // Status / / 
                try
                {
                    if (hasBulletItem)
                    {
                        ctx.buffChance = gun.BulletBuffChanceMultiplier * gun.BuffChance;
                        ctx.bleedChance = gun.BulletBleedChance;
                    }
                    ctx.explosionRange = gun.BulletExplosionRange;                                // !!!! RPG 
                    ctx.explosionDamage = gun.BulletExplosionDamage * gun.ExplosionDamageMultiplier;
                    ctx.penetrate = gun.Penetrate;

                    if (ctx.fromWeaponItemID == 0 && gun.Item != null)
                        ctx.fromWeaponItemID = gun.Item.TypeID;
                }
                catch
                {
                    if (ctx.fromWeaponItemID == 0) ctx.fromWeaponItemID = weaponType;
                }

                if (ctx.halfDamageDistance <= 0f) ctx.halfDamageDistance = ctx.distance * 0.5f;

                try
                {
                    if (gun.Holder && gun.Holder.HasNearByHalfObsticle()) ctx.ignoreHalfObsticle = true;
                    if (ctx.critRate > 0.99f) ctx.ignoreHalfObsticle = true;
                }
                catch { }
            }

            if (gotPayload && ctx.explosionRange <= 0f && gun != null)
            {
                try
                {
                    ctx.explosionRange = gun.BulletExplosionRange;
                    ctx.explosionDamage = gun.BulletExplosionDamage * gun.ExplosionDamageMultiplier;
                }
                catch { }
            }

            // Client Projectile ctx.explosionRange>0 
            Projectile pfb = null;
            try { if (gun && gun.GunItemSetting && gun.GunItemSetting.bulletPfb) pfb = gun.GunItemSetting.bulletPfb; } catch { }
            if (!pfb) pfb = Duckov.Utilities.GameplayDataSettings.Prefabs.DefaultBullet;
            if (!pfb) return;

            var proj = LevelManager.Instance.BulletPool.GetABullet(pfb);
            proj.transform.position = spawnPos;
            proj.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
            proj.Init(ctx);

            PlayMuzzleFxAndShell(shooterId, weaponType, spawnPos, dir);
            TryPlayShootAnim(shooterId);
        }







        private void TryPlayShootAnim(string shooterId)
        {
            // shooterId Host 
            if (IsSelfId(shooterId)) return;

            if (!clientRemoteCharacters.TryGetValue(shooterId, out var shooterGo) || !shooterGo) return;

            var animCtrl = shooterGo.GetComponent<CharacterAnimationControl_MagicBlend>();
            if (animCtrl && animCtrl.animator)
            {
                animCtrl.OnAttack();
            }
        }



        private bool TryGetProjectilePrefab(int weaponTypeId, out Projectile pfb)
         => _projCacheByWeaponType.TryGetValue(weaponTypeId, out pfb);


        public void BroadcastReliable(NetDataWriter w)
        {
            if (!IsServer || netManager == null) return;
            TransportBroadcast(w, true);
        }

        public void SendReliable(NetDataWriter w)
        {
            if (transport != null) transport.Broadcast(w.CopyData(), true);
            else { if (IsServer) netManager?.SendToAll(w, DeliveryMethod.ReliableOrdered); else TransportSendToServer(w, true); }
        }

        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            HandleNetworkReceive(peer, reader);
        }

        private void HandleNetworkReceive(NetPeer peer, NetPacketReader reader)
        {
            if (reader.AvailableBytes <= 0) { if (reader is NetPacketReader npr) npr.Recycle(); return; }
            var op = (Op)reader.GetByte();
            switch (op)
            {
                // ===== Host -> Client Player Status =====
                case Op.PLAYER_STATUS_UPDATE:
                    if (!IsServer)
                    {
                        int playerCount = reader.GetInt();
                        clientPlayerStatuses.Clear();

                        for (int i = 0; i < playerCount; i++)
                        {
                            string endPoint = reader.GetString();
                            string playerName = reader.GetString();
                            int latency = reader.GetInt();
                            bool isInGame = reader.GetBool();
                            Vector3 position = reader.GetVector3();        
                            Quaternion rotation = reader.GetQuaternion();

                            string sceneId = reader.GetString();
                            string customFaceJson = reader.GetString();

                            int equipmentCount = reader.GetInt();
                            var equipmentList = new List<EquipmentSyncData>();
                            for (int j = 0; j < equipmentCount; j++)
                                equipmentList.Add(EquipmentSyncData.Deserialize(reader));

                            int weaponCount = reader.GetInt();
                            var weaponList = new List<WeaponSyncData>();
                            for (int j = 0; j < weaponCount; j++)
                                weaponList.Add(WeaponSyncData.Deserialize(reader));

                            if (IsSelfId(endPoint)) continue;

                            if (!clientPlayerStatuses.TryGetValue(endPoint, out var pst))
                                pst = clientPlayerStatuses[endPoint] = new PlayerStatus();

                            pst.EndPoint = endPoint;
                            pst.PlayerName = playerName;
                            pst.Latency = latency;
                            pst.IsInGame = isInGame;
                            pst.LastIsInGame = isInGame;
                            pst.Position = position;
                            pst.Rotation = rotation;
                            if (!string.IsNullOrEmpty(customFaceJson))
                                pst.CustomFaceJson = customFaceJson;
                            pst.EquipmentList = equipmentList;
                            pst.WeaponList = weaponList;

                            if (!string.IsNullOrEmpty(sceneId))
                            {
                                pst.SceneId = sceneId;
                                _cliLastSceneIdByPlayer[endPoint] = sceneId; // A 
                            }

                            if (clientRemoteCharacters.TryGetValue(pst.EndPoint, out var existing) && existing != null)
                                Client_ApplyFaceIfAvailable(pst.EndPoint, existing, pst.CustomFaceJson);

                            if (isInGame)
                            {
                                if (!clientRemoteCharacters.ContainsKey(endPoint) || clientRemoteCharacters[endPoint] == null)
                                {
                                    CreateRemoteCharacterForClient(endPoint, position, rotation, customFaceJson).Forget();
                                }
                                else
                                {
                                    var go = clientRemoteCharacters[endPoint];
                                    var ni = NetInterpUtil.Attach(go);
                                    ni?.Push(pst.Position, pst.Rotation);
                                }

                                foreach (var e in equipmentList) ApplyEquipmentUpdate_Client(endPoint, e.SlotHash, e.ItemId).Forget();
                                foreach (var w in weaponList) ApplyWeaponUpdate_Client(endPoint, w.SlotHash, w.ItemId).Forget();
                            }
                        }
                    }
                    break;

                // ===== Client -> Host Status =====
                case Op.CLIENT_STATUS_UPDATE:
                    if (IsServer)
                    {
                        HandleClientStatusUpdate(peer, reader);
                    }
                    break;

                // ===== =====
                case Op.POSITION_UPDATE:
                    if (IsServer)
                    {
                        string endPointC = reader.GetString();
                        Vector3 posS = reader.GetV3cm();   // GetVector3()
                        Vector3 dirS = reader.GetDir();
                        Quaternion rotS = Quaternion.LookRotation(dirS, Vector3.up);

                        HandlePositionUpdate_Q(peer, endPointC, posS, rotS);
                    }
                    else
                    {
                        string endPointS = reader.GetString();
                        Vector3 posS = reader.GetV3cm();   // GetVector3()
                        Vector3 dirS = reader.GetDir();
                        Quaternion rotS = Quaternion.LookRotation(dirS, Vector3.up);

                        if (IsSelfId(endPointS)) break;

                        // 
                        if (float.IsNaN(posS.x) || float.IsNaN(posS.y) || float.IsNaN(posS.z) ||
                            float.IsInfinity(posS.x) || float.IsInfinity(posS.y) || float.IsInfinity(posS.z))
                            break;

                        if (!clientPlayerStatuses.TryGetValue(endPointS, out var pst))
                            pst = clientPlayerStatuses[endPointS] = new PlayerStatus { EndPoint = endPointS, IsInGame = true };

                        pst.Position = posS;
                        pst.Rotation = rotS;

                        if (clientRemoteCharacters.TryGetValue(endPointS, out var go) && go != null)
                        {
                            var ni = NetInterpUtil.Attach(go);
                            ni?.Push(pst.Position, pst.Rotation);   // 

                            var cmc = go.GetComponentInChildren<CharacterMainControl>(true);
                            if (cmc && cmc.modelRoot)
                            {
                                var e = pst.Rotation.eulerAngles;
                                cmc.modelRoot.transform.rotation = Quaternion.Euler(0f, e.y, 0f);
                            }
                        }
                        else
                        {
                            CreateRemoteCharacterForClient(endPointS, posS, rotS, pst.CustomFaceJson).Forget();
                        }
                    }
                    break;

                // 
                case Op.ANIM_SYNC:
                    if (IsServer)
                    {
                        // Client -> Host
                        HandleClientAnimationStatus(peer, reader);
                    }
                    else
                    {
                        // Host -> Client playerId 
                        string playerId = reader.GetString();
                        if (IsSelfId(playerId)) break;

                        float moveSpeed = reader.GetFloat();
                        float moveDirX = reader.GetFloat();
                        float moveDirY = reader.GetFloat();
                        bool isDashing = reader.GetBool();
                        bool isAttacking = reader.GetBool();
                        int handState = reader.GetInt();
                        bool gunReady = reader.GetBool();
                        int stateHash = reader.GetInt();
                        float normTime = reader.GetFloat();

                        if (clientRemoteCharacters.TryGetValue(playerId, out var obj) && obj != null)
                        {
                            var ai = AnimInterpUtil.Attach(obj);
                            ai?.Push(new AnimSample
                            {
                                speed = moveSpeed,
                                dirX = moveDirX,
                                dirY = moveDirY,
                                dashing = isDashing,
                                attack = isAttacking,
                                hand = handState,
                                gunReady = gunReady,
                                stateHash = stateHash,
                                normTime = normTime
                            });
                        }

                    }
                    break;

                // ===== =====
                case Op.EQUIPMENT_UPDATE:
                    if (IsServer)
                    {
                        HandleEquipmentUpdate(peer, reader);
                    }
                    else
                    {
                        string endPoint = reader.GetString();
                        if (IsSelfId(endPoint)) break;
                        int slotHash = reader.GetInt();
                        string itemId = reader.GetString();
                        ApplyEquipmentUpdate_Client(endPoint, slotHash, itemId).Forget();
                    }
                    break;

                // ===== =====
                case Op.PLAYERWEAPON_UPDATE:
                    if (IsServer)
                    {
                        HandleWeaponUpdate(peer, reader);
                    }
                    else
                    {
                        string endPoint = reader.GetString();
                        if (IsSelfId(endPoint)) break;
                        int slotHash = reader.GetInt();
                        string itemId = reader.GetString();
                        ApplyWeaponUpdate_Client(endPoint, slotHash, itemId).Forget();
                    }
                    break;

                case Op.FIRE_REQUEST:
                    if (IsServer)
                    {
                        HandleFireRequest(peer, reader);
                    }
                    break;

                case Op.FIRE_EVENT:
                    if (!IsServer)
                    {
                        //Debug.Log("[RECV FIRE_EVENT] opcode path");
                        HandleFireEvent(reader);
                    }
                    break;

                default:
                    // opcode 
                    Debug.LogWarning($"Unknown opcode: {(byte)op}");
                    break;

                case Op.GRENADE_THROW_REQUEST:
                    if (IsServer) HandleGrenadeThrowRequest(peer, reader);
                    break;
                case Op.GRENADE_SPAWN:
                    if (!IsServer) HandleGrenadeSpawn(reader);
                    break;
                case Op.GRENADE_EXPLODE:
                    if (!IsServer) HandleGrenadeExplode(reader);
                    break;

                //case Op.DISCOVER_REQUEST:
                // if (IsServer) HandleDiscoverRequest(peer, reader);
                // break;
                //case Op.DISCOVER_RESPONSE:
                // if (!IsServer) HandleDiscoverResponse(peer, reader);
                // break;
                case Op.ITEM_DROP_REQUEST:
                    if (IsServer) HandleItemDropRequest(peer, reader);
                    break;

                case Op.ITEM_SPAWN:
                    if (!IsServer) HandleItemSpawn(reader);
                    break;
                case Op.ITEM_PICKUP_REQUEST:
                    if (IsServer) HandleItemPickupRequest(peer, reader);
                    break;
                case Op.ITEM_DESPAWN:
                    if (!IsServer) HandleItemDespawn(reader);
                    break;

                case Op.MELEE_ATTACK_REQUEST:
                    if (IsServer) HandleMeleeAttackRequest(peer, reader);
                    break;
                case Op.MELEE_ATTACK_SWING:
                    {
                        if (!IsServer)
                        {
                            string shooter = reader.GetString();
                            float delay = reader.GetFloat(); 

                            // Player 
                            if (!IsSelfId(shooter) && clientRemoteCharacters.TryGetValue(shooter, out var who) && who)
                            {
                                var anim = who.GetComponentInChildren<CharacterAnimationControl_MagicBlend>(true);
                                if (anim != null) anim.OnAttack();

                                var cmc = who.GetComponent<CharacterMainControl>();
                                var model = cmc ? cmc.characterModel : null;
                                if (model) DuckovCoopMod.MeleeFx.SpawnSlashFx(model);
                            }
                            // AI:xxx
                            else if (shooter.StartsWith("AI:"))
                            {
                                if (int.TryParse(shooter.Substring(3), out var aiId) && aiById.TryGetValue(aiId, out var cmc) && cmc)
                                {
                                    var anim = cmc.GetComponentInChildren<CharacterAnimationControl_MagicBlend>(true);
                                    if (anim != null) anim.OnAttack();

                                    var model = cmc.characterModel;
                                    if (model) DuckovCoopMod.MeleeFx.SpawnSlashFx(model);
                                }
                            }
                        }
                        break;
                    }

                case Op.MELEE_HIT_REPORT:
                    if (IsServer) HandleMeleeHitReport(peer, reader);
                    break;

                case Op.ENV_HURT_REQUEST:
                    if (IsServer) Server_HandleEnvHurtRequest(peer, reader);
                    break;
                case Op.ENV_HURT_EVENT:
                    if (!IsServer) Client_ApplyDestructibleHurt(reader);
                    break;
                case Op.ENV_DEAD_EVENT:
                    if (!IsServer) Client_ApplyDestructibleDead(reader);
                    break;

                case Op.PLAYER_HEALTH_REPORT:
                    {
                        if (IsServer)
                        {
                            float max = reader.GetFloat();
                            float cur = reader.GetFloat();
                            if (max <= 0f)
                            {
                                _srvPendingHp[peer] = (max, cur);
                                break;
                            }
                            if (remoteCharacters != null && remoteCharacters.TryGetValue(peer, out var go) && go)
                            {
                                // Host 
                                ApplyHealthAndEnsureBar(go, max, cur);

                                // + Client
                                var h = go.GetComponentInChildren<Health>(true);
                                if (h) Server_OnHealthChanged(peer, h);
                            }
                            else
                            {
                                // Health 
                                _srvPendingHp[peer] = (max, cur);
                            }
                        }
                        break;
                    }


                case Op.AUTH_HEALTH_SELF:
                    {
                        float max = reader.GetFloat();
                        float cur = reader.GetFloat();

                        if (max <= 0f)
                        {
                            _cliSelfHpMax = max; _cliSelfHpCur = cur;
                            _cliSelfHpPending = true;
                            break;
                        }

                        // --- ---
                        bool shouldApply = true;
                        try
                        {
                            var main = CharacterMainControl.Main;
                            var selfH = main ? main.Health : null;
                            if (selfH)
                            {
                                float localCur = selfH.CurrentHealth;
                                // 
                                if (Time.time - _cliLastSelfHurtAt <= SELF_ACCEPT_WINDOW)
                                {
                                    // echo 
                                    if (cur > localCur + 0.0001f)
                                    {

                                       UnityEngine.Debug.Log($"[HP][SelfEcho] drop stale echo in window: local={localCur:F3} srv={cur:F3}");

                                        shouldApply = false;
                                    }
                                }
                            }
                        }
                        catch { }

                        _cliApplyingSelfSnap = true;
                        _cliEchoMuteUntil = Time.time + SELF_MUTE_SEC;
                        try
                        {
                            if (shouldApply)
                            {
                                if (_cliSelfHpPending)
                                {
                                    _cliSelfHpMax = max; _cliSelfHpCur = cur;
                                    Client_ApplyPendingSelfIfReady();
                                }
                                else
                                {
                                    var main = CharacterMainControl.Main;
                                    var go = main ? main.gameObject : null;
                                    if (go)
                                    {
                                        var h = main.Health;
                                        var cmc = main;
                                        if (h)
                                        {
                                            try { h.autoInit = false; } catch { }
                                            BindHealthToCharacter(h, cmc);
                                            ForceSetHealth(h, max, cur, ensureBar: true);
                                        }
                                    }
                                    _cliSelfHpPending = false;
                                }
                            }
                            else
                            {
                                // 
                            }
                        }
                        finally
                        {
                            _cliApplyingSelfSnap = false;
                        }
                        break;
                    }

                case Op.AUTH_HEALTH_REMOTE:
                    {
                        if (!IsServer)
                        {
                            string playerId = reader.GetString();
                            float max = reader.GetFloat();
                            float cur = reader.GetFloat();

                            // 0/0 
                            if (max <= 0f)
                            {
                                _cliPendingRemoteHp[playerId] = (max, cur);
                                break;
                            }

                            if (clientRemoteCharacters != null && clientRemoteCharacters.TryGetValue(playerId, out var go) && go)
                                ApplyHealthAndEnsureBar(go, max, cur);
                            else
                                _cliPendingRemoteHp[playerId] = (max, cur);
                        }
                        break;
                    }

                case Op.PLAYER_BUFF_SELF_APPLY:
                    if (!IsServer) HandlePlayerBuffSelfApply(reader);
                    break;
                case Op.HOST_BUFF_PROXY_APPLY:
                    if (!IsServer) HandleBuffProxyApply(reader);
                    break;


                case Op.SCENE_VOTE_START:
                    {
                        if (!IsServer)
                        {
                            Client_OnSceneVoteStart(reader);
                            // 
                            if (_spectatorActive) _spectatorEndOnVotePending = true;
                        }
                        break;
                    }

                case Op.SCENE_VOTE_REQ:
                    {
                        if (IsServer)
                        {
                            string targetId = reader.GetString();
                            byte flags = reader.GetByte();
                            bool hasCurtain, useLoc, notifyEvac, saveToFile;
                            UnpackFlags(flags, out hasCurtain, out useLoc, out notifyEvac, out saveToFile);

                            string curtainGuid = null;
                            if (hasCurtain) TryGetString(reader, out curtainGuid);
                            if (!TryGetString(reader, out var locName)) locName = string.Empty;

                            // Host 
                            if (_spectatorActive) _spectatorEndOnVotePending = true;

                            Host_BeginSceneVote_Simple(targetId, curtainGuid, notifyEvac, saveToFile, useLoc, locName);
                        }
                        break;
                    }



                case Op.SCENE_READY_SET:
                    {
                        if (IsServer)
                        {
                            bool ready = reader.GetBool();
                            Server_OnSceneReadySet(peer, ready);
                        }
                        else
                        {
                            string pid = reader.GetString();
                            bool rdy = reader.GetBool();

                            if (!sceneReady.ContainsKey(pid) && sceneParticipantIds.Contains(pid))
                                sceneReady[pid] = false;

                            if (sceneReady.ContainsKey(pid))
                            {
                                sceneReady[pid] = rdy;
                                Debug.Log($"[SCENE] READY_SET -> {pid} = {rdy}");
                            }
                            else
                            {
                                Debug.LogWarning($"[SCENE] READY_SET for unknown pid '{pid}'. participants=[{string.Join(",", sceneParticipantIds)}]");
                            }
                        }
                        break;
                    }

                case Op.SCENE_BEGIN_LOAD:
                    {
                        if (!IsServer)
                        {
                            // Player 
                            if (_spectatorActive && _spectatorEndOnVotePending)
                            {
                                _spectatorEndOnVotePending = false;
                                sceneVoteActive = false;
                                sceneReady.Clear();
                                localReady = false;

                                EndSpectatorAndShowClosure(); // 
                                break; // Client_OnBeginSceneLoad(reader)
                            }

                            // Player 
                            Client_OnBeginSceneLoad(reader);
                        }
                        break;
                    }

                case Op.SCENE_CANCEL:
                    {
                        sceneVoteActive = false;
                        sceneReady.Clear();
                        localReady = false;

                        if (_spectatorActive && _spectatorEndOnVotePending)
                        {

                            _spectatorEndOnVotePending = false;
                            EndSpectatorAndShowClosure();
                        }
                        break;
                    }


                case Op.SCENE_READY:
                    {
                        string id = reader.GetString();   // id EndPoint 
                        string sid = reader.GetString();  // SceneId string 
                        Vector3 pos = reader.GetVector3(); // 
                        Quaternion rot = reader.GetQuaternion();
                        string face = reader.GetString();

                        if (IsServer)
                        {
                            Server_HandleSceneReady(peer, id, sid, pos, rot, face);
                        }
                        // Client Host REMOTE_CREATE 
                        break;
                    }

                case Op.ENV_SYNC_REQUEST:
                    if (IsServer) Server_BroadcastEnvSync(peer);
                    break;

                case Op.ENV_SYNC_STATE:
                    {
                        // Client 
                        if (!IsServer)
                        {
                            long day = reader.GetLong();
                            double sec = reader.GetDouble();
                            float scale = reader.GetFloat();
                            int seed = reader.GetInt();
                            bool forceW = reader.GetBool();
                            int forceWVal = reader.GetInt();
                            int curWeather = reader.GetInt();
                            byte stormLv = reader.GetByte();

                            int lootCount = 0;
                            try { lootCount = reader.GetInt(); } catch { lootCount = 0; }
                            var vis = new Dictionary<int, bool>(lootCount);
                            for (int i = 0; i < lootCount; ++i)
                            {
                                int k = 0; bool on = false;
                                try { k = reader.GetInt(); } catch { }
                                try { on = reader.GetBool(); } catch { }
                                vis[k] = on;
                            }
                            Client_ApplyLootVisibility(vis);

                            // Host 0 
                            int doorCount = 0;
                            try { doorCount = reader.GetInt(); } catch { doorCount = 0; }
                            for (int i = 0; i < doorCount; ++i)
                            {
                                int dk = 0; bool cl = false;
                                try { dk = reader.GetInt(); } catch { }
                                try { cl = reader.GetBool(); } catch { }
                                Client_ApplyDoorState(dk, cl);
                            }

                            int deadCount = 0;
                            try { deadCount = reader.GetInt(); } catch { deadCount = 0; }
                            for (int i = 0; i < deadCount; ++i)
                            {
                                uint did = 0;
                                try { did = reader.GetUInt(); } catch { }
                                if (did != 0) Client_ApplyDestructibleDead_Snapshot(did);
                            }

                            Client_ApplyEnvSync(day, sec, scale, seed, forceW, forceWVal, curWeather, stormLv);
                        }
                        break;
                    }


                case Op.LOOT_REQ_OPEN:
                    {
                        if (IsServer) Server_HandleLootOpenRequest(peer, reader);
                        break;
                    }



                case Op.LOOT_STATE:
                    {
                        if (IsServer) break;
                        Client_ApplyLootboxState(reader);

                        break;
                    }
                case Op.LOOT_REQ_PUT:
                    {
                        if (!IsServer) break;
                        Server_HandleLootPutRequest(peer, reader);
                        break;
                    }
                case Op.LOOT_REQ_TAKE:
                    {
                        if (!IsServer) break;
                        Server_HandleLootTakeRequest(peer, reader);
                        break;
                    }
                case Op.LOOT_PUT_OK:
                    {
                        if (IsServer) break;
                        Client_OnLootPutOk(reader);
                        break;
                    }
                case Op.LOOT_TAKE_OK:
                    {
                        if (IsServer) break;
                        Client_OnLootTakeOk(reader);
                        break;
                    }

                case Op.LOOT_DENY:
                    {
                        if (IsServer) break;
                        string reason = reader.GetString();
                        Debug.LogWarning($"[LOOT] Request denied:{reason}");

                        // no_inv 
                        if (reason == "no_inv")
                            break;

                        // rm_fail/bad_snapshot 
                        var lv = Duckov.UI.LootView.Instance;
                        var inv = lv ? lv.TargetInventory : null;
                        if (inv) Client_RequestLootState(inv);
                        break;
                    }



                case Op.AI_SEED_SNAPSHOT:
                    {
                        if (!IsServer) HandleAiSeedSnapshot(reader);
                        break;
                    }
                case Op.AI_LOADOUT_SNAPSHOT:
                    {
                        byte ver = reader.GetByte();
                        int aiId = reader.GetInt();

                        int ne = reader.GetInt();
                        var equips = new List<(int slot, int tid)>(ne);
                        for (int i = 0; i < ne; ++i)
                        {
                            int sh = reader.GetInt();
                            int tid = reader.GetInt();
                            equips.Add((sh, tid));
                        }

                        int nw = reader.GetInt();
                        var weapons = new List<(int slot, int tid)>(nw);
                        for (int i = 0; i < nw; ++i)
                        {
                            int sh = reader.GetInt();
                            int tid = reader.GetInt();
                            weapons.Add((sh, tid));
                        }

                        bool hasFace = reader.GetBool();
                        string faceJson = hasFace ? reader.GetString() : null;

                        bool hasModelName = reader.GetBool();
                        string modelName = hasModelName ? reader.GetString() : null;

                        int iconType = reader.GetInt();

                        bool showName = false;
                        if (ver >= 4) showName = reader.GetBool();

                        string displayName = null;
                        if (ver >= 5)
                        {
                            bool hasName = reader.GetBool();
                            if (hasName) displayName = reader.GetString();
                        }

                        if (IsServer) break;

                        if (LogAiLoadoutDebug)
                            Debug.Log($"[AI-RECV] ver={ver} aiId={aiId} model='{modelName}' icon={iconType} showName={showName} faceLen={(faceJson != null ? faceJson.Length : 0)}");

                        if (aiById.TryGetValue(aiId, out var cmc) && cmc)
                            Client_ApplyAiLoadout(aiId, equips, weapons, faceJson, modelName, iconType, showName, displayName).Forget();
                        else
                            pendingAiLoadouts[aiId] = (equips, weapons, faceJson, modelName, iconType, showName, displayName);

                        break;
                    }

                case Op.AI_TRANSFORM_SNAPSHOT:
                    {
                        if (IsServer) break; 
                        int n = reader.GetInt();

                        if (!_aiSceneReady)
                        {
                            for (int i = 0; i < n; ++i)
                            {
                                int aiId = reader.GetInt();
                                Vector3 p = reader.GetV3cm();
                                Vector3 f = reader.GetDir();
                                if (_pendingAiTrans.Count < 512) _pendingAiTrans.Enqueue((aiId, p, f)); // Mr.Sans 
                            }
                            break;
                        }

                        for (int i = 0; i < n; i++)
                        {
                            int aiId = reader.GetInt();
                            Vector3 p = reader.GetV3cm();
                            Vector3 f = reader.GetDir();
                            ApplyAiTransform(aiId, p, f); // 
                        }
                        break;
                    }

                case Op.AI_ANIM_SNAPSHOT:
                    {
                        if (!IsServer)
                        {
                            int n = reader.GetInt();
                            for (int i = 0; i < n; ++i)
                            {
                                int id = reader.GetInt();
                                var st2 = new AiAnimState
                                {
                                    speed = reader.GetFloat(),
                                    dirX = reader.GetFloat(),
                                    dirY = reader.GetFloat(),
                                    hand = reader.GetInt(),
                                    gunReady = reader.GetBool(),
                                    dashing = reader.GetBool(),
                                };
                                if (!Client_ApplyAiAnim(id, st2))
                                    _pendingAiAnims[id] = st2;
                            }
                        }
                        break;
                    }

                case Op.AI_ATTACK_SWING:
                    {
                        if (!IsServer)
                        {
                            int id = reader.GetInt();
                            if (aiById.TryGetValue(id, out var cmc) && cmc)
                            {
                                var anim = cmc.GetComponent<CharacterAnimationControl_MagicBlend>();
                                if (anim != null) anim.OnAttack();
                                var model = cmc.characterModel;
                                if (model) MeleeFx.SpawnSlashFx(model);
                            }
                        }
                        break;
                    }

                case Op.AI_HEALTH_SYNC:
                    {
                        int id = reader.GetInt();

                        float max = 0f, cur = 0f;
                        if (reader.AvailableBytes >= 8)
                        {   
                            max = reader.GetFloat();
                            cur = reader.GetFloat();
                        }
                        else
                        {                            
                            cur = reader.GetFloat();
                        }

                        Client_ApplyAiHealth(id, max, cur);
                        break;
                    }


                // --- Client aiId ---
                case Op.DEAD_LOOT_SPAWN:
                    {
                        int scene = reader.GetInt();
                        int aiId = reader.GetInt();
                        int lootUid = reader.GetInt();                  
                        Vector3 pos = reader.GetV3cm();
                        Quaternion rot = reader.GetQuaternion();
                        if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex != scene) break;

                        SpawnDeadLootboxAt(aiId, lootUid, pos, rot);    
                        break;
                    }

             


                case Op.AI_NAME_ICON:
                    {
                        if (IsServer) break;

                        int aiId = reader.GetInt();
                        int iconType = reader.GetInt();
                        bool showName = reader.GetBool();
                        string displayName = null;
                        bool hasName = reader.GetBool();
                        if (hasName) displayName = reader.GetString();

                        if (aiById.TryGetValue(aiId, out var cmc) && cmc)
                        {
                            RefreshNameIconWithRetries(cmc, iconType, showName, displayName).Forget();
                        }
                        else
                        {
                            Debug.LogWarning($"[AI_icon_Name 10s] cmc is null!");
                        }
                        // cmc 10s 
                        break;
                    }

                case Op.PLAYER_DEAD_TREE:
                    {
                        if (!IsServer) break;
                        Vector3 pos = reader.GetV3cm();
                        Quaternion rot = reader.GetQuaternion();

                        var snap = ReadItemSnapshot(reader);        
                        var tmpRoot = BuildItemFromSnapshot(snap);  
                        if (!tmpRoot) { Debug.LogWarning("[LOOT] PLAYER_DEAD_TREE BuildItemFromSnapshot failed."); break; }

                        var deadPfb = ResolveDeadLootPrefabOnServer();
                        var box = InteractableLootbox.CreateFromItem(tmpRoot, pos + Vector3.up * 0.10f, rot, true, deadPfb, false);
                        if (box) Server_OnDeadLootboxSpawned(box, null);   // lootUid + aiId + LOOT_STATE

                        if (remoteCharacters.TryGetValue(peer, out var proxy) && proxy)
                        {
                            UnityEngine.Object.Destroy(proxy);
                            remoteCharacters.Remove(peer);
                        }

                        // B) Client Player 
                        if (playerStatuses.TryGetValue(peer, out var st) && !string.IsNullOrEmpty(st.EndPoint))
                        {
                            var w2 = writer; w2.Reset();
                            w2.Put((byte)Op.REMOTE_DESPAWN);
                            w2.Put(st.EndPoint);                 // Client EndPoint key
                            TransportBroadcast(w2, true);
                        }


                        if (tmpRoot && tmpRoot.gameObject) UnityEngine.Object.Destroy(tmpRoot.gameObject);
                        break;
                    }

                case Op.LOOT_REQ_SPLIT:
                    {
                        if (!IsServer) break;
                        Server_HandleLootSplitRequest(peer, reader);
                        break;
                    }

                case Op.REMOTE_DESPAWN:
                    {
                        if (IsServer) break;                 // Client 
                        string id = reader.GetString();
                        if (clientRemoteCharacters.TryGetValue(id, out var go) && go)
                            UnityEngine.Object.Destroy(go);
                        clientRemoteCharacters.Remove(id);
                        break;
                    }

                case Op.AI_SEED_PATCH:
                    HandleAiSeedPatch(reader);
                    break;

                case Op.DOOR_REQ_SET:
                    {
                        if (IsServer) Server_HandleDoorSetRequest(peer, reader);
                        break;
                    }
                case Op.DOOR_STATE:
                    {
                        if (!IsServer)
                        {
                            int k = reader.GetInt();
                            bool cl = reader.GetBool();
                            Client_ApplyDoorState(k, cl);
                        }
                        break;
                    }

                case Op.LOOT_REQ_SLOT_UNPLUG:
                    {
                        if (IsServer) Server_HandleLootSlotUnplugRequest(peer, reader);
                        break;
                    }
                case Op.LOOT_REQ_SLOT_PLUG:
                    {
                        if (IsServer) Server_HandleLootSlotPlugRequest(peer, reader);
                        break;
                    }


                case Op.SCENE_GATE_READY:
                    {
                        if (IsServer)
                        {
                            string pid = reader.GetString();
                            string sid = reader.GetString();

                            // Host gate sid READY sid
                            if (string.IsNullOrEmpty(_srvGateSid))
                                _srvGateSid = sid;

                            if (sid == _srvGateSid)
                            {
                                _srvGateReadyPids.Add(pid);

                            }
                        }
                        break;
                    }

                case Op.SCENE_GATE_RELEASE:
                    {
                        if (!IsServer)
                        {
                            string sid = reader.GetString();
                            // /Client 
                            if (string.IsNullOrEmpty(_cliGateSid) || sid == _cliGateSid)
                            {
                                _cliGateSid = sid;
                                _cliSceneGateReleased = true;
                                Client_ReportSelfHealth_IfReadyOnce();
                            }
                            else
                            {
                                Debug.LogWarning($"[GATE] release sid mismatch: srv={sid}, cli={_cliGateSid} - accepting");
                                _cliGateSid = sid;                // 
                                _cliSceneGateReleased = true;
                                Client_ReportSelfHealth_IfReadyOnce();
                            }
                        }
                        break;
                    }


                case Op.PLAYER_HURT_EVENT:
                    if (!IsServer) Client_ApplySelfHurtFromServer(reader);
                    break;





            }

            reader.Recycle();
        }



        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            Debug.Log($"DisconnectedConnect: {peer.EndPoint}, Reason: {disconnectInfo.Reason}");
            if (!IsServer)
            {
                status = "Connection lost";
                isConnecting = false;
            }
            if (connectedPeer == peer) connectedPeer = null;

            if (playerStatuses.ContainsKey(peer))
            {
                var _st = playerStatuses[peer];
                if (_st != null && !string.IsNullOrEmpty(_st.EndPoint))
                    _cliLastSceneIdByPlayer.Remove(_st.EndPoint);
                playerStatuses.Remove(peer);
            }
            if (remoteCharacters.ContainsKey(peer) && remoteCharacters[peer] != null)
            {
                Destroy(remoteCharacters[peer]);
                remoteCharacters.Remove(peer);
            }



        }

        public void OnNetworkError(IPEndPoint endPoint, System.Net.Sockets.SocketError socketError)
        {
            Debug.LogError($"Networkerror: {socketError} from {endPoint}");
        }

        public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
            if (playerStatuses.ContainsKey(peer))
                playerStatuses[peer].Latency = latency;
        }

        public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
            string msg = reader.GetString();

            if (IsServer && msg == "DISCOVER_REQUEST")
            {
                writer.Reset();
                writer.Put("DISCOVER_RESPONSE");
                netManager.SendUnconnectedMessage(writer, remoteEndPoint);
            }
            else if (!IsServer && msg == "DISCOVER_RESPONSE")
            {
                string hostInfo = remoteEndPoint.Address + ":" + port;
                if (!hostSet.Contains(hostInfo))
                {
                    hostSet.Add(hostInfo);
                    hostList.Add(hostInfo);
                    Debug.Log("Found host: " + hostInfo);
                }
            }
        }

        private void SendBroadcastDiscovery()
        {
            // Only valid in LiteNetLib/UDP discovery mode
            if (IsServer) return;
            if (transport != null) return;
            if (netManager == null) return;
            writer.Reset();
            writer.Put("DISCOVER_REQUEST");
            netManager.SendUnconnectedMessage(writer, "255.255.255.255", port);
        }

        private void ConnectToHost(string ip, int port)
        {
            // 
            if (string.IsNullOrWhiteSpace(ip))
            {
                status = "IP is empty";
                isConnecting = false;
                return;
            }
            if (port <= 0 || port > 65535)
            {
                status = "Invalid port";
                isConnecting = false;
                return;
            }

            if (IsServer)
            {
                Debug.LogWarning("Server mode cannot actively connect to other hosts");
                return;
            }
            if (isConnecting)
            {
                Debug.LogWarning("Connecting...");
                return;
            }

            // HostMode ClientNetwork 
            if (netManager == null || !netManager.IsRunning || IsServer || !networkStarted)
            {
                try
                {
                    StartNetwork(false); // /Switch to ClientMode
                }
                catch (Exception e)
                {
                    Debug.LogError($"Start client network failed: {e}");
                    status = "Client network start failed";
                    isConnecting = false;
                    return;
                }
            }

            // 
            if (netManager == null || !netManager.IsRunning)
            {
                status = "Client not started";
                isConnecting = false;
                return;
            }

            try
            {
                status = $"Connecting: {ip}:{port}";
                isConnecting = true;

                // Connect Disconnected Status 
                try { connectedPeer?.Disconnect(); } catch { }
                connectedPeer = null;

                if (writer == null) writer = new LiteNetLib.Utils.NetDataWriter();

                writer.Reset();
                writer.Put("gameKey");
                netManager.Connect(ip, port, writer);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Connect to host failed: {ex}");
                status = "Connection failed";
                isConnecting = false;
                connectedPeer = null;
            }
        }

        private void SendClientStatusUpdate()
        {
            if (IsServer || connectedPeer == null) return;

            localPlayerStatus.CustomFaceJson = LoadLocalCustomFaceJson();
            var equipmentList = GetLocalEquipment();
            var weaponList = GetLocalWeapons();

            writer.Reset();
            writer.Put((byte)Op.CLIENT_STATUS_UPDATE);     // opcode
            writer.Put(localPlayerStatus.EndPoint);
            writer.Put(localPlayerStatus.PlayerName);
            writer.Put(localPlayerStatus.IsInGame);
            writer.PutVector3(localPlayerStatus.Position); 
            writer.PutQuaternion(localPlayerStatus.Rotation);

            writer.Put(localPlayerStatus?.SceneId ?? string.Empty);

            writer.Put(localPlayerStatus.CustomFaceJson ?? "");

            writer.Put(equipmentList.Count);
            foreach (var e in equipmentList) e.Serialize(writer);

            writer.Put(weaponList.Count);
            foreach (var w in weaponList) w.Serialize(writer);

            TransportSendToServer(writer, true);
        }


        private void SendPlayerStatusUpdate()
        {
            if (!IsServer) return;

            var statuses = new List<PlayerStatus> { localPlayerStatus };
            foreach (var kvp in playerStatuses) statuses.Add(kvp.Value);

            writer.Reset();
            writer.Put((byte)Op.PLAYER_STATUS_UPDATE);     // opcode
            writer.Put(statuses.Count);

            foreach (var st in statuses)
            {
                writer.Put(st.EndPoint);
                writer.Put(st.PlayerName);
                writer.Put(st.Latency);
                writer.Put(st.IsInGame);
                writer.PutVector3(st.Position);
                writer.PutQuaternion(st.Rotation);

                string sid = st.SceneId;
                writer.Put(sid ?? string.Empty);

                writer.Put(st.CustomFaceJson ?? "");

                var equipmentList = st == localPlayerStatus ? GetLocalEquipment() : (st.EquipmentList ?? new List<EquipmentSyncData>());
                writer.Put(equipmentList.Count);
                foreach (var e in equipmentList) e.Serialize(writer);

                var weaponList = st == localPlayerStatus ? GetLocalWeapons() : (st.WeaponList ?? new List<WeaponSyncData>());
                writer.Put(weaponList.Count);
                foreach (var w in weaponList) w.Serialize(writer);
            }

            TransportBroadcast(writer, true);
        }


        private void SendPositionUpdate()
        {
            if (localPlayerStatus == null || !networkStarted) return;

            var main = CharacterMainControl.Main;
            if (!main) return;

            var tr = main.transform;
            var mr = main.modelRoot ? main.modelRoot.transform : null;

            Vector3 pos = tr.position;
            Vector3 fwd = mr ? mr.forward : tr.forward;
            if (fwd.sqrMagnitude < 1e-12f) fwd = Vector3.forward;
         

            writer.Reset();
            writer.Put((byte)Op.POSITION_UPDATE);
            writer.Put(localPlayerStatus.EndPoint);

            // + 
            NetPack.PutV3cm(writer, pos);
            NetPack.PutDir(writer, fwd);

            if (IsServer) TransportBroadcast(writer, false);
            else TransportSendToServer(writer, false);
        }



        private List<EquipmentSyncData> GetLocalEquipment()
        {
            var equipmentList = new List<EquipmentSyncData>();
            var equipmentController = CharacterMainControl.Main?.EquipmentController;
            if (equipmentController == null) return equipmentList;

            var slotNames = new[] { "armorSlot", "helmatSlot", "faceMaskSlot", "backpackSlot", "headsetSlot" };
            var slotHashes = new[] { CharacterEquipmentController.armorHash, CharacterEquipmentController.helmatHash, CharacterEquipmentController.faceMaskHash, CharacterEquipmentController.backpackHash, CharacterEquipmentController.headsetHash };

            for (int i = 0; i < slotNames.Length; i++)
            {
                try
                {
                    var slotField = Traverse.Create(equipmentController).Field<ItemStatsSystem.Items.Slot>(slotNames[i]);
                    if (slotField.Value == null) continue;

                    var slot = slotField.Value;
                    string itemId = (slot?.Content != null) ? slot.Content.TypeID.ToString() : "";
                    equipmentList.Add(new EquipmentSyncData { SlotHash = slotHashes[i], ItemId = itemId });
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Error getting slot {slotNames[i]}: {ex.Message}");
                }
            }

            return equipmentList;
        }

        private List<WeaponSyncData> GetLocalWeapons()
        {
            var weaponList = new List<WeaponSyncData>();
            var mainControl = CharacterMainControl.Main;
            if (mainControl == null) return weaponList;

            try
            {
                var rangedWeapon = mainControl.GetGun();
                weaponList.Add(new WeaponSyncData
                {
                    SlotHash = (int)HandheldSocketTypes.normalHandheld,
                    ItemId = rangedWeapon != null ? rangedWeapon.Item.TypeID.ToString() : ""
                });

                var meleeWeapon = mainControl.GetMeleeWeapon();
                weaponList.Add(new WeaponSyncData
                {
                    SlotHash = (int)HandheldSocketTypes.meleeWeapon,
                    ItemId = meleeWeapon != null ? meleeWeapon.Item.TypeID.ToString() : ""
                });
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error getting local weapon data: {ex.Message}");
            }

            return weaponList;
        }

        

        

        

        

        void OnDestroy()
        {
            StopNetwork();
        }

        private static string CleanName(string n)
        {
            if (string.IsNullOrEmpty(n)) return string.Empty;
            if (n.EndsWith("(Clone)", StringComparison.Ordinal)) n = n.Substring(0, n.Length - "(Clone)".Length);
            return n.Trim();
        }
        private static string TypeNameOf(Grenade g)
        {
            return g ? g.GetType().FullName : string.Empty;
        }

        



        // Client 
        

        // Host 
        

        // Server Client Client Server 
        // Server Client -> Server Grenade
        

        // Client 
        // Client Host 
        

        


        // Client Press typeId Item Skill_Grenade grenadePfb 
        


        

        // 
        

        

        


        


        public void SendItemDropRequest(uint token, Item item, Vector3 pos, bool createRb, Vector3 dir, float angle)
        {
            if (netManager == null || IsServer) return;
            var w = writer;
            w.Reset();
            w.Put((byte)Op.ITEM_DROP_REQUEST);
            w.Put(token);
            NetPack.PutV3cm(w, pos);
            NetPack.PutDir(w, dir);
            w.Put(angle);
            w.Put(createRb);
            WriteItemSnapshot(w, item);
            SendReliable(w);
        }

        void HandleItemDropRequest(NetPeer peer, NetPacketReader r)
        {
            if (!IsServer) return;
            uint token = r.GetUInt();
            Vector3 pos = r.GetV3cm();
            Vector3 dir = r.GetDir();
            float angle = r.GetFloat();
            bool create = r.GetBool();
            var snap = ReadItemSnapshot(r);

            // Host Postfix 
            var item = BuildItemFromSnapshot(snap);
            if (item == null) return;
            _serverSpawnedFromClientItems.Add(item);
            var agent = item.Drop(pos, create, dir, angle);

            // id 
            uint id = AllocateDropId();
            serverDroppedItems[id] = item;


            if (agent && agent.gameObject) AddNetDropTag(agent.gameObject, id);

            // SPAWN token Client 
            var w = writer;
            w.Reset();
            w.Put((byte)Op.ITEM_SPAWN);
            w.Put(token);          // Client token 
            w.Put(id);
            NetPack.PutV3cm(w, pos);
            NetPack.PutDir(w, dir);
            w.Put(angle);
            w.Put(create);
            WriteItemSnapshot(w, item); // Status
            BroadcastReliable(w);
        }

        void HandleItemSpawn(NetPacketReader r)
        {
            if (IsServer) return;
            uint token = r.GetUInt();
            uint id = r.GetUInt();
            Vector3 pos = r.GetV3cm();
            Vector3 dir = r.GetDir();
            float angle = r.GetFloat();
            bool create = r.GetBool();
            var snap = ReadItemSnapshot(r);

            if (pendingLocalDropTokens.Remove(token))
            {
                if (pendingTokenItems.TryGetValue(token, out var localItem) && localItem != null)
                {
                    clientDroppedItems[id] = localItem;   // Hostid -> item
                    pendingTokenItems.Remove(token);

                    AddNetDropTag(localItem, id);
                }
                else
                {
                    // 
                    var item2 = BuildItemFromSnapshot(snap);
                    if (item2 != null)
                    {
                        _clientSpawnByServerItems.Add(item2);
                        var agent2 = item2.Drop(pos, create, dir, angle);
                        clientDroppedItems[id] = item2;

                        if (agent2 && agent2.gameObject) AddNetDropTag(agent2.gameObject, id);
                    }
                }
                return;
            }

            // Host 
            var item = BuildItemFromSnapshot(snap);
            if (item == null) return;

            _clientSpawnByServerItems.Add(item);
            var agent = item.Drop(pos, create, dir, angle);
            clientDroppedItems[id] = item;

            if (agent && agent.gameObject) AddNetDropTag(agent.gameObject, id);
        }

        void SendItemPickupRequest(uint dropId)
        {
            if (IsServer || !networkStarted) return;
            var w = writer; w.Reset();
            w.Put((byte)Op.ITEM_PICKUP_REQUEST);
            w.Put(dropId);
            SendReliable(w);
        }

        void HandleItemPickupRequest(NetPeer peer, NetPacketReader r)
        {
            if (!IsServer) return;
            uint id = r.GetUInt();
            if (!serverDroppedItems.TryGetValue(id, out var item) || item == null)
                return; // 

            // agent 
            serverDroppedItems.Remove(id);
            try
            {
                var agent = item.ActiveAgent;
                if (agent != null && agent.gameObject != null)
                    UnityEngine.Object.Destroy(agent.gameObject);
            }
            catch (Exception e) { UnityEngine.Debug.LogWarning($"[ITEM] Serverdestroy agent exception: {e.Message}"); }

            // DESPAWN
            var w = writer; w.Reset();
            w.Put((byte)Op.ITEM_DESPAWN);
            w.Put(id);
            BroadcastReliable(w);
        }

        void HandleItemDespawn(NetPacketReader r)
        {
            if (IsServer) return;
            uint id = r.GetUInt();
            if (clientDroppedItems.TryGetValue(id, out var item))
            {
                clientDroppedItems.Remove(id);
                try
                {
                    var agent = item?.ActiveAgent;
                    if (agent != null && agent.gameObject != null)
                        UnityEngine.Object.Destroy(agent.gameObject);
                }
                catch (Exception e) { UnityEngine.Debug.LogWarning($"[ITEM] Clientdestroy agent exception: {e.Message}"); }
            }
        }

        static void AddNetDropTag(UnityEngine.GameObject go, uint id)
        {
            if (!go) return;
            var tag = go.GetComponent<NetDropTag>() ?? go.AddComponent<NetDropTag>();
            tag.id = id;
        }
        static void AddNetDropTag(Item item, uint id)
        {
            try
            {
                var ag = item?.ActiveAgent;
                if (ag && ag.gameObject) AddNetDropTag(ag.gameObject, id);
            }
            catch { }
        }

        // Press typeId itemId Item Skill_Grenade.grenadePfb
        // Removed legacy pending-grenade resolver (PendingSpawn path)

        // Server itemId Skill_Grenade 
        // di DamageInfo + Grenade OnRelease 
        private async Cysharp.Threading.Tasks.UniTask<(global::DamageInfo di, bool create, float shake, float effectRange, bool delayFromCollide, float delay, bool isMine, float mineRange)>
            ReadGrenadeTemplateAsync(int typeId)
        {
            Item item = null;
            try
            {
                item = await DuckovCoopMod.COOPManager.GetItemAsync(typeId);
                var skill = item ? item.GetComponent<Skill_Grenade>() : null;

                // 
                global::DamageInfo di = default;
                bool create = true;
                float shake = 1f;
                float effectRange = 3f;
                bool delayFromCollide = false;
                float delay = 0f;
                bool isMine = false;
                float mineRange = 0f;

                if (skill != null)
                {
                    di = skill.damageInfo;
                    create = skill.createExplosion;
                    shake = skill.explosionShakeStrength;
                    delayFromCollide = skill.delayFromCollide;
                    delay = skill.delay;
                    isMine = skill.isLandmine;
                    mineRange = skill.landmineTriggerRange;

                    // effectRange skillContext 
                    try
                    {
                        var ctx = skill.SkillContext;
                        //if (ctx != null)
                        {
                            var fEff = AccessTools.Field(ctx.GetType(), "effectRange");
                            if (fEff != null) effectRange = (float)fEff.GetValue(ctx);
                        }
                    }
                    catch { }
                }

                try { di.fromWeaponItemID = typeId; } catch { }

                return (di, create, shake, effectRange, delayFromCollide, delay, isMine, mineRange);
            }
            finally
            {
                if (item && item.gameObject) UnityEngine.Object.Destroy(item.gameObject);
            }
        }

        // Client 
        public void Net_OnClientMeleeAttack(float dealDelay, Vector3 snapPos, Vector3 snapDir)
        {
            if (!networkStarted || IsServer || connectedPeer == null) return;
            writer.Reset();
            writer.Put((byte)Op.MELEE_ATTACK_REQUEST);
            writer.Put(dealDelay);
            writer.PutV3cm(snapPos);
            writer.PutDir(snapDir);
            TransportSendToServer(writer, true);
        }

        public void BroadcastMeleeSwing(string playerId, float dealDelay)
        {
            foreach (var p in netManager.ConnectedPeerList)
            {
                var w = new NetDataWriter();
                w.Put((byte)Op.MELEE_ATTACK_SWING);
                w.Put(playerId);
                w.Put(dealDelay);
                TransportSend(p, w, true);
            }
        }


        // Host Client + FX 
        void HandleMeleeAttackRequest(NetPeer sender, NetPacketReader reader)
        {

            float delay = reader.GetFloat();
            Vector3 pos = reader.GetV3cm();
            Vector3 dir = reader.GetDir();

            if (remoteCharacters.TryGetValue(sender, out var who) && who)
            {
                var anim = who.GetComponent<CharacterMainControl>().characterModel.GetComponent<CharacterAnimationControl_MagicBlend>();
                if (anim != null) anim.OnAttack();

                var model = who.GetComponent<CharacterMainControl>().characterModel;
                if (model) DuckovCoopMod.MeleeFx.SpawnSlashFx(model);
            }

            string pid = (playerStatuses.TryGetValue(sender, out var st) && !string.IsNullOrEmpty(st.EndPoint))
                          ? st.EndPoint : sender.EndPoint.ToString();
            foreach (var p in netManager.ConnectedPeerList)
            {
                if (p == sender) continue;
                var w = new NetDataWriter();
                w.Put((byte)Op.MELEE_ATTACK_SWING);
                w.Put(pid);
                w.Put(delay);
                TransportSend(p, w, true);
            }
        }

        // DR 
        static bool IsSelfDR(DamageReceiver dr, CharacterMainControl attacker)
        {
            if (!dr || !attacker) return false;
            var owner = dr.GetComponentInParent<CharacterMainControl>(true);
            return owner == attacker;
        }

        // DR / 
        static bool IsCharacterDR(DamageReceiver dr)
        {
            return dr && dr.GetComponentInParent<CharacterMainControl>(true) != null;
        }
        void HandleMeleeHitReport(NetPeer sender, NetPacketReader reader)
        {
            Debug.Log($"[SERVER] HandleMeleeHitReport begin, from={sender?.EndPoint}, bytes={reader.AvailableBytes}");

            string attackerId = reader.GetString();

            float dmg = reader.GetFloat();
            float ap = reader.GetFloat();
            float cdf = reader.GetFloat();
            float cr = reader.GetFloat();
            int crit = reader.GetInt();

            Vector3 hitPoint = reader.GetV3cm();
            Vector3 normal = reader.GetDir();

            int wid = reader.GetInt();
            float bleed = reader.GetFloat();
            bool boom = reader.GetBool();
            float range = reader.GetFloat();

            if (!remoteCharacters.TryGetValue(sender, out var attackerGo) || !attackerGo)
            {
                Debug.LogWarning("[SERVER] melee: attackerGo missing for sender");
                return;
            }

            // Player 
            CharacterMainControl attackerCtrl = null;
            var attackerModel = attackerGo.GetComponent<CharacterModel>() ?? attackerGo.GetComponentInChildren<CharacterModel>(true);
            if (attackerModel && attackerModel.characterMainControl) attackerCtrl = attackerModel.characterMainControl;
            if (!attackerCtrl) attackerCtrl = attackerGo.GetComponent<CharacterMainControl>() ?? attackerGo.GetComponentInChildren<CharacterMainControl>(true);
            if (!attackerCtrl)
            {
                Debug.LogWarning("[SERVER] melee: attackerCtrl null (unexpected instance structure)");
                return;
            }

            // Trigger 
            int mask = GameplayDataSettings.Layers.damageReceiverLayerMask;
            float radius = Mathf.Clamp(range * 0.6f, 0.4f, 1.2f);

            Collider[] buf = new Collider[12];
            int n = 0;
            try
            {
                n = Physics.OverlapSphereNonAlloc(hitPoint, radius, buf, mask, QueryTriggerInteraction.UseGlobal);
            }
            catch
            {
                var tmp = Physics.OverlapSphere(hitPoint, radius, mask, QueryTriggerInteraction.UseGlobal);
                n = Mathf.Min(tmp.Length, buf.Length);
                Array.Copy(tmp, buf, n);
            }

            DamageReceiver best = null;
            float bestD2 = float.MaxValue;

            for (int i = 0; i < n; i++)
            {
                var col = buf[i]; if (!col) continue;
                var dr = col.GetComponent<DamageReceiver>(); if (!dr) continue;

                if (IsSelfDR(dr, attackerCtrl)) continue;                // 
                if (IsCharacterDR(dr) && !Team.IsEnemy(dr.Team, attackerCtrl.Team)) continue; // 

                float d2 = (dr.transform.position - hitPoint).sqrMagnitude;
                if (d2 < bestD2) { bestD2 = d2; best = dr; }
            }

            // 
            if (!best)
            {
                Vector3 dir = attackerCtrl.transform.forward;
                Vector3 start = hitPoint - dir * 0.5f;
                if (Physics.SphereCast(start, 0.3f, dir, out var hit, 1.5f, mask, QueryTriggerInteraction.UseGlobal))
                {
                    var dr = hit.collider ? hit.collider.GetComponent<DamageReceiver>() : null;
                    if (dr != null && !IsSelfDR(dr, attackerCtrl))
                    {
                        if (!IsCharacterDR(dr) || Team.IsEnemy(dr.Team, attackerCtrl.Team))
                            best = dr;
                    }
                }
            }

            if (!best)
            {
                Debug.Log($"[SERVER] melee hit miss @ {hitPoint} r={radius}");
                return;
            }

            // / 
            bool victimIsChar = IsCharacterDR(best);

            // / 
            var attackerForDI = (victimIsChar || !ServerTuning.UseNullAttackerForEnv) ? attackerCtrl : null;

            var di = new DamageInfo(attackerForDI)
            {
                damageValue = dmg,
                armorPiercing = ap,
                critDamageFactor = cdf,
                critRate = cr,
                crit = crit,
                damagePoint = hitPoint,
                damageNormal = normal,
                fromWeaponItemID = wid,
                bleedChance = bleed,
                isExplosion = boom
            };

            float scale = victimIsChar ? ServerTuning.RemoteMeleeCharScale : ServerTuning.RemoteMeleeEnvScale;
            if (Mathf.Abs(scale - 1f) > 1e-3f) di.damageValue = Mathf.Max(0f, di.damageValue * scale);

            Debug.Log($"[SERVER] melee hit -> target={best.name} raw={dmg} scaled={di.damageValue} env={!victimIsChar}");
            best.Hurt(di);
        }

        // Client Host payload 
        public void Client_RequestDestructibleHurt(uint id, DamageInfo dmg)
        {
            if (!networkStarted || IsServer || connectedPeer == null) return;

            var w = new NetDataWriter();
            w.Put((byte)Op.ENV_HURT_REQUEST);
            w.Put(id);

            // NetPack.PutDamagePayload 
            w.PutDamagePayload(
                dmg.damageValue, dmg.armorPiercing, dmg.critDamageFactor, dmg.critRate, dmg.crit,
                dmg.damagePoint, dmg.damageNormal, dmg.fromWeaponItemID, dmg.bleedChance, dmg.isExplosion,
                0f
            );
            TransportSendToServer(w, true);
        }

        // Host Client 
        private void Server_HandleEnvHurtRequest(NetPeer sender, NetPacketReader r)
        {
            uint id = r.GetUInt();
            var payload = r.GetDamagePayload(); // (dmg, ap, cdf, cr, crit, point, normal, wid, bleed, boom, range)

            var hs = FindDestructible(id);
            if (!hs) return;

            // DamageInfo / 
            var info = new DamageInfo
            {
                damageValue = payload.dmg * ServerTuning.RemoteMeleeEnvScale, // Mod.cs 
                armorPiercing = payload.ap,
                critDamageFactor = payload.cdf,
                critRate = payload.cr,
                crit = payload.crit,
                damagePoint = payload.point,
                damageNormal = payload.normal,
                fromWeaponItemID = payload.wid,
                bleedChance = payload.bleed,
                isExplosion = payload.boom,
                fromCharacter = null // ServerTuning.UseNullAttackerForEnv 
            };

            // HealthSimpleBase OnHurt / Postfix 
            try { hs.dmgReceiver.Hurt(info); } catch { }
        }

        // Host Client HitFx ClientUI/ 
        public void Server_BroadcastDestructibleHurt(uint id, float newHealth, DamageInfo dmg)
        {
            if (!networkStarted || !IsServer) return;
            var w = new NetDataWriter();
            w.Put((byte)Op.ENV_HURT_EVENT);
            w.Put(id);
            w.Put(newHealth);
            // Hit + 
            w.PutV3cm(dmg.damagePoint);
            w.PutDir(dmg.damageNormal.sqrMagnitude < 1e-6f ? Vector3.forward : dmg.damageNormal.normalized);
            TransportBroadcast(w, true);
        }

        public void Server_BroadcastDestructibleDead(uint id, DamageInfo dmg)
        {
            var w = new NetDataWriter();
            w.Put((byte)Op.ENV_DEAD_EVENT);
            w.Put(id);
            w.PutV3cm(dmg.damagePoint);
            w.PutDir(dmg.damageNormal.sqrMagnitude < 1e-6f ? Vector3.up : dmg.damageNormal.normalized);
            TransportBroadcast(w, true);
        }

        // Client OnHurt 
        // Client + Breakable 
        private void Client_ApplyDestructibleHurt(NetPacketReader r)
        {
            uint id = r.GetUInt();
            float curHealth = r.GetFloat();
            Vector3 point = r.GetV3cm();
            Vector3 normal = r.GetDir();

            // 
            if (_deadDestructibleIds.Contains(id)) return;

            // Host <= 0 
            if (curHealth <= 0f)
            {
                Client_ApplyDestructibleDead_Inner(id, point, normal);
                return;
            }

            var hs = FindDestructible(id);
            if (!hs) return;

            // HurtVisual 
            var hv = hs.GetComponent<HurtVisual>();
            if (hv && hv.HitFx) Object.Instantiate(hv.HitFx, point, Quaternion.LookRotation(normal));

            // Breakable Switch 
            var br = hs.GetComponent<Breakable>();
            if (br)
            {
                // simpleHealth.HealthValue <= dangerHealth danger :contentReference[oaicite:7]{index=7}
                try
                {
                    // Server & fx
                    if (curHealth <= br.dangerHealth && !_dangerDestructibleIds.Contains(id))
                    {
                        // normal -> danger
                        if (br.normalVisual) br.normalVisual.SetActive(false);
                        if (br.dangerVisual) br.dangerVisual.SetActive(true);
                        if (br.dangerFx) Object.Instantiate(br.dangerFx, br.transform.position, br.transform.rotation);
                        _dangerDestructibleIds.Add(id);
                    }
                }
                catch { /* Defensive: ignore when decompiled field is null */ }
            }
        }


        // Client 
        // Client Breakable/ / FX/ 
        private void Client_ApplyDestructibleDead_Inner(uint id, Vector3 point, Vector3 normal)
        {
            if (_deadDestructibleIds.Contains(id)) return;
            _deadDestructibleIds.Add(id);

            var hs = FindDestructible(id);
            if (!hs) return;

            // Breakable OnDead 
            var br = hs.GetComponent<Breakable>();
            if (br)
            {
                try
                {
                    // normal/danger -> breaked
                    if (br.normalVisual) br.normalVisual.SetActive(false);
                    if (br.dangerVisual) br.dangerVisual.SetActive(false);
                    if (br.breakedVisual) br.breakedVisual.SetActive(true);

                    // 
                    if (br.mainCollider) br.mainCollider.SetActive(false);

                    // LevelManager.ExplosionManager.CreateExplosion(...) :contentReference[oaicite:9]{index=9}
                    if (br.createExplosion)
                    {
                        // fromCharacter Client 
                        var di = br.explosionDamageInfo;
                        di.fromCharacter = null;
                        LevelManager.Instance.ExplosionManager.CreateExplosion(
                            hs.transform.position, br.explosionRadius, di, ExplosionFxTypes.normal, 1f
                        );
                    }
                }
                catch { /* Ignore exceptions due to decompilation differences */ }
            }

            // HalfObsticle Dead 
            var half = hs.GetComponent<HalfObsticle>();
            if (half) { try { half.Dead(new DamageInfo { damagePoint = point, damageNormal = normal }); } catch { } }

            // HurtVisual.DeadFx 
            var hv = hs.GetComponent<HurtVisual>();
            if (hv && hv.DeadFx) Object.Instantiate(hv.DeadFx, hs.transform.position, hs.transform.rotation);

            // Collider 
            foreach (var c in hs.GetComponentsInChildren<Collider>(true)) c.enabled = false;
        }

        // ENV_DEAD_EVENT 
        private void Client_ApplyDestructibleDead(NetPacketReader r)
        {
            uint id = r.GetUInt();
            Vector3 point = r.GetV3cm();
            Vector3 normal = r.GetDir();
            Client_ApplyDestructibleDead_Inner(id, point, normal);
        }


        public void RegisterDestructible(uint id, HealthSimpleBase hs)
        {
            if (id == 0 || hs == null) return;
            if (IsServer) _serverDestructibles[id] = hs;
            else _clientDestructibles[id] = hs;
        }

        // Switch 
        private HealthSimpleBase FindDestructible(uint id)
        {
            HealthSimpleBase hs = null;
            if (IsServer) _serverDestructibles.TryGetValue(id, out hs);
            else _clientDestructibles.TryGetValue(id, out hs);
            if (hs) return hs;

            var all = Object.FindObjectsOfType<HealthSimpleBase>(true);
            foreach (var e in all)
            {
                var tag = e.GetComponent<NetDestructibleTag>() ?? e.gameObject.AddComponent<NetDestructibleTag>();
                RegisterDestructible(tag.id, e);
                if (tag.id == id) hs = e;
            }
            return hs;
        }
        private void BuildDestructibleIndex()
        {
            // Status //
            if (_deadDestructibleIds != null) _deadDestructibleIds.Clear();
            if (_dangerDestructibleIds != null) _dangerDestructibleIds.Clear();

            if (_serverDestructibles != null) _serverDestructibles.Clear();
            if (_clientDestructibles != null) _clientDestructibles.Clear();

            // HSB index 
            var all = UnityEngine.Object.FindObjectsOfType<HealthSimpleBase>(true);
            for (int i = 0; i < all.Length; i++)
            {
                var hs = all[i];
                if (!hs) continue;

                var tag = hs.GetComponent<NetDestructibleTag>();
                if (!tag) continue; // NetDestructibleTag / 

                // ID //
                uint id = ComputeStableIdForDestructible(hs);
                if (id == 0u)
                {
                    // gameObject 
                    try { id = NetDestructibleTag.ComputeStableId(hs.gameObject); } catch { }
                }
                tag.id = id;

                // //
                RegisterDestructible(tag.id, hs);
            }

            // Host _deadDestructibleIds //
            if (IsServer) // Host / 
            {
                ScanAndMarkInitiallyDeadDestructibles();
            }
        }


        private void OnSceneLoaded_IndexDestructibles(Scene s, LoadSceneMode m)
        {
            if (!networkStarted) return;
            BuildDestructibleIndex();

            _cliHookedSelf = false;

            if (!IsServer)
            {
                _cliInitHpReported = false;      // 
                Client_ReportSelfHealth_IfReadyOnce(); // 
            }

            try
            {
                if (!networkStarted || localPlayerStatus == null) return;

                var ok = ComputeIsInGame(out var sid);
                localPlayerStatus.SceneId = sid;
                localPlayerStatus.IsInGame = ok;

                if (!IsServer) SendClientStatusUpdate();
                else SendPlayerStatusUpdate();
            }
            catch { }

        }

        private void OnLevelInitialized_IndexDestructibles()
        {
            if (!networkStarted) return;
            BuildDestructibleIndex();
        }

        private string GetPlayerId(NetPeer peer)
        {
            if (peer == null)
            {
                if (localPlayerStatus != null && !string.IsNullOrEmpty(localPlayerStatus.EndPoint))
                    return localPlayerStatus.EndPoint;   // "Host:9050"
                return $"Host:{port}";
            }
            if (playerStatuses != null && playerStatuses.TryGetValue(peer, out var st) && !string.IsNullOrEmpty(st.EndPoint))
                return st.EndPoint;
            return peer.EndPoint.ToString();
        }

        // Health Character Health UI/Hidden 
        private static void BindHealthToCharacter(Health h, CharacterMainControl cmc)
        {
            try { FI_characterCached?.SetValue(h, cmc); FI_hasCharacter?.SetValue(h, true); } catch { }
        }

        // UI 
        private static IEnumerator EnsureBarRoutine(Health h, int attempts, float interval)
        {
            for (int i = 0; i < attempts; i++)
            {
                if (h == null) yield break;
                try { h.showHealthBar = true; } catch { }
                try { h.RequestHealthBar(); } catch { }
                try { h.OnMaxHealthChange?.Invoke(h); } catch { }
                try { h.OnHealthChange?.Invoke(h); } catch { }
                yield return new WaitForSeconds(interval);
            }
        }

        // (max,cur) Health defaultMax=0 
        private void ForceSetHealth(Health h, float max, float cur, bool ensureBar = true)
        {
            if (!h) return;

            float nowMax = 0f; try { nowMax = h.MaxHealth; } catch { }
            int defMax = 0; try { defMax = (int)(FI_defaultMax?.GetValue(h) ?? 0); } catch { }

            // max defaultMaxHealth Max 
            if (max > 0f && (nowMax <= 0f || max > nowMax + 0.0001f || defMax <= 0))
            {
                try
                {
                    FI_defaultMax?.SetValue(h, Mathf.RoundToInt(max));
                    FI_lastMax?.SetValue(h, -12345f);
                    h.OnMaxHealthChange?.Invoke(h);
                }
                catch { }
            }

            // SetHealth() Max 
            float effMax = 0f; try { effMax = h.MaxHealth; } catch { }
            if (effMax > 0f && cur > effMax + 0.0001f)
            {
                try { FI__current?.SetValue(h, cur); } catch { }
                try { h.OnHealthChange?.Invoke(h); } catch { }
            }
            else
            {
                try { h.SetHealth(cur); } catch { try { FI__current?.SetValue(h, cur); } catch { } }
                try { h.OnHealthChange?.Invoke(h); } catch { }
            }

            if (ensureBar)
            {
                try { h.showHealthBar = true; } catch { }
                try { h.RequestHealthBar(); } catch { }
                StartCoroutine(EnsureBarRoutine(h, 30, 0.1f));
            }
        }


        // GameObject Health 
        // Mod.cs
        private void ApplyHealthAndEnsureBar(GameObject go, float max, float cur)
        {
            if (!go) return;

            var cmc = go.GetComponent<CharacterMainControl>();
            var h = go.GetComponentInChildren<Health>(true);
            if (!cmc || !h) return;

            try { h.autoInit = false; } catch { }

            // Health Character UI/Hidden 
            BindHealthToCharacter(h, cmc);

            // OnMax/OnHealth 
            ForceSetHealth(h, max > 0 ? max : 40f, (cur > 0 ? cur : (max > 0 ? max : 40f)), ensureBar: false);

            // + UI Request 
            try { h.showHealthBar = true; } catch { }
            try { h.RequestHealthBar(); } catch { }

            // UI 
            try { h.OnMaxHealthChange?.Invoke(h); } catch { }
            try { h.OnHealthChange?.Invoke(h); } catch { }

            // 8 0.25s EnsureBarRoutine(h, attempts, interval) 
            StartCoroutine(EnsureBarRoutine(h, 8, 0.25f));
        }


        private void Server_OnHealthChanged(NetPeer ownerPeer, Health h)
        {
            if (!IsServer || !h) return;

            float max = 0f, cur = 0f;
            try { max = h.MaxHealth; } catch { }
            try { cur = h.CurrentHealth; } catch { }

            if (max <= 0f) return;
            // + 
            if (_srvLastSent.TryGetValue(h, out var last))
                if (Mathf.Approximately(max, last.max) && Mathf.Approximately(cur, last.cur))
                    return;

            float now = Time.time;
            if (_srvNextSend.TryGetValue(h, out var tNext) && now < tNext)
                return;

            _srvLastSent[h] = (max, cur);
            _srvNextSend[h] = now + SRV_HP_SEND_COOLDOWN;

            // playerId 
            string pid = GetPlayerId(ownerPeer);

            // AUTH_HEALTH_SELF 
            if (ownerPeer != null && ownerPeer.ConnectionState == ConnectionState.Connected)
            {
                var w1 = new NetDataWriter();
                w1.Put((byte)Op.AUTH_HEALTH_SELF);
                w1.Put(max);
                w1.Put(cur);
                TransportSend(ownerPeer, w1, true);
            }

            // Player AUTH_HEALTH_REMOTE playerId 
            var w2 = new NetDataWriter();
            w2.Put((byte)Op.AUTH_HEALTH_REMOTE);
            w2.Put(pid);
            w2.Put(max);
            w2.Put(cur);

            foreach (var p in netManager.ConnectedPeerList)
            {
                if (p == ownerPeer) continue; // 
                TransportSend(p, w2, true);
            }
        }

        private void Server_HookOneHealth(NetPeer peer, GameObject instance)
        {
            if (!instance) return;

            var h = instance.GetComponentInChildren<Health>(true);
            var cmc = instance.GetComponent<CharacterMainControl>();
            if (!h) return;

            try { h.autoInit = false; } catch { }
            BindHealthToCharacter(h, cmc); // hasCharacter UI/Hidden 

            // + 
            _srvHealthOwner[h] = peer;      // host null
            if (!_srvHooked.Contains(h))
            {
                h.OnHealthChange.AddListener(_ => Server_OnHealthChanged(peer, h));
                h.OnMaxHealthChange.AddListener(_ => Server_OnHealthChanged(peer, h));
                _srvHooked.Add(h);
            }

            // 1) Server Client Client
            if (peer != null && _srvPendingHp.TryGetValue(peer, out var snap))
            {
                ApplyHealthAndEnsureBar(instance, snap.max, snap.cur);
                _srvPendingHp.Remove(peer);
                Server_OnHealthChanged(peer, h);
                return;
            }

            // 2) Max<=0 autoInit=false 40f 
            float max = 0f, cur = 0f;
            try { max = h.MaxHealth; } catch { }
            try { cur = h.CurrentHealth; } catch { }

            if (max <= 0f) { max = 40f; if (cur <= 0f) cur = max; }

            ApplyHealthAndEnsureBar(instance, max, cur); // showHealthBar + RequestHealthBar + 
            Server_OnHealthChanged(peer, h);             // Player 
        }




        // Server Host 
        private void Server_EnsureAllHealthHooks()
        {
            if (!IsServer || !networkStarted) return;

            var hostMain = CharacterMainControl.Main;
            if (hostMain) Server_HookOneHealth(null, hostMain.gameObject);

            if (remoteCharacters != null)
            {
                foreach (var kv in remoteCharacters)
                {
                    var peer = kv.Key;
                    var go = kv.Value;
                    if (peer == null || !go) continue;
                    Server_HookOneHealth(peer, go);
                }
            }
        }

        private void Client_ApplyPendingSelfIfReady()
        {
            if (!_cliSelfHpPending) return;
            var main = CharacterMainControl.Main;
            if (!main) return;

            var h = main.GetComponentInChildren<Health>(true);
            var cmc = main.GetComponent<CharacterMainControl>();
            if (!h) return;

            try { h.autoInit = false; } catch { } // Init() 
            BindHealthToCharacter(h, cmc);
            ForceSetHealth(h, _cliSelfHpMax, _cliSelfHpCur, ensureBar: true);

            // 0 Client 
            Client_EnsureSelfDeathEvent(h, cmc);

            _cliSelfHpPending = false;
        }

        private void Client_ApplyPendingRemoteIfAny(string playerId, GameObject go)
        {
            if (string.IsNullOrEmpty(playerId) || !go) return;
            if (!_cliPendingRemoteHp.TryGetValue(playerId, out var snap)) return;

            var cmc = go.GetComponent<CharacterMainControl>();
            var h = cmc.Health;

            if (!h) return;

            try { h.autoInit = false; } catch { }
            BindHealthToCharacter(h, cmc);

            float applyMax = snap.max > 0f ? snap.max : (h.MaxHealth > 0f ? h.MaxHealth : 40f);
            float applyCur = snap.cur > 0f ? snap.cur : applyMax;

            ForceSetHealth(h, applyMax, applyCur, ensureBar: true);
            _cliPendingRemoteHp.Remove(playerId);


            if (_cliPendingProxyBuffs.TryGetValue(playerId, out var pendings) && pendings != null && pendings.Count > 0)
            {
                if (cmc)
                {
                    foreach (var (weaponTypeId, buffId) in pendings)
                    {
                        COOPManager.ResolveBuffAsync(weaponTypeId, buffId)
                            .ContinueWith(b => { if (b != null && cmc) cmc.AddBuff(b, null, weaponTypeId); })
                            .Forget();
                    }
                }
                _cliPendingProxyBuffs.Remove(playerId);
            }

        }

        private void Client_ReportSelfHealth_IfReadyOnce()
        {
            if (_cliApplyingSelfSnap || Time.time < _cliEchoMuteUntil) return;
            if (IsServer || _cliInitHpReported) return;
            if (connectedPeer == null || connectedPeer.ConnectionState != ConnectionState.Connected) return;

            var main = CharacterMainControl.Main;
            var h = main ? main.GetComponentInChildren<Health>(true) : null;
            if (!h) return;

            float max = 0f, cur = 0f;
            try { max = h.MaxHealth; } catch { }
            try { cur = h.CurrentHealth; } catch { }

            var w = new NetDataWriter();
            w.Put((byte)Op.PLAYER_HEALTH_REPORT);
            w.Put(max);
            w.Put(cur);
            TransportSendToServer(w, true);

            _cliInitHpReported = true;
        }

        void HandlePlayerBuffSelfApply(NetPacketReader r)
        {
            int weaponTypeId = r.GetInt(); // overrideWeaponID / Item.TypeID 
            int buffId = r.GetInt();       // buff id
            ApplyBuffToSelf_Client(weaponTypeId, buffId).Forget();
        }


        async Cysharp.Threading.Tasks.UniTask ApplyBuffToSelf_Client(int weaponTypeId, int buffId)
        {
            var me = LevelManager.Instance ? LevelManager.Instance.MainCharacter : null;
            if (!me) return;

            var buff = await COOPManager.ResolveBuffAsync(weaponTypeId, buffId);
            if (buff != null) me.AddBuff(buff, fromWho: null, overrideWeaponID: weaponTypeId);
        }

        void HandleBuffProxyApply(NetPacketReader r)
        {
            string hostId = r.GetString();   // e.g. "Host:9050"
            int weaponTypeId = r.GetInt();
            int buffId = r.GetInt();
            ApplyBuffProxy_Client(hostId, weaponTypeId, buffId).Forget();
        }

        async Cysharp.Threading.Tasks.UniTask ApplyBuffProxy_Client(string playerId, int weaponTypeId, int buffId)
        {
            if (IsSelfId(playerId)) return; // 
            if (!clientRemoteCharacters.TryGetValue(playerId, out var go) || go == null)
            {
                // Host CreateRemoteCharacterForClient 
                if (!_cliPendingProxyBuffs.TryGetValue(playerId, out var list))
                    list = _cliPendingProxyBuffs[playerId] = new System.Collections.Generic.List<(int, int)>();
                list.Add((weaponTypeId, buffId));
                return;
            }

            var cmc = go.GetComponent<CharacterMainControl>();
            if (!cmc) return;

            var buff = await COOPManager.ResolveBuffAsync(weaponTypeId, buffId);
            if (buff != null) cmc.AddBuff(buff, fromWho: null, overrideWeaponID: weaponTypeId);


        }

        


        

        





        

        public static void Call_NotifyEntryClicked_ByInvoke(
        MapSelectionView view,
        MapSelectionEntry entry,
        PointerEventData evt // null 
    )
        {
            var mi = typeof(MapSelectionView).GetMethod(
                "NotifyEntryClicked",
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(MapSelectionEntry), typeof(PointerEventData) },
                modifiers: null
            );
            if (mi == null)
                throw new MissingMethodException("MapSelectionView.NotifyEntryClicked(MapSelectionEntry, PointerEventData) not found.");

            mi.Invoke(view, new object[] { entry, evt });
        }

        




        // ===== Client ReadyStatus pid + ready =====

        public bool IsMapSelectionEntry = false;
        

        

        private static void MakeRemotePhysicsPassive(GameObject go)
        {
            if (!go) return;

            // 1) / 
            var ai = go.GetComponentInChildren<AICharacterController>(true);
            if (ai) ai.enabled = false;

            var nma = go.GetComponentInChildren<NavMeshAgent>(true);
            if (nma) nma.enabled = false;

            var cc = go.GetComponentInChildren<CharacterController>(true);
            if (cc) cc.enabled = false; // collider CC

            // 2) 
            var rb = go.GetComponentInChildren<Rigidbody>(true);
            if (rb) { rb.isKinematic = true; rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }

            // 3) Animator root motion 
            var anim = go.GetComponentInChildren<Animator>(true);
            if (anim) anim.applyRootMotion = false;

            // 
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (!mb) continue;
                var n = mb.GetType().Name;
                // / 
                if (n.Contains("Locomotion") || n.Contains("Movement") || n.Contains("Motor"))
                {
                    var beh = mb as Behaviour;
                    if (beh) beh.enabled = false;
                }
            }
        }

        private void Server_HandleSceneReady(NetPeer fromPeer, string playerId, string sceneId, Vector3 pos, Quaternion rot, string faceJson)
        {
            // Record scene by transport flavor
            if (fromPeer != null) _srvPeerScene[fromPeer] = sceneId;
            var curTid = _currentTransportSenderId;
            if (fromPeer == null && !string.IsNullOrEmpty(curTid)) _tPeerScene[curTid] = sceneId;

            // 0) If client scene differs from host, nudge client to host scene (targeted)
            if (IsServer)
            {
                string hostSceneId = null; ComputeIsInGame(out hostSceneId); hostSceneId = hostSceneId ?? string.Empty;
                if (!string.IsNullOrEmpty(hostSceneId) && sceneId != hostSceneId)
                {
                    // Send SCENE_VOTE_START (v2) to set client-side sceneTargetId etc.
                    var wv = new NetDataWriter();
                    wv.Put((byte)Op.SCENE_VOTE_START);
                    wv.Put((byte)2);
                    wv.Put(hostSceneId);
                    byte flags = PackFlags(false, false, false, false);
                    wv.Put(flags);
                    wv.Put(""); // curtainGuid
                    wv.Put(""); // location
                    wv.Put(hostSceneId); // hostScene
                    wv.Put(1); // count
                    wv.Put(playerId);

                    // Immediately follow with SCENE_BEGIN_LOAD to force load
                    var wb = new NetDataWriter();
                    wb.Put((byte)Op.SCENE_BEGIN_LOAD);
                    wb.Put((byte)1);
                    wb.Put(hostSceneId);
                    wb.Put(flags);
                    wb.Put(""); // curtainGuid
                    wb.Put(""); // location

                    if (fromPeer != null)
                    {
                        TransportSend(fromPeer, wv, true);
                        TransportSend(fromPeer, wb, true);
                    }
                    else if (transport is SteamSocketsTransport sstNudge && !string.IsNullOrEmpty(curTid))
                    {
                        sstNudge.Send(curTid, wv.CopyData(), true);
                        sstNudge.Send(curTid, wb.CopyData(), true);
                    }
                }
            }

            // 1) Tell the joining peer about others already in this scene
            if (fromPeer != null)
            {
                foreach (var kv in _srvPeerScene)
                {
                    var other = kv.Key; if (other == fromPeer) continue; if (kv.Value != sceneId) continue;
                    Vector3 opos = Vector3.zero; Quaternion orot = Quaternion.identity; string oface = "";
                    if (playerStatuses.TryGetValue(other, out var s) && s != null) { opos = s.Position; orot = s.Rotation; oface = s.CustomFaceJson ?? ""; }
                    var w = new NetDataWriter(); w.Put((byte)Op.REMOTE_CREATE); w.Put(playerStatuses[other].EndPoint);
                    w.Put(sceneId); w.PutVector3(opos); w.PutQuaternion(orot); w.Put(oface);
                    TransportSend(fromPeer, w, true);
                }
                if (transport is SteamSocketsTransport sstLite)
                {
                    foreach (var kv in _tPeerScene)
                    {
                        var otherTid = kv.Key; if (kv.Value != sceneId) continue;
                        if (_tPlayerStatuses.TryGetValue(otherTid, out var s) && s != null)
                        {
                            var w = new NetDataWriter(); w.Put((byte)Op.REMOTE_CREATE); w.Put(s.EndPoint ?? otherTid);
                            w.Put(sceneId); w.PutVector3(s.Position); w.PutQuaternion(s.Rotation); w.Put(s.CustomFaceJson ?? "");
                            // Send via NetPeer path isn’t possible; joining peer is NetPeer
                            // So nothing to do here for NetPeer joiner to learn about Steam-only others.
                        }
                    }
                }
            }
            else if (transport is SteamSocketsTransport sst && !string.IsNullOrEmpty(curTid))
            {
                foreach (var kv in _srvPeerScene)
                {
                    if (kv.Value != sceneId) continue; var other = kv.Key;
                    Vector3 opos = Vector3.zero; Quaternion orot = Quaternion.identity; string oface = "";
                    if (playerStatuses.TryGetValue(other, out var s) && s != null) { opos = s.Position; orot = s.Rotation; oface = s.CustomFaceJson ?? ""; }
                    var w = new NetDataWriter(); w.Put((byte)Op.REMOTE_CREATE); w.Put(playerStatuses[other].EndPoint);
                    w.Put(sceneId); w.PutVector3(opos); w.PutQuaternion(orot); w.Put(oface);
                    sst.Send(curTid, w.CopyData(), true);
                }
                foreach (var kv in _tPeerScene)
                {
                    var otherTid = kv.Key; if (otherTid == curTid) continue; if (kv.Value != sceneId) continue;
                    Vector3 opos = Vector3.zero; Quaternion orot = Quaternion.identity; string oface = "";
                    if (_tPlayerStatuses.TryGetValue(otherTid, out var s) && s != null) { opos = s.Position; orot = s.Rotation; oface = s.CustomFaceJson ?? ""; }
                    var w = new NetDataWriter(); w.Put((byte)Op.REMOTE_CREATE); w.Put((_tPlayerStatuses.TryGetValue(otherTid, out var st) ? st.EndPoint : otherTid) ?? otherTid);
                    w.Put(sceneId); w.PutVector3(opos); w.PutQuaternion(orot); w.Put(oface);
                    sst.Send(curTid, w.CopyData(), true);
                }
            }

            // 2) Tell others in the same scene about this joining peer
            if (fromPeer != null)
            {
                foreach (var kv in _srvPeerScene)
                {
                    var other = kv.Key; if (other == fromPeer) continue; if (kv.Value != sceneId) continue;
                    var w = new NetDataWriter(); w.Put((byte)Op.REMOTE_CREATE); w.Put(playerId); w.Put(sceneId);
                    w.PutVector3(pos); w.PutQuaternion(rot);
                    string useFace = !string.IsNullOrEmpty(faceJson) ? faceJson : ((playerStatuses.TryGetValue(fromPeer, out var ss) && !string.IsNullOrEmpty(ss.CustomFaceJson)) ? ss.CustomFaceJson : "");
                    w.Put(useFace); TransportSend(other, w, true);
                }
                if (transport is SteamSocketsTransport sstOther)
                {
                    foreach (var pid in sstOther.Peers)
                    {
                        if (_tPeerScene.TryGetValue(pid, out var psid) && psid == sceneId)
                        {
                            var w = new NetDataWriter(); w.Put((byte)Op.REMOTE_CREATE); w.Put(playerId); w.Put(sceneId);
                            w.PutVector3(pos); w.PutQuaternion(rot); w.Put(faceJson ?? "");
                            sstOther.Send(pid, w.CopyData(), true);
                        }
                    }
                }
            }
            else if (transport is SteamSocketsTransport sst2 && !string.IsNullOrEmpty(curTid))
            {
                foreach (var kv in _srvPeerScene)
                {
                    if (kv.Value != sceneId) continue; var other = kv.Key;
                    var w = new NetDataWriter(); w.Put((byte)Op.REMOTE_CREATE); w.Put(playerId); w.Put(sceneId);
                    w.PutVector3(pos); w.PutQuaternion(rot); w.Put(faceJson ?? "");
                    TransportSend(other, w, true);
                }
                foreach (var pid in sst2.Peers)
                {
                    if (pid == curTid) continue; if (_tPeerScene.TryGetValue(pid, out var psid) && psid == sceneId)
                    {
                        var w = new NetDataWriter(); w.Put((byte)Op.REMOTE_CREATE); w.Put(playerId); w.Put(sceneId);
                        w.PutVector3(pos); w.PutQuaternion(rot); w.Put(faceJson ?? "");
                        sst2.Send(pid, w.CopyData(), true);
                    }
                }
            }

            // 3) Despawn for those in different scenes
            if (fromPeer != null)
            {
                foreach (var kv in _srvPeerScene)
                {
                    var other = kv.Key; if (other == fromPeer) continue;
                    if (kv.Value != sceneId)
                    {
                        var w1 = new NetDataWriter(); w1.Put((byte)Op.REMOTE_DESPAWN); w1.Put(playerId); TransportSend(other, w1, true);
                        var w2 = new NetDataWriter(); w2.Put((byte)Op.REMOTE_DESPAWN); w2.Put(playerStatuses[other].EndPoint); TransportSend(fromPeer, w2, true);
                    }
                }
            }
            else if (transport is SteamSocketsTransport sst3 && !string.IsNullOrEmpty(curTid))
            {
                foreach (var pid in sst3.Peers)
                {
                    if (pid == curTid) continue;
                    if (_tPeerScene.TryGetValue(pid, out var psid))
                    {
                        if (psid != sceneId)
                        {
                            var w1 = new NetDataWriter(); w1.Put((byte)Op.REMOTE_DESPAWN); w1.Put(playerId); sst3.Send(pid, w1.CopyData(), true);
                            var w2 = new NetDataWriter(); w2.Put((byte)Op.REMOTE_DESPAWN);
                            w2.Put(_tPlayerStatuses.TryGetValue(pid, out var stOther) ? (stOther.EndPoint ?? pid) : pid);
                            sst3.Send(curTid, w2.CopyData(), true);
                        }
                    }
                }
            }

            // 4) Host-side replica for NetPeer clients (Steam path TBD)
            if (fromPeer != null)
            {
                if (remoteCharacters.TryGetValue(fromPeer, out var exists) == false || exists == null)
                    CreateRemoteCharacterAsync(fromPeer, pos, rot, faceJson).Forget();
            }
        }

        // ========== Host ==========
        private void Server_BroadcastEnvSync(NetPeer target = null)
        {
            if (!IsServer || netManager == null) return;

            // 1) Host + + 
            long day = GameClock.Day;                                      // OK :contentReference[oaicite:6]{index=6}
            double secOfDay = GameClock.TimeOfDay.TotalSeconds;            // 0~86300 :contentReference[oaicite:7]{index=7}
            float timeScale = 60f;
            try { timeScale = GameClock.Instance.clockTimeScale; } catch { } // :contentReference[oaicite:8]{index=8}

            // 2) seed / / / 
            var wm = Duckov.Weathers.WeatherManager.Instance;
            int seed = -1;
            bool forceWeather = false;
            int forceWeatherVal = (int)Duckov.Weathers.Weather.Sunny;
            int currentWeather = (int)Duckov.Weathers.Weather.Sunny;
            byte stormLevel = 0;

            if (wm != null)
            {
                try { seed = (int)AccessTools.Field(wm.GetType(), "seed").GetValue(wm); } catch { }
                try { forceWeather = (bool)AccessTools.Field(wm.GetType(), "forceWeather").GetValue(wm); } catch { } // 
                try { forceWeatherVal = (int)AccessTools.Field(wm.GetType(), "forceWeatherValue").GetValue(wm); } catch { }
                try { currentWeather = (int)Duckov.Weathers.WeatherManager.GetWeather(); } catch { } // :contentReference[oaicite:9]{index=9}
                try { stormLevel = (byte)wm.Storm.GetStormLevel(GameClock.Now); } catch { } // Now :contentReference[oaicite:10]{index=10}
            }

            // 3) 
            var w = new NetDataWriter();
            w.Put((byte)Op.ENV_SYNC_STATE);
            w.Put(day);
            w.Put(secOfDay);
            w.Put(timeScale);
            w.Put(seed);
            w.Put(forceWeather);
            w.Put(forceWeatherVal);
            w.Put(currentWeather);
            w.Put(stormLevel);

            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<Duckov.Utilities.LootBoxLoader>(true);
                // (key, active)
                var tmp = new System.Collections.Generic.List<(int k, bool on)>(all.Length);
                foreach (var l in all)
                {
                    if (!l || !l.gameObject) continue;
                    int k = ComputeLootKey(l.transform);
                    bool on = l.gameObject.activeSelf; // RandomActive 
                    tmp.Add((k, on));
                }

                w.Put(tmp.Count);
                for (int i = 0; i < tmp.Count; ++i)
                {
                    w.Put(tmp[i].k);
                    w.Put(tmp[i].on);
                }
            }
            catch
            {
                // 0 Client 
                w.Put(0);
            }

            // Door

            bool includeDoors = (target != null);
            if (includeDoors)
            {
                var doors = UnityEngine.Object.FindObjectsOfType<Door>(true);
                var tmp = new System.Collections.Generic.List<(int key, bool closed)>(doors.Length);

                foreach (var d in doors)
                {
                    if (!d) continue;
                    int k = 0;
                    try { k = (int)AccessTools.Field(typeof(Door), "doorClosedDataKeyCached").GetValue(d); } catch { }
                    if (k == 0) k = ComputeDoorKey(d.transform);

                    bool closed;
                    try { closed = !d.IsOpen; } catch { closed = true; } // 
                    tmp.Add((k, closed));
                }

                w.Put(tmp.Count);
                for (int i = 0; i < tmp.Count; ++i)
                {
                    w.Put(tmp[i].key);
                    w.Put(tmp[i].closed);
                }
            }
            else
            {
                w.Put(0); // 
            }

            w.Put(_deadDestructibleIds.Count);
            foreach (var id in _deadDestructibleIds) w.Put(id);

            if (target != null) TransportSend(target, w, true);
            else TransportBroadcast(w, true);
        }

        // ========== Client ==========
        private void Client_RequestEnvSync()
        {
            if (IsServer || connectedPeer == null) return;
            var w = new NetDataWriter();
            w.Put((byte)Op.ENV_SYNC_REQUEST);
            TransportSendToServer(w, false);
        }

        // ========== Client ==========
        private void Client_ApplyEnvSync(long day, double secOfDay, float timeScale, int seed, bool forceWeather, int forceWeatherVal, int currentWeather /*fallback*/, byte stormLevel /*redundant*/)
        {
            // 1) GameClock StepTimeTil 
            try
            {
                var inst = GameClock.Instance;
                if (inst != null)
                {
                    AccessTools.Field(inst.GetType(), "days")?.SetValue(inst, day);
                    AccessTools.Field(inst.GetType(), "secondsOfDay")?.SetValue(inst, secOfDay);
                    try { inst.clockTimeScale = timeScale; } catch { }

                    // onGameClockStep 0 Step 
                    typeof(GameClock).GetMethod("Step", BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, new object[] { 0f });
                }
            }
            catch { }

            // 2) WeatherManager 
            try
            {
                var wm = Duckov.Weathers.WeatherManager.Instance;
                if (wm != null && seed != -1)
                {
                    AccessTools.Field(wm.GetType(), "seed")?.SetValue(wm, seed);                // seed :contentReference[oaicite:11]{index=11}
                    wm.GetType().GetMethod("SetupModules", BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(wm, null); // seed Storm/Precipitation :contentReference[oaicite:12]{index=12}
                    AccessTools.Field(wm.GetType(), "_weatherDirty")?.SetValue(wm, true);       // GetWeather
                }
            }
            catch { }

            // 3) Host Status Client 
            try
            {
                Duckov.Weathers.WeatherManager.SetForceWeather(forceWeather, (Duckov.Weathers.Weather)forceWeatherVal); // :contentReference[oaicite:13]{index=13}
            }
            catch { }

            // 4) ETA Now+seed Storm.* UI 0.5s TimeOfDayDisplay :contentReference[oaicite:14]{index=14}
        }

        



        // 
        


        


        // Host peer 
        


        



        // Mod.cs
        


        // TAKE Client 
        // 
        

        // + 
        




        // Host PUT Client -> Host 
        


        






        

        // Client PUT_OK -> 
        


        

        private static void Client_ApplyLootVisibility(Dictionary<int, bool> vis)
        {
            try
            {
                var core = Duckov.Scenes.MultiSceneCore.Instance;
                if (core == null || vis == null) return;

                foreach (var kv in vis)
                    core.inLevelData[kv.Key] = kv.Value; // 

                // LootBoxLoader 
                var loaders = UnityEngine.Object.FindObjectsOfType<Duckov.Utilities.LootBoxLoader>(true);
                foreach (var l in loaders)
                {
                    try
                    {
                        int k = ModBehaviour.Instance.ComputeLootKey(l.transform);
                        if (vis.TryGetValue(k, out bool on))
                            l.gameObject.SetActive(on);
                    }
                    catch { }
                }
            }
            catch { }
        }


        // Mod.cs
        // moved to Mod.AI.cs: Server_SendAiSeeds

        // moved to Mod.AI.cs: StableRootId_Alt


        // moved to Mod.AI.cs: HandleAiSeedSnapshot

        // moved to Mod.AI.cs: StableHash, TransformPath, DeriveSeed

        // AI 
        // moved to Mod.AI.cs: TryFreezeAI

        // moved to Mod.AI.cs: RegisterAi

        // moved to Mod.AI.cs: GetLocalAIEquipment


        // moved to Mod.AI.cs: Server_BroadcastAiLoadout

        // moved to Mod.AI.cs: Client_ApplyAiLoadout (list overload)

        // moved to Mod.AI.cs: Client_ApplyAiLoadout (async list overload)


        public void Server_BroadcastAiTransforms()
        {
            if (!IsServer || aiById.Count == 0) return;

            writer.Reset();
            writer.Put((byte)Op.AI_TRANSFORM_SNAPSHOT);
            // 
            int cnt = 0;
            foreach (var kv in aiById) if (kv.Value) cnt++;
            writer.Put(cnt);
            foreach (var kv in aiById)
            {
                var cmc = kv.Value; if (!cmc) continue;
                var t = cmc.transform;
                writer.Put(kv.Key);                 // aiId
                writer.PutV3cm(t.position);         // 
                Vector3 fwd = cmc.characterModel.transform.rotation * Vector3.forward;
                writer.PutDir(fwd);
            }
            BroadcastReliable(writer);
        }



        // moved to Mod.AI.cs: TryAutoBindAi


        private void Client_ForceFreezeAllAI()
        {
            if (!networkStarted || IsServer) return;
            var all = UnityEngine.Object.FindObjectsOfType<AICharacterController>(true);
            foreach (var aic in all)
            {
                if (!aic) continue;
                aic.enabled = false;
                var cmc = aic.GetComponentInParent<CharacterMainControl>();
                if (cmc) TryFreezeAI(cmc); // BehaviourTreeOwner + NavMeshAgent + AICtrl
            }
        }

        public int NextAiSerial(int rootId)
        {
            if (!_aiSerialPerRoot.TryGetValue(rootId, out var n)) n = 0;
            n++;
            _aiSerialPerRoot[rootId] = n;
            return n;
        }

        public void ResetAiSerials() => _aiSerialPerRoot.Clear();

        public void MarkAiSceneReady() => _aiSceneReady = true;


        void ApplyAiTransform(int aiId, Vector3 p, Vector3 f)
        {
            if (!aiById.TryGetValue(aiId, out var cmc) || !cmc)
            {
                cmc = TryAutoBindAiWithBudget(aiId, p); // + 
                if (!cmc) return; // 
            }
            if (!IsRealAI(cmc)) return;

            var follower = cmc.GetComponent<NetAiFollower>() ?? cmc.gameObject.AddComponent<NetAiFollower>();
            follower.SetTarget(p, f);
        }

        private CharacterMainControl TryAutoBindAiWithBudget(int aiId, Vector3 snapPos)
        {

            // 1) aiId 
            if (_lastAutoBindTryTime.TryGetValue(aiId, out var last) && (Time.time - last) < AUTOBIND_COOLDOWN)
                return null;
            _lastAutoBindTryTime[aiId] = Time.time;

            // 2) OverlapSphere 
            CharacterMainControl best = null;
            float bestSqr = float.MaxValue;

            var cols = Physics.OverlapSphere(snapPos, AUTOBIND_RADIUS, AUTOBIND_LAYERMASK, AUTOBIND_QTI);
            for (int i = 0; i < cols.Length; i++)
            {
                var c = cols[i];
                if (!c) continue;

                var cmc = c.GetComponentInParent<CharacterMainControl>();
                if (!cmc) continue;
                if (LevelManager.Instance && LevelManager.Instance.MainCharacter == cmc) continue; // Player 
                if (!cmc.gameObject.activeInHierarchy) continue;
                if (!IsRealAI(cmc)) continue;

                if (aiById.ContainsValue(cmc)) continue; // aiId 

                float d2 = (cmc.transform.position - snapPos).sqrMagnitude;
                if (d2 < bestSqr) { bestSqr = d2; best = cmc; }
            }

            if (best != null)
            {
                if (!IsRealAI(best)) return null;

                RegisterAi(aiId, best);                 // & Client NetAiFollower
                if (freezeAI) TryFreezeAI(best);        // 
                return best;
            }

            // 3) NetAiTag 
            if ((Time.frameCount % 20) == 0) // 20 
            {
                var tags = UnityEngine.Object.FindObjectsOfType<NetAiTag>(true);
                for (int i = 0; i < tags.Length; i++)
                {
                    var tag = tags[i];
                    if (!tag || tag.aiId != aiId) continue;
                    var cmc = tag.GetComponentInParent<CharacterMainControl>();
                    if (cmc && !aiById.ContainsValue(cmc))
                    {
                        RegisterAi(aiId, cmc);
                        if (freezeAI) TryFreezeAI(cmc);
                        return cmc;
                    }
                }
            }

            return null; // / 
        }

        private void Server_BroadcastAiAnimations()
        {
            if (!IsServer || aiById == null || aiById.Count == 0) return;

            var list = new List<(int id, AiAnimState st)>(aiById.Count);
            foreach (var kv in aiById)
            {
                int id = kv.Key;
                var cmc = kv.Value;
                if (!cmc) continue;

                // AI 
                if (!IsRealAI(cmc)) continue;      // 

                // GameObject/ Status
                if (!cmc.gameObject.activeInHierarchy || !cmc.enabled) continue;

                var magic = cmc.GetComponentInChildren<CharacterAnimationControl_MagicBlend>(true);
                var anim = magic ? magic.animator : cmc.GetComponentInChildren<Animator>(true);
                if (!anim || !anim.isActiveAndEnabled || !anim.gameObject.activeInHierarchy) continue;

                var st2 = new AiAnimState
                {
                    speed = anim.GetFloat(Animator.StringToHash("MoveSpeed")),
                    dirX = anim.GetFloat(Animator.StringToHash("MoveDirX")),
                    dirY = anim.GetFloat(Animator.StringToHash("MoveDirY")),
                    hand = anim.GetInteger(Animator.StringToHash("HandState")),
                    gunReady = anim.GetBool(Animator.StringToHash("GunReady")),
                    dashing = anim.GetBool(Animator.StringToHash("Dashing")),
                };
                list.Add((id, st2));
            }
            if (list.Count == 0) return;

            // 
            const DeliveryMethod METHOD = DeliveryMethod.Unreliable;
            int maxSingle = 1200;
            try { maxSingle = (connectedPeer != null) ? connectedPeer.GetMaxSinglePacketSize(METHOD) : maxSingle; } catch { }
            const int HEADER = 16;
            const int ENTRY = 24;

            int budget = Math.Max(256, maxSingle - HEADER);
            int perPacket = Math.Max(1, budget / ENTRY);

            for (int i = 0; i < list.Count; i += perPacket)
            {
                int n = Math.Min(perPacket, list.Count - i);

                writer.Reset();
                writer.Put((byte)Op.AI_ANIM_SNAPSHOT);
                writer.Put(n);
                for (int j = 0; j < n; ++j)
                {
                    var e = list[i + j];
                    writer.Put(e.id);
                    writer.Put(e.st.speed);
                    writer.Put(e.st.dirX);
                    writer.Put(e.st.dirY);
                    writer.Put(e.st.hand);
                    writer.Put(e.st.gunReady);
                    writer.Put(e.st.dashing);
                }
                if (transport != null) transport.Broadcast(writer.CopyData(), METHOD == DeliveryMethod.ReliableOrdered); else netManager?.SendToAll(writer, METHOD);
            }
        }



        private bool Client_ApplyAiAnim(int id, AiAnimState st)
        {
            if (aiById.TryGetValue(id, out var cmc) && cmc)
            {
                if (!IsRealAI(cmc)) return false;  // 
                // AI NetAiFollower RemoteReplicaTag MagicBlend.Update 
                var follower = cmc.GetComponent<NetAiFollower>();
                if (!follower) follower = cmc.gameObject.AddComponent<NetAiFollower>();
                if (!cmc.GetComponent<RemoteReplicaTag>()) cmc.gameObject.AddComponent<RemoteReplicaTag>();

                follower.SetAnim(st.speed, st.dirX, st.dirY, st.hand, st.gunReady, st.dashing);
                return true;
            }
            return false;
        }

        public bool IsRealAI(CharacterMainControl cmc)
        {
            if (cmc == null) return false;

            // 
            if (cmc == CharacterMainControl.Main)
                return false;

            if (cmc.Team == Teams.player)
            {
                return false;
            }

            var lm = LevelManager.Instance;
            if (lm != null)
            {
                if (cmc == lm.PetCharacter) return false;
                if (lm.PetProxy != null && cmc.gameObject == lm.PetProxy.gameObject) return false;
            }

            // Player remoteCharacters 
            foreach (var go in remoteCharacters.Values)
            {
                if (go != null && cmc.gameObject == go)
                    return false;
            }
            foreach (var go in clientRemoteCharacters.Values)
            {
                if (go != null && cmc.gameObject == go)
                    return false;
            }

            return true;
        }


        // moved to Mod.AI.cs: NormalizePrefabName

        static CharacterModel FindCharacterModelByName_Any(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            name = NormalizePrefabName(name);

            // A. prefab Scene 
            // FindObjectsOfTypeAll / 
            foreach (var m in Resources.FindObjectsOfTypeAll<CharacterModel>())
            {
                if (!m) continue;
                if (m.gameObject.scene.IsValid()) continue; // 
                if (string.Equals(NormalizePrefabName(m.name), name, StringComparison.OrdinalIgnoreCase))
                    return m;
            }

            // B. Resources Addressables 
            try
            {
                foreach (var m in Resources.LoadAll<CharacterModel>(""))
                {
                    if (!m) continue;
                    if (string.Equals(NormalizePrefabName(m.name), name, StringComparison.OrdinalIgnoreCase))
                        return m;
                }
            }
            catch { /* Project may not include Resources; ignore */ }

            // C. 
            foreach (var m in UnityEngine.GameObject.FindObjectsOfType<CharacterModel>())
            {
                if (!m) continue;
                if (string.Equals(NormalizePrefabName(m.name), name, StringComparison.OrdinalIgnoreCase))
                    return m;
            }

            return null;
        }

        // moved to Mod.AI.cs: ApplyFaceJsonToModel

        // moved to Mod.AI.cs: ReapplyFaceIfKnown


        private readonly HashSet<int> _nameIconSealed = new HashSet<int>();

        // Level 
        private void Client_ResetNameIconSeal_OnLevelInit()
        {
            if (!IsServer) _nameIconSealed.Clear();
            if (IsServer) return;
            foreach (var tag in GameObject.FindObjectsOfType<NetAiTag>())
            {
                var cmc = tag ? tag.GetComponent<CharacterMainControl>() : null;
                if (!cmc) { Destroy(tag); continue; }
                if (!IsRealAI(cmc)) Destroy(tag);
            }
        }

        // moved to Mod.AI.cs: ResolveIconSprite

        // moved to Mod.AI.cs: RefreshNameIconWithRetries

        // Host icon 
        private readonly HashSet<int> _iconRebroadcastScheduled = new HashSet<int>();

        private void Server_TryRebroadcastIconLater(int aiId, CharacterMainControl cmc)
        {
            if (!IsServer || aiId == 0 || !cmc) return;
            if (!_iconRebroadcastScheduled.Add(aiId)) return; // 

            StartCoroutine(IconRebroadcastRoutine(aiId, cmc));
        }

        private IEnumerator IconRebroadcastRoutine(int aiId, CharacterMainControl cmc)
        {
            yield return new WaitForSeconds(0.6f); // UIStyle/ 

            try
            {
                if (!IsServer || !cmc) yield break;

                var pr = cmc.characterPreset;
                int iconType = 0;
                bool showName = false;

                if (pr)
                {
                    try { iconType = (int)FR_IconType(pr); } catch { }
                    try
                    {
                        // icon 
                        if (iconType == 0 && pr.GetCharacterIcon() != null)
                            iconType = (int)FR_IconType(pr);
                    }
                    catch { }
                }

                var e = (global::CharacterIconTypes)iconType;
                if (e == global::CharacterIconTypes.boss || e == global::CharacterIconTypes.elete)
                    showName = true;

                // none 
                if (iconType != 0 || showName)
                    Server_BroadcastAiNameIcon(aiId, cmc);
            }
            finally { _iconRebroadcastScheduled.Remove(aiId); }
        }



        /// <summary>
        /// /////////////AI //////////////AI //////////////AI //////////////AI //////////////AI ////////
        /// </summary>

        public void Server_BroadcastAiHealth(int aiId, float maxHealth, float currentHealth)
        {
            if (!networkStarted || !IsServer) return;
            var w = new NetDataWriter();
            w.Put((byte)Op.AI_HEALTH_SYNC);
            w.Put(aiId);
            w.Put(maxHealth);
            w.Put(currentHealth);
            TransportBroadcast(w, true);
        }



        private void Client_ApplyAiHealth(int aiId, float max, float cur)
        {
            if (IsServer) return;

            // AI max/cur RegisterAi 
            if (!aiById.TryGetValue(aiId, out var cmc) || !cmc)
            {
                _cliPendingAiHealth[aiId] = cur;
                if (max > 0f) _cliPendingAiMax[aiId] = max;
                if (LogAiHpDebug) Debug.Log($"[AI-HP][CLIENT] pending aiId={aiId} max={max} cur={cur}");
                return;
            }

            var h = cmc.Health;
            if (!h) return;

            try
            {
                float prev = 0f;
                _cliLastAiHp.TryGetValue(aiId, out prev);
                _cliLastAiHp[aiId] = cur;

                float delta = prev - cur;                     // 
                if (delta > 0.01f)
                {
                    var pos = cmc.transform.position + Vector3.up * 1.1f;
                    var di = new global::DamageInfo();
                    di.damagePoint = pos;
                    di.damageNormal = Vector3.up;
                    di.damageValue = delta;
                    // finalDamage A 
                    try { di.finalDamage = delta; } catch { }
                    LocalHitKillFx.PopDamageText(pos, di);
                }
            }
            catch { }

            // / Max max 
            if (max > 0f)
            {
                _cliAiMaxOverride[h] = max;
                // defaultMaxHealth OnMaxHealthChange item stat 
                try { FI_defaultMax?.SetValue(h, Mathf.RoundToInt(max)); } catch { }
                try { FI_lastMax?.SetValue(h, -12345f); } catch { }
                try { h.OnMaxHealthChange?.Invoke(h); } catch { }
            }

            // client Max get_MaxHealth Harmony max 
            float nowMax = 0f; try { nowMax = h.MaxHealth; } catch { }

            // SetHealth() Max cur>nowMax _currentHealth 
            if (nowMax > 0f && cur > nowMax + 0.0001f)
            {
                try { FI__current?.SetValue(h, cur); } catch { }
                try { h.OnHealthChange?.Invoke(h); } catch { }
            }
            else
            {
                // 
                try { h.SetHealth(Mathf.Max(0f, cur)); } catch { try { FI__current?.SetValue(h, Mathf.Max(0f, cur)); } catch { } }
                try { h.OnHealthChange?.Invoke(h); } catch { }
            }

            // 
            try { h.showHealthBar = true; } catch { }
            try { h.RequestHealthBar(); } catch { }

            // AI 
            if (cur <= 0f)
            {
                try
                {
                    var ai = cmc.GetComponent<AICharacterController>();
                    if (ai) ai.enabled = false;

                    // / 
                    try
                    {
                        var miGet = AccessTools.DeclaredMethod(typeof(HealthBarManager), "GetActiveHealthBar", new[] { typeof(global::Health) });
                        var hb = miGet?.Invoke(HealthBarManager.Instance, new object[] { h }) as Duckov.UI.HealthBar;
                        if (hb != null)
                        {
                            var miRel = AccessTools.DeclaredMethod(typeof(global::Duckov.UI.HealthBar), "Release", Type.EmptyTypes);
                            if (miRel != null) miRel.Invoke(hb, null);
                            else hb.gameObject.SetActive(false);
                        }
                    }
                    catch { }

                    cmc.gameObject.SetActive(false);
                }
                catch { }


                if (_cliAiDeathFxOnce.Add(aiId))
                    Client_PlayAiDeathFxAndSfx(cmc);
            }
        }


        public void Server_OnDeadLootboxSpawned(InteractableLootbox box)
        {
            if (!IsServer || box == null) return;
            try
            {
                int lootUid = _nextLootUid++;
                var inv = box.Inventory;
                if (inv) _srvLootByUid[lootUid] = inv;

                // AddItem 
                if (inv) Server_MuteLoot(inv, 2.0f);

                writer.Reset();
                writer.Put((byte)Op.DEAD_LOOT_SPAWN);
                writer.Put(UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex);
                writer.PutV3cm(box.transform.position);
                writer.PutQuaternion(box.transform.rotation);
                TransportBroadcast(writer, true);

                // 2) Client 
                // eager broadcast disabled; removed dead branch
            }
            catch (System.Exception e)
            {
                Debug.LogError("[LOOT] Server_OnDeadLootboxSpawned failed: " + e);
            }
        }


        private void SpawnDeadLootboxAt(int aiId, int lootUid, Vector3 pos, Quaternion rot)
        {
            try
            {
                TryClientRemoveNearestAICorpse(pos, 3.0f);

                var prefab = GetDeadLootPrefabOnClient(aiId);
                if (!prefab) { Debug.LogWarning("[LOOT] DeadLoot prefab not found on client, spawn aborted."); return; }

                var go = UnityEngine.Object.Instantiate(prefab, pos, rot);
                var box = go ? go.GetComponent<InteractableLootbox>() : null;
                if (!box) return;

                var inv = box.Inventory;
                if (!inv) { Debug.LogWarning("[Client DeadLootBox Spawn] Inventory is null!"); return; }

                WorldLootPrime.PrimeIfClient(box);

                // Host pos posKey inv 
                var dict = InteractableLootbox.Inventories;
                if (dict != null)
                {
                    int correctKey = ComputeLootKeyFromPos(pos);
                    int wrongKey = -1;
                    foreach (var kv in dict)
                        if (kv.Value == inv && kv.Key != correctKey) { wrongKey = kv.Key; break; }
                    if (wrongKey != -1) dict.Remove(wrongKey);
                    dict[correctKey] = inv;
                }

                // ID inv
                if (lootUid >= 0) _cliLootByUid[lootUid] = inv;

                // 
                if (lootUid >= 0 && _pendingLootStatesByUid.TryGetValue(lootUid, out var pack))
                {
                    _pendingLootStatesByUid.Remove(lootUid);

                    _applyingLootState = true;
                    try
                    {
                        int cap = Mathf.Clamp(pack.capacity, 1, 128);
                        inv.Loading = true;               // 
                        inv.SetCapacity(cap);

                        for (int i = inv.Content.Count - 1; i >= 0; --i)
                        {
                            Item removed; inv.RemoveAt(i, out removed);
                            try { if (removed) UnityEngine.Object.Destroy(removed.gameObject); } catch { }
                        }
                        foreach (var (p, snap) in pack.Item2)
                        {
                            var item = BuildItemFromSnapshot(snap);
                            if (item) inv.AddAt(item, p);
                        }
                    }
                    finally
                    {
                        inv.Loading = false;              // 
                        _applyingLootState = false;
                    }
                    WorldLootPrime.PrimeIfClient(box);
                    return; // 
                }

                // Status + 
                Client_RequestLootState(inv);
                StartCoroutine(ClearLootLoadingTimeout(inv, 1.5f));
            }
            catch (System.Exception e)
            {
                Debug.LogError("[LOOT] SpawnDeadLootboxAt failed: " + e);
            }
        }


        private GameObject GetDeadLootPrefabOnClient(int aiId)
        {
            // 1) CMC private deadLootBoxPrefab
            try
            {
                if (aiId > 0 && aiById.TryGetValue(aiId, out var cmc) && cmc)
                {
                    Debug.LogWarning($"[SpawnDeadloot] AiID:{cmc.GetComponent<NetAiTag>().aiId}");
                    if (cmc.deadLootBoxPrefab.gameObject == null)
                    {
                        Debug.LogWarning("[SPawnDead] deadLootBoxPrefab.gameObject null!");
                    }


                    if (cmc != null)
                    {
                        var obj = cmc.deadLootBoxPrefab.gameObject;
                        if (obj) return obj;
                    }
                    else
                    {
                        Debug.LogWarning("[SPawnDead] cmc is null!");
                    }
                }
            }
            catch { }

            // 2) Main CMC 
            try
            {
                var main = CharacterMainControl.Main;
                if (main)
                {
                    var obj = main.deadLootBoxPrefab.gameObject;
                    if (obj) return obj;
                }
            }
            catch { }

            try
            {
                var any = UnityEngine.GameObject.FindObjectOfType<CharacterMainControl>();
                if (any)
                {
                    var obj = any.deadLootBoxPrefab.gameObject;
                    if (obj) return obj;
                }
            }
            catch { }
            return null;
        }
        public void Server_OnDeadLootboxSpawned(InteractableLootbox box, CharacterMainControl whoDied)
        {
            if (!IsServer || box == null) return;
            try
            {
                // ID 
                int lootUid = _nextLootUid++;
                var inv = box.Inventory;
                if (inv) _srvLootByUid[lootUid] = inv;

                int aiId = 0;
                if (whoDied)
                {
                    var tag = whoDied.GetComponent<NetAiTag>();
                    if (tag != null) aiId = tag.aiId;
                    if (aiId == 0) foreach (var kv in aiById) if (kv.Value == whoDied) { aiId = kv.Key; break; }
                }

                // >>> writer.Reset() <<<
                if (inv != null)
                {
                    inv.NeedInspection = true;
                    // 
                    try { Traverse.Create(inv).Field<bool>("hasBeenInspectedInLootBox").Value = false; } catch { }

                    // 
                    for (int i = 0; i < inv.Content.Count; ++i)
                    {
                        var it = inv.GetItemAt(i);
                        if (it) it.Inspected = false;
                    }
                }


                // ID
                writer.Reset();
                writer.Put((byte)Op.DEAD_LOOT_SPAWN);
                writer.Put(UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex);
                writer.Put(aiId);
                writer.Put(lootUid);                              // ID
                writer.PutV3cm(box.transform.position);
                writer.PutQuaternion(box.transform.rotation);
                TransportBroadcast(writer, true);

                // eager broadcast disabled; removed dead branch
            }
            catch (System.Exception e)
            {
                Debug.LogError("[LOOT] Server_OnDeadLootboxSpawned failed: " + e);
            }
        }

        // Removed dead coroutine (eager broadcast)

        


        private void Server_PeriodicNameIconSync()
        {
            foreach (var kv in aiById)  // aiId -> cmc
            {
                int aiId = kv.Key;
                var cmc = kv.Value;
                if (!cmc) continue;

                var pr = cmc.characterPreset;
                if (!pr) continue;

                int iconType = 0;
                bool showName = false;

                try { iconType = (int)FR_IconType(pr); } catch { }
                try { showName = pr.showName; } catch { }

                var e = (global::CharacterIconTypes)iconType;
                // boss/elete Client 
                if (!showName && (e == global::CharacterIconTypes.boss || e == global::CharacterIconTypes.elete))
                    showName = true;

                // 
                if (e != global::CharacterIconTypes.none || showName)
                {
                    if (LogAiLoadoutDebug)
                        UnityEngine.Debug.Log($"[AI-REBROADCAST-10s] aiId={aiId} icon={e} showName={showName}");
                    Server_BroadcastAiLoadout(aiId, cmc); // 
                }
            }
        }

        // Client preset / 
        private void Client_PeriodicNameIconRefresh()
        {
            foreach (var kv in aiById)
            {
                var cmc = kv.Value;
                if (!cmc) continue;

                var pr = cmc.characterPreset;
                if (!pr) continue;

                int iconType = 0;
                bool showName = false;
                string displayName = null;

                try { iconType = (int)FR_IconType(pr); } catch { }
                try { showName = pr.showName; } catch { }
                try { displayName = pr.DisplayName; } catch { }

                var e = (global::CharacterIconTypes)iconType;
                if (!showName && (e == global::CharacterIconTypes.boss || e == global::CharacterIconTypes.elete))
                    showName = true;

                // 
                if (e == global::CharacterIconTypes.none && !showName) continue;

                // HealthBar RefreshCharacterIcon()
                RefreshNameIconWithRetries(cmc, iconType, showName, displayName).Forget();
            }
        }

        // moved to Mod.AI.cs: Server_BroadcastAiNameIcon


        
        



        // inv Lootbox false
        

        // lootbox Host 
        

        // 
        

        // LootView 
        

        

        


        private readonly System.Collections.Generic.Dictionary<uint, (Inventory inv, int pos)> _cliPendingReorder
    = new System.Collections.Generic.Dictionary<uint, (Inventory inv, int pos)>();

        public void NoteLootReorderPending(uint token, Inventory inv, int targetPos)
        {
            if (token != 0 && inv) _cliPendingReorder[token] = (inv, targetPos);
        }

        private static bool TryGetLootInvByKeyEverywhere(int posKey, out Inventory inv)
        {
            inv = null;

            // A) InteractableLootbox.Inventories
            var dictA = InteractableLootbox.Inventories;
            if (dictA != null && dictA.TryGetValue(posKey, out inv) && inv) return true;

            // B) LevelManager.LootBoxInventories
            try
            {
                var lm = LevelManager.Instance;
                var dictB = lm != null ? LevelManager.LootBoxInventories : null;
                if (dictB != null && dictB.TryGetValue(posKey, out inv) && inv)
                {
                    // A 
                    try { if (dictA != null) dictA[posKey] = inv; } catch { }
                    return true;
                }
            }
            catch { }

            inv = null;
            return false;
        }

        private readonly Dictionary<int, float> _cliLastAiHp = new Dictionary<int, float>();

        // UI 
        static void TryShowDamageBarUI(Health h, float damage)
        {
            if (h == null || damage <= 0f) return;

            try
            {
                // 1) HealthBar
                var hbm = HealthBarManager.Instance;
                if (hbm == null) return;

                var miGet = AccessTools.DeclaredMethod(typeof(HealthBarManager), "GetActiveHealthBar", new[] { typeof(global::Health) });
                var hb = miGet?.Invoke(hbm, new object[] { h });
                if (hb == null) return;

                // 2) fill rect 
                var fiFill = AccessTools.Field(typeof(global::Duckov.UI.HealthBar), "fill");
                var fillImg = fiFill?.GetValue(hb) as UnityEngine.UI.Image;
                float width = 0f;
                if (fillImg != null)
                {
                    // rect 
                    width = fillImg.rectTransform.rect.width;
                }

                // 3) 
                // - minPixels: 
                // - minPercent: 
                const float minPixels = 2f;
                const float minPercent = 0.0015f; // 0.15%

                float maxHp = Mathf.Max(1f, h.MaxHealth);
                float minByPixels = (width > 0f) ? (minPixels / width) * maxHp : 0f;
                float minByPercent = minPercent * maxHp;
                float minDamageToShow = Mathf.Max(minByPixels, minByPercent);

                // 4) or 
                float visualDamage = Mathf.Max(damage, minDamageToShow);

                // 5) HealthBar.ShowDamageBar(float)
                var miShow = AccessTools.DeclaredMethod(typeof(global::Duckov.UI.HealthBar), "ShowDamageBar", new[] { typeof(float) });
                miShow?.Invoke(hb, new object[] { visualDamage });
            }
            catch
            {
                // Failed UI 
            }
        }

        // Client AI pending cur max 
        private readonly Dictionary<int, float> _cliPendingAiMax = new Dictionary<int, float>();

        private readonly Dictionary<Health, float> _cliAiMaxOverride = new Dictionary<Health, float>();
        internal bool TryGetClientMaxOverride(Health h, out float v) => _cliAiMaxOverride.TryGetValue(h, out v);

        private void Client_HookSelfHealth()
        {
            if (_cliHookedSelf) return;
            var main = CharacterMainControl.Main;
            var h = main ? main.GetComponentInChildren<Health>(true) : null;
            if (!h) return;

            _cbSelfHpChanged = _ => Client_SendSelfHealth(h, force: false);
            _cbSelfMaxChanged = _ => Client_SendSelfHealth(h, force: true);
            _cbSelfHurt = di =>
            {
                _cliLastSelfHurtAt = Time.time;              // 
                try { _cliLastSelfHpLocal = h.CurrentHealth; } catch { }
                Client_SendSelfHealth(h, force: true);       // 20Hz 
            };
            _cbSelfDead = _ => Client_SendSelfHealth(h, force: true);

            h.OnHealthChange.AddListener(_cbSelfHpChanged);
            h.OnMaxHealthChange.AddListener(_cbSelfMaxChanged);
            h.OnHurtEvent.AddListener(_cbSelfHurt);
            h.OnDeadEvent.AddListener(_cbSelfDead);

            _cliHookedSelf = true;

            // 
            Client_SendSelfHealth(h, force: true);
        }

        private void Client_UnhookSelfHealth()
        {
            if (!_cliHookedSelf) return;
            var main = CharacterMainControl.Main;
            var h = main ? main.GetComponentInChildren<Health>(true) : null;
            if (h)
            {
                if (_cbSelfHpChanged != null) h.OnHealthChange.RemoveListener(_cbSelfHpChanged);
                if (_cbSelfMaxChanged != null) h.OnMaxHealthChange.RemoveListener(_cbSelfMaxChanged);
                if (_cbSelfHurt != null) h.OnHurtEvent.RemoveListener(_cbSelfHurt);
                if (_cbSelfDead != null) h.OnDeadEvent.RemoveListener(_cbSelfDead);
            }
            _cliHookedSelf = false;
            _cbSelfHpChanged = _cbSelfMaxChanged = null;
            _cbSelfHurt = _cbSelfDead = null;
        }

        // 20Hz & 
        private void Client_SendSelfHealth(Health h, bool force)
        {
            if (_cliApplyingSelfSnap || Time.time < _cliEchoMuteUntil) return;

            if (!networkStarted || IsServer || connectedPeer == null || h == null) return;

            float max = 0f, cur = 0f;
            try { max = h.MaxHealth; } catch { }
            try { cur = h.CurrentHealth; } catch { }

            // 
            if (!force && Mathf.Approximately(max, _cliLastSentHp.max) && Mathf.Approximately(cur, _cliLastSentHp.cur))
                return;

            // 20Hz
            if (!force && Time.time < _cliNextSendHp) return;

            var w = new NetDataWriter();
            w.Put((byte)Op.PLAYER_HEALTH_REPORT);
            w.Put(max);
            w.Put(cur);
            TransportSendToServer(w, true);

            _cliLastSentHp = (max, cur);
            _cliNextSendHp = Time.time + 0.05f;
        }

        public uint AllocateDropId()
        {
            uint id = nextDropId++;
            while (serverDroppedItems.ContainsKey(id))
                id = nextDropId++;
            return id;
        }

        private InteractableLootbox ResolveDeadLootPrefabOnServer()
        {
            var any = GameplayDataSettings.Prefabs;
            try
            {      
                if (any != null && any.LootBoxPrefab_Tomb != null) return any.LootBoxPrefab_Tomb;
            }
            catch { }

            if(any != null)
            {
                return any.LootBoxPrefab;
            }

            return null; // Client DEAD_LOOT_SPAWN 
        }


        public void Net_ReportPlayerDeadTree(CharacterMainControl who)
        {
            // Client Host 
            if (!networkStarted || IsServer || connectedPeer == null || who == null) return;

            var item = who.CharacterItem;            // 
            if (item == null) return;

            // / 
            var pos = who.transform.position;
            var rot = (who.characterModel ? who.characterModel.transform.rotation : who.transform.rotation);

            // 
            writer.Reset();
            writer.Put((byte)Op.PLAYER_DEAD_TREE);  
            writer.PutV3cm(pos);
            writer.PutQuaternion(rot);

            // 
            WriteItemSnapshot(writer, item);

            TransportSendToServer(writer, true);
        }

        

        // ====== Sans loot ( ) =======
        // ====== Sans loot ( ) =======
        // ====== Sans loot ( ) =======

        


        

        public void Server_HandlePlayerDeadTree(Vector3 pos, Quaternion rot, ItemSnapshot snap)
        {
            if (!IsServer) return;

            var tmpRoot = BuildItemFromSnapshot(snap);
            if (!tmpRoot) { Debug.LogWarning("[LOOT] HostDeath BuildItemFromSnapshot failed."); return; }

            var deadPfb = ResolveDeadLootPrefabOnServer();                     // LootBoxPrefab_Tomb
            var box = InteractableLootbox.CreateFromItem(tmpRoot, pos + Vector3.up * 0.10f, rot, true, deadPfb, false);
            if (box) Server_OnDeadLootboxSpawned(box, null);                   // whoDied=null aiId=0 Client Player 

            if (tmpRoot && tmpRoot.gameObject) UnityEngine.Object.Destroy(tmpRoot.gameObject);
        }

        // Host Client 
        public void Server_HandleHostDeathViaTree(CharacterMainControl who)
        {
            if (!networkStarted || !IsServer || !who) return;
            var item = who.CharacterItem;
            if (!item) return;

            var pos = who.transform.position;
            var rot = (who.characterModel ? who.characterModel.transform.rotation : who.transform.rotation);

            var snap = MakeSnapshot(item);                                     // WriteItemSnapshot 
            Server_HandlePlayerDeadTree(pos, rot, snap);
        }

        private ItemSnapshot MakeSnapshot(ItemStatsSystem.Item item)
        {
            ItemSnapshot s;
            s.typeId = item.TypeID;
            s.stack = item.StackCount;
            s.durability = item.Durability;
            s.durabilityLoss = item.DurabilityLoss;
            s.inspected = item.Inspected;
            s.slots = new System.Collections.Generic.List<(string, ItemSnapshot)>();
            s.inventory = new System.Collections.Generic.List<ItemSnapshot>();

            var slots = item.Slots;
            if (slots != null && slots.list != null)
            {
                foreach (var slot in slots.list)
                    if (slot != null && slot.Content != null)
                        s.slots.Add((slot.Key ?? string.Empty, MakeSnapshot(slot.Content)));
            }

            var invItems = TryGetInventoryItems(item.Inventory);             
            if (invItems != null)
                foreach (var child in invItems)
                    if (child != null) s.inventory.Add(MakeSnapshot(child));

            return s;
        }

        public int StableRootId(CharacterSpawnerRoot r)
        {
            if (r == null) return 0;
            if (r.SpawnerGuid != 0) return r.SpawnerGuid;

            // relatedScene Init 
            int sceneIndex = -1;
            try
            {
                var fi = typeof(CharacterSpawnerRoot).GetField("relatedScene", BindingFlags.NonPublic | BindingFlags.Instance);
                if (fi != null) sceneIndex = (int)fi.GetValue(r);
            }
            catch { }
            if (sceneIndex < 0) sceneIndex = UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex;

            // 0.1m 
            Vector3 p = r.transform.position;
            int qx = Mathf.RoundToInt(p.x * 10f);
            int qy = Mathf.RoundToInt(p.y * 10f);
            int qz = Mathf.RoundToInt(p.z * 10f);

            // + + FNV1a
            string key = $"{sceneIndex}:{r.name}:{qx},{qy},{qz}";
            return StableHash(key);
        }

        // Host Root guid id 
        public void Server_SendRootSeedDelta(CharacterSpawnerRoot r, NetPeer target = null)
        {
            if (!IsServer || r == null) return;

            int idA = StableRootId(r);      // SpawnerGuid
            int idB = StableRootId_Alt(r);  // guid + + 

            int seed = DeriveSeed(sceneSeed, idA);
            aiRootSeeds[idA] = seed;        // Host 

            var w = writer; w.Reset();
            w.Put((byte)Op.AI_SEED_PATCH);
            int count = (idA == idB) ? 1 : 2;
            w.Put(count);
            w.Put(idA); w.Put(seed);
            if (count == 2) { w.Put(idB); w.Put(seed); }

            if (target == null) BroadcastReliable(w);
            else TransportSend(target, w, true);
        }

        // Client / 
        void HandleAiSeedPatch(NetPacketReader r)
        {
            int n = r.GetInt();
            for (int i = 0; i < n; i++)
            {
                int id = r.GetInt();
                int seed = r.GetInt();
                aiRootSeeds[id] = seed;
            }
            Debug.Log("[AI-SEED] Applied incremental Root seeds: " + n);
        }

        static void EnsureMagicBlendBound(CharacterMainControl cmc)
        {
            if (!cmc) return;
            var model = cmc.characterModel;
            if (!model) return;

            var blend = model.GetComponent<CharacterAnimationControl_MagicBlend>();
            if (!blend) blend = model.gameObject.AddComponent<CharacterAnimationControl_MagicBlend>();

            if (cmc.GetGun() != null)
            {
                blend.animator.SetBool(Animator.StringToHash("GunReady"), true);
                Traverse.Create(blend).Field<ItemAgent_Gun>("gunAgent").Value = cmc.GetGun();
                Traverse.Create(blend).Field<DuckovItemAgent>("holdAgent").Value = cmc.GetGun();
            }

            if (cmc.GetMeleeWeapon() != null)
            {
                blend.animator.SetBool(Animator.StringToHash("GunReady"), true);
                Traverse.Create(blend).Field<DuckovItemAgent>("holdAgent").Value = cmc.GetMeleeWeapon();
            }

            blend.characterModel = model;
            blend.characterMainControl = cmc;

            if (!blend.animator || blend.animator == null)
                blend.animator = model.GetComponentInChildren<Animator>(true);

            var anim = blend.animator;
            if (anim)
            {
                anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                anim.updateMode = AnimatorUpdateMode.Normal;
                int idx = anim.GetLayerIndex("MeleeAttack");
                if (idx >= 0) anim.SetLayerWeight(idx, 0f);
            }
        }

        public void Server_ForceAuthSelf(Health h)
        {
            if (!networkStarted || !IsServer || h == null) return;
            if (!_srvHealthOwner.TryGetValue(h, out var ownerPeer) || ownerPeer == null) return;

            var w = writer; w.Reset();
            w.Put((byte)Op.AUTH_HEALTH_SELF);
            float max = 0f, cur = 0f;
            try { max = h.MaxHealth; cur = h.CurrentHealth; } catch { }
            w.Put(max);
            w.Put(cur);
            TransportSend(ownerPeer, w, true);
        }

        [ThreadStatic] public static bool _applyingDoor;  // Client Network 

        // Door.GetKey Key Door_{round(pos*10)} GetHashCode
        public int ComputeDoorKey(Transform t)
        {
            if (!t) return 0;
            var p = t.position * 10f;
            var k = new Vector3Int(
                Mathf.RoundToInt(p.x),
                Mathf.RoundToInt(p.y),
                Mathf.RoundToInt(p.z)
            );
            return $"Door_{k}".GetHashCode();
        }

        // key Door doorClosedDataKeyCached 
        private Door FindDoorByKey(int key)
        {
            if (key == 0) return null;
            var doors = UnityEngine.Object.FindObjectsOfType<Door>(true);
            var fCache = AccessTools.Field(typeof(Door), "doorClosedDataKeyCached");
            var mGetKey = AccessTools.Method(typeof(Door), "GetKey");

            foreach (var d in doors)
            {
                if (!d) continue;
                int k = 0;
                try { k = (int)fCache.GetValue(d); } catch { }
                if (k == 0)
                {
                    try { k = (int)mGetKey.Invoke(d, null); } catch { }
                }
                if (k == key) return d;
            }
            return null;
        }

        // Client closed/open
        public void Client_RequestDoorSetState(Door d, bool closed)
        {
            if (IsServer || connectedPeer == null || d == null) return;

            int key = 0;
            try
            {
                // Door.GetKey 
                key = (int)AccessTools.Field(typeof(Door), "doorClosedDataKeyCached").GetValue(d);
            }
            catch { }
            if (key == 0) key = ComputeDoorKey(d.transform);
            if (key == 0) return;

            var w = writer; w.Reset();
            w.Put((byte)Op.DOOR_REQ_SET);
            w.Put(key);
            w.Put(closed);
            TransportSendToServer(w, true);
        }

        // Host Client 
        private void Server_HandleDoorSetRequest(LiteNetLib.NetPeer peer, NetPacketReader reader)
        {
            if (!IsServer) return;
            int key = reader.GetInt();
            bool closed = reader.GetBool();

            var door = FindDoorByKey(key);
            if (!door) return;

            // API / / NavMesh
            if (closed) door.Close();
            else door.Open();
            // Postfix 
            // Server_BroadcastDoorState(key, closed);
        }

        // Host Status
        public void Server_BroadcastDoorState(int key, bool closed)
        {
            if (!IsServer) return;
            var w = writer; w.Reset();
            w.Put((byte)Op.DOOR_STATE);
            w.Put(key);
            w.Put(closed);
            TransportBroadcast(w, true);
        }

        // Client Status SetClosed NavMeshCut/ / 
        private void Client_ApplyDoorState(int key, bool closed)
        {
            if (IsServer) return;
            var door = FindDoorByKey(key);
            if (!door) return;

            try
            {
                _applyingDoor = true;

                var mSetClosed2 = AccessTools.Method(typeof(Door), "SetClosed",
                                   new[] { typeof(bool), typeof(bool) });
                if (mSetClosed2 != null)
                {
                    mSetClosed2.Invoke(door, new object[] { closed, true });
                }
                else
                {
                    if (closed)
                        AccessTools.Method(typeof(Door), "Close")?.Invoke(door, null);
                    else
                        AccessTools.Method(typeof(Door), "Open")?.Invoke(door, null);
                }
            }
            finally
            {
                _applyingDoor = false;
            }
        }

        // dangerFx 
        private readonly HashSet<uint> _dangerDestructibleIds = new HashSet<uint>();

        // Client ENV Switch to 
        private void Client_ApplyDestructibleDead_Snapshot(uint id)
        {
            if (_deadDestructibleIds.Contains(id)) return;
            var hs = FindDestructible(id);
            if (!hs) return;

            // Breakable / 
            var br = hs.GetComponent<Breakable>();
            if (br)
            {
                try
                {
                    if (br.normalVisual) br.normalVisual.SetActive(false);
                    if (br.dangerVisual) br.dangerVisual.SetActive(false);
                    if (br.breakedVisual) br.breakedVisual.SetActive(true);
                    if (br.mainCollider) br.mainCollider.SetActive(false);
                }
                catch { }
            }

            // HalfObsticle Dead 
            var half = hs.GetComponent<HalfObsticle>();
            if (half) { try { half.Dead(new DamageInfo()); } catch { } }

            // Collider
            try
            {
                foreach (var c in hs.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            }
            catch { }

            _deadDestructibleIds.Add(id);
        }

        private static Transform FindBreakableWallRoot(Transform t)
        {
            var p = t;
            while (p != null)
            {
                string nm = p.name;
                if (!string.IsNullOrEmpty(nm) &&
                    nm.IndexOf("BreakableWall", StringComparison.OrdinalIgnoreCase) >= 0)
                    return p;
                p = p.parent;
            }
            return null;
        }

        private static uint ComputeStableIdForDestructible(HealthSimpleBase hs)
        {
            if (!hs) return 0u;
            Transform root = FindBreakableWallRoot(hs.transform);
            if (root == null) root = hs.transform;
            try { return NetDestructibleTag.ComputeStableId(root.gameObject); }
            catch { return 0u; }
        }
        private void ScanAndMarkInitiallyDeadDestructibles()
        {
            if (_deadDestructibleIds == null) return;
            if (_serverDestructibles == null || _serverDestructibles.Count == 0) return;

            foreach (var kv in _serverDestructibles)
            {
                uint id = kv.Key;
                var hs = kv.Value;
                if (!hs) continue;
                if (_deadDestructibleIds.Contains(id)) continue;

                bool isDead = false;

                // 1) HP HSB HealthValue 
                try { if (hs.HealthValue <= 0f) isDead = true; } catch { }

                // 2) Breakable breaked / => 
                if (!isDead)
                {
                    try
                    {
                        var br = hs.GetComponent<Breakable>();
                        if (br)
                        {
                            bool brokenView = (br.breakedVisual && br.breakedVisual.activeInHierarchy);
                            bool mainOff = (br.mainCollider && !br.mainCollider.activeSelf);
                            if (brokenView || mainOff) isDead = true;
                        }
                    }
                    catch { }
                }

                // 3) HalfObsticle isDead 
                if (!isDead)
                {
                    try
                    {
                        var half = hs.GetComponent("HalfObsticle"); // 
                        if (half != null)
                        {
                            var t = half.GetType();
                            var fi = HarmonyLib.AccessTools.Field(t, "isDead");
                            if (fi != null)
                            {
                                object v = fi.GetValue(half);
                                if (v is bool && (bool)v) isDead = true;
                            }
                        }
                    }
                    catch {}
                }

                if (isDead) _deadDestructibleIds.Add(id);
            }
        }

        // Client AI true 
        public bool Client_ForceShowAllRemoteAI = true;

        static bool HasParam(Animator anim, string name, AnimatorControllerParameterType type)
        {
            foreach (var p in anim.parameters)
                if (p.name == name && p.type == type) return true;
            return false;
        }

        static void TrySetBool(Animator anim, string name, bool v)
        {
            if (HasParam(anim, name, AnimatorControllerParameterType.Bool))
                anim.SetBool(name, v);
        }

        static void TrySetInt(Animator anim, string name, int v)
        {
            if (HasParam(anim, name, AnimatorControllerParameterType.Int))
                anim.SetInteger(name, v);
        }

        static void TrySetFloat(Animator anim, string name, float v)
        {
            if (HasParam(anim, name, AnimatorControllerParameterType.Float))
                anim.SetFloat(name, v);
        }

        static void TryTrigger(Animator anim, string name)
        {
            if (HasParam(anim, name, AnimatorControllerParameterType.Trigger))
                anim.SetTrigger(name);
        }

        private void Client_ApplyFaceIfAvailable(string playerId, GameObject instance, string faceOverride = null)
        {
            try
            {
                // JSON
                string face = faceOverride;
                if (string.IsNullOrEmpty(face))
                {
                    if (_cliPendingFace.TryGetValue(playerId, out var pf) && !string.IsNullOrEmpty(pf))
                        face = pf;
                    else if (clientPlayerStatuses.TryGetValue(playerId, out var st) && !string.IsNullOrEmpty(st.CustomFaceJson))
                        face = st.CustomFaceJson;
                }

                // JSON Status 
                if (string.IsNullOrEmpty(face))
                    return;

                // struct null 
                var data = JsonUtility.FromJson<CustomFaceSettingData>(face);

                // CustomFaceInstance 
                var cm = instance != null ? instance.GetComponentInChildren<CharacterModel>(true) : null;
                var cf = cm != null ? cm.CustomFace : null;
                if (cf != null)
                {
                    HardApplyCustomFace(cf, data);
                    _cliPendingFace[playerId] = face; // Succeeded JSON
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[COOP][FACE] Apply failed for {playerId}: {e}");
            }
        }

        static void HardApplyCustomFace(CustomFaceInstance cf, in CustomFaceSettingData data)
        {
            if (cf == null) return;
            try { StripAllCustomFaceParts(cf.gameObject); } catch { }
            try { cf.LoadFromData(data); } catch { }
            try { cf.RefreshAll(); } catch { }
        }

        static void StripAllCustomFaceParts(GameObject root)
        {
            try
            {
                var all = root.GetComponentsInChildren<global::CustomFacePart>(true);
                int n = 0;
                foreach (var p in all)
                {
                    if (!p) continue;
                    n++;
                    UnityEngine.Object.Destroy(p.gameObject);
                }
                 Debug.Log($"[COOP][FACE] stripped {n} CustomFacePart");
            }
            catch {  }
        }

        private readonly HashSet<int> _cliAiDeathFxOnce = new HashSet<int>();

        private void Client_PlayAiDeathFxAndSfx(CharacterMainControl cmc)
        {
            if (!cmc) return;
            var model = cmc.characterModel;
            if (!model) return;
          
            object hv = null;
            try
            {
                var fi = model.GetType().GetField("hurtVisual",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (fi != null) hv = fi.GetValue(model);
            }
            catch { }

            if (hv == null)
            {
                try { hv = model.GetComponentInChildren(typeof(global::HurtVisual), true); } catch { }
            }

            if (hv != null)
            {
                try
                {
                    var miDead = hv.GetType().GetMethod("OnDead",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (miDead != null)
                    {
                        var di = new global::DamageInfo
                        {
                            // OnDead DamageInfo 
                            damagePoint = cmc.transform.position,
                            damageNormal = Vector3.up
                        };
                        miDead.Invoke(hv, new object[] { di });

                        if (FmodEventExists("event:/e_KillMarker"))
                        {
                            AudioManager.Post("e_KillMarker");
                        }
                    }
                }
                catch {  }
            }
        }

        private void Client_EnsureSelfDeathEvent(global::Health h, global::CharacterMainControl cmc)
        {
            if (!h || !cmc) return;

            float cur = 1f;
            try { cur = h.CurrentHealth; } catch { }

            // > 0 / 
            if (cur > 1e-3f)
            {
                _cliSelfDeathFired = false;
                _cliCorpseTreeReported = false;      // 
                _cliInEnsureSelfDeathEmit = false;   // 
                return;
            }

            // OnDead
            if (_cliSelfDeathFired) return;

            try
            {
                var di = new global::DamageInfo
                {
                    isFromBuffOrEffect = false,
                    damageValue = 0f,
                    finalDamage = 0f,
                    damagePoint = cmc.transform.position,
                    damageNormal = UnityEngine.Vector3.up,
                    fromCharacter = null
                };

                // OnDead 
                _cliInEnsureSelfDeathEmit = true;

                // -> CharacterMainControl.OnDead(.)
                h.OnDeadEvent?.Invoke(di);

                _cliSelfDeathFired = true;
            }
            finally
            {
                _cliInEnsureSelfDeathEmit = false; // 
            }
        }


        // Host Client 
        //private void Server_HandleLootSlotUnplugRequest(NetPeer peer, NetPacketReader r)
        //{
        // int scene = r.GetInt(); int posKey = r.GetInt(); int iid = r.GetInt(); int lootUid = r.GetInt();
        // int weaponPos = r.GetInt(); int slotIndex = r.GetInt(); // Client 

        // Inventory inv = null;
        // if (lootUid >= 0) _srvLootByUid.TryGetValue(lootUid, out inv);
        // if (inv == null && !TryResolveLootById(scene, posKey, iid, out inv)) { Server_SendLootDeny(peer, "no_inv"); return; }
        // if (LootboxDetectUtil.IsPrivateInventory(inv)) { Server_SendLootDeny(peer, "no_inv"); return; } // PUT/TAKE 

        // var weapon = inv.GetItemAt(weaponPos); // 
        // if (!weapon) { Server_SendLootDeny(peer, "bad_weapon"); return; }

        // bool ok = false;
        // _serverApplyingLoot = true;
        // try
        // {
        // // Unplug inv AddAndMerge 
        // var slots = weapon.Slots;
        // var slot = slots != null ? slots[slotIndex] : null;
        // var removed = slot != null ? slot.Unplug() : null;
        // if (removed) ok = ItemUtilities.AddAndMerge(inv, removed, 0);
        // }
        // catch (Exception ex) { Debug.LogError($"[LOOT][UNPLUG] {ex}"); }
        // finally { _serverApplyingLoot = false; }

        // if (!ok) { Server_SendLootDeny(peer, "slot_unplug_fail"); Server_SendLootboxState(peer, inv); return; }
        // Server_SendLootboxState(null, inv); // 
        //}

        private void Server_HandleLootSlotPlugRequest(NetPeer peer, NetPacketReader r)
        {
            // 1) 
            int scene = r.GetInt();
            int posKey = r.GetInt();
            int iid = r.GetInt();
            int lootUid = r.GetInt();
            var inv = ResolveLootInv(scene, posKey, iid, lootUid);
            if (inv == null || LootboxDetectUtil.IsPrivateInventory(inv)) { Server_SendLootDeny(peer, "no_inv"); return; }

            // 2) + 
            var master = ReadItemRef(r, inv);
            string slotKey = r.GetString();
            if (!master) { Server_SendLootDeny(peer, "bad_weapon"); Server_SendLootboxState(peer, inv); return; }
            var dstSlot = master?.Slots?.GetSlot(slotKey);
            if (dstSlot == null) { Server_SendLootDeny(peer, "bad_slot"); Server_SendLootboxState(peer, inv); return; }

            // 3) 
            bool srcInLoot = r.GetBool();
            Item srcItem = null;
            uint token = 0;
            ItemSnapshot snap = default;

            if (srcInLoot)
            {
                 srcItem = ReadItemRef(r, inv);
                if (!srcItem)
                {
                    Server_SendLootDeny(peer, "bad_src");
                    Server_SendLootboxState(peer, inv);   // Client 
                    return;
                }
            }
            else
            {
                token = r.GetUInt();
                snap = ReadItemSnapshot(r);
            }

            // 4) 
            _serverApplyingLoot = true;
            bool ok = false;
            Item unplugged = null;
            try
            {
                Item child = srcItem;
                if (!srcInLoot)
                {
                    // snapshot 
                    child = BuildItemFromSnapshot(snap);
                    if (!child)
                    {
                        Server_SendLootDeny(peer, "build_fail");
                        Server_SendLootboxState(peer, inv);
                        return;
                    }
                }
                else
                {
                    // / 
                    try { child.Detach(); } catch { }
                }

                ok = dstSlot.Plug(child, out unplugged);

                if (ok)
                {
                    // 
                    if (!srcInLoot)
                    {
                        var ack = new NetDataWriter();
                        ack.Put((byte)Op.LOOT_PUT_OK);  // PUT OK 
                        ack.Put(token);
                        TransportSend(peer, ack, true);
                    }

                    // 
                    Server_SendLootboxState(null, inv);
                }
                else
                {
                    Server_SendLootDeny(peer, "slot_plug_fail");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[LOOT][PLUG] {ex}");
                ok = false;
            }
            finally { _serverApplyingLoot = false; }

            if (!ok)
            {
                // snapshot child 
                if (!srcInLoot) { try { /* child remains in memory when Plug Failed */ } catch { } }
                Server_SendLootDeny(peer, "plug_fail");
                Server_SendLootboxState(peer, inv);
                return;
            }

            // 
            if (unplugged)
            {
                if (!ItemUtilities.AddAndMerge(inv, unplugged, 0))
                {
                    try { if (unplugged) UnityEngine.Object.Destroy(unplugged.gameObject); } catch { }
                }
            }

            // (B) Player LOOT_PUT_OK 
            if (!srcInLoot && token != 0)
            {
                var w2 = new NetDataWriter();
                w2.Put((byte)Op.LOOT_PUT_OK);
                w2.Put(token);
                TransportSend(peer, w2, true);
            }

            // 5) Status
            Server_SendLootboxState(null, inv);
        }

        private uint _cliLocalToken
        {
            get => _nextLootToken;
            set => _nextLootToken = value;
        }

        public void Client_RequestLootSlotPlug(Inventory inv, Item master, string slotKey, Item child)
        {
            if (!networkStarted || IsServer || connectedPeer == null) return;

            var w = new NetDataWriter();
            w.Put((byte)Op.LOOT_REQ_SLOT_PLUG);

            // 
            PutLootId(w, inv);
            WriteItemRef(w, inv, master);
            w.Put(slotKey);

            bool srcInLoot = LootboxDetectUtil.IsLootboxInventory(child ? child.InInventory : null);
            w.Put(srcInLoot);

            if (srcInLoot)
            {
                // Item 
                WriteItemRef(w, child.InInventory, child);
            }
            else
            {
                // token + 
                uint token = ++_cliLocalToken;           // token / 
                _cliPendingSlotPlug[token] = child;
                w.Put(token);
                WriteItemSnapshot(w, child);
            }

            TransportSendToServer(w, true);
        }





        


        // inv item
        


        // Inventory ID 
        
        

        

        

        

        private static readonly Collider[] _corpseScanBuf = new Collider[64];
        private const QueryTriggerInteraction QTI = QueryTriggerInteraction.Collide;
        private const int LAYER_MASK_ANY = ~0;


        private void TryClientRemoveNearestAICorpse(Vector3 pos, float radius)
        {
            if (!networkStarted || IsServer) return;

            try
            {
                CharacterMainControl best = null;
                float bestSqr = radius * radius;

                // 1) aiById O(n) 
                try
                {
                    foreach (var kv in aiById)
                    {
                        var cmc = kv.Value;
                        if (!cmc || cmc.IsMainCharacter) continue;

                        bool isAI = cmc.GetComponent<AICharacterController>() != null
                                 || cmc.GetComponent<NetAiTag>() != null;
                        if (!isAI) continue;

                        var p = cmc.transform.position; p.y = 0f;
                        var q = pos; q.y = 0f;
                        float d2 = (p - q).sqrMagnitude;
                        if (d2 < bestSqr) { best = cmc; bestSqr = d2; }
                    }
                }
                catch { }

                // 2) GC 
                if (!best)
                {
                    int n = Physics.OverlapSphereNonAlloc(pos, radius, _corpseScanBuf, LAYER_MASK_ANY, QTI);
                    for (int i = 0; i < n; i++)
                    {
                        var c = _corpseScanBuf[i];
                        if (!c) continue;

                        var cmc = c.GetComponentInParent<CharacterMainControl>();
                        if (!cmc || cmc.IsMainCharacter) continue;

                        bool isAI = cmc.GetComponent<AICharacterController>() != null
                                 || cmc.GetComponent<NetAiTag>() != null;
                        if (!isAI) continue;

                        var p = cmc.transform.position; p.y = 0f;
                        var q = pos; q.y = 0f;
                        float d2 = (p - q).sqrMagnitude;
                        if (d2 < bestSqr) { best = cmc; bestSqr = d2; }
                    }
                }

      
                if (best)
                {
                    DamageInfo DamageInfo = new DamageInfo { armorBreak = 999f, damageValue = 9999f, fromWeaponItemID = CharacterMainControl.Main.CurrentHoldItemAgent.Item.TypeID, damageType = DamageTypes.normal, fromCharacter = CharacterMainControl.Main, finalDamage = 9999f, toDamageReceiver = best.mainDamageReceiver };
                    EXPManager.AddExp(Traverse.Create(best.Health).Field<Item>("item").Value.GetInt("Exp", 0));
                    
                    // lol

                    //best.Health.Hurt(DamageInfo);
                    best.Health.OnDeadEvent.Invoke(DamageInfo);
                    TryFireOnDead(best.Health, DamageInfo);

                    try
                    {
                        var tag = best.GetComponent<NetAiTag>();
                        if (tag != null)
                        {
                            if (_cliAiDeathFxOnce.Add(tag.aiId))
                                Client_PlayAiDeathFxAndSfx(best);
                        }
                    }
                    catch {  }

                    UnityEngine.Object.Destroy(best.gameObject);
                }
            }
            catch { }
        }

        public static bool TryFireOnDead(Health health, DamageInfo di)
        {
            try
            {
                // OnDead static event<Action<Health, DamageInfo>>
                var fi = AccessTools.Field(typeof(Health), "OnDead");
                if (fi == null)
                {
                    UnityEngine.Debug.LogError("[HEALTH] OnDead field not found (possible custom add/remove event)");
                    return false;
                }

                var del = fi.GetValue(null) as Action<Health, DamageInfo>;
                if (del == null)
                {
                    // 
                    return false;
                }

                del.Invoke(health, di);
                return true;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[HEALTH] Trigger OnDead Failed: " + e);
                return false;
            }
        }


        public bool TryEnterSpectatorOnDeath(global::DamageInfo dmgInfo)
        {
            var main = CharacterMainControl.Main;
            if (!LevelManager.LevelInited || main == null) return false;

            BuildSpectateList(exclude: main);
            Debug.Log("Spectating: " + _spectateList.Count); 

            if (_spectateList.Count <= 0) return false;     // -> 

            _lastDeathInfo = dmgInfo;
            _spectatorActive = true;
            _spectateIdx = 0;

            if (GameCamera.Instance) GameCamera.Instance.SetTarget(_spectateList[_spectateIdx]);

            if (sceneVoteActive)
                _spectatorEndOnVotePending = true;

            return true; // 
        }

        private void BuildSpectateList(CharacterMainControl exclude)
        {
            _spectateList.Clear();

            string mySceneId = localPlayerStatus != null ? localPlayerStatus.SceneId : null;
            if (string.IsNullOrEmpty(mySceneId))
                ComputeIsInGame(out mySceneId);
            var myK = CanonicalizeSceneId(mySceneId);

            int cand = 0, kept = 0;

            if (IsServer)
            {
                foreach (var kv in remoteCharacters)
                {
                    var go = kv.Value;
                    var cmc = go ? go.GetComponent<CharacterMainControl>() : null;
                    if (!IsAlive(cmc) || cmc == exclude) continue;
                    cand++;

                    string peerScene = null;
                    if (!_srvPeerScene.TryGetValue(kv.Key, out peerScene) && playerStatuses.TryGetValue(kv.Key, out var pst))
                        peerScene = pst?.SceneId;

                    if (AreSameMap(mySceneId, peerScene))
                    {
                        _spectateList.Add(cmc);
                        kept++;
                    }
                }
            }
            else
            {
                foreach (var kv in clientRemoteCharacters)
                {
                    var go = kv.Value;
                    var cmc = go ? go.GetComponent<CharacterMainControl>() : null;
                    if (!IsAlive(cmc) || cmc == exclude) continue;
                    cand++;

                    // clientPlayerStatuses SceneId
                    string peerScene = null;
                    if (clientPlayerStatuses.TryGetValue(kv.Key, out var pst))
                        peerScene = pst?.SceneId;

                    // _cliLastSceneIdByPlayer 
                    if (string.IsNullOrEmpty(peerScene))
                        _cliLastSceneIdByPlayer.TryGetValue(kv.Key, out peerScene);

                    if (AreSameMap(mySceneId, peerScene))
                    {
                        _spectateList.Add(cmc);
                        kept++;
                    }
                }
            }

            Debug.Log($"[SPECTATE] candidates={cand}, kept={kept}, mySceneId={mySceneId} (canon={myK})");
        }

        // // // // 
        private static string CanonicalizeSceneId(string id)
        {
            if (string.IsNullOrEmpty(id)) return string.Empty;
            var s = id.Trim().ToLowerInvariant();

            // 
            string[] suffixes = { "_main", "_gameplay", "_core", "_scene", "_lod0", "_lod", "_client", "_server" };
            bool removed;
            do
            {
                removed = false;
                foreach (var suf in suffixes)
                {
                    if (s.EndsWith(suf))
                    {
                        s = s.Substring(0, s.Length - suf.Length);
                        removed = true;
                    }
                }
            } while (removed);

            while (s.Contains("__")) s = s.Replace("__", "_");

            var parts = s.Split('_');
            if (parts.Length >= 2 && parts[0] == "level")
                s = parts[0] + "_" + parts[1];

            if (s == "base" || s.StartsWith("base_")) s = "base";
            return s;
        }

        private static bool AreSameMap(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return a == b;
            return CanonicalizeSceneId(a) == CanonicalizeSceneId(b);
        }

        // CMC Player 
        private bool IsInSameScene(CharacterMainControl cmc)
        {
            if (!cmc) return false;
            string mySceneId = localPlayerStatus != null ? localPlayerStatus.SceneId : null;
            if (string.IsNullOrEmpty(mySceneId)) return true; // ID 

            if (IsServer)
            {
                foreach (var kv in remoteCharacters)
                {
                    if (kv.Value == null) continue;
                    var v = kv.Value.GetComponent<CharacterMainControl>();
                    if (v == cmc)
                    {
                        if (playerStatuses.TryGetValue(kv.Key, out var st) && st != null)
                            return st.SceneId == mySceneId;
                        return false;
                    }
                }
            }
            else
            {
                foreach (var kv in clientRemoteCharacters)
                {
                    if (kv.Value == null) continue;
                    var v = kv.Value.GetComponent<CharacterMainControl>();
                    if (v == cmc)
                    {
                        if (clientPlayerStatuses.TryGetValue(kv.Key, out var st) && st != null)
                            return st.SceneId == mySceneId;
                        return false;
                    }
                }
            }
            return false;
        }



        public bool IsAlive(CharacterMainControl cmc)
        {
            if (!cmc) return false;
            try { return cmc.Health != null && cmc.Health.CurrentHealth > 0.001f; } catch { return false; }
        }

        private void SpectateNext()
        {
            if (_spectateList.Count == 0) return;
            _spectateIdx = (_spectateIdx + 1) % _spectateList.Count;
            if (GameCamera.Instance) GameCamera.Instance.SetTarget(_spectateList[_spectateIdx]);
        }

        private void SpectatePrev()
        {
            if (_spectateList.Count == 0) return;
            _spectateIdx = (_spectateIdx - 1 + _spectateList.Count) % _spectateList.Count;
            if (GameCamera.Instance) GameCamera.Instance.SetTarget(_spectateList[_spectateIdx]);
        }

        // Player 
        private bool AllPlayersDead()
        {
            // SceneId Compute sans 
            string mySceneId = localPlayerStatus != null ? localPlayerStatus.SceneId : null;
            if (string.IsNullOrEmpty(mySceneId))
                ComputeIsInGame(out mySceneId);

            // SceneId 
            if (string.IsNullOrEmpty(mySceneId))
            {
                int alive = 0;
                if (IsAlive(CharacterMainControl.Main)) alive++;
                if (IsServer)
                    foreach (var kv in remoteCharacters) { var cmc = kv.Value ? kv.Value.GetComponent<CharacterMainControl>() : null; if (IsAlive(cmc)) alive++; }
                else
                    foreach (var kv in clientRemoteCharacters) { var cmc = kv.Value ? kv.Value.GetComponent<CharacterMainControl>() : null; if (IsAlive(cmc)) alive++; }
                return alive == 0;
            }

            int aliveSameScene = 0;

            // 0 
            if (IsAlive(CharacterMainControl.Main)) aliveSameScene++;

            if (IsServer)
            {
                foreach (var kv in remoteCharacters)
                {
                    var cmc = kv.Value ? kv.Value.GetComponent<CharacterMainControl>() : null;
                    if (!IsAlive(cmc)) continue;

                    string peerScene = null;
                    if (!_srvPeerScene.TryGetValue(kv.Key, out peerScene) && playerStatuses.TryGetValue(kv.Key, out var pst))
                        peerScene = pst?.SceneId;

                    if (AreSameMap(mySceneId, peerScene)) aliveSameScene++;
                }
            }
            else
            {
                foreach (var kv in clientRemoteCharacters)
                {
                    var cmc = kv.Value ? kv.Value.GetComponent<CharacterMainControl>() : null;
                    if (!IsAlive(cmc)) continue;

                    string peerScene = clientPlayerStatuses.TryGetValue(kv.Key, out var st) ? st?.SceneId : null;
                    if (AreSameMap(mySceneId, peerScene)) aliveSameScene++;
                }
            }

            bool none = (aliveSameScene <= 0);
            if (none)
                Debug.Log("[SPECTATE] No one alive in current map -> exit spectate and trigger results");
            return none;
        }



        private void EndSpectatorAndShowClosure()
        {
            _spectatorEndOnVotePending = false;

            if (!_spectatorActive) return;
            _spectatorActive = false;
            _skipSpectatorForNextClosure = true;

            try
            {
                var t = AccessTools.TypeByName("Duckov.UI.ClosureView");
                var mi = AccessTools.Method(t, "ShowAndReturnTask", new System.Type[] { typeof(global::DamageInfo), typeof(float) });
                if (mi != null)
                {
                    ((UniTask)mi.Invoke(null, new object[] { _lastDeathInfo, 0.5f })).Forget();
                }
            }
            catch { }
        }

        public void Client_RequestBeginSceneVote(
        string targetId, string curtainGuid,
        bool notifyEvac, bool saveToFile,
        bool useLocation, string locationName)
        {
            if (!networkStarted || IsServer || connectedPeer == null) return;

            var w = new NetDataWriter();
            w.Put((byte)Op.SCENE_VOTE_REQ);
            w.Put(targetId);
            w.Put(PackFlags(!string.IsNullOrEmpty(curtainGuid), useLocation, notifyEvac, saveToFile));
            if (!string.IsNullOrEmpty(curtainGuid)) w.Put(curtainGuid);
            w.Put(locationName ?? string.Empty);

            TransportSendToServer(w, true);
        }

        static bool FmodEventExists(string path)
        {
            try
            {
                var sys = FMODUnity.RuntimeManager.StudioSystem;
                if (!sys.isValid()) return false;
                FMOD.Studio.EventDescription desc;
                var r = sys.getEvent(path, out desc);
                return r == FMOD.RESULT.OK && desc.isValid();
            }
            catch { return false; }
        }
        private void Client_PlayLocalShotFx(ItemAgent_Gun gun, Transform muzzleTf, int weaponType)
        {
            if (!muzzleTf) return;

            GameObject ResolveMuzzlePrefab()
            {
                GameObject fxPfb = null;
                _muzzleFxCacheByWeaponType.TryGetValue(weaponType, out fxPfb);
                if (!fxPfb && gun && gun.GunItemSetting) fxPfb = gun.GunItemSetting.muzzleFxPfb;
                if (!fxPfb) fxPfb = defaultMuzzleFx;
                return fxPfb;
            }

            void PlayFxGameObject(GameObject go)
            {
                if (!go) return;
                var ps = go.GetComponent<ParticleSystem>();
                if (ps)
                {
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.Play(true);
                }
                else
                {
                    go.SetActive(false);
                    go.SetActive(true);
                }
            }

            // ========== GC ==========
            if (gun != null)
            {
                if (!_muzzleFxByGun.TryGetValue(gun, out var fxGo) || !fxGo)
                {
                    var fxPfb = ResolveMuzzlePrefab();
                    if (fxPfb)
                    {
                        fxGo = Instantiate(fxPfb, muzzleTf, false);
                        fxGo.transform.localPosition = Vector3.zero;
                        fxGo.transform.localRotation = Quaternion.identity;
                        _muzzleFxByGun[gun] = fxGo;
                    }
                }
                PlayFxGameObject(fxGo);

                if (!_shellPsByGun.TryGetValue(gun, out var shellPs) || shellPs == null)
                {
                    try { shellPs = (ParticleSystem)FI_ShellParticle?.GetValue(gun); } catch { shellPs = null; }
                    _shellPsByGun[gun] = shellPs;
                }
                try { if (shellPs) shellPs.Emit(1); } catch { }

                TryStartVisualRecoil_NoAlloc(gun);
                return;
            }

            // ========== FX ==========
            var pfb = ResolveMuzzlePrefab();
            if (pfb)
            {
                var tempFx = Instantiate(pfb, muzzleTf, false);
                tempFx.transform.localPosition = Vector3.zero;
                tempFx.transform.localRotation = Quaternion.identity;

                var ps = tempFx.GetComponent<ParticleSystem>();
                if (ps) ps.Play(true);
                else { tempFx.SetActive(false); tempFx.SetActive(true); }

                Destroy(tempFx, 0.5f);
            }
        }

        private void TryStartVisualRecoil_NoAlloc(ItemAgent_Gun gun)
        {
            if (!gun) return;
            try
            {
                MI_StartVisualRecoil?.Invoke(gun, null);
                return;
            }
            catch { }

            try { FI_RecoilBack?.SetValue(gun, true); } catch { }
        }

        sealed class RefEq<T> : IEqualityComparer<T> where T : class
        {
            public bool Equals(T a, T b) => ReferenceEquals(a, b);
            public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        // Server sans 
        private readonly Dictionary<ItemStatsSystem.Inventory, float> _srvLootMuteUntil
            = new Dictionary<ItemStatsSystem.Inventory, float>(new RefEq<ItemStatsSystem.Inventory>());

        public bool Server_IsLootMuted(ItemStatsSystem.Inventory inv)
        {
            if (!inv) return false;
            if (_srvLootMuteUntil.TryGetValue(inv, out var until))
            {
                if (Time.time < until) return true;
                _srvLootMuteUntil.Remove(inv); // 
            }
            return false;
        }

        public void Server_MuteLoot(ItemStatsSystem.Inventory inv, float seconds)
        {
            if (!inv) return;
            _srvLootMuteUntil[inv] = Time.time + Mathf.Max(0.01f, seconds);
        }

        // / = Host 
        internal bool _cliCorpseTreeReported = false;

        // OnDead 
        internal bool _cliInEnsureSelfDeathEmit = false;

        
        

        // / socket ItemAgent Host 
        private static void StripAllHandItems(CharacterMainControl cmc)
        {
            if (!cmc) return;
            var model = cmc.characterModel;
            if (!model) return;

            void KillChildren(Transform root)
            {
                if (!root) return;
                try
                {
                    foreach (var g in root.GetComponentsInChildren<ItemAgent_Gun>(true))
                        if (g && g.gameObject) UnityEngine.Object.Destroy(g.gameObject);

                    foreach (var m in root.GetComponentsInChildren<ItemAgent_MeleeWeapon>(true))
                        if (m && m.gameObject) UnityEngine.Object.Destroy(m.gameObject);

                    foreach (var x in root.GetComponentsInChildren<DuckovItemAgent>(true))
                        if (x && x.gameObject) UnityEngine.Object.Destroy(x.gameObject);

                    var baseType = typeof(Component).Assembly.GetType("ItemAgent");
                    if (baseType != null)
                    {
                        foreach (var c in root.GetComponentsInChildren(baseType, true))
                            if (c is Component comp && comp.gameObject) UnityEngine.Object.Destroy(comp.gameObject);
                    }
                }
                catch { }
            }

            try { KillChildren(model.RightHandSocket); } catch { }
            try { KillChildren(model.LefthandSocket); } catch { }
            try { KillChildren(model.MeleeWeaponSocket); } catch { }

        }

        // 2 /UI 0.2s 10 
        private IEnumerator Server_EnsureBarRoutine(NetPeer peer, GameObject go)
        {
            const int attempts = 10;
            const float interval = 0.2f;
            for (int i = 0; i < attempts; i++)
            {
                if (!IsServer || !networkStarted || !go) yield break;
                // Health CMC Max=40 showHealthBar + RequestHealthBar 
                // _srvPendingHp 
                Server_HookOneHealth(peer, go); 
                yield return new WaitForSeconds(interval);
            }
        }

        // ==== Client 6 ==== 
        private float _cliSpawnProtectUntil = 0f;

        // Client Server/peer 
        public void Client_ArmSpawnProtection(float seconds)
        {
            if (seconds <= 0f) return;
            _cliSpawnProtectUntil = Time.realtimeSinceStartup + seconds;
        }

        // 
        public bool Client_IsSpawnProtected()
        {
            return Time.realtimeSinceStartup < _cliSpawnProtectUntil;
        }

        public NetPeer Server_FindOwnerPeerByHealth(Health h)
        {
            if (h == null) return null;
            CharacterMainControl cmc = null;
            try { cmc = h.TryGetCharacter(); } catch { }
            if (!cmc) { try { cmc = h.GetComponentInParent<CharacterMainControl>(); } catch { } }
            if (!cmc) return null;

            foreach (var kv in remoteCharacters) // remoteCharacters: NetPeer -> GameObject Host 
            {
                if (kv.Value == cmc.gameObject) return kv.Key;
            }
            return null;
        }

        // Host DamageInfo Client Hurt
        public void Server_ForwardHurtToOwner(NetPeer owner, global::DamageInfo di)
        {
            if (!IsServer || owner == null) return;

            var w = new NetDataWriter();
            w.Put((byte)Op.PLAYER_HURT_EVENT);

            // 
            w.Put(di.damageValue);
            w.Put(di.armorPiercing);
            w.Put(di.critDamageFactor);
            w.Put(di.critRate);
            w.Put(di.crit);
            w.PutV3cm(di.damagePoint);
            w.PutDir(di.damageNormal.sqrMagnitude < 1e-6f ? Vector3.up : di.damageNormal.normalized);
            w.Put(di.fromWeaponItemID);
            w.Put(di.bleedChance);
            w.Put(di.isExplosion);
   
            TransportSend(owner, w, true);
        }



        private void Client_ApplySelfHurtFromServer(NetPacketReader r)
        {
            try
            {
                // 
                float dmg = r.GetFloat();
                float ap = r.GetFloat();
                float cdf = r.GetFloat();
                float cr = r.GetFloat();
                int crit = r.GetInt();
                Vector3 hit = r.GetV3cm();
                Vector3 nrm = r.GetDir();
                int wid = r.GetInt();
                float bleed = r.GetFloat();
                bool boom = r.GetBool();

                var main = LevelManager.Instance ? LevelManager.Instance.MainCharacter : null;
                if (!main || main.Health == null) return;

                // DamageInfo / main 
                var di = new DamageInfo(main)
                {
                    damageValue = dmg,
                    armorPiercing = ap,
                    critDamageFactor = cdf,
                    critRate = cr,
                    crit = crit,
                    damagePoint = hit,
                    damageNormal = nrm,
                    fromWeaponItemID = wid,
                    bleedChance = bleed,
                    isExplosion = boom
                };

                // echo 
                _cliLastSelfHurtAt = Time.time;

                main.Health.Hurt(di);

                Client_ReportSelfHealth_IfReadyOnce(); 

            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[CLIENT] apply self hurt from server failed: " + e);
            }
        }
    }
}
