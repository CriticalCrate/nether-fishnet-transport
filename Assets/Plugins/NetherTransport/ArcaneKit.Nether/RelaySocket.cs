using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using UnityEngine;

namespace ArcaneKit.Nether
{
    public class RelaySocket : ISocket
    {
        public byte ConnectionId { get; private set; }
        private readonly ISocket socket;
        private EndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
        private readonly byte[] decryptionBuffer;
        private readonly byte[] sendBuffer;

        private readonly Dictionary<byte, EndPoint> clientToEndpointMap = new();
        private readonly Dictionary<EndPoint, byte> endpointToClientMap = new();
        private readonly IPEndPoint relayEndpoint;
        private PacketEncryption packetEncryption;
        private int roomId;
        private byte[] connectionSecretBytes;
        private DateTime lastSendPacket = DateTime.MaxValue;

        public RelaySocket(ISocket socket, IPEndPoint relayEndpoint)
        {
            this.socket = socket;
            this.relayEndpoint = relayEndpoint;
            decryptionBuffer = new byte[socket.GetMTU()];
            sendBuffer = new byte[socket.GetMTU()];
        }

        public (EndPoint, byte[], int) Receive()
        {
            if (lastSendPacket.AddSeconds(1) < DateTime.UtcNow)
            {
                // Ping relay to prevent timeout.
                SendInternal(0, connectionSecretBytes, connectionSecretBytes.Length);
            }
            var (endpoint, data, length) = socket.Receive();
            if (length == 0)
                return (remoteEndPoint, Array.Empty<byte>(), 0);
            var roomId = BitConverter.ToInt32(data, 0);
            if (roomId != this.roomId)
                return (remoteEndPoint, Array.Empty<byte>(), 0);
            var from = data[4];
            var to = data[5];
            if (to != ConnectionId)
                return (remoteEndPoint, Array.Empty<byte>(), 0);
            var packet = data.AsSpan(6, length - 6);
            remoteEndPoint = GetOrCreateEndpointMapping(from);
            length = packetEncryption.Decrypt(packet, decryptionBuffer);
            return (remoteEndPoint, decryptionBuffer, length);
        }

        public EndPoint GetOrCreateEndpointMapping(byte connectionId)
        {
            if (clientToEndpointMap.TryGetValue(connectionId, out var endpointMapping))
                return endpointMapping;
            var mappedEndpoint = new IPEndPoint(IPAddress.Parse($"1.1.1.{connectionId}"), 10000);
            clientToEndpointMap.Add(connectionId, mappedEndpoint);
            endpointToClientMap.Add(mappedEndpoint, connectionId);
            return mappedEndpoint;
        }

        public async Task Setup(EndPoint serverIp = null)
        {
            Debug.Log("Connecting to relay...");
            if (serverIp != null)
            {
                clientToEndpointMap.Add(1, serverIp);
                endpointToClientMap.Add(serverIp, 1);
            }
            var connectionStartTime = DateTime.UtcNow;
            while (DateTime.UtcNow - connectionStartTime <= TimeSpan.FromSeconds(10))
            {
                SendInternal(0, connectionSecretBytes, connectionSecretBytes.Length);
                await Task.Delay(500);
                var (relayEndpoint, data, length) = socket.Receive();
                if (length < 6)
                    continue;
                var recvRoomId = BitConverter.ToInt32(data, 0);
                if (recvRoomId != roomId)
                    continue;
                var recvFrom = data[4];
                var recvTo = data[5];
                if (recvFrom != 0 && recvTo == 0)
                {
                    ConnectionId = recvFrom;
                    GetOrCreateEndpointMapping(ConnectionId);
                    Debug.Log("Connected to relay.");
                    return;
                }
                await Task.Delay(1000);
            }
            throw new Exception("Unable to connect");
        }

        public void Send(EndPoint endPoint, byte[] data, int size)
        {
            if (!endpointToClientMap.TryGetValue(endPoint, out var toRelayId))
                return;
            SendInternal(toRelayId, data, size);
        }

        private void SendInternal(byte to, byte[] data, int size)
        {
            lastSendPacket = DateTime.UtcNow;
            BitConverter.TryWriteBytes(sendBuffer.AsSpan(0, 4), roomId);
            sendBuffer[4] = ConnectionId;
            sendBuffer[5] = to;
            var length = packetEncryption.Encrypt(sendBuffer.AsSpan().Slice(6, sendBuffer.Length - 6), data.AsSpan(0, size)) + 6;
            socket.Send(relayEndpoint, sendBuffer, length);
        }

        public RelaySocket WithConfiguration(byte connectionId, int roomId, string roomSecret, string connectionSecret)
        {
            this.roomId = roomId;
            ConnectionId = connectionId;
            packetEncryption = new PacketEncryption(roomSecret);
            connectionSecretBytes = System.Text.Encoding.UTF8.GetBytes(connectionSecret);
            return this;
        }

        private const int RelayOverhead = 6;
        public int GetMTU() => socket.GetMTU() - RelayOverhead - PacketEncryption.IvSize - PacketEncryption.TagSize;

        public void Dispose()
        {
            socket.Dispose();
        }
    }
}