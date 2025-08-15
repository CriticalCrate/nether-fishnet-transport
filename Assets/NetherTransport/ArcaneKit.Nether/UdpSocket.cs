using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace ArcaneKit.Nether
{
    public class UdpSocket : ISocket
    {
        private readonly Socket socket;
        private readonly byte[] receiveBuffer = new byte[MTU];
        private EndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);

        private const int MTU = 1200;
        public int GetMTU() => MTU;

        public UdpSocket(ushort port = 0)
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            socket.Blocking = false;
        }

        public Task Setup(EndPoint serverEndpoint = null) => Task.CompletedTask;
        public void Send(EndPoint endPoint, byte[] data, int length)
        {
            socket.SendTo(data, length, SocketFlags.None, endPoint);
        }

        public (EndPoint, byte[], int) Receive()
        {
            if (socket.Available == 0)
                return (remoteEndPoint, Array.Empty<byte>(), 0);
            var received = socket.ReceiveFrom(receiveBuffer, ref remoteEndPoint);
            return received == 0 ? (remoteEndPoint, Array.Empty<byte>(), 0) : (remoteEndPoint, receiveBuffer, received);
        }

        public void Dispose()
        {
            socket.Dispose();
        }
    }
}