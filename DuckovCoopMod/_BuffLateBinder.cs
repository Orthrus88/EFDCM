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

using Duckov.Buffs;
using HarmonyLib;
using ItemStatsSystem;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace DuckovCoopMod
{
    class _BuffLateBinder : MonoBehaviour
    {
        Buff _buff;
        FieldInfo _fiEffects;
        bool _done;

        public void Init(Buff buff, FieldInfo fiEffects)
        {
            _buff = buff;
            _fiEffects = fiEffects;
        }

        void Update()
        {
            if (_done || _buff == null) { Destroy(this); return; }

            // Get CharacterItem (Buff has a safe getter)
            var cmc = (_buff ? AccessTools.Field(typeof(Buff), "master")?.GetValue(_buff) as CharacterBuffManager : null)?.Master;
            var item = cmc ? cmc.CharacterItem : null;
            if (item == null || item.transform == null) return; // Not ready yet; try next frame

            // 1) Attach Buff's transform under CharacterItem
            _buff.transform.SetParent(item.transform, false);

            // 2) Bind Item to all effects
            var effectsObj = _fiEffects?.GetValue(_buff) as IList<Effect>;
            if (effectsObj != null)
            {
                for (int i = 0; i < effectsObj.Count; i++)
                {
                    var e = effectsObj[i];
                    if (e != null) e.SetItem(item);
                }
            }

            // Done once; remove this component
            _done = true;
            Destroy(this);
        }
    }
}

