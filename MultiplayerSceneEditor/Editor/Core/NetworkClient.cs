using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace MultiplayerSceneEditor
{
    public enum ClientState { Idle, Connecting, WaitingApproval, Connected, Denied, Disconnected }

    /// <summary>
    /// TCP client — used by everyone who joins a session hosted by another editor.
    /// </summary>
    public class NetworkClient : IDisposable
    {
        public ClientState State        { get; private set; } = ClientState.Idle;
        public string      LocalUserId  { get; private set; }
        public string      DenialReason { get; private set; }

        public bool IsConnected => State == ClientState.Connected;

        public ConcurrentQueue<Envelope> InboundQueue { get; } = new ConcurrentQueue<Envelope>();

        private TcpClient     _tcp;
        private NetworkStream _stream;
        private Thread        _readThread;
        private Thread        _sendThread;
        private readonly ConcurrentQueue<byte[]> _sendQ = new ConcurrentQueue<byte[]>();
        private bool          _disposed;

        private byte[] _readBuf  = new byte[65536];
        private byte[] _stageBuf = new byte[65536];
        private int    _stageLen;

        // ── Connect ───────────────────────────────────────────────────────────

        public void Connect(string host, int port, UserInfo localUser)
        {
            LocalUserId = localUser.userId;
            State       = ClientState.Connecting;

            _tcp = new TcpClient();
            _tcp.NoDelay = true;

            try
            {
                _tcp.Connect(host, port);
            }
            catch (SocketException ex)
            {
                State = ClientState.Disconnected;
                throw new Exception(
                    $"Could not connect to {host}:{port}.\n\n" +
                    $"Possible causes:\n" +
                    $"  • Wrong IP address or port\n" +
                    $"  • Host's firewall is blocking port {port}\n" +
                    $"  • Host is not running MSE or the session hasn't started yet\n\n" +
                    $"Technical: {ex.Message}", ex);
            }

            _stream     = _tcp.GetStream();

            _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "MSE-ClientRead" };
            _sendThread = new Thread(SendLoop) { IsBackground = true, Name = "MSE-ClientSend" };
            _readThread.Start();
            _sendThread.Start();

            // Send handshake — host will queue us as PendingJoin
            Send(Envelope.Create(MsgType.Handshake, localUser.userId,
                     Protocol.Ser(new HandshakePayload { user = localUser })));

            State = ClientState.WaitingApproval;
            Debug.Log($"[MSE Client] Connected to {host}:{port} — waiting for host approval…");
        }

        public void Disconnect()
        {
            if (State == ClientState.Idle) return;
            State = ClientState.Disconnected;
            try { _tcp?.Close(); } catch { }
        }

        // ── Send ─────────────────────────────────────────────────────────────

        public void Send(Envelope env) => _sendQ.Enqueue(Protocol.Encode(env));

        // ── Loops ─────────────────────────────────────────────────────────────

        private void ReadLoop()
        {
            try
            {
                while (State != ClientState.Disconnected && State != ClientState.Denied)
                {
                    int n = _stream.Read(_readBuf, 0, _readBuf.Length);
                    if (n == 0) break;

                    EnsureCapacity(_stageLen + n);
                    Buffer.BlockCopy(_readBuf, 0, _stageBuf, _stageLen, n);
                    _stageLen += n;

                    int offset = 0;
                    while (true)
                    {
                        // Zero-allocation decode: offset+length, no Slice()
                        var env = Protocol.TryDecode(_stageBuf, offset, _stageLen - offset, out int consumed);
                        if (env == null) break;
                        offset += consumed;

                        var type = (MsgType)env.type;

                        if (type == MsgType.JoinPending)
                        {
                            State = ClientState.WaitingApproval;
                            InboundQueue.Enqueue(env);
                            continue;
                        }

                        if (type == MsgType.JoinDenied)
                        {
                            var p = Protocol.Des<JoinDeniedPayload>(env.payload);
                            DenialReason = p.reason;
                            State        = ClientState.Denied;
                            InboundQueue.Enqueue(env);
                            Debug.LogWarning($"[MSE Client] Join denied: {p.reason}");
                            return;
                        }

                        if (type == MsgType.HandshakeAck)
                            State = ClientState.Connected;

                        InboundQueue.Enqueue(env);
                    }
                    if (offset > 0)
                    {
                        _stageLen -= offset;
                        if (_stageLen > 0)
                            Buffer.BlockCopy(_stageBuf, offset, _stageBuf, 0, _stageLen);
                    }
                }
            }
            catch (Exception ex) when (!_disposed && State == ClientState.Connected)
            {
                Debug.LogWarning($"[MSE Client] Connection lost: {ex.Message}");
            }
            finally
            {
                if (State == ClientState.Connected)
                {
                    State = ClientState.Disconnected;
                    InboundQueue.Enqueue(Envelope.Create(MsgType.UserLeft, LocalUserId ?? "",
                        Protocol.Ser(new UserInfo { userId = LocalUserId ?? "" })));
                }
            }
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
            Disconnect();
        }
    }
}
