using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace System.Net.Rtmp
{
    public class DefaultRtmpProtocol : RtmpProtocol
    {
        private Framer _framer;
        private Chunker _chunker;
        private volatile bool _isRunning = false;

        public override byte ProtocolVersion
        {
            get => 3;
        }

        public DefaultRtmpProtocol()
        {
            _framer = new Framer();
            _chunker = new Chunker();

            _framer.ChunkReceived += (s, e) =>
            {
                _chunker.AddChunk(e.Chunk);
            };

            _chunker.MessageReceived += (s, e) =>
            {
                this.OnMessageReceived(s, e);
            };
        }

        public override void Start(Stream stream)
        {
            _isRunning = true;
            // TODO: add cancellations
            Task.Factory.StartNew(() =>
            {
                byte[] readBuffer = new byte[1024];
                using (BinaryReader reader = new BinaryReader(stream))
                //using (StreamWriter writer = new StreamWriter(stream))
                {
                    while (_isRunning)
                    {
                        int amtRead = reader.Read(readBuffer, 0, readBuffer.Length);
                        _framer.AppendData(ref readBuffer, amtRead);
                    }
                }
            });
        }

        public override void Stop()
        {
            _isRunning = false;
        }

        private class FramerEventArgs : EventArgs
        {
            public Chunk Chunk { get; set; }
        }

        private class Framer
        {
            public event EventHandler<FramerEventArgs> ChunkReceived;
            private byte[] _buffer;
            private int _currentInsertIndex;
            private int _maxBufferSize;
            private Chunk _lastReadChunk;

            public Framer(int initialBufferSize = 4 * 1024, int maxAllowedBufferSize = 1 * 1024 * 1024)
            {
                _buffer = new byte[initialBufferSize];
                _currentInsertIndex = 0;
                _maxBufferSize = maxAllowedBufferSize;
            }

            protected virtual void OnChunkReceived(Chunk c)
            {
                ChunkReceived?.Invoke(this, new FramerEventArgs { Chunk = c });
            }

            public void AppendData(ref byte[] data, int length)
            {
                // check to see if the buffer is too small, then resize of necessary
                if (length + _currentInsertIndex > _buffer.Length)
                {
                    ResizeBuffer();
                }

                Array.Copy(data, 0, _buffer, _currentInsertIndex, length);
                _currentInsertIndex += length;

                Chunk chunk = null;
                var amtRead = ReadChunk(out chunk);
                _lastReadChunk = chunk;
                if (amtRead > 0 && chunk != null)
                {
                    OnChunkReceived(chunk);
                }
            }

            private uint ReadChunk(out Chunk c)
            {
                // must have at least 3 bytes. In practice you always need > 3
                if (_currentInsertIndex < 3)
                {
                    c = null;
                    return 0;
                }

                uint amtRead = 0;
                c = null;

                // read basic header
                var chunkHeaderFormat = ((uint)_buffer[0] & 0b11000000) >> 6;
                if (chunkHeaderFormat > 3)
                {
                    throw new Exception("BUG: chunk header format is invalid (Code: L83)");
                }

                var chunkStreamId = (uint)_buffer[0] & 0b00111111;
                amtRead = 1;
                if (chunkStreamId == 0)
                {
                    // id of 0 is a two byte header 
                    chunkStreamId = ((uint)_buffer[1]) + 64;
                    amtRead = 2;
                }
                else if (chunkStreamId == 1)
                {
                    // id of 1 is a three byte header
                    chunkStreamId = ((uint)_buffer[3] * 256) + _buffer[1] + 64;
                    amtRead = 3;
                }

                // NOTE: Chunk Stream ID of 2 is an internal message for the protocol

                if (_lastReadChunk == null && (chunkHeaderFormat > 0))
                {
                    throw new Exception("Received a chunk type greater than 0 without received any prior chunks.");
                }

                c = new Chunk
                {
                    ChunkStreamId = chunkStreamId
                };

                // read chunk header
                if (chunkHeaderFormat == 0)
                {
                    c.Delta = BitConverter.ToUInt32(new byte[] { 0x00, _buffer[amtRead++], _buffer[amtRead++], _buffer[amtRead++] }, 0);
                    c.Length = BitConverter.ToUInt32(new byte[] { 0x00, _buffer[amtRead++], _buffer[amtRead++], _buffer[amtRead++] }, 0);
                    c.ChunkType = _buffer[amtRead++];
                    c.StreamId = BitConverter.ToUInt32(new byte[] { _buffer[amtRead++], _buffer[amtRead++], _buffer[amtRead++], _buffer[amtRead++] }, 0);
                }
                else if (chunkHeaderFormat == 1)
                {
                    c.Delta = BitConverter.ToUInt32(new byte[] { 0x00, _buffer[amtRead++], _buffer[amtRead++], _buffer[amtRead++] }, 0);
                    c.Length = BitConverter.ToUInt32(new byte[] { 0x00, _buffer[amtRead++], _buffer[amtRead++], _buffer[amtRead++] }, 0);
                    c.ChunkType = _buffer[amtRead++];
                    c.StreamId = _lastReadChunk.StreamId;
                }
                else if (chunkHeaderFormat == 2)
                {
                    c.Delta = BitConverter.ToUInt32(new byte[] { 0x00, _buffer[amtRead++], _buffer[amtRead++], _buffer[amtRead++] }, 0);
                    c.ChunkType = _lastReadChunk.ChunkType;
                    c.Length = _lastReadChunk.Length;
                    c.StreamId = _lastReadChunk.StreamId;
                }
                else if (chunkHeaderFormat == 3)
                {
                    c.Delta = _lastReadChunk.Delta;
                    c.ChunkType = _lastReadChunk.ChunkType;
                    c.Length = _lastReadChunk.Length;
                    c.StreamId = _lastReadChunk.StreamId;
                }

                // Read data
                if (c.Length + amtRead >= _currentInsertIndex)
                {
                    c = null;
                    return 0;
                }

                byte[] data = new byte[c.Length];
                Array.Copy(_buffer, amtRead, data, 0, c.Length);
                amtRead += c.Length;

                return amtRead;
            }

            private void ShiftBuffer(int newStartIndex)
            {
                if (newStartIndex == 0)
                {
                    return;
                }

                for (int i = 0; newStartIndex + i < _buffer.Length; i++)
                {
                    _buffer[i] = _buffer[newStartIndex + i];
                }

                _currentInsertIndex = 0;
            }

            private void ResizeBuffer()
            {
                if (_buffer.Length >= _maxBufferSize)
                {
                    throw new Exception("Maximum buffer size reached. Either a HUGE 1MB chunk was sent (unlikely), or there is a bug on the remote server.");
                }

                Array.Resize(ref _buffer, Math.Min(_buffer.Length * 2, _maxBufferSize));
            }
        }

        private class Chunker
        {
            public event EventHandler<RtmpProtocolEventArgs> MessageReceived;
            private IDictionary<int, List<Chunk>> _store;
            private uint _lastReceviedTime;
            private uint _lastSentTime;

            public Chunker()
            {
                _store = new Dictionary<int, List<Chunk>>();
            }

            public void AddChunk(Chunk chunk)
            {
                if (chunk == null || chunk.Data == null)
                {
                    throw new ArgumentNullException("chunk");
                }

                Trace.TraceInformation($"Received chunk for chunk stream {chunk.ChunkStreamId}");

                if (!_store.ContainsKey((int)chunk.ChunkStreamId))
                {
                    _store[(int)chunk.ChunkStreamId] = new List<Chunk>();
                }

                var currentChunkList = _store[(int)chunk.ChunkStreamId];

                Chunk lastReceivedChunk = null;
                if (currentChunkList.Count > 0)
                {
                    lastReceivedChunk = currentChunkList[currentChunkList.Count - 1];
                }

                if (chunk.ChunkType != 0 && lastReceivedChunk == null)
                {
                    throw new Exception("There are no chunks in the current chunk stream. The first chunk in the chunk stream must be of type 0.");
                }

                // We shouldn't need this (this might actually break things)
                // will decide after testing
                switch (chunk.ChunkType)
                {
                    case 0:
                        break;
                    case 1:
                        chunk.StreamId = lastReceivedChunk.StreamId;
                        break;
                    case 2:
                        chunk.StreamId = lastReceivedChunk.StreamId;
                        chunk.Length = lastReceivedChunk.Length;
                        break;
                    case 3:
                        chunk.StreamId = lastReceivedChunk.StreamId;
                        chunk.Length = lastReceivedChunk.Length;
                        chunk.Delta = lastReceivedChunk.Delta;
                        break;
                    default:
                        throw new Exception("Invalid chunk type");
                }

                Trace.TraceInformation($"Determined chunk to be for message stream {chunk.StreamId}");

                currentChunkList.Add(chunk);

                // Do we have any messages?
                foreach (var key in _store.Keys)
                {
                    // if we have a list that has nothing in it
                    // delete it to free up memory
                    if (_store[key].Count > 0)
                    {
                        var firstChunk = _store[key][0];
                        var msgLength = firstChunk.Length;
                        var receivedLength = firstChunk.Data.Length;

                        // find out how much data we have received
                        for (int i = 1; i < _store[key].Count; i++)
                        {
                            receivedLength += _store[key][i].Data.Length;
                        }

                        // did we receive more than we were expecting?
                        if (receivedLength > msgLength)
                        {
                            throw new Exception("Received more data then was expecting.");
                        }

                        // we received the amount of data we need to make a message
                        if (msgLength == receivedLength)
                        {
                            var chunks = _store[key];
                            Trace.TraceInformation($"Creating message from {chunks.Count} chunks.");
                            _store[key] = null;
                            OnMessageReceived(ChunksToMessage(chunks));
                        }
                    }
                }
            }

            protected virtual void OnMessageReceived(Message msg)
            {
                MessageReceived?.Invoke(this, new RtmpProtocolEventArgs { Message = msg });
            }

            public Message ChunksToMessage(List<Chunk> chunks)
            {
                // this could be better
                // do we need to make a new array, maybe create a linked list type thing
                byte[] messageData = new byte[chunks[0].Length];
                var msg = new Message
                {
                    Data = messageData,
                    Length = chunks[0].Length,
                    MessageStreamId = chunks[0].StreamId,
                    MessageType = chunks[0].ChunkType,
                    Time = chunks[0].Delta
                };

                int messageCopyOffset = 0;
                foreach (var chunk in chunks)
                {
                    Array.Copy(chunk.Data, 0, messageData, messageCopyOffset, chunk.Data.Length);
                    messageCopyOffset += chunk.Data.Length;
                }

                // Clean up memory (all chunks except last, since we copy the master info to all chunks)
                // we need the last one for retieving chunk stream info
                for (int i = 0; i < chunks.Count - 1; i++)
                {
                    chunks.RemoveAt(0);
                }

                return msg;
            }
        }

        private class Chunk
        {
            public uint Delta { get; set; }
            public uint ChunkStreamId { get; set; }
            public uint StreamId { get; set; }
            public uint ChunkType { get; set; }
            public uint Length { get; set; }
            public byte[] Data { get; set; }
        }
    }
}