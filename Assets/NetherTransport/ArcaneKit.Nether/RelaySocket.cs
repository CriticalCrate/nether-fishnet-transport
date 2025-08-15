using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using UnityEngine;

namespace ArcaneKit.Nether
{
    public class RelaySocket : ISocket
    {
        private const int HostConnectionId = 1;
        public byte ConnectionId { get; private set; }
        private readonly ISocket socket;
        private EndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
        private readonly byte[] receiveBuffer;
        private readonly byte[] sendBuffer;

        private readonly Dictionary<byte, EndPoint> clientToEndpointMap = new();
        private readonly Dictionary<EndPoint, byte> endpointToClientMap = new();
        private readonly IPEndPoint relayEndpoint;
        private int roomId;
        private byte[] connectionSecretBytes;
        private byte[] RoomSecretBytes;
        private DateTime lastSendPacket = DateTime.MaxValue;

        public RelaySocket(ISocket socket, IPEndPoint relayEndpoint)
        {
            this.socket = socket;
            this.relayEndpoint = relayEndpoint;
            receiveBuffer = new byte[socket.GetMTU()];
            sendBuffer = new byte[socket.GetMTU()];
        }

        public (EndPoint, byte[], int) Receive()
        {
            if (lastSendPacket.AddSeconds(5) < DateTime.UtcNow)
            {
                // Ping relay to prevent timeout.
                var payload = CreateConnectionPayload();
                SendInternal(0, payload, payload.Length);
            }
            var (endpoint, data, length) = socket.Receive();
            if (length == 0 || !endpoint.Equals(relayEndpoint))
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
            packet.CopyTo(receiveBuffer);
            return (remoteEndPoint, receiveBuffer, packet.Length);
        }

        private EndPoint GetOrCreateEndpointMapping(byte connectionId)
        {
            if (clientToEndpointMap.TryGetValue(connectionId, out var endpointMapping))
                return endpointMapping;
            var mappedEndpoint = new IPEndPoint(IPAddress.Parse($"1.1.1.{connectionId}"), 10000);
            clientToEndpointMap.Add(connectionId, mappedEndpoint);
            endpointToClientMap.Add(mappedEndpoint, connectionId);
            return mappedEndpoint;
        }

        private byte[] CreateConnectionPayload()
        {
            var payload = new byte[sizeof(int) + RoomSecretBytes.Length + sizeof(int) + connectionSecretBytes.Length];
            BitConverter.TryWriteBytes(payload.AsSpan(0), RoomSecretBytes.Length);
            RoomSecretBytes.CopyTo(payload.AsSpan(sizeof(int)));
            BitConverter.TryWriteBytes(payload.AsSpan(sizeof(int) + RoomSecretBytes.Length), connectionSecretBytes.Length);
            connectionSecretBytes.CopyTo(payload.AsSpan(sizeof(int) + sizeof(int) + RoomSecretBytes.Length));
            return payload;
        }

        public IEnumerator ConnectToRelay(Action<bool> callback)
        {
            var connectionStartTime = DateTime.UtcNow;
            var payload = CreateConnectionPayload();
            while (DateTime.UtcNow - connectionStartTime <= TimeSpan.FromSeconds(10))
            {
                SendInternal(0, payload, payload.Length);
                yield return new WaitForSeconds(0.5f);
                var (endpoint, data, length) = socket.Receive();
                if (length < 6 || !endpoint.Equals(relayEndpoint))
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
                    callback?.Invoke(true);
                    yield break;
                }
                yield return new WaitForSeconds(1);
            }
            callback?.Invoke(false);
        }

        public void AssignHost(EndPoint hostEndPoint)
        {
            if (hostEndPoint == null)
                return;
           endpointToClientMap.Add(hostEndPoint, HostConnectionId);
           clientToEndpointMap.Add(HostConnectionId, hostEndPoint);
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
            data.AsSpan(0, size).CopyTo(sendBuffer.AsSpan().Slice(6, sendBuffer.Length - 6));
            socket.Send(relayEndpoint, sendBuffer, size + 6);
        }

        public RelaySocket WithConfiguration(byte connectionId, int roomId, string roomSecret, string connectionSecret)
        {
            this.roomId = roomId;
            ConnectionId = connectionId;
            connectionSecretBytes = System.Text.Encoding.UTF8.GetBytes(connectionSecret);
            RoomSecretBytes = Convert.FromBase64String(roomSecret);
            return this;
        }

        private const int RelayOverhead = 6;
        public int GetMTU() => socket.GetMTU() - RelayOverhead;

        public void Dispose()
        {
            socket.Dispose();
        }
    }
}