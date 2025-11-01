using System;
using System.Collections.Generic;

namespace DuckovCoopMod
{
    internal interface ITransport
    {
        bool IsServer { get; }
        IEnumerable<string> Peers { get; }

        void StartServer(int port);
        void StartClient();
        void Stop();

        void Broadcast(byte[] data, bool reliable);
        void Send(string peerId, byte[] data, bool reliable);

        event Action<string> OnPeerConnected;
        event Action<string> OnPeerDisconnected;
        event Action<string, byte[]> OnData;
    }
}

