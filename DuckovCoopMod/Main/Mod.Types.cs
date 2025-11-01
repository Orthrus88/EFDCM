using System.Runtime.CompilerServices;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace DuckovCoopMod
{
    public static class NetDataExtensions
    {
        public static void PutVector3(this NetDataWriter writer, Vector3 vector)
        {
            writer.Put(vector.x);
            writer.Put(vector.y);
            writer.Put(vector.z);
        }

        public static Vector3 GetVector3(this NetPacketReader reader)
        {
            return new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
        }

        public static Vector3 GetVector3(this NetDataReader reader)
        {
            return new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Quaternion NormalizeSafe(Quaternion q)
        {
            if (!Finite(q.x) || !Finite(q.y) || !Finite(q.z) || !Finite(q.w))
                return Quaternion.identity;

            float mag2 = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            if (mag2 < 1e-12f) return Quaternion.identity;

            float inv = 1.0f / Mathf.Sqrt(mag2);
            q.x *= inv; q.y *= inv; q.z *= inv; q.w *= inv;
            return q;
        }

        public static void PutQuaternion(this NetDataWriter writer, Quaternion q)
        {
            q = NormalizeSafe(q);
            writer.Put(q.x); writer.Put(q.y); writer.Put(q.z); writer.Put(q.w);
        }

        public static Quaternion GetQuaternion(this NetPacketReader reader)
        {
            var q = new Quaternion(reader.GetFloat(), reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            return NormalizeSafe(q);
        }

        public static Quaternion GetQuaternion(this NetDataReader reader)
        {
            var q = new Quaternion(reader.GetFloat(), reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            return NormalizeSafe(q);
        }
    }

    public enum EquipKind { None, Armor, Helmat, FaceMask, Backpack, Headset }

    public enum Op : byte
    {
        PLAYER_STATUS_UPDATE = 1,
        CLIENT_STATUS_UPDATE = 2,
        POSITION_UPDATE = 3,
        ANIM_SYNC = 4,
        EQUIPMENT_UPDATE = 5,
        PLAYERWEAPON_UPDATE = 6,
        FIRE_REQUEST = 7,
        FIRE_EVENT = 8,
        GRENADE_THROW_REQUEST = 9,
        GRENADE_SPAWN = 10,
        GRENADE_EXPLODE = 11,
        ITEM_DROP_REQUEST = 12,
        ITEM_SPAWN = 13,
        ITEM_PICKUP_REQUEST = 14,
        ITEM_DESPAWN = 15,
        PLAYER_HEALTH_REPORT = 16,
        AUTH_HEALTH_SELF = 17,
        AUTH_HEALTH_REMOTE = 18,
        SCENE_VOTE_START = 19,
        SCENE_READY_SET = 20,
        SCENE_BEGIN_LOAD = 21,
        SCENE_CANCEL = 22,
        SCENE_READY = 23,
        REMOTE_CREATE = 24,
        REMOTE_DESPAWN = 25,

        DOOR_REQ_SET = 206,
        DOOR_STATE = 207,

        LOOT_REQ_SLOT_UNPLUG = 208,
        LOOT_REQ_SLOT_PLUG = 209,

        SCENE_VOTE_REQ = 210,

        AI_TRANSFORM_SNAPSHOT = 233,
        AI_SEED_SNAPSHOT = 230,
        AI_FREEZE_TOGGLE = 231,
        AI_LOADOUT_SNAPSHOT = 232,
        AI_ANIM_SNAPSHOT = 234,
        AI_ATTACK_SWING = 235,
        AI_ATTACK_TELL = 236,
        AI_HEALTH_SYNC = 237,
        AI_NAME_ICON = 238,
        AI_SEED_PATCH = 227,

        DEAD_LOOT_DESPAWN = 247,
        DEAD_LOOT_SPAWN = 248,

        SCENE_GATE_READY = 228,
        SCENE_GATE_RELEASE = 229,

        PLAYER_DEAD_TREE = 173,
        PLAYER_HURT_EVENT = 170,

        HOST_BUFF_PROXY_APPLY = 172,
        PLAYER_BUFF_SELF_APPLY = 171,
        ENV_HURT_REQUEST = 220,
        ENV_HURT_EVENT = 221,
        ENV_DEAD_EVENT = 222,
        MELEE_ATTACK_REQUEST = 242,
        MELEE_ATTACK_SWING = 243,
        MELEE_HIT_REPORT = 244,
        DISCOVER_REQUEST = 240,
        DISCOVER_RESPONSE = 241,
        ENV_SYNC_REQUEST = 245,
        ENV_SYNC_STATE = 246,

        LOOT_REQ_SPLIT = 239,

        LOOT_REQ_OPEN = 250,
        LOOT_STATE = 251,
        LOOT_REQ_PUT = 252,
        LOOT_REQ_TAKE = 253,
        LOOT_PUT_OK = 254,
        LOOT_TAKE_OK = 255,
        LOOT_DENY = 249,
    }

    public static class NetPack
    {
        const float POS_SCALE = 100f;

        public static void PutV3cm(this NetDataWriter w, Vector3 v)
        {
            w.Put((int)Mathf.Round(v.x * POS_SCALE));
            w.Put((int)Mathf.Round(v.y * POS_SCALE));
            w.Put((int)Mathf.Round(v.z * POS_SCALE));
        }
        public static Vector3 GetV3cm(this NetPacketReader r)
        {
            float inv = 1f / POS_SCALE;
            return new Vector3(r.GetInt() * inv, r.GetInt() * inv, r.GetInt() * inv);
        }
        public static Vector3 GetV3cm(this NetDataReader r)
        {
            float inv = 1f / POS_SCALE;
            return new Vector3(r.GetInt() * inv, r.GetInt() * inv, r.GetInt() * inv);
        }

        public static void PutDir(this NetDataWriter w, Vector3 dir)
        {
            if (dir.sqrMagnitude < 1e-8f) dir = Vector3.forward;
            dir.Normalize();
            float pitch = Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg;
            float yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            if (yaw < 0) yaw += 360f;

            ushort qYaw = (ushort)Mathf.Clamp(Mathf.RoundToInt(yaw / 360f * 65535f), 0, 65535);
            ushort qPitch = (ushort)Mathf.Clamp(Mathf.RoundToInt((pitch + 90f) / 180f * 65535f), 0, 65535);
            w.Put(qYaw);
            w.Put(qPitch);
        }
        public static Vector3 GetDir(this NetPacketReader r)
        {
            float yaw = r.GetUShort() / 65535f * 360f;
            float pitch = (r.GetUShort() / 65535f) * 180f - 90f;
            float cy = Mathf.Cos(yaw * Mathf.Deg2Rad);
            float sy = Mathf.Sin(yaw * Mathf.Deg2Rad);
            float cp = Mathf.Cos(pitch * Mathf.Deg2Rad);
            float sp = Mathf.Sin(pitch * Mathf.Deg2Rad);
            Vector3 d = new Vector3(sy * cp, sp, cy * cp);
            if (d.sqrMagnitude < 1e-8f) d = Vector3.forward;
            return d;
        }
        public static Vector3 GetDir(this NetDataReader r)
        {
            float yaw = r.GetUShort() / 65535f * 360f;
            float pitch = (r.GetUShort() / 65535f) * 180f - 90f;
            float cy = Mathf.Cos(yaw * Mathf.Deg2Rad);
            float sy = Mathf.Sin(yaw * Mathf.Deg2Rad);
            float cp = Mathf.Cos(pitch * Mathf.Deg2Rad);
            float sp = Mathf.Sin(pitch * Mathf.Deg2Rad);
            Vector3 d = new Vector3(sy * cp, sp, cy * cp);
            if (d.sqrMagnitude < 1e-8f) d = Vector3.forward;
            return d;
        }

        public static void PutSNorm16(this NetDataWriter w, float v)
        {
            int q = Mathf.RoundToInt(Mathf.Clamp(v, -8f, 8f) * 16f);
            w.Put((sbyte)Mathf.Clamp(q, sbyte.MinValue, sbyte.MaxValue));
        }
        public static float GetSNorm16(this NetPacketReader r)
        {
            return r.GetSByte() / 16f;
        }

        public static void PutDamagePayload(this NetDataWriter w,
            float damageValue, float armorPiercing, float critDmgFactor, float critRate, int crit,
            Vector3 damagePoint, Vector3 damageNormal, int fromWeaponItemID, float bleedChance, bool isExplosion,
            float attackRange)
        {
            w.Put(damageValue);
            w.Put(armorPiercing);
            w.Put(critDmgFactor);
            w.Put(critRate);
            w.Put(crit);
            w.PutV3cm(damagePoint);
            w.PutDir(damageNormal.sqrMagnitude < 1e-6f ? Vector3.forward : damageNormal.normalized);
            w.Put(fromWeaponItemID);
            w.Put(bleedChance);
            w.Put(isExplosion);
            w.Put(attackRange);
        }

        public static (float dmg, float ap, float cdf, float cr, int crit, Vector3 point, Vector3 normal, int wid, float bleed, bool boom, float range)
            GetDamagePayload(this NetPacketReader r)
        {
            float dmg = r.GetFloat();
            float ap = r.GetFloat();
            float cdf = r.GetFloat();
            float cr = r.GetFloat();
            int crit = r.GetInt();
            Vector3 p = r.GetV3cm();
            Vector3 n = r.GetDir();
            int wid = r.GetInt();
            float bleed = r.GetFloat();
            bool boom = r.GetBool();
            float rng = r.GetFloat();
            return (dmg, ap, cdf, cr, crit, p, n, wid, bleed, boom, rng);
        }
    }

    static class ServerTuning
    {
        public const float RemoteMeleeCharScale = 1.00f;
        public const float RemoteMeleeEnvScale = 1.5f;
        public const bool UseNullAttackerForEnv = true;
    }

    public class EquipmentSyncData
    {
        public int SlotHash;
        public string ItemId;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(SlotHash);
            writer.Put(ItemId ?? "");
        }

        public static EquipmentSyncData Deserialize(NetPacketReader reader)
        {
            return new EquipmentSyncData
            {
                SlotHash = reader.GetInt(),
                ItemId = reader.GetString()
            };
        }

        public static EquipmentSyncData Deserialize(NetDataReader reader)
        {
            return new EquipmentSyncData
            {
                SlotHash = reader.GetInt(),
                ItemId = reader.GetString()
            };
        }
    }

    public class WeaponSyncData
    {
        public int SlotHash;
        public string ItemId;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(SlotHash);
            writer.Put(ItemId ?? "");
        }

        public static WeaponSyncData Deserialize(NetPacketReader reader)
        {
            return new WeaponSyncData
            {
                SlotHash = reader.GetInt(),
                ItemId = reader.GetString()
            };
        }

        public static WeaponSyncData Deserialize(NetDataReader reader)
        {
            return new WeaponSyncData
            {
                SlotHash = reader.GetInt(),
                ItemId = reader.GetString()
            };
        }
    }
}
