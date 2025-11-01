using LiteNetLib;
using LiteNetLib.Utils;

namespace DuckovCoopMod
{
    public partial class ModBehaviour
    {
        private byte[] BuildPayload(NetDataWriter w) { return w.CopyData(); }

        private void TransportBroadcast(NetDataWriter w, bool reliable)
        {
            if (transport != null) transport.Broadcast(BuildPayload(w), reliable);
            else netManager?.SendToAll(w, reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);
        }

        private void TransportSend(NetPeer p, NetDataWriter w, bool reliable)
        {
            if (transport != null)
            {
                var id = p?.EndPoint?.ToString() ?? string.Empty;
                if (!string.IsNullOrEmpty(id)) transport.Send(id, BuildPayload(w), reliable);
            }
            else p?.Send(w, reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);
        }

        private void TransportSendToServer(NetDataWriter w, bool reliable)
        {
            if (transport != null)
            {
                if (!string.IsNullOrEmpty(_serverPeerId))
                    transport.Send(_serverPeerId, w.CopyData(), reliable);
                else
                    transport.Broadcast(w.CopyData(), reliable);
            }
            else connectedPeer?.Send(w, reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);
        }

        private void TransportBroadcastMethod(NetDataWriter w, DeliveryMethod m)
        {
            if (transport != null) transport.Broadcast(w.CopyData(), m == DeliveryMethod.ReliableOrdered);
            else netManager?.SendToAll(w, m);
        }
    }
}
