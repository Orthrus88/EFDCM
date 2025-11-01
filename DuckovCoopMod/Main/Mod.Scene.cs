using System;
using System.Collections;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DuckovCoopMod
{
    public partial class ModBehaviour
    {
        // Scene vote/state
        public bool allowLocalSceneLoad = false;
        public bool sceneVoteActive = false;
        private string sceneTargetId = null;   // SceneID
        private string sceneCurtainGuid = null;   // GUID
        private bool sceneNotifyEvac = false;
        private bool sceneSaveToFile = true;

        // Participants and ready map (pid -> ready)
        private readonly List<string> sceneParticipantIds = new List<string>();
        private readonly Dictionary<string, bool> sceneReady = new Dictionary<string, bool>();
        private bool localReady = false;
        private readonly KeyCode readyKey = KeyCode.J;

        // Optional location metadata
        private bool sceneUseLocation = false;
        private string sceneLocationName = null;

        private string _sceneReadySidSent; // last ready sid sent
        public bool IsMapSelectionEntry = false;

        // Scene Gate
        private volatile bool _cliSceneGateReleased = false;
        private string _cliGateSid = null;
        private float _cliGateDeadline = 0f;
        private float _cliGateSeverDeadline = 0f;
        private string _srvGateSid = null;
        private readonly HashSet<string> _srvGateReadyPids = new HashSet<string>();

        // ----- Host: collect participant ids in same scene -----
        private List<string> BuildParticipantIds_Server()
        {
            var list = new List<string>();
            string hostSceneId = null;
            ComputeIsInGame(out hostSceneId);
            var hostPid = GetPlayerId(null);
            if (!string.IsNullOrEmpty(hostPid)) list.Add(hostPid);

            foreach (var kv in playerStatuses)
            {
                var peer = kv.Key; if (peer == null) continue;
                string peerScene = null;
                if (!_srvPeerScene.TryGetValue(peer, out peerScene))
                    peerScene = kv.Value?.SceneId;

                if (!string.IsNullOrEmpty(hostSceneId) && !string.IsNullOrEmpty(peerScene))
                {
                    if (peerScene == hostSceneId)
                    {
                        var pid = GetPlayerId(peer);
                        if (!string.IsNullOrEmpty(pid)) list.Add(pid);
                    }
                }
                else
                {
                    var pid = GetPlayerId(peer);
                    if (!string.IsNullOrEmpty(pid)) list.Add(pid);
                }
            }
            return list;
        }

        private IEnumerable<NetPeer> Server_EnumPeersInSameSceneAsHost()
        {
            string hostSceneId = localPlayerStatus != null ? localPlayerStatus.SceneId : null;
            if (string.IsNullOrEmpty(hostSceneId)) ComputeIsInGame(out hostSceneId);
            if (string.IsNullOrEmpty(hostSceneId)) yield break;
            foreach (var p in netManager.ConnectedPeerList)
            {
                string peerScene = null;
                if (!_srvPeerScene.TryGetValue(p, out peerScene) && playerStatuses.TryGetValue(p, out var pst))
                    peerScene = pst.SceneId;
                if (!string.IsNullOrEmpty(peerScene) && peerScene == hostSceneId)
                    yield return p;
            }
        }

        // ----- Host: begin vote -----
        public void Host_BeginSceneVote_Simple(string targetSceneId, string curtainGuid,
                                               bool notifyEvac, bool saveToFile,
                                               bool useLocation, string locationName)
        {
            sceneTargetId = targetSceneId ?? "";
            sceneCurtainGuid = string.IsNullOrEmpty(curtainGuid) ? null : curtainGuid;
            sceneNotifyEvac = notifyEvac;
            sceneSaveToFile = saveToFile;
            sceneUseLocation = useLocation;
            sceneLocationName = locationName ?? "";

            sceneParticipantIds.Clear();
            sceneParticipantIds.AddRange(BuildParticipantIds_Server());

            sceneVoteActive = true;
            localReady = false;
            sceneReady.Clear();
            foreach (var pid in sceneParticipantIds) sceneReady[pid] = false;

            string hostSceneId = null; ComputeIsInGame(out hostSceneId); hostSceneId = hostSceneId ?? string.Empty;

            var w = new NetDataWriter();
            w.Put((byte)Op.SCENE_VOTE_START);
            w.Put((byte)2);
            w.Put(sceneTargetId);

            bool hasCurtain = !string.IsNullOrEmpty(sceneCurtainGuid);
            byte flags = PackFlags(hasCurtain, sceneUseLocation, sceneNotifyEvac, sceneSaveToFile);
            w.Put(flags);

            if (hasCurtain) w.Put(sceneCurtainGuid);
            w.Put(sceneLocationName);
            w.Put(hostSceneId);

            w.Put(sceneParticipantIds.Count);
            foreach (var pid in sceneParticipantIds) w.Put(pid);

            TransportBroadcast(w, true);
            Debug.Log($"[SCENE] Vote started v2: target='{sceneTargetId}', hostScene='{hostSceneId}', loc='{sceneLocationName}', count={sceneParticipantIds.Count}");
        }

        // ----- Host: someone toggled ready -----
        private void Server_OnSceneReadySet(NetPeer fromPeer, bool ready)
        {
            if (!IsServer) return;
            string pid = (fromPeer != null) ? GetPlayerId(fromPeer) : GetPlayerId(null);
            if (!sceneVoteActive) return;
            if (!sceneReady.ContainsKey(pid)) return;
            sceneReady[pid] = ready;

            var w = new NetDataWriter();
            w.Put((byte)Op.SCENE_READY_SET);
            w.Put(pid);
            w.Put(ready);
            TransportBroadcast(w, true);

            foreach (var id in sceneParticipantIds)
                if (!sceneReady.TryGetValue(id, out bool r) || !r) return;
            Server_BroadcastBeginSceneLoad();
        }

        private void Server_BroadcastBeginSceneLoad()
        {
            if (_spectatorActive && _spectatorEndOnVotePending)
            {
                _spectatorEndOnVotePending = false;
                EndSpectatorAndShowClosure();
            }

            var w = new NetDataWriter();
            w.Put((byte)Op.SCENE_BEGIN_LOAD);
            w.Put((byte)1);
            w.Put(sceneTargetId ?? "");

            bool hasCurtain = !string.IsNullOrEmpty(sceneCurtainGuid);
            w.Put(PackFlags(hasCurtain, sceneUseLocation, sceneNotifyEvac, sceneSaveToFile));
            if (hasCurtain) w.Put(sceneCurtainGuid);
            w.Put(sceneLocationName ?? "");

            TransportBroadcast(w, true);
        }

        // ----- Client: handle vote start -----
        private void Client_OnSceneVoteStart(NetPacketReader r)
        {
            if (!EnsureAvailable(r, 2)) { Debug.LogWarning("[SCENE] vote: header too short"); return; }
            byte ver = r.GetByte();
            if (ver != 2) { Debug.LogWarning($"[SCENE] vote: unsupported ver={ver}"); return; }

            if (!TryGetString(r, out sceneTargetId)) { Debug.LogWarning("[SCENE] vote: bad sceneId"); return; }
            if (!EnsureAvailable(r, 1)) { Debug.LogWarning("[SCENE] vote: no flags"); return; }
            byte flags = r.GetByte();
            bool hasCurtain, useLoc, notifyEvac, saveToFile;
            UnpackFlags(flags, out hasCurtain, out useLoc, out notifyEvac, out saveToFile);

            string curtainGuid = null;
            if (hasCurtain)
            {
                if (!TryGetString(r, out curtainGuid)) { Debug.LogWarning("[SCENE] vote: bad curtain"); return; }
            }
            if (!TryGetString(r, out var locName)) { Debug.LogWarning("[SCENE] vote: bad location"); return; }
            if (!TryGetString(r, out var hostSceneId)) { Debug.LogWarning("[SCENE] vote: bad hostSceneId"); return; }
            if (!EnsureAvailable(r, 4)) { Debug.LogWarning("[SCENE] vote: no count"); return; }
            int cnt = r.GetInt();
            sceneParticipantIds.Clear();
            for (int i = 0; i < cnt; i++)
            {
                if (!TryGetString(r, out var pid)) { Debug.LogWarning($"[SCENE] vote: bad pid[{i}]"); return; }
                sceneParticipantIds.Add(pid);
            }

            string mySceneId = null; ComputeIsInGame(out mySceneId); mySceneId = mySceneId ?? string.Empty;
            if (!string.IsNullOrEmpty(hostSceneId) && !string.IsNullOrEmpty(mySceneId))
            {
                if (!string.Equals(hostSceneId, mySceneId, StringComparison.Ordinal))
                {
                    Debug.Log($"[SCENE] vote: ignore (diff scene) host='{hostSceneId}' me='{mySceneId}'");
                    return;
                }
            }

            if (sceneParticipantIds.Count > 0 && localPlayerStatus != null)
            {
                var me = localPlayerStatus.EndPoint ?? string.Empty;
                if (!sceneParticipantIds.Contains(me))
                {
                    Debug.Log($"[SCENE] vote: ignore (not in participants) me='{me}'");
                    return;
                }
            }

            sceneCurtainGuid = curtainGuid;
            sceneUseLocation = useLoc;
            sceneNotifyEvac = notifyEvac;
            sceneSaveToFile = saveToFile;
            sceneLocationName = locName ?? "";

            sceneVoteActive = true;
            localReady = false;
            sceneReady.Clear();
            foreach (var pid in sceneParticipantIds) sceneReady[pid] = false;

            Debug.Log($"[SCENE] Vote received v{ver}: target='{sceneTargetId}', hostScene='{hostSceneId}', myScene='{mySceneId}', players={cnt}");
        }

        // ----- Client: ready toggle -----
        private void Client_SendReadySet(bool ready)
        {
            if (IsServer || connectedPeer == null) return;
            var w = new NetDataWriter();
            w.Put((byte)Op.SCENE_READY_SET);
            w.Put(ready);
            TransportSendToServer(w, true);

            if (sceneVoteActive && localPlayerStatus != null)
            {
                var me = localPlayerStatus.EndPoint ?? string.Empty;
                if (!string.IsNullOrEmpty(me) && sceneReady.ContainsKey(me)) sceneReady[me] = ready;
            }
        }

        // ----- Client: begin load -----
        private void Client_OnBeginSceneLoad(NetPacketReader r)
        {
            if (!EnsureAvailable(r, 2)) { Debug.LogWarning("[SCENE] begin: header too short"); return; }
            byte ver = r.GetByte(); if (ver != 1) { Debug.LogWarning($"[SCENE] begin: unsupported ver={ver}"); return; }
            if (!TryGetString(r, out var id)) { Debug.LogWarning("[SCENE] begin: bad sceneId"); return; }
            if (!EnsureAvailable(r, 1)) { Debug.LogWarning("[SCENE] begin: no flags"); return; }
            byte flags = r.GetByte();
            bool hasCurtain, useLoc, notifyEvac, saveToFile; UnpackFlags(flags, out hasCurtain, out useLoc, out notifyEvac, out saveToFile);
            string curtainGuid = null; if (hasCurtain) { if (!TryGetString(r, out curtainGuid)) { Debug.LogWarning("[SCENE] begin: bad curtain"); return; } }
            if (!TryGetString(r, out var locName)) { Debug.LogWarning("[SCENE] begin: bad locName"); return; }

            allowLocalSceneLoad = true;
            var map = GetMapSelectionEntrylist(sceneTargetId);
            if (map != null && sceneLocationName == "OnPointerClick")
            {
                IsMapSelectionEntry = false;
                allowLocalSceneLoad = false;
                Call_NotifyEntryClicked_ByInvoke(MapSelectionView.Instance, map, null);
            }
            else
            {
                TryPerformSceneLoad_Local(sceneTargetId, sceneCurtainGuid, sceneNotifyEvac, sceneSaveToFile, sceneUseLocation, sceneLocationName);
            }

            sceneVoteActive = false; sceneParticipantIds.Clear(); sceneReady.Clear(); localReady = false;
        }

        private void TryPerformSceneLoad_Local(string targetSceneId, string curtainGuid, bool notifyEvac, bool save, bool useLocation, string locationName)
        {
            try
            {
                bool launched = false;
                foreach (var ii in GameObject.FindObjectsOfType<SceneLoaderProxy>())
                {
                    try
                    {
                        if (Traverse.Create(ii).Field<string>("sceneID").Value == targetSceneId)
                        { ii.LoadScene(); launched = true; Debug.Log($"[SCENE] Fallback via SceneLoaderProxy -> {targetSceneId}"); break; }
                    }
                    catch (Exception e) { Debug.LogWarning("[SCENE] proxy check failed: " + e); }
                }
                if (!launched) Debug.LogWarning($"[SCENE] Local load fallback failed: no proxy for '{targetSceneId}'");
            }
            catch (Exception e) { Debug.LogWarning("[SCENE] Local load failed: " + e); }
            finally
            {
                allowLocalSceneLoad = false;
                if (networkStarted) { if (IsServer) SendPlayerStatusUpdate(); else SendClientStatusUpdate(); }
            }
        }

        private Eflatun.SceneReference.SceneReference TryResolveCurtain(string guid)
        { if (string.IsNullOrEmpty(guid)) return null; return null; }

        static byte PackFlags(bool hasCurtain, bool useLoc, bool notifyEvac, bool saveToFile)
        {
            byte f = 0; if (hasCurtain) f |= 1 << 0; if (useLoc) f |= 1 << 1; if (notifyEvac) f |= 1 << 2; if (saveToFile) f |= 1 << 3; return f;
        }
        static void UnpackFlags(byte f, out bool hasCurtain, out bool useLoc, out bool notifyEvac, out bool saveToFile)
        { hasCurtain = (f & (1 << 0)) != 0; useLoc = (f & (1 << 1)) != 0; notifyEvac = (f & (1 << 2)) != 0; saveToFile = (f & (1 << 3)) != 0; }

        static bool TryGetString(NetPacketReader r, out string s) { try { s = r.GetString(); return true; } catch { s = null; return false; } }
        static bool EnsureAvailable(NetPacketReader r, int need) => r.AvailableBytes >= need;

        private bool ComputeIsInGame(out string sceneId)
        {
            sceneId = null;
            var lm = LevelManager.Instance; if (lm == null || lm.MainCharacter == null) return false;
            try
            {
                var core = Duckov.Scenes.MultiSceneCore.Instance;
                if (core != null)
                {
                    var active = SceneManager.GetActiveScene();
                    if (active.IsValid())
                    {
                        var idFromBuild = SceneInfoCollection.GetSceneID(active.buildIndex);
                        if (!string.IsNullOrEmpty(idFromBuild)) sceneId = idFromBuild; else sceneId = active.name;
                    }
                }
            }
            catch { }
            if (string.IsNullOrEmpty(sceneId)) sceneId = SceneInfoCollection.BaseSceneID;
            return !string.IsNullOrEmpty(sceneId);
        }

        private void TrySendSceneReadyOnce()
        {
            if (!networkStarted) return;
            if (!ComputeIsInGame(out var sid) || string.IsNullOrEmpty(sid)) return;
            if (_sceneReadySidSent == sid) return;
            _sceneReadySidSent = sid;

            var w = new NetDataWriter();
            w.Put((byte)Op.SCENE_READY);
            w.Put(localPlayerStatus != null ? localPlayerStatus.EndPoint : "");
            w.Put(sid);
            try
            {
                var m = LevelManager.Instance;
                var pos = m ? m.MainCharacter.transform.position : Vector3.zero;
                var rot = m ? m.MainCharacter.transform.rotation : Quaternion.identity;
                w.PutVector3(pos); w.PutQuaternion(rot);
            }
            catch { w.PutVector3(Vector3.zero); w.PutQuaternion(Quaternion.identity); }
            w.Put(SerializeFace());

            if (transport != null) transport.Broadcast(w.CopyData(), true);
            else netManager?.SendToAll(w, DeliveryMethod.ReliableOrdered);
        }

        public UniTask AppendSceneGate(UniTask original)
        {
            return Internal();
            async UniTask Internal()
            {
                await original;
                try
                {
                    if (!networkStarted) return;
                    await Client_SceneGateAsync();
                }
                catch (System.Exception e) { Debug.LogError("[SCENE-GATE] " + e); }
            }
        }

        public async UniTask Client_SceneGateAsync()
        {
            if (!networkStarted || IsServer) return;
            float connectDeadline = Time.realtimeSinceStartup + 8f;
            while (connectedPeer == null && Time.realtimeSinceStartup < connectDeadline)
                await UniTask.Delay(100);

            _cliSceneGateReleased = false;
            string sid = _cliGateSid; if (string.IsNullOrEmpty(sid)) sid = TryGuessActiveSceneId(); _cliGateSid = sid;

            if (connectedPeer != null)
            {
                writer.Reset();
                writer.Put((byte)Op.SCENE_GATE_READY);
                writer.Put(localPlayerStatus != null ? localPlayerStatus.EndPoint : "");
                writer.Put(sid ?? "");
                TransportSendToServer(writer, true);
            }

            float retryDeadline = Time.realtimeSinceStartup + 5f;
            while (connectedPeer == null && Time.realtimeSinceStartup < retryDeadline)
            {
                await UniTask.Delay(200);
                if (connectedPeer != null)
                {
                    writer.Reset();
                    writer.Put((byte)Op.SCENE_GATE_READY);
                    writer.Put(localPlayerStatus != null ? localPlayerStatus.EndPoint : "");
                    writer.Put(sid ?? "");
                    TransportSendToServer(writer, true);
                    break;
                }
            }

            _cliGateDeadline = Time.realtimeSinceStartup + 100f;
            while (!_cliSceneGateReleased && Time.realtimeSinceStartup < _cliGateDeadline)
            {
                try { SceneLoader.LoadingComment = "[Coop] Waiting for host to finish loading... (Auto enter after 100s if stuck)"; } catch { }
                await UniTask.Delay(100);
            }

            Client_ReportSelfHealth_IfReadyOnce();
            try { SceneLoader.LoadingComment = "Host finished, entering..."; } catch { }
        }

        public async UniTask Server_SceneGateAsync()
        {
            if (!IsServer || !networkStarted) return;
            _srvGateSid = TryGuessActiveSceneId();
            _cliGateSeverDeadline = Time.realtimeSinceStartup + 15f;
            while (Time.realtimeSinceStartup < _cliGateSeverDeadline)
                await UniTask.Delay(100);

            if (playerStatuses != null && playerStatuses.Count > 0)
            {
                foreach (var kv in playerStatuses)
                {
                    var peer = kv.Key; var st2 = kv.Value; if (peer == null || st2 == null) continue;
                    if (_srvGateReadyPids.Contains(st2.EndPoint)) Server_SendGateRelease(peer, _srvGateSid);
                }
            }
        }

        private void Server_SendGateRelease(NetPeer peer, string sid)
        {
            if (peer == null) return; var w = new NetDataWriter(); w.Put((byte)Op.SCENE_GATE_RELEASE); w.Put(sid ?? ""); TransportSend(peer, w, true);
        }

        private string TryGuessActiveSceneId() => sceneTargetId;
    }
}

