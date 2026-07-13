using FSO.Common.Serialization;
using FSO.Server.Common;
using FSO.Server.Protocol.Aries;
using FSO.Server.Protocol.Voltron.Packets;
using Ninject;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace FSO.Server.Clients
{
    public interface IAriesMessageSubscriber
    {
        void MessageReceived(AriesClient client, object message);
    }

    public interface IAriesEventSubscriber
    {
        void SessionCreated(AriesClient client);
        void SessionOpened(AriesClient client);
        void SessionClosed(AriesClient client);
        void SessionIdle(AriesClient client);
        void InputClosed(AriesClient session);
    }

    /// <summary>
    /// Client end of an Aries connection (the game's city/lot connections, and lot->city links
    /// between servers). A plain TcpClient with one async read loop (frames are length-prefixed,
    /// so reads are exact) and one ordered write loop, speaking the wire format via AriesFraming.
    ///
    /// Behavior contract, matching the Mina.NET connector this replaces: SessionCreated then
    /// SessionOpened fire on successful connect (subscribers may Write from inside them); a
    /// connect failure or timeout fires SessionClosed; a remote half-close fires InputClosed then
    /// SessionClosed; SessionClosed fires exactly once per connection; a connection superseded by
    /// a newer Connect dies silently; Write is non-blocking, callable from any thread, and each
    /// call's packets go out contiguously in order; subscriber exceptions don't kill the
    /// connection. SessionIdle is never fired (the old client never configured an idle timer).
    /// </summary>
    public class AriesClient
    {
        private const int ConnectTimeoutMs = 10000;
        private const uint MaxFrameSize = 128 * 1024 * 1024; // sanity cap; largest legit frames (lot states) are a few MB

        private IKernel Kernel;
        private volatile Session Current;

        private List<IAriesMessageSubscriber> MessageSubscribers = new List<IAriesMessageSubscriber>();
        private List<IAriesEventSubscriber> EventSubscribers = new List<IAriesEventSubscriber>();

        public AriesClient(IKernel kernel)
        {
            this.Kernel = kernel;
        }

        /// <summary>Per-connection state; a newer Connect abandons the old instance.</summary>
        private class Session
        {
            public TcpClient Tcp;
            public NetworkStream Stream;
            public readonly Channel<object> WriteQueue = Channel.CreateUnbounded<object>(
                new UnboundedChannelOptions { SingleReader = true });
            public readonly DateTime CreationTime = DateTime.Now;
            public volatile bool Abandoned; // superseded by a newer Connect: fire no events
            public volatile bool Opened;
            public int Closed;              // interlocked once-guard for teardown + SessionClosed
        }

        public void AddSubscriber(object sub)
        {
            lock (EventSubscribers)
            {
                if (sub is IAriesEventSubscriber)
                {
                    EventSubscribers.Add((IAriesEventSubscriber)sub);
                }
                if (sub is IAriesMessageSubscriber)
                {
                    MessageSubscribers.Add((IAriesMessageSubscriber)sub);
                }
            }
        }

        public void RemoveSubscriber(object sub)
        {
            lock (EventSubscribers)
            {
                if (sub is IAriesEventSubscriber)
                {
                    EventSubscribers.Remove((IAriesEventSubscriber)sub);
                }
                if (sub is IAriesMessageSubscriber)
                {
                    MessageSubscribers.Remove((IAriesMessageSubscriber)sub);
                }
            }
        }

        public void Connect(string address)
        {
            Connect(IPEndPointUtils.CreateIPEndPoint(address));
        }

        public void Connect(IPEndPoint target)
        {
            Session old;
            lock (this)
            {
                old = Current;
                Current = null;
            }
            if (old != null)
            {
                // a superseded connection (pending or established) dies without firing events
                old.Abandoned = true;
                Close(old);
            }

            var session = new Session();
            lock (this) Current = session;
            Task.Run(() => Run(session, target));
        }

        /// <summary>Graceful disconnect: queued writes flush, then the socket closes.</summary>
        public void Disconnect()
        {
            var session = Current;
            if (session != null && session.Opened)
            {
                session.WriteQueue.Writer.TryComplete();
            }
        }

        /// <summary>
        /// Queue packets for sending. Non-blocking; the batch is written contiguously in order.
        /// Silently dropped when not connected (as before).
        /// </summary>
        public void Write(params object[] packets)
        {
            var session = Current;
            if (session != null && session.Opened && session.Closed == 0)
            {
                session.WriteQueue.Writer.TryWrite(packets);
            }
        }

        public bool IsConnected
        {
            get
            {
                var session = Current;
                return session != null && session.Opened && session.Closed == 0;
            }
        }

        public IPEndPoint RemoteEndPoint
        {
            get
            {
                try { return Current?.Tcp?.Client?.RemoteEndPoint as IPEndPoint; }
                catch { return null; } // socket already disposed
            }
        }

        private async Task Run(Session session, IPEndPoint target)
        {
            try
            {
                session.Tcp = new TcpClient(target.AddressFamily);
                session.Tcp.NoDelay = true;
                using (var timeout = new CancellationTokenSource(ConnectTimeoutMs))
                {
                    await session.Tcp.ConnectAsync(target.Address, target.Port, timeout.Token);
                }
                if (session.Abandoned || session.Closed != 0)
                {
                    Close(session);
                    return;
                }

                session.Stream = session.Tcp.GetStream();
                session.Opened = true;
                FireEvent(session, s => s.SessionCreated(this));
                FireEvent(session, s => s.SessionOpened(this));

                var context = Kernel.Get<ISerializationContext>();
                _ = Task.Run(() => WriteLoop(session, context));
                await ReadLoop(session, context);
            }
            catch (EndOfStreamException)
            {
                // remote half-closed cleanly at a frame boundary
                FireEvent(session, s => s.InputClosed(this));
                Close(session);
            }
            catch
            {
                Close(session);
            }
        }

        private async Task ReadLoop(Session session, ISerializationContext context)
        {
            var header = new byte[AriesFraming.HeaderSize];
            var messages = new List<object>();
            while (true)
            {
                await session.Stream.ReadExactlyAsync(header, 0, header.Length);
                uint packetType = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
                // header[4..8] is the sender's timestamp; nothing consumes it
                uint payloadSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
                if (payloadSize > MaxFrameSize) throw new IOException("Aries frame too large: " + payloadSize);

                var payload = new byte[payloadSize];
                if (payloadSize > 0) await session.Stream.ReadExactlyAsync(payload, 0, (int)payloadSize);

                messages.Clear();
                AriesFraming.Decode(packetType, payload, context, messages);
                foreach (var message in messages)
                {
                    // flush pending writes then close THIS session, as before (Mina Close(false));
                    // the subscriber still receives the PDU below
                    if (message is ServerByePDU) session.WriteQueue.Writer.TryComplete();
                    DispatchMessage(message);
                }
            }
        }

        private async Task WriteLoop(Session session, ISerializationContext context)
        {
            try
            {
                while (await session.WriteQueue.Reader.WaitToReadAsync())
                {
                    while (session.WriteQueue.Reader.TryRead(out var item))
                    {
                        uint timestamp = (uint)(DateTime.Now - session.CreationTime).TotalMilliseconds;
                        if (item is object[] batch)
                        {
                            foreach (var message in batch) await WriteMessage(session, message, context, timestamp);
                        }
                        else
                        {
                            await WriteMessage(session, item, context, timestamp);
                        }
                    }
                }
                // queue completed (graceful Disconnect) and fully flushed
                Close(session);
            }
            catch
            {
                Close(session);
            }
        }

        private async Task WriteMessage(Session session, object message, ISerializationContext context, uint timestamp)
        {
            var frame = AriesFraming.Encode(message, context, timestamp);
            if (frame != null) await session.Stream.WriteAsync(frame, 0, frame.Length);
        }

        /// <summary>Tear down once: close the socket, and fire SessionClosed unless abandoned.</summary>
        private void Close(Session session)
        {
            if (Interlocked.Exchange(ref session.Closed, 1) != 0) return;
            session.WriteQueue.Writer.TryComplete();
            try { session.Stream?.Close(); } catch { }
            try { session.Tcp?.Close(); } catch { }
            lock (this)
            {
                if (Current == session) Current = null;
            }
            FireEvent(session, s => s.SessionClosed(this));
        }

        private void FireEvent(Session session, Action<IAriesEventSubscriber> evt)
        {
            if (session.Abandoned) return;
            List<IAriesEventSubscriber> subs;
            lock (EventSubscribers)
                subs = new List<IAriesEventSubscriber>(EventSubscribers);
            foreach (var sub in subs)
            {
                try { evt(sub); } catch { }
            }
        }

        private void DispatchMessage(object message)
        {
            List<IAriesMessageSubscriber> subs;
            lock (EventSubscribers)
                subs = new List<IAriesMessageSubscriber>(MessageSubscribers);
            foreach (var sub in subs)
            {
                try { sub.MessageReceived(this, message); } catch { }
            }
        }
    }
}
