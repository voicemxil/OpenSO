using System;
using System.Buffers.Binary;
using System.Text;

namespace FSO.Common.Serialization
{
    public enum ByteOrder
    {
        BigEndian,
        LittleEndian
    }

    /// <summary>
    /// The byte-buffer surface the PDU/serialization layer is written against — a minimal,
    /// behavior-identical replacement for Mina.NET's IoBuffer (Java NIO semantics: capacity /
    /// position / limit, Flip to switch from writing to reading, switchable endianness applying
    /// to subsequent typed accesses, optional auto-expansion on writes). Only the members this
    /// codebase actually uses exist; IoBufferUtils supplies the rest as extensions.
    /// </summary>
    public class IoBuffer
    {
        private byte[] Buf;
        private int _Position;
        private int _Limit;

        public ByteOrder Order = ByteOrder.BigEndian;
        public bool AutoExpand;

        private IoBuffer(byte[] buf, int limit)
        {
            Buf = buf;
            _Limit = limit;
        }

        public static IoBuffer Allocate(int capacity)
        {
            return new IoBuffer(new byte[capacity], capacity);
        }

        public static IoBuffer Wrap(byte[] data)
        {
            return new IoBuffer(data, data.Length);
        }

        public int Position
        {
            get { return _Position; }
            set { _Position = value; }
        }

        public int Limit
        {
            get { return _Limit; }
        }

        public int Remaining
        {
            get { return _Limit - _Position; }
        }

        public bool HasRemaining
        {
            get { return Remaining > 0; }
        }

        /// <summary>Switch from writing to reading: limit = position, position = 0.</summary>
        public IoBuffer Flip()
        {
            _Limit = _Position;
            _Position = 0;
            return this;
        }

        public IoBuffer Clear()
        {
            _Position = 0;
            _Limit = Buf.Length;
            return this;
        }

        public IoBuffer Skip(int size)
        {
            if (_Position + size > _Limit) throw new InvalidOperationException("Buffer underflow (skip)");
            _Position += size;
            return this;
        }

        // ---- reads ----

        private void DemandReadable(int count)
        {
            if (_Position + count > _Limit) throw new InvalidOperationException(
                $"Buffer underflow: need {count} at {_Position}, limit {_Limit}");
        }

        public byte Get()
        {
            DemandReadable(1);
            return Buf[_Position++];
        }

        public void Get(byte[] destination, int offset, int length)
        {
            DemandReadable(length);
            Array.Copy(Buf, _Position, destination, offset, length);
            _Position += length;
        }

        public short GetInt16()
        {
            DemandReadable(2);
            var span = Buf.AsSpan(_Position, 2);
            _Position += 2;
            return (Order == ByteOrder.BigEndian)
                ? BinaryPrimitives.ReadInt16BigEndian(span)
                : BinaryPrimitives.ReadInt16LittleEndian(span);
        }

        public int GetInt32()
        {
            DemandReadable(4);
            var span = Buf.AsSpan(_Position, 4);
            _Position += 4;
            return (Order == ByteOrder.BigEndian)
                ? BinaryPrimitives.ReadInt32BigEndian(span)
                : BinaryPrimitives.ReadInt32LittleEndian(span);
        }

        public long GetInt64()
        {
            DemandReadable(8);
            var span = Buf.AsSpan(_Position, 8);
            _Position += 8;
            return (Order == ByteOrder.BigEndian)
                ? BinaryPrimitives.ReadInt64BigEndian(span)
                : BinaryPrimitives.ReadInt64LittleEndian(span);
        }

        public ushort GetUInt16()
        {
            return (ushort)GetInt16();
        }

        public uint GetUInt32()
        {
            return (uint)GetInt32();
        }

        public ulong GetUInt64()
        {
            return (ulong)GetInt64();
        }

        public float GetSingle()
        {
            DemandReadable(4);
            var span = Buf.AsSpan(_Position, 4);
            _Position += 4;
            return (Order == ByteOrder.BigEndian)
                ? BinaryPrimitives.ReadSingleBigEndian(span)
                : BinaryPrimitives.ReadSingleLittleEndian(span);
        }

        /// <summary>Read a fixed-size field; the string ends at the first NUL within it.</summary>
        public string GetString(int fieldSize, Encoding encoding)
        {
            DemandReadable(fieldSize);
            int end = _Position;
            int fieldEnd = _Position + fieldSize;
            while (end < fieldEnd && Buf[end] != 0) end++;
            var result = encoding.GetString(Buf, _Position, end - _Position);
            _Position = fieldEnd;
            return result;
        }

        /// <summary>Read to the first NUL (consumed) or the end of the buffer.</summary>
        public string GetString(Encoding encoding)
        {
            int end = _Position;
            while (end < _Limit && Buf[end] != 0) end++;
            var result = encoding.GetString(Buf, _Position, end - _Position);
            _Position = (end < _Limit) ? end + 1 : _Limit;
            return result;
        }

        /// <summary>The next `length` bytes as an independent buffer; this position advances.</summary>
        public IoBuffer GetSlice(int length)
        {
            DemandReadable(length);
            var copy = new byte[length];
            Array.Copy(Buf, _Position, copy, 0, length);
            _Position += length;
            return Wrap(copy);
        }

        // ---- writes ----

        private void DemandWritable(int count)
        {
            if (_Position + count <= _Limit) return;
            if (!AutoExpand) throw new InvalidOperationException(
                $"Buffer overflow: need {count} at {_Position}, limit {_Limit}");
            int newCapacity = Math.Max(Buf.Length, 16);
            while (newCapacity < _Position + count) newCapacity *= 2;
            if (newCapacity != Buf.Length) Array.Resize(ref Buf, newCapacity);
            _Limit = Buf.Length;
        }

        public IoBuffer Put(byte value)
        {
            DemandWritable(1);
            Buf[_Position++] = value;
            return this;
        }

        public IoBuffer Put(byte[] source)
        {
            return Put(source, 0, source.Length);
        }

        public IoBuffer Put(byte[] source, int offset, int length)
        {
            DemandWritable(length);
            Array.Copy(source, offset, Buf, _Position, length);
            _Position += length;
            return this;
        }

        /// <summary>Copy the source's remaining bytes; both positions advance.</summary>
        public IoBuffer Put(IoBuffer source)
        {
            int count = source.Remaining;
            DemandWritable(count);
            Array.Copy(source.Buf, source._Position, Buf, _Position, count);
            source._Position += count;
            _Position += count;
            return this;
        }

        public IoBuffer PutInt16(short value)
        {
            DemandWritable(2);
            var span = Buf.AsSpan(_Position, 2);
            if (Order == ByteOrder.BigEndian) BinaryPrimitives.WriteInt16BigEndian(span, value);
            else BinaryPrimitives.WriteInt16LittleEndian(span, value);
            _Position += 2;
            return this;
        }

        public IoBuffer PutInt32(int value)
        {
            DemandWritable(4);
            var span = Buf.AsSpan(_Position, 4);
            if (Order == ByteOrder.BigEndian) BinaryPrimitives.WriteInt32BigEndian(span, value);
            else BinaryPrimitives.WriteInt32LittleEndian(span, value);
            _Position += 4;
            return this;
        }

        public IoBuffer PutInt64(long value)
        {
            DemandWritable(8);
            var span = Buf.AsSpan(_Position, 8);
            if (Order == ByteOrder.BigEndian) BinaryPrimitives.WriteInt64BigEndian(span, value);
            else BinaryPrimitives.WriteInt64LittleEndian(span, value);
            _Position += 8;
            return this;
        }

        public IoBuffer PutSingle(float value)
        {
            DemandWritable(4);
            var span = Buf.AsSpan(_Position, 4);
            if (Order == ByteOrder.BigEndian) BinaryPrimitives.WriteSingleBigEndian(span, value);
            else BinaryPrimitives.WriteSingleLittleEndian(span, value);
            _Position += 4;
            return this;
        }

        /// <summary>Write the encoded string with no terminator or padding.</summary>
        public IoBuffer PutString(string value, Encoding encoding)
        {
            return Put(encoding.GetBytes(value ?? ""));
        }

        /// <summary>Write a fixed-size field: encoded bytes truncated or NUL-padded to fieldSize.</summary>
        public IoBuffer PutString(string value, int fieldSize, Encoding encoding)
        {
            DemandWritable(fieldSize);
            var bytes = encoding.GetBytes(value ?? "");
            int copy = Math.Min(bytes.Length, fieldSize);
            Array.Copy(bytes, 0, Buf, _Position, copy);
            Array.Clear(Buf, _Position + copy, fieldSize - copy);
            _Position += fieldSize;
            return this;
        }
    }
}
