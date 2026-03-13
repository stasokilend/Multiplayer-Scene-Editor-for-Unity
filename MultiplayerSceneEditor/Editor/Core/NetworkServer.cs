using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace MultiplayerSceneEditor
{
    // ── Pending join request (main-thread visible) ────────────────────────────

    /// <summary>
    /// Represents a client that has sent a Handshake but is waiting for the
    /// host to explicitly accept or deny them.
    /// </summary>
    public class PendingJoin
    {
        public string   UserId;
        public string   DisplayName;
        public string   RemoteEndPoint;   // e.g. "192.168.1.42:51234"
        public double   ReceivedAt;
        internal PeerConn Peer;
    }

    /// <summary>
    /// TCP server — run by whoever chooses "Host" in the editor window.
    ///
    /// Join flow:
    ///   1. Client connects and sends Handshake.
    ///   2. Server queues a PendingJoin for the main thread and sends JoinPending to client.
    ///   3. Host calls ApproveJoin / DenyJoin from the main thread.
    /// </summary>
    public class NetworkServer : IDisposable
    {
        public bool IsRunning { get; private set; }
        public int  Port      { get; private set; }

        public ConcurrentQueue<(string fromUserId, Envelope env)> InboundQueue { get; }
            = new ConcurrentQueue<(string, Envelope)>();

        /// <summary>Clients waiting for host approval.  Drain on the main thread.</summary>
        public ConcurrentQueue<PendingJoin> PendingQueue { get; }
            = new ConcurrentQueue<PendingJoin>();

        private TcpListener                             _listener;
        private Thread                                  _acceptThread;
        private readonly List<PeerConn>                 _peers    = new List<PeerConn>();
        private readonly Dictionary<string, PeerConn>   _pending  = new Dictionary<string, PeerConn>();
        private readonly object                         _lock     = new object();
        private bool                                    _disposed;

        // ─────────────────────────────────────────────────────────────────────

        public void Start(int port)
        {
            Port      = port;
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start(16);
            IsRunning     = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "MSE-Accept" };
            _acceptThread.Start();
            Debug.Log($"[MSE Server] Listening on *:{port}");
        }

        public void Stop()
        {
            IsRunning = false;
            try { _listener?.Stop(); } catch { }
            lock (_lock)
            {
                foreach (var p in _peers)           p.Dispose();
                foreach (var kv in _pending)        kv.Value.Dispose();
                _peers.Clear();
                _pending.Clear();
            }
        }

        // ── Approval API (call from main thread) ──────────────────────────────

        public PeerConn ApprovePeer(string userId)
        {
            lock (_lock)
            {
                if (!_pending.TryGetValue(userId, out var peer)) return null;
                _pending.Remove(userId);
                _peers.Add(peer);
                return peer;
            }
        }

        public void DenyPeer(string userId, string reason = "The host declined your request.")
        {
            PeerConn peer;
            lock (_lock)
            {
                if (!_pending.TryGetValue(userId, out peer)) return;
                _pending.Remove(userId);
            }
            try
            {
                peer.Send(Protocol.Encode(
                    Envelope.Create(MsgType.JoinDenied, "server",
                        Protocol.Ser(new JoinDeniedPayload { reason = reason }))));
                Thread.Sleep(200);
            }
            catch { }
            peer.Dispose();
            Debug.Log($"[MSE Server] Denied {userId}: {reason}");
        }

        // ── Broadcast / Send ──────────────────────────────────────────────────

        public void Broadcast(Envelope env, string exceptUserId = null)
        {
            byte[] frame = Protocol.Encode(env);
            lock (_lock)
                foreach (var p in _peers)
                    if (p.UserId != exceptUserId)
                        p.Send(frame);
        }

        public void SendTo(string userId, Envelope env)
        {
            byte[] frame = Protocol.Encode(env);
            lock (_lock)
                foreach (var p in _peers)
                    if (p.UserId == userId) { p.Send(frame); break; }
        }

        public List<string> ConnectedUserIds()
        {
            lock (_lock)
            {
                var ids = new List<string>();
                foreach (var p in _peers) ids.Add(p.UserId);
                return ids;
            }
        }

        // ── Accept loop ───────────────────────────────────────────────────────

        private void AcceptLoop()
        {
            while (IsRunning)
            {
                try
                {
                    var tcp = _listener.AcceptTcpClient();
                    tcp.NoDelay = true;
                    var remote = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
                    Debug.Log($"[MSE Server] TCP connection from {remote} — awaiting handshake…");
                    var peer = new PeerConn(tcp, OnPeerMessage, OnPeerDisconnected);
                    peer.StartReading();
                }
                catch (SocketException) when (!IsRunning) { break; }
                catch (Exception ex) when (IsRunning)
                { Debug.LogWarning($"[MSE Server] AcceptLoop: {ex.Message}"); }
            }
        }

        // ── Peer callbacks (background thread) ───────────────────────────────

        private void OnPeerMessage(PeerConn peer, Envelope env)
        {
            if ((MsgType)env.type == MsgType.Handshake)
            {
                // Don't add to _peers yet; park in _pending
                var hs = Protocol.Des<HandshakePayload>(env.payload);
                peer.UserId      = hs.user.userId;
                peer.DisplayName = hs.user.displayName;

                lock (_lock) _pending[peer.UserId] = peer;

                // Tell client it's in the waiting room
                peer.Send(Protocol.Encode(
                    Envelope.Create(MsgType.JoinPending, "server", "{}")));

                PendingQueue.Enqueue(new PendingJoin
                {
                    UserId         = peer.UserId,
                    DisplayName    = peer.DisplayName,
                    RemoteEndPoint = peer.RemoteEndPoint,
                    Peer           = peer,
                });

                Debug.Log($"[MSE Server] Join request: \"{peer.DisplayName}\" from {peer.RemoteEndPoint}");
                return;
            }

            // Only process messages from approved peers
            bool approved;
            lock (_lock) approved = _peers.Contains(peer);
            if (approved)
                InboundQueue.Enqueue((peer.UserId, env));
        }

        private void OnPeerDisconnected(PeerConn peer)
        {
            bool wasPeer;
            lock (_lock)
            {
                wasPeer = _peers.Remove(peer);
                _pending.Remove(peer.UserId);
            }

            if (wasPeer)
            {
                var env = Envelope.Create(MsgType.UserLeft, peer.UserId,
                              Protocol.Ser(new UserInfo { userId = peer.UserId, displayName = peer.DisplayName }));
                InboundQueue.Enqueue((peer.UserId, env));
                Broadcast(env);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  One TCP peer connection
    // ──────────────────────────────────────────────────────────────────────────

    public class PeerConn : IDisposable
    {
        public string UserId        { get; set; } = "";
        public string DisplayName   { get; set; } = "Unknown";
        public string RemoteEndPoint => _tcp.Client?.RemoteEndPoint?.ToString() ?? "unknown";

        private readonly TcpClient                       _tcp;
        private readonly NetworkStream                   _stream;
        private readonly Action<PeerConn, Envelope>      _onMsg;
        private readonly Action<PeerConn>                _onDiscon;
        private readonly byte[]                          _readBuf  = new byte[65536];
        private          byte[]                          _stageBuf = new byte[65536];
        private          int                             _stageLen;
        private readonly ConcurrentQueue<byte[]>         _sendQ    = new ConcurrentQueue<byte[]>();
        private          Thread                          _readThread;
        private          Thread                          _sendThread;
        private          bool                            _disposed;

        public PeerConn(TcpClient tcp, Action<PeerConn, Envelope> onMsg, Action<PeerConn> onDiscon)
        {
            _tcp = tcp; _stream = tcp.GetStream(); _onMsg = onMsg; _onDiscon = onDiscon;
        }

        public void StartReading()
        {
            _readThread = new Thread(ReadLoop)  { IsBackground = true, Name = "MSE-Read" };
            _sendThread = new Thread(SendLoop)  { IsBackground = true, Name = "MSE-Send" };
            _readThread.Start();
            _sendThread.Start();
        }

        public void Send(byte[] frame) => _sendQ.Enqueue(frame);

        private void ReadLoop()
        {
            try
            {
                while (!_disposed)
                {
                    int n = _stream.Read(_readBuf, 0, _readBuf.Length);
                    if (n == 0) break;

                    EnsureCapacity(_stageLen + n);
                    Buffer.BlockCopy(_readBuf, 0, _stageBuf, _stageLen, n);
                    _stageLen += n;

                    int offset = 0;
                    while (true)
                    {
                        var env = Protocol.TryDecode(_stageBuf.Slice(offset), _stageLen - offset, out int consumed);
                        if (env == null) break;
                        offset += consumed;
                        _onMsg(this, env);
                    }
                    if (offset > 0)
                    {
                        _stageLen -= offset;
                        Buffer.BlockCopy(_stageBuf, offset, _stageBuf, 0, _stageLen);
                    }
                }
            }
            catch (Exception ex) when (!_disposed)
            { Debug.LogWarning($"[MSE Peer] Read error: {ex.Message}"); }
            finally { if (!_disposed) _onDiscon(this); Dispose(); }
        }

        private void SendLoop()
        {
            try
            {
                while (!_disposed)
                {
                    if (_sendQ.TryDequeue(out var frame)) _stream.Write(frame, 0, frame.Length);
                    else Thread.Sleep(1);
                }
            }
            catch { }
        }

        private void EnsureCapacity(int needed)
        {
            if (_stageBuf.Length < needed)
                Array.Resize(ref _stageBuf, Math.Max(_stageBuf.Length * 2, needed));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _tcp.Close(); } catch { }
        }
    }

    // ── byte[] slice helper ───────────────────────────────────────────────────
    internal static class ByteExt
    {
        public static byte[] Slice(this byte[] src, int offset)
        {
            if (offset == 0) return src;
            int len = src.Length - offset;
            if (len <= 0) return Array.Empty<byte>();
            var dst = new byte[len];
            Buffer.BlockCopy(src, offset, dst, 0, len);
            return dst;
        }
    }
}
