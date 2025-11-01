using System;
using System.Collections.Generic;
using HarmonyLib;
using Cysharp.Threading.Tasks;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace DuckovCoopMod
{
    public partial class ModBehaviour
    {
        public void Server_SendAiSeeds(NetPeer target = null)
        {
            if (!IsServer) return;

            aiRootSeeds.Clear();
            sceneSeed = Environment.TickCount ^ UnityEngine.Random.Range(int.MinValue, int.MaxValue);

            var roots = UnityEngine.Object.FindObjectsOfType<CharacterSpawnerRoot>(true);
            var pairs = new List<(int id, int seed)>(roots.Length * 2);
            foreach (var r in roots)
            {
                int idA = StableRootId(r);
                int idB = StableRootId_Alt(r);
                int seed = DeriveSeed(sceneSeed, idA);
                aiRootSeeds[idA] = seed;
                pairs.Add((idA, seed));
                if (idB != idA) pairs.Add((idB, seed));
            }

            var w = writer; w.Reset();
            w.Put((byte)Op.AI_SEED_SNAPSHOT);
            w.Put(sceneSeed);
            w.Put(pairs.Count);
            foreach (var pr in pairs) { w.Put(pr.id); w.Put(pr.seed); }
            if (target == null) BroadcastReliable(w); else TransportSend(target, w, true);
        }

        public int StableRootId_Alt(CharacterSpawnerRoot r)
        {
            if (r == null) return 0;
            int sceneIndex = -1;
            try { var fi = typeof(CharacterSpawnerRoot).GetField("relatedScene", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance); if (fi != null) sceneIndex = (int)fi.GetValue(r); } catch { }
            if (sceneIndex < 0) sceneIndex = UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex;
            Vector3 p = r.transform.position; int qx = Mathf.RoundToInt(p.x * 10f); int qy = Mathf.RoundToInt(p.y * 10f); int qz = Mathf.RoundToInt(p.z * 10f);
            string key = $"{sceneIndex}:{r.name}:{qx},{qy},{qz}"; return StableHash(key);
        }

        void HandleAiSeedSnapshot(NetPacketReader r)
        {
            sceneSeed = r.GetInt(); aiRootSeeds.Clear(); int n = r.GetInt();
            for (int i = 0; i < n; i++) { int id = r.GetInt(); int seed = r.GetInt(); aiRootSeeds[id] = seed; }
        }

        public int StableHash(string s)
        { unchecked { uint h = 2166136261; for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619; } return (int)h; } }
        public string TransformPath(Transform t)
        { var stack = new Stack<string>(); while (t != null) { stack.Push(t.name); t = t.parent; } return string.Join("/", stack); }
        public int DeriveSeed(int a, int b)
        { unchecked { uint h = 2166136261; h ^= (uint)a; h *= 16777619; h ^= (uint)b; h *= 16777619; return (int)h; } }

        public void TryFreezeAI(CharacterMainControl cmc)
        {
            if (!cmc || !IsRealAI(cmc)) return;
            foreach (var a in UnityEngine.Object.FindObjectsOfType<AICharacterController>(true)) { if (a) a.enabled = false; }
            foreach (var a in UnityEngine.Object.FindObjectsOfType<AI_PathControl>(true)) { if (a) a.enabled = false; }
            foreach (var a in UnityEngine.Object.FindObjectsOfType<NodeCanvas.StateMachines.FSMOwner>(true)) { if (a) a.enabled = false; }
            foreach (var a in UnityEngine.Object.FindObjectsOfType<NodeCanvas.Framework.Blackboard>(true)) { if (a) a.enabled = false; }
        }

        public void RegisterAi(int aiId, CharacterMainControl cmc)
        {
            if (!IsRealAI(cmc)) return;
            aiById[aiId] = cmc;

            float pendCur = -1f, pendMax = -1f;
            if (_cliPendingAiHealth.TryGetValue(aiId, out var pc)) { pendCur = pc; _cliPendingAiHealth.Remove(aiId); }
            if (_cliPendingAiMax.TryGetValue(aiId, out var pm)) { pendMax = pm; _cliPendingAiMax.Remove(aiId); }

            var h = cmc.Health;
            if (h)
            {
                if (pendMax > 0f)
                {
                    _cliAiMaxOverride[h] = pendMax;
                    try { FI_defaultMax?.SetValue(h, Mathf.RoundToInt(pendMax)); } catch { }
                    try { FI_lastMax?.SetValue(h, -12345f); } catch { }
                    try { h.OnMaxHealthChange?.Invoke(h); } catch { }
                }
                if (pendCur >= 0f || pendMax > 0f)
                {
                    float applyMax = (pendMax > 0f) ? pendMax : h.MaxHealth;
                    ForceSetHealth(h, applyMax, Mathf.Max(0f, pendCur >= 0f ? pendCur : h.CurrentHealth), ensureBar: true);
                }
            }

            if (IsServer && cmc) Server_BroadcastAiLoadout(aiId, cmc);

            if (!IsServer && cmc)
            {
                var follower = cmc.GetComponent<NetAiFollower>(); if (!follower) follower = cmc.gameObject.AddComponent<NetAiFollower>();
                if (!cmc.GetComponent<NetAiVisibilityGuard>()) cmc.gameObject.AddComponent<NetAiVisibilityGuard>();
                try { var tag = cmc.GetComponent<NetAiTag>() ?? cmc.gameObject.AddComponent<NetAiTag>(); if (tag.aiId != aiId) tag.aiId = aiId; } catch { }
                if (!cmc.GetComponent<RemoteReplicaTag>()) cmc.gameObject.AddComponent<RemoteReplicaTag>();
                if (_pendingAiAnims.TryGetValue(aiId, out var pst)) { follower.SetAnim(pst.speed, pst.dirX, pst.dirY, pst.hand, pst.gunReady, pst.dashing); _pendingAiAnims.Remove(aiId); }
                if (pendingAiLoadouts.TryGetValue(aiId, out var data)) { pendingAiLoadouts.Remove(aiId); Client_ApplyAiLoadout(aiId, data.equips, data.weapons, data.faceJson, data.modelName, data.iconType, data.showName, data.displayName).Forget(); }
            }
        }

        private List<EquipmentSyncData> GetLocalAIEquipment(CharacterMainControl cmc)
        {
            var equipmentList = new List<EquipmentSyncData>();
            var equipmentController = cmc?.EquipmentController; if (equipmentController == null) return equipmentList;
            var slotNames = new[] { "armorSlot", "helmatSlot", "faceMaskSlot", "backpackSlot", "headsetSlot" };
            var slotHashes = new[] { CharacterEquipmentController.armorHash, CharacterEquipmentController.helmatHash, CharacterEquipmentController.faceMaskHash, CharacterEquipmentController.backpackHash, CharacterEquipmentController.headsetHash };
            for (int i = 0; i < slotNames.Length; i++)
            {
                try
                {
                    var slotField = Traverse.Create(equipmentController).Field<ItemStatsSystem.Items.Slot>(slotNames[i]);
                    if (slotField.Value == null) continue;
                    var slot = slotField.Value; string itemId = (slot?.Content != null) ? slot.Content.TypeID.ToString() : "";
                    equipmentList.Add(new EquipmentSyncData { SlotHash = slotHashes[i], ItemId = itemId });
                }
                catch { }
            }
            return equipmentList;
        }

        public void Server_BroadcastAiLoadout(int aiId, CharacterMainControl cmc)
        {
            if (!IsServer || cmc == null) return;
            writer.Reset(); writer.Put((byte)Op.AI_LOADOUT_SNAPSHOT); writer.Put(AI_LOADOUT_VER); writer.Put(aiId);
            var eqList = GetLocalAIEquipment(cmc);
            writer.Put(eqList.Count); foreach (var eq in eqList) { writer.Put(eq.SlotHash); int tid = 0; if (!string.IsNullOrEmpty(eq.ItemId)) int.TryParse(eq.ItemId, out tid); writer.Put(tid); }
            var listW = new List<(int slot, int tid)>(); var gun = cmc.GetGun(); var melee = cmc.GetMeleeWeapon(); if (gun != null) listW.Add(((int)gun.handheldSocket, gun.Item ? gun.Item.TypeID : 0)); if (melee != null) listW.Add(((int)melee.handheldSocket, melee.Item ? melee.Item.TypeID : 0));
            writer.Put(listW.Count); foreach (var p in listW) { writer.Put(p.slot); writer.Put(p.tid); }
            string faceJson = null; writer.Put(!string.IsNullOrEmpty(faceJson)); if (!string.IsNullOrEmpty(faceJson)) writer.Put(faceJson);
            string modelName = NormalizePrefabName(cmc.characterModel ? cmc.characterModel.name : null);
            int iconType = 0; bool showName = false; try { var pr = cmc.characterPreset; if (pr) { var e = (global::CharacterIconTypes)iconType; if (e == global::CharacterIconTypes.none && pr.GetCharacterIcon() != null) iconType = (int)FR_IconType(pr); e = (global::CharacterIconTypes)iconType; if (!showName && (e == global::CharacterIconTypes.boss || e == global::CharacterIconTypes.elete)) showName = true; } } catch { }
            writer.Put(modelName ?? ""); writer.Put(iconType); writer.Put(showName); writer.Put(false);
            BroadcastReliable(writer);
        }

        private UniTask Client_ApplyAiLoadout(int aiId, List<(int slot, int tid)> equips, List<(int slot, int tid)> weapons, string faceJson, string modelName, int iconType, bool showName, string displayName)
        { return Client_ApplyAiLoadout(aiId, equips.ToArray(), weapons.ToArray(), faceJson, modelName, iconType, showName, displayName); }

        private async UniTask Client_ApplyAiLoadout(int aiId, (int slot, int tid)[] equips, (int slot, int tid)[] weapons, string faceJson, string modelName, int iconType, bool showName, string displayName)
        {
            if (!aiById.TryGetValue(aiId, out var cmc) || !cmc) return;
            try { if (!string.IsNullOrEmpty(faceJson)) ApplyFaceJsonToModel(cmc.characterModel, faceJson); } catch { }
            try { await UniTask.Yield(); } catch { }
        }

        private CharacterMainControl TryAutoBindAi(int aiId, Vector3 snapPos)
        {
            if (aiById.TryGetValue(aiId, out var exists) && exists) return exists;
            float now = Time.time; if (_lastAutoBindTryTime.TryGetValue(aiId, out var t) && now - t < AUTOBIND_COOLDOWN) return null; _lastAutoBindTryTime[aiId] = now;
            var all = UnityEngine.Object.FindObjectsOfType<AICharacterController>(true);
            CharacterMainControl best = null; float bestD2 = AUTOBIND_RADIUS * AUTOBIND_RADIUS;
            foreach (var a in all)
            {
                if (!a) continue; var c = a.GetComponent<CharacterMainControl>(); if (!c || !IsRealAI(c)) continue; if (aiById.ContainsValue(c)) continue;
                float d2 = (c.transform.position - snapPos).sqrMagnitude; if (d2 < bestD2) { bestD2 = d2; best = c; }
            }
            if (best) RegisterAi(aiId, best);
            return best;
        }

        static UnityEngine.Sprite ResolveIconSprite(int iconType)
        {
            switch ((global::CharacterIconTypes)iconType)
            {
                case global::CharacterIconTypes.none: return null;
                case global::CharacterIconTypes.elete: return Duckov.Utilities.GameplayDataSettings.UIStyle.EleteCharacterIcon;
                case global::CharacterIconTypes.pmc: return Duckov.Utilities.GameplayDataSettings.UIStyle.PmcCharacterIcon;
                case global::CharacterIconTypes.boss: return Duckov.Utilities.GameplayDataSettings.UIStyle.BossCharacterIcon;
                case global::CharacterIconTypes.merchant: return Duckov.Utilities.GameplayDataSettings.UIStyle.MerchantCharacterIcon;
                case global::CharacterIconTypes.pet: return Duckov.Utilities.GameplayDataSettings.UIStyle.PetCharacterIcon;
                default: return null;
            }
        }

        private async UniTask RefreshNameIconWithRetries(CharacterMainControl cmc, int iconType, bool showName, string displayNameFromHost)
        {
            await UniTask.Yield();
            try { if (!cmc) return; } catch { return; }
        }

        public void Server_BroadcastAiNameIcon(int aiId, CharacterMainControl cmc)
        {
            if (!networkStarted || !IsServer || aiId == 0 || !cmc) return;
            int iconType = 0; bool showName = false; string displayName = null;
            try
            {
                var pr = cmc.characterPreset; if (pr)
                {
                    try { iconType = (int)FR_IconType(pr); } catch { }
                    try { if (iconType == 0 && pr.GetCharacterIcon() != null) iconType = (int)FR_IconType(pr); } catch { }
                    try { showName = pr.showName; } catch { }
                    var e = (global::CharacterIconTypes)iconType; if (!showName && (e == global::CharacterIconTypes.boss || e == global::CharacterIconTypes.elete)) showName = true;
                    try { displayName = pr.Name; } catch { }
                }
            }
            catch { }
            var w = new NetDataWriter(); w.Put((byte)Op.AI_NAME_ICON); w.Put(aiId); w.Put(iconType); w.Put(showName); w.Put(!string.IsNullOrEmpty(displayName)); if (!string.IsNullOrEmpty(displayName)) w.Put(displayName); BroadcastReliable(w);
        }

        public void ReapplyFaceIfKnown(CharacterMainControl cmc)
        {
            if (!cmc || IsServer) return; int aiId = -1; foreach (var kv in aiById) { if (kv.Value == cmc) { aiId = kv.Key; break; } } if (aiId < 0) return; if (_aiFaceJsonById.TryGetValue(aiId, out var json) && !string.IsNullOrEmpty(json)) ApplyFaceJsonToModel(cmc.characterModel, json);
        }

        public static void ApplyFaceJsonToModel(CharacterModel model, string faceJson)
        {
            if (model == null || string.IsNullOrEmpty(faceJson)) return; try { CustomFaceSettingData data; bool ok = CustomFaceSettingData.JsonToData(faceJson, out data); if (!ok) data = JsonUtility.FromJson<CustomFaceSettingData>(faceJson); model.SetFaceFromData(data); } catch { }
        }

        static string NormalizePrefabName(string n)
        {
            if (string.IsNullOrEmpty(n)) return n; n = n.Trim(); const string clone = "(Clone)"; if (n.EndsWith(clone)) n = n.Substring(0, n.Length - clone.Length).Trim(); return n;
        }
    }
}
