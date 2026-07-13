using FSO.Common.Serialization;
using FSO.Server.Common;
using FSO.Server.Protocol.Aries;
using NLog;
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace FSO.Server.Framework.Aries
{
    /// <summary>Callbacks AbstractAriesServer receives from the socket transport.</summary>
    public interface IAriesSocketHandler
    {
        void SessionCreated(AriesTransportSession session);
        void SessionOpened(AriesTransportSession session);
        void MessageReceived(AriesTransportSession session, object message);
        void SessionClosed(AriesTransportSession session);
    }

    /// <summary>
    /// One accepted server-side connection: the surface AriesSession previously consumed from
    /// Mina's IoSession (ordered writes, graceful/immediate close, an attribute bag that session
    /// migration copies across connections, remote endpoint), on a TcpClient with one exact-read
    /// read loop and one channel-drained write loop speaking AriesFraming.
    /// </summary>
    public class AriesTransportSession
    {
        private static Logger LOG = LogManager.GetCurrentClassLogger();
        private const uint MaxFrameSize = 128 * 1024 * 1024;

        private readonly TcpClient Tcp;
        private readonly Stream Stream;
        private readonly ISerializationContext Context;
        private readonly IAriesSocketHandler Handler;
        private readonly IPacketLogger PacketLogger;
        private readonly ConcurrentDictionary<string, object> Attributes = new ConcurrentDictionary<string, object>();
        private readonly Channel<object> WriteQueue = Channel.CreateUnbounded<object>(
            new UnboundedChannelOptions { SingleReader = true });

        public readonly DateTime CreationTime = DateTime.Now;
        private int Closed;
        private volatile bool _Connected = true;

        public AriesTransportSession(TcpClient tcp, Stream stream, ISerializationContext context,
                                     IAriesSocketHandler handler, IPacketLogger packetLogger)
        {
            Tcp = tcp;
            Stream = stream;
            Context = context;
            Handler = handler;
            PacketLogger = packetLogger;
            try { RemoteEndPoint = Tcp.Client.RemoteEndPoint; } catch { }
        }

        public bool Connected
        {
            get { return _Connected; }
        }

        public EndPoint RemoteEndPoint { get; private set; }

        public object GetAttribute(string key)
        {
            Attributes.TryGetValue(key, out var value);
            return value;
        }

        public T GetAttribute<T>(string key)
        {
            var value = GetAttribute(key);
            return (value is T typed) ? typed : default;
        }

        public void SetAttribute(string key, object value)
        {
            Attributes[key] = value;
        }

        /// <summary>Queue one message for sending, in order. Dropped if the session is closed.</summary>
        public void Write(object message)
        {
            WriteQueue.Writer.TryWrite(message);
        }

        /// <summary>
        /// immediately=false flushes queued writes first (Mina Close(false)); immediately=true
        /// tears the socket down now.
        /// </summary>
        public void Close(bool immediately)
        {
            if (immediately) Teardown();
            else WriteQueue.Writer.TryComplete();
        }

        /// <summary>Fire created/opened and start the IO loops. Called once by the acceptor.</summary>
        public void Start()
        {
            Handler.SessionCreated(this);
            Handler.SessionOpened(this);
            _ = Task.Run(WriteLoop);
            _ = Task.Run(ReadLoop);
        }

        private async Task ReadLoop()
        {
            var header = new byte[AriesFraming.HeaderSize];
            var messages = new List<object>();
            try
            {
                while (true)
                {
                    await Stream.ReadExactlyAsync(header, 0, header.Length);
                    uint packetType = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
                    uint payloadSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
                    if (payloadSize > MaxFrameSize) throw new IOException("Aries frame too large: " + payloadSize);

                    var payload = new byte[payloadSize];
                    if (payloadSize > 0) await Stream.ReadExactlyAsync(payload, 0, (int)payloadSize);

                    if (PacketLogger != null) LogFrame(PacketDirection.INPUT, packetType, payload);

                    messages.Clear();
                    AriesFraming.Decode(packetType, payload, Context, messages);
                    foreach (var message in messages)
                    {
                        Dispatch(message);
                    }
                }
            }
            catch
            {
                Teardown();
            }
        }

        /// <summary>
        /// Handler exception policy carried over from the old IoHandler.ExceptionCaught: socket
        /// errors and invalid-operation close the session, anything else logs and the session
        /// lives on.
        /// </summary>
        private void Dispatch(object message)
        {
            try
            {
                Handler.MessageReceived(this, message);
            }
            catch (SocketException)
            {
                Teardown();
            }
            catch (InvalidOperationException ex)
            {
                LOG.Error(ex, "CRITICAL: " + ex.ToString());
                Teardown();
            }
            catch (Exception ex)
            {
                LOG.Error(ex, "Unknown error: " + ex.ToString());
            }
        }

        private async Task WriteLoop()
        {
            try
            {
                while (await WriteQueue.Reader.WaitToReadAsync())
                {
                    while (WriteQueue.Reader.TryRead(out var message))
                    {
                        uint timestamp = (uint)(DateTime.Now - CreationTime).TotalMilliseconds;
                        var frame = AriesFraming.Encode(message, Context, timestamp);
                        if (frame == null) continue;
                        if (PacketLogger != null)
                        {
                            var payload = new byte[frame.Length - AriesFraming.HeaderSize];
                            Array.Copy(frame, AriesFraming.HeaderSize, payload, 0, payload.Length);
                            LogFrame(PacketDirection.OUTPUT, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(0, 4)), payload);
                        }
                        await Stream.WriteAsync(frame, 0, frame.Length);
                    }
                }
                // queue completed (graceful close) and fully flushed
                Teardown();
            }
            catch
            {
                Teardown();
            }
        }

        private void Teardown()
        {
            if (Interlocked.Exchange(ref Closed, 1) != 0) return;
            _Connected = false;
            WriteQueue.Writer.TryComplete();
            try { Stream?.Close(); } catch { }
            try { Tcp?.Close(); } catch { }
            try { Handler.SessionClosed(this); }
            catch (Exception ex) { LOG.Error(ex); }
        }

        /// <summary>
        /// Network-debugger packet capture, matching the old AriesProtocolLogger: Voltron frames
        /// (type 0) log each sub-packet's payload; every other frame logs whole as ARIES.
        /// </summary>
        private void LogFrame(PacketDirection direction, uint packetType, byte[] payload)
        {
            try
            {
                if (packetType == 0)
                {
                    int offset = 0;
                    while (offset + 6 <= payload.Length)
                    {
                        ushort voltronType = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(offset, 2));
                        int size = (int)(BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(offset + 2, 4)) - 6);
                        var data = new byte[size];
                        Array.Copy(payload, offset + 6, data, 0, size);
                        PacketLogger.OnPacket(new Packet
                        {
                            Data = data,
                            Type = PacketType.VOLTRON,
                            SubType = voltronType,
                            Direction = direction
                        });
                        offset += size + 6;
                    }
                }
                else
                {
                    PacketLogger.OnPacket(new Packet
                    {
                        Data = payload,
                        Type = PacketType.ARIES,
                        SubType = packetType,
                        Direction = direction
                    });
                }
            }
            catch (Exception ex)
            {
                LOG.Error(ex, "Packet logger error");
            }
        }

        public override string ToString()
        {
            return "(TCP " + (RemoteEndPoint?.ToString() ?? "unknown") + ")";
        }
    }

    /// <summary>
    /// Accepts Aries connections on one endpoint, optionally terminating TLS (which Mina.NET
    /// never managed — its broken SslFilter is why production ran in the plain).
    /// </summary>
    public class AriesSocketServer
    {
        private static Logger LOG = LogManager.GetCurrentClassLogger();

        private readonly IPEndPoint Binding;
        private readonly X509Certificate2 Certificate;
        private readonly IAriesSocketHandler Handler;
        private readonly ISerializationContext Context;
        private readonly IPacketLogger PacketLogger;
        private TcpListener Listener;
        private volatile bool Stopped;

        public AriesSocketServer(IPEndPoint binding, X509Certificate2 certificate,
                                 IAriesSocketHandler handler, ISerializationContext context,
                                 IPacketLogger packetLogger)
        {
            Binding = binding;
            Certificate = certificate;
            Handler = handler;
            Context = context;
            PacketLogger = packetLogger;
        }

        public void Start()
        {
            Listener = new TcpListener(Binding);
            Listener.Start();
            _ = Task.Run(AcceptLoop);
        }

        /// <summary>Stop accepting. Existing sessions stay up (shutdown closes them itself).</summary>
        public void Stop()
        {
            Stopped = true;
            try { Listener?.Stop(); } catch { }
        }

        private async Task AcceptLoop()
        {
            while (!Stopped)
            {
                TcpClient tcp;
                try
                {
                    tcp = await Listener.AcceptTcpClientAsync();
                }
                catch
                {
                    if (Stopped) return;
                    continue;
                }
                _ = Task.Run(() => SetupSession(tcp));
            }
        }

        private async Task SetupSession(TcpClient tcp)
        {
            try
            {
                tcp.NoDelay = true;
                Stream stream = tcp.GetStream();
                if (Certificate != null)
                {
                    var ssl = new SslStream(stream, false);
                    await ssl.AuthenticateAsServerAsync(Certificate, false, SslProtocols.Tls12 | SslProtocols.Tls13, false);
                    stream = ssl;
                }
                new AriesTransportSession(tcp, stream, Context, Handler, PacketLogger).Start();
            }
            catch (Exception ex)
            {
                LOG.Error(ex, "Error establishing session from " + tcp.Client?.RemoteEndPoint);
                try { tcp.Close(); } catch { }
            }
        }
    }
}
