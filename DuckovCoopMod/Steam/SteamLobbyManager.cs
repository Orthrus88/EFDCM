using System;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;

namespace DuckovCoopMod
{
    internal sealed class SteamLobbyManager
    {
        public static SteamLobbyManager Instance { get; private set; } = new SteamLobbyManager();

        public CSteamID CurrentLobby { get; private set; }
        public bool InLobby => CurrentLobby.m_SteamID != 0;

        // Lobby data keys
        const string KEY_MOD = "duckov_coop";
        const string KEY_VERSION = "version";
        const string KEY_PORT = "port";

        private Callback<LobbyCreated_t> _cbLobbyCreated;
        private Callback<LobbyEnter_t> _cbLobbyEntered;
        private Callback<GameLobbyJoinRequested_t> _cbJoinRequested;

        private SteamLobbyManager()
        {
            if (!SteamManager.Initialized) SteamManager.Init();
            if (SteamManager.Initialized)
            {
                _cbLobbyCreated = Callback<LobbyCreated_t>.Create(OnLobbyCreated);
                _cbLobbyEntered = Callback<LobbyEnter_t>.Create(OnLobbyEntered);
                _cbJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnGameLobbyJoinRequested);
            }
        }

        public void CreateLobby(int maxMembers, string version, int port)
        {
            if (!SteamManager.Initialized) { Debug.LogWarning("[Steam] Not initialized"); return; }
            SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, maxMembers);
            _pendingVersion = version; _pendingPort = port;
        }

        public void JoinLobby(CSteamID lobbyId)
        {
            if (!SteamManager.Initialized) return;
            SteamMatchmaking.JoinLobby(lobbyId);
        }

        public List<CSteamID> FindLobbies(int max = 20)
        {
            var list = new List<CSteamID>();
            if (!SteamManager.Initialized) return list;
            // Lightweight: query friend lobbies only by scanning friends' games
            int friendCount = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            for (int i = 0; i < friendCount && list.Count < max; i++)
            {
                var fid = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                var lobby = SteamMatchmaking.GetLobbyOwner(fid);
                // Fallback: rely on join request callback or explicit invite flows
            }
            return list;
        }

        private string _pendingVersion;
        private int _pendingPort;

        void OnLobbyCreated(LobbyCreated_t ev)
        {
            if (ev.m_eResult != EResult.k_EResultOK)
            {
                Debug.LogWarning("[Steam] CreateLobby failed: " + ev.m_eResult);
                return;
            }
            CurrentLobby = new CSteamID(ev.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyData(CurrentLobby, KEY_MOD, "1");
            SteamMatchmaking.SetLobbyData(CurrentLobby, KEY_VERSION, _pendingVersion ?? "");
            SteamMatchmaking.SetLobbyData(CurrentLobby, KEY_PORT, _pendingPort.ToString());
            SteamMatchmaking.SetLobbyJoinable(CurrentLobby, true);
            Debug.Log("[Steam] Lobby created: " + CurrentLobby);
        }

        void OnLobbyEntered(LobbyEnter_t ev)
        {
            CurrentLobby = new CSteamID(ev.m_ulSteamIDLobby);
            Debug.Log("[Steam] Lobby entered: " + CurrentLobby);
            // Auto-connect client to lobby owner via Steam sockets if enabled in mod
            try
            {
                var owner = SteamMatchmaking.GetLobbyOwner(CurrentLobby);
                var mod = DuckovCoopMod.ModBehaviour.Instance;
                if (mod != null && mod.useSteamLobby && !mod.IsServer && owner.m_SteamID != 0) { if(mod.transport==null){ try{ mod.StartNetwork(false);} catch{} }
                    if (mod.transport is DuckovCoopMod.SteamSocketsTransport st)
                    {
                        st.ConnectTo(owner);
                        Debug.Log("[Steam] Auto-connect to lobby owner: " + owner);
                    }
                }
            }
            catch { }
        }

        void OnGameLobbyJoinRequested(GameLobbyJoinRequested_t ev)
        {
            Debug.Log("[Steam] Join requested: " + ev.m_steamIDLobby);
            JoinLobby(ev.m_steamIDLobby);
        }

        public string GetLobbyPort()
        {
            if (!InLobby) return null;
            return SteamMatchmaking.GetLobbyData(CurrentLobby, KEY_PORT);
        }

        public CSteamID GetLobbyOwner()
        {
            return InLobby ? SteamMatchmaking.GetLobbyOwner(CurrentLobby) : default;
        }
    }
}

