using System;
using Cysharp.Threading.Tasks;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace DuckovCoopMod
{
    public partial class ModBehaviour
    {
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
            writer.Put((byte)Op.FIRE_REQUEST);
            writer.Put(localPlayerStatus.EndPoint);
            writer.Put(gun.Item.TypeID);
            writer.PutV3cm(muzzle);
            writer.PutDir(baseDir);
            writer.PutV3cm(firstCheckStart);

            float clientScatter = 0f;
            float ads01 = 0f;
            try { clientScatter = Mathf.Max(0f, gun.CurrentScatter); ads01 = (gun.IsInAds ? 1f : 0f); } catch { }
            writer.Put(clientScatter);
            writer.Put(ads01);

            var hint = new ProjectileContext();
            try
            {
                bool hasBulletItem = (gun.BulletItem != null);
                float charMul = gun.CharacterDamageMultiplier;
                float bulletMul = hasBulletItem ? Mathf.Max(0.0001f, gun.BulletDamageMultiplier) : 1f;
                int shots = Mathf.Max(1, gun.ShotCount);
                hint.damage = gun.Damage * bulletMul * charMul / shots;
                if (gun.Damage > 1f && hint.damage < 1f) hint.damage = 1f;
                float bulletCritRateGain = hasBulletItem ? gun.bulletCritRateGain : 0f;
                float bulletCritDmgGain = hasBulletItem ? gun.BulletCritDamageFactorGain : 0f;
                hint.critDamageFactor = (gun.CritDamageFactor + bulletCritDmgGain) * (1f + gun.CharacterGunCritDamageGain);
                hint.critRate = gun.CritRate * (1f + gun.CharacterGunCritRateGain + bulletCritRateGain);
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
            catch { }

            writer.PutProjectilePayload(hint);
            TransportSendToServer(writer, true);
        }

        private void HandleFireRequest(NetPeer peer, NetPacketReader r)
        {
            string shooterId = r.GetString();
            int weaponType = r.GetInt();
            Vector3 muzzle = r.GetV3cm();
            Vector3 baseDir = r.GetDir();
            Vector3 firstCheckStart = r.GetV3cm();

            float clientScatter = 0f;
            float ads01 = 0f;
            try { clientScatter = r.GetFloat(); ads01 = r.GetFloat(); } catch { clientScatter = 0f; ads01 = 0f; }

            _payloadHint = default;
            _hasPayloadHint = NetPack_Projectile.TryGetProjectilePayload(r, ref _payloadHint);

            if (!remoteCharacters.TryGetValue(peer, out var who) || !who) { _hasPayloadHint = false; return; }
            var cm = who.GetComponent<CharacterMainControl>().characterModel;

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

            Vector3 finalDir;
            float speed, distance;

            if (gun)
            {
                if (!Server_SpawnProjectile(gun, muzzle, baseDir, firstCheckStart, out finalDir, clientScatter, ads01))
                { _hasPayloadHint = false; return; }
                speed = gun.BulletSpeed * (gun.Holder ? gun.Holder.GunBulletSpeedMultiplier : 1f);
                distance = gun.BulletDistance + 0.4f;
            }
            else
            {
                finalDir = (baseDir.sqrMagnitude > 1e-8f ? baseDir.normalized : Vector3.forward);
                speed = _speedCacheByWeaponType.TryGetValue(weaponType, out var sp) ? sp : 60f;
                distance = _distCacheByWeaponType.TryGetValue(weaponType, out var dist) ? dist : 50f;
            }

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
            _hasPayloadHint = false;
        }

        private void PlayShootAnimOnServerPeer(NetPeer peer)
        {
            if (!remoteCharacters.TryGetValue(peer, out var who) || !who) return;
            var animCtrl = who.GetComponent<CharacterMainControl>().characterModel.GetComponentInParent<CharacterAnimationControl_MagicBlend>();
            if (animCtrl && animCtrl.animator) animCtrl.OnAttack();
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

            var w = writer; w.Reset();
            w.Put((byte)Op.FIRE_EVENT);
            w.Put(localPlayerStatus.EndPoint);
            w.Put(gun.Item.TypeID);
            w.PutV3cm(muzzleWorld);
            w.PutDir(finalDir);
            w.Put(speed);
            w.Put(distance);

            var payloadCtx = new ProjectileContext();
            bool hasBulletItem = false; try { hasBulletItem = (gun.BulletItem != null); } catch { }
            float charMul = 1f, bulletMul = 1f; int shots = 1;
            try { charMul = gun.CharacterDamageMultiplier; bulletMul = hasBulletItem ? Mathf.Max(0.0001f, gun.BulletDamageMultiplier) : 1f; shots = Mathf.Max(1, gun.ShotCount); } catch { }
            try { payloadCtx.damage = gun.Damage * bulletMul * charMul / shots; if (gun.Damage > 1f && payloadCtx.damage < 1f) payloadCtx.damage = 1f; } catch { if (payloadCtx.damage <= 0f) payloadCtx.damage = 1f; }
            try { float r = hasBulletItem ? gun.bulletCritRateGain : 0f; float d = hasBulletItem ? gun.BulletCritDamageFactorGain : 0f; payloadCtx.critDamageFactor = (gun.CritDamageFactor + d) * (1f + gun.CharacterGunCritDamageGain); payloadCtx.critRate = gun.CritRate * (1f + gun.CharacterGunCritRateGain + r); } catch { }
            try { float ap = hasBulletItem ? gun.BulletArmorPiercingGain : 0f; float ab = hasBulletItem ? gun.BulletArmorBreakGain : 0f; payloadCtx.armorPiercing = gun.ArmorPiercing + ap; payloadCtx.armorBreak = gun.ArmorBreak + ab; } catch { }
            try
            {
                var setting = gun.GunItemSetting; if (setting != null)
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
                if (hasBulletItem) { payloadCtx.buffChance = gun.BulletBuffChanceMultiplier * gun.BuffChance; payloadCtx.bleedChance = gun.BulletBleedChance; }
                payloadCtx.penetrate = gun.Penetrate; payloadCtx.fromWeaponItemID = gun.Item.TypeID;
            }
            catch { }

            w.PutProjectilePayload(payloadCtx);
            TransportBroadcast(w, true);

            PlayMuzzleFxAndShell(localPlayerStatus.EndPoint, gun.Item.TypeID, muzzleWorld, finalDir);
        }

        bool Server_SpawnProjectile(ItemAgent_Gun gun, Vector3 muzzle, Vector3 baseDir, Vector3 firstCheckStart, out Vector3 finalDir, float clientScatter, float ads01)
        {
            finalDir = baseDir.sqrMagnitude < 1e-8f ? Vector3.forward : baseDir.normalized;

            bool isMain = (gun.Holder && gun.Holder.IsMainCharacter);
            if (isMain)
            {
                try
                {
                    var projInst = Traverse.Create(gun).Field<Projectile>("projInst").Value;
                    if (projInst)
                    {
                        var t = projInst.transform; Vector3 fwd = t.forward; if (fwd.sqrMagnitude < 1e-8f) fwd = (gun.muzzle ? gun.muzzle.forward : Vector3.forward); finalDir = fwd.normalized;
                        TryStartVisualRecoil(gun);
                        return true;
                    }
                }
                catch { }
            }

            Projectile pfb = null;
            if (!_projCacheByWeaponType.TryGetValue(gun.Item ? gun.Item.TypeID : 0, out pfb) || !pfb)
            {
                try { var setting = gun.GunItemSetting; if (setting && setting.bulletPfb) pfb = setting.bulletPfb; } catch { }
                if (!pfb) pfb = Duckov.Utilities.GameplayDataSettings.Prefabs.DefaultBullet;
            }
            if (!pfb) return false;

            Vector3 dir = finalDir;
            if (ads01 < 0.5f)
            {
                float spread = Mathf.Max(0f, clientScatter);
                if (spread > 0.0001f)
                {
                    var q = UnityEngine.Random.rotationUniform;
                    dir = (q * dir).normalized;
                }
            }

            var inst = LevelManager.Instance.BulletPool.GetABullet(pfb);
            inst.transform.position = muzzle;
            inst.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);

            var ctx = new ProjectileContext
            {
                direction = dir,
                speed = gun.BulletSpeed * (gun.Holder ? gun.Holder.GunBulletSpeedMultiplier : 1f),
                distance = gun.BulletDistance + 0.4f,
                halfDamageDistance = (gun.BulletDistance + 0.4f) * 0.5f,
                firstFrameCheck = true,
                firstFrameCheckStartPoint = firstCheckStart,
                team = (gun.Holder && gun.Holder.IsMainCharacter) ? Teams.player : Teams.enemy
            };

            try
            {
                bool hasBulletItem = (gun.BulletItem != null);
                float charMul = gun.CharacterDamageMultiplier;
                float bulletMul = hasBulletItem ? Mathf.Max(0.0001f, gun.BulletDamageMultiplier) : 1f;
                int shots = Mathf.Max(1, gun.ShotCount);
                ctx.damage = gun.Damage * bulletMul * charMul / shots;
                if (gun.Damage > 1f && ctx.damage < 1f) ctx.damage = 1f;
                float bulletCritRateGain = hasBulletItem ? gun.bulletCritRateGain : 0f;
                float bulletCritDmgGain = hasBulletItem ? gun.BulletCritDamageFactorGain : 0f;
                ctx.critDamageFactor = (gun.CritDamageFactor + bulletCritDmgGain) * (1f + gun.CharacterGunCritDamageGain);
                ctx.critRate = gun.CritRate * (1f + gun.CharacterGunCritRateGain + bulletCritRateGain);
                ctx.armorPiercing = gun.ArmorPiercing + (hasBulletItem ? gun.BulletArmorPiercingGain : 0f);
                ctx.armorBreak = gun.ArmorBreak + (hasBulletItem ? gun.BulletArmorBreakGain : 0f);
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
                ctx.explosionRange = gun.BulletExplosionRange;
                ctx.explosionDamage = gun.BulletExplosionDamage * gun.ExplosionDamageMultiplier;
                if (hasBulletItem) { ctx.buffChance = gun.BulletBuffChanceMultiplier * gun.BuffChance; ctx.bleedChance = gun.BulletBleedChance; }
                ctx.penetrate = gun.Penetrate;
                ctx.fromWeaponItemID = (gun.Item != null ? gun.Item.TypeID : 0);
            }
            catch { }

            inst.Init(ctx);
            _speedCacheByWeaponType[gun.Item ? gun.Item.TypeID : 0] = ctx.speed;
            _distCacheByWeaponType[gun.Item ? gun.Item.TypeID : 0] = ctx.distance;
            return true;
        }
    }
}

