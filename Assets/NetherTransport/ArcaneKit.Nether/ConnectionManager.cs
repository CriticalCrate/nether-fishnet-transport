using System;
using System.Collections.Generic;
using System.Net;

namespace ArcaneKit.Nether
{

    internal interface IConnectionManager : IConnectionNotifier
    {
        void CheckConnectionsTimeouts();
        void PacketReceived(EndPoint endpoint, byte[] packet, int length);
    }

    public interface IConnectionNotifier
    {
        event Action<EndPoint>? OnDisconnected;
        event Action<EndPoint>? OnConnected;
    }

    internal class ConnectionManager : IConnectionManager
    {
        public event Action<EndPoint>? OnDisconnected;
        public event Action<EndPoint>? OnConnected;
        public IReadOnlyCollection<EndPoint> Connections => connections.Keys;

        private readonly Dictionary<EndPoint, DateTime> connections = new();
        private readonly Queue<EndPoint> timeoutQueue = new();
        private const int TimeoutSeconds = 10;

        public void CheckConnectionsTimeouts()
        {
            while (timeoutQueue.Count > 0)
            {
                var endpoint = timeoutQueue.Dequeue();
                if (!connections.TryGetValue(endpoint, out DateTime lastPacketTime))
                {
                    continue;
                }

                if (lastPacketTime.AddSeconds(TimeoutSeconds) < DateTime.UtcNow)
                {
                    if (!connections.Remove(endpoint, out _))
                        return;
                    OnDisconnected?.Invoke(endpoint);
                    continue;
                }
                timeoutQueue.Enqueue(endpoint);
                break;
            }
        }

        public void PacketReceived(EndPoint endpoint, byte[] packet, int length)
        {
            if (length <= 0)
                return;

            if (packet[0] == (byte)MessageType.Disconnect)
            {
                if (!connections.Remove(endpoint, out _))
                    return;
                OnDisconnected?.Invoke(endpoint);
                return;
            }

            if (packet[0] == (byte)MessageType.Connect)
            {
                if (!connections.TryGetValue(endpoint, out _))
                {
                    connections.Add(endpoint, DateTime.UtcNow);
                    timeoutQueue.Enqueue(endpoint);
                    OnConnected?.Invoke(endpoint);
                    return;
                }
            }

            if (connections.ContainsKey(endpoint))
                connections[endpoint] = DateTime.UtcNow;
        }
    }
}