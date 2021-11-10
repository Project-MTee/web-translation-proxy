using System.Collections.Generic;

namespace WebTranslationProxy.Models.Configuration
{
    public class Configuration
    {
        /// <summary>
        /// Public Url of this website. For example https://www.google.com/
        /// </summary>
        public string PublicUrl { get; set; }
        /// <summary>
        /// Path prefix of proxy service URL after which URL follows that has to be loaded via proxy
        /// </summary>
        public string ProxyPrefix { get; set; }
        /// <summary>
        /// Allow to proxy static assets through this proxy.
        /// Some content can have CORS, so not all content can be left without proxy.
        /// This will be mainly for CSS and IMG assets
        /// </summary>
        public bool WebsiteProxyProxyStaticAssets { get; set; }
        /// <summary>
        /// Allowed referrers that can use this web proxy.
        /// Requests that does not match this referrer will be blocked.
        /// </summary>
        public List<string> AllowedReferrers { get; set; } = new List<string>();

    }
}
