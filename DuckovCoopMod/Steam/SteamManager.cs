using System;
using UnityEngine;
using Steamworks;

namespace DuckovCoopMod
{
    /// <summary>
    /// Minimal Steam bootstrapper: initializes Steam API and pumps callbacks.
    /// Safe to call Init() multiple times; will only initialize once.
    /// </summary>
    internal sealed class SteamManager : MonoBehaviour
    {
        private static SteamManager _instance;
        public static bool Initialized { get; private set; }

        public static void Init()
        {
            if (Initialized) return;
            try
            {
                if (!SteamAPI.Init())
                {
                    Debug.LogWarning("[Steam] SteamAPI.Init failed — running without Steam.");
                    return;
                }
                try { SteamNetworkingUtils.InitRelayNetworkAccess(); } catch { }
                var go = new GameObject("SteamManager");
                _instance = go.AddComponent<SteamManager>();
                DontDestroyOnLoad(go);
                Initialized = true;
                Debug.Log("[Steam] Initialized. Me=" + SteamUser.GetSteamID());
            }
            catch (DllNotFoundException e)
            {
                Debug.LogWarning("[Steam] DLL not found: " + e.Message);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Steam] Init exception: " + e.Message);
            }
        }

        private void Update()
        {
            if (!Initialized) return;
            try { SteamAPI.RunCallbacks(); } catch { }
        }

        private void OnDestroy()
        {
            if (!Initialized) return;
            try { SteamAPI.Shutdown(); } catch { }
            Initialized = false;
        }
    }
}
