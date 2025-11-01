using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Duckov.Utilities;
using ItemStatsSystem;
using UnityEngine;

namespace DuckovCoopMod
{
    public partial class ModBehaviour
    {
        private void SendAnimationStatus()
        {
            if (!networkStarted) return;

            var mainControl = CharacterMainControl.Main;
            if (mainControl == null) return;

            var model = mainControl.modelRoot.Find("0_CharacterModel_Custom_Template(Clone)");
            if (model == null) return;

            var animCtrl = model.GetComponent<CharacterAnimationControl_MagicBlend>();
            if (animCtrl == null || animCtrl.animator == null) return;

            var anim = animCtrl.animator;
            var state = anim.GetCurrentAnimatorStateInfo(0);
            int stateHash = state.shortNameHash;
            float normTime = state.normalizedTime;

            writer.Reset();
            writer.Put((byte)Op.ANIM_SYNC);

            if (IsServer)
            {
                writer.Put(localPlayerStatus.EndPoint);
                writer.Put(anim.GetFloat("MoveSpeed"));
                writer.Put(anim.GetFloat("MoveDirX"));
                writer.Put(anim.GetFloat("MoveDirY"));
                writer.Put(anim.GetBool("Dashing"));
                writer.Put(anim.GetBool("Attack"));
                writer.Put(anim.GetInteger("HandState"));
                writer.Put(anim.GetBool("GunReady"));
                writer.Put(stateHash);
                writer.Put(normTime);
                TransportBroadcast(writer, false);
            }
            else
            {
                if (connectedPeer == null) return;
                writer.Put(anim.GetFloat("MoveSpeed"));
                writer.Put(anim.GetFloat("MoveDirX"));
                writer.Put(anim.GetFloat("MoveDirY"));
                writer.Put(anim.GetBool("Dashing"));
                writer.Put(anim.GetBool("Attack"));
                writer.Put(anim.GetInteger("HandState"));
                writer.Put(anim.GetBool("GunReady"));
                writer.Put(stateHash);
                writer.Put(normTime);
                TransportSendToServer(writer, false);
            }
        }

        void HandleClientAnimationStatus(NetPeer sender, NetPacketReader reader)
        {
            float moveSpeed = reader.GetFloat();
            float moveDirX = reader.GetFloat();
            float moveDirY = reader.GetFloat();
            bool isDashing = reader.GetBool();
            bool isAttacking = reader.GetBool();
            int handState = reader.GetInt();
            bool gunReady = reader.GetBool();
            int stateHash = reader.GetInt();
            float normTime = reader.GetFloat();

            HandleRemoteAnimationStatus(sender, moveSpeed, moveDirX, moveDirY, isDashing, isAttacking, handState, gunReady, stateHash, normTime);

            string playerId = playerStatuses.TryGetValue(sender, out var st) && !string.IsNullOrEmpty(st.EndPoint)
                ? st.EndPoint
                : sender.EndPoint.ToString();

            foreach (var p in netManager.ConnectedPeerList)
            {
                if (p == sender) continue;
                var w = new NetDataWriter();
                w.Put((byte)Op.ANIM_SYNC);
                w.Put(playerId);
                w.Put(moveSpeed);
                w.Put(moveDirX);
                w.Put(moveDirY);
                w.Put(isDashing);
                w.Put(isAttacking);
                w.Put(handState);
                w.Put(gunReady);
                w.Put(stateHash);
                w.Put(normTime);
                TransportSend(p, w, false);
            }
        }

        void HandleRemoteAnimationStatus(NetPeer peer, float moveSpeed, float moveDirX, float moveDirY,
                                  bool isDashing, bool isAttacking, int handState, bool gunReady,
                                  int stateHash, float normTime)
        {
            if (!remoteCharacters.TryGetValue(peer, out var remoteObj) || remoteObj == null) return;

            var ai = AnimInterpUtil.Attach(remoteObj);
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

        private async UniTask<GameObject> CreateRemoteCharacterAsync(NetPeer peer, Vector3 position, Quaternion rotation, string customFaceJson)
        {
            if (remoteCharacters.ContainsKey(peer) && remoteCharacters[peer] != null) return null;

            var levelManager = LevelManager.Instance;
            if (levelManager == null || levelManager.MainCharacter == null) return null;

            GameObject instance = Instantiate(CharacterMainControl.Main.gameObject, position, rotation);
            var characterModel = instance.GetComponent<CharacterMainControl>();

            var cmc = instance.GetComponent<CharacterMainControl>();
            StripAllHandItems(cmc);
            var itemLoaded = await Saves.ItemSavesUtilities.LoadItem(LevelManager.MainCharacterItemSaveKey);
            if (itemLoaded == null)
            {
                itemLoaded = await ItemAssetsCollection.InstantiateAsync(GameplayDataSettings.ItemAssets.DefaultCharacterItemTypeID);
                Debug.LogWarning("Item Loading failed");
            }
            Traverse.Create(characterModel).Field<Item>("characterItem").Value = itemLoaded;

            instance.transform.SetPositionAndRotation(position, rotation);

            MakeRemotePhysicsPassive(instance);
            StripAllCustomFaceParts(instance);

            if (characterModel?.characterModel.CustomFace != null && !string.IsNullOrEmpty(customFaceJson))
            {
                var customFaceData = JsonUtility.FromJson<CustomFaceSettingData>(customFaceJson);
                characterModel.characterModel.CustomFace.LoadFromData(customFaceData);
            }

            try
            {
                var cm = characterModel.characterModel;
                COOPManager.ChangeArmorModel(cm, null);
                COOPManager.ChangeHelmatModel(cm, null);
                COOPManager.ChangeFaceMaskModel(cm, null);
                COOPManager.ChangeBackpackModel(cm, null);
                COOPManager.ChangeHeadsetModel(cm, null);
            }
            catch { }

            instance.AddComponent<RemoteReplicaTag>();
            var anim = instance.GetComponentInChildren<Animator>(true);
            if (anim)
            {
                anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                anim.updateMode = AnimatorUpdateMode.Normal;
            }

            var h = instance.GetComponentInChildren<Health>(true);
            if (h) h.autoInit = false;
            instance.AddComponent<AutoRequestHealthBar>();
            Server_HookOneHealth(peer, instance);
            instance.AddComponent<HostForceHealthBar>();

            NetInterpUtil.Attach(instance)?.Push(position, rotation);
            AnimInterpUtil.Attach(instance);
            cmc.gameObject.SetActive(false);
            remoteCharacters[peer] = instance;
            cmc.gameObject.SetActive(true);
            return instance;
        }

        private async UniTask CreateRemoteCharacterForClient(string playerId, Vector3 position, Quaternion rotation, string customFaceJson)
        {
            if (IsSelfId(playerId)) return;
            if (clientRemoteCharacters.ContainsKey(playerId) && clientRemoteCharacters[playerId] != null) return;

            Debug.Log(playerId + " CreateRemoteCharacterForClient");

            var levelManager = LevelManager.Instance;
            if (levelManager == null || levelManager.MainCharacter == null) return;

            GameObject instance = Instantiate(CharacterMainControl.Main.gameObject, position, rotation);
            var characterModel = instance.GetComponent<CharacterMainControl>();

            var itemLoaded = await Saves.ItemSavesUtilities.LoadItem(LevelManager.MainCharacterItemSaveKey);
            if (itemLoaded == null)
            {
                itemLoaded = await ItemAssetsCollection.InstantiateAsync(GameplayDataSettings.ItemAssets.DefaultCharacterItemTypeID);
            }
            Traverse.Create(characterModel).Field<Item>("characterItem").Value = itemLoaded;

            var cmc = instance.GetComponent<CharacterMainControl>();
            StripAllHandItems(cmc);

            instance.transform.SetPositionAndRotation(position, rotation);

            var cmc0 = instance.GetComponentInChildren<CharacterMainControl>(true);
            if (cmc0 && cmc0.modelRoot)
            {
                var e = rotation.eulerAngles;
                cmc0.modelRoot.transform.rotation = Quaternion.Euler(0f, e.y, 0f);
            }

            MakeRemotePhysicsPassive(instance);
            StripAllCustomFaceParts(instance);

            if (string.IsNullOrEmpty(customFaceJson))
            {
                if (clientPlayerStatuses.TryGetValue(playerId, out var st) && !string.IsNullOrEmpty(st.CustomFaceJson))
                    customFaceJson = st.CustomFaceJson;
                else if (_cliPendingFace.TryGetValue(playerId, out var pending) && !string.IsNullOrEmpty(pending))
                    customFaceJson = pending;
            }

            Client_ApplyFaceIfAvailable(playerId, instance, customFaceJson);

            try
            {
                var cm = characterModel.characterModel;
                COOPManager.ChangeArmorModel(cm, null);
                COOPManager.ChangeHelmatModel(cm, null);
                COOPManager.ChangeFaceMaskModel(cm, null);
                COOPManager.ChangeBackpackModel(cm, null);
                COOPManager.ChangeHeadsetModel(cm, null);
            }
            catch { }

            instance.AddComponent<RemoteReplicaTag>();
            var anim = instance.GetComponentInChildren<Animator>(true);
            if (anim)
            {
                anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                anim.updateMode = AnimatorUpdateMode.Normal;
            }

            var h = instance.GetComponentInChildren<Health>(true);
            if (h) h.autoInit = false;
            instance.AddComponent<AutoRequestHealthBar>();
            Client_ApplyPendingRemoteIfAny(playerId, instance);

            NetInterpUtil.Attach(instance)?.Push(position, rotation);
            AnimInterpUtil.Attach(instance);
            cmc.gameObject.SetActive(false);
            clientRemoteCharacters[playerId] = instance;
            cmc.gameObject.SetActive(true);
        }

        private void ModBehaviour_onSlotContentChanged(ItemStatsSystem.Items.Slot obj)
        {
            if (!networkStarted || localPlayerStatus == null || !localPlayerStatus.IsInGame) return;
            if (obj == null) return;

            string itemId1 = "";
            if (obj.Content != null) itemId1 = obj.Content.TypeID.ToString();
            int slotHash1 = obj.GetHashCode();
            if (obj.Key == "Helmat") slotHash1 = 200;
            if (obj.Key == "Armor") slotHash1 = 100;
            if (obj.Key == "FaceMask") slotHash1 = 300;
            if (obj.Key == "Backpack") slotHash1 = 400;
            if (obj.Key == "Head") slotHash1 = 500;

            var equipmentData1 = new EquipmentSyncData { SlotHash = slotHash1, ItemId = itemId1 };
            SendEquipmentUpdate(equipmentData1);
        }

        private void SendEquipmentUpdate(EquipmentSyncData equipmentData)
        {
            if (localPlayerStatus == null || !networkStarted) return;

            writer.Reset();
            writer.Put((byte)Op.EQUIPMENT_UPDATE);
            writer.Put(localPlayerStatus.EndPoint);
            writer.Put(equipmentData.SlotHash);
            writer.Put(equipmentData.ItemId ?? "");

            if (IsServer) TransportBroadcast(writer, true);
            else TransportSendToServer(writer, true);
        }

        private void SendWeaponUpdate(WeaponSyncData weaponSyncData)
        {
            if (localPlayerStatus == null || !networkStarted) return;

            writer.Reset();
            writer.Put((byte)Op.PLAYERWEAPON_UPDATE);
            writer.Put(localPlayerStatus.EndPoint);
            writer.Put(weaponSyncData.SlotHash);
            writer.Put(weaponSyncData.ItemId ?? "");

            if (IsServer) TransportBroadcast(writer, true);
            else TransportSendToServer(writer, true);
        }

        private void HandleEquipmentUpdate(NetPeer sender, NetPacketReader reader)
        {
            string endPoint = reader.GetString();
            int slotHash = reader.GetInt();
            string itemId = reader.GetString();

            ApplyEquipmentUpdate(sender, slotHash, itemId).Forget();

            foreach (var p in netManager.ConnectedPeerList)
            {
                if (p == sender) continue;
                var w = new NetDataWriter();
                w.Put((byte)Op.EQUIPMENT_UPDATE);
                w.Put(endPoint);
                w.Put(slotHash);
                w.Put(itemId);
                TransportSend(p, w, true);
            }
        }

        private void HandleWeaponUpdate(NetPeer sender, NetPacketReader reader)
        {
            string endPoint = reader.GetString();
            int slotHash = reader.GetInt();
            string itemId = reader.GetString();

            ApplyWeaponUpdate(sender, slotHash, itemId).Forget();

            foreach (var p in netManager.ConnectedPeerList)
            {
                if (p == sender) continue;
                var w = new NetDataWriter();
                w.Put((byte)Op.PLAYERWEAPON_UPDATE);
                w.Put(endPoint);
                w.Put(slotHash);
                w.Put(itemId);
                TransportSend(p, w, true);
            }
        }

        private async UniTask ApplyEquipmentUpdate(NetPeer peer, int slotHash, string itemId)
        {
            if (!remoteCharacters.TryGetValue(peer, out var remoteObj) || remoteObj == null) return;

            var characterModel = remoteObj.GetComponent<CharacterMainControl>().characterModel;
            if (characterModel == null) return;

            if (string.IsNullOrEmpty(itemId))
            {
                if (slotHash == 100) COOPManager.ChangeArmorModel(characterModel, null);
                if (slotHash == 200) COOPManager.ChangeHelmatModel(characterModel, null);
                if (slotHash == 300) COOPManager.ChangeFaceMaskModel(characterModel, null);
                if (slotHash == 400) COOPManager.ChangeBackpackModel(characterModel, null);
                if (slotHash == 500) COOPManager.ChangeHeadsetModel(characterModel, null);
                return;
            }

            string slotName = null;
            if (slotHash == CharacterEquipmentController.armorHash) slotName = "armorSlot";
            else if (slotHash == CharacterEquipmentController.helmatHash) slotName = "helmatSlot";
            else if (slotHash == CharacterEquipmentController.faceMaskHash) slotName = "faceMaskSlot";
            else if (slotHash == CharacterEquipmentController.backpackHash) slotName = "backpackSlot";
            else if (slotHash == CharacterEquipmentController.headsetHash) slotName = "headsetSlot";
            else
            {
                if (!string.IsNullOrEmpty(itemId) && int.TryParse(itemId, out var ids))
                {
                    Debug.Log($"Trying to update equipment: {peer.EndPoint}, Slot={slotHash}, ItemId={itemId}");
                    var item = await COOPManager.GetItemAsync(ids);
                    if (item == null)
                    {
                        Debug.LogWarning($"Failed to get item: ItemId={itemId}, slot {slotHash} not updated");
                    }
                    if (slotHash == 100) COOPManager.ChangeArmorModel(characterModel, item);
                    if (slotHash == 200) COOPManager.ChangeHelmatModel(characterModel, item);
                    if (slotHash == 300) COOPManager.ChangeFaceMaskModel(characterModel, item);
                    if (slotHash == 400) COOPManager.ChangeBackpackModel(characterModel, item);
                    if (slotHash == 500) COOPManager.ChangeHeadsetModel(characterModel, item);
                }
                return;
            }

            try
            {
                if (int.TryParse(itemId, out var ids))
                {
                    var item = await COOPManager.GetItemAsync(ids);
                    if (item != null)
                    {
                        if (slotName == "armorSlot") COOPManager.ChangeArmorModel(characterModel, item);
                        if (slotName == "helmatSlot") COOPManager.ChangeHelmatModel(characterModel, item);
                        if (slotName == "faceMaskSlot") COOPManager.ChangeFaceMaskModel(characterModel, item);
                        if (slotName == "backpackSlot") COOPManager.ChangeBackpackModel(characterModel, item);
                        if (slotName == "headsetSlot") COOPManager.ChangeHeadsetModel(characterModel, item);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Update equipment failed (Host): {peer.EndPoint}, SlotHash={slotHash}, ItemId={itemId}, error: {ex.Message}");
            }
        }

        private async UniTask ApplyEquipmentUpdate_Client(string playerId, int slotHash, string itemId)
        {
            if (IsSelfId(playerId)) return;
            if (!clientRemoteCharacters.TryGetValue(playerId, out var remoteObj) || remoteObj == null) return;

            var characterModel = remoteObj.GetComponent<CharacterMainControl>().characterModel;
            if (characterModel == null) return;

            if (string.IsNullOrEmpty(itemId))
            {
                if (slotHash == 100) COOPManager.ChangeArmorModel(characterModel, null);
                if (slotHash == 200) COOPManager.ChangeHelmatModel(characterModel, null);
                if (slotHash == 300) COOPManager.ChangeFaceMaskModel(characterModel, null);
                if (slotHash == 400) COOPManager.ChangeBackpackModel(characterModel, null);
                if (slotHash == 500) COOPManager.ChangeHeadsetModel(characterModel, null);
                return;
            }

            string slotName = null;
            if (slotHash == CharacterEquipmentController.armorHash) slotName = "armorSlot";
            else if (slotHash == CharacterEquipmentController.helmatHash) slotName = "helmatSlot";
            else if (slotHash == CharacterEquipmentController.faceMaskHash) slotName = "faceMaskSlot";
            else if (slotHash == CharacterEquipmentController.backpackHash) slotName = "backpackSlot";
            else if (slotHash == CharacterEquipmentController.headsetHash) slotName = "headsetSlot";
            else
            {
                if (!string.IsNullOrEmpty(itemId) && int.TryParse(itemId, out var ids))
                {
                    var item = await COOPManager.GetItemAsync(ids);
                    if (item == null)
                    {
                        Debug.LogWarning($"Failed to get item: ItemId={itemId}, slot {slotHash} not updated");
                    }
                    if (slotHash == 100) COOPManager.ChangeArmorModel(characterModel, item);
                    if (slotHash == 200) COOPManager.ChangeHelmatModel(characterModel, item);
                    if (slotHash == 300) COOPManager.ChangeFaceMaskModel(characterModel, item);
                    if (slotHash == 400) COOPManager.ChangeBackpackModel(characterModel, item);
                    if (slotHash == 500) COOPManager.ChangeHeadsetModel(characterModel, item);
                }
                return;
            }

            try
            {
                if (int.TryParse(itemId, out var ids))
                {
                    var item = await COOPManager.GetItemAsync(ids);
                    if (item != null)
                    {
                        if (slotName == "armorSlot") COOPManager.ChangeArmorModel(characterModel, item);
                        if (slotName == "helmatSlot") COOPManager.ChangeHelmatModel(characterModel, item);
                        if (slotName == "faceMaskSlot") COOPManager.ChangeFaceMaskModel(characterModel, item);
                        if (slotName == "backpackSlot") COOPManager.ChangeBackpackModel(characterModel, item);
                        if (slotName == "headsetSlot") COOPManager.ChangeHeadsetModel(characterModel, item);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Update equipment failed (Client): {playerId}, SlotHash={slotHash}, ItemId={itemId}, error: {ex.Message}");
            }
        }

        private static void SafeKillItemAgent(ItemStatsSystem.Item item)
        {
            if (item == null) return;
            try
            {
                var ag = item.ActiveAgent;
                if (ag != null && ag.gameObject != null)
                    UnityEngine.Object.Destroy(ag.gameObject);
            }
            catch { }

            try { item.Detach(); } catch { }
        }

        private static void ClearWeaponSlot(CharacterModel model, HandheldSocketTypes socket)
        {
            COOPManager.ChangeWeaponModel(model, null, socket);
        }

        private static HandheldSocketTypes ResolveSocketOrDefault(int slotHash)
        {
            var socket = (HandheldSocketTypes)slotHash;
            if (socket != HandheldSocketTypes.normalHandheld &&
                socket != HandheldSocketTypes.meleeWeapon &&
                socket != HandheldSocketTypes.leftHandSocket)
            {
                socket = HandheldSocketTypes.normalHandheld;
            }
            return socket;
        }

        private const float WeaponApplyDebounce = 0.20f;

        private async UniTask ApplyWeaponUpdate(NetPeer peer, int slotHash, string itemId)
        {
            if (!remoteCharacters.TryGetValue(peer, out var remoteObj) || remoteObj == null) return;

            var cm = remoteObj.GetComponent<CharacterMainControl>();
            var model = cm ? cm.characterModel : null;
            if (model == null) return;

            string key = $"{peer?.Id ?? -1}:{slotHash}";
            string want = itemId ?? string.Empty;
            if (_lastWeaponAppliedByPeer.TryGetValue(key, out var last) && last == want)
            {
                return;
            }
            if (_lastWeaponAppliedTimeByPeer.TryGetValue(key, out var ts))
            {
                if (Time.time - ts < WeaponApplyDebounce && last == want)
                    return;
            }
            _lastWeaponAppliedByPeer[key] = want;
            _lastWeaponAppliedTimeByPeer[key] = Time.time;

            var socket = ResolveSocketOrDefault(slotHash);

            try
            {
                if (!string.IsNullOrEmpty(itemId) && int.TryParse(itemId, out var typeId))
                {
                    var item = await COOPManager.GetItemAsync(typeId);
                    if (item != null)
                    {
                        SafeKillItemAgent(item);
                        ClearWeaponSlot(model, socket);
                        await UniTask.NextFrame();
                        COOPManager.ChangeWeaponModel(model, item, socket);

                        try
                        {
                            await UniTask.NextFrame();
                            var gun = model ? model.GetComponentInChildren<ItemAgent_Gun>(true) : null;
                            Transform mz = (gun && gun.muzzle) ? gun.muzzle : null;
                            if (!mz && model)
                            {
                                var t = model.transform;
                                mz = t.Find("Muzzle") ??
                                     (model.RightHandSocket ? model.RightHandSocket.Find("Muzzle") : null) ??
                                     (model.LefthandSocket ? model.LefthandSocket.Find("Muzzle") : null) ??
                                     (model.MeleeWeaponSocket ? model.MeleeWeaponSocket.Find("Muzzle") : null);
                            }

                            if (playerStatuses.TryGetValue(peer, out var ps) && ps != null && !string.IsNullOrEmpty(ps.EndPoint) && gun)
                            {
                                _gunCacheByShooter[ps.EndPoint] = (gun, mz);
                            }
                        }
                        catch { }

                        var gunSetting = item.GetComponent<ItemSetting_Gun>();
                        var pfb = (gunSetting && gunSetting.bulletPfb)
                                ? gunSetting.bulletPfb
                                : Duckov.Utilities.GameplayDataSettings.Prefabs.DefaultBullet;
                        _projCacheByWeaponType[typeId] = pfb;
                        _muzzleFxCacheByWeaponType[typeId] = gunSetting ? gunSetting.muzzleFxPfb : null;
                    }
                }
                else
                {
                    ClearWeaponSlot(model, socket);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Update weapon failed (Host): {peer?.EndPoint}, Slot={socket}, ItemId={itemId}, error: {ex.Message}");
            }
        }

        private async UniTask ApplyWeaponUpdate_Client(string playerId, int slotHash, string itemId)
        {
            if (IsSelfId(playerId)) return;

            if (!clientRemoteCharacters.TryGetValue(playerId, out var remoteObj) || remoteObj == null) return;
            var cm = remoteObj.GetComponent<CharacterMainControl>();
            var model = cm ? cm.characterModel : null;
            if (model == null) return;

            string key = $"{playerId}:{slotHash}";
            string want = itemId ?? string.Empty;
            if (_lastWeaponAppliedByPlayer.TryGetValue(key, out var last) && last == want)
            {
                return;
            }
            if (_lastWeaponAppliedTimeByPlayer.TryGetValue(key, out var ts))
            {
                if (Time.time - ts < WeaponApplyDebounce && last == want)
                    return;
            }
            _lastWeaponAppliedByPlayer[key] = want;
            _lastWeaponAppliedTimeByPlayer[key] = Time.time;

            var socket = ResolveSocketOrDefault(slotHash);

            try
            {
                if (!string.IsNullOrEmpty(itemId) && int.TryParse(itemId, out var typeId))
                {
                    var item = await COOPManager.GetItemAsync(typeId);
                    if (item != null)
                    {
                        SafeKillItemAgent(item);
                        ClearWeaponSlot(model, socket);
                        await UniTask.NextFrame();
                        COOPManager.ChangeWeaponModel(model, item, socket);

                        var gunSetting = item.GetComponent<ItemSetting_Gun>();
                        var pfb = (gunSetting && gunSetting.bulletPfb)
                                ? gunSetting.bulletPfb
                                : Duckov.Utilities.GameplayDataSettings.Prefabs.DefaultBullet;
                        _projCacheByWeaponType[typeId] = pfb;
                        _muzzleFxCacheByWeaponType[typeId] = gunSetting ? gunSetting.muzzleFxPfb : null;
                    }
                }
                else
                {
                    ClearWeaponSlot(model, socket);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Update weapon failed (Client): {playerId}, Slot={socket}, ItemId={itemId}, error: {ex.Message}");
            }
        }
    }
}
