using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using UnityEngine;

namespace DuckovCoopMod
{
    public partial class ModBehaviour
    {
        public void WriteItemSnapshot(NetDataWriter w, Item item)
        {
            w.Put(item.TypeID);
            w.Put(item.StackCount);
            w.Put(item.Durability);
            w.Put(item.DurabilityLoss);
            w.Put(item.Inspected);

            var slots = item.Slots;
            if (slots != null && slots.list != null)
            {
                int filled = 0;
                foreach (var s in slots.list) if (s != null && s.Content != null) filled++;
                w.Put((ushort)filled);
                foreach (var s in slots.list)
                {
                    if (s == null || s.Content == null) continue;
                    w.Put(s.Key ?? string.Empty);
                    WriteItemSnapshot(w, s.Content);
                }
            }
            else w.Put((ushort)0);

            var invItems = TryGetInventoryItems(item.Inventory);
            if (invItems != null)
            {
                var valid = new List<Item>(invItems.Count);
                foreach (var c in invItems) if (c != null) valid.Add(c);
                w.Put((ushort)valid.Count);
                foreach (var child in valid) WriteItemSnapshot(w, child);
            }
            else w.Put((ushort)0);
        }

        static ItemSnapshot ReadItemSnapshot(NetPacketReader r)
        {
            ItemSnapshot s;
            s.typeId = r.GetInt();
            s.stack = r.GetInt();
            s.durability = r.GetFloat();
            s.durabilityLoss = r.GetFloat();
            s.inspected = r.GetBool();
            s.slots = new List<(string, ItemSnapshot)>();
            s.inventory = new List<ItemSnapshot>();

            int slotsCount = r.GetUShort();
            for (int i = 0; i < slotsCount; i++)
            {
                string key = r.GetString();
                var child = ReadItemSnapshot(r);
                s.slots.Add((key, child));
            }
            int invCount = r.GetUShort();
            for (int i = 0; i < invCount; i++)
            {
                var child = ReadItemSnapshot(r);
                s.inventory.Add(child);
            }
            return s;
        }

        static Item BuildItemFromSnapshot(ItemSnapshot s)
        {
            Item item = null;
            try { item = COOPManager.GetItemAsync(s.typeId).Result; }
            catch (Exception e)
            {
                Debug.LogError($"[ITEM] Instantiate failed typeId={s.typeId}, err={e}");
                return null;
            }
            if (item == null) return null;
            ApplySnapshotToItem(item, s);
            return item;
        }

        static void ApplySnapshotToItem(Item item, ItemSnapshot s)
        {
            try
            {
                if (item.Stackable)
                {
                    int target = s.stack;
                    if (target < 1) target = 1;
                    try { target = Mathf.Clamp(target, 1, item.MaxStackCount); } catch { }
                    item.StackCount = target;
                }

                item.Durability = s.durability;
                item.DurabilityLoss = s.durabilityLoss;
                item.Inspected = s.inspected;

                if (s.slots != null && s.slots.Count > 0 && item.Slots != null)
                {
                    foreach (var (key, childSnap) in s.slots)
                    {
                        if (string.IsNullOrEmpty(key)) continue;
                        var slot = item.Slots.GetSlot(key);
                        if (slot == null) { Debug.LogWarning($"[ITEM] Slot not found key={key} on {item.DisplayName}"); continue; }
                        var child = BuildItemFromSnapshot(childSnap);
                        if (child == null) continue;
                        if (!slot.Plug(child, out _)) TryAddToInventory(item.Inventory, child);
                    }
                }

                if (s.inventory != null && s.inventory.Count > 0)
                {
                    foreach (var childSnap in s.inventory)
                    {
                        var child = BuildItemFromSnapshot(childSnap);
                        if (child == null) continue;
                        TryAddToInventory(item.Inventory, child);
                    }
                }
            }
            catch (Exception e) { Debug.LogError($"[ITEM] ApplySnapshot error: {e}"); }
        }

        static List<Item> TryGetInventoryItems(Inventory inv)
        {
            if (inv == null) return null;
            var list = inv.Content; return list;
        }

        static bool TryAddToInventory(Inventory inv, Item child)
        {
            if (inv == null || child == null) return false;
            try { return ItemUtilities.AddAndMerge(inv, child, 0); }
            catch (Exception e)
            {
                Debug.LogWarning($"[ITEM] Inventory.Add* Failed: {e.Message}");
                try { child.Detach(); return inv.AddItem(child); } catch { }
            }
            return false;
        }

        // ==== Remaining loot operations (requests/handlers/state) ====
        // The following methods were migrated from Mod.cs to keep loot logic together.

        private void PutLootId(NetDataWriter w, Inventory inv)
        {
            int scene = SceneManager.GetActiveScene().buildIndex;
            int posKey = -1; int instanceId = -1;
            var dict = InteractableLootbox.Inventories;
            if (inv != null && dict != null)
            {
                foreach (var kv in dict) { if (kv.Value == inv) { posKey = kv.Key; break; } }
            }
            if (inv != null && (posKey < 0 || instanceId < 0))
            {
                var boxes = GameObject.FindObjectsOfType<InteractableLootbox>();
                foreach (var b in boxes)
                { if (!b) continue; if (b.Inventory == inv) { posKey = ComputeLootKey(b.transform); instanceId = b.GetInstanceID(); break; } }
            }
            int lootUid = -1;
            if (IsServer) { foreach (var kv in _srvLootByUid) if (kv.Value == inv) { lootUid = kv.Key; break; } }
            else { foreach (var kv in _cliLootByUid) if (kv.Value == inv) { lootUid = kv.Key; break; } }
            w.Put(scene); w.Put(posKey); w.Put(instanceId); w.Put(lootUid);
        }

        private bool TryResolveLootById(int scene, int posKey, int iid, out Inventory inv)
        {
            inv = null; if (posKey != 0 && TryGetLootInvByKeyEverywhere(posKey, out inv)) return true;
            if (iid != 0)
            {
                try
                {
                    var all = UnityEngine.Object.FindObjectsOfType<InteractableLootbox>(true);
                    foreach (var b in all)
                    { if (!b) continue; if (b.GetInstanceID() == iid && (scene < 0 || b.gameObject.scene.buildIndex == scene)) { inv = b.Inventory; if (inv) return true; } }
                }
                catch { }
            }
            return false;
        }

        public void Client_RequestLootState(Inventory lootInv)
        {
            if (!networkStarted || IsServer || connectedPeer == null || lootInv == null) return;
            if (LootboxDetectUtil.IsPrivateInventory(lootInv)) return;
            var w = writer; w.Reset(); w.Put((byte)Op.LOOT_REQ_OPEN); PutLootId(w, lootInv); byte reqVer = 1; w.Put(reqVer);
            Vector3 pos; if (!TryGetLootboxWorldPos(lootInv, out pos)) pos = Vector3.zero; w.PutV3cm(pos); TransportSendToServer(w, true);
        }

        public void Server_SendLootboxState(NetPeer toPeer, Inventory inv)
        {
            if (toPeer == null && Server_IsLootMuted(inv)) return; if (!IsServer || inv == null) return; if (!LootboxDetectUtil.IsLootboxInventory(inv) || LootboxDetectUtil.IsPrivateInventory(inv)) return;
            var w = new NetDataWriter(); w.Put((byte)Op.LOOT_STATE); PutLootId(w, inv); int capacity = inv.Capacity; w.Put(capacity);
            int count = 0; var content = inv.Content; for (int i = 0; i < content.Count; ++i) if (content[i] != null) count++; w.Put(count);
            for (int i = 0; i < content.Count; ++i) { var it = content[i]; if (it == null) continue; w.Put(i); WriteItemSnapshot(w, it); }
            if (toPeer != null) TransportSend(toPeer, w, true); else BroadcastReliable(w);
        }

        public void Client_ApplyLootboxState(NetPacketReader r)
        {
            int scene = r.GetInt(); int posKey = r.GetInt(); int iid = r.GetInt(); int lootUid = r.GetInt(); int capacity = r.GetInt(); int count = r.GetInt();
            Inventory inv = null; if (lootUid >= 0 && _cliLootByUid.TryGetValue(lootUid, out var byUid) && byUid) inv = byUid;
            if (inv == null && (!TryResolveLootById(scene, posKey, iid, out inv) || inv == null))
            {
                if (LootboxDetectUtil.IsPrivateInventory(inv)) return; var list = new List<(int pos, ItemSnapshot snap)>(count);
                for (int k = 0; k < count; ++k) { int p = r.GetInt(); var snap = ReadItemSnapshot(r); list.Add((p, snap)); }
                if (lootUid >= 0) _pendingLootStatesByUid[lootUid] = (capacity, list); return;
            }
            if (LootboxDetectUtil.IsPrivateInventory(inv)) return; capacity = Mathf.Clamp(capacity, 1, 128);
            _applyingLootState = true; try
            {
                inv.SetCapacity(capacity); inv.Loading = false; for (int i = inv.Content.Count - 1; i >= 0; --i) { Item removed; inv.RemoveAt(i, out removed); if (removed) UnityEngine.Object.Destroy(removed.gameObject); }
                for (int k = 0; k < count; ++k) { int pos = r.GetInt(); var snap = ReadItemSnapshot(r); var item = BuildItemFromSnapshot(snap); if (item == null) continue; inv.AddAt(item, pos); }
            }
            finally { _applyingLootState = false; }
            try { var lv = Duckov.UI.LootView.Instance; if (lv && lv.open && ReferenceEquals(lv.TargetInventory, inv)) { AccessTools.Method(typeof(Duckov.UI.LootView), "RefreshDetails")?.Invoke(lv, null); AccessTools.Method(typeof(Duckov.UI.LootView), "RefreshPickAllButton")?.Invoke(lv, null); AccessTools.Method(typeof(Duckov.UI.LootView), "RefreshCapacityText")?.Invoke(lv, null); } }
            catch { }
        }

        public void Client_SendLootPutRequest(Inventory lootInv, Item item, int preferPos)
        {
            if (!networkStarted || IsServer || connectedPeer == null || lootInv == null || item == null) return; if (LootboxDetectUtil.IsPrivateInventory(lootInv)) return;
            foreach (var kv in _cliPendingPut) { var pending = kv.Value; if (pending && ReferenceEquals(pending, item)) { Debug.Log($"[LOOT] Duplicate PUT suppressed for item: {item.DisplayName}"); return; } }
            uint token = _nextLootToken++; _cliPendingPut[token] = item; var w = writer; w.Reset(); w.Put((byte)Op.LOOT_REQ_PUT); PutLootId(w, lootInv); w.Put(preferPos); w.Put(token); WriteItemSnapshot(w, item); TransportSendToServer(w, true);
        }

        public void Client_SendLootTakeRequest(Inventory lootInv, int position) { Client_SendLootTakeRequest(lootInv, position, null, -1, null); }

        public uint Client_SendLootTakeRequest(Inventory lootInv, int position, Inventory destInv, int destPos, Slot destSlot)
        {
            if (!networkStarted || IsServer || connectedPeer == null || lootInv == null) return 0; if (LootboxDetectUtil.IsPrivateInventory(lootInv)) return 0; if (destInv != null && LootboxDetectUtil.IsLootboxInventory(destInv)) destInv = null;
            uint token = _nextLootToken++; if (destInv != null || destSlot != null) _cliPendingTake[token] = new PendingTakeDest { inv = destInv, pos = destPos, slot = destSlot, srcLoot = lootInv, srcPos = position };
            var w = writer; w.Reset(); w.Put((byte)Op.LOOT_REQ_TAKE); PutLootId(w, lootInv); w.Put(position); w.Put(token); TransportSendToServer(w, true); return token;
        }

        private void Server_HandleLootPutRequest(NetPeer peer, NetPacketReader r)
        {
            int scene = r.GetInt(); int posKey = r.GetInt(); int iid = r.GetInt(); int lootUid = r.GetInt(); int prefer = r.GetInt(); uint token = r.GetUInt();
            ItemSnapshot snap; try { snap = ReadItemSnapshot(r); } catch (DecoderFallbackException ex) { Debug.LogError($"[LOOT][PUT] snapshot decode failed: {ex.Message}"); Server_SendLootDeny(peer, "bad_snapshot"); return; } catch (Exception ex) { Debug.LogError($"[LOOT][PUT] snapshot parse failed: {ex}"); Server_SendLootDeny(peer, "bad_snapshot"); return; }
            Inventory inv = null; if (lootUid >= 0) _srvLootByUid.TryGetValue(lootUid, out inv); if (inv == null && !TryResolveLootById(scene, posKey, iid, out inv)) { Server_SendLootDeny(peer, "no_inv"); return; }
            if (LootboxDetectUtil.IsPrivateInventory(inv)) { Server_SendLootDeny(peer, "no_inv"); return; }
            var item = BuildItemFromSnapshot(snap); if (item == null) { Server_SendLootDeny(peer, "bad_item"); return; }
            _serverApplyingLoot = true; bool ok = false; try { ok = ItemUtilities.AddAndMerge(inv, item, prefer); if (!ok) UnityEngine.Object.Destroy(item.gameObject); } catch (Exception ex) { Debug.LogError($"[LOOT][PUT] AddAndMerge exception: {ex}"); ok = false; } finally { _serverApplyingLoot = false; }
            if (!ok) { Server_SendLootDeny(peer, "add_fail"); return; }
            var ack = new NetDataWriter(); ack.Put((byte)Op.LOOT_PUT_OK); ack.Put(token); TransportSend(peer, ack, true); Server_SendLootboxState(null, inv);
        }

        private void Server_HandleLootTakeRequest(NetPeer peer, NetPacketReader r)
        {
            int scene = r.GetInt(); int posKey = r.GetInt(); int iid = r.GetInt(); int lootUid = r.GetInt(); int position = r.GetInt(); uint token = r.GetUInt();
            Inventory inv = null; if (lootUid >= 0) _srvLootByUid.TryGetValue(lootUid, out inv); if (inv == null && !TryResolveLootById(scene, posKey, iid, out inv)) { Server_SendLootDeny(peer, "no_inv"); return; }
            if (LootboxDetectUtil.IsPrivateInventory(inv)) { Server_SendLootDeny(peer, "no_inv"); return; }
            _serverApplyingLoot = true; bool ok = false; Item removed = null; try { if (position >= 0 && position < inv.Capacity) { try { ok = inv.RemoveAt(position, out removed); } catch (ArgumentOutOfRangeException) { ok = false; removed = null; } } } finally { _serverApplyingLoot = false; }
            if (!ok || removed == null) { Server_SendLootDeny(peer, "rm_fail"); Server_SendLootboxState(peer, inv); return; }
            var wCli = new NetDataWriter(); wCli.Put((byte)Op.LOOT_TAKE_OK); wCli.Put(token); WriteItemSnapshot(wCli, removed); TransportSend(peer, wCli, true); try { UnityEngine.Object.Destroy(removed.gameObject); } catch { } Server_SendLootboxState(null, inv);
        }

        private void Server_SendLootDeny(NetPeer peer, string reason)
        { var w = new NetDataWriter(); w.Put((byte)Op.LOOT_DENY); w.Put(reason ?? ""); TransportSend(peer, w, true); }

        private void Client_OnLootPutOk(NetPacketReader r)
        {
            uint token = r.GetUInt(); if (_cliPendingSlotPlug.TryGetValue(token, out var victim) && victim)
            { try { var srcInv = victim.InInventory; if (srcInv) { try { srcInv.RemoveItem(victim); } catch { } } UnityEngine.Object.Destroy(victim.gameObject); } catch { } finally { _cliPendingSlotPlug.Remove(token); } return; }
            if (_cliPendingPut.TryGetValue(token, out var localItem) && localItem)
            { _cliPendingPut.Remove(token); if (_cliSwapByVictim.TryGetValue(localItem, out var ctx)) { _cliSwapByVictim.Remove(localItem); try { localItem.Detach(); } catch { } try { UnityEngine.Object.Destroy(localItem.gameObject); } catch { } try { if (ctx.destSlot != null) { if (ctx.destSlot.CanPlug(ctx.newItem)) ctx.destSlot.Plug(ctx.newItem, out var _); } else if (ctx.destInv != null && ctx.destPos >= 0) { ctx.destInv.AddAt(ctx.newItem, ctx.destPos); } } catch { } var toRemove = new List<uint>(); foreach (var kv in _cliPendingPut) if (!kv.Value || ReferenceEquals(kv.Value, localItem)) toRemove.Add(kv.Key); foreach (var k in toRemove) _cliPendingPut.Remove(k); return; } try { localItem.Detach(); } catch { } try { UnityEngine.Object.Destroy(localItem.gameObject); } catch { } var stale = new List<uint>(); foreach (var kv in _cliPendingPut) if (!kv.Value || ReferenceEquals(kv.Value, localItem)) stale.Add(kv.Key); foreach (var k in stale) _cliPendingPut.Remove(k); }
        }

        private void Client_OnLootTakeOk(NetPacketReader r)
        {
            uint token = r.GetUInt(); var snap = ReadItemSnapshot(r); var newItem = BuildItemFromSnapshot(snap); if (newItem == null) return;
            PendingTakeDest dest; if (_cliPendingTake.TryGetValue(token, out dest)) _cliPendingTake.Remove(token); else dest = default;
            void PutBackToSource_NoTrack(ItemStatsSystem.Item item, PendingTakeDest srcInfo)
            { var loot = srcInfo.srcLoot != null ? srcInfo.srcLoot : (Duckov.UI.LootView.Instance ? Duckov.UI.LootView.Instance.TargetInventory : null); int preferPos = srcInfo.srcPos >= 0 ? srcInfo.srcPos : -1; try { if (networkStarted && !IsServer && connectedPeer != null && loot != null && item != null) { var w = writer; w.Reset(); w.Put((byte)Op.LOOT_REQ_PUT); PutLootId(w, loot); w.Put(preferPos); w.Put((uint)0); WriteItemSnapshot(w, item); TransportSendToServer(w, true); } } catch { } try { item.Detach(); } catch { } try { UnityEngine.Object.Destroy(item.gameObject); } catch { } try { var lv = Duckov.UI.LootView.Instance; var inv = lv ? lv.TargetInventory : null; if (inv) Client_RequestLootState(inv); } catch { } }
            if (_cliPendingReorder.TryGetValue(token, out var reo)) { _cliPendingReorder.Remove(token); Client_SendLootPutRequest(reo.inv, newItem, reo.pos); return; }
            if (dest.slot != null)
            { ItemStatsSystem.Item victim = null; try { victim = dest.slot.Content; } catch { } if (victim != null) { _cliSwapByVictim[victim] = (newItem, null, -1, dest.slot); var srcLoot = dest.srcLoot ?? (Duckov.UI.LootView.Instance ? Duckov.UI.LootView.Instance.TargetInventory : null); Client_SendLootPutRequest(srcLoot, victim, dest.srcPos); return; } else { try { if (dest.slot.CanPlug(newItem) && dest.slot.Plug(newItem, out var _)) return; } catch { } PutBackToSource_NoTrack(newItem, dest); return; } }
            if (dest.inv != null)
            { if (dest.pos >= 0) { try { var cur = dest.inv.GetItemAt(dest.pos); if (cur != null) { _cliSwapByVictim[cur] = (newItem, dest.inv, dest.pos, null); Client_SendLootPutRequest(dest.inv, cur, -1); return; } else { dest.inv.AddAt(newItem, dest.pos); return; } } catch { } } try { if (!ItemUtilities.AddAndMerge(dest.inv, newItem, 0)) { PutBackToSource_NoTrack(newItem, dest); return; } } catch { PutBackToSource_NoTrack(newItem, dest); return; } return; }
            try { var inv = Duckov.UI.LootView.Instance ? Duckov.UI.LootView.Instance.TargetInventory : null; if (!inv) { PutBackToSource_NoTrack(newItem, dest); return; } if (!ItemUtilities.AddAndMerge(inv, newItem, 0)) { PutBackToSource_NoTrack(newItem, dest); return; } } catch { PutBackToSource_NoTrack(newItem, dest); return; }
        }

        public void Client_SendLootSplitRequest(Inventory lootInv, int srcPos, int count, int preferPos)
        {
            if (!networkStarted || IsServer || connectedPeer == null || lootInv == null) return; if (LootboxDetectUtil.IsPrivateInventory(lootInv)) return; if (count <= 0) return;
            var w = writer; w.Reset(); w.Put((byte)Op.LOOT_REQ_SPLIT); PutLootId(w, lootInv); w.Put(srcPos); w.Put(count); w.Put(preferPos); TransportSendToServer(w, true);
        }

        private void Server_HandleLootSplitRequest(NetPeer peer, NetPacketReader r)
        { int scene = r.GetInt(); int posKey = r.GetInt(); int iid = r.GetInt(); int lootUid = r.GetInt(); int srcPos = r.GetInt(); int count = r.GetInt(); int prefer = r.GetInt(); Inventory inv = null; if (lootUid >= 0) _srvLootByUid.TryGetValue(lootUid, out inv); if (inv == null && !TryResolveLootById(scene, posKey, iid, out inv)) { Server_SendLootDeny(peer, "no_inv"); return; } if (LootboxDetectUtil.IsPrivateInventory(inv)) { Server_SendLootDeny(peer, "no_inv"); return; } var srcItem = inv.GetItemAt(srcPos); if (!srcItem || count <= 0 || !srcItem.Stackable || count >= srcItem.StackCount) { Server_SendLootDeny(peer, "split_bad"); return; } Server_DoSplitAsync(inv, srcPos, count, prefer).Forget(); }

        private async UniTaskVoid Server_DoSplitAsync(Inventory inv, int srcPos, int count, int prefer)
        { _serverApplyingLoot = true; try { var srcItem = inv.GetItemAt(srcPos); if (!srcItem) return; var newItem = await srcItem.Split(count); if (!newItem) return; int dst = prefer; if (dst < 0 || inv.GetItemAt(dst)) dst = inv.GetFirstEmptyPosition(srcPos + 1); if (dst < 0) dst = inv.GetFirstEmptyPosition(0); bool ok = false; if (dst >= 0) ok = inv.AddAt(newItem, dst); if (!ok) ok = ItemUtilities.AddAndMerge(inv, newItem, srcPos + 1); if (!ok) { try { UnityEngine.Object.Destroy(newItem.gameObject); } catch { } if (srcItem) srcItem.StackCount = srcItem.StackCount + count; } } catch (System.Exception ex) { UnityEngine.Debug.LogError($"[LOOT][SPLIT] exception: {ex}"); } finally { _serverApplyingLoot = false; Server_SendLootboxState(null, inv); } }

        private bool TryGetLootboxWorldPos(Inventory inv, out Vector3 pos)
        { pos = default; if (!inv) return false; var boxes = GameObject.FindObjectsOfType<InteractableLootbox>(); foreach (var b in boxes) { if (!b) continue; if (b.Inventory == inv) { pos = b.transform.position; return true; } } return false; }
    }
}
