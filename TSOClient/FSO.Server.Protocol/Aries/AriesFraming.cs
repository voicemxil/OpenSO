using FSO.Common.Serialization;
using FSO.Server.Protocol.Electron;
using FSO.Server.Protocol.Gluon;
using FSO.Server.Protocol.Voltron;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace FSO.Server.Protocol.Aries
{
    /// <summary>
    /// The Aries wire format as plain encode/decode helpers (byte-identical to the retired Mina
    /// codec): a little-endian 12 byte frame header (type, timestamp, payload size) whose payload
    /// is either one raw little-endian Aries packet or a run of big-endian Voltron/Electron/Gluon
    /// sub-packets (uint16 type + uint32 size(+6) each). IoBuffer appears only as the
    /// serialization surface the PDU classes are written against.
    /// </summary>
    public static class AriesFraming
    {
        public const int HeaderSize = 12;

        /// <summary>
        /// Encode one message (IVoltronPacket/IElectronPacket/IGluonPacket/IAriesPacket) into a
        /// complete frame. Returns null for message types the old encoder silently ignored.
        /// </summary>
        public static byte[] Encode(object message, ISerializationContext context, uint timestamp)
        {
            if (message is IVoltronPacket voltron)
                return EncodeVoltronStyle(AriesPacketType.Voltron, voltron.GetPacketType().GetPacketCode(), voltron, context, timestamp);
            if (message is IElectronPacket electron)
                return EncodeVoltronStyle(AriesPacketType.Electron, electron.GetPacketType().GetPacketCode(), electron, context, timestamp);
            if (message is IGluonPacket gluon)
                return EncodeVoltronStyle(AriesPacketType.Gluon, gluon.GetPacketType().GetPacketCode(), gluon, context, timestamp);
            if (message is IAriesPacket aries)
                return EncodeAries(aries, context, timestamp);
            return null;
        }

        /// <summary>
        /// Decode one complete frame payload (the frame header already consumed by the caller)
        /// into packet instances. Unknown packet types are skipped, matching the old decoder.
        /// </summary>
        public static void Decode(uint packetType, byte[] payload, ISerializationContext context, IList<object> output)
        {
            if (packetType == AriesPacketType.Voltron.GetPacketCode())
            {
                DecodeVoltronStyle(payload, context, output, VoltronPackets.GetByPacketCode);
            }
            else if (packetType == AriesPacketType.Electron.GetPacketCode())
            {
                DecodeVoltronStyle(payload, context, output, ElectronPackets.GetByPacketCode);
            }
            else if (packetType == AriesPacketType.Gluon.GetPacketCode())
            {
                DecodeVoltronStyle(payload, context, output, GluonPackets.GetByPacketCode);
            }
            else
            {
                var packetClass = AriesPackets.GetByPacketCode(packetType);
                if (packetClass != null)
                {
                    var packet = (IAriesPacket)Activator.CreateInstance(packetClass);
                    var io = IoBuffer.Wrap(payload);
                    io.Order = ByteOrder.LittleEndian;
                    packet.Deserialize(io, context);
                    output.Add(packet);
                }
                // unknown Aries type: the whole payload was already consumed with the frame
            }
        }

        private static byte[] EncodeAries(IAriesPacket packet, ISerializationContext context, uint timestamp)
        {
            var payload = IoBuffer.Allocate(128);
            payload.Order = ByteOrder.LittleEndian;
            payload.AutoExpand = true;
            packet.Serialize(payload, context);
            payload.Flip();

            int payloadSize = payload.Remaining;
            var frame = new byte[HeaderSize + payloadSize];
            WriteFrameHeader(frame, packet.GetPacketType().GetPacketCode(), timestamp, (uint)payloadSize);
            if (payloadSize > 0) payload.Get(frame, HeaderSize, payloadSize);
            return frame;
        }

        private static byte[] EncodeVoltronStyle(AriesPacketType ariesType, ushort packetType, IoBufferSerializable message, ISerializationContext context, uint timestamp)
        {
            var payload = IoBuffer.Allocate(512);
            payload.Order = ByteOrder.BigEndian;
            payload.AutoExpand = true;
            message.Serialize(payload, context);
            payload.Flip();

            int payloadSize = payload.Remaining;
            var frame = new byte[HeaderSize + 6 + payloadSize];
            WriteFrameHeader(frame, ariesType.GetPacketCode(), timestamp, (uint)payloadSize + 6);
            // inner Voltron-style header is big-endian: uint16 type, uint32 size (includes the 6)
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(HeaderSize, 2), packetType);
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(HeaderSize + 2, 4), (uint)payloadSize + 6);
            if (payloadSize > 0) payload.Get(frame, HeaderSize + 6, payloadSize);
            return frame;
        }

        private static void WriteFrameHeader(byte[] frame, uint packetType, uint timestamp, uint payloadSize)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(0, 4), packetType);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4, 4), timestamp);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8, 4), payloadSize);
        }

        private static void DecodeVoltronStyle(byte[] payload, ISerializationContext context, IList<object> output, Func<ushort, Type> typeResolver)
        {
            var buffer = IoBuffer.Wrap(payload);
            buffer.Order = ByteOrder.BigEndian;
            long remaining = payload.Length;
            while (remaining > 0)
            {
                ushort type = buffer.GetUInt16();
                uint innerPayloadSize = buffer.GetUInt32() - 6;

                byte[] data = new byte[(int)innerPayloadSize];
                buffer.Get(data, 0, (int)innerPayloadSize);

                var packetClass = typeResolver(type);
                if (packetClass != null)
                {
                    // inner wrap keeps IoBuffer's default (big-endian) order, as before
                    var packet = (IoBufferDeserializable)Activator.CreateInstance(packetClass);
                    packet.Deserialize(IoBuffer.Wrap(data), context);
                    output.Add(packet);
                }

                remaining -= innerPayloadSize + 6;
            }
        }
    }
}
