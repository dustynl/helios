﻿using System;
using System.Collections.Generic;
using Helios.Buffers;
using Helios.Exceptions;
using Helios.Net;
using Helios.Topology;

namespace Helios.Serialization
{
    /// <summary>
    /// Decodes messages based off of a length frame added to the front of the message
    /// </summary>
    public class LengthFieldFrameBasedDecoder : MessageDecoderBase
    {
        private readonly int _maxFrameLength;
        private readonly int _lengthFieldOffset;
        private readonly int _lengthFieldEndOffset;
        private readonly int _lengthFieldLength;
        private readonly int _lengthAdjustment;
        private readonly int _initialBytesToStrip;
        private readonly bool _failFast;
        private bool _discardingTooLongFrame;
        private long _bytesToDiscard;
        private long _tooLongFrameLength;

        public LengthFieldFrameBasedDecoder(int maxFrameLength, int lengthFieldOffset, int lengthFieldLength) : this(maxFrameLength, lengthFieldOffset, lengthFieldLength, 0, 0)
        {
        }

        public LengthFieldFrameBasedDecoder(int maxFrameLength, int lengthFieldOffset, int lengthFieldLength, int lengthAdjustment, int initialBytesToStrip)
            : this(maxFrameLength, lengthFieldOffset, lengthFieldLength, lengthAdjustment, initialBytesToStrip, true)
        {
        }

        public LengthFieldFrameBasedDecoder(int maxFrameLength, int lengthFieldOffset, int lengthFieldLength, int lengthAdjustment, int initialBytesToStrip, bool failFast)
        {
            _maxFrameLength = maxFrameLength;
            _lengthFieldOffset = lengthFieldOffset;
            _lengthFieldLength = lengthFieldLength;
            _lengthFieldEndOffset = lengthFieldLength + lengthFieldOffset;
            _lengthAdjustment = lengthAdjustment;
            _initialBytesToStrip = initialBytesToStrip;
            _failFast = failFast;
        }

        public override void Decode(NetworkData data, out List<NetworkData> decoded)
        {
            decoded = new List<NetworkData>();
            var position = 0;
            while (position < data.Length)
            {
                NetworkData nextFrame;
                position = Decode(data, position, out nextFrame);
                if(!nextFrame.RemoteHost.IsEmpty())
                    decoded.Add(nextFrame);
            }
        }

        public override void Decode(IConnection connection, IByteBuf buffer, out List<IByteBuf> decoded)
        {
            decoded = new List<IByteBuf>();
            var obj = Decode(connection, buffer);
            while (obj != null)
            {
                decoded.Add(obj);
                obj = Decode(connection, buffer);
            } 
        }

        protected IByteBuf Decode(IConnection connection, IByteBuf input)
        {
            if (_discardingTooLongFrame)
            {
                var bytesToDiscard = _bytesToDiscard;
                var localBytesToDiscard = (int) Math.Min(bytesToDiscard, input.ReadableBytes);
                input.SkipBytes(localBytesToDiscard);
                bytesToDiscard -= localBytesToDiscard;
                _bytesToDiscard = bytesToDiscard;
                FailIfNecessary(false);
            }

            if (input.ReadableBytes < _lengthFieldEndOffset) return null;

            var actualLengthFieldOffset = input.ReaderIndex + _lengthFieldOffset;
            var frameLength = GetUnadjustedFrameLength(input, actualLengthFieldOffset, _lengthFieldLength);

            if (frameLength < 0)
            {
                input.SkipBytes(_lengthFieldEndOffset);
                throw new CorruptedFrameException(string.Format("negative pre-adjustment lenght field: " + frameLength));
            }

            frameLength += _lengthAdjustment + _lengthFieldEndOffset;

            if (frameLength < _lengthFieldEndOffset)
            {
                input.SkipBytes(_lengthFieldEndOffset);
                throw new CorruptedFrameException(string.Format("Adjusted frame length ({0}) is less than lengthFieldEndOffset: {1}", frameLength, _lengthFieldEndOffset));
            }

            if (frameLength > _maxFrameLength)
            {
                var discard = frameLength - input.ReadableBytes;
                _tooLongFrameLength = frameLength;

                if (discard < 0)
                {
                    // buffer contains more bytes than the frameLength so we can discard all now
                    input.SkipBytes((int) frameLength);
                }
                else
                {
                    //Enter discard mode and discard everything receive so far
                    _discardingTooLongFrame = true;
                    _bytesToDiscard = discard;
                    input.SkipBytes(input.ReadableBytes);
                }
                FailIfNecessary(true);
                return null;
            }

            // never overflows bceause it's less than _maxFrameLength
            var frameLengthInt = (int) frameLength;
            if (input.ReadableBytes < frameLengthInt)
            {
                //need additional data from the network before we can finish decoding this message
                return null;
            }

            if (_initialBytesToStrip > frameLengthInt)
            {
                input.SkipBytes(frameLengthInt);
                throw new CorruptedFrameException(string.Format("Adjusted frame lenght ({0}) is less than initialBytesToStrip: {1}", frameLength, _initialBytesToStrip));
            }
            input.SkipBytes(_initialBytesToStrip);

            //extract frame
            var readerIndex = input.ReaderIndex;
            var actualFrameLength = frameLengthInt - _initialBytesToStrip;
            var frame = ExtractFrame(connection, input, readerIndex, actualFrameLength);
            input.SetReaderIndex(readerIndex + actualFrameLength);
            return frame;
        }

        protected IByteBuf ExtractFrame(IConnection connection, IByteBuf buffer, int index, int length)
        {
            var frame = connection.Allocator.Buffer(length);
            frame.WriteBytes(buffer, index, length);
            return frame;
        }

        protected long GetUnadjustedFrameLength(IByteBuf buf, int offset, int length)
        {
            long framelength;
            switch (length)
            {
                case 1:
                    framelength = buf.GetByte(offset);
                    break;
                case 2:
                    framelength = buf.GetUnsignedShort(offset);
                    break;
                case 4:
                    framelength = buf.GetUnsignedInt(offset);
                    break;
                case 8:
                    framelength = buf.GetLong(offset);
                    break;
                default:
                    throw new DecoderException(string.Format("unsupported lengtFieldLength: {0} (expected: 1, 2, 4, or 8)", length));
            }
            return framelength;
        }

        protected void FailIfNecessary(bool firstDetectionOfTooLongFrame)
        {
            if (_bytesToDiscard == 0)
            {
                // Reset to the initial state and tell the handlers that the
                // frame was too large
                var tooLongFrameLength = _tooLongFrameLength;
                _tooLongFrameLength = 0;
                _discardingTooLongFrame = false;
                if (!_failFast || (_failFast && firstDetectionOfTooLongFrame))
                {
                    Fail(_tooLongFrameLength);
                }
                else
                {
                    // Keep discarding and notify handlers if necessary
                    if (_failFast && firstDetectionOfTooLongFrame)
                    {
                        Fail(tooLongFrameLength);
                    }
                }
            }
        }

        private void Fail(long frameLength)
        {
            if(frameLength > 0)
                throw new TooLongFrameException(string.Format("Adjusted frame length exceeds {0}: {1} - discarded", _maxFrameLength, frameLength));
            else
                throw new TooLongFrameException(string.Format("Adjusted frame lenght exceeds {0} - discarding", _maxFrameLength));
        }

        protected int Decode(NetworkData input, int initialOffset, out NetworkData nextFrame)
        {
            nextFrame = NetworkData.Empty;
            if (input.Length < _lengthFieldOffset) return input.Length;

            var actualLengthFieldOffset = initialOffset + _lengthFieldOffset;
            var frameLength = GetFrameLength(input, actualLengthFieldOffset, _lengthFieldLength);

            if (frameLength > _maxFrameLength)
            {
                if(_failFast) throw new Exception(string.Format("Object exceeded maximum length of {0} bytes. Was {1}", _maxFrameLength, frameLength));
                return input.Length;
            }

            var frameLengthInt = (int) frameLength;
            if (input.Length < frameLengthInt)
            {
                return input.Length;
            }

            //extract the framed message
            var index = _lengthFieldLength + actualLengthFieldOffset;
            var actualFrameLength = frameLengthInt - _initialBytesToStrip;
            nextFrame = ExtractFrame(input, index, actualFrameLength);
            return index + actualFrameLength;
        }

        protected long GetFrameLength(NetworkData data, int offset, int length)
        {
            long frameLength = 0;
            switch (length)
            {
                case 1:
                    frameLength = data.Buffer[offset];
                    break;
                case 2:
                    frameLength = BitConverter.ToUInt16(data.Buffer, offset);
                    break;
                case 4:
                    frameLength = BitConverter.ToUInt32(data.Buffer, offset);
                    break;
                case 8:
                    frameLength = BitConverter.ToInt64(data.Buffer, offset);
                    break;
                default:
                    throw new ArgumentException(
                        "unsupported lengthFieldLength: " + _lengthFieldLength + " (expected: 1, 2, 4, or 8)");
                
            }

            return frameLength;
        }

        protected NetworkData ExtractFrame(NetworkData data, int offset, int length)
        {
            try
            {
                var newData = new byte[length];
                Array.Copy(data.Buffer, offset, newData, 0, length);
                return NetworkData.Create(data.RemoteHost, newData, length);
            }
            catch (Exception ex)
            {
                throw new HeliosException(
                    string.Format("Error while copying {0} bytes from buffer of length {1} from starting index {2} to {3} into buffer of length {0}",
                    length, data.Length, offset, offset + length)
                    , ex);
            }
        }
    }

    
}