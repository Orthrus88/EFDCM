using System;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;

namespace DuckovCoopMod
{
    internal sealed class LiteNetLibTransport : ITransport, INetEventListener
    {
        private readonly NetDataWriter _writer = new NetDataWriter();
        private readonly Dictionary<string, NetPeer> _peerById = new Dictionary<string, NetPeer>();
        private readonly HashSet<string> _peers = new HashSet<string>();
        private NetManager _net;
        public bool IsServer { get; private set; }

        public event Action<string> OnPeerConnected;
        public event Action<string> OnPeerDisconnected;
        public event Action<string, byte[]> OnData;

        public IEnumerable<string> Peers => _peers;

        public void StartServer(int port)
        {
            Stop(); IsServer = true;
            _net = new NetManager(this) { BroadcastReceiveEnabled = true };
            _net.Start(port);
        }

        public void StartClient()
        {
            Stop(); IsServer = false;
            _net = new NetManager(this) { BroadcastReceiveEnabled = true };
            _net.Start();
        }

        public void Stop()
        {
            try { _net?.Stop(); } catch { }
            _net = null; _peers.Clear(); _peerById.Clear();
        }

        public void Broadcast(byte[] data, bool reliable)
        {
            if (_net == null) return;
            _writer.Reset(); _writer.Put(data);
            _net.SendToAll(_writer, reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);
        }

        public void Send(string peerId, byte[] data, bool reliable)
        {
            if (_net == null) return;
            if (!_peerById.TryGetValue(peerId, out var p) || p == null) return;
            _writer.Reset(); _writer.Put(data);
            p.Send(_writer, reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);
        }

        string MakeId(NetPeer p) => p?.EndPoint?.ToString() ?? string.Empty;

        void INetEventListener.OnPeerConnected(NetPeer peer)
        {
            var id = MakeId(peer); if (string.IsNullOrEmpty(id)) return;
            _peerById[id] = peer; _peers.Add(id);
            OnPeerConnected?.Invoke(id);
        }

        void INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            var id = MakeId(peer); if (string.IsNullOrEmpty(id)) return;
            _peerById.Remove(id); _peers.Remove(id);
            OnPeerDisconnected?.Invoke(id);
        }

        void INetEventListener.OnNetworkError(System.Net.IPEndPoint endPoint, System.Net.Sockets.SocketError socketError) { }
        void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            // Forward raw payload
            try
            {
                var id = MakeId(peer);
                var buf = reader.GetRemainingBytes();
                OnData?.Invoke(id, buf);
            }
            finally { reader.Recycle(); }
        }
        void INetEventListener.OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
        void INetEventListener.OnConnectionRequest(ConnectionRequest request) { request.AcceptIfKey(null); }
        void INetEventListener.OnNetworkReceiveUnconnected(System.Net.IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }
    }
}
