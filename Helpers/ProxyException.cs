using System;

namespace WebTranslationProxy.Helpers
{
    /// <summary>
    /// Custom exception for proxy related errors
    /// </summary>
    public class ProxyException : Exception
    {
        public ProxyException(string message) : base($"Proxy exceptions: {message}")
        {

        }
    }
}
