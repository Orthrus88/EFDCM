using System;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;

namespace DuckovCoopMod
{
    internal sealed class SteamSocketsTransport : ITransport
    {
        private Callback<SteamNetConnectionStatusChangedCallback_t> _cbStatus;
        public bool IsServer { get; private set; }
        public event Action<string> OnPeerConnected;
        public event Action<string> OnPeerDisconnected;
        public event Action<string, byte[]> OnData;

        private HSteamListenSocket _listen;
        private readonly Dictionary<string, HSteamNetConnection> _connById = new Dictionary<string, HSteamNetConnection>();
        private readonly Dictionary<HSteamNetConnection, string> _idByConn = new Dictionary<HSteamNetConnection, string>();

        private class Pump : MonoBehaviour
        {
            public SteamSocketsTransport owner;
            private IntPtr[] _msgPtrs = new IntPtr[32];
            void Update()
            {
                if (owner == null) return;
                foreach (var kv in owner._idByConn)
                {
                    int received = SteamNetworkingSockets.ReceiveMessagesOnConnection(kv.Key, _msgPtrs, _msgPtrs.Length);
                    for (int i = 0; i < received; i++)
                    {
                        var msg = System.Runtime.InteropServices.Marshal.PtrToStructure<SteamNetworkingMessage_t>(_msgPtrs[i]);
                        try
                        {
                            var data = new byte[msg.m_cbSize];
                            System.Runtime.InteropServices.Marshal.Copy(msg.m_pData, data, 0, msg.m_cbSize);
                            owner.OnData?.Invoke(kv.Value, data);
                        }
                        catch { }
                        finally { SteamNetworkingMessage_t.Release(_msgPtrs[i]); }
                    }
                }
            }
        }

        public IEnumerable<string> Peers => _connById.Keys;


        public struct PeerQuickStatus { public string id; public int ping; public ESteamNetworkingConnectionState state; }

        public System.Collections.Generic.List<PeerQuickStatus> GetQuickStatuses()
        {
            var list = new System.Collections.Generic.List<PeerQuickStatus>();
            try
            {
                foreach (var kv in _connById)
                {
                    var conn = kv.Value; var id = kv.Key;
                    SteamNetConnectionInfo_t info;
                    SteamNetworkingSockets.GetConnectionInfo(conn, out info);
                    list.Add(new PeerQuickStatus { id = id, ping = -1, state = (ESteamNetworkingConnectionState)info.m_eState });
                }
            }
            catch { }
            return list;
        }

        public void StartServer(int port)
        {
            Stop(); IsServer = true;
            SteamManager.Init();
            var addr = new SteamNetworkingIPAddr();
            addr.Clear(); addr.m_port = (ushort)port;
            _listen = SteamNetworkingSockets.CreateListenSocketP2P(0, 0, null);
            if (_listen.m_HSteamListenSocket == 0)
                Debug.LogWarning("[SteamNet] CreateListenSocketP2P failed");
            AttachPump();
            if (_cbStatus == null) _cbStatus = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnStatusChanged);
        }

        public void StartClient()
        {
            Stop(); IsServer = false;
            SteamManager.Init();
            AttachPump();
            if (_cbStatus == null) _cbStatus = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnStatusChanged);
        }

        public void Stop()
        {
            foreach (var kv in _connById) { try { SteamNetworkingSockets.CloseConnection(kv.Value, 0, "stop", false); } catch { } }
            _connById.Clear(); _idByConn.Clear();
            if (_listen.m_HSteamListenSocket != 0) { try { SteamNetworkingSockets.CloseListenSocket(_listen); } catch { } _listen = default; }
        }

        public void Broadcast(byte[] data, bool reliable)
        {
            foreach (var id in Peers) Send(id, data, reliable);
        }

        public void Send(string peerId, byte[] data, bool reliable)
        {
            if (!_connById.TryGetValue(peerId, out var conn)) return;
            try
            {
                int flags = reliable ? 8 /* k_ESteamNetworkingSend_Reliable */ : 1 /* k_ESteamNetworkingSend_Unreliable */;
                unsafe
                {
                    fixed (byte* p = data)
                    {
                        SteamNetworkingSockets.SendMessageToConnection(conn, (IntPtr)p, (uint)data.Length, flags, out _);
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning("[SteamNet] Send exception: " + e.Message); }
        }

        private void AttachPump()
        {
            try
            {
                var go = new GameObject("SteamSocketsPump");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<Pump>().owner = this;
            }
            catch { }
        }

        // Accept and connect helpers
        public void Accept(HSteamNetConnection conn)
        {
            var info = new SteamNetConnectionInfo_t();
            SteamNetworkingSockets.GetConnectionInfo(conn, out info);
            var id = info.m_identityRemote.GetSteamID().ToString();
            _connById[id] = conn; _idByConn[conn] = id;
            SteamNetworkingSockets.AcceptConnection(conn);
            #if DEBUG
            UnityEngine.Debug.Log("[SteamNet] Connected: " + id);
            #endif
            OnPeerConnected?.Invoke(id);
        }

        public void ConnectTo(CSteamID remote)
        {
            var iden = new SteamNetworkingIdentity(); iden.SetSteamID(remote);
            var conn = SteamNetworkingSockets.ConnectP2P(ref iden, 0, 0, null);
            var peerId = remote.ToString();
            _connById[peerId] = conn; _idByConn[conn] = peerId;
            #if DEBUG
            UnityEngine.Debug.Log("[SteamNet] Connected: " + peerId);
            #endif
            OnPeerConnected?.Invoke(peerId);
        }

        private void OnStatusChanged(SteamNetConnectionStatusChangedCallback_t ev)
        {
            try
            {
                var conn = ev.m_hConn;
                var state = (ESteamNetworkingConnectionState)ev.m_info.m_eState;
                if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting)
                {
                    if (IsServer)
                    {
                        #if DEBUG
                        UnityEngine.Debug.Log("[SteamNet] Incoming connection -> accepting");
                        #endif
                        Accept(conn);
                    }
                    else
                    {
                        #if DEBUG
                        UnityEngine.Debug.Log("[SteamNet] Connecting (client)");
                        #endif
                    }
                }
                else if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected)
                {
                    var info = ev.m_info; var remote = info.m_identityRemote.GetSteamID(); var id = remote.ToString();
                    if (!_connById.ContainsKey(id))
                    {
                        _connById[id] = conn;
                        _idByConn[conn] = id;
                        #if DEBUG
                        UnityEngine.Debug.Log("[SteamNet] Connected: " + id);
                        #endif
                        OnPeerConnected?.Invoke(id);
                    }
                }
                else if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer ||
                         state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally)
                {
                    if (_idByConn.TryGetValue(conn, out var id))
                    {
                        _idByConn.Remove(conn);
                        _connById.Remove(id);
                        #if DEBUG
                        UnityEngine.Debug.Log("[SteamNet] Disconnected: " + id);
                        #endif
                        OnPeerDisconnected?.Invoke(id);
                    }
                    SteamNetworkingSockets.CloseConnection(conn, 0, "closed", false);
                }
            }
            catch (Exception e) { Debug.LogWarning("[SteamNet] StatusChanged: " + e.Message); }
        }
    }
}





