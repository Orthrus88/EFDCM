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

using Duckov.UI;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace DuckovCoopMod
{
    /// <summary>
    /// Client-side component that retries binding a HealthBar to remote actors,
    /// fixing race conditions during network cloning/UI initialization.
    /// </summary>
    [DisallowMultipleComponent]
    public class AutoRequestHealthBar : MonoBehaviour
    {
        [SerializeField] int attempts = 30;      // Max retry attempts (~3 seconds total)
        [SerializeField] float interval = 0.1f;  // Retry interval

        static readonly System.Reflection.FieldInfo FI_character =
            typeof(Health).GetField("characterCached", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        static readonly System.Reflection.FieldInfo FI_hasChar =
            typeof(Health).GetField("hasCharacter", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        void OnEnable()
        {
            StartCoroutine(Bootstrap());
        }

        IEnumerator Bootstrap()
        {
            // Wait two frames to let hierarchy/scene/UI pipeline settle
            yield return null;
            yield return null;

            var cmc = GetComponent<CharacterMainControl>();
            var h = GetComponentInChildren<Health>(true);
            if (!h) yield break;

            // Bind Health Character (common issue for remote clones)
            try { FI_character?.SetValue(h, cmc); FI_hasChar?.SetValue(h, true); } catch { }

            for (int i = 0; i < attempts; i++)
            {
                if (!h) yield break;

                try { h.showHealthBar = true; } catch { }
                try { h.RequestHealthBar(); } catch { }

                try { h.OnMaxHealthChange?.Invoke(h); } catch { }
                try { h.OnHealthChange?.Invoke(h); } catch { }

                yield return new WaitForSeconds(interval);
            }
        }
    }
}

