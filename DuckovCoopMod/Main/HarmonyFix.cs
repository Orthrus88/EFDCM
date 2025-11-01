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
using Duckov.Buffs;
using Duckov.Quests;
using Duckov.Quests.Tasks;
using Duckov.Scenes;
using Duckov.UI;
using Duckov.UI.Animations;
using Duckov.Utilities;
using HarmonyLib;
using ItemStatsSystem;
using ItemStatsSystem.Items;
using LiteNetLib;
using LiteNetLib.Utils;
using NodeCanvas.StateMachines;
using Steamworks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/*
 HarmonyFix.cs - Runtime patches to integrate co-op networking with game systems

 Purpose
 - Client-side: intercept local actions (gun fire, melee, grenade throw, item drop/pickup)
   and send requests instead of applying full effects locally.
 - Host-side: compute authoritative results (projectiles, loot, doors, environment) and
   broadcast compact events/snapshots to all clients.

 Highlights
 - Shooting: prevent local projectile instantiation on clients; host spawns and broadcasts.
 - Grenades: capture prefab/type/ballistics on host; broadcast launch/explode events.
 - Melee: client packs hit info; host validates and mirrors damage to owner client.
 - Loot: unify drop/pickup flows; suppress echo/duplicates; tag spawned items with tokens.
 - Doors: door open/close requests; host applies and broadcasts state changes.

 Safety
 - Avoids double-broadcast by tagging host actions triggered by client requests.
 - Defensive try/catch blocks around reflection and optional fields to prevent UI/runtime crashes.
*/
namespace DuckovCoopMod
{
    public sealed class LocalMeleeOncePerFrame : UnityEngine.MonoBehaviour
    {
        public int lastFrame;
    }

    // Client FIRE_REQUEST 
    [HarmonyPatch(typeof(ItemAgent_Gun), "ShootOneBullet")]
    public static class Patch_ShootOneBullet_Client
    {
        static bool Prefix(ItemAgent_Gun __instance, Vector3 _muzzlePoint, Vector3 _shootDirection, Vector3 firstFrameCheckStartPoint)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted) return true;

            bool isClient = !mod.IsServer;
            if (!isClient) return true;

            var holder = __instance.Holder;
            bool isLocalMain = (holder == CharacterMainControl.Main);
            bool isAI = holder && holder.GetComponent<NetAiTag>() != null;

            if (isLocalMain)
            {
                mod.Net_OnClientShoot(__instance, _muzzlePoint, _shootDirection, firstFrameCheckStartPoint);
                return false; // Client Host
            }

            if (isAI) return false;     // Client AI Host FIRE_EVENT
            if (!isLocalMain) return false;
            return true;
        }
    }

    // Projectile.Init Client
    [HarmonyPatch(typeof(Projectile), nameof(Projectile.Init), new[] { typeof(ProjectileContext) })]
    static class Patch_ProjectileInit_Broadcast
    {
        static void Postfix(Projectile __instance, ref ProjectileContext _context)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.IsServer || __instance == null) return;

            if (mod._serverSpawnedFromClient != null && mod._serverSpawnedFromClient.Contains(__instance)) return;

            var fromC = _context.fromCharacter;
            if (!fromC) return;

            string shooterId = null;
            if (fromC.IsMainCharacter) shooterId = mod.localPlayerStatus?.EndPoint;
            else
            {
                var tag = fromC.GetComponent<NetAiTag>();
                if (tag == null || tag.aiId == 0) return;
                shooterId = $"AI:{tag.aiId}";
            }

            int weaponType = 0;
            try { var gun = fromC.GetGun(); if (gun != null && gun.Item != null) weaponType = gun.Item.TypeID; } catch { }

            var w = new LiteNetLib.Utils.NetDataWriter();
            w.Put((byte)Op.FIRE_EVENT);
            w.Put(shooterId ?? string.Empty);
            w.Put(weaponType);
            w.PutV3cm(__instance.transform.position); // muzzle
            w.PutDir(_context.direction);
            w.Put(_context.speed);
            w.Put(_context.distance);

            // explosionRange / explosionDamage 
            w.PutProjectilePayload(_context);

            if (mod.transport != null)
                mod.transport.Broadcast(w.CopyData(), true);
            else
                mod.netManager?.SendToAll(w, DeliveryMethod.ReliableOrdered);
        }
    }



    [HarmonyPatch]
    public static class Patch_Grenade_Sync
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Skill_Grenade), nameof(Skill_Grenade.OnRelease))]
        static bool Skill_Grenade_OnRelease_Prefix(Skill_Grenade __instance)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted) return true;

            // Host/Client 
            try
            {
                var prefab = __instance.grenadePfb; // public 
                int typeId = 0; try { typeId = (__instance.fromItem != null) ? __instance.fromItem.TypeID : __instance.damageInfo.fromWeaponItemID; } catch { }
                if (prefab) mod.CacheGrenadePrefab(typeId, prefab);
            }
            catch { }


            if (mod.IsServer)
            {
                // Server damageInfo.fromWeaponItemID Grenade.Launch Postfix 
                try
                {
                    int tid = 0;
                    try { if (__instance.fromItem != null) tid = __instance.fromItem.TypeID; } catch { }
                    if (tid == 0)
                    {
                        try { tid = __instance.damageInfo.fromWeaponItemID; } catch { }
                    }
                    if (tid != 0)
                    {
                        try { __instance.damageInfo.fromWeaponItemID = tid; } catch { }
                    }
                }
                catch { }

                // Launch Postfix 
                return true;
            }


            // Client 
            if (mod.connectedPeer == null || mod.connectedPeer.ConnectionState != ConnectionState.Connected) return true;

            // 
            CharacterMainControl fromChar = null;
            try
            {
                var f_from = AccessTools.Field(typeof(SkillBase), "fromCharacter");
                fromChar = f_from?.GetValue(__instance) as CharacterMainControl;
            }
            catch { }
            if (fromChar != CharacterMainControl.Main) return true;

            try
            {
                Vector3 position = fromChar ? fromChar.CurrentUsingAimSocket.position : Vector3.zero;

                Vector3 releasePoint = Vector3.zero;
                var relCtx = AccessTools.Field(typeof(SkillBase), "skillReleaseContext")?.GetValue(__instance);
                if (relCtx != null)
                {
                    var f_rp = AccessTools.Field(relCtx.GetType(), "releasePoint");
                    if (f_rp != null) releasePoint = (Vector3)f_rp.GetValue(relCtx);
                }
                float y = releasePoint.y;
                Vector3 point = releasePoint - (fromChar ? fromChar.transform.position : Vector3.zero);
                point.y = 0f; float dist = point.magnitude;
                var ctxObj = AccessTools.Field(typeof(SkillBase), "skillContext")?.GetValue(__instance);
                if (!__instance.canControlCastDistance && ctxObj != null)
                {
                    var f_castRange = AccessTools.Field(ctxObj.GetType(), "castRange");
                    if (f_castRange != null) dist = (float)f_castRange.GetValue(ctxObj);
                }
                point.Normalize(); Vector3 target = position + point * dist; target.y = y;

                float vert = 8f, effectRange = 3f;
                if (ctxObj != null)
                {
                    var f_vert = AccessTools.Field(ctxObj.GetType(), "grenageVerticleSpeed");
                    var f_eff = AccessTools.Field(ctxObj.GetType(), "effectRange");
                    if (f_vert != null) vert = (float)f_vert.GetValue(ctxObj);
                    if (f_eff != null) effectRange = (float)f_eff.GetValue(ctxObj);
                }
                Vector3 velocity = __instance.CalculateVelocity(position, target, vert);

                string prefabType = __instance.grenadePfb ? __instance.grenadePfb.GetType().FullName : string.Empty;
                string prefabName = __instance.grenadePfb ? __instance.grenadePfb.name : string.Empty;
                int typeId2 = 0; try { typeId2 = (__instance.fromItem != null) ? __instance.fromItem.TypeID : __instance.damageInfo.fromWeaponItemID; } catch { }

                bool createExplosion = __instance.createExplosion;
                float shake = __instance.explosionShakeStrength;
                float damageRange = effectRange;
                bool delayFromCollide = __instance.delayFromCollide;
                float delayTime = __instance.delay;
                bool isLandmine = __instance.isLandmine;
                float landmineRange = __instance.landmineTriggerRange;

                // 
                mod.Net_OnClientThrow(__instance, typeId2, prefabType, prefabName, position, velocity,
                    createExplosion, shake, damageRange, delayFromCollide, delayTime, isLandmine, landmineRange);
                return false;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[GRENADE Prefix] exception -> pass through: " + e);
                return true;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Grenade), nameof(Grenade.Launch))]
        static void Grenade_Launch_Postfix(Grenade __instance, Vector3 startPoint, Vector3 velocity, CharacterMainControl fromCharacter)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted || !mod.IsServer) return;

            int typeId = 0;
            try { typeId = __instance.damageInfo.fromWeaponItemID; } catch { }

            if (typeId == 0)
            {
                try
                {
                    typeId = Traverse.Create(__instance).Field<ItemAgent>("bindedAgent").Value.Item.TypeID;
                }
                catch {}
            }

            mod.Server_OnGrenadeLaunched(__instance, startPoint, velocity, typeId);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Grenade), "Explode")]
        static void Grenade_Explode_Prefix(Grenade __instance, ref bool __state)
        {
            __state = __instance.createExplosion;
            var mod = ModBehaviour.Instance;
            if (mod != null && mod.networkStarted && !mod.IsServer)
            {
                var isNetworkGrenade = __instance && __instance.GetComponent<DuckovCoopMod.NetGrenadeTag>() != null;
                if (!isNetworkGrenade)
                    __instance.createExplosion = false;
            }
        }
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Grenade), "Explode")]
        static void Grenade_Explode_Postfix(Grenade __instance, bool __state)
        {
            var mod = ModBehaviour.Instance;
            if (mod != null && mod.networkStarted && !mod.IsServer) __instance.createExplosion = __state;
        }
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Grenade), "Explode")]
        static void Grenade_Explode_ServerBroadcast(Grenade __instance)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted || !mod.IsServer) return;
            mod.Server_OnGrenadeExploded(__instance);
        }
    }

    [HarmonyPatch(typeof(ItemExtensions), nameof(ItemExtensions.Drop), new[] { typeof(Item), typeof(Vector3), typeof(bool), typeof(Vector3), typeof(float) })]
    public static class Patch_Item_Drop
    {
        static bool Prefix(Item item, Vector3 pos, bool createRigidbody, Vector3 dropDirection, float randomAngle)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted) return true;


            if (NetSilenceGuards.InPickupItem || NetSilenceGuards.InCapacityShrinkCleanup)
            {
                UnityEngine.Debug.Log("[ITEM] Silent discard: auto drop due to pickup failure/capacity cleanup; not reporting to host");
                return true;
            }

            if (mod.IsServer)
            {
                // Server Postfix Client 
                return true;
            }

            // Client " Host " 
            if (mod._clientSpawnByServerItems.Remove(item))
                return true;


            // Client 
            uint token = ++mod.nextLocalDropToken;
            mod.pendingLocalDropTokens.Add(token);
            mod.pendingTokenItems[token] = item; 
            mod.SendItemDropRequest(token, item, pos, createRigidbody, dropDirection, randomAngle);
            return true;

        }

        static void Postfix(Item item, DuckovItemAgent __result, Vector3 pos, bool createRigidbody, Vector3 dropDirection, float randomAngle)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted || !mod.IsServer) return;

            // Client 
            if (mod._serverSpawnedFromClientItems.Remove(item))
                return;

            // Server 
            if (NetSilenceGuards.InPickupItem)
            {
                UnityEngine.Debug.Log("[SVR] Auto drop (pickup rollback) - do not broadcast SPAWN to avoid duplication");
                return;
            }

            try
            {
                // Host AI Client
                var w = mod.writer;
                w.Reset();
                w.Put((byte)Op.ITEM_SPAWN);
                w.Put((uint)0);                     // token=0 Host 
                uint id = mod.AllocateDropId();
                mod.serverDroppedItems[id] = item;
                w.Put(id);
                NetPack.PutV3cm(w, pos);
                NetPack.PutDir(w, dropDirection);
                w.Put(randomAngle);
                w.Put(createRigidbody);
                mod.WriteItemSnapshot(w, item);
                mod.BroadcastReliable(w);
            }
            catch (Exception e)
            {
                Debug.LogError($"[ITEM] Hostbroadcast Failed: {e}");
            }
        }
    }

    [HarmonyPatch(typeof(Item), "NotifyAddedToInventory")]
    public static class Patch_Item_Pickup_NotifyAdded
    {
        static void Postfix(Item __instance, Inventory __0 /* inv */)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted) return;

            // inv Player / 
            // NPC/ inv.AttachedToItem Player 
            // Client Client 
            if (!mod.IsServer)
            {
                if (TryFindId(mod.clientDroppedItems, __instance, out uint cid))
                {
                    // 
                    try
                    {
                        var ag = __instance.ActiveAgent;
                        if (ag && ag.gameObject) UnityEngine.Object.Destroy(ag.gameObject);
                    }
                    catch { }

                    // Host Host DESPAWN Client 
                    var w = mod.writer; w.Reset();
                    w.Put((byte)Op.ITEM_PICKUP_REQUEST);
                    w.Put(cid);
                    mod.connectedPeer?.Send(w, DeliveryMethod.ReliableOrdered);
                }
                return;
            }

            // Host Host Host DESPAWN
            if (mod.IsServer && TryFindId(mod.serverDroppedItems, __instance, out uint sid))
            {
                mod.serverDroppedItems.Remove(sid);

                try
                {
                    var ag = __instance.ActiveAgent;
                    if (ag && ag.gameObject) UnityEngine.Object.Destroy(ag.gameObject);
                }
                catch { }

                var w = mod.writer; w.Reset();
                w.Put((byte)Op.ITEM_DESPAWN);
                w.Put(sid);
                mod.netManager.SendToAll(w, DeliveryMethod.ReliableOrdered);
            }
        }

        // ReferenceEquals 
        static bool TryFindId(System.Collections.Generic.Dictionary<uint, Item> dict, Item item, out uint id)
        {
            foreach (var kv in dict)
                if (object.ReferenceEquals(kv.Value, item))
                { id = kv.Key; return true; }
            id = 0; return false;
        }
    }


    [HarmonyPatch(typeof(Inventory), "NotifyContentChanged")]
    public static class Patch_Inventory_NotifyContentChanged
    {
        const float PICK_RADIUS = 2.5f; // 2~3
        const QueryTriggerInteraction QTI = QueryTriggerInteraction.Collide;
        const int LAYER_MASK = ~0; // Layer 

        static void Postfix(Inventory __instance, Item item)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted || item == null) return;

            if (mod._applyingLootState) return;

            if (LootboxDetectUtil.IsLootboxInventory(__instance) && !LootboxDetectUtil.IsPrivateInventory(__instance))
                return;

            // --- Client ---
            if (!mod.IsServer)
            {
                // A) 
                if (TryFindId(mod.clientDroppedItems, item, out uint cid))
                {
                    LocalDestroyAgent(item);
                    SendPickupReq(mod, cid);
                    return;
                }

                // B) NetDropTag 
                if (TryFindNearestTaggedId(out uint nearId))
                {
                    LocalDestroyAgentById(mod.clientDroppedItems, nearId);
                    SendPickupReq(mod, nearId);
                }
                return;
            }

            // --- Host ---
            if (TryFindId(mod.serverDroppedItems, item, out uint sid))
            {
                ServerDespawn(mod, sid);
                return;
            }

            if (TryFindNearestTaggedId(out uint nearSid))
            {
                ServerDespawn(mod, nearSid);
            }
        }

        static void SendPickupReq(ModBehaviour mod, uint id)
        {
            var w = mod.writer; w.Reset();
            w.Put((byte)Op.ITEM_PICKUP_REQUEST);
            w.Put(id);
            mod.connectedPeer?.Send(w, DeliveryMethod.ReliableOrdered);
        }

        static void ServerDespawn(ModBehaviour mod, uint id)
        {
            if (mod.serverDroppedItems.TryGetValue(id, out var it) && it != null)
                LocalDestroyAgent(it);
            mod.serverDroppedItems.Remove(id);

            var w = mod.writer; w.Reset();
            w.Put((byte)Op.ITEM_DESPAWN);
            w.Put(id);
            mod.netManager.SendToAll(w, DeliveryMethod.ReliableOrdered);
        }

        static void LocalDestroyAgent(Item it)
        {
            try
            {
                var ag = it.ActiveAgent;
                if (ag && ag.gameObject) Object.Destroy(ag.gameObject);
            }
            catch { }
        }

        static void LocalDestroyAgentById(Dictionary<uint, Item> dict, uint id)
        {
            if (dict.TryGetValue(id, out var it) && it != null) LocalDestroyAgent(it);
        }

        static bool TryFindId(Dictionary<uint, Item> dict, Item it, out uint id)
        {
            foreach (var kv in dict)
                if (ReferenceEquals(kv.Value, it)) { id = kv.Key; return true; }
            id = 0; return false;
        }

        // NetDropTag 
        static readonly Collider[] _nearbyBuf = new Collider[64];
        const int LAYER_MASK_ANY = ~0;

        static bool TryFindNearestTaggedId(out uint id)
        {
            id = 0;
            var main = CharacterMainControl.Main;
            if (main == null) return false;

            var pos = main.transform.position;
            int n = Physics.OverlapSphereNonAlloc(pos, PICK_RADIUS, _nearbyBuf, LAYER_MASK_ANY, QTI);

            float best = float.MaxValue;
            NetDropTag bestTag = null;

            for (int i = 0; i < n; i++)
            {
                var c = _nearbyBuf[i]; if (!c) continue;
                var t = c.GetComponentInParent<NetDropTag>() ?? c.GetComponent<NetDropTag>();
                if (t == null || t.id == 0) continue;

                float d2 = (t.transform.position - pos).sqrMagnitude;
                if (d2 < best) { best = d2; bestTag = t; }
            }

            if (bestTag != null) { id = bestTag.id; return true; }
            return false;
        }
    }

    [HarmonyPatch]
    public static class Patch_ItemExtensions_Drop_AddNetDropTag
    {
        // Drop(Item, Vector3, bool, Vector3, float)
        [HarmonyPatch(typeof(global::ItemExtensions), "Drop",
            new System.Type[] {
            typeof(global::ItemStatsSystem.Item),
            typeof(global::UnityEngine.Vector3),
            typeof(bool),
            typeof(global::UnityEngine.Vector3),
            typeof(float)
            })]
        [HarmonyPostfix]
        private static void Postfix(
            // this Item 
            global::ItemStatsSystem.Item item,
            global::UnityEngine.Vector3 pos,
            bool createRigidbody,
            global::UnityEngine.Vector3 dropDirection,
            float randomAngle,
            // ref 
            ref global::DuckovItemAgent __result)
        {
            try
            {
                var agent = __result;
                if (agent == null) return;

                var go = agent.gameObject;
                if (go == null) return;

                // 
                var tag = go.GetComponent<NetDropTag>();
                if (tag == null)
                    tag = go.AddComponent<NetDropTag>();

                // 
                // tag.itemTypeId = item?.TypeID ?? 0;
                // tag.ownerNetId = ModBehaviour.Instance?.LocalPlayerId ?? 0;
            }
            catch (System.Exception e)
            {
                global::UnityEngine.Debug.LogError($"[Harmony][Drop.Postfix] Add NetDropTag failed: {e}");
            }
        }
    }

    public static class NetSilenceGuards
    {
        // 
        [ThreadStatic] public static bool InPickupItem;           // CharacterItemControl.PickupItem
        [ThreadStatic] public static bool InCapacityShrinkCleanup; // 
    }

    // CharacterItemControl.PickupItem 
    [HarmonyPatch(typeof(CharacterItemControl), nameof(CharacterItemControl.PickupItem))]
    static class Patch_CharacterItemControl_PickupItem
    {
        static void Prefix() { NetSilenceGuards.InPickupItem = true; }
        static void Finalizer() { NetSilenceGuards.InPickupItem = false; }
    }

    public static class MeleeLocalGuard
    {
        [ThreadStatic] public static bool LocalMeleeTryingToHurt;
    }

    [HarmonyPatch(typeof(ItemAgent_MeleeWeapon), "CheckCollidersInRange")]
    static class Patch_Melee_FlagLocalDeal
    {
        static void Prefix(ItemAgent_MeleeWeapon __instance, bool dealDamage)
        {
            var mod = DuckovCoopMod.ModBehaviour.Instance;
            bool isClient = (mod != null && mod.networkStarted && !mod.IsServer);
            bool fromLocalMain = (__instance && __instance.Holder == CharacterMainControl.Main);
            DuckovCoopMod.MeleeLocalGuard.LocalMeleeTryingToHurt = (isClient && fromLocalMain && dealDamage);
        }
        static void Postfix()
        {
            DuckovCoopMod.MeleeLocalGuard.LocalMeleeTryingToHurt = false;
        }
    }

    [HarmonyPatch(typeof(DamageReceiver), "Hurt")]
    static class Patch_ClientReportMeleeHit
    {
        static bool Prefix(DamageReceiver __instance, ref global::DamageInfo __0)
        {
            var mod = DuckovCoopMod.ModBehaviour.Instance;

            // Host 
            if (mod == null || !mod.networkStarted || mod.IsServer || !MeleeLocalGuard.LocalMeleeTryingToHurt)
                return true;

            if (mod.connectedPeer == null)
            {
                Debug.LogWarning("[CLIENT] MELEE_HIT_REPORT aborted: connectedPeer==null, fallback to local Hurt");
                return true; // Hurt 
            }

            try
            {
                var w = new LiteNetLib.Utils.NetDataWriter();
                w.Put((byte)DuckovCoopMod.Op.MELEE_HIT_REPORT);
                w.Put(mod.localPlayerStatus != null ? mod.localPlayerStatus.EndPoint : "");

                // DamageInfo 
                w.Put(__0.damageValue);
                w.Put(__0.armorPiercing);
                w.Put(__0.critDamageFactor);
                w.Put(__0.critRate);
                w.Put(__0.crit);

                w.PutV3cm(__0.damagePoint);
                w.PutDir(__0.damageNormal);

                w.Put(__0.fromWeaponItemID);
                w.Put(__0.bleedChance);
                w.Put(__0.isExplosion);

                // Host 
                float range = 1.2f;
                try
                {
                    var main = CharacterMainControl.Main;
                    var melee = main ? (main.CurrentHoldItemAgent as ItemAgent_MeleeWeapon) : null;
                    if (melee != null) range = Mathf.Max(0.6f, melee.AttackRange);
                }
                catch { }
                w.Put(range);

               
                mod.connectedPeer.Send(w, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[CLIENT] Melee hit report failed: " + e);
                return true; // Failed Hurt 
            }

            try
            {
                if (global::FX.PopText.instance)
                {
                    // Health.Hurt 
                    var look = global::Duckov.Utilities.GameplayDataSettings.UIStyle
                        .GetElementDamagePopTextLook(global::ElementTypes.physics);

                    // 
                    Vector3 pos = (__0.damagePoint.sqrMagnitude > 1e-6f ? __0.damagePoint : __instance.transform.position)
                                  + global::UnityEngine.Vector3.up * 2f;

                    // / 
                    float size = (__0.crit > 0) ? look.critSize : look.normalSize;
                    var sprite = (__0.crit > 0) ? global::Duckov.Utilities.GameplayDataSettings.UIStyle.CritPopSprite : null;

                    // HIT 
                    string text = (__0.damageValue > 0f) ? __0.damageValue.ToString("F1") : "HIT";

                    global::FX.PopText.Pop(text, pos, look.color, size, sprite);
                }
            }
            catch { }



            // Succeeded Host 
            return false;
        }
    }


    [HarmonyPatch(typeof(CharacterAnimationControl_MagicBlend), "OnAttack")]
    static class Patch_Melee_OnAttack_SendNetAndFx
    {
        static void Postfix(CharacterAnimationControl_MagicBlend __instance)
        {
            var mod = DuckovCoopMod.ModBehaviour.Instance;
            var ctrl = __instance?.characterMainControl;
            if (mod == null || !mod.networkStarted || ctrl == null) return;
            if (ctrl != CharacterMainControl.Main) return; // Player

            // / 
            var model = ctrl.characterModel;
            if (model)
            {
                var gate = model.GetComponent<LocalMeleeOncePerFrame>() ?? model.gameObject.AddComponent<LocalMeleeOncePerFrame>();
                if (gate.lastFrame == UnityEngine.Time.frameCount) return;
                gate.lastFrame = UnityEngine.Time.frameCount;
            }

            var melee = ctrl.CurrentHoldItemAgent as ItemAgent_MeleeWeapon;
            if (!melee) return;

            float dealDelay = 0.1f;
            try { dealDelay = Mathf.Max(0f, melee.DealDamageTime); } catch { }

            Vector3 snapPos = ctrl.modelRoot ? ctrl.modelRoot.position : ctrl.transform.position;
            Vector3 snapDir = ctrl.CurrentAimDirection.sqrMagnitude > 1e-6f ? ctrl.CurrentAimDirection : ctrl.transform.forward;

            if (mod.IsServer)
            {
                mod.BroadcastMeleeSwing(mod.localPlayerStatus.EndPoint, dealDelay);
            }
            else
            {
                // Client FX + Host
                DuckovCoopMod.MeleeFx.SpawnSlashFx(ctrl.characterModel);
                mod.Net_OnClientMeleeAttack(dealDelay, snapPos, snapDir);
            }
        }
    }



    [HarmonyPatch(typeof(CharacterAnimationControl_MagicBlend), "Update")]
    static class Patch_MagicBlend_Update_ForRemote
    {
        static bool Prefix(CharacterAnimationControl_MagicBlend __instance)
        {
            // Animator Network 
            if (__instance && __instance.GetComponentInParent<RemoteReplicaTag>() != null)
                return false;
            return true;
        }
    }

    public sealed class RemoteReplicaTag : MonoBehaviour { }



    [HarmonyPatch(typeof(HealthSimpleBase), "Awake")]
    public static class Patch_HSB_Awake_TagRegister
    {
        static void Postfix(HealthSimpleBase __instance)
        {
            if (!__instance) return;

            var tag = __instance.GetComponent<NetDestructibleTag>();
            if (!tag) return; // / AddComponent

            // BreakableWall ID //
            Transform wallRoot = FindBreakableWallRoot(__instance.transform);
            if (wallRoot != null)
            {
                try
                {
                    uint computed = NetDestructibleTag.ComputeStableId(wallRoot.gameObject);
                    if (tag.id != computed) tag.id = computed;
                }
                catch {}
            }

            // //
            var mod = DuckovCoopMod.ModBehaviour.Instance;
            if (mod != null)
            {
                mod.RegisterDestructible(tag.id, __instance);
            }
        }

        // BreakableWall 
        static Transform FindBreakableWallRoot(Transform t)
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
    }


    // Client Host 
    // Host Postfix 
    [HarmonyPatch(typeof(HealthSimpleBase), "OnHurt")]
    public static class Patch_HSB_OnHurt_RedirectNet
    {
        static bool Prefix(HealthSimpleBase __instance, DamageInfo dmgInfo)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted) return true;

            if (!mod.IsServer)
            {

                // UI / Hit true Kill
                LocalHitKillFx.ClientPlayForDestructible(__instance, dmgInfo, predictedDead: false);

                var tag = __instance.GetComponent<NetDestructibleTag>();
                if (!tag) tag = __instance.gameObject.AddComponent<NetDestructibleTag>();
                mod.Client_RequestDestructibleHurt(tag.id, dmgInfo);
                return false;
            }
            return true;
        }

        static void Postfix(HealthSimpleBase __instance, DamageInfo dmgInfo)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted || !mod.IsServer) return;

            var tag = __instance.GetComponent<NetDestructibleTag>();
            if (!tag) return;
            mod.Server_BroadcastDestructibleHurt(tag.id, __instance.HealthValue, dmgInfo);
        }
    }

    // Host Client Switch
    [HarmonyPatch(typeof(HealthSimpleBase), "Dead")]
    public static class Patch_HSB_Dead_Broadcast
    {
        static void Postfix(HealthSimpleBase __instance, DamageInfo dmgInfo)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted || !mod.IsServer) return;

            var tag = __instance.GetComponent<NetDestructibleTag>();
            if (!tag) return;
            mod.Server_BroadcastDestructibleDead(tag.id, dmgInfo);
        }
    }


    [HarmonyPatch(typeof(Buff), "Setup")]
    static class Patch_Buff_Setup_Safe
    {
        // 
        static readonly FieldInfo FI_master = AccessTools.Field(typeof(Buff), "master");
        static readonly FieldInfo FI_timeWhenStarted = AccessTools.Field(typeof(Buff), "timeWhenStarted");
        static readonly FieldInfo FI_buffFxPfb = AccessTools.Field(typeof(Buff), "buffFxPfb");
        static readonly FieldInfo FI_buffFxInstance = AccessTools.Field(typeof(Buff), "buffFxInstance");
        static readonly FieldInfo FI_OnSetupEvent = AccessTools.Field(typeof(Buff), "OnSetupEvent");
        static readonly FieldInfo FI_effects = AccessTools.Field(typeof(Buff), "effects");
        static readonly MethodInfo MI_OnSetup = AccessTools.Method(typeof(Buff), "OnSetup");

        static bool Prefix(Buff __instance, CharacterBuffManager manager)
        {
            // CharacterItem 
            var masterCMC = manager ? manager.Master : null;
            var item = (masterCMC != null) ? masterCMC.CharacterItem : null;
            if (item != null && item.transform != null) return true;

            // CharacterItem //
            // master / timeWhenStarted
            FI_master?.SetValue(__instance, manager);
            FI_timeWhenStarted?.SetValue(__instance, Time.time);

            // Buff Transform CharacterItem.transform 
            var parent = masterCMC ? masterCMC.transform : __instance.transform.parent;
            if (parent) __instance.transform.SetParent(parent, false);

            // FX ArmorSocket/ 
            var oldFx = FI_buffFxInstance?.GetValue(__instance) as GameObject;
            if (oldFx) Object.Destroy(oldFx);

            var pfb = FI_buffFxPfb?.GetValue(__instance) as GameObject;
            if (pfb && masterCMC && masterCMC.characterModel)
            {
                var fx = Object.Instantiate(pfb);
                var t = masterCMC.characterModel.ArmorSocket ? masterCMC.characterModel.ArmorSocket : masterCMC.transform;
                fx.transform.SetParent(t);
                fx.transform.position = t.position;
                fx.transform.localRotation = Quaternion.identity;
                FI_buffFxInstance?.SetValue(__instance, fx);
            }

            // effects.SetItem Item OnSetup / OnSetupEvent 
            MI_OnSetup?.Invoke(__instance, null);
            var onSetupEvent = FI_OnSetupEvent?.GetValue(__instance) as UnityEvent;
            onSetupEvent?.Invoke();

            // CharacterItem SetItem/SetParent 
            if (!__instance.gameObject.GetComponent<_BuffLateBinder>())
            {
                var binder = __instance.gameObject.AddComponent<_BuffLateBinder>();
                binder.Init(__instance, FI_effects);
            }

            //sans 
            return false;
        }


        [HarmonyPatch(typeof(CharacterBuffManager), nameof(CharacterBuffManager.AddBuff))]
        static class Patch_BroadcastBuffToOwner
        {
            static void Postfix(CharacterBuffManager __instance, Buff buffPrefab, CharacterMainControl fromWho, int overrideWeaponID)
            {
                var mod = ModBehaviour.Instance;
                if (mod == null || !mod.networkStarted || !mod.IsServer) return;
                if (buffPrefab == null) return;

                var target = __instance.Master;                // Buff 
                if (target == null) return;

                // Player Server remoteCharacters: NetPeer -> GameObject 
                NetPeer peer = null;
                foreach (var kv in mod.remoteCharacters)
                {
                    if (kv.Value == null) continue;
                    if (kv.Value == target.gameObject) { peer = kv.Key; break; }
                }
                if (peer == null) return; // Player Host 

                // Buff Player 
                var w = new NetDataWriter();
                w.Put((byte)Op.PLAYER_BUFF_SELF_APPLY); // opcode Mod.cs 
                w.Put(overrideWeaponID);   // weaponTypeId Client buff prefab
                w.Put(buffPrefab.ID);      // buffId id 
                peer.Send(w, DeliveryMethod.ReliableOrdered);
            }
        }


        [HarmonyPatch(typeof(CharacterBuffManager), nameof(CharacterBuffManager.AddBuff))]
        static class Patch_BroadcastBuffApply
        {
            static void Postfix(CharacterBuffManager __instance, Buff buffPrefab, CharacterMainControl fromWho, int overrideWeaponID)
            {
                var mod = ModBehaviour.Instance;
                if (mod == null || !mod.networkStarted || !mod.IsServer) return;
                if (buffPrefab == null) return;

                var target = __instance.Master; // Buff 
                if (target == null) return;

                // Client 
                NetPeer ownerPeer = null;
                foreach (var kv in mod.remoteCharacters)
                {
                    if (kv.Value == null) continue;
                    if (kv.Value == target.gameObject) { ownerPeer = kv.Key; break; }
                }
                if (ownerPeer != null)
                {
                    var w = new NetDataWriter();
                    w.Put((byte)Op.PLAYER_BUFF_SELF_APPLY);
                    w.Put(overrideWeaponID);
                    w.Put(buffPrefab.ID);
                    ownerPeer.Send(w, DeliveryMethod.ReliableOrdered);
                }

                // Host Client Host Buff FX 
                if (target.IsMainCharacter)
                {
                    var w2 = new NetDataWriter();
                    w2.Put((byte)Op.HOST_BUFF_PROXY_APPLY);
                    // Player Host endPoint InitializeLocalPlayer "Host:Port"
                    w2.Put(mod.localPlayerStatus?.EndPoint ?? $"Host:{mod.port}");
                    w2.Put(overrideWeaponID);
                    w2.Put(buffPrefab.ID);
                    mod.netManager.SendToAll(w2, DeliveryMethod.ReliableOrdered);
                }
            }
        }
    }


    [HarmonyPatch(typeof(SceneLoaderProxy), "LoadScene")]
    public static class Patch_SceneLoaderProxy_Authority
    {
        static bool Prefix(SceneLoaderProxy __instance)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted) return true;
            if (mod.allowLocalSceneLoad) return true;


            string proxySceneId = Traverse.Create(__instance).Field<string>("sceneID").Value;
            bool useLoc = Traverse.Create(__instance).Field<bool>("useLocation").Value;
            var loc = Traverse.Create(__instance).Field<Duckov.Scenes.MultiSceneLocation>("location").Value;
            var curtain = Traverse.Create(__instance).Field<Eflatun.SceneReference.SceneReference>("overrideCurtainScene").Value;
            bool notifyEvac = Traverse.Create(__instance).Field<bool>("notifyEvacuation").Value;
            bool save = Traverse.Create(__instance).Field<bool>("saveToFile").Value;

            string targetId = proxySceneId;
            string locationName = useLoc ? loc.LocationName : null;
            string curtainGuid = (curtain != null) ? curtain.Guid : null;

            if (mod.IsServer)
            {
                mod.Host_BeginSceneVote_Simple(targetId, curtainGuid, notifyEvac, save, useLoc, locationName);
                return false;
            }
            else
            {

                mod.Client_RequestBeginSceneVote(targetId, curtainGuid, notifyEvac, save, useLoc, locationName);
                //string mySceneId = null;
                //try { mySceneId = mod.localPlayerStatus != null ? mod.localPlayerStatus.SceneId : null; } catch { } 

                //ModBehaviour.PlayerStatus host = null;
                //if (mod.clientPlayerStatuses != null)
                //{
                // foreach (var kv in mod.clientPlayerStatuses)
                // {
                // var st = kv.Value;
                // if (st == null) continue;
                // bool isHostName = false;
                // try { isHostName = (st.PlayerName == "Host"); } catch { }
                // bool isHostId = false;
                // try { isHostId = (!string.IsNullOrEmpty(st.EndPoint) && st.EndPoint.StartsWith("Host:")); } catch { }

                // if (isHostName || isHostId) { host = st; break; }
                // }
                //}

                //bool hostMissing = (host == null);

                //bool hostNotInGame = false;
                //try { hostNotInGame = (host != null && !host.IsInGame); } catch { } 

                //bool hostSceneDiff = false;
                //try
                //{
                // string hostSid = (host != null) ? host.SceneId : null;
                // hostSceneDiff = (!string.IsNullOrEmpty(hostSid) && !string.IsNullOrEmpty(mySceneId) && !string.Equals(hostSid, mySceneId, StringComparison.Ordinal));
                //}
                //catch { }

                //bool hostDead = false;
                //try
                //{
                // // Host EndPoint "Host:{port}" d1 Mod.cs.InitializeLocalPlayer 
                // string hostKey = $"Host:{mod.port}";

                // if (mod.clientRemoteCharacters != null &&
                // mod.clientRemoteCharacters.TryGetValue(hostKey, out var hostProxy) &&
                // hostProxy)
                // {
                // var h = hostProxy.GetComponentInChildren<Health>(true);
                // hostDead = (h == null) || h.CurrentHealth <= 0.001f;
                // }
                // else
                // {
                // // HostStatus Host 
                // if (!hostMissing && !hostSceneDiff) hostDead = true;
                // }
                //}
                //catch { }

                //// allow hostDead 
                //bool allowClientVote = hostMissing || hostNotInGame || hostSceneDiff || hostDead;

                //if (allowClientVote)
                //{
                // Debug.Log($"[SCENE] Client target={targetId}, hostMissing={hostMissing}, hostNotInGame={hostNotInGame}, hostSceneDiff={hostSceneDiff}");
                // mod.Client_RequestBeginSceneVote(targetId, curtainGuid, notifyEvac, save, useLoc, locationName);
                // return false;
                //}
                #if DEBUG
                Debug.Log($"[SCENE] Clientrelease scene gate (allow vote):target={targetId}");
                #endif
                return false;
            }
        }
    }


    [HarmonyPatch(typeof(InteractableLootbox), "StartLoot")]
    static class Patch_Lootbox_StartLoot_RequestState
    {
        static void Postfix(InteractableLootbox __instance, ref bool __result)
        {
            if (!__result) return;

            var m = ModBehaviour.Instance;
            if (m == null || !m.networkStarted || m.IsServer) return;

            var inv = __instance ? __instance.Inventory : null;
            if (inv == null) return;

            inv.Loading = true;                 // UI
            m.Client_RequestLootState(inv);     // 
            m.KickLootTimeout(inv, 1.5f);       // 1.5s 
        }
    }


    [HarmonyPatch(typeof(InteractableLootbox), "OnInteractStop")]
    static class Patch_Lootbox_OnInteractStop_DisableFogWhenAllInspected
    {
        static void Postfix(InteractableLootbox __instance)
        {
            var inv = __instance?.Inventory;
            if (inv == null) return;

            // 
            bool allInspected = true;
            int last = inv.GetLastItemPosition();
            for (int i = 0; i <= last; i++)
            {
                var it = inv.GetItemAt(i);
                if (it != null && !it.Inspected) { allInspected = false; break; }
            }

            if (allInspected)
            {
                inv.NeedInspection = false; 
            }
   
        }
    }

  

    [HarmonyPatch(typeof(ItemStatsSystem.Inventory), "AddAt")]
    static class Patch_Inventory_AddAt_LootPut
    {
        static bool Prefix(ItemStatsSystem.Inventory __instance, ItemStatsSystem.Item item, int atPosition, ref bool __result)
        {
            var m = ModBehaviour.Instance;
            if (m == null || !m.networkStarted) return true;

            // Client Server 
            if (!m.IsServer && !m._applyingLootState)
            {
                bool targetIsLoot = LootboxDetectUtil.IsLootboxInventory(__instance) && !LootboxDetectUtil.IsPrivateInventory(__instance);
                var srcInv = item ? item.InInventory : null;
                bool srcIsLoot = LootboxDetectUtil.IsLootboxInventory(srcInv) && !LootboxDetectUtil.IsPrivateInventory(srcInv);

                // === A) ===
                if (targetIsLoot && ReferenceEquals(srcInv, __instance))
                {
                    int srcPos = __instance.GetIndex(item);
                    if (srcPos == atPosition) { __result = true; return false; }

                    uint tk = m.Client_SendLootTakeRequest(__instance, srcPos, null, -1, null);
                    m.NoteLootReorderPending(tk, __instance, atPosition);
                    __result = true;
                    return false;
                }

                // === B) -> ===
                if (targetIsLoot && srcInv && !ReferenceEquals(srcInv, __instance))
                {
                    int srcPos = srcInv.GetIndex(item);
                    if (srcPos >= 0)
                    {
                        m.Client_SendLootTakeRequest(srcInv, srcPos, __instance, atPosition, null);
                        __result = true;
                        return false;
                    }
                }

                // === C) -> PUT ===
                if (!targetIsLoot && srcIsLoot)
                {
                    int srcPos = srcInv.GetIndex(item);
                    if (srcPos >= 0)
                    {
                        m.Client_SendLootTakeRequest(srcInv, srcPos, __instance, atPosition, null);
                        __result = true;
                        return false;
                    }
                }

                // === D) UI / ===
                bool isLootInv = LootboxDetectUtil.IsLootboxInventory(__instance) && !LootboxDetectUtil.IsPrivateInventory(__instance);
                if (isLootInv)
                {
                    m.Client_SendLootPutRequest(__instance, item, atPosition);
                    __result = false;
                    return false;
                }
            }

            return true;
        }
    }


    [HarmonyPatch(typeof(ItemStatsSystem.Inventory), "AddItem")]
    static class Patch_Inventory_AddItem_LootPut
    {
        static bool Prefix(ItemStatsSystem.Inventory __instance, ItemStatsSystem.Item item, ref bool __result)
        {
            var m = ModBehaviour.Instance;
            if (m == null || !m.networkStarted) return true;

            // Add
            if (!m.IsServer && m.ClientLootSetupActive)
            {
                bool isLootInv = LootboxDetectUtil.IsLootboxInventory(__instance)
                                 && !LootboxDetectUtil.IsPrivateInventory(__instance);
                if (isLootInv)
                {
                    try { if (item) { item.Detach(); UnityEngine.Object.Destroy(item.gameObject); } } catch { }
                    __result = true;
                    return false;
                }
            }

            if (!m.IsServer && !m._applyingLootState)
            {
                bool isLootInv = LootboxDetectUtil.IsLootboxInventory(__instance)
                                 && !LootboxDetectUtil.IsPrivateInventory(__instance);
                if (isLootInv)
                {
                    m.Client_SendLootPutRequest(__instance, item, 0);
                    __result = false;
                    return false;
                }
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(ItemUtilities), "AddAndMerge")]
    static class Patch_ItemUtilities_AddAndMerge_LootPut
    {
        static bool Prefix(ItemStatsSystem.Inventory inventory, ItemStatsSystem.Item item, int preferedFirstPosition, ref bool __result)
        {
            var m = ModBehaviour.Instance;
            if (m == null || !m.networkStarted) return true;

            // 
            if (!m.IsServer && m.ClientLootSetupActive)
            {
                bool isLootInv = LootboxDetectUtil.IsLootboxInventory(inventory)
                                 && !LootboxDetectUtil.IsPrivateInventory(inventory);
                if (isLootInv)
                {
                    try { if (item) { item.Detach(); UnityEngine.Object.Destroy(item.gameObject); } } catch { }
                    __result = true;
                    return false;
                }
            }

            if (!m.IsServer && !m._applyingLootState)
            {
                bool isLootInv = LootboxDetectUtil.IsLootboxInventory(inventory)
                                 && !LootboxDetectUtil.IsPrivateInventory(inventory);
                if (isLootInv)
                {
                    m.Client_SendLootPutRequest(inventory, item, preferedFirstPosition);
                    __result = false;
                    return false;
                }
            }

            return true;
        }

        static void Postfix(ItemStatsSystem.Inventory inventory, ItemStatsSystem.Item item, int preferedFirstPosition, bool __result)
        {
            var m = ModBehaviour.Instance;
            if (m == null || !m.networkStarted || !m.IsServer) return;
            if (!__result || m._serverApplyingLoot) return;

            bool isLootInv = LootboxDetectUtil.IsLootboxInventory(inventory) && !LootboxDetectUtil.IsPrivateInventory(inventory);
            if (isLootInv)
                m.Server_SendLootboxState(null, inventory);
        }
    }



    [HarmonyPatch(typeof(LootBoxLoader), "Setup")]
    static class Patch_LootBoxLoader_Setup_GuardClientInit
    {
        static void Prefix()
        {
            var m = ModBehaviour.Instance;
            if (m != null && m.networkStarted && !m.IsServer)
                m._clientLootSetupDepth++;
        }

        // Finalizer 
        static void Finalizer(Exception __exception)
        {
            var m = ModBehaviour.Instance;
            if (m != null && m.networkStarted && !m.IsServer && m._clientLootSetupDepth > 0)
                m._clientLootSetupDepth--;
        }
    }


    [HarmonyPatch(typeof(LootBoxLoader), "Setup")]
    static class Patch_LootBoxLoader_Setup_BroadcastOnServer
    {
        static async void Postfix(LootBoxLoader __instance)
        {
            var m = ModBehaviour.Instance;
            if (m == null || !m.networkStarted || !m.IsServer) return;
            await Cysharp.Threading.Tasks.UniTask.Yield(); // 
            var box = __instance ? __instance.GetComponent<InteractableLootbox>() : null;
            var inv = box ? box.Inventory : null;
            if (inv != null) m.Server_SendLootboxState(null, inv);
        }
    }

    // === Host Inventory.AddAt Succeeded ===
    [HarmonyPatch(typeof(ItemStatsSystem.Inventory), "AddAt")]
    static class Patch_Inventory_AddAt_BroadcastOnServer
    {
        static void Postfix(ItemStatsSystem.Inventory __instance, ItemStatsSystem.Item item, int atPosition, bool __result)
        {

            var m = ModBehaviour.Instance;
            if (m == null || !m.networkStarted || !m.IsServer) return;
            if (!__result || m._serverApplyingLoot) return;
            if (!LootboxDetectUtil.IsLootboxInventory(__instance)) return;

            if (!LootboxDetectUtil.IsLootboxInventory(__instance) || LootboxDetectUtil.IsPrivateInventory(__instance)) return;

            m.Server_SendLootboxState(null, __instance);
        }
    }

    // === Host Inventory.AddItem Succeeded ===
    [HarmonyPatch(typeof(ItemStatsSystem.Inventory), "AddItem")]
    static class Patch_Inventory_AddItem_BroadcastLootState
    {
        static void Postfix(ItemStatsSystem.Inventory __instance, ItemStatsSystem.Item item, bool __result)
        {
            var m = ModBehaviour.Instance;
            if (m == null || !m.networkStarted || !m.IsServer) return;
            if (!__result || m._serverApplyingLoot) return;

            if (!LootboxDetectUtil.IsLootboxInventory(__instance) || LootboxDetectUtil.IsPrivateInventory(__instance)) return;


            var dict = InteractableLootbox.Inventories;
            bool isLootInv = dict != null && dict.ContainsValue(__instance);
            if (!isLootInv) return;

            m.Server_SendLootboxState(null, __instance);
        }
    }


    [HarmonyPatch(typeof(LootBoxLoader), "RandomActive")]
    static class Patch_LootBoxLoader_RandomActive_NetAuthority
    {
        static bool Prefix(LootBoxLoader __instance)
        {
            var m = ModBehaviour.Instance;
            if (m == null || !m.networkStarted || m.IsServer) return true;

            try
            {
                var core = MultiSceneCore.Instance;
                if (core == null) return true; // core 

                // key GetKey 
                int key = ModBehaviour_ComputeLootKeyCompat(__instance.transform);


                if (core.inLevelData != null && core.inLevelData.TryGetValue(key, out object obj) && obj is bool on)
                {
                    __instance.gameObject.SetActive(on);
                }
                else
                {
                    __instance.gameObject.SetActive(false); // 
                }

                return false; // 
            }
            catch
            {
                return true; // 
            }
        }

        static int ModBehaviour_ComputeLootKeyCompat(Transform t)
        {
            if (t == null) return 0;
            var v = t.position * 10f;
            int x = Mathf.RoundToInt(v.x);
            int y = Mathf.RoundToInt(v.y);
            int z = Mathf.RoundToInt(v.z);
            var v3i = new Vector3Int(x, y, z);
            return v3i.GetHashCode();
        }

    }

    [HarmonyPatch(typeof(InteractableLootbox), "get_Inventory")]
    static class Patch_Lootbox_GetInventory_Safe
    {
        // / 
        static System.Exception Finalizer(InteractableLootbox __instance, ref Inventory __result, System.Exception __exception)
        {
            try
            {
                if (__instance != null && (__exception != null || __result == null))
                {
                    var mCreate = AccessTools.Method(typeof(InteractableLootbox), "GetOrCreateInventory", new System.Type[] { typeof(InteractableLootbox) });
                    if (mCreate != null)
                    {
                        var inv = (Inventory)mCreate.Invoke(null, new object[] { __instance });
                        if (inv != null)
                        {
                            __result = inv;
                            return null; // 
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        static void Postfix(InteractableLootbox __instance, ref Inventory __result)
        {
            if (__result != null) return;

            // LevelManager.LootBoxInventories 
            try
            {
                int key = ModBehaviour.Instance != null
                          ? ModBehaviour.Instance.ComputeLootKey(__instance.transform)
                          : __instance.GetHashCode();

                // InteractableLootbox.Inventories
                var dict1 = InteractableLootbox.Inventories;
                if (dict1 != null && dict1.TryGetValue(key, out var inv1) && inv1)
                {
                    __result = inv1;
                    return;
                }

                // LevelManager.LootBoxInventories
                var lm = LevelManager.Instance;
                var dict2 = lm != null ? LevelManager.LootBoxInventories : null;
                if (dict2 != null && dict2.TryGetValue(key, out var inv2) && inv2)
                {
                    __result = inv2;

                    // InteractableLootbox.Inventories 
                    try { if (dict1 != null) dict1[key] = inv2; } catch { }
                }
            }
            catch { }
        }
    }


    [HarmonyPatch(typeof(InteractableLootbox), "get_Inventory")]
    static class Patch_Lootbox_GetInventory_Register
    {
        static void Postfix(InteractableLootbox __instance, ref ItemStatsSystem.Inventory __result)
        {
            try
            {
                if (!__result) return;

                int key = (ModBehaviour.Instance != null)
                          ? ModBehaviour.Instance.ComputeLootKey(__instance.transform)
                          : __instance.GetHashCode();

                var dictA = InteractableLootbox.Inventories;
                if (dictA != null) dictA[key] = __result;

                var lm = LevelManager.Instance;
                var dictB = lm != null ? LevelManager.LootBoxInventories : null;
                if (dictB != null) dictB[key] = __result;
            }
            catch { }
        }
    }

    public sealed class NetAiTag : MonoBehaviour
    {
        public int aiId;
        public int? iconTypeOverride;   // Host CharacterIconTypes int 
        public bool? showNameOverride;  // Host 
        public string nameOverride;     // Host 

        void Awake() { Guard(); }
        void OnEnable() { Guard(); }

        void Guard()
        {
            try
            {
                var cmc = GetComponent<CharacterMainControl>();
                var mod = ModBehaviour.Instance;
                if (!cmc || mod == null) return;

                if (!mod.IsRealAI(cmc))
                {
                    Destroy(this);
                }
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(CharacterSpawnerRoot), "StartSpawn")]
    static class Patch_Root_StartSpawn
    {
        static readonly System.Collections.Generic.HashSet<int> _waiting = new HashSet<int>();
        static readonly System.Collections.Generic.Stack<UnityEngine.Random.State> _rngStack = new Stack<UnityEngine.Random.State>();
        static readonly System.Reflection.MethodInfo _miStartSpawn =
            AccessTools.Method(typeof(CharacterSpawnerRoot), "StartSpawn");

        static bool Prefix(CharacterSpawnerRoot __instance)
        {
            try
            {
                var mod = ModBehaviour.Instance;
                int rootId = mod.StableRootId(__instance);

                // :) Waiting StartSpawn()
                if (!mod.IsServer && !mod.aiRootSeeds.ContainsKey(rootId))
                {
                    if (_waiting.Add(rootId))
                        __instance.StartCoroutine(WaitSeedAndSpawn(__instance, rootId));
                    return false;
                }

                // 
                int useSeed = mod.IsServer ? mod.DeriveSeed(mod.sceneSeed, rootId) : mod.aiRootSeeds[rootId];
                _rngStack.Push(UnityEngine.Random.state);
                UnityEngine.Random.InitState(useSeed);
                return true;
            }
            catch { return true; }
        }

        static void ForceActivateHierarchy(Transform t)
        {
            while (t)
            {
                if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                t = t.parent;
            }
        }

        static System.Collections.IEnumerator WaitSeedAndSpawn(CharacterSpawnerRoot inst, int rootId)
        {
            var mod = ModBehaviour.Instance;
            while (mod && !mod.aiRootSeeds.ContainsKey(rootId)) yield return null;

            _waiting.Remove(rootId);

            if (inst)
            {
                // Failed
                ForceActivateHierarchy(inst.transform);

                if (_miStartSpawn != null)
                    _miStartSpawn.Invoke(inst, null);  // private StartSpawn()
            }
        }

        static void Postfix(CharacterSpawnerRoot __instance)
        {
            try
            {
                if (_rngStack.Count > 0) UnityEngine.Random.state = _rngStack.Pop();

                // AI / / Host 
                var list = Traverse.Create(__instance)
                    .Field<System.Collections.Generic.List<CharacterMainControl>>("createdCharacters")
                    .Value;

                if (list != null && ModBehaviour.Instance.freezeAI)
                    foreach (var c in list) ModBehaviour.Instance.TryFreezeAI(c);

                if (list != null)
                {
                    var mod = ModBehaviour.Instance;
                    int rootId = mod.StableRootId(__instance);

                    // + + InstanceID 
                    var ordered = new List<CharacterMainControl>(list);
                    ordered.RemoveAll(c => !c);
                    ordered.Sort((a, b) =>
                    {
                        int n = string.Compare(a.name, b.name, StringComparison.Ordinal);
                        if (n != 0) return n;
                        var pa = a.transform.position; var pb = b.transform.position;
                        int ax = Mathf.RoundToInt(pa.x * 100f), az = Mathf.RoundToInt(pa.z * 100f), ay = Mathf.RoundToInt(pa.y * 100f);
                        int bx = Mathf.RoundToInt(pb.x * 100f), bz = Mathf.RoundToInt(pb.z * 100f), by = Mathf.RoundToInt(pb.y * 100f);
                        if (ax != bx) return ax.CompareTo(bx);
                        if (az != bz) return az.CompareTo(bz);
                        if (ay != by) return ay.CompareTo(by);
                        return a.GetInstanceID().CompareTo(b.GetInstanceID());
                    });

                    for (int i = 0; i < ordered.Count; i++)
                    {
                        var cmc = ordered[i];
                        if (!cmc || !mod.IsRealAI(cmc)) continue;

                        int aiId = mod.DeriveSeed(rootId, i + 1);
                        var tag = cmc.GetComponent<NetAiTag>() ?? cmc.gameObject.AddComponent<NetAiTag>();

                        // Host id + + Client tag.aiId=0 Waiting A 
                        if (mod.IsServer)
                        {
                            tag.aiId = aiId;
                            mod.RegisterAi(aiId, cmc);
                            mod.Server_BroadcastAiLoadout(aiId, cmc);
                        }
                    }


                    // Host root 
                    if (mod.IsServer)
                    {
                        mod.Server_BroadcastAiTransforms();
                    }
                }
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(CharacterSpawnerRoot), "Init")]
    static class Patch_Root_Init_FixContain
    {
        static bool Prefix(CharacterSpawnerRoot __instance)
        {
            try
            {
                var msc = Duckov.Scenes.MultiSceneCore.Instance;

                // SpawnerGuid != 0 
                if (msc != null && __instance.SpawnerGuid != 0 &&
                    msc.usedCreatorIds.Contains(__instance.SpawnerGuid))
                {
                    return true; // 
                }

                var tr = Traverse.Create(__instance);
                tr.Field("inited").SetValue(true);

                var spComp = tr.Field<CharacterSpawnerComponentBase>("spawnerComponent").Value;
                if (spComp != null) spComp.Init(__instance);

                int buildIndex = UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex;
                tr.Field("relatedScene").SetValue(buildIndex);

                __instance.transform.SetParent(null);
                if (msc != null)
                {
                    Duckov.Scenes.MultiSceneCore.MoveToMainScene(__instance.gameObject);
                    // Guid 0 0 
                    if (__instance.SpawnerGuid != 0)
                        msc.usedCreatorIds.Add(__instance.SpawnerGuid);
                }

                var mod = ModBehaviour.Instance;
                if (mod != null && mod.IsServer) mod.Server_SendRootSeedDelta(__instance);


                return false; // Init 
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AI-SEED] Patch_Root_Init_FixContain failed: " + e);
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(CharacterSpawnerRoot), "Update")]
    static class Patch_Root_Update_ClientAutoSpawn
    {
        static readonly MethodInfo _miStartSpawn =
            AccessTools.Method(typeof(CharacterSpawnerRoot), "StartSpawn");
        static readonly MethodInfo _miCheckTiming =
            AccessTools.Method(typeof(CharacterSpawnerRoot), "CheckTiming"); 

        static void Postfix(CharacterSpawnerRoot __instance)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted || mod.IsServer) return;

            var tr = Traverse.Create(__instance);
            bool inited = tr.Field<bool>("inited").Value;
            bool created = tr.Field<bool>("created").Value;
            if (!inited || created) return;

            int rootId = mod.StableRootId(__instance);

            // AltId 
            if (!mod.aiRootSeeds.ContainsKey(rootId))
            {
                int altId = mod.StableRootId_Alt(__instance);
                if (mod.aiRootSeeds.TryGetValue(altId, out var seed))
                    mod.aiRootSeeds[rootId] = seed;
                else
                    return; // 
            }

            // / / 
            bool ok = false;
            try { ok = (bool)_miCheckTiming.Invoke(__instance, null); } catch { }
            if (!ok) return;

            // 
            ForceActivateHierarchy(__instance.transform);
            try { _miStartSpawn?.Invoke(__instance, null); } catch { }
        }

        static void ForceActivateHierarchy(Transform t)
        {
            while (t)
            {
                if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                t = t.parent;
            }
        }
    }



    [HarmonyPatch(typeof(CharacterSpawnerGroup), "Awake")]
    static class Patch_Group_Awake
    {
        static void Postfix(CharacterSpawnerGroup __instance)
        {
            try
            {
                var mod = ModBehaviour.Instance;

                // + Group Transform 
                int gid = mod.StableHash(mod.TransformPath(__instance.transform));
                int seed = mod.DeriveSeed(mod.sceneSeed, gid);

                var rng = new System.Random(seed);
                if (__instance.hasLeader)
                {
                    // = hasLeaderChance
                    bool keep = rng.NextDouble() <= __instance.hasLeaderChance;
                    __instance.hasLeader = keep;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AI-SEED] Group.Awake Postfix error: {e.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(AICharacterController), "Init")]
    static class Patch_AI_Init
    {
        static void Postfix(AICharacterController __instance, CharacterMainControl _characterMainControl)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted) return;
            if (!mod.IsRealAI(_characterMainControl)) return;

            var cmc = _characterMainControl;
            if (mod.freezeAI) mod.TryFreezeAI(cmc);

            // 1) / 
            var tag = cmc.GetComponent<NetAiTag>() ?? cmc.gameObject.AddComponent<NetAiTag>();

            // Client / aiId Host 
            if (!mod.IsServer)
            {
                ModBehaviour.Instance.MarkAiSceneReady(); // Client 
                return;
            }

            // 2) Host aiId Press rootId + 
            if (tag.aiId == 0)
            {
                int rootId = 0;
                var root = cmc.GetComponentInParent<CharacterSpawnerRoot>();
                rootId = (root && root.SpawnerGuid != 0)
                    ? root.SpawnerGuid
                    : mod.StableHash(mod.TransformPath(root ? root.transform : cmc.transform));
                int serial = mod.NextAiSerial(rootId);
                tag.aiId = mod.DeriveSeed(rootId, serial);
            }

            // 3) Host Client Host 
            mod.RegisterAi(tag.aiId, cmc);
            ModBehaviour.Instance.MarkAiSceneReady();
        }
    }



    [HarmonyPatch(typeof(Duckov.Utilities.SetActiveByPlayerDistance), "FixedUpdate")]
    static class Patch_SABPD_FixedUpdate_AllPlayersUnion
    {
        static bool Prefix(Duckov.Utilities.SetActiveByPlayerDistance __instance)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted) return true; // 

            var tr = Traverse.Create(__instance);

            // 
            var list = tr.Field<List<GameObject>>("cachedListRef").Value;
            if (list == null) return false;

            // 
            float dist;
            var prop = AccessTools.Property(__instance.GetType(), "Distance");
            if (prop != null) dist = (float)prop.GetValue(__instance, null);
            else dist = tr.Field<float>("distance").Value;
            float d2 = dist * dist;

            // === Player + ===
            var sources = new List<Vector3>(8);
            var main = CharacterMainControl.Main;
            if (main) sources.Add(main.transform.position);

            foreach (var kv in mod.playerStatuses)
            {
                var st = kv.Value;
                if (st != null && st.IsInGame) sources.Add(st.Position);
            }

            // 
            if (sources.Count == 0) return true;

            // Player 
            for (int i = 0; i < list.Count; i++)
            {
                var go = list[i];
                if (!go) continue;

                bool within = false;
                var p = go.transform.position;
                for (int s = 0; s < sources.Count; s++)
                {
                    if ((p - sources[s]).sqrMagnitude <= d2) { within = true; break; }
                }
                if (go.activeSelf != within) go.SetActive(within);
            }

            return false; // 

        }

    }

    [HarmonyPatch(typeof(CharacterAnimationControl_MagicBlend), "OnAttack")]
    static class Patch_AI_OnAttack_Broadcast
    {
        static void Postfix(CharacterAnimationControl_MagicBlend __instance)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.IsServer) return;

            var cmc = __instance.characterMainControl;
            if (!cmc) return;

            // Player AI 
            // AI / NetAiTag AI id 
            var aiCtrl = cmc.GetComponent<AICharacterController>();
            var aiTag = cmc.GetComponent<NetAiTag>();
            if (!aiCtrl && aiTag == null) return;

            int aiId = aiTag != null ? aiTag.aiId : 0;
            if (aiId == 0) return;

            mod.writer.Reset();
            mod.writer.Put((byte)Op.AI_ATTACK_SWING);
            mod.writer.Put(aiId);
            mod.netManager.SendToAll(mod.writer, DeliveryMethod.ReliableUnordered);
        }
    }

    [HarmonyPatch(typeof(CharacterMainControl), "OnChangeItemAgentChangedFunc")]
    static class Patch_CMC_OnChangeHold_AIRebroadcast
    {
        static void Postfix(CharacterMainControl __instance)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted || !mod.IsServer) return; // Host 
            if (!__instance || __instance == CharacterMainControl.Main) return; // Player

            var tag = __instance.GetComponent<NetAiTag>();
            if (tag == null || tag.aiId == 0) return;

            // AI Switch/ / + 
            mod.Server_BroadcastAiLoadout(tag.aiId, __instance);
        }
    }

    [HarmonyPatch(typeof(CharacterAnimationControl_MagicBlend), "OnAttack")]
    static class Patch_AI_OnAttack_BroadcastAll
    {
        static void Postfix(CharacterAnimationControl_MagicBlend __instance)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted || !mod.IsServer) return;

            var cmc = __instance ? __instance.characterMainControl : null;
            if (!cmc) return;

            if (cmc.IsMainCharacter) return;

            // NetAiTag aiId
            var tag = cmc.GetComponent<NetAiTag>();
            if (tag == null || tag.aiId == 0) return;

            // AI Projectile.Init/Postfix FIRE_EVENT
            var gun = cmc.GetGun();
            if (gun != null)
            {
                return;
            }

            // Player MELEE_ATTACK_SWING
            var w = new NetDataWriter();
            w.Put((byte)Op.MELEE_ATTACK_SWING);
            w.Put($"AI:{tag.aiId}");
            w.Put(__instance.attackTime); // 
            mod.netManager.SendToAll(w, DeliveryMethod.ReliableOrdered);
        }

    }

    [HarmonyPatch(typeof(CharacterAnimationControl_MagicBlend), "OnAttack")]
    static class Patch_AI_OnAttack_MeleeOnly
    {
        static void Postfix(CharacterAnimationControl_MagicBlend __instance)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted || !mod.IsServer) return;

            var cmc = __instance ? __instance.characterMainControl : null;
            if (!cmc || cmc.IsMainCharacter) return;

            var tag = cmc.GetComponent<NetAiTag>();
            if (tag == null || tag.aiId == 0) return;

            // Player MELEE_ATTACK_SWING 
            var gun = cmc.GetGun();
            if (gun != null) return; // Projectile.Init 

            var w = new NetDataWriter();
            w.Put((byte)Op.MELEE_ATTACK_SWING);
            w.Put($"AI:{tag.aiId}");
            mod.netManager.SendToAll(w, DeliveryMethod.ReliableUnordered);
        }
    }



    [HarmonyPatch(typeof(CharacterMainControl), nameof(CharacterMainControl.SetCharacterModel))]
    static class Patch_CMC_SetCharacterModel_FaceReapply
    {
        static void Postfix(CharacterMainControl __instance)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || mod.IsServer) return;
            mod.ReapplyFaceIfKnown(__instance);
        }
    }

    [HarmonyPatch(typeof(CharacterMainControl), nameof(CharacterMainControl.SetCharacterModel))]
    static class Patch_CMC_SetCharacterModel_FaceReapply_Client
    {
        static void Postfix(CharacterMainControl __instance)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || mod.IsServer) return; // Client
            mod.ReapplyFaceIfKnown(__instance);
        }
    }

    // Host / / Status 
    [HarmonyPatch(typeof(CharacterMainControl), nameof(CharacterMainControl.SetCharacterModel))]
    static class Patch_CMC_SetCharacterModel_Rebroadcast_Server
    {
        static void Postfix(CharacterMainControl __instance)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.IsServer) return;

            int aiId = -1;
            foreach (var kv in mod.aiById) { if (kv.Value == __instance) { aiId = kv.Key; break; } }
            if (aiId < 0) return;

            if (ModBehaviour.LogAiLoadoutDebug)
            {
                #if DEBUG
                Debug.Log($"[AI-REBROADCAST] aiId={aiId} after SetCharacterModel");
                #endif
            }
            mod.Server_BroadcastAiLoadout(aiId, __instance);
        }
    }

    [HarmonyPatch(typeof(Health), "Hurt", new[] { typeof(global::DamageInfo) })]
    static class Patch_AIHealth_Hurt_HostAuthority
    {
        [HarmonyPriority(Priority.High)]
        static bool Prefix(Health __instance, ref global::DamageInfo damageInfo)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted) return true;
            if (mod.IsServer) return true;            // Host 
            bool isMain = false; try { isMain = __instance.IsMainCharacterHealth; } catch { }
            if (isMain) return true;

            if (__instance.gameObject.GetComponent<AutoRequestHealthBar>() != null)
            {
                return false;
            }

            // AI
            CharacterMainControl victim = null;
            try { victim = __instance.TryGetCharacter(); } catch { }
            if (!victim) { try { victim = __instance.GetComponentInParent<CharacterMainControl>(); } catch { } }

            bool victimIsAI = victim &&
                              (victim.GetComponent<AICharacterController>() != null ||
                               victim.GetComponent<NetAiTag>() != null);
            if (!victimIsAI) return true;

            // AI AI 
            var attacker = damageInfo.fromCharacter;
            bool attackerIsAI = attacker &&
                                (attacker.GetComponent<AICharacterController>() != null ||
                                 attacker.GetComponent<NetAiTag>() != null);
            if (attackerIsAI)
                return false; // AI AI 


          // LocalHitKillFx.ClientPlayForAI(victim, damageInfo, predictedDead: false);

            return false; 
        }

        // Host AI 
        static void Postfix(Health __instance, global::DamageInfo damageInfo)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted || !mod.IsServer) return;

            var cmc = __instance.TryGetCharacter();
            if (!cmc) { try { cmc = __instance.GetComponentInParent<CharacterMainControl>(); } catch { } }
            if (!cmc) return;

            var tag = cmc.GetComponent<NetAiTag>();
            if (!tag) return;

            if (ModBehaviour.LogAiHpDebug)
                Debug.Log($"[AI-HP][SERVER] Hurt => broadcast aiId={tag.aiId} cur={__instance.CurrentHealth}");
            mod.Server_BroadcastAiHealth(tag.aiId, __instance.MaxHealth, __instance.CurrentHealth);
        }
    }

    // / Host 
    [HarmonyPatch(typeof(Health), "SetHealth")]
    static class Patch_AIHealth_SetHealth_Broadcast
    {
        static void Postfix(Health __instance, float healthValue)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted || !mod.IsServer) return;

            var cmc = __instance.TryGetCharacter();
            if (!cmc) { try { cmc = __instance.GetComponentInParent<CharacterMainControl>(); } catch { } }
            if (!cmc || !cmc.GetComponent<NetAiTag>()) return;

            var tag = cmc.GetComponent<NetAiTag>();
            if (!tag) return;

            if (ModBehaviour.LogAiHpDebug) Debug.Log($"[AI-HP][SERVER] SetHealth => broadcast aiId={tag.aiId} cur={__instance.CurrentHealth}");
            mod.Server_BroadcastAiHealth(tag.aiId, __instance.MaxHealth, __instance.CurrentHealth);
        }
    }

    [HarmonyPatch(typeof(Health), "AddHealth")]
    static class Patch_AIHealth_AddHealth_Broadcast
    {
        static void Postfix(Health __instance, float healthValue)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted || !mod.IsServer) return;

            var cmc = __instance.TryGetCharacter();
            if (!cmc) { try { cmc = __instance.GetComponentInParent<CharacterMainControl>(); } catch { } }
            if (!cmc || !cmc.GetComponent<NetAiTag>()) return;

            var tag = cmc.GetComponent<NetAiTag>();
            if (!tag) return;

            if (ModBehaviour.LogAiHpDebug) Debug.Log($"[AI-HP][SERVER] AddHealth => broadcast aiId={tag.aiId} cur={__instance.CurrentHealth}");
            mod.Server_BroadcastAiHealth(tag.aiId, __instance.MaxHealth, __instance.CurrentHealth);
        }
    }

    internal static class DeadLootSpawnContext
    {
        [ThreadStatic] public static CharacterMainControl InOnDead;
    }

    // / OnDead / AI Player 
    [HarmonyPatch(typeof(CharacterMainControl), "OnDead")]
    static class Patch_CMC_OnDead_Mark
    {
        static void Prefix(CharacterMainControl __instance)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted) return;
            if (!__instance) return;

            // AI Player 
            if (__instance == CharacterMainControl.Main) return;
            bool isAI = __instance.GetComponent<AICharacterController>() != null
                        || __instance.GetComponent<NetAiTag>() != null;
            if (!isAI) return;

            DeadLootSpawnContext.InOnDead = __instance;
        }
        static void Finalizer()
        {
            DeadLootSpawnContext.InOnDead = null;
        }
    }


    // Client 
    [HarmonyPatch(typeof(InteractableLootbox), "CreateFromItem")]
    static class Patch_Lootbox_CreateFromItem_BlockClient
    {
        static bool Prefix()
        {
            var mod = ModBehaviour.Instance;
            if (mod != null && mod.networkStarted && !mod.IsServer && DeadLootSpawnContext.InOnDead != null)
                return false; // Client OnDead 
            return true;
        }
    }

    // CreateFromItem spawn + state
    [HarmonyPatch(typeof(InteractableLootbox), "CreateFromItem")]
    static class Patch_Lootbox_CreateFromItem_DeferredSpawn
    {
        static void Postfix(InteractableLootbox __result)
        {
            var mod = ModBehaviour.Instance;
            var dead = DeadLootSpawnContext.InOnDead;
            if (mod == null || !mod.networkStarted || !mod.IsServer) return;
            if (dead == null || !__result) return;

            mod.StartCoroutine(DeferredSpawn(__result, dead));
        }

        static System.Collections.IEnumerator DeferredSpawn(InteractableLootbox box, CharacterMainControl who)
        {
            yield return null; 
            var mod = ModBehaviour.Instance;
            if (mod && mod.IsServer && box) mod.Server_OnDeadLootboxSpawned(box, who);
        }
    }

    [HarmonyPatch(typeof(InteractableLootbox), "CreateFromItem")]
    static class Patch_Lootbox_CreateFromItem_Register
    {
        static void Postfix(InteractableLootbox __result)
        {
            // No-op: legacy registration path disabled; other patches maintain the mapping.
        }
    }

    [HarmonyPatch(typeof(Duckov.UI.HealthBar), "RefreshCharacterIcon")]
    static class Patch_HealthBar_RefreshCharacterIcon_Override
    {
        static void Postfix(Duckov.UI.HealthBar __instance)
        {
            try
            {
                var h = __instance.target;
                if (!h) return;

                var cmc = h.TryGetCharacter();
                if (!cmc) return;

                var tag = cmc.GetComponent<NetAiTag>();
                if (!tag) return;

                // 
                bool hasIcon = tag.iconTypeOverride.HasValue;
                bool hasShow = tag.showNameOverride.HasValue;
                bool hasName = !string.IsNullOrEmpty(tag.nameOverride);
                if (!hasIcon && !hasShow && !hasName) return;

                // UI 
                var tr = Traverse.Create(__instance);
                var levelIcon = tr.Field<UnityEngine.UI.Image>("levelIcon").Value;
                var nameText = tr.Field<TMPro.TextMeshProUGUI>("nameText").Value;

                // 1) 
                if (levelIcon && hasIcon)
                {
                    var sp = ResolveIconSpriteCompat(tag.iconTypeOverride.Value);
                    if (sp)
                    {
                        levelIcon.sprite = sp;
                        levelIcon.gameObject.SetActive(true);
                    }
                    else
                    {
                        levelIcon.gameObject.SetActive(false);
                    }
                }

                // 2) Host boss/elete 
                bool show = hasShow ? tag.showNameOverride.Value : (cmc.characterPreset ? cmc.characterPreset.showName : false);
                if (tag.iconTypeOverride.HasValue)
                {
                    var t = (CharacterIconTypes)tag.iconTypeOverride.Value;
                    if (!show && (t == CharacterIconTypes.boss || t == CharacterIconTypes.elete))
                        show = true;
                }

                if (nameText)
                {
                    if (show)
                    {
                        if (hasName) nameText.text = tag.nameOverride;
                        nameText.gameObject.SetActive(true);
                    }
                    else
                    {
                        nameText.gameObject.SetActive(false);
                    }
                }
            }
            catch { /* defensive: avoid UI crash */ }
        }

        // 
        static Sprite ResolveIconSpriteCompat(int iconType)
        {
            switch ((CharacterIconTypes)iconType)
            {
                case CharacterIconTypes.elete: return Duckov.Utilities.GameplayDataSettings.UIStyle.EleteCharacterIcon;
                case CharacterIconTypes.pmc: return Duckov.Utilities.GameplayDataSettings.UIStyle.PmcCharacterIcon;
                case CharacterIconTypes.boss: return Duckov.Utilities.GameplayDataSettings.UIStyle.BossCharacterIcon;
                case CharacterIconTypes.merchant: return Duckov.Utilities.GameplayDataSettings.UIStyle.MerchantCharacterIcon;
                case CharacterIconTypes.pet: return Duckov.Utilities.GameplayDataSettings.UIStyle.PetCharacterIcon;
                default: return null;
            }
        }
    }

    // LootView.RegisterEvents open==false 
    [HarmonyPatch(typeof(Duckov.UI.LootView), "RegisterEvents")]
    static class Patch_LootView_RegisterEvents_Safe
    {
        // Prefix 
        static System.Exception Finalizer(Duckov.UI.LootView __instance, System.Exception __exception)
        {
            if (__exception != null)
            {
                UnityEngine.Debug.LogWarning("[LOOT][UI] RegisterEvents threw and was swallowed: " + __exception);
                return null; // UI 
            }
            return null;
        }
    }


    // 
    [HarmonyPatch(typeof(Duckov.UI.LootView), "OnPreviousPage")]
    static class Patch_LootView_OnPreviousPage_OnlyWhenOpen
    {
        static bool Prefix(Duckov.UI.LootView __instance)
        {
            bool isOpen = false;
            try
            {
                var tr = Traverse.Create(__instance);
                try { isOpen = tr.Property<bool>("open").Value; }
                catch { isOpen = tr.Field<bool>("open").Value; }
            }
            catch { }
            return isOpen; // ==false -> 
        }
    }

    [HarmonyPatch(typeof(Duckov.UI.LootView), "OnNextPage")]
    static class Patch_LootView_OnNextPage_OnlyWhenOpen
    {
        static bool Prefix(Duckov.UI.LootView __instance)
        {
            bool isOpen = false;
            try
            {
                var tr = Traverse.Create(__instance);
                try { isOpen = tr.Property<bool>("open").Value; }
                catch { isOpen = tr.Field<bool>("open").Value; }
            }
            catch { }
            return isOpen;
        }
    }

    [HarmonyPatch(typeof(Duckov.UI.LootView), "get_TargetInventory")]
    static class Patch_LootView_GetTargetInventory_Safe
    {
        static System.Exception Finalizer(Duckov.UI.LootView __instance,
                                          ref ItemStatsSystem.Inventory __result,
                                          System.Exception __exception)
        {
            if (__exception != null)
            {
                __result = null;     // / 
                return null;         // 
            }
            return null;
        }
    }

    [HarmonyPatch(typeof(Slot), nameof(Slot.Plug))]
    public static class Patch_Slot_Plug_PickupCleanup
    {
        const float PICK_RADIUS = 2.5f;                 // 
        const QueryTriggerInteraction QTI = QueryTriggerInteraction.Collide;
        const int LAYER_MASK = ~0;

        // bool Plug(Item otherItem, out Item unpluggedItem, bool dontForce = false, Slot[] acceptableSlot = null, int acceptableSlotMask = 0)
        static void Postfix(Slot __instance, Item otherItem, Item unpluggedItem, bool __result)
        {
            if (!__result || otherItem == null) return;

            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted) return;

            // --- Client + Agent ---
            if (!mod.IsServer)
            {
                // A) item 
                if (TryFindId(mod.clientDroppedItems, otherItem, out uint cid))
                {
                    LocalDestroyAgent(otherItem);
                    SendPickupReq(mod, cid);
                    return;
                }

                // B) / NetDropTag ID
                if (TryFindNearestTaggedId(otherItem, out uint nearId))
                {
                    LocalDestroyAgentById(mod.clientDroppedItems, nearId);
                    SendPickupReq(mod, nearId);
                }
                return;
            }

            // --- Host DESPAWN ---
            if (TryFindId(mod.serverDroppedItems, otherItem, out uint sid))
            {
                ServerDespawn(mod, sid);
                return;
            }
            if (TryFindNearestTaggedId(otherItem, out uint nearSid))
            {
                ServerDespawn(mod, nearSid);
            }
        }

        // ========= =========
        static void SendPickupReq(ModBehaviour mod, uint id)
        {
            var w = mod.writer; w.Reset();
            w.Put((byte)Op.ITEM_PICKUP_REQUEST);
            w.Put(id);
            mod.connectedPeer?.Send(w, DeliveryMethod.ReliableOrdered);
        }

        static void ServerDespawn(ModBehaviour mod, uint id)
        {
            if (mod.serverDroppedItems.TryGetValue(id, out var it) && it != null)
                LocalDestroyAgent(it);
            mod.serverDroppedItems.Remove(id);

            var w = mod.writer; w.Reset();
            w.Put((byte)Op.ITEM_DESPAWN);
            w.Put(id);
            mod.netManager.SendToAll(w, DeliveryMethod.ReliableOrdered);
        }

        static void LocalDestroyAgent(Item it)
        {
            try
            {
                var ag = it.ActiveAgent;
                if (ag && ag.gameObject) UnityEngine.Object.Destroy(ag.gameObject);
            }
            catch { }
        }

        static void LocalDestroyAgentById(Dictionary<uint, Item> dict, uint id)
        {
            if (dict.TryGetValue(id, out var it) && it != null) LocalDestroyAgent(it);
        }

        static bool TryFindId(Dictionary<uint, Item> dict, Item it, out uint id)
        {
            foreach (var kv in dict)
                if (ReferenceEquals(kv.Value, it)) { id = kv.Key; return true; }
            id = 0; return false;
        }

        // ActiveAgent NetDropTag
        static bool TryFindNearestTaggedId(Item item, out uint id)
        {
            id = 0;
            if (item == null) return false;

            Vector3 center;
            try
            {
                var ag = item.ActiveAgent;
                center = ag ? ag.transform.position : item.transform.position;
            }
            catch { center = item.transform.position; }

            var cols = Physics.OverlapSphere(center, PICK_RADIUS, LAYER_MASK, QTI);
            float best = float.MaxValue;
            uint bestId = 0;

            foreach (var c in cols)
            {
                var tag = c.GetComponentInParent<NetDropTag>();
                if (tag == null) continue;
                float d2 = (c.transform.position - center).sqrMagnitude;
                if (d2 < best)
                {
                    best = d2;
                    bestId = tag.id;
                }
            }

            if (bestId != 0) { id = bestId; return true; }
            return false;
        }
    }

    [HarmonyPatch(typeof(Slot), "Plug")]
    static class Patch_Slot_Plug_BlockEquipFromLoot
    {
        static bool Prefix(Slot __instance, Item otherItem, ref Item unpluggedItem)
        {
            var m = ModBehaviour.Instance;
            if (m == null || !m.networkStarted || m.IsServer) return true;
            if (m._applyingLootState) return true;

            var inv = otherItem ? otherItem.InInventory : null;
            // 
            if (LootboxDetectUtil.IsLootboxInventory(inv) && !LootboxDetectUtil.IsPrivateInventory(inv))
            {
                int srcPos = inv?.GetIndex(otherItem) ?? -1;
                m.Client_SendLootTakeRequest(inv, srcPos, null, -1, __instance);
                unpluggedItem = null;
                return false;
            }
            return true;
        }
    }





    [HarmonyPatch(typeof(Inventory), "AddAt")]
    static class Patch_Inventory_AddAt_FromLoot
    {
        static bool Prefix(Inventory __instance, Item item, int atPosition, ref bool __result)
        {
            var m = ModBehaviour.Instance;
            if (m == null || !m.networkStarted || m.IsServer) return true;
            if (m._applyingLootState) return true;

            var srcInv = item ? item.InInventory : null;
            if (srcInv == null || srcInv == __instance) return true;

            // 
            if (LootboxDetectUtil.IsLootboxInventory(srcInv) && !LootboxDetectUtil.IsPrivateInventory(srcInv))
            {
                int srcPos = srcInv.GetIndex(item);

                // Loot.AddAt 
                LootUiGuards.InLootAddAtDepth++;
                try
                {
                    m.Client_SendLootTakeRequest(srcInv, srcPos, __instance, atPosition, null);
                }
                finally
                {
                    LootUiGuards.InLootAddAtDepth--;
                }

                __result = true;
                return false;
            }
            return true;
        }
    }


    [HarmonyPatch(typeof(ItemStatsSystem.Inventory), nameof(ItemStatsSystem.Inventory.AddAt), typeof(ItemStatsSystem.Item), typeof(int))]
    [HarmonyPriority(Priority.First)]
    static class Patch_Inventory_AddAt_SlotToPrivate_Reroute
    {
        static bool Prefix(Inventory __instance, Item item, int atPosition, ref bool __result)
        {
            var m = ModBehaviour.Instance;
            if (m == null || !m.networkStarted || m.IsServer) return true;

            // Player / / 
            if (!LootboxDetectUtil.IsPrivateInventory(__instance)) return true;

            // 
            var slot = item ? item.PluggedIntoSlot : null;
            if (slot == null) return true;

            // 
            var master = slot.Master;
            while (master && master.PluggedIntoSlot != null)
                master = master.PluggedIntoSlot.Master;

            // master.InInventory LootView 
            var srcLoot = master ? master.InInventory : null;
            if (!srcLoot)
            {
                try { var lv = LootView.Instance; if (lv) srcLoot = lv.TargetInventory; } catch { }
            }

            // 
            if (!srcLoot || !LootboxDetectUtil.IsLootboxInventory(srcLoot) || LootboxDetectUtil.IsPrivateInventory(srcLoot))
            {
                // 
                Debug.LogWarning($"[Coop] AddAt(private, slot->backpack) srcLoot not found; block local AddAt for '{item?.name}'");
                __result = false;
                return false;
            }

            #if DEBUG
            Debug.Log($"[Coop] AddAt(private, slot->backpack) -> send UNPLUG(takeToBackpack), destPos={atPosition}");
            #endif
            // Host TAKE_OK atPosition
            m.Client_RequestSlotUnplugToBackpack(srcLoot, master, slot.Key, __instance, atPosition);

            __result = true;   // 
            return false;      // AddAt 
        }
    }


    // HarmonyFix.cs
    [HarmonyPatch]
    static class Patch_ItemUtilities_SendToPlayerCharacterInventory_FromLoot
    {
        static MethodBase TargetMethod()
        {
            var t = typeof(ItemUtilities);
            var m2 = AccessTools.Method(t, "SendToPlayerCharacterInventory",
                new[] { typeof(ItemStatsSystem.Item), typeof(bool) });
            if (m2 != null) return m2;

            // 5 
            return AccessTools.Method(t, "SendToPlayerCharacterInventory",
                new[] { typeof(ItemStatsSystem.Item), typeof(bool), typeof(bool),
                    typeof(ItemStatsSystem.Inventory), typeof(int) });
        }

        // (Item item, bool dontMerge, ref bool __result) 

        static bool Prefix(ItemStatsSystem.Item item, bool dontMerge, ref bool __result)
        {
            var m = ModBehaviour.Instance;
            if (m == null || !m.networkStarted || m.IsServer) return true;

            // Loot.AddAt 
            if (LootUiGuards.InLootAddAt)
            {
                __result = false;
                return false;
            }

            // A) TAKE
            var inv = item ? item.InInventory : null;
            if (inv && LootboxDetectUtil.IsLootboxInventory(inv) && !LootboxDetectUtil.IsPrivateInventory(inv))
            {
                int srcPos = inv.GetIndex(item);
                if (srcPos >= 0)
                {
                    m.Client_SendLootTakeRequest(inv, srcPos); // TAKE_OK 
                    __result = true;
                    return false;
                }
            }

            // B) UNPLUG + takeToBackpack
            var slot = item ? item.PluggedIntoSlot : null;
            if (slot != null)
            {
                var master = slot.Master;
                while (master && master.PluggedIntoSlot != null) master = master.PluggedIntoSlot.Master;

                var srcLoot = master ? master.InInventory : null;
                if (!srcLoot)
                {
                    try { var lv = Duckov.UI.LootView.Instance; if (lv) srcLoot = lv.TargetInventory; } catch { }
                }

                if (srcLoot && LootboxDetectUtil.IsLootboxInventory(srcLoot) && !LootboxDetectUtil.IsPrivateInventory(srcLoot))
                {
                    #if DEBUG
                    Debug.Log("[Coop] SendToPlayerCharInv (slot->backpack) -> send UNPLUG(takeToBackpack=true)");
                    #endif
                    // TAKE_OK Client_OnLootTakeOk 
                    m.Client_RequestLootSlotUnplug(srcLoot, master, slot.Key, true, 0);
                    __result = true;
                    return false;
                }
            }

            // 
            return true;
        }


    }






    static class LootUiGuards
    {
        [ThreadStatic] public static int InLootAddAtDepth;
        public static bool InLootAddAt => InLootAddAtDepth > 0;
        // Removed unused: BlockNextSendToInventory
    }

    // HarmonyFix.cs
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.AddAt), typeof(Item), typeof(int))]
    static class Patch_Inventory_AddAt_BlockLocalInLoot
    {
        static bool Prefix(Inventory __instance, Item item, int atPosition, ref bool __result)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.IsClient) return true;

            if (LootboxDetectUtil.IsPrivateInventory(__instance)) return true; // 

            if (!ModBehaviour.IsCurrentLootInv(__instance)) return true;
            if (mod.ApplyingLootState) return true;

            LootUiGuards.InLootAddAtDepth++;
            try
            {
                var srcInv = item ? item.InInventory : null;

                // === A) / TAKE -> PUT ===
                if (ReferenceEquals(srcInv, __instance))
                {
                    // Succeeded
                    int srcPos = __instance.GetIndex(item);
                    if (srcPos == atPosition) { __result = true; return false; }

                    if (srcPos < 0) { __result = false; return false; }

                    // 1) TAKE 
                    uint tk = mod.Client_SendLootTakeRequest(__instance, srcPos, null, -1, null);

                    // 2) TAKE_OK PUT(atPosition)
                    mod.NoteLootReorderPending(tk, __instance, atPosition);

                    __result = true;   // 
                    return false;      // AddAt 
                }

                // B) -> TAKE 
                if (ModBehaviour.IsCurrentLootInv(srcInv))
                {
                    int srcPos = srcInv.GetIndex(item);
                    if (srcPos < 0) { __result = false; return false; }

                    mod.Client_SendLootTakeRequest(srcInv, srcPos, __instance, atPosition, null);
                    __result = true;
                    return false;
                }

                // C) slot -> loot 
                if (__instance && LootboxDetectUtil.IsLootboxInventory(__instance) && !LootboxDetectUtil.IsPrivateInventory(__instance))
                {
                    var slot = item ? item.PluggedIntoSlot : null;
                    if (slot != null)
                    {
                        // 
                        Item master = slot.Master;
                        while (master && master.PluggedIntoSlot != null) master = master.PluggedIntoSlot.Master;
                        var masterLoot = master ? master.InInventory : null;

                        if (masterLoot == __instance) // 
                        {
                            #if DEBUG
                            Debug.Log("[Coop] AddAt@Loot (slot->loot) -> send UNPLUG(takeToBackpack=false)");
                            #endif
                            try { LootUiGuards.InLootAddAtDepth++; } catch { }
                            try
                            {
                                // + takeToBackpack=false
                                ModBehaviour.Instance.Client_RequestLootSlotUnplug(__instance, slot.Master, slot.Key, false, 0);
                            }
                            finally { LootUiGuards.InLootAddAtDepth--; }

                            __result = true;     // Succeeded WaitingHost LOOT_STATE UI
                            return false;        // AddAt 
                        }
                    }
                }


                // -> PUT 
                return true;
            }
            finally { LootUiGuards.InLootAddAtDepth--; }
        }
    }


    [HarmonyPatch(typeof(Inventory))]
    static class Patch_Inventory_RemoveAt_BlockLocalInLoot
    {
        // RemoveAt(int, out Item) 
        static MethodBase TargetMethod()
        {
            var tInv = typeof(Inventory);
            var tItemByRef = typeof(Item).MakeByRefType();
            return AccessTools.Method(tInv, "RemoveAt", new Type[] { typeof(int), tItemByRef });
        }

        // out Item ref 
        static bool Prefix(Inventory __instance, int position, ref Item __1, ref bool __result)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.IsClient) return true;

            if (LootboxDetectUtil.IsPrivateInventory(__instance)) return true;

            bool isLootInv = false;
            try
            {
                var lv = LootView.Instance;
                isLootInv = lv && __instance && ReferenceEquals(__instance, lv.TargetInventory);

            }
            catch { }

            if (!isLootInv) return true;
            if (LootboxDetectUtil.IsPrivateInventory(__instance)) return true; // 
            if (mod.ApplyingLootState) return true;
            __1 = null; __result = false; return false;
        }
    }

    [HarmonyPatch(typeof(LootView), "OnLootTargetItemDoubleClicked")]
    [HarmonyPriority(Priority.First)]
    static class Patch_LootView_OnLootTargetItemDoubleClicked_EquipDirectly
    {
        // Duckov.UI.InventoryEntry InventoryDisplayEntry 
        static bool Prefix(LootView __instance, InventoryDisplay display, InventoryEntry entry, PointerEventData data)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted || mod.IsServer) return true;   // Host/ 

            var item = entry?.Item;
            if (item == null) return false;

            var lootInv = __instance?.TargetInventory;
            if (lootInv == null) return true;

            // / 
            if (!ReferenceEquals(item.InInventory, lootInv)) return true;
            if (!LootboxDetectUtil.IsLootboxInventory(lootInv)) return true;

            // 
            int pos;
            try { pos = lootInv.GetIndex(item); } catch { return true; }
            if (pos < 0) return true;

            // 
            var destSlot = PickEquipSlot(item);

            // TAKE Mod.cs _cliPendingTake[token].slot slot.Plug(item)
            if (destSlot != null)
                mod.Client_SendLootTakeRequest(lootInv, pos, null, -1, destSlot);
            else
                mod.Client_SendLootTakeRequest(lootInv, pos);

            data?.Use();     // 
            return false;    // 
        }

        static Slot PickEquipSlot(Item item)
        {
            var cmc = CharacterMainControl.Main;
            var charItem = cmc ? cmc.CharacterItem : null;
            var slots = charItem ? charItem.Slots : null;
            if (slots == null) return null;

            // 
            try { var s = cmc.PrimWeaponSlot(); if (s != null && s.Content == null && s.CanPlug(item)) return s; } catch { }
            try { var s = cmc.SecWeaponSlot(); if (s != null && s.Content == null && s.CanPlug(item)) return s; } catch { }
            try { var s = cmc.MeleeWeaponSlot(); if (s != null && s.Content == null && s.CanPlug(item)) return s; } catch { }

            // 
            foreach (var s in slots)
            {
                if (s == null || s.Content != null) continue;
                try { if (s.CanPlug(item)) return s; } catch { }
            }
            return null;
        }
    }

    // Host Inventory.RemoveAt Succeeded Host Client 
    [HarmonyPatch(typeof(ItemStatsSystem.Inventory))]
    static class Patch_Inventory_RemoveAt_BroadcastOnServer
    {
        // RemoveAt(int, out Item) 
        static MethodBase TargetMethod()
        {
            var tInv = typeof(ItemStatsSystem.Inventory);
            var tItemByRef = typeof(ItemStatsSystem.Item).MakeByRefType();
            return AccessTools.Method(tInv, "RemoveAt", new Type[] { typeof(int), tItemByRef });
        }

        // Postfix Host Succeeded Status
        static void Postfix(ItemStatsSystem.Inventory __instance, int position, ItemStatsSystem.Item __1, bool __result)
        {
            var m = ModBehaviour.Instance;
            if (m == null || !m.networkStarted || !m.IsServer) return;               // Host
            if (!__result || m._serverApplyingLoot) return;                           // Failed/Network 
            if (!LootboxDetectUtil.IsLootboxInventory(__instance)) return;            // 
            if (LootboxDetectUtil.IsPrivateInventory(__instance)) return;             // Player / 

            m.Server_SendLootboxState(null, __instance);                              // Client
        }
    }

    [HarmonyPatch(typeof(ItemAgent_Gun), "ShootOneBullet")]
    static class Patch_BlockClientAiShoot
    {
        [HarmonyPriority(Priority.First)]
        static bool Prefix(ItemAgent_Gun __instance, Vector3 _muzzlePoint, Vector3 _shootDirection, Vector3 firstFrameCheckStartPoint)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted) return true;

            // Host Client 
            if (mod.IsServer) return true;

            var holder = __instance ? __instance.Holder : null;

            // && AI AICharacterController NetAiTag => 
            if (holder && holder != CharacterMainControl.Main)
            {
                bool isAI = holder.GetComponent<AICharacterController>() != null
                         || holder.GetComponent<NetAiTag>() != null;

                if (isAI)
                {
                    if (ModBehaviour.LogAiHpDebug)
                    {
                        #if DEBUG
                        Debug.Log($"[CLIENT] Block local AI ShootOneBullet holder='{holder.name}'");
                        #endif
                    }
                    return false; // Client 
                }
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(DamageReceiver), "Hurt")]
    static class Patch_BlockClientAiVsAi_AtReceiver
    {
        [HarmonyPriority(HarmonyLib.Priority.First)]
        static bool Prefix(DamageReceiver __instance, ref global::DamageInfo __0)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted || mod.IsServer) return true;

            var target = __instance ? __instance.GetComponentInParent<CharacterMainControl>() : null;
            bool victimIsAI = target && (target.GetComponent<AICharacterController>() != null || target.GetComponent<NetAiTag>() != null);
            if (!victimIsAI) return true;

            var attacker = __0.fromCharacter;
            bool attackerIsAI = attacker && (attacker.GetComponent<NetAiTag>() != null || attacker.GetComponent<NetAiTag>() != null);
            if (attackerIsAI) return false; // Health

            return true;
        }
    }

    [HarmonyPatch(typeof(Health), "get_MaxHealth")]
    static class Patch_Health_get_MaxHealth_ClientOverride
    {
        static void Postfix(Health __instance, ref float __result)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || mod.IsServer) return;

            // AI Player UI 
            var cmc = __instance.TryGetCharacter();
            bool isAI = cmc && (cmc.GetComponent<AICharacterController>() != null || cmc.GetComponent<NetAiTag>() != null);
            if (!isAI) return;

            if (mod.TryGetClientMaxOverride(__instance, out var v) && v > 0f)
            {
                if (__result <= 0f || v > __result) __result = v;
            }
        }
    }


    [HarmonyPatch(typeof(CharacterMainControl), "OnDead")]
    static class Patch_Client_OnDead_ReportCorpseTree
    {
        static void Postfix(CharacterMainControl __instance)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted) return;

            // Client + Player 
            if (mod.IsServer) return;
            if (__instance != CharacterMainControl.Main) return;

            // = Host / / 
            if (mod._cliCorpseTreeReported) return;

            try
            {
                // Client CreateFromItem 
               DeadLootSpawnContext.InOnDead = __instance;

                // Host 
                mod.Net_ReportPlayerDeadTree(__instance);

                // 
                mod._cliCorpseTreeReported = true;
            }
            finally
            {
               DeadLootSpawnContext.InOnDead = null;
            }
        }
    }


    [HarmonyPatch(typeof(Item), nameof(Item.Split), new[] { typeof(int) })]
    static class Patch_Item_Split_RecordForLoot
    {
        static void Postfix(Item __instance, int count, ref UniTask<Item> __result)
        {
            var m = ModBehaviour.Instance;
            if (m == null || !m.networkStarted || m.IsServer) return;

            var srcInv = __instance ? __instance.InInventory : null;
            if (srcInv == null) return;
            if (!LootboxDetectUtil.IsLootboxInventory(srcInv) || LootboxDetectUtil.IsPrivateInventory(srcInv)) return;

            int srcPos = srcInv.GetIndex(__instance);
            if (srcPos < 0) return;

            __result = __result.ContinueWith(newItem =>
            {
                if (newItem != null)
                {
                    ModBehaviour.map[newItem.GetInstanceID()] = new ModBehaviour.Pending
                    {
                        inv = srcInv,
                        srcPos = srcPos,
                        count = count
                    };
                }
                return newItem;
            });
        }
    }

    [HarmonyPatch(typeof(Item), nameof(Item.Split), new[] { typeof(int) })]
    static class Patch_Item_Split_InterceptLoot_Prefix
    {
        static bool Prefix(Item __instance, int count, ref UniTask<Item> __result)
        {
            var m = ModBehaviour.Instance;
            if (m == null || !m.networkStarted || m.IsServer) return true;

            var inv = __instance ? __instance.InInventory : null;
            if (inv == null) return true;
            if (!LootboxDetectUtil.IsLootboxInventory(inv) || LootboxDetectUtil.IsPrivateInventory(inv)) return true;

            // Client Host 
            int srcPos = inv.GetIndex(__instance);
            if (srcPos < 0) return true;

            // srcPos Host -1
            int prefer = inv.GetFirstEmptyPosition(srcPos + 1);
            if (prefer < 0) prefer = inv.GetFirstEmptyPosition(0);
            if (prefer < 0) prefer = -1;

            // 
            m.Client_SendLootSplitRequest(inv, srcPos, count, prefer);

            // Add/Merge 
            __result = UniTask.FromResult<Item>(null);
            return false;
        }
    }

    // 2) AddAndMerge SPLIT
    [HarmonyPatch(typeof(ItemUtilities), "AddAndMerge")]
    [HarmonyPriority(Priority.First)] // AddAndMerge 
    static class Patch_AddAndMerge_SplitFirst
    {
        static bool Prefix(Inventory inventory, Item item, int preferedFirstPosition, ref bool __result)
        {
            var m = ModBehaviour.Instance;
            if (m == null || !m.networkStarted || m.IsServer) return true; // Host / 

            if (inventory == null || item == null) return true;
            if (!LootboxDetectUtil.IsLootboxInventory(inventory) || LootboxDetectUtil.IsPrivateInventory(inventory))
                return true;

            // 
            if (!ModBehaviour.map.TryGetValue(item.GetInstanceID(), out var p)) return true;
            if (!ReferenceEquals(p.inv, inventory)) return true; // 

            // Host srcPos count preferedFirstPosition 
            m.Client_SendLootSplitRequest(inventory, p.srcPos, p.count, preferedFirstPosition);

            // newItem Host 
            try { if (item) { item.Detach(); UnityEngine.Object.Destroy(item.gameObject); } } catch { }
            ModBehaviour.map.Remove(item.GetInstanceID());

            __result = true;   // 
            return false;      // PUT 
        }
    }

    // 3) Inventory.AddAt(...) AddAndMerge 
    [HarmonyPatch(typeof(Inventory), "AddAt")]
    [HarmonyPriority(Priority.First)]
    static class Patch_AddAt_SplitFirst
    {
        static bool Prefix(Inventory __instance, Item item, int atPosition, ref bool __result)
        {
            var m = ModBehaviour.Instance;
            if (m == null || !m.networkStarted || m.IsServer) return true;

            if (__instance == null || item == null) return true;
            if (!LootboxDetectUtil.IsLootboxInventory(__instance) || LootboxDetectUtil.IsPrivateInventory(__instance))
                return true;

            if (!ModBehaviour.map.TryGetValue(item.GetInstanceID(), out var p)) return true;
            if (!ReferenceEquals(p.inv, __instance)) return true;

            m.Client_SendLootSplitRequest(__instance, p.srcPos, p.count, atPosition);

            try { if (item) { item.Detach(); UnityEngine.Object.Destroy(item.gameObject); } } catch { }
            ModBehaviour.map.Remove(item.GetInstanceID());

            __result = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(SplitDialogue), "DoSplit")]
    static class Patch_SplitDialogue_DoSplit_NetOnly
    {
        static bool Prefix(SplitDialogue __instance, int value, ref UniTask __result)
        {
            var m = ModBehaviour.Instance;
            // / Host / Mod 
            if (m == null || !m.networkStarted || m.IsServer)
                return true;

            // SplitDialogue 
            var tr = Traverse.Create(__instance);
            var target = tr.Field<Item>("target").Value;
            var destInv = tr.Field<Inventory>("destination").Value;
            var destIndex = tr.Field<int>("destinationIndex").Value;

            var inv = target ? target.InInventory : null;
            // 
            if (inv == null || !LootboxDetectUtil.IsLootboxInventory(inv) || LootboxDetectUtil.IsPrivateInventory(inv))
                return true;

            // Client 
            int srcPos = inv.GetIndex(target);
            if (srcPos < 0)
            {
                __result = UniTask.CompletedTask;
                return false;
            }

            // 
            int prefer = -1;
            if (destInv == inv && destIndex >= 0 && destIndex < inv.Capacity && inv.GetItemAt(destIndex) == null)
            {
                prefer = destIndex;
            }
            else
            {
                // Host -1 
                prefer = inv.GetFirstEmptyPosition(srcPos + 1);
                if (prefer < 0) prefer = inv.GetFirstEmptyPosition(0);
                if (prefer < 0) prefer = -1;
            }

            // Host Network 
            m.Client_SendLootSplitRequest(inv, srcPos, value, prefer);


            // Busy Complete UI 
            try { tr.Method("Hide").GetValue(); } catch { }

            __result = UniTask.CompletedTask;
            return false; // <DoSplit>g__Send|24_0
        }
    }

    [HarmonyPatch(typeof(ItemStatsSystem.Inventory), nameof(ItemStatsSystem.Inventory.Sort), new Type[] { })]
    static class Patch_Inventory_Sort_BlockLocalInLoot
    {
        static bool Prefix(ItemStatsSystem.Inventory __instance)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.IsClient) return true;              // Host/ 
            if (mod.ApplyingLootState) return true;                      // Server UI 
            if (!ModBehaviour.IsCurrentLootInv(__instance)) return true; // 
            if (LootboxDetectUtil.IsPrivateInventory(__instance)) return true; // 

            // / Network Host Host 
            // 
            return false; // Sort()
        }
    }

    [HarmonyPatch(typeof(CharacterMainControl), "OnDead")]
    static class Patch_Server_OnDead_Host_UsePlayerTree
    {
        static void Postfix(CharacterMainControl __instance)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted || !mod.IsServer) return;

            var lm = LevelManager.Instance;
            if (lm == null || __instance != lm.MainCharacter) return;  // Host 

            mod.Server_HandleHostDeathViaTree(__instance);             // Client 
        }
    }


    [HarmonyPatch(typeof(CharacterMainControl), "OnDead")]
    static class Patch_Client_OnDead_MarkAll_ForBlock
    {
        static void Prefix(CharacterMainControl __instance)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted) return;
            if (mod.IsServer) return;                  // Client 
            DeadLootSpawnContext.InOnDead = __instance;
        }

        static void Finalizer()
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted) return;
            if (mod.IsServer) return;
            DeadLootSpawnContext.InOnDead = null;
        }
    }


    /// //// AI Client NRE ////////////////////////// //// AI Client NRE ////////////////////////// //// AI Client NRE ///////////////////////


    [HarmonyPatch(typeof(CharacterMainControl), "GetHelmatItem")]
    static class Patch_CMC_GetHelmatItem_NullSafe
    {
        // Health.Hurt
        static System.Exception Finalizer(System.Exception __exception, CharacterMainControl __instance, ref Item __result)
        {
            if (__exception != null)
            {
                Debug.LogWarning($"[NET] Suppressed exception in GetHelmatItem() on {__instance?.name}: {__exception}");
                __result = null;     // Buff 
                return null;         // 
            }
            return null;
        }
    }

    [HarmonyPatch(typeof(CharacterMainControl), "GetArmorItem")]
    static class Patch_CMC_GetArmorItem_NullSafe
    {
        // 
        static System.Exception Finalizer(System.Exception __exception, CharacterMainControl __instance, ref Item __result)
        {
            if (__exception != null)
            {
                Debug.LogWarning($"[NET] Suppressed exception in GetArmorItem() on {__instance?.name}: {__exception}");
                __result = null;   // & 
                return null;       // 
            }
            return null;
        }
    }

    /// //// AI Client NRE ////////////////////////// //// AI Client NRE ////////////////////////// //// AI Client NRE ///////////////////////

    // ========= Client Door.Open -> Host =========
    [HarmonyPatch(typeof(Door), nameof(Door.Open))]
    static class Patch_Door_Open_ClientToServer
    {
        static bool Prefix(Door __instance)
        {
            var m = ModBehaviour.Instance;
            if (m == null || !m.networkStarted) return true;
            if (m.IsServer) return true;                 // Host 
            if (ModBehaviour._applyingDoor) return true;  // Network 

            m.Client_RequestDoorSetState(__instance, closed: false);
            return false; // Client 
        }
    }

    // ========= Client Door.Close -> Host =========
    [HarmonyPatch(typeof(Door), nameof(Door.Close))]
    static class Patch_Door_Close_ClientToServer
    {
        static bool Prefix(Door __instance)
        {
            var m = ModBehaviour.Instance;
            if (m == null || !m.networkStarted) return true;
            if (m.IsServer) return true;
            if (ModBehaviour._applyingDoor) return true;

            m.Client_RequestDoorSetState(__instance, closed: true);
            return false;
        }
    }

    // ========= Client Door.Switch -> Host =========
    [HarmonyPatch(typeof(Door), nameof(Door.Switch))]
    static class Patch_Door_Switch_ClientToServer
    {
        static bool Prefix(Door __instance)
        {
            var m = ModBehaviour.Instance;
            if (m == null || !m.networkStarted) return true;
            if (m.IsServer) return true;
            if (ModBehaviour._applyingDoor) return true;

            bool isOpen = false;
            try { isOpen = __instance.IsOpen; } catch { }
            m.Client_RequestDoorSetState(__instance, closed: isOpen /* open->close, close->open */);
            return false;
        }
    }

    // ========= Host SetClosed Client =========
    [HarmonyPatch(typeof(Door), "SetClosed")]
    static class Patch_Door_SetClosed_BroadcastOnServer
    {
        static void Postfix(Door __instance, bool _closed)
        {
            var m = ModBehaviour.Instance;
            if (m == null || !m.networkStarted || !m.IsServer) return;

            int key = 0;
            try { key = (int)AccessTools.Field(typeof(Door), "doorClosedDataKeyCached").GetValue(__instance); } catch { }
            if (key == 0) key = m.ComputeDoorKey(__instance.transform);
            if (key == 0) return;

            m.Server_BroadcastDoorState(key, _closed);
        }
    }

    [HarmonyPatch(typeof(Breakable), "Awake")]
    static class Patch_Breakable_Awake_ForceVisibleInCoop
    {
        static void Postfix(Breakable __instance)
        {
            var mod = DuckovCoopMod.ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted) return;

            // NetDestructibleTag 
            try
            {
                var hs = __instance.simpleHealth;
                if (hs)
                {
                    var tag = hs.GetComponent<NetDestructibleTag>() ?? hs.gameObject.AddComponent<NetDestructibleTag>();
                    tag.id = NetDestructibleTag.ComputeStableId(hs.gameObject);
                    mod.RegisterDestructible(tag.id, hs);
                }
            }
            catch { }

            // Client Awake Save / 
            if (!mod.IsServer)
            {
                try
                {
                    if (__instance.normalVisual) __instance.normalVisual.SetActive(true);
                    if (__instance.dangerVisual) __instance.dangerVisual.SetActive(false);
                    if (__instance.breakedVisual) __instance.breakedVisual.SetActive(false);
                    if (__instance.mainCollider) __instance.mainCollider.SetActive(true);

                    var hs = __instance.simpleHealth;
                    if (hs && hs.dmgReceiver) hs.dmgReceiver.gameObject.SetActive(true);
                }
                catch { }
            }
        }
    }

    [HarmonyPatch(typeof(CharacterMainControl), nameof(CharacterMainControl.SetCharacterModel))]
    static class Patch_CMC_SetCharacterModel_RebindNetAiFollower
    {
        static void Postfix(CharacterMainControl __instance)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted) return;

            try
            {
                if (mod != null && mod.networkStarted && !mod.IsServer)
                {
                    int id = -1;
                    // aiById CMC aiId
                    foreach (var kv in mod.aiById)
                    {
                        if (kv.Value == __instance) { id = kv.Key; break; }
                    }
                    if (id >= 0)
                    {
                        var tag = __instance.GetComponent<NetAiTag>() ?? __instance.gameObject.AddComponent<NetAiTag>();
                        if (tag.aiId != id) tag.aiId = id;
                    }
                }
            }
            catch { }

            // AI Player/Host 
            // IsRealAI(.) 
            try
            {
                if (!mod.IsServer && mod.IsRealAI(__instance))
                {
                    // RemoteReplicaTag MagicBlend.Update 
                    if (!__instance.GetComponent<RemoteReplicaTag>())
                        __instance.gameObject.AddComponent<RemoteReplicaTag>();
                }
            }
            catch { }

            // NetAiFollower Animator
            try
            {
                var follower = __instance.GetComponent<DuckovCoopMod.NetAiFollower>();
                if (follower) follower.ForceRebindAfterModelSwap();
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(CharacterAnimationControl_MagicBlend), "Update")]
    static class Patch_MagicBlend_Update_SkipOnRemoteAI
    {
        [HarmonyPriority(Priority.First)]
        static bool Prefix(CharacterAnimationControl_MagicBlend __instance)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted) return true;
            if (mod.IsServer) return true;

            CharacterMainControl cmc = null;
            try
            {
                var cm = __instance.characterModel;
                cmc = cm ? cm.characterMainControl : __instance.GetComponentInParent<CharacterMainControl>();
            }
            catch { }

            if (!cmc) return true;

            // Client AI 
            bool isAI =
                cmc.GetComponent<AICharacterController>() != null ||
                cmc.GetComponent<NetAiTag>() != null;

            bool isRemoteReplica =
                cmc.GetComponent<DuckovCoopMod.NetAiFollower>() != null ||
                cmc.GetComponent<RemoteReplicaTag>() != null;

            if (isAI && isRemoteReplica)
                return false; // Update Network 

            return true;
        }
    }

    [HarmonyPatch(typeof(CharacterMainControl), nameof(CharacterMainControl.SetCharacterModel))]
    static class Patch_CMC_SetCharacterModel_TagAndRebindOnClient
    {
        static void Postfix(CharacterMainControl __instance)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted || mod.IsServer) return; // Client 

            // Client AI Animator
            bool isAI =
                __instance.GetComponent<AICharacterController>() != null ||
                __instance.GetComponent<NetAiTag>() != null;

            if (isAI)
            {
                if (!__instance.GetComponent<RemoteReplicaTag>())
                    __instance.gameObject.AddComponent<RemoteReplicaTag>();

                var follower = __instance.GetComponent<DuckovCoopMod.NetAiFollower>();
                if (follower) follower.ForceRebindAfterModelSwap();
            }
        }
    }

    [HarmonyPatch(typeof(SetActiveByPlayerDistance), "FixedUpdate")]
    static class Patch_SABD_KeepRemoteAIActive_Client
    {
        static void Postfix(SetActiveByPlayerDistance __instance)
        {
            var m = ModBehaviour.Instance;
            if (m == null || !m.networkStarted || m.IsServer) return;

            bool forceAll = m.Client_ForceShowAllRemoteAI;
            if (forceAll)
            {
                Traverse.Create(__instance).Field<float>("distance").Value = 9999f;
            }
        }
    }


    [HarmonyPatch(typeof(CharacterAnimationControl), "Update")]
    static class Patch_CharAnimCtrl_Update_SkipOnRemoteAI
    {
        [HarmonyPriority(Priority.First)]
        static bool Prefix(CharacterAnimationControl __instance)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted) return true;
            if (mod.IsServer) return true; // Host AI 

            CharacterMainControl cmc = null;
            try
            {
                var cm = __instance.characterModel;
                cmc = cm ? cm.characterMainControl : __instance.GetComponentInParent<CharacterMainControl>();
            }
            catch { }

            if (!cmc) return true;

            bool isAI =
                cmc.GetComponent<AICharacterController>() != null ||
                cmc.GetComponent<NetAiTag>() != null;

            bool isRemoteReplica =
                cmc.GetComponent<DuckovCoopMod.NetAiFollower>() != null ||
                cmc.GetComponent<RemoteReplicaTag>() != null;

            // Client AI Update
            if (isAI && isRemoteReplica)
                return false;

            return true;
        }
    }


    //[HarmonyPatch(typeof(SteamManager), "OnRichPresenceChanged")]
    //static class Patch_OnRichPresenceChanged
    //{
    // static bool Prefix(SteamManager __instance, RichPresenceManager manager)
    // {
    // if (!global::SteamManager.Initialized || manager == null)
    // return false;


    // string token = CallGetSteamDisplay(manager); // : "#Status_Playing" / "#Status_MainMenu"
    // Debug.Log(token);
    // global::Steamworks.SteamFriends.SetRichPresence("steam_display", "#Status_UnityEditor");


    // //var mapName = manager.levelDisplayNameRaw ?? "";
    // //int playerCount = 2;
    // //Debug.Log(mapName);
    // //string levelText = $"{mapName} Mode :{playerCount} ";
    // //global::Steamworks.SteamFriends.SetRichPresence("level", levelText);


    // return false; 
    // }

    // public static string CallGetSteamDisplay(object target)
    // {
    // if (target == null) throw new ArgumentNullException(nameof(target));

    // Type t = target.GetType();

    // MethodInfo m = t.GetMethod(
    // "GetSteamDisplay",
    // BindingFlags.Instance | BindingFlags.NonPublic
    // );

    // if (m == null)
    // throw new MissingMethodException(t.FullName, "GetSteamDisplay");

    // return (string)m.Invoke(target, null);
    // }
    //}

    // ========== Client Health.Hurt AI -> Player / Host ==========
    [HarmonyPatch(typeof(Health), "Hurt")]
    static class Patch_Health
    {
        static bool Prefix(Health __instance, ref global::DamageInfo __0)
        {
            var mod = DuckovCoopMod.ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted) return true;

            if (__instance.gameObject.GetComponent<AutoRequestHealthBar>() != null)
            {
                return false;
            }

            // AI/NPC
            global::CharacterMainControl victimCmc = null;
            try { victimCmc = __instance ? __instance.TryGetCharacter() : null; } catch { }
            bool isAiVictim = (victimCmc && victimCmc != global::CharacterMainControl.Main);

            // Player
            var from = __0.fromCharacter;
            bool fromLocalMain = (from == global::CharacterMainControl.Main);

            // Client + Player AI Network 
            if (!mod.IsServer && isAiVictim && fromLocalMain)
            {
                // / 
                bool predictedDead = false;
                try
                {
                    float cur = __instance.CurrentHealth;
                    predictedDead = (cur > 0f && __0.damageValue >= cur - 0.001f);
                }
                catch { }
               // LocalHitKillFx.RememberLastBaseDamage(__0.damageValue);
               // DuckovCoopMod.LocalHitKillFx.ClientPlayForAI(victimCmc, __0, predictedDead);

                return false;
            }

            // AI AI AI Player AI 
            return true;
        }
    }

    [HarmonyPatch(typeof(HealthSimpleBase), "OnHurt")]
    static class Patch_HealthSimpleBase_OnHurt_RedirectNet
    {
        static bool Prefix(HealthSimpleBase __instance, ref global::DamageInfo __0)
        {
            var mod = DuckovCoopMod.ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted) return true;

            // Player AI UI
            var from = __0.fromCharacter;
            bool fromLocalMain = (from == global::CharacterMainControl.Main);

            if (!mod.IsServer && fromLocalMain)
            {
                // HealthValue 
                bool predictedDead = false;
                try
                {
                    float cur = __instance.HealthValue;
                    predictedDead = (cur > 0f && __0.damageValue >= cur - 0.001f);
                }
                catch { }

                DuckovCoopMod.LocalHitKillFx.ClientPlayForDestructible(__instance, __0, predictedDead);

                // Host 
                return false;
            }

            return true;
        }
    }

   


    // AddAndMerge 
    [HarmonyPatch(typeof(ItemUtilities), nameof(ItemUtilities.AddAndMerge))]
    static class Patch_ItemUtilities_AddAndMerge_InterceptSlotToBackpack
    {
        // static bool AddAndMerge(Inventory inventory, Item item, int preferedFirstPosition)
        static bool Prefix(ItemStatsSystem.Inventory inventory, ItemStatsSystem.Item item, int preferedFirstPosition, ref bool __result)
        {
            var m = ModBehaviour.Instance;
            if (m == null || !m.networkStarted || m.IsServer) return true;
            if (m._applyingLootState) return true;

            // / / 
            if (!inventory || !LootboxDetectUtil.IsPrivateInventory(inventory))
                return true;

            var slot = item ? item.PluggedIntoSlot : null;
            if (slot == null) return true;

            // + LootView 
            var master = slot.Master;
            while (master && master.PluggedIntoSlot != null) master = master.PluggedIntoSlot.Master;

            var srcLoot = master ? master.InInventory : null;
            if (!srcLoot)
            {
                try { var lv = Duckov.UI.LootView.Instance; if (lv) srcLoot = lv.TargetInventory; } catch { }
            }

            if (srcLoot && LootboxDetectUtil.IsLootboxInventory(srcLoot) && !LootboxDetectUtil.IsPrivateInventory(srcLoot))
            {
                #if DEBUG
                Debug.Log($"[Coop] AddAndMerge(slot->backpack) -> UNPLUG(takeToBackpack), prefer={preferedFirstPosition}");
                #endif
                // preferedFirstPosition 
                m.Client_RequestSlotUnplugToBackpack(srcLoot, master, slot.Key, inventory, preferedFirstPosition);
                __result = true;
                return false; // AddAndMerge
            }

            // 
            __result = false;
            return false;
        }

    }


    


    [HarmonyPatch(typeof(Slot), nameof(Slot.Plug))]
    static class Patch_Slot_Plug_ClientRedirect
    {
        static bool Prefix(Slot __instance, Item otherItem, ref bool __result)
        {
            var m = ModBehaviour.Instance;
            if (m == null || !m.networkStarted || m.IsServer || m.ClientLootSetupActive || m._applyingLootState)
                return true; // Host/ / 

            var master = __instance?.Master;
            var inv = master ? master.InInventory : null;
            if (!inv) return true;
            if (!LootboxDetectUtil.IsLootboxInventory(inv) || LootboxDetectUtil.IsPrivateInventory(inv))
                return true; // 

            if (!otherItem) return true;

            // Network Client -> Host
            m.Client_RequestLootSlotPlug(inv, master, __instance.Key, otherItem);

            __result = true;    // UI Host 
            return false;       // Plug
        }
    }



    // HarmonyFix.cs
    [HarmonyPatch(typeof(Slot), nameof(Slot.Unplug))]
    [HarmonyPriority(Priority.First)]
    static class Patch_Slot_Unplug_ClientRedirect
    {
        static bool Prefix(Slot __instance, ref Item __result)
        {
            var m = ModBehaviour.Instance;
            if (m == null || !m.networkStarted || m.IsServer) return true;
            if (m.ApplyingLootState) return true;

            // Master.InInventory 
            var inv = __instance?.Master ? __instance.Master.InInventory : null;
            if (inv == null) return true;
            // 
            if (!LootboxDetectUtil.IsLootboxInventory(inv) || LootboxDetectUtil.IsPrivateInventory(inv))
                return true;

            // Unplug Waiting AddAt/ AddAndMerge/ SendToInventory Network
            #if DEBUG
            UnityEngine.Debug.Log("[Coop] Slot.Unplug@Loot -> ignore (network-handled)");
            #endif
            __result = null;      // 
            return false;         // Unplug
        }
    }



    [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveAt))]
    static class Patch_ServerBroadcast_OnRemoveAt
    {
        static void Postfix(Inventory __instance, int position, Item removedItem, bool __result)
        {
            var m = ModBehaviour.Instance;
            if (m == null || !m.networkStarted || !m.IsServer) return;
            if (!__result || m._serverApplyingLoot) return;
            if (!LootboxDetectUtil.IsLootboxInventory(__instance) || LootboxDetectUtil.IsPrivateInventory(__instance)) return;

            if (m.Server_IsLootMuted(__instance)) return; // 
            m.Server_SendLootboxState(null, __instance);
        }
    }


    // Inventory.AddAt Host 
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.AddAt))]
    static class Patch_ServerBroadcast_OnAddAt
    {
        // AddAt 
        static void Prefix(Inventory __instance)
        {
            var m = ModBehaviour.Instance;
            if (m == null || !m.networkStarted || !m.IsServer) return;

            // AI OnDead 
            if (DeadLootSpawnContext.InOnDead == null) return;

            if (!LootboxDetectUtil.IsLootboxInventory(__instance) || LootboxDetectUtil.IsPrivateInventory(__instance)) return;
            m.Server_MuteLoot(__instance, 1.0f); // 1 
        }

        static void Postfix(Inventory __instance, Item item, int atPosition, bool __result)
        {
            var m = ModBehaviour.Instance;
            if (m == null || !m.networkStarted || !m.IsServer) return;
            if (!__result || m._serverApplyingLoot) return;
            if (!LootboxDetectUtil.IsLootboxInventory(__instance) || LootboxDetectUtil.IsPrivateInventory(__instance)) return;

            // 
            if (m.Server_IsLootMuted(__instance)) return;

            m.Server_SendLootboxState(null, __instance);
        }
    }

    // Slot.Plug Host master Inventory 
    [HarmonyPatch(typeof(Slot), nameof(Slot.Plug))]
    static class Patch_ServerBroadcast_OnSlotPlug
    {
        static void Postfix(Slot __instance, Item otherItem, Item unpluggedItem, bool __result)
        {
            var m = ModBehaviour.Instance;
            if (m == null || !m.networkStarted || !m.IsServer) return;
            if (!__result || m._serverApplyingLoot) return;

            var master = __instance?.Master;
            var inv = master ? master.InInventory : null;
            if (!inv) return;
            if (!LootboxDetectUtil.IsLootboxInventory(inv) || LootboxDetectUtil.IsPrivateInventory(inv)) return;

            if (m.Server_IsLootMuted(inv)) return; // 
            m.Server_SendLootboxState(null, inv);
        }
    }

    // Slot.Unplug Host 
    [HarmonyPatch(typeof(Slot), nameof(Slot.Unplug))]
    static class Patch_ServerBroadcast_OnSlotUnplug
    {
        static void Postfix(Slot __instance, Item __result)
        {
            var m = ModBehaviour.Instance;
            if (m == null || !m.networkStarted || !m.IsServer) return;
            if (m._serverApplyingLoot) return;

            var master = __instance?.Master;
            var inv = master ? master.InInventory : null;
            if (!inv) return;
            if (!LootboxDetectUtil.IsLootboxInventory(inv) || LootboxDetectUtil.IsPrivateInventory(inv)) return;

            m.Server_SendLootboxState(null, inv);
        }
    }

    //[HarmonyPatch(typeof(ItemStatsSystem.Items.Slot), nameof(ItemStatsSystem.Items.Slot.Plug))]
    //[HarmonyPriority(Priority.First)]
    //static class Patch_Slot_Plug_BlockEquipFromLoot_Client
    //{
    // // bool Plug(Item otherItem, out Item unpluggedItem)
    // static bool Prefix(ItemStatsSystem.Items.Slot __instance,
    // ItemStatsSystem.Item otherItem,
    // ref ItemStatsSystem.Item unpluggedItem,
    // ref bool __result)
    // {
    // var m = ModBehaviour.Instance;
    // if (m == null || !m.networkStarted || m.IsServer) return true; // Host/ 
    // if (m.ApplyingLootState) return true; // 

    // var master = __instance?.Master;
    // var inv = master ? master.InInventory : null; // InInventory
    // if (inv == null) return true;
    // if (!LootboxDetectUtil.IsLootboxInventory(inv) || LootboxDetectUtil.IsPrivateInventory(inv)) return true;

    // // Host Client Plug
    // unpluggedItem = null;
    // __result = true; // Host 
    // m.Client_RequestLootSlotPlug(inv, master, __instance.Key, otherItem);
    // return false; // 
    // }
    //}

    // HealthSimpleBase NetDestructibleTag 
    [HarmonyPatch(typeof(HealthSimpleBase), "Awake")]
    static class Patch_HSB_Awake_AddTagAndRegister
    {
        static void Postfix(HealthSimpleBase __instance)
        {
            try
            {
                var mod = ModBehaviour.Instance;
                if (mod == null) return;

                // 
                var tag = __instance.GetComponent<NetDestructibleTag>();
                if (!tag) tag = __instance.gameObject.AddComponent<NetDestructibleTag>();

                // ID Failed 
                uint id = 0;
                try
                {
                    // ID Mod.cs NetDestructibleTag 
                    id = NetDestructibleTag.ComputeStableId(__instance.gameObject);
                }
                catch { /* ignore differences */ }

                tag.id = id;
                mod.RegisterDestructible(id, __instance);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Coop][HSB.Awake] Tag/Register failed: {ex}");
            }
        }
    }

    [HarmonyPatch(typeof(DamageReceiver), "Hurt")]
    static class Patch_ClientMelee_HurtRedirect_Destructible
    {
        [HarmonyPriority(HarmonyLib.Priority.First)]
        static bool Prefix(DamageReceiver __instance, ref global::DamageInfo __0)
        {
            var m = ModBehaviour.Instance;
            if (m == null || !m.networkStarted || m.IsServer) return true;

            // Player 
            if (!MeleeLocalGuard.LocalMeleeTryingToHurt) return true;

            // 
            var hs = __instance ? __instance.GetComponentInParent<HealthSimpleBase>() : null;
            if (!hs) return true;

            // / id
            uint id = 0;
            var tag = hs.GetComponent<NetDestructibleTag>();
            if (tag) id = tag.id;
            if (id == 0)
            {
                try { id = NetDestructibleTag.ComputeStableId(hs.gameObject); } catch { }
            }
            if (id == 0) return true; // id 

            // id HealthSimpleBase
            m.Client_RequestDestructibleHurt(id, __0);
            return false; // Host 
        }
    }

    [HarmonyPatch]
    static class Patch_ClosureView_ShowAndReturnTask_SpectatorGate
    {
        static System.Reflection.MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Duckov.UI.ClosureView");
            if (t == null) return null;
            return AccessTools.Method(t, "ShowAndReturnTask", new Type[] { typeof(global::DamageInfo), typeof(float) });
        }

        static bool Prefix(ref UniTask __result, global::DamageInfo dmgInfo, float duration)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted) return true;

            if (mod._skipSpectatorForNextClosure)
            {
                mod._skipSpectatorForNextClosure = false;
                __result = UniTask.CompletedTask;
                return true; 
            }

            // UI
            if (mod.TryEnterSpectatorOnDeath(dmgInfo))
            {
               // __result = UniTask.CompletedTask;
               // ClosureView.Instance.gameObject.SetActive(false);
                return true; // 
            }

            return true;
        }

      
    }

    [HarmonyPatch(typeof(GameManager), "get_Paused")]
    internal static class Patch_Paused_AlwaysFalse
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(ref bool __result)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted) return true;

            __result = false;

            return false; 
        }
    }

    [HarmonyPatch(typeof(PauseMenu), "Show")]
    internal static class Patch_PauseMenuShow_AlwaysFalse
    {
        [HarmonyPriority(Priority.First)]
        [HarmonyPostfix]
        private static void Postfix()
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted) return;

            mod.Pausebool = true;

        }
    }

    [HarmonyPatch(typeof(PauseMenu), "Hide")]
    internal static class Patch_PauseMenuHide_AlwaysFalse
    {
        [HarmonyPriority(Priority.First)]
        [HarmonyPostfix]
        private static void Postfix()
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted) return;

            mod.Pausebool = false;

        }
    }

    [HarmonyPatch(typeof(Health), "DestroyOnDelay")]
    static class Patch_Health_DestroyOnDelay_SkipForAI_Server
    {
        static bool Prefix(Health __instance)
        {
            var mod = DuckovCoopMod.ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted || !mod.IsServer) return true;

            CharacterMainControl cmc = null;
            try { cmc = __instance.TryGetCharacter(); } catch { }
            if (!cmc) { try { cmc = __instance.GetComponentInParent<CharacterMainControl>(); } catch { } }

            bool isAI = cmc &&
                        (cmc.GetComponent<AICharacterController>() != null ||
                         cmc.GetComponent<NetAiTag>() != null);
            if (!isAI) return true;

            // AI Host DestroyOnDelay NRE
            return false;
        }
    }

    // DestroyOnDelay 
    [HarmonyPatch(typeof(Health), "DestroyOnDelay")]
    static class Patch_Health_DestroyOnDelay_Finalizer
    {
        static Exception Finalizer(Exception __exception)
        {
            // null 
            if (__exception != null)
                Debug.LogWarning("[COOP] Swallow DestroyOnDelay exception: " + __exception.Message);
            return null;
        }
    }

    [HarmonyPatch(typeof(InteractableLootbox), "CreateFromItem")]
    [HarmonyPriority(Priority.High)]
    static class Patch_Lootbox_CreateFromItem_DeferOnServerFromOnDead
    {
        // CreateFromItem 
        [ThreadStatic] static bool _bypassDefer;

        static bool Prefix(
            ItemStatsSystem.Item item,
            UnityEngine.Vector3 position,
            UnityEngine.Quaternion rotation,
            bool moveToMainScene,
            InteractableLootbox prefab,
            bool filterDontDropOnDead,
            ref InteractableLootbox __result)
        {
            var mod = ModBehaviour.Instance;
            var dead = DeadLootSpawnContext.InOnDead;

            // + + OnDead 
            if (_bypassDefer || mod == null || !mod.networkStarted || !mod.IsServer || dead == null)
                return true;

            mod.StartCoroutine(DeferOneFrame(
                item, position, rotation, moveToMainScene, prefab, filterDontDropOnDead, dead
            ));

            // OnDead 
            __result = null;
            return false;
        }

        static IEnumerator DeferOneFrame(
            ItemStatsSystem.Item item,
            UnityEngine.Vector3 position,
            UnityEngine.Quaternion rotation,
            bool moveToMainScene,
            InteractableLootbox prefab,
            bool filterDontDropOnDead,
            CharacterMainControl deadOwner)
        {
            yield return null;

            var old = DeadLootSpawnContext.InOnDead;
            DeadLootSpawnContext.InOnDead = deadOwner;

            _bypassDefer = true;
            try
            {
                InteractableLootbox.CreateFromItem(
                    item, position, rotation, moveToMainScene, prefab, filterDontDropOnDead
                );
            }
            finally
            {
                _bypassDefer = false;
                DeadLootSpawnContext.InOnDead = old;
            }
        }
    }



    [HarmonyPatch(typeof(AICharacterController), "Update")]
    static class Patch_AICC_ZeroForceTraceMain
    {
        static void Prefix(AICharacterController __instance)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted || !mod.IsServer) return;

            // CharacterMainControl.Main
            __instance.forceTracePlayerDistance = 0f;
        }
    }

    static class NcMainRedirector
    {
        [System.ThreadStatic] static CharacterMainControl _overrideMain;
        public static CharacterMainControl Current => _overrideMain;

        public static void Set(CharacterMainControl cmc) { _overrideMain = cmc; }
        public static void Clear() { _overrideMain = null; }
    }

    [HarmonyPatch(typeof(CharacterMainControl), "get_Main")]
    static class Patch_CMC_Main_OverrideDuringFSM
    {
        static bool Prefix(ref CharacterMainControl __result)
        {
            var ov = NcMainRedirector.Current;
            if (ov != null)
            {
                __result = ov;
                return false;
            }
            return true; 
        }
    }

    [HarmonyPatch(typeof(FSM), "OnGraphUpdate")]
    static class Patch_FSM_OnGraphUpdate_MainRedirect
    {
        static void Prefix(FSM __instance)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted || !mod.IsServer) return;


            Component agent = null;
            try
            {
                agent = (Component)AccessTools.Property(typeof(NodeCanvas.Framework.Graph), "agent").GetValue(__instance, null);
            }
            catch { }

            if (!agent) return;

            var aiCmc = agent.GetComponentInParent<CharacterMainControl>();
            if (!aiCmc) return;
            if (!mod.IsRealAI(aiCmc)) return; // AI Player 

            // AI Player Host + 
            var scene = agent.gameObject.scene;
            var best = FindNearestEnemyPlayer(mod, aiCmc, scene, aiCmc.transform.position);
            if (best != null)
                NcMainRedirector.Set(best);
        }

        static void Postfix()
        {
            // 
            NcMainRedirector.Clear();
        }

        static CharacterMainControl FindNearestEnemyPlayer(ModBehaviour mod, CharacterMainControl ai, Scene scene, Vector3 aiPos)
        {
            CharacterMainControl best = null;
            float bestD2 = float.MaxValue;

            void Try(CharacterMainControl cmc)
            {
                if (!cmc) return;
                if (!cmc.gameObject.activeInHierarchy) return;
                if (cmc.gameObject.scene != scene) return;
                if (cmc.Team == ai.Team) return;          
                if (!mod.IsAlive(cmc)) return;

                float d2 = (cmc.transform.position - aiPos).sqrMagnitude;
                if (d2 < bestD2) { bestD2 = d2; best = cmc; }
            }

            // Host Player
            Try(CharacterMainControl.Main);

            // Server Player 
            foreach (var kv in mod.remoteCharacters)
            {
                var go = kv.Value; if (!go) continue;
                var cmc = go.GetComponent<CharacterMainControl>() ?? go.GetComponentInChildren<CharacterMainControl>(true);
                Try(cmc);
            }

            return best;
        }
    }

    [HarmonyPatch(typeof(AICharacterController), "Update")]
    static class Patch_AIC_Update_PickNearestPlayer
    {
        static void Postfix(AICharacterController __instance)
        {
            var mod = ModBehaviour.Instance;
           
            if (mod == null || !mod.networkStarted || !mod.IsServer || __instance == null) return;

            var aiCmc = __instance.CharacterMainControl;
            if (!aiCmc) return;

            // :)))
            if (__instance.name == "AIController_Merchant_Myst(Clone)") { return; }

            CharacterMainControl best = null;
            float bestD2 = float.MaxValue;

            void Consider(CharacterMainControl cmc)
            {
                if (!cmc) return;
           
                if (cmc.Team == aiCmc.Team) return;

                // 
                var h = cmc.Health;
                if (!h) return;
                float hp = 1f;
                try { hp = h.CurrentHealth; } catch { }
                if (hp <= 0f) return;

                // / 
                Vector3 delta = cmc.transform.position - __instance.transform.position;
                float dist2 = delta.sqrMagnitude;
                float maxDist = (__instance.sightDistance > 0f ? __instance.sightDistance : 50f);
                if (dist2 > maxDist * maxDist) return;

                if (__instance.sightAngle > 1f)
                {
                    Vector3 fwd = __instance.transform.forward; fwd.y = 0f;
                    Vector3 dir = delta; dir.y = 0f;
                    if (dir.sqrMagnitude < 1e-6f) return;
                    float cos = Vector3.Dot(dir.normalized, fwd.normalized);
                    float cosThresh = Mathf.Cos(__instance.sightAngle * 0.5f * Mathf.Deg2Rad);
                    if (cos < cosThresh) return;
                }

                if (dist2 < bestD2)
                {
                    bestD2 = dist2;
                    best = cmc;
                }
            }

            // 1) Host 
            Consider(CharacterMainControl.Main); 

            // 2) ClientPlayer Host 
            if (mod.remoteCharacters != null)
            {
                foreach (var kv in mod.remoteCharacters) 
                {
                    var go = kv.Value;
                    if (!go) continue;
                    var cmc = go.GetComponent<CharacterMainControl>();
                    Consider(cmc);
                }
            }

            if (best == null) return;

            // / Switch 
            var cur = __instance.searchedEnemy;
            if (cur)
            {
                bool bad = false;
                try { if (cur.Team == aiCmc.Team) bad = true; } catch { }
                try { if (cur.health != null && cur.health.CurrentHealth <= 0f) bad = true; } catch { }
                if (cur.gameObject.scene != __instance.gameObject.scene) bad = true;

                if (!bad)
                {
                    float curD2 = (cur.transform.position - __instance.transform.position).sqrMagnitude;
       
                    if (curD2 <= bestD2 * 0.81f) return;
                }
            }

            // Switch Player
            var dr = best.mainDamageReceiver;
            if (dr)
            {
                __instance.searchedEnemy = dr;                      // /FSM searchedEnemy
                __instance.SetTarget(dr.transform);
                __instance.SetNoticedToTarget(dr);                  // 
            }
        }
    }

    [HarmonyPatch(typeof(LevelManager), "StartInit")]
    static class Patch_Level_StartInit_Gate
    {
        static bool Prefix(LevelManager __instance, SceneLoadingContext context)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null) return true;         
            if (mod.IsServer) return true;         

            bool needGate = mod.sceneVoteActive || (mod.networkStarted && !mod.IsServer);
            if (!needGate) return true;

            RunAsync(__instance, context).Forget();
            return false; 
        }

        static async UniTaskVoid RunAsync(LevelManager self, SceneLoadingContext ctx)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null) return;

            await mod.Client_SceneGateAsync();

            try
            {
                var m = AccessTools.Method(typeof(LevelManager), "InitLevel", new Type[] { typeof(SceneLoadingContext) });
                if (m != null) m.Invoke(self, new object[] { ctx });
            }
            catch (Exception e)
            {
                Debug.LogError("[SCENE] StartInit gate -> InitLevel failed: " + e);
            }
        }
    }

    [HarmonyPatch(typeof(MapSelectionEntry), "OnPointerClick")]
    static class Patch_Mapen_OnPointerClick
    {
        static bool Prefix(MapSelectionEntry __instance, PointerEventData eventData)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted) return true;  
            if (!mod.IsServer) return false;                       
            mod.IsMapSelectionEntry = true;
            mod.Host_BeginSceneVote_Simple(__instance.SceneID, "", false, false, false, "OnPointerClick");
            return false;
        }
    }

    [HarmonyPatch(typeof(Health), "Hurt", new[] { typeof(global::DamageInfo) })]
    static class Patch_CoopPlayer_Health_Hurt
    {
        [HarmonyPriority(Priority.First)]
        static bool Prefix(Health __instance, ref global::DamageInfo damageInfo)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted) return true; 

            if (!mod.IsServer)
            {
                bool isMain = false; try { isMain = __instance.IsMainCharacterHealth; } catch { }
                if (isMain) return true;
            }

            bool isProxy = __instance.gameObject.GetComponent<AutoRequestHealthBar>() != null;

            if (mod.IsServer && isProxy)
            {
                var owner = mod.Server_FindOwnerPeerByHealth(__instance);
                if (owner != null)
                {
                    try { mod.Server_ForwardHurtToOwner(owner, damageInfo); }
                    catch (System.Exception e) { UnityEngine.Debug.LogWarning("[HP] forward to owner failed: " + e); }
                }
                return false; 
            }

            if (!mod.IsServer && isProxy) return false;
            return true;
        }
    }

    static class WorldLootPrime
    {
        public static void PrimeIfClient(InteractableLootbox lb)
        {
            var mod = DuckovCoopMod.ModBehaviour.Instance;
            if (mod == null || mod.IsServer) return;
            if (!lb) return;

            var inv = lb.Inventory;
            if (!inv) return;

            // true false 
            LootSearchWorldGate.EnsureWorldFlag(inv);

            // 
            bool need = false;
            try { need = inv.NeedInspection; } catch { }
            if (need) return;

            try { lb.needInspect = true; } catch { }
            try { inv.NeedInspection = true; } catch { }

            // Inventory foreach 
            try
            {
                foreach (var it in inv)
                {
                    if (!it) continue;
                    try { it.Inspected = false; } catch { }
                }
            }
            catch { }
        }
    }

    static class LootSearchWorldGate
    {

        static readonly Dictionary<ItemStatsSystem.Inventory, bool> _world = new Dictionary<Inventory, bool>();

        public static void EnsureWorldFlag(ItemStatsSystem.Inventory inv)
        {
            if (inv) _world[inv] = true; // true 
        }

        public static bool IsWorldLootByInventory(ItemStatsSystem.Inventory inv)
        {
            if (!inv) return false;
            if (_world.TryGetValue(inv, out var yes) && yes) return true;

            // false 
            try
            {
                var boxes = UnityEngine.Object.FindObjectsOfType<InteractableLootbox>(true);
                foreach (var b in boxes)
                {
                    if (!b) continue;
                    if (b.Inventory == inv)
                    {
                        bool isWorld = b.GetComponent<Duckov.Utilities.LootBoxLoader>() != null;
                        if (isWorld) _world[inv] = true;
                        return isWorld;
                    }
                }
            }
            catch { }
            return false;
        }

        static MemberInfo _miNeedInspection;

        internal static bool GetNeedInspection(Inventory inv)
        {
            if (inv == null) return false;
            try
            {
                var m = FindNeedInspectionMember(inv.GetType());
                if (m is FieldInfo fi) return (bool)(fi.GetValue(inv) ?? false);
                if (m is PropertyInfo pi) return (bool)(pi.GetValue(inv) ?? false);
            }
            catch { }
            return false;
        }

        static MemberInfo FindNeedInspectionMember(Type t)
        {
            if (_miNeedInspection != null) return _miNeedInspection;
            _miNeedInspection = (MemberInfo)t.GetField("NeedInspection", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                               ?? t.GetProperty("NeedInspection", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return _miNeedInspection;
        }

        internal static void TrySetNeedInspection(ItemStatsSystem.Inventory inv, bool v)
        {
            if (!inv) return;
            inv.NeedInspection = v;
        }


        internal static void ForceTopLevelUninspected(Inventory inv)
        {
            if (inv == null) return;
            try
            {
                foreach (var it in inv)
                {
                    if (!it) continue;
                    try { it.Inspected = false; } catch {}
                }
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(Duckov.Utilities.LootSpawner), "Start")]
    static class Patch_LootSpawner_Start_PrimeNeedInspect
    {
        static void Postfix(Duckov.Utilities.LootSpawner __instance)
        {
            var lb = __instance.GetComponent<InteractableLootbox>();
            WorldLootPrime.PrimeIfClient(lb);
        }
    }

    [HarmonyPatch(typeof(Duckov.Utilities.LootSpawner), "Setup")]
    static class Patch_LootSpawner_Setup_PrimeNeedInspect
    {
        static void Postfix(Duckov.Utilities.LootSpawner __instance)
        {
            var lb = __instance.GetComponent<InteractableLootbox>();
            WorldLootPrime.PrimeIfClient(lb);
        }
    }

    [HarmonyPatch(typeof(Duckov.Utilities.LootBoxLoader), "Awake")]
    static class Patch_LootBoxLoader_Awake_PrimeNeedInspect
    {
        static void Postfix(Duckov.Utilities.LootBoxLoader __instance)
        {
            var lb = __instance.GetComponent<InteractableLootbox>();
            WorldLootPrime.PrimeIfClient(lb);
        }
    }


    // + :)
    [HarmonyPatch(typeof(Duckov.UI.LootView), nameof(Duckov.UI.LootView.HasInventoryEverBeenLooted))]
    static class Patch_LootView_HasInventoryEverBeenLooted_NeedAware_AllLoot
    {
        [HarmonyPriority(Priority.First)]
        static bool Prefix(ref bool __result, ItemStatsSystem.Inventory inventory)
        {
            if (!inventory) return true;

            if (LootboxDetectUtil.IsPrivateInventory(inventory)) return true;

            if (!LootboxDetectUtil.IsLootboxInventory(inventory)) return true;

            bool needInspect = false;
            try { needInspect = inventory.NeedInspection; } catch { }

            if (needInspect)
            {
                __result = false;   // UI / 
                return false;     
            }
            return true;          
        }
    }


    [HarmonyPatch(typeof(InteractableLootbox), "StartLoot")]
    static class Patch_Lootbox_StartLoot_RequestState_AndPrime
    {
        static void Postfix(InteractableLootbox __instance)
        {
            var m = ModBehaviour.Instance;
            if (m == null || !m.networkStarted || m.IsServer) return;

            var inv = __instance ? __instance.Inventory : null;
            if (!inv) return;

            try { inv.Loading = true; } catch { }
            m.Client_RequestLootState(inv);
            m.KickLootTimeout(inv, 1.5f);

            if (!LootboxDetectUtil.IsPrivateInventory(inv) && LootboxDetectUtil.IsLootboxInventory(inv))
            {
                bool needInspect = false; try { needInspect = inv.NeedInspection; } catch { }
                if (!needInspect)
                {
                    bool hasUninspected = false;
                    try
                    {
                        foreach (var it in inv) { if (it != null && !it.Inspected) { hasUninspected = true; break; } }
                    }
                    catch { }
                    if (hasUninspected) inv.NeedInspection = true;
                }
            }
        }
    }


    [HarmonyPatch(typeof(global::Duckov.UI.LootView), "OnStartLoot")]
    static class Patch_LootView_OnStartLoot_PrimeSearchGate_Robust
    {
        static void Postfix(global::Duckov.UI.LootView __instance, global::InteractableLootbox lootbox)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted || mod.IsServer) return;

            var inv = __instance.TargetInventory;
            if (!inv || lootbox == null) return;

            if (LootboxDetectUtil.IsPrivateInventory(inv)) return;

            if (inv.hasBeenInspectedInLootBox) return;

            {
                int last = inv.GetLastItemPosition();
                bool allInspectedNow = true;
                for (int i = 0; i <= last; i++)
                {
                    var it = inv.GetItemAt(i);
                    if (it != null && !it.Inspected) { allInspectedNow = false; break; }
                }
                if (allInspectedNow) return;
            }

            TrySetNeedInspection(inv, true);
            TrySetLootboxNeedInspect(lootbox, true);

            mod.StartCoroutine(KickSearchGateOnceStable(inv, lootbox));
        }

        static System.Collections.IEnumerator KickSearchGateOnceStable(
            global::ItemStatsSystem.Inventory inv,
            global::InteractableLootbox lootbox)
        {
            yield return null;
            yield return null;

            if (!inv) yield break;

            int last = inv.GetLastItemPosition();
            bool allInspected = true;
            for (int i = 0; i <= last; i++)
            {
                var it = inv.GetItemAt(i);
                if (it != null && !it.Inspected) { allInspected = false; break; }
            }

            TrySetNeedInspection(inv, !allInspected);
            TrySetLootboxNeedInspect(lootbox, !allInspected);
        }

        static void TrySetNeedInspection(global::ItemStatsSystem.Inventory inv, bool v)
        {
            try { inv.NeedInspection = v; } catch { }
        }

        static void TrySetLootboxNeedInspect(global::InteractableLootbox box, bool v)
        {
            if (box == null) return;
            try
            {
                var t = box.GetType();
                var f = HarmonyLib.AccessTools.Field(t, "needInspect");
                if (f != null) { f.SetValue(box, v); return; }
                var p = HarmonyLib.AccessTools.Property(t, "needInspect");
                if (p != null && p.CanWrite) { p.SetValue(box, v, null); return; }
            }
            catch { }
        }
    }


    [HarmonyPatch(typeof(global::ItemStatsSystem.Inventory), nameof(global::ItemStatsSystem.Inventory.AddAt),
    new System.Type[] { typeof(global::ItemStatsSystem.Item), typeof(int) })]
    static class Patch_Inventory_AddAt_FlagUninspected_WhenApplyingLoot
    {
        static void Postfix(global::ItemStatsSystem.Inventory __instance, global::ItemStatsSystem.Item item)
        {
            ApplyUninspectedFlag(__instance, item);
        }

        static void ApplyUninspectedFlag(global::ItemStatsSystem.Inventory inv, global::ItemStatsSystem.Item item)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted || mod.IsServer) return;


            if (!mod.ApplyingLootState) return;

            if (LootboxDetectUtil.IsPrivateInventory(inv)) return;
            if (!(LootboxDetectUtil.IsLootboxInventory(inv) || ModBehaviour.IsCurrentLootInv(inv))) return;

            try
            {
                int last = inv.GetLastItemPosition();
                bool hasUninspected = false;
                for (int i = 0; i <= last; i++)
                {
                    var it = inv.GetItemAt(i);
                    if (it != null && !it.Inspected) { hasUninspected = true; break; }
                }
                inv.NeedInspection = hasUninspected;
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(global::ItemStatsSystem.Inventory), "AddItem",
        new System.Type[] { typeof(global::ItemStatsSystem.Item) })]
    static class Patch_Inventory_AddItem_FlagUninspected_WhenApplyingLoot
    {
        static void Postfix(global::ItemStatsSystem.Inventory __instance, global::ItemStatsSystem.Item item)
        {
            ApplyUninspectedFlag(__instance, item);
        }

        static void ApplyUninspectedFlag(global::ItemStatsSystem.Inventory inv, global::ItemStatsSystem.Item item)
        {
            var mod = ModBehaviour.Instance;
            if (mod == null || !mod.networkStarted || mod.IsServer) return;

            if (!mod.ApplyingLootState) return;

            if (LootboxDetectUtil.IsPrivateInventory(inv)) return;
            if (!(LootboxDetectUtil.IsLootboxInventory(inv) || ModBehaviour.IsCurrentLootInv(inv))) return;

            try
            {
                int last = inv.GetLastItemPosition();
                bool hasUninspected = false;
                for (int i = 0; i <= last; i++)
                {
                    var it = inv.GetItemAt(i);
                    if (it != null && !it.Inspected) { hasUninspected = true; break; }
                }
                inv.NeedInspection = hasUninspected;
            }
            catch { }
        }
    }

}
