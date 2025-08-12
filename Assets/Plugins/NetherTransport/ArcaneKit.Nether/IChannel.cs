using System;

namespace ArcaneKit.Nether
{
    public interface IChannel : IDisposable
    {
        bool TryReceive(byte[] data, int length, out byte[] result, out int resultLength);
        void Send(byte[] data, int length);
        void Update(uint currentMs);
    }
}