using System;
using System.Net;
using System.Threading.Tasks;

namespace ArcaneKit.Nether
{
    public interface ISocket : IDisposable
    {
        void Send(EndPoint endPoint, byte[] data, int length);
        (EndPoint, byte[], int) Receive();
        int GetMTU();
    }
}