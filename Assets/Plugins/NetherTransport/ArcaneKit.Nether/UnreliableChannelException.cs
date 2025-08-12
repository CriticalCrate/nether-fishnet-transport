using System;

namespace ArcaneKit.Nether
{
    public class UnreliableChannelException : Exception
    {
        public UnreliableChannelException(string message) : base(message)
        {
        }

    }
}