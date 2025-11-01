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
using Duckov.Buffs;
using HarmonyLib;
using ItemStatsSystem;
using ItemStatsSystem.Items;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Assertions.Must;

namespace DuckovCoopMod
{
    /// <summary>
    /// Utility functions for item/model operations used by co-op flows.
    /// Provides async item spawn helpers and equipment model binding on characters.
    /// </summary>
    public class COOPManager
    {
        /// <summary>
        /// Instantiate an item prefab by TypeID, asynchronously.
        /// </summary>
        public static async Task<Item> GetItemAsync(int itemId)
        {
            // Debug.Log(itemId);
            return await ItemAssetsCollection.InstantiateAsync(itemId);
        }

        /// <summary>
        /// Swap the character's armor visual model to the specified item (or clear when null).
        /// Updates the underlying equipment slot content and rebinds the spawned ItemAgent.
        /// </summary>
        public static void ChangeArmorModel(CharacterModel characterModel, Item item)
        {
            if(item != null)
            {
                Slot slot = characterModel.characterMainControl.CharacterItem.Slots["Armor"];
                Traverse.Create(slot).Field<Item>("content").Value = item;
            }

            if (item == null)
            {
                Transform socket = characterModel.ArmorSocket;
                for (int i = socket.childCount - 1; i >= 0; i--)
                {
                    UnityEngine.Object.Destroy(socket.GetChild(i).gameObject);
                }
                return;
            }
            global::UnityEngine.Transform faceMaskSocket = characterModel.ArmorSocket;
            global::ItemStatsSystem.ItemAgent itemAgent = item.AgentUtilities.CreateAgent(global::CharacterEquipmentController.equipmentModelHash, global::ItemStatsSystem.ItemAgent.AgentTypes.equipment);
            if (itemAgent == null)
            {
                global::UnityEngine.Debug.LogError("Failed to create equipment agent for item: " + item.gameObject.name);
                return;
            }
            if (itemAgent != null)
            {
                itemAgent.transform.SetParent(faceMaskSocket, false);
                itemAgent.transform.localRotation = global::UnityEngine.Quaternion.identity;
                itemAgent.transform.localPosition = global::UnityEngine.Vector3.zero;
            }
        }


        public static void ChangeHelmatModel(CharacterModel characterModel, Item item)
        {
            if (item != null)
            {
                Slot slot = characterModel.characterMainControl.CharacterItem.Slots["Helmat"];
                Traverse.Create(slot).Field<Item>("content").Value = item;
            }
                     
            if (item == null)
            {
                Transform socket = characterModel.HelmatSocket;
                for (int i = socket.childCount - 1; i >= 0; i--)
                {
                    UnityEngine.Object.Destroy(socket.GetChild(i).gameObject);
                }
                characterModel.CustomFace.hairSocket.gameObject.SetActive(true);
                characterModel.CustomFace.mouthPart.socket.gameObject.SetActive(true);
                return;
            }
            characterModel.CustomFace.hairSocket.gameObject.SetActive(false);
            characterModel.CustomFace.mouthPart.socket.gameObject.SetActive(false);
            global::UnityEngine.Transform faceMaskSocket = characterModel.HelmatSocket;
            global::ItemStatsSystem.ItemAgent itemAgent = item.AgentUtilities.CreateAgent(global::CharacterEquipmentController.equipmentModelHash, global::ItemStatsSystem.ItemAgent.AgentTypes.equipment);
            if (itemAgent == null)
            {
                global::UnityEngine.Debug.LogError("Failed to create equipment agent for item: " + item.gameObject.name);
                return;
            }
            if (itemAgent != null)
            {
                itemAgent.transform.SetParent(faceMaskSocket, false);
                itemAgent.transform.localRotation = global::UnityEngine.Quaternion.identity;
                itemAgent.transform.localPosition = global::UnityEngine.Vector3.zero;
            }
        }
        public static void ChangeHeadsetModel(CharacterModel characterModel, Item item)
        {
            if(item != null)
            {
                Slot slot = characterModel.characterMainControl.CharacterItem.Slots["Headset"];
                Traverse.Create(slot).Field<Item>("content").Value = item;
            }
              
            if (item == null)
            {
                Transform socket = characterModel.HelmatSocket;
                for (int i = socket.childCount - 1; i >= 0; i--)
                {
                    UnityEngine.Object.Destroy(socket.GetChild(i).gameObject);
                }
                characterModel.CustomFace.hairSocket.gameObject.SetActive(true);
                characterModel.CustomFace.mouthPart.socket.gameObject.SetActive(true);
                return;
            }
            characterModel.CustomFace.hairSocket.gameObject.SetActive(false);
            characterModel.CustomFace.mouthPart.socket.gameObject.SetActive(false);
            global::UnityEngine.Transform faceMaskSocket = characterModel.HelmatSocket;
            global::ItemStatsSystem.ItemAgent itemAgent = item.AgentUtilities.CreateAgent(global::CharacterEquipmentController.equipmentModelHash, global::ItemStatsSystem.ItemAgent.AgentTypes.equipment);
            if (itemAgent == null)
            {
                global::UnityEngine.Debug.LogError("Failed to create equipment agent for item: " + item.gameObject.name);
                return;
            }
            if (itemAgent != null)
            {
                itemAgent.transform.SetParent(faceMaskSocket, false);
                itemAgent.transform.localRotation = global::UnityEngine.Quaternion.identity;
                itemAgent.transform.localPosition = global::UnityEngine.Vector3.zero;
            }
        }

        public static void ChangeBackpackModel(CharacterModel characterModel, Item item)
        {
            if (item != null)
            {
                Slot slot = characterModel.characterMainControl.CharacterItem.Slots["Backpack"];
                Traverse.Create(slot).Field<Item>("content").Value = item;
            }         
       
            if (item == null)
            {
                Transform socket = characterModel.BackpackSocket;
                for (int i = socket.childCount - 1; i >= 0; i--)
                {
                    UnityEngine.Object.Destroy(socket.GetChild(i).gameObject);
                }
                return;
            }
            global::UnityEngine.Transform faceMaskSocket = characterModel.BackpackSocket;
            global::ItemStatsSystem.ItemAgent itemAgent = item.AgentUtilities.CreateAgent(global::CharacterEquipmentController.equipmentModelHash, global::ItemStatsSystem.ItemAgent.AgentTypes.equipment);
            if (itemAgent == null)
            {
                global::UnityEngine.Debug.LogError("Failed to create equipment agent for item: " + item.gameObject.name);
                return;
            }
            if (itemAgent != null)
            {
                itemAgent.transform.SetParent(faceMaskSocket, false);
                itemAgent.transform.localRotation = global::UnityEngine.Quaternion.identity;
                itemAgent.transform.localPosition = global::UnityEngine.Vector3.zero;
            }
        }


        public static void ChangeFaceMaskModel(CharacterModel characterModel, Item item)
        {
            if(item != null)
            {
                Slot slot = characterModel.characterMainControl.CharacterItem.Slots["FaceMask"];
                Traverse.Create(slot).Field<Item>("content").Value = item;
            }
                     
            if (item == null)
            {
                Transform socket = characterModel.FaceMaskSocket;
                for (int i = socket.childCount - 1; i >= 0; i--)
                {
                    UnityEngine.Object.Destroy(socket.GetChild(i).gameObject);
                }
                return;
            }
            global::UnityEngine.Transform faceMaskSocket = characterModel.FaceMaskSocket;
            global::ItemStatsSystem.ItemAgent itemAgent = item.AgentUtilities.CreateAgent(global::CharacterEquipmentController.equipmentModelHash, global::ItemStatsSystem.ItemAgent.AgentTypes.equipment);
            if (itemAgent == null)
            {
                global::UnityEngine.Debug.LogError("Failed to create equipment agent for item: " + item.gameObject.name);
                return;
            }
            if (itemAgent != null)
            {
                itemAgent.transform.SetParent(faceMaskSocket, false);
                itemAgent.transform.localRotation = global::UnityEngine.Quaternion.identity;
                itemAgent.transform.localPosition = global::UnityEngine.Vector3.zero;
            }
        }

        //public static void ChangeWeaponModel(CharacterModel characterModel, Item item, HandheldSocketTypes handheldSocket)
        //{
        // if (item == null)
        // {
        // if(handheldSocket == HandheldSocketTypes.normalHandheld)
        // {
        // Transform socket = characterModel.RightHandSocket;
        // for (int i = socket.childCount - 1; i >= 0; i--)
        // {
        // UnityEngine.Object.Destroy(socket.GetChild(i).gameObject);
        // }
        // }
        // if (handheldSocket == HandheldSocketTypes.meleeWeapon)
        // {
        // Transform socket = characterModel.MeleeWeaponSocket;
        // for (int i = socket.childCount - 1; i >= 0; i--)
        // {
        // UnityEngine.Object.Destroy(socket.GetChild(i).gameObject);
        // }
        // }
        // if (handheldSocket == HandheldSocketTypes.leftHandSocket)
        // {
        // Transform socket = characterModel.LefthandSocket;
        // for (int i = socket.childCount - 1; i >= 0; i--)
        // {
        // UnityEngine.Object.Destroy(socket.GetChild(i).gameObject);
        // }
        // }

        // return;
        // }
        // Transform transform = null;
        // if (handheldSocket == HandheldSocketTypes.normalHandheld)
        // {
        // transform = characterModel.RightHandSocket;
        // }
        // if (handheldSocket == HandheldSocketTypes.meleeWeapon)
        // {
        // transform = characterModel.MeleeWeaponSocket;
        // }
        // if (handheldSocket == HandheldSocketTypes.leftHandSocket)
        // {
        // transform = characterModel.LefthandSocket;
        // }

        // ItemAgent itemAgent = item.CreateHandheldAgent();

        // var currentHoldItemAgent = (itemAgent as global::DuckovItemAgent);
        // if (currentHoldItemAgent == null)
        // {
        // global::UnityEngine.Object.Destroy(itemAgent.gameObject);
        // return;
        // }

        // currentHoldItemAgent.transform.SetParent(transform, false);
        // currentHoldItemAgent.transform.localPosition = global::UnityEngine.Vector3.zero;
        // currentHoldItemAgent.transform.localRotation = global::UnityEngine.Quaternion.identity;

        //}


        public static async Task<Grenade> GetGrenadePrefabByItemIdAsync(int itemId)
        {
            Item item = null;
            try
            {
                item = await GetItemAsync(itemId);
                if (item == null) return null;
                var skill = item.GetComponent<Skill_Grenade>();
                return skill != null ? skill.grenadePfb : null;
            }
            finally
            {
                if (item != null && item.gameObject)
                    UnityEngine.Object.Destroy(item.gameObject);
            }
        }

        public static Grenade GetGrenadePrefabByItemIdBlocking(int itemId)
        {
            return GetGrenadePrefabByItemIdAsync(itemId).GetAwaiter().GetResult();
        }

        public static void EnsureRemotePlayersHaveHealthBar()
        {
            foreach (var kv in ModBehaviour.Instance.remoteCharacters)
            {
                var go = kv.Value;
                if (!go) continue;
                if (!go.GetComponent<AutoRequestHealthBar>())
                    go.AddComponent<AutoRequestHealthBar>(); // Start() 
            }
        }

        public static async UniTask<Buff> ResolveBuffAsync(int weaponTypeId, int buffId)
        {
            // 1) id / 
            if (weaponTypeId > 0)
            {
                try
                {
                    var item = await ItemAssetsCollection.InstantiateAsync(weaponTypeId);
                    var gunAgent = item?.AgentUtilities?.ActiveAgent as ItemAgent_Gun;
                    var prefab = gunAgent?.GunItemSetting?.buff;
                    if (prefab != null) return prefab;
                }
                catch { }
            }

            // 2) Buff id / Buff 
            try
            {
                foreach (var b in Resources.FindObjectsOfTypeAll<Buff>())
                {
                    if (b && b.ID == buffId) return b;
                }
            }
            catch { }

            return null;
        }

        public static void ChangeWeaponModel(CharacterModel characterModel, Item item, HandheldSocketTypes handheldSocket)
        {
            if (characterModel == null) return;

            // / / 
            var tSocket = ResolveHandheldSocket(characterModel, handheldSocket);
            if (tSocket == null) return;

            // socket 
            ClearChildren(tSocket);

            if (item == null) return;

            ItemAgent itemAgent = null;
            try { itemAgent = item.ActiveAgent; } catch { }

            if (itemAgent == null)
            {
                try { itemAgent = item.CreateHandheldAgent(); }
                catch (Exception e)
                {
                    Debug.Log($"[COOP] CreateHandheldAgent Failed:{e.Message}");
                    return;
                }
            }

            if (itemAgent == null) return;

            // DuckovItemAgent 
            var duck = itemAgent.GetComponent<DuckovItemAgent>();
            if (duck != null)
                duck.handheldSocket = handheldSocket;

            // socket 
            var tr = itemAgent.transform;
            tr.SetParent(tSocket, true);
            tr.localPosition = Vector3.zero;
            tr.localRotation = Quaternion.identity;
            tr.localScale = Vector3.one;

            var go = itemAgent.gameObject;
            if (go && !go.activeSelf) go.SetActive(true);
        }

        //public static void ChangeWeaponModel(CharacterModel characterModel, Item item, HandheldSocketTypes handheldSocket)
        //{
        // if (!characterModel) return;

        // // 
        // var socket = ResolveHandheldSocket(characterModel, handheldSocket);
        // if (!socket)
        // {
        // Debug.LogWarning($"[COOP] ChangeWeaponModel: socket '{handheldSocket}' not found on model '{characterModel?.name}'.");
        // return;
        // }

        // // item==null 
        // if (item == null)
        // {
        // ClearChildren(socket);
        // return;
        // }

        // // Agent null 
        // ItemAgent itemAgent = null;
        // try { itemAgent = item.CreateHandheldAgent(); }
        // catch (Exception e)
        // {
        // Debug.LogError($"[COOP] CreateHandheldAgent failed for item '{item?.name}': {e}");
        // return;
        // }
        // if (!itemAgent) return; // Destroy(null.gameObject) 

        // // DuckovItemAgent 
        // var duck = itemAgent as DuckovItemAgent ?? itemAgent.GetComponent<DuckovItemAgent>();
        // if (!duck)
        // {
        // if (itemAgent && itemAgent.gameObject) UnityEngine.Object.Destroy(itemAgent.gameObject);
        // Debug.LogWarning($"[COOP] Handheld agent isn't DuckovItemAgent: {item?.name}");
        // return;
        // }

        // // 
        // try { duck.handheldSocket = handheldSocket; } catch { }


        // duck.transform.SetParent(socket, false);
        // duck.transform.localPosition = Vector3.zero;
        // duck.transform.localRotation = Quaternion.identity;

        // characterModel.characterMainControl.ChangeHoldItem(item);
        //}

        static Transform ResolveHandheldSocket(CharacterModel model, HandheldSocketTypes socket)
        {
            switch (socket)
            {
                case HandheldSocketTypes.meleeWeapon:
                    return model.MeleeWeaponSocket ? model.MeleeWeaponSocket
                         : (model.RightHandSocket ? model.RightHandSocket : model.LefthandSocket);
                case HandheldSocketTypes.leftHandSocket:
                    return model.LefthandSocket ? model.LefthandSocket
                         : (model.RightHandSocket ? model.RightHandSocket : model.MeleeWeaponSocket);
                case HandheldSocketTypes.normalHandheld:
                default:
                    return model.RightHandSocket ? model.RightHandSocket
                         : (model.MeleeWeaponSocket ? model.MeleeWeaponSocket : model.LefthandSocket);
            }
        }

        static void ClearChildren(Transform t)
        {
            if (!t) return;
            for (int i = t.childCount - 1; i >= 0; --i)
            {
                var c = t.GetChild(i);
                if (c) UnityEngine.Object.Destroy(c.gameObject);
            }
        }











    }

}



