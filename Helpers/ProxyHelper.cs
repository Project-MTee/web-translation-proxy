using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Net;
using WebTranslationProxy.Models.Configuration;

namespace WebTranslationProxy.Helpers
{
    public class ProxyHelper
    {
        // http://en.wikipedia.org/wiki/Private_network
        private static readonly List<Tuple<uint, uint>> privateSubnets = new List<Tuple<uint, uint>> {
            new Tuple<uint, uint>(
                BitConverter.ToUInt32(new byte[] {10,0,0,0},0), //10.0.0.0/8
                BitConverter.ToUInt32(new byte[] {255,0,0,0},0) ),
            new Tuple<uint, uint>(
                BitConverter.ToUInt32(new byte[] {172,16,0,0},0), //172.16.0.0/12
                BitConverter.ToUInt32(new byte[] {255,240,0,0},0) ),
            new Tuple<uint, uint>(
                BitConverter.ToUInt32(new byte[] {192,168,0,0},0), //192.168.0.0/16
                BitConverter.ToUInt32(new byte[] {255,255,0,0},0) ),
            new Tuple<uint, uint>(
                BitConverter.ToUInt32(new byte[] {127,0,0,1},0), //127.0.0.1
                BitConverter.ToUInt32(new byte[] {255,255,255,255},0) )
        };

        private readonly AppConfiguration appConfiguration;
        private readonly ILogger logger;

        private string ControllerPath => appConfiguration.Configuration.ProxyPrefix;

        public ProxyHelper(IOptions<AppConfiguration> appConfiguration, ILogger<ProxyHelper> logger)
        {
            this.appConfiguration = appConfiguration.Value;
            this.logger = logger;
        }

        /// <summary>
        /// Checks if host is in private network. We would like to proxy only publically available sites.
        /// </summary>
        /// <param name="hostEntry"></param>
        /// <returns></returns>
        public bool IsPrivateUrl(IPHostEntry hostEntry)
        {
            // do not allow to access intranet or localhost sites (does not work with IPV6)
            foreach (var ipAddress in hostEntry.AddressList)
            {
                if (ipAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) // IPV4
                {
                    var addressInteger = BitConverter.ToUInt32(ipAddress.GetAddressBytes(), 0);

                    foreach (var subnet in privateSubnets)
                    {
                        if (subnet.Item1.Equals(addressInteger & subnet.Item2)) //subnetStartAddress == adress & subnetMask
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Change uri that has been proxied to original uri
        /// </summary>
        /// <param name="proxyUri"></param>
        /// <returns></returns>
        public Uri ProxyUriToRealUri(Uri proxyUri)
        {
            int proxyIndex = proxyUri.PathAndQuery.IndexOf(ControllerPath, StringComparison.OrdinalIgnoreCase);
            if (proxyIndex > -1)
            {
                int schemeEndIndex = proxyUri.PathAndQuery.IndexOf("/", proxyIndex + ControllerPath.Length);
                if (schemeEndIndex > -1)
                {
                    var scheme = proxyUri.PathAndQuery.Substring(proxyIndex + ControllerPath.Length, schemeEndIndex - proxyIndex - ControllerPath.Length);
                    if (scheme == "http" || scheme == "https")
                    {
                        try
                        {
                            return new Uri(scheme + "://" + proxyUri.PathAndQuery.Substring(schemeEndIndex + 1));
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Failed to Translate Proxy uri to original uri for page: {0}", proxyUri.PathAndQuery);
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Change all Uri to proxy uri to guarantee that all resources are same-domain (proxy domain) resources
        /// </summary>
        /// <param name="realUri"></param>
        /// <param name="scheme"></param>
        /// <param name="domain"></param>
        /// <param name="proxyToSelf">Proxy requests to this proxy</param>
        /// <returns></returns>
        public string RealUriToProxyUri(string realUri, string scheme, string domain, bool canProxyToSelf = true)
        {
            try
            {
                if (realUri != null)
                {
                    string uriFromProxy = null;
                    if (realUri.StartsWith("/"))
                    {
                        if (realUri.StartsWith("//"))
                        {
                            var parsedUri = new Uri(realUri);

                            if (canProxyToSelf && domain == parsedUri.DnsSafeHost)
                            {
                                uriFromProxy = string.Format("{0}{1}{2}/{3}{4}", appConfiguration.Configuration.PublicUrl, ControllerPath, scheme, parsedUri.DnsSafeHost, parsedUri.PathAndQuery);
                            }
                            else
                            {
                                uriFromProxy = realUri;
                            }
                        }
                        else
                        {
                            if (canProxyToSelf)
                            {
                                uriFromProxy = string.Format("{0}{1}{2}/{3}{4}", appConfiguration.Configuration.PublicUrl, ControllerPath, scheme, domain, realUri);
                            }
                            else
                            {
                                uriFromProxy = string.Format($"{scheme}://{domain}{realUri}");
                            }
                        }
                    }
                    else if (realUri.StartsWith("http://"))
                    {
                        var parsedUri = new Uri(realUri);

                        if (canProxyToSelf)
                        {
                            uriFromProxy = string.Format("{0}{1}{2}/{3}{4}", appConfiguration.Configuration.PublicUrl, ControllerPath, "http", parsedUri.DnsSafeHost, parsedUri.PathAndQuery);
                        }
                        else
                        {
                            uriFromProxy = realUri;
                        }
                    }
                    else if (realUri.StartsWith("https://"))
                    {
                        var parsedUri = new Uri(realUri);

                        if (canProxyToSelf)
                        {
                            uriFromProxy = string.Format("{0}{1}{2}/{3}{4}", appConfiguration.Configuration.PublicUrl, ControllerPath, "https", parsedUri.DnsSafeHost, parsedUri.PathAndQuery);
                        }
                        else
                        {
                            uriFromProxy = realUri;
                        }
                    }

                    if (uriFromProxy != null)
                    {
                        return uriFromProxy;
                    }
                }
            }
            catch
            {
                return realUri;
            }

            return realUri;
        }
    }
}
