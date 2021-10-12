using HtmlAgilityPack;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using WebTranslationProxy.Helpers;
using WebTranslationProxy.Models.Configuration;

namespace WebTranslationProxy.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class ProxyController : ControllerBase
    {
        private readonly ILogger<ProxyController> logger;
        private readonly ProxyHelper proxyHelper;
        private readonly HTMLHelper htmlHelper;
        private readonly AppConfiguration appConfiguration;
        private readonly IHttpClientFactory clientFactory;

        public ProxyController(ILogger<ProxyController> logger, IOptions<AppConfiguration> appConfiguration,
            ProxyHelper proxyHelper, HTMLHelper htmlHelper, IHttpClientFactory clientFactory)
        {
            this.logger = logger;
            this.proxyHelper = proxyHelper;
            this.htmlHelper = htmlHelper;
            this.appConfiguration = appConfiguration.Value;
            this.clientFactory = clientFactory;
        }

        /// <summary>
        /// specify target url like this: /http/target.domain.com/path?queryStringParam=value
        /// </summary>
        /// <param name="scheme"></param>
        /// <param name="domain"></param>
        /// <param name="dynamicPath"></param>
        /// <returns></returns>
        [Route("/{scheme}/{domain}/{**dynamicPath}")]
        public async Task<IActionResult> Get(string scheme, string domain, string dynamicPath)
        {
            // TODO: Parameters of the method are not used. Update of the interfaces should be developed.
            // Currently, there is no way to transferring dynamic parameters from the request to parameters of the method.
            // Solution now is to read entire request URL using `Request.GetEncodedUrl()` and parse necessary parameters.
            try
            {
#if !DEBUG
                // if request comes form translation iframe
                // or from a page that has already been through the proxy
                if (Request.Headers.ContainsKey("Referer"))
                {
                    var referrer = Request.Headers["Referer"].ToString();

                    var allowedReferrers = appConfiguration.Configuration.AllowedReferrers.Where(allowed => referrer.StartsWith(allowed)).Any();
                    var selfReferring = referrer.StartsWith(appConfiguration.Configuration.PublicUrl);

                    if (!allowedReferrers && !selfReferring)
                    {
                        logger.LogError($"Invalid referrer: '{referrer}', check configuration: {nameof(appConfiguration.Configuration.AllowedReferrers)}");
                        throw new ProxyException("Forbidden");
                    }
                }
                else
                {
                    logger.LogError("Referrer not provided. Make sure you use proxy from iframe");
                    throw new ProxyException("Forbidden");
                }
#endif
                string encoderUrl = Request.GetEncodedUrl();
                Uri uri = proxyHelper.ProxyUriToRealUri(new Uri(encoderUrl));
                if (uri == null)
                {
                    logger.LogWarning($"Uri is not proxied, rewriting to Referrer: {encoderUrl}");
                    var originalUri = new UriBuilder(encoderUrl);
                    var newUri = new UriBuilder(Request.Headers["Referer"].ToString())
                    {
                        Path = originalUri.Path,
                        Query = originalUri.Query
                    };

                    uri = newUri.Uri;
                }

                if (appConfiguration.Configuration.ProxyPrefix.Length > 1 &&
                    uri.AbsolutePath.StartsWith(appConfiguration.Configuration.ProxyPrefix))
                {
                    throw new ProxyException("Loop detected, proxy loading itself.");
                }

                IPHostEntry hostEntry = null;

                try
                {
                    hostEntry = await Dns.GetHostEntryAsync(uri.DnsSafeHost);
                }
                catch (System.Net.Sockets.SocketException ex)
                {
                    logger.LogError(ex, $"Bad hostname: {uri.DnsSafeHost}");
                    throw new ProxyException("Bad address");
                }

                if (hostEntry == null)
                {
                    logger.LogError($"Failed to resolve hostname: {uri.DnsSafeHost}");
                    throw new ProxyException("Bad address");
                }

#if !DEBUG
                if (proxyHelper.IsPrivateUrl(hostEntry))
                {
                    logger.LogError($"Forbid private network access: {uri.DnsSafeHost}");
                    throw new ProxyException("Access to private network is denied.");
                }
#endif

                var request = new HttpRequestMessage(new HttpMethod(Request.Method), uri);

                if (request.Method == HttpMethod.Post)
                {
                    // post data
                    request.Content = new StreamContent(Request.Body);

                    if (Request.Headers.ContainsKey("Content-Type"))
                    {

                        string requestContentType = Request.Headers["Content-Type"].ToString();
                        var parts = requestContentType.Split(';');
                        var mediaType = new MediaTypeHeaderValue(parts[0].Trim());
                        if (parts.Length >= 2)
                        {
                            var charsetParts = parts[1].Split('=');
                            if (charsetParts.Length >= 2)
                            {
                                mediaType.CharSet = charsetParts[1];
                            }
                        }

                        request.Content.Headers.ContentType = mediaType;
                    }

                }

                //pass through some "safe" request headers
                if (Request.Headers.ContainsKey("User-Agent"))
                {
                    request.Headers.Add("User-Agent", Request.Headers["User-Agent"].ToString());
                }

                if (Request.Headers.ContainsKey("X-Requested-With"))
                {
                    request.Headers.Add("X-Requested-With", Request.Headers["X-Requested-With"].ToString());
                }

                if (Request.Headers.ContainsKey("Accept-Language"))
                {
                    request.Headers.Add("Accept-Language", Request.Headers["Accept-Language"].ToString());
                }

                if (Request.Headers.ContainsKey("Accept-Encoding"))
                {
                    //gzip and deflate work in proxy, 
                    //but other newer encoding will not be procesed correctly by proxy
                    //even if supported by browser
                    var browserAcceptsEncodings = Request.Headers["Accept-Encoding"].ToString();
                    var proxyAcceptsEncodings = browserAcceptsEncodings.Replace("sdch", "").Replace("br", "").Trim(new char[] { ' ', ',' });
                    request.Headers.Add("Accept-Encoding", proxyAcceptsEncodings);

                }

                if (Request.Headers.ContainsKey("Accept"))
                {
                    request.Headers.Add("Accept", Request.Headers["Accept"].ToString());
                }

                if (Request.Headers.ContainsKey("Pragma"))
                {
                    request.Headers.Add("Pragma", Request.Headers["Pragma"].ToString());
                }

                if (Request.Headers.ContainsKey("Cache-Control"))
                {
                    request.Headers.Add("Cache-Control", Request.Headers["Cache-Control"].ToString());
                }

                if (Request.Headers.ContainsKey("Referer"))
                {
                    var originalReferer = Request.Headers["Referer"].ToString();
                    if (originalReferer.IndexOf(appConfiguration.Configuration.ProxyPrefix, StringComparison.OrdinalIgnoreCase) > -1)
                    {
                        request.Headers.Referrer = proxyHelper.ProxyUriToRealUri(new Uri(originalReferer));
                    }
                    else
                    {
                        request.Headers.Referrer = new Uri(originalReferer);
                    }
                }

                var client = clientFactory.CreateClient("proxyClient");
                var response = await client.SendAsync(request);

                Response.StatusCode = (int)response.StatusCode;

                HeaderValues("Location", response)
                    .ForEach(headerValue => Response.Headers.Add("Location", proxyHelper.RealUriToProxyUri(headerValue, uri.Scheme, uri.Host)));

                MapResponeHeader("Cache-Control", response);
                MapResponeHeader("Pragma", response);

                // Normally compressed content should not be served to this proxy,
                // because request headers that this proxy sends do not tell that compression is supported.
                // But some sites don't respect that and send comprsesed content anyway.
                // And we need to decompress it to replace urls in html.
                bool compressed = false;
                string compressionType = "";

                foreach (var headerValue in response.Content.Headers.Where(header => header.Key.ToLower() == "content-encoding").SelectMany(header => header.Value))
                {
                    compressed = true;
                    compressionType = headerValue;
                    if (headerValue != "gzip" && headerValue == "deflate")
                    {
                        // We know how to decompress gzip & deflate
                        // everything else will be passed on as-is
                        // and client will have to decompress it
                        Response.Headers.Add("Content-Encoding", headerValue);
                    }
                }

                var stream = await response.Content.ReadAsStreamAsync();

                // decompress data, if possible
                if (compressed)
                {
                    if (compressionType == "gzip")
                    {
                        stream = new GZipStream(stream, CompressionMode.Decompress);
                        compressed = false;
                    }
                    else if (compressionType == "deflate")
                    {
                        stream = new DeflateStream(stream, CompressionMode.Decompress);
                        compressed = false;
                    }
                }

                var encoding = System.Text.Encoding.Default;
                if (response.Content.Headers.ContentType != null && response.Content.Headers.ContentType.CharSet != null)
                {
                    string charset = response.Content.Headers.ContentType.CharSet;

                    // quoted charset header is rarely used (miljons.com is special:D)
                    // but theoretically permitted in HTTP specification
                    charset = charset.Replace("\"", "");

                    if (charset == "utf8") // some people omit the "-"
                    {
                        charset = "utf-8";
                    }
                    encoding = System.Text.Encoding.GetEncoding(charset);
                }

                if (response.Content.Headers.ContentType != null)
                {
                    Response.ContentType = response.Content.Headers.ContentType.ToString();
                }

                if (response.Content.Headers.ContentType != null && response.Content.Headers.ContentType.MediaType == "text/html" && !compressed)
                {
                    // html tag handlig bugfixes
                    HtmlNode.ElementsFlags.Remove("option");
                    HtmlNode.ElementsFlags["form"] = HtmlNode.ElementsFlags["form"] & ~HtmlElementFlag.Empty;

                    var doc = new HtmlDocument
                    {
                        OptionWriteEmptyNodes = true
                    };
                    doc.Load(stream, encoding, true);

                    htmlHelper.TransformHTMLForProxy(doc, uri.Scheme, uri.Host);
                    await Response.WriteAsync(doc.DocumentNode.OuterHtml);
                }
                else if (
                    response.Content.Headers.ContentType != null
                    && response.Content.Headers.ContentType.MediaType == "text/css"
                    && !compressed)
                {
                    var reader = new System.IO.StreamReader(stream, encoding, true);
                    var css = reader.ReadToEnd();
                    reader.Close();
                    css = htmlHelper.ReplaceUrlsInCss(css, uri.Scheme, uri.Host);

                    // byte order mark for unicode,
                    // allows the browser choose the right encoding,
                    // because when webserver serves a static css file,
                    // encoding is not normally provided in http headers
                    await Response.WriteAsync(css);
                }
                /* It kinda looks like url rewriting in javascript gives more trouble than it solves
                 * 
                 * else if (response.Content.Headers.ContentType != null
                    && (response.Content.Headers.ContentType.MediaType == "text/javascript"
                    || response.Content.Headers.ContentType.MediaType == "application/javascript"
                    || response.Content.Headers.ContentType.MediaType == "application/x-javascript"))
                {
                    var reader = new System.IO.StreamReader(stream, encoding, true);
                    string javascript = reader.ReadToEnd();
                    context.Response.ContentEncoding = reader.CurrentEncoding;
                    reader.Close();
                    javascript = ReplaceUrlsInJavascript(javascript, context);
                    context.Response.Write(javascript);
                }*/
                else
                {
                    await stream.CopyToAsync(Response.Body);
                }
            }
            catch (Exception ex)
            {
                // Log all exceptions except incorrect web address errors which are not unexpected exceptions
                if (!(ex is ProxyException))
                {
                    logger.LogError(ex, "Failed to Proxy url: {0}", Request.GetEncodedUrl());
                }

                Response.Clear();

                return BadRequest(ex.Message);
            }

            return new EmptyResult();
        }


        private static List<string> HeaderValues(string headerKey, HttpResponseMessage response) => response.Headers.Where(header => header.Key == headerKey)
                    .SelectMany(header => header.Value).ToList();

        private void MapResponeHeader(string headerKey, HttpResponseMessage response) => HeaderValues(headerKey, response)
                    .ForEach(headerValue => Response.Headers.Add(headerKey, headerValue));
    }
}