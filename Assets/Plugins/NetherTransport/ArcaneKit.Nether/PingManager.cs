using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace ArcaneKit.Nether
{
    public interface IPingTracker
    {
        long GetPingInTicks(EndPoint endPoint);
    }

    internal class PingManager : IPingTracker
    {
        private readonly Dictionary<EndPoint, Queue<long>> pingInTicks = new();
        private readonly Dictionary<EndPoint, Queue<PingRequest?>> lastSend = new();
        private readonly byte[] sendBuffer = new byte[2];
        private readonly ISocket socket;

        private const int MaxTrackedPings = 10;
        private const int PingMessageSize = 2;

        public PingManager(ISocket socket)
        {
            this.socket = socket;
        }

        public void OnClientConnected(EndPoint endpoint)
        {
            pingInTicks.Add(endpoint, new Queue<long>());
            lastSend.Add(endpoint, new Queue<PingRequest?>());
        }

        public void OnClientDisconnected(EndPoint endpoint)
        {
            pingInTicks.Remove(endpoint);
            lastSend.Remove(endpoint);
        }
        private int pings = 0;
        public void CheckForPendingPings()
        {
            foreach (var endpointToLastSend in lastSend)
            {
                switch (endpointToLastSend.Value.Count)
                {
                    case > 0 when endpointToLastSend.Value.Last()!.Value.Timestamp.AddMilliseconds(100) > DateTime.UtcNow:
                        continue;
                    case > MaxTrackedPings:
                        endpointToLastSend.Value.Dequeue();
                        break;
                }
                var lastPingRequest = endpointToLastSend.Value.LastOrDefault();
                var pingRequest = new PingRequest
                    { Sequence = (byte)(lastPingRequest != null ? lastPingRequest.Value.Sequence + 1 : 0), Timestamp = DateTime.UtcNow };
                endpointToLastSend.Value.Enqueue(pingRequest);
                sendBuffer[0] = (byte)MessageType.Ping;
                sendBuffer[1] = pingRequest.Sequence;
                socket.Send(endpointToLastSend.Key, sendBuffer, PingMessageSize);
            }
        }

        public void PingReceived(EndPoint endpoint, byte[] message, int length)
        {
            if (length != PingMessageSize)
                return;
            message[0] = (byte)MessageType.Pong;
            socket.Send(endpoint, message, length);
        }

        public void PongReceived(EndPoint endpoint, byte[] message, int length)
        {
            if (length != PingMessageSize)
                return;
            if (!lastSend.TryGetValue(endpoint, out var pingRequests))
                return;
            var pingRequest = pingRequests.FirstOrDefault(x => x.HasValue && x.Value.Sequence == message[1]);
            if (pingRequest == null)
                return;
            if (!pingInTicks.TryGetValue(endpoint, out var queue))
                return;
            if (queue.Count > MaxTrackedPings)
                queue.Dequeue();
            queue.Enqueue((long)(DateTime.UtcNow - pingRequest.Value.Timestamp).TotalMilliseconds * 10000);
        }

        public override string ToString()
        {
            StringBuilder builder = new();
            foreach (var ping in pingInTicks)
                builder.AppendLine($"{ping.Key}: {ping.Value.Sum() / (float)ping.Value.Count / 10000f}");
            return builder.ToString();
        }

        public long GetPingInTicks(EndPoint endPoint)
        {
            if (pingInTicks.TryGetValue(endPoint, out var queue) && queue.Count > 0)
                return queue.Sum() / queue.Count;
            return 0;
        }

        private struct PingRequest
        {
            public byte Sequence;
            public DateTime Timestamp;
        }

    }
}