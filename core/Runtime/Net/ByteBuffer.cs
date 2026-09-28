using System;
using System.Text;

namespace Tailed.Core.Net
{
    /// <summary>Little-endian growable byte writer for network messages (engine-agnostic).</summary>
    public sealed class ByteWriter
    {
        byte[] _buf;
        public int Length { get; private set; }

        public ByteWriter(int capacity = 256) => _buf = new byte[capacity];

        public byte[] Buffer => _buf;
        public void Clear() => Length = 0;

        public byte[] ToArray()
        {
            var a = new byte[Length];
            Array.Copy(_buf, a, Length);
            return a;
        }

        void Ensure(int n)
        {
            if (Length + n <= _buf.Length) return;
            Array.Resize(ref _buf, Math.Max(_buf.Length * 2, Length + n));
        }

        public void U8(byte v) { Ensure(1); _buf[Length++] = v; }
        public void I8(sbyte v) => U8((byte)v);
        public void Bool(bool v) => U8(v ? (byte)1 : (byte)0);
        public void U16(ushort v) { Ensure(2); _buf[Length++] = (byte)v; _buf[Length++] = (byte)(v >> 8); }
        public void I16(short v) => U16((ushort)v);
        public void U32(uint v) { Ensure(4); for (int i = 0; i < 4; i++) _buf[Length++] = (byte)(v >> (8 * i)); }
        public void I32(int v) => U32((uint)v);
        public void U64(ulong v) { Ensure(8); for (int i = 0; i < 8; i++) _buf[Length++] = (byte)(v >> (8 * i)); }
        public unsafe void F32(float v) => U32(*(uint*)&v);

        public void Str(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s ?? string.Empty);
            if (bytes.Length > ushort.MaxValue) throw new ArgumentException("string too long");
            U16((ushort)bytes.Length);
            Ensure(bytes.Length);
            Array.Copy(bytes, 0, _buf, Length, bytes.Length);
            Length += bytes.Length;
        }

        public void Bytes(byte[] b, int count)
        {
            Ensure(count);
            Array.Copy(b, 0, _buf, Length, count);
            Length += count;
        }
    }

    public sealed class ByteReader
    {
        readonly byte[] _buf;
        int _pos;
        readonly int _end;

        public ByteReader(byte[] buf, int offset = 0, int count = -1)
        {
            _buf = buf;
            _pos = offset;
            _end = count < 0 ? buf.Length : offset + count;
        }

        public bool AtEnd => _pos >= _end;
        public int Remaining => _end - _pos;

        void Need(int n) { if (_pos + n > _end) throw new FormatException("message truncated"); }

        public byte U8() { Need(1); return _buf[_pos++]; }
        public sbyte I8() => (sbyte)U8();
        public bool Bool() => U8() != 0;
        public ushort U16() { Need(2); ushort v = (ushort)(_buf[_pos] | _buf[_pos + 1] << 8); _pos += 2; return v; }
        public short I16() => (short)U16();
        public uint U32() { Need(4); uint v = 0; for (int i = 0; i < 4; i++) v |= (uint)_buf[_pos++] << (8 * i); return v; }
        public int I32() => (int)U32();
        public ulong U64() { Need(8); ulong v = 0; for (int i = 0; i < 8; i++) v |= (ulong)_buf[_pos++] << (8 * i); return v; }
        public unsafe float F32() { uint v = U32(); return *(float*)&v; }

        public string Str()
        {
            int n = U16();
            Need(n);
            var s = Encoding.UTF8.GetString(_buf, _pos, n);
            _pos += n;
            return s;
        }
    }
}
