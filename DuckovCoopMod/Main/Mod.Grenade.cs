using Cysharp.Threading.Tasks;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace DuckovCoopMod
{
    public partial class ModBehaviour
    {
        public void CacheGrenadePrefab(int typeId, Grenade prefab)
        {
            if (!prefab) return;
            prefabByTypeId[typeId] = prefab;
        }

        private bool TryResolvePrefab(int typeId, string _, string __, out Grenade prefab)
        {
            prefab = null;
            if (prefabByTypeId.TryGetValue(typeId, out var p) && p) { prefab = p; return true; }
            return false;
        }

        // Client
        public void Net_OnClientThrow(
            Skill_Grenade skill, int typeId, string prefabType, string prefabName,
            Vector3 startPoint, Vector3 velocity,
            bool createExplosion, float shake, float damageRange,
            bool delayFromCollide, float delayTime, bool isLandmine, float landmineRange)
        {
            if (IsServer || connectedPeer == null) return;
            writer.Reset();
            writer.Put((byte)Op.GRENADE_THROW_REQUEST);
            writer.Put("local"); // ID
            writer.Put(typeId);
            writer.Put(prefabType ?? string.Empty);
            writer.Put(prefabName ?? string.Empty);
            writer.PutV3cm(startPoint);
            writer.PutV3cm(velocity);
            writer.Put(createExplosion);
            writer.Put(shake);
            writer.Put(damageRange);
            writer.Put(delayFromCollide);
            writer.Put(delayTime);
            writer.Put(isLandmine);
            writer.Put(landmineRange);
            TransportSendToServer(writer, true);
        }

        // Host
        private void HandleGrenadeThrowRequest(NetPeer peer, NetPacketReader r)
        {
            string shooterId = r.GetString();
            int typeId = r.GetInt();
            string prefabType = r.GetString();   //
            string prefabName = r.GetString();   //
            Vector3 start = r.GetV3cm();
            Vector3 vel = r.GetV3cm();
            bool create = r.GetBool();
            float shake = r.GetFloat();
            float dmg = r.GetFloat();
            bool delayOnHit = r.GetBool();
            float delay = r.GetFloat();
            bool isMine = r.GetBool();
            float mineRange = r.GetFloat();

            HandleGrenadeThrowRequestAsync(peer, typeId, start, vel,
                create, shake, dmg, delayOnHit, delay, isMine, mineRange).Forget();
        }

        // Server validates client throw and spawns/broadcasts
        private async UniTask HandleGrenadeThrowRequestAsync(
            NetPeer peer, int typeId, Vector3 start, Vector3 vel,
            bool _create, float _shake, float _dmg, bool _delayOnHit, float _delay, bool _isMine, float _mineRange)
        {
            Grenade prefab = null;
            if (!prefabByTypeId.TryGetValue(typeId, out prefab) || !prefab)
                prefab = await COOPManager.GetGrenadePrefabByItemIdAsync(typeId);

            if (!prefab)
            {
                uint fid = nextGrenadeId++;
                Server_BroadcastGrenadeSpawn(fid, typeId, string.Empty, string.Empty, start, vel,
                    _create, _shake, _dmg, _delayOnHit, _delay, _isMine, _mineRange);
                return;
            }

            CacheGrenadePrefab(typeId, prefab);

            var tpl = await ReadGrenadeTemplateAsync(typeId);

            var fromChar = TryGetRemoteCharacterForPeer(peer);
            var g = Instantiate(prefab, start, Quaternion.identity);
            g.createExplosion = tpl.create;
            g.explosionShakeStrength = tpl.shake;
            g.damageRange = tpl.effectRange;
            g.delayFromCollide = tpl.delayFromCollide;
            g.delayTime = tpl.delay;
            g.isLandmine = tpl.isMine;
            g.landmineTriggerRange = tpl.mineRange;

            var di = tpl.di;
            try { di.fromCharacter = fromChar; } catch { }
            try { di.fromWeaponItemID = typeId; } catch { }
            g.damageInfo = di;

            g.SetWeaponIdInfo(typeId);
            g.Launch(start, vel, fromChar, true);
        }

        // Client
        private void HandleGrenadeSpawn(NetPacketReader r)
        {
            uint id = r.GetUInt();
            int typeId = r.GetInt();

            _ = r.GetString(); // prefabType (ignored)
            _ = r.GetString(); // prefabName (ignored)

            Vector3 start = r.GetV3cm();
            Vector3 vel = r.GetV3cm();
            bool create = r.GetBool();
            float shake = r.GetFloat();
            float dmg = r.GetFloat();
            bool delayOnHit = r.GetBool();
            float delay = r.GetFloat();
            bool isMine = r.GetBool();
            float mineRange = r.GetFloat();

            if (prefabByTypeId.TryGetValue(typeId, out var prefab) && prefab)
            {
                CacheGrenadePrefab(typeId, prefab);

                var g = Instantiate(prefab, start, Quaternion.identity);
                g.createExplosion = create;
                g.explosionShakeStrength = shake;
                g.damageRange = dmg;
                g.delayFromCollide = delayOnHit;
                g.delayTime = delay;
                g.isLandmine = isMine;
                g.landmineTriggerRange = mineRange;
                g.SetWeaponIdInfo(typeId);
                g.Launch(start, vel, null, true);
                AddNetGrenadeTag(g.gameObject, id);

                clientGrenades[id] = g.gameObject;
                return;
            }

            ResolveAndSpawnClientAsync(
                id, typeId, start, vel,
                create, shake, dmg, delayOnHit, delay, isMine, mineRange
            ).Forget();
        }

        static void AddNetGrenadeTag(GameObject go, uint id)
        {
            if (!go) return;
            var tag = go.GetComponent<NetGrenadeTag>() ?? go.AddComponent<NetGrenadeTag>();
            tag.id = id;
        }

        private async UniTask ResolveAndSpawnClientAsync(
            uint id, int typeId, Vector3 start, Vector3 vel,
            bool create, float shake, float dmg, bool delayOnHit, float delay,
            bool isMine, float mineRange)
        {
            var prefab = await COOPManager.GetGrenadePrefabByItemIdAsync(typeId);
            if (!prefab)
            {
                UnityEngine.Debug.LogError($"[CLIENT] grenade prefab exact resolve failed: typeId={typeId}");
                return;
            }

            CacheGrenadePrefab(typeId, prefab);

            var g = Instantiate(prefab, start, Quaternion.identity);
            g.createExplosion = create;
            g.explosionShakeStrength = shake;
            g.damageRange = dmg;
            g.delayFromCollide = delayOnHit;
            g.delayTime = delay;
            g.isLandmine = isMine;
            g.landmineTriggerRange = mineRange;
            g.SetWeaponIdInfo(typeId);
            g.Launch(start, vel, null, true);
            AddNetGrenadeTag(g.gameObject, id);

            clientGrenades[id] = g.gameObject;
        }

        private void HandleGrenadeExplode(NetPacketReader r)
        {
            uint id = r.GetUInt();
            Vector3 pos = r.GetV3cm();
            float dmg = r.GetFloat();
            float shake = r.GetFloat();
            if (clientGrenades.TryGetValue(id, out var go) && go)
            {
                go.SendMessage("Explode", SendMessageOptions.DontRequireReceiver);
                Destroy(go, 0.1f);
                clientGrenades.Remove(id);
            }
        }

        private void Server_BroadcastGrenadeSpawn(uint id, Grenade g, int typeId, string prefabType, string prefabName, Vector3 start, Vector3 vel)
        {
            writer.Reset();
            writer.Put((byte)Op.GRENADE_SPAWN);
            writer.Put(id);
            writer.Put(typeId);
            writer.Put(prefabType ?? string.Empty);
            writer.Put(prefabName ?? string.Empty);
            writer.PutV3cm(start);
            writer.PutV3cm(vel);
            writer.Put(g.createExplosion);
            writer.Put(g.explosionShakeStrength);
            writer.Put(g.damageRange);
            writer.Put(g.delayFromCollide);
            writer.Put(g.delayTime);
            writer.Put(g.isLandmine);
            writer.Put(g.landmineTriggerRange);
            BroadcastReliable(writer);
        }

        private void Server_BroadcastGrenadeSpawn(uint id, int typeId, string prefabType, string prefabName, Vector3 start, Vector3 vel,
            bool create, float shake, float dmg, bool delayOnHit, float delay, bool isMine, float mineRange)
        {
            writer.Reset();
            writer.Put((byte)Op.GRENADE_SPAWN);
            writer.Put(id);
            writer.Put(typeId);
            writer.Put(prefabType ?? string.Empty);
            writer.Put(prefabName ?? string.Empty);
            writer.PutV3cm(start);
            writer.PutV3cm(vel);
            writer.Put(create);
            writer.Put(shake);
            writer.Put(dmg);
            writer.Put(delayOnHit);
            writer.Put(delay);
            writer.Put(isMine);
            writer.Put(mineRange);
            BroadcastReliable(writer);
        }

        private void Server_BroadcastGrenadeExplode(uint id, Grenade g, Vector3 pos)
        {
            writer.Reset();
            writer.Put((byte)Op.GRENADE_EXPLODE);
            writer.Put(id);
            writer.PutV3cm(pos);
            writer.Put(g.damageRange);
            writer.Put(g.explosionShakeStrength);
            BroadcastReliable(writer);
        }

        public void Server_OnGrenadeLaunched(Grenade g, Vector3 start, Vector3 vel, int typeId)
        {
            if (g.damageRange <= 0f)
            {
                ReadGrenadeTemplateAsync(typeId).ContinueWith(defs =>
                {
                    g.damageInfo = defs.di;
                    g.createExplosion = defs.create;
                    g.explosionShakeStrength = defs.shake;
                    g.damageRange = defs.effectRange;
                    g.delayFromCollide = defs.delayFromCollide;
                    g.delayTime = defs.delay;
                    g.isLandmine = defs.isMine;
                    g.landmineTriggerRange = defs.mineRange;

                    var di = g.damageInfo;
                    try { di.fromWeaponItemID = typeId; } catch { }
                    g.damageInfo = di;
                }).Forget();
            }

            uint id = 0; foreach (var kv in serverGrenades) if (kv.Value == g) { id = kv.Key; break; }
            if (id == 0) { id = nextGrenadeId++; serverGrenades[id] = g; }
            const string prefabType = ""; const string prefabName = "";
            Server_BroadcastGrenadeSpawn(id, g, typeId, prefabType, prefabName, start, vel);
        }

        public void Server_OnGrenadeExploded(Grenade g)
        {
            uint id = 0; foreach (var kv in serverGrenades) if (kv.Value == g) { id = kv.Key; break; }
            if (id == 0) return;
            Server_BroadcastGrenadeExplode(id, g, g.transform.position);
        }

        private CharacterMainControl TryGetRemoteCharacterForPeer(NetPeer peer)
        {
            if (remoteCharacters.TryGetValue(peer, out var remoteObj) && remoteObj)
            {
                var cm = remoteObj.GetComponent<CharacterMainControl>().characterModel;
                if (cm != null) return cm.characterMainControl;
            }
            return null;
        }
    }
}

