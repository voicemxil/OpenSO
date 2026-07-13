using FSO.SimAntics.NetPlay.Model;
using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace FSO.Client.Network.Sandbox
{
    /// <summary>
    /// One sandbox-mode connection (either end): a TcpClient with an exact-read read loop and an
    /// ordered write loop speaking the sandbox frame — LE uint32 type (unused, 0), LE uint32
    /// payload size, then one VMNetMessageType byte + data. Replaces the Mina connector/acceptor
    /// sessions the sandbox lot server and client used.
    /// </summary>
    public class SandboxSession
    {
        private const uint MaxFrameSize = 128 * 1024 * 1024;

        private readonly TcpClient Tcp;
        private readonly NetworkStream Stream;
        private readonly Action<SandboxSession, VMNetMessage> OnMessage;
        private readonly Action<SandboxSession> OnClosed;
        private readonly Channel<object> WriteQueue = Channel.CreateUnbounded<object>(
            new UnboundedChannelOptions { SingleReader = true });
        private int Closed;

        /// <summary>The VMNetClient this session belongs to (server side).</summary>
        public object Tag;
        public EndPoint RemoteEndPoint { get; private set; }

        public bool Connected
        {
            get { return Closed == 0; }
        }

        public SandboxSession(TcpClient tcp, Action<SandboxSession, VMNetMessage> onMessage,
                              Action<SandboxSession> onClosed)
        {
            Tcp = tcp;
            Stream = tcp.GetStream();
            OnMessage = onMessage;
            OnClosed = onClosed;
            try { RemoteEndPoint = tcp.Client.RemoteEndPoint; } catch { }
        }

        public void Start()
        {
            _ = Task.Run(WriteLoop);
            _ = Task.Run(ReadLoop);
        }

        /// <summary>Queue a VMNetMessage or object[] of them; each batch sends contiguously.</summary>
        public void Write(object message)
        {
            WriteQueue.Writer.TryWrite(message);
        }

        /// <summary>immediately=false flushes queued writes first, matching Mina Close(false).</summary>
        public void Close(bool immediately)
        {
            if (immediately) Teardown();
            else WriteQueue.Writer.TryComplete();
        }

        private static byte[] Encode(VMNetMessage msg)
        {
            var frame = new byte[8 + 1 + msg.Data.Length];
            // uint32 packet type (always 0, never consumed) + uint32 payload size, little-endian
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4, 4), (uint)(msg.Data.Length + 1));
            frame[8] = (byte)msg.Type;
            msg.Data.CopyTo(frame, 9);
            return frame;
        }

        private async Task ReadLoop()
        {
            var header = new byte[8];
            try
            {
                while (true)
                {
                    await Stream.ReadExactlyAsync(header, 0, header.Length);
                    uint payloadSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
                    if (payloadSize < 1 || payloadSize > MaxFrameSize)
                        throw new IOException("Bad sandbox frame size: " + payloadSize);

                    var payload = new byte[payloadSize];
                    await Stream.ReadExactlyAsync(payload, 0, (int)payloadSize);

                    var data = new byte[payloadSize - 1];
                    Array.Copy(payload, 1, data, 0, data.Length);
                    var message = new VMNetMessage((VMNetMessageType)payload[0], data);
                    try { OnMessage(this, message); } catch { }
                }
            }
            catch
            {
                Teardown();
            }
        }

        private async Task WriteLoop()
        {
            try
            {
                while (await WriteQueue.Reader.WaitToReadAsync())
                {
                    while (WriteQueue.Reader.TryRead(out var item))
                    {
                        if (item is object[] batch)
                        {
                            foreach (var m in batch)
                            {
                                if (m is VMNetMessage inner)
                                {
                                    var f = Encode(inner);
                                    await Stream.WriteAsync(f, 0, f.Length);
                                }
                            }
                        }
                        else if (item is VMNetMessage msg)
                        {
                            var frame = Encode(msg);
                            await Stream.WriteAsync(frame, 0, frame.Length);
                        }
                    }
                }
                Teardown(); // graceful close: queue completed and flushed
            }
            catch
            {
                Teardown();
            }
        }

        private void Teardown()
        {
            if (Interlocked.Exchange(ref Closed, 1) != 0) return;
            WriteQueue.Writer.TryComplete();
            try { Stream?.Close(); } catch { }
            try { Tcp?.Close(); } catch { }
            try { OnClosed?.Invoke(this); } catch { }
        }
    }
}
