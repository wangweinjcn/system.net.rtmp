using System;
using System.IO;

namespace System.Net.Rtmp
{
    public class RtmpProtocolEventArgs : EventArgs
    {
        public Message Message { get; set; }
    }

    public class Message
    {
        public uint MessageStreamId { get; set; }
        public uint MessageType { get; set; }
        public uint Time { get; set; }
        public uint Length { get; set; }
        public byte[] Data { get; set; }
    }

    public abstract class RtmpProtocol
    {
        public event EventHandler<RtmpProtocolEventArgs> MessageReceived;

        public abstract byte ProtocolVersion
        {
            get;
        }

        public virtual void Start(Stream stream)
        {

        }

        public virtual void Stop()
        {
            
        }

        protected virtual void OnMessageReceived(object sender, RtmpProtocolEventArgs e)
        {
            MessageReceived?.Invoke(sender, e);
        }
    }
}