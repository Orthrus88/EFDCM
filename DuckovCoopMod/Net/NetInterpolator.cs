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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace DuckovCoopMod
{
    /// <summary>
    /// Temporal interpolation buffer for remote transforms.
    /// - Push() enqueues time-stamped positions/rotations.
    /// - Update() replays with a small rewind (interpolationBackTime) for smoothness.
    /// - Predicts briefly when missing frames (maxExtrapolate) and hard-snaps on large error.
    /// - Can disable extrapolation during fast run to reduce overshoot artifacts.
    /// </summary>
    public class NetInterpolator : MonoBehaviour
    {
        struct Snap { public double t; public Vector3 pos; public Quaternion rot; }

        [Tooltip("Render rewind time; larger is smoother, smaller is snappier")]
        public float interpolationBackTime = 0.12f;  
        [Tooltip("Max prediction window when frames drop")]
        public float maxExtrapolate = 0.05f;        
        [Tooltip("Hard-snap distance when error is too large")]
        public float hardSnapDistance = 6f;          // 6 meters
        [Tooltip("Instant weight for position smoothing lerp")]
        public float posLerpFactor = 0.9f;
        [Tooltip("Instant weight for rotation smoothing lerp")]
        public float rotLerpFactor = 0.9f;

        [Header("Run Extrapolation Guard")]
        public bool extrapolateWhenRunning = false; // Disable prediction when running by default
        public float runSpeedThreshold = 3.0f;      // >3 m/s considered as running

        Transform root;      // 
        Transform modelRoot; // 

        readonly List<Snap> _buf = new List<Snap>(64);
        Vector3 _lastVel = Vector3.zero;

        public void Init(Transform rootT, Transform modelRootT)
        {
            root = rootT; modelRoot = modelRootT ? modelRootT : rootT;
        }

        /// <summary>
        /// Enqueue a network sample. If 'when' &lt; 0, uses current unscaled time.
        /// </summary>
        public void Push(Vector3 pos, Quaternion rot, double when = -1)
        {
            if (when < 0) when = Time.unscaledTimeAsDouble;
            if (_buf.Count > 0)
            {
                var prev = _buf[_buf.Count - 1];
                double dt = when - prev.t;
                if (dt > 1e-6) _lastVel = (pos - prev.pos) / (float)dt;

                // 
                if ((pos - prev.pos).sqrMagnitude > hardSnapDistance * hardSnapDistance)
                    _buf.Clear();
            }

            _buf.Add(new Snap { t = when, pos = pos, rot = rot });
            if (_buf.Count > 64) _buf.RemoveAt(0);
        }

        void LateUpdate()
        {
            // 
            if (!root)
            {
                var cmc = GetComponentInChildren<CharacterMainControl>();
                if (cmc)
                {
                    root = cmc.transform;
                    modelRoot = cmc.modelRoot ? cmc.modelRoot.transform : cmc.transform;
                }
                else root = transform;
            }
            if (!modelRoot) modelRoot = root;
            if (_buf.Count == 0) return;

            double renderT = Time.unscaledTimeAsDouble - interpolationBackTime;

            // [i-1, i] renderT 
            int i = 0;
            while (i < _buf.Count && _buf[i].t < renderT) i++;

            if (i == 0)
            {
                // 100ms 
                Apply(_buf[0].pos, _buf[0].rot, hardSnap: true);
                return;
            }

            if (i < _buf.Count)
            {
                // 
                var a = _buf[i - 1]; var b = _buf[i];
                float t = (float)((renderT - a.t) / System.Math.Max(1e-6, b.t - a.t));
                var pos = Vector3.LerpUnclamped(a.pos, b.pos, t);
                var rot = Quaternion.Slerp(a.rot, b.rot, t);
                Apply(pos, rot);

                // 
                if (i > 1) _buf.RemoveRange(0, i - 1);
            }
            else
            {
                var last = _buf[_buf.Count - 1];
                double dt = renderT - last.t;

                // 
                bool allow = (dt <= maxExtrapolate);
                if (!extrapolateWhenRunning)
                {
                    float speed = _lastVel.magnitude;
                    if (speed > runSpeedThreshold) allow = false; // 
                }

                if (allow)
                    Apply(last.pos + _lastVel * (float)dt, last.rot);
                else
                    Apply(last.pos, last.rot);

                if (_buf.Count > 2) _buf.RemoveRange(0, _buf.Count - 2);
            }
        }

        void Apply(Vector3 pos, Quaternion rot, bool hardSnap = false)
        {
            if (!root) return;

            // Sans 
            if (hardSnap || (root.position - pos).sqrMagnitude > hardSnapDistance * hardSnapDistance)
            {
                root.SetPositionAndRotation(pos, rot);
                if (modelRoot && modelRoot != root) modelRoot.rotation = rot;
                return;
            }

            // 
            root.position = Vector3.Lerp(root.position, pos, posLerpFactor);
            if (modelRoot)
                modelRoot.rotation = Quaternion.Slerp(modelRoot.rotation, rot, rotLerpFactor);
        }
    }

    // 
    public static class NetInterpUtil
    {
        public static NetInterpolator Attach(GameObject go)
        {
            if (!go) return null;
            var ni = go.GetComponent<NetInterpolator>();
            if (!ni) ni = go.AddComponent<NetInterpolator>();
            var cmc = go.GetComponent<CharacterMainControl>();
            if (cmc) ni.Init(cmc.transform, cmc.modelRoot ? cmc.modelRoot.transform : cmc.transform);
            return ni;
        }
    }

}
