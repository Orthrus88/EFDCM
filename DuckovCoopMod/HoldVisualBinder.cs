// Escape-From-Duckov-Coop-Mod-Preview
// Copyright (C) 2025  Mr.sans and InitLoader's team
//
// This program is not a free software.
// It's distributed under a license based on AGPL-3.0,
// with strict additional restrictions:
//  YOU MUST NOT use this software for commercial purposes.
//  YOU MUST NOT use this software to run a headless game server.
//  YOU MUST include a conspicuous notice of attribution to
//  Mr-sans-and-InitLoader-s-team/Escape-From-Duckov-Coop-Mod-Preview as the original author.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DuckovCoopMod
{
    /// <summary>
    /// Ensures item agents held by a character have their Holder set (melee/guns),
    /// so visuals and behavior link to the correct CharacterMainControl after network cloning.
    /// </summary>
    internal static class HoldVisualBinder
    {
        public static void EnsureHeldVisuals(CharacterMainControl cmc)
        {
            if (!cmc) return;
            var model = cmc.characterModel;
            if (!model) return;

            try
            {
                // Melee: handheld slot
                var melee =
                    (model.MeleeWeaponSocket ? model.MeleeWeaponSocket.GetComponentInChildren<ItemAgent_MeleeWeapon>(true) : null)
                    ?? model.GetComponentInChildren<ItemAgent_MeleeWeapon>(true);
                if (melee && melee.Holder == null) melee.SetHolder(cmc);

                // Right-hand gun / main hand
                var rGun =
                    (model.RightHandSocket ? model.RightHandSocket.GetComponentInChildren<ItemAgent_Gun>(true) : null)
                    ?? model.GetComponentInChildren<ItemAgent_Gun>(true);
                if (rGun && rGun.Holder == null) rGun.SetHolder(cmc);

                // Left hand (some items/dual-wield/flashlights)
                var lGun = model.LefthandSocket ? model.LefthandSocket.GetComponentInChildren<ItemAgent_Gun>(true) : null;
                if (lGun && lGun.Holder == null) lGun.SetHolder(cmc);
            }
            catch { /* Visual fallback only; safe to ignore */ }
        }
    }
}

