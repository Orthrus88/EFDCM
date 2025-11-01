using System;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;
using Duckov.UI;

namespace DuckovCoopMod
{
    public partial class ModBehaviour
    {
        void OnGUI()
        {
            if (showUI)
            {
                mainWindowRect = GUI.Window(94120, mainWindowRect, DrawMainWindow, "Co-op Mod Control Panel");

                if (showPlayerStatusWindow)
                {
                    playerStatusWindowRect = GUI.Window(94121, playerStatusWindowRect, DrawPlayerStatusWindow, "Player Status");
                }
            }

            if (sceneVoteActive)
            {
                float h = 220f;
                var area = new Rect(10, Screen.height * 0.5f - h * 0.5f, 320, h);
                GUILayout.BeginArea(area, GUI.skin.box);
                GUILayout.Label($"Map Vote / Ready  [{SceneInfoCollection.GetSceneInfo(sceneTargetId).DisplayName}]");
                GUILayout.Label($"Press {readyKey} to toggle Ready (Current: {(localReady ? "Ready" : "Not Ready")})");

                GUILayout.Space(8);
                GUILayout.Label("Players Ready Status:");
                foreach (var pid in sceneParticipantIds)
                {
                    bool r = false; sceneReady.TryGetValue(pid, out r);
                    GUILayout.Label($"- {pid}  -- {(r ? "Ready" : "Not Ready")}");
                }
                GUILayout.EndArea();
            }

            if (_spectatorActive)
            {
                var style = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.LowerCenter,
                    fontSize = 18
                };
                style.normal.textColor = Color.white;

                try { } catch { }

                GUI.Label(new Rect(0, Screen.height - 40, Screen.width, 30),
                    $"Spectate: Left Click > Next | Right Click < Previous | Watching", style);
            }
        }

        private void DrawMainWindow(int windowID)
        {
            GUILayout.BeginVertical();
            GUILayout.Label($"Mode: {(IsServer ? "Server" : "Client")}");

            if (GUILayout.Button("Switch to " + (IsServer ? "Client" : "Server") + " Mode"))
            {
                IsServer = !IsServer;
                StartNetwork(IsServer);
            }

            GUILayout.Space(10);

            // Steam Transport (P2P)
            GUILayout.Space(6);
            useSteamLobby = GUILayout.Toggle(useSteamLobby, "Use Steam Transport (P2P)");
            if (useSteamLobby)
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Create Steam Lobby", GUILayout.Width(160)))
                {
                    try { SteamManager.Init(); StartNetwork(true); SteamLobbyManager.Instance.CreateLobby(8, "v1", port); } catch { }
                }
                if (GUILayout.Button("Join Lobby Owner", GUILayout.Width(160)))
                {
                    try
                    {
                        SteamManager.Init();
                        var owner = SteamLobbyManager.Instance.GetLobbyOwner();
                        if (owner.m_SteamID != 0)
                        {
                            StartNetwork(false);
                            if (transport is SteamSocketsTransport st) st.ConnectTo(owner);
                        }
                    }
                    catch { }
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(10);
            if (useSteamLobby && transport is DuckovCoopMod.SteamSocketsTransport st2)
            {
                try
                {
                    var statuses = st2.GetQuickStatuses();
                    GUILayout.Label($"Steam Peers: {statuses.Count}");
                    foreach (var s in statuses)
                        GUILayout.Label($"- {s.id}  ping={s.ping}  state={s.state}");
                }
                catch { }
            }
            GUILayout.Space(10);

            if (!IsServer)
            {
                GUILayout.Label("LAN Hosts");

                if (hostList.Count == 0)
                {
                    GUILayout.Label("(Waiting for broadcast replies, no hosts yet)");
                }
                else
                {
                    // existing host list UI continues here (omitted for brevity)
                }

            }

            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        private MapSelectionEntry GetMapSelectionEntrylist(string SceneID)
        {
            const string keyword = "MapSelectionEntry";

            var trs = Object.FindObjectsByType<Transform>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            var gos = trs
                .Select(t => t.gameObject)
                .Where(go => go.name.IndexOf(keyword, System.StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            foreach (var i in gos)
            {
                try
                {
                    var map = i.GetComponentInChildren<MapSelectionEntry>();
                    if (map != null)
                    {
                        if (map.SceneID == SceneID)
                        {
                            return map;
                        }
                    }
                }
                catch { continue; }
            }
            return null;
        }

        private void DrawPlayerStatusWindow(int windowID)
        {
            if (GUI.Button(new Rect(playerStatusWindowRect.width - 25, 5, 20, 20), "x"))
            {
                showPlayerStatusWindow = false;
            }

            playerStatusScrollPos = GUILayout.BeginScrollView(playerStatusScrollPos, GUILayout.ExpandWidth(true));

            if (localPlayerStatus != null)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"ID: {localPlayerStatus.EndPoint}", GUILayout.Width(180));
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                GUILayout.Label($"Name: {localPlayerStatus.PlayerName}", GUILayout.Width(180));
                GUILayout.Label($"Latency: {localPlayerStatus.Latency}ms", GUILayout.Width(100));
                GUILayout.Label($"In Game: {(localPlayerStatus.IsInGame ? "Yes" : "No")}");
                GUILayout.EndHorizontal();
                GUILayout.Space(10);
            }

            if (IsServer)
            {
                foreach (var kvp in playerStatuses)
                {
                    var st2 = kvp.Value;
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"ID: {st2.EndPoint}", GUILayout.Width(180));
                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"Name: {st2.PlayerName}", GUILayout.Width(180));
                    GUILayout.Label($"Latency: {st2.Latency}ms", GUILayout.Width(100));
                    GUILayout.Label($"In Game: {(st2.IsInGame ? "Yes" : "No")}");
                    GUILayout.EndHorizontal();
                    GUILayout.Space(10);
                }
            }
            else
            {
                foreach (var kvp in clientPlayerStatuses)
                {
                    var st2 = kvp.Value;
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"ID: {st2.EndPoint}", GUILayout.Width(180));
                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"Name: {st2.PlayerName}", GUILayout.Width(180));
                    GUILayout.Label($"Latency: {st2.Latency}ms", GUILayout.Width(100));
                    GUILayout.Label($"In Game: {(st2.IsInGame ? "Yes" : "No")}");
                    GUILayout.EndHorizontal();
                    GUILayout.Space(10);
                }
            }

            GUILayout.EndScrollView();
            GUI.DragWindow();
        }
    }
}
