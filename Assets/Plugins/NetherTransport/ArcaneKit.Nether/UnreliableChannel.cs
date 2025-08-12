using System;
using System.Collections.Generic;
using System.Net;

namespace ArcaneKit.Nether
{
    public class UnreliableChannel : IChannel
    {
        private readonly ISocket socket;
        private readonly EndPoint remoteEndPoint;
        private readonly byte[] sendBuffer;
        private readonly List<Received> receiveBuffers = new(byte.MaxValue);
        private readonly byte[] constructedPacket;
        public int AvailableMTU => socket.GetMTU() - sizeof(byte) - sizeof(byte) - sizeof(byte) - sizeof(byte);
        private byte sequence;

        public UnreliableChannel(ISocket socket, EndPoint remoteEndPoint)
        {
            this.socket = socket;
            this.remoteEndPoint = remoteEndPoint;
            sendBuffer = new byte[socket.GetMTU()];
            constructedPacket = new byte[AvailableMTU * byte.MaxValue];
            for (var i = 0; i < byte.MaxValue; i++)
            {
                receiveBuffers.Add(new Received
                {
                    Data = new byte[socket.GetMTU()],
                    Size = -1,
                    SequenceIndex = -1
                });
            }
        }

        public void Send(byte[] data, int length)
        {
            foreach (var (packet, size) in PackIntoPackets(data, length))
                socket.Send(remoteEndPoint, packet, size);
            sequence++;
        }

        private IEnumerable<(byte[], int)> PackIntoPackets(byte[] data, int length)
        {
            var packetsCount = length / AvailableMTU + (length % AvailableMTU > 0 ? 1 : 0);
            if (packetsCount > byte.MaxValue)
                throw new UnreliableChannelException("Packet to big for unreliable transfer.");
            var offset = 0;
            var size = length;
            for (byte packetSliceIndex = 0; packetSliceIndex < packetsCount; packetSliceIndex++)
            {
                sendBuffer[ChannelTypeIndex] = (byte)MessageType.Unreliable;
                sendBuffer[SequenceIndex] = sequence;
                sendBuffer[SliceIndex] = packetSliceIndex;
                sendBuffer[SliceCountIndex] = (byte)packetsCount;
                var bytesToSend = packetSliceIndex != packetsCount - 1 ? AvailableMTU : size;
                Array.Copy(data, offset, sendBuffer, 4, bytesToSend);
                offset += AvailableMTU;
                size -= AvailableMTU;
                yield return (sendBuffer, bytesToSend + 4);
            }
        }

        public bool TryReceive(byte[] data, int length, out byte[] result, out int resultLength)
        {
            result = constructedPacket;
            resultLength = 0;
            if (length < 4)
                return false;
            if (data[ChannelTypeIndex] != (byte)MessageType.Unreliable)
                return false;
            if (data[SequenceIndex] == byte.MaxValue)
                return false;
            if (data[SliceIndex] == byte.MaxValue)
                return false;
            if (data[SliceCountIndex] == byte.MaxValue)
                return false;
            var received = receiveBuffers[data[SliceIndex]];
            received.Size = length - 4;
            received.SequenceIndex = data[SequenceIndex];
            Array.Copy(data, 4, received.Data, 0, length - 4);
            receiveBuffers[data[SliceIndex]] = received;
            for (var sliceIndex = 0; sliceIndex < data[SliceCountIndex]; sliceIndex++)
            {
                received = receiveBuffers[sliceIndex];
                if (received.Size == -1 || received.SequenceIndex != data[SequenceIndex])
                    return false;
            }

            resultLength = 0;
            var sliceCount = data[SliceCountIndex];
            for (var sliceIndex = 0; sliceIndex < sliceCount; sliceIndex++)
            {
                received = receiveBuffers[sliceIndex];
                Array.Copy(received.Data, 0, constructedPacket, resultLength, received.Size);
                resultLength += received.Size;
                received.Size = -1;
                received.SequenceIndex = -1;
                receiveBuffers[sliceIndex] = received;
            }
            return true;
        }

        public void Update(uint currentMs)
        {
        }

        public void Dispose()
        {
        }

        private struct Received
        {
            public byte[] Data;
            public int Size;
            public int SequenceIndex;
        }

        private const int ChannelTypeIndex = 0;
        private const int SequenceIndex = 1;
        private const int SliceIndex = 2;
        private const int SliceCountIndex = 3;
    }
}