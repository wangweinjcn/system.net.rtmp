using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace System.Net.Rtmp
{
    public enum RtmpErrors : int
    {
        Success = 0,
        InvalidVersion,
        HandshakeError
    }

    public class RtmpClient : IDisposable
    {
        public event EventHandler<RtmpProtocolEventArgs> MessageReceived;

        private Stream _baseStream;
        private BinaryReader _reader;
        private BinaryWriter _writer;
        private RtmpProtocol _selectedProtocol;
        private uint _epoch;

        private static IDictionary<byte, RtmpProtocol> _protocols;

        static RtmpClient()
        {
            _protocols = new Dictionary<byte, RtmpProtocol>();

            RegisterProtocol(new DefaultRtmpProtocol());
        }

        public static void RegisterProtocol(RtmpProtocol protocol)
        {
            // valid version numbers range from 3 - 31
            if (protocol.ProtocolVersion < 3 || protocol.ProtocolVersion > 31)
            {
                throw new ArgumentException("Invalid version number", "protocol");
            }

            _protocols[protocol.ProtocolVersion] = protocol;
        }

        public RtmpClient(Stream stream)
        {
            _baseStream = stream;
            _reader = new BinaryReader(_baseStream);
            _writer = new BinaryWriter(_baseStream);

            RtmpErrors result = DoHandshake();

            if (result != RtmpErrors.Success)
            {
                throw new Exception($"Handshake error {(int)result}");
            }

            if (_selectedProtocol == null)
            {
                throw new Exception("Protocol not set");
            }

            Trace.TraceInformation($"RtmpClient using protocol version {_selectedProtocol.ProtocolVersion}");

            _selectedProtocol.MessageReceived += (s,e) =>
            {
                Trace.TraceInformation($"RtmpClient got message of type {e.Message.MessageType}");
                MessageReceived?.Invoke(s, e);
            };

            _selectedProtocol.Start(stream);
        }

        public void Close()
        {
            _selectedProtocol.Stop();
        }

        private RtmpErrors DoHandshake()
        {
            uint streamEpoch = 0;
            RtmpErrors retval = RtmpErrors.Success;

            // Step 1: Read C0
            var c0 = _reader.ReadByte();

            // does the client have a protocol that we have?
            if (!_protocols.ContainsKey(c0))
            {
                retval = RtmpErrors.InvalidVersion;
                goto close;
            }

            _selectedProtocol = _protocols[c0];
            // Step 2: Write S0
            _writer.Write(_selectedProtocol.ProtocolVersion);

            // Step 3: Read C1
            var c1Time = _reader.ReadUInt32();
            var c1Zero = _reader.ReadUInt32();
            var c1Rand = _reader.ReadBytes(1528);

            streamEpoch = c1Time;
            // second 4 bytes must be 0
            if (c1Zero != 0)
            {
                retval = RtmpErrors.HandshakeError;
                goto close;
            }

            // Step 4: Write S1
            var s1Time = 0;
            var s1Rand = new byte[1528];
            new Random().NextBytes(s1Rand);
            _writer.Write(s1Time);
            _writer.Write(0);
            _writer.Write(s1Rand);

            // Step 5: Write S2
            _writer.Write(c1Time);
            _writer.Write(0);
            _writer.Write(c1Rand);

            // Step 6: Read C2
            var c2AckTime = _reader.ReadUInt32();
            var c2time = _reader.ReadUInt32();
            var c2Rand = _reader.ReadBytes(1528);

            // Verify the client sent back the correct time
            if (c2AckTime != 0)
            {
                retval = RtmpErrors.HandshakeError;
                goto close;
            }

            // Vertify the client sent back the correct rand data
            for (int i = 0; i < 1528; i++)
            {
                if (s1Rand[i] != c2Rand[i])
                {
                    retval = RtmpErrors.HandshakeError;
                    goto close;
                }
            }

            _epoch = streamEpoch;
            goto ret;

        close:
            _reader.Close();
            _writer.Close();
            _baseStream.Close();

        ret:
            return retval;
        }

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects).
                    _baseStream.Dispose();
                }

                // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
                // TODO: set large fields to null.

                disposedValue = true;
            }
        }

        // TODO: override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
        // ~RtmpClient()
        // {
        //   // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
        //   Dispose(false);
        // }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
            // TODO: uncomment the following line if the finalizer is overridden above.
            // GC.SuppressFinalize(this);
        }
        #endregion
    }
}