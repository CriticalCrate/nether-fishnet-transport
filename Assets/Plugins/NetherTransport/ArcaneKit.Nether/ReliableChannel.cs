using System;
using System.Net;
using System.Runtime.InteropServices;
using kcp;

namespace ArcaneKit.Nether
{
    public class ReliableChannel : IChannel
    {
        private int Mtu;

        private readonly EndPoint remoteEndPoint;
        private readonly ISocket socket;
        private unsafe readonly IKCPCB* kcp;
        private readonly byte[] receiveBuffer;
        private readonly byte[] sendBuffer;
        private readonly GCHandle handle;

        public ReliableChannel(ISocket socket, EndPoint remoteEndPoint)
        {
            this.remoteEndPoint = remoteEndPoint;
            Mtu = socket.GetMTU() - 1;
            unsafe
            {
                this.socket = socket;
                receiveBuffer = new byte[4 * 1024 * 1024];
                sendBuffer = new byte[1500];
                handle = GCHandle.Alloc(this);
                kcp = KCP.ikcp_create(0, (void*)GCHandle.ToIntPtr(handle));
                KCP.ikcp_setmtu(kcp, Mtu);
                KCP.ikcp_nodelay(kcp, 1, 20, 2, 1);
                kcp->output = &UdpOutput;
            }
        }

        public unsafe void Send(byte[] data, int length)
        {
            fixed (byte* ptr = data)
            {
                if (KCP.ikcp_send(kcp, ptr, length) < 0)
                    throw new ReliableChannelException("Failed to send data");
            }
        }

        public unsafe void Update(uint currentMs)
        {
            KCP.ikcp_update(kcp, currentMs);
        }

        public unsafe bool TryReceive(byte[] data, int length, out byte[] result, out int resultLength)
        {
            result = null;
            resultLength = 0;
            fixed (byte* ptr = data)
            {
                KCP.ikcp_input(kcp, ptr + 1, length - 1);
            }

            var peek = KCP.ikcp_peeksize(kcp);
            if (peek < 0)
                return false;

            fixed (byte* recv = receiveBuffer)
            {
                var received = KCP.ikcp_recv(kcp, recv, receiveBuffer.Length);
                if (received <= 0)
                    return false;
                result = receiveBuffer;
                resultLength = received;
                return true;
            }
        }

        private static unsafe int UdpOutput(byte* buf, int length, IKCPCB* kcp, void* user)
        {
            try
            {
                var handle = GCHandle.FromIntPtr((IntPtr)user);
                var instance = (ReliableChannel)handle.Target!;

                Marshal.Copy((IntPtr)buf, instance.sendBuffer, 1, length);
                instance.sendBuffer[0] = (byte)MessageType.Reliable;

                instance.socket.Send(instance.remoteEndPoint, instance.sendBuffer, length + 1);
                return 0;
            }
            catch
            {
                return -1;
            }
        }

        public void Dispose()
        {
            if (handle.IsAllocated)
                handle.Free();
        }
    }
}