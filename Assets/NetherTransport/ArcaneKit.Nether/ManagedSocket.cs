using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using UnityEngine;

namespace ArcaneKit.Nether
{
    public class ManagedSocket : IConnectionNotifier, IDisposable
    {
        public event Action<EndPoint>? OnDisconnected;
        public event Action<EndPoint>? OnConnected;

        public readonly ISocket InternalSocket;
        private readonly ConnectionManager connectionManager;
        private readonly Dictionary<EndPoint, ReliableChannel> reliableChannels = new();
        private readonly Dictionary<EndPoint, UnreliableChannel> unreliableChannels = new();
        private readonly DateTime startTime;
        private bool isHost = true;

        public ManagedSocket(ISocket internalSocket)
        {
            InternalSocket = internalSocket;
            connectionManager = new ConnectionManager();
            connectionManager.OnConnected += HandleOnConnected;
            connectionManager.OnDisconnected += HandleOnDisconnected;
            startTime = DateTime.UtcNow;
        }

        private void RespondToConnect(EndPoint endPoint)
        {
            InternalSocket.Send(endPoint, new[] { (byte)MessageType.Connect }, 1);
        }

        public IEnumerator Connect(EndPoint endPoint, Action<bool> callback)
        {
            isHost = false;
            var attempts = 3;
            while (attempts > 0)
            {
                attempts--;
                InternalSocket.Send(endPoint, new[] { (byte)MessageType.Connect }, 1);
                yield return new WaitForSeconds(1);
                Pool(out _, out _, out _, out _);
                if (!GetConnectionState(endPoint).HasValue)
                    continue;
                callback?.Invoke(true);
                yield break;
            }
            callback?.Invoke(false);
        }

        public void Disconnect(EndPoint endPoint)
        {
            InternalSocket.Send(endPoint, new[] { (byte)MessageType.Disconnect }, 1);
            connectionManager.PacketReceived(endPoint, new[] { (byte)MessageType.Disconnect }, 1);
        }

        public ConnectionState? GetConnectionState(EndPoint endPoint)
        {
            if (reliableChannels.ContainsKey(endPoint))
                return new ConnectionState
                {
                    // LatencyInTicks = pingManager.GetPingInTicks(endPoint),
                    RemoteEndPoint = endPoint
                };
            return null;
        }

        private void HandleOnDisconnected(EndPoint endpoint)
        {
            if (reliableChannels.Remove(endpoint, out var reliableChannel))
                reliableChannel.Dispose();
            if (unreliableChannels.Remove(endpoint, out var unreliableChannel))
                unreliableChannel.Dispose();

            OnDisconnected?.Invoke(endpoint);
        }
        private void HandleOnConnected(EndPoint endpoint)
        {
            reliableChannels.TryAdd(endpoint, new ReliableChannel(InternalSocket, endpoint));
            unreliableChannels.TryAdd(endpoint, new UnreliableChannel(InternalSocket, endpoint));
            OnConnected?.Invoke(endpoint);
        }

        public void SendReliable(EndPoint endpoint, byte[] data, int length)
        {
            if (!reliableChannels.TryGetValue(endpoint, out var reliableChannel))
            {
                reliableChannel = new ReliableChannel(InternalSocket, endpoint);
                reliableChannels.Add(endpoint, reliableChannel);
            }
            reliableChannel.Send(data, length);
        }

        public void SendUnreliable(EndPoint endpoint, byte[] data, int length)
        {
            if (!unreliableChannels.TryGetValue(endpoint, out var unreliableChannel))
            {
                unreliableChannel = new UnreliableChannel(InternalSocket, endpoint);
                unreliableChannels.Add(endpoint, unreliableChannel);
            }
            unreliableChannel.Send(data, length);
        }

        public bool Pool(out EndPoint sender, out byte[] packet, out int packetLength, out MessageType type)
        {
            sender = null;
            packet = null;
            packetLength = 0;
            type = MessageType.Unreliable;
            connectionManager.CheckConnectionsTimeouts();
            foreach (var reliableChannel in reliableChannels)
                reliableChannel.Value.Update((uint)(DateTime.UtcNow - startTime).TotalMilliseconds);

            while (true)
            {
                var (endpoint, data, length) = InternalSocket.Receive();
                if (length <= 0)
                    return false;
                sender = endpoint;
                connectionManager.PacketReceived(sender, data, length);
                switch (data[0])
                {
                    case (byte)MessageType.Connect:
                    {
                        if (!isHost)
                            return false;
                        RespondToConnect(sender);
                        return false;
                    }
                    case (byte)MessageType.Reliable when reliableChannels.TryGetValue(endpoint, out var reliableChannel):
                    {
                        type = MessageType.Reliable;
                        return reliableChannel.TryReceive(data, length, out packet, out packetLength);
                    }
                    case (byte)MessageType.Unreliable when unreliableChannels.TryGetValue(endpoint, out var unreliableChannel):
                    {
                        type = MessageType.Unreliable;
                        return unreliableChannel.TryReceive(data, length, out packet, out packetLength);
                    }
                }
            }
        }

        public void Dispose()
        {
            foreach (var reliableChannel in reliableChannels)
                reliableChannel.Value.Dispose();
            foreach (var unreliableChannel in unreliableChannels)
                unreliableChannel.Value.Dispose();
            foreach (var endpoint in connectionManager.Connections)
                InternalSocket.Send(endpoint, new[] { (byte)MessageType.Disconnect }, 1);
            InternalSocket.Dispose();
        }
    }

    public struct ConnectionState
    {
        public EndPoint RemoteEndPoint { get; set; }
    }
}