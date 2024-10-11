using System;
using System.IO;
using System.Runtime.InteropServices;

namespace RakDotNet.IO
{
    public class BitWriter : IDisposable
    {
        private readonly Stream _stream;
        private readonly bool _leaveOpen;
        private readonly bool _orderLocked;
        private readonly bool _positionLocked;
        private readonly object _lock;

        private Endianness _endianness;
        private bool _disposed;
        private long _pos;

        public virtual Stream BaseStream => _stream;
        public virtual Endianness Endianness
        {
            get => _endianness;
            set
            {
                if (_orderLocked)
                    throw new InvalidOperationException("Endianness is fixed");

                // wait for write operations to complete so we don't mess them up
                lock (_lock)
                    _endianness = value;
            }
        }
        public virtual bool CanChangeEndianness => !_orderLocked;
        public virtual bool CanChangePosition => !_positionLocked;
        public virtual long Position
        {
            get => _pos;
            set
            {
                if (_positionLocked)
                    throw new InvalidOperationException("Position is locked");

                lock (_lock)
                    _pos = value;
            }
        }
        public virtual long BytePosition => (long) Math.Floor(Position / 8d);

        public BitWriter(Stream stream, Endianness endianness = Endianness.LittleEndian, bool orderLocked = true,
            bool leaveOpen = false, bool positionLocked = true, long startOffset = 0)
        {
            if (!stream.CanWrite)
                throw new ArgumentException("Stream is not writeable", nameof(stream));

            if (!stream.CanRead)
                throw new ArgumentException("Stream is not readable", nameof(stream));

            _stream = stream;
            _leaveOpen = leaveOpen;
            _orderLocked = orderLocked;
            _positionLocked = positionLocked;
            _lock = new object();

            _endianness = endianness;
            _disposed = false;
            _pos = startOffset;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    if (_leaveOpen)
                        _stream.Flush();
                    else
                        _stream.Close();
                }

                _disposed = true;
            }
        }

        public void Dispose() => Dispose(true);

        public virtual void Close() => Dispose(true);

        public virtual void WriteBit(bool bit)
        {
            lock (_lock)
            {
                // offset in bits, in case we're not starting on the 8th (i = 7) bit
                var bitOffset = (byte)(_pos & 7);

                // read the last byte from the stream
                var val = _stream.ReadByte();

                // don't go back if we haven't actually read anything
                if (val != -1)
                    _stream.Position--;
                else // ReadByte returns -1 if we reached the end of the stream, we need unsigned data so set it to 0
                    val = 0;

                if (bit)
                {
                    // if we're setting, shift 0x80 (10000000) to the right by bitOffset
                    var mask = (byte)(0x80 >> bitOffset);

                    // we set the bit using our mask and bitwise OR
                    val |= mask;
                }
                else
                {
                    // hacky mask
                    var mask = (byte)(1 << (-(bitOffset + 1) & 7));

                    // unset using bitwise AND and bitwise NOT on the mask
                    val &= ~mask;
                }

                // write the modified byte to the stream
                _stream.WriteByte((byte)val);

                // advance the bit position
                _pos++;

                // if we aren't ending on a new byte, go back 1 byte on the stream
                if ((_pos & 7) != 0)
                    _stream.Position--;
            }
        }

        public virtual int Write(Span<byte> buf, int bits)
        {
            // offset in bits, in case we're not starting on the 8th (i = 7) bit
            var bitOffset = (byte)(_pos & 7);

            // inverted bit offset (eg. 3 becomes 5)
            var invertedOffset = (byte)(-bitOffset & 7);

            // get num of bytes we have to read from the Stream, we add bitOffset so we have enough data to add in case bitOffset != 0
            var byteCount = (int)Math.Ceiling((bits + bitOffset) / 8d);

            // get size of output buffer
            var bufSize = (int)Math.Ceiling(bits / 8d);

            // swap endianness in case we're not using same endianness as host
            if ((_endianness != Endianness.LittleEndian && BitConverter.IsLittleEndian) ||
                (_endianness != Endianness.BigEndian && !BitConverter.IsLittleEndian))
                buf.Reverse();

            // lock the read so we don't mess up other calls
            lock (_lock)
            {
                // check if we don't have to do complex bit level operations
                if (bitOffset == 0 && (bits & 7) == 0)
                {
                    _stream.Write(buf);

                    _pos += bits;

                    return bufSize;
                }

                // allocate a buffer on the stack to write
                Span<byte> bytes = stackalloc byte[byteCount];

                // we might already have data in the stream
                var readSize = _stream.Read(bytes);

                // subtract the read bytes from the position so we can write them later
                _stream.Position -= readSize;

                for (var i = 0; bits > 0; i++)
                {
                    // add bits starting from bitOffset from the input buffer to the write buffer
                    bytes[i] |= (byte)(buf[i] >> bitOffset);

                    // set the leaking bits on the next byte
                    if (invertedOffset < 8 && bits > invertedOffset)
                        bytes[i + 1] = (byte)(buf[i] << invertedOffset);

                    // add max 8 remaining bits to the position
                    _pos += bits < 8 ? bits & 7 : 8;

                    // we wrote a byte, remove 8 bits from the bit count
                    bits -= 8;

                    // if we're at the last byte, cut off the unused bits
                    if (bits < 8)
                        bytes[i] <<= (-bits & 7);
                }

                // write the buffer
                _stream.Write(bytes);

                // roll back the position in case we haven't used the last byte fully
                _stream.Position -= (byteCount - bufSize);
            }

            return bufSize;
        }

        [Obsolete("Write has been changed to use a regular Span instead of a ReadOnlySpan in order to fix endianness swapping, please only use this method if you absolutely cannot pass a regular Span")]
        public virtual int Write(ReadOnlySpan<byte> buf, int bits)
        {
            Span<byte> mutable = stackalloc byte[buf.Length];
            buf.CopyTo(mutable);

            return Write(buf, bits);
        }

        public virtual int Write(byte[] buf, int index, int length, int bits)
        {
            if (bits > (length * 8))
                throw new ArgumentOutOfRangeException(nameof(bits), "Bit count exceeds buffer length");

            if (index > length)
                throw new ArgumentOutOfRangeException(nameof(index), "Index exceeds buffer length");

            return Write(new Span<byte>(buf, index, length), bits);
        }

        // FIXME: deal with endianness (eg https://stackoverflow.com/a/15020402)
        public virtual int Write<T>(T val, int bits) where T : struct
        {
            var size = Marshal.SizeOf<T>();
            var ptr = IntPtr.Zero;

            try
            {
                ptr = Marshal.AllocHGlobal(size);
                Marshal.StructureToPtr(val, ptr, false);

                var buf = new byte[size];
                Marshal.Copy(ptr, buf, 0, size);

                return Write(new Span<byte>(buf), bits);
            }
            finally
            {
                if (ptr != IntPtr.Zero)
                    Marshal.FreeHGlobal(ptr);
            }
        }

        public virtual int Write<T>(T val) where T : struct
            => Write(val, Marshal.SizeOf<T>() * 8);

        public virtual void WriteSerializable(ISerializable serializable)
            => serializable.Serialize(this);

        public virtual void AlignWrite(bool startAlign = false)
        {
            if ((_pos & 7) != 0)
            {
                lock (_lock)
                {
                    if (!startAlign)
                    {
                        _pos = (long)Math.Ceiling(_pos / 8d) * 8;

                        _stream.Position++;
                    }
                    else
                    {
                        _pos = (long)Math.Floor(_pos / 8d) * 8;
                    }
                }
            }
        }
    }
}

