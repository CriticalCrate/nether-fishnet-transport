using System;
using System.Net;

namespace ArcaneKit.Nether
{
    public class UnreliableChannel : IChannel
    {
        private readonly ISocket socket;
        private readonly EndPoint remoteEndPoint;
        private readonly byte[] sendBuffer;
        private readonly byte[] receiveBuffer;
        public int AvailableMTU => socket.GetMTU() - sizeof(byte);

        public UnreliableChannel(ISocket socket, EndPoint remoteEndPoint)
        {
            this.socket = socket;
            this.remoteEndPoint = remoteEndPoint;
            sendBuffer = new byte[socket.GetMTU()];
            receiveBuffer = new byte[socket.GetMTU()];
        }

        public void Send(byte[] data, int length)
        {
            sendBuffer[ChannelTypeIndex] = (byte)MessageType.Unreliable;
            data.AsSpan(0, length).CopyTo(sendBuffer.AsSpan(1));
            socket.Send(remoteEndPoint, sendBuffer, length + 1);
        }

        public bool TryReceive(byte[] data, int length, out byte[] result, out int resultLength)
        {
            result = data;
            resultLength = length - 1;
            if (length < 4)
                return false;
            if (data[ChannelTypeIndex] != (byte)MessageType.Unreliable)
                return false;
            data.AsSpan(1, resultLength).CopyTo(receiveBuffer);
            result = receiveBuffer;
            return true;
        }

        public void Update(uint currentMs)
        {
        }

        public void Dispose()
        {
        }

        private const int ChannelTypeIndex = 0;
    }
}