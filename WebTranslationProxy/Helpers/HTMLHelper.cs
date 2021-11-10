using HtmlAgilityPack;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebTranslationProxy.Models.Configuration;

namespace WebTranslationProxy.Helpers
{
    public class HTMLHelper
    {
        private readonly HashSet<string> ignoreTextInTags = new HashSet<string>
        {
            "script",
            "style",
            "var",
            "kbd",
            "samp",
            "code"
        };

        private readonly Regex cssUrlRegex = new Regex("([u|U][r|R][l|L]\\s*?\\(\\s*?['|\"]?)(.*?)(['|\"]?\\s*?\\))");
        private readonly Regex cssImportRegex = new Regex("(@[i|I][m|M][p|P][o|O][r|R][t|T]\\s*?['|\"])(.*?)(['|\"])");

        private readonly Regex javascriptAbsoluteUrlRegex = new Regex(@"([""']{1})((?:(?:http:\/\/)|(?:https:\/\/)).*?)([""']{1})");
        private readonly Regex javascriptDirectRelativeRedirects = new Regex(@"(\n\s*window.location.href\s*=\s*[""']{1}\s*)(\/[^\n]*)([""']{1})");

        readonly static List<KeyValuePair<string, List<string>>> UriElementsAndAttributes = new List<KeyValuePair<string, List<string>>>
        {
            new KeyValuePair<string, List<string>>("a", new List<string> { "href" }),
            new KeyValuePair<string, List<string>>("area", new List<string> { "href" }),
            new KeyValuePair<string, List<string>>("link", new List<string> { "href" }),
            new KeyValuePair<string, List<string>>("img", new List<string> { "src", "longdesc", "srcset" }),
            new KeyValuePair<string, List<string>>("object", new List<string> { "codebase", "data" }),
            new KeyValuePair<string, List<string>>("q", new List<string> { "cite" }),
            new KeyValuePair<string, List<string>>("blockquote", new List<string> { "cite" }),
            new KeyValuePair<string, List<string>>("ins", new List<string> { "cite" }),
            new KeyValuePair<string, List<string>>("del", new List<string> { "cite" }),
            new KeyValuePair<string, List<string>>("form", new List<string> { "action" }),
            new KeyValuePair<string, List<string>>("input", new List<string> { "src" }),
            new KeyValuePair<string, List<string>>("head", new List<string> { "profile" }),
            new KeyValuePair<string, List<string>>("script", new List<string> { "src" }),
            new KeyValuePair<string, List<string>>("iframe", new List<string> { "src" }),
            new KeyValuePair<string, List<string>>("base", new List<string> { "href" })
        };

        /// <summary>
        /// Here are elements and their attributes which are not proxied through this proxy. Instead they are served from original domain.
        /// </summary>
        private readonly Dictionary<string, HashSet<string>> AttributesInElementsWithoutProxy = new Dictionary<string, HashSet<string>>();

        readonly static List<string> eventAttributes = new List<string>
        {
            "onclick",
            "ondblclick",
            "onmousedown",
            "onmouseup",
            "onmouseover",
            "onmousemove",
            "onmouseout",
            "onkeypress",
            "onkeydown",
            "onkeyup",
            "onfocus",
            "onblur",
            "onload",
            "onunload",
            "onsubmit",
            "onreset",
            "onselect",
            "onchange"
        };

        private readonly ProxyHelper proxyHelper;
        private readonly ILogger logger;
        private readonly AppConfiguration appConfiguration;

        public HTMLHelper(ProxyHelper proxyHelper, ILogger<HTMLHelper> logger, IOptions<AppConfiguration> appConfiguration)
        {
            this.proxyHelper = proxyHelper;
            this.logger = logger;
            this.appConfiguration = appConfiguration.Value;

            if (!appConfiguration.Value.Configuration.WebsiteProxyProxyStaticAssets)
            {
                AttributesInElementsWithoutProxy["link"] = new HashSet<string>() { "href" };
                AttributesInElementsWithoutProxy["script"] = new HashSet<string>() { "src" };
                AttributesInElementsWithoutProxy["img"] = new HashSet<string>() { "src", "longdesc", "srcset" };
            }
        }

        /// <summary>
        /// Transform HTML so that all resources (JS/ CSS/ ... maybe api later on?) will be used from proxy to prevent cross-domain resource gathering
        /// </summary>
        /// <param name="doc"></param>
        /// <param name="scheme"></param>
        /// <param name="domain"></param>
        public void TransformHTMLForProxy(HtmlDocument doc, string scheme, string domain)
        {
            // language detection
            var htmlElement = doc.DocumentNode.SelectSingleNode("/html");
            if (htmlElement != null)
            {
                Encoding textEncoding = doc.StreamEncoding;

                if (doc.DeclaredEncoding != null)
                {
                    // if stream was detected as unicode,
                    // it is not likely that the encoding declared in the document is actually relevant
                    if (textEncoding != Encoding.UTF8
                        && textEncoding != Encoding.Unicode
                        && textEncoding != Encoding.UTF32)
                    {
                        textEncoding = doc.DeclaredEncoding;
                    }
                }
                else
                {
                    // some use <meta charset=""> instead of <meta http-equiv="Content-Type"
                    var charsetNode = doc.DocumentNode.SelectSingleNode("/html/head/meta[@charset]");
                    if (charsetNode != null)
                    {
                        var charset = charsetNode.GetAttributeValue("charset", null);
                        if (charset != null)
                        {
                            try
                            {
                                textEncoding = Encoding.GetEncoding(charset);
                            }
                            catch (Exception ex)
                            {
                                logger.LogWarning(ex, "Failed to set text encoding with charset: {0}", charset);
                            }
                        }
                    }
                }

                StringBuilder stringBuilder = new StringBuilder();
                ExtractText(
                    doc.DocumentNode.ChildNodes,
                    stringBuilder,
                    doc.StreamEncoding,
                    textEncoding,
                    limit: 10000
                );

                string text = Regex.Replace(stringBuilder.ToString(), "\\s+", " "); //compress whitespace

                // remove header and footer (it is likely to contain english text)
                bool cutTop = text.Length > 300;
                bool cutBottom = cutTop && text.Length != 10000;
                if (cutTop)
                {
                    text = text.Substring(Convert.ToInt32(text.Length * 0.2));
                }
                if (cutBottom)
                {
                    text = text.Substring(0, Convert.ToInt32(text.Length * 0.8));
                }
            }

            var refreshMetadata = doc.DocumentNode.SelectNodes("/html/head/meta[@http-equiv='refresh']");


            if (refreshMetadata != null) {
                foreach (var meta in refreshMetadata)
                {
                    var attr = meta.GetAttributeValue("content", "");
                    var urlContents = Regex.Split(attr, @"url\s*=");

                    var newUrl = proxyHelper.RealUriToProxyUri(urlContents[1], scheme, domain, canProxyToSelf:true);
                    urlContents[1] = newUrl;

                    meta.SetAttributeValue("content", string.Join("url=", urlContents));
                }
            }

            // replace urls in elemets/attributes predefined in UriElementsAndAttributes
            foreach (var element in UriElementsAndAttributes)
            {
                var nodes = doc.DocumentNode.SelectNodes("//" + element.Key);
                if (nodes != null)
                {
                    foreach (var node in nodes)
                    {
                        foreach (var attributeName in element.Value)
                        {
                            // Attribute that needs to be served from original domain. 
                            var canProxyToSelf = true;
                            if (AttributesInElementsWithoutProxy.TryGetValue(element.Key, out HashSet<string> keyAttributes))
                            {
                                canProxyToSelf = !keyAttributes.Contains(attributeName);
                            }

                            var attributeValue = node.GetAttributeValue(attributeName, (string)null);
                            if (attributeValue != null)
                            {
                                if (attributeName == "srcset" && node.Name == "img")
                                {
                                    var changedSources = attributeValue.Trim().Split(",").Select(source =>
                                    {
                                        var parts = source.Split(" ");
                                        parts[0] = proxyHelper.RealUriToProxyUri(parts[0], scheme, domain, canProxyToSelf);
                                        return string.Join(" ", parts);
                                    });

                                    node.SetAttributeValue(attributeName, string.Join(",", changedSources));
                                }
                                else
                                {
                                    node.SetAttributeValue(attributeName, proxyHelper.RealUriToProxyUri(attributeValue, scheme, domain, canProxyToSelf));
                                }
                            }
                        }
                    }
                }
            }

            // replace urls in css style tags
            var styleNodes = doc.DocumentNode.SelectNodes("//style[@type='text/css']");
            if (styleNodes != null)
            {
                foreach (var node in styleNodes)
                {
                    foreach (HtmlNode child in node.ChildNodes)
                    {
                        if (child is HtmlTextNode textNode)
                        {
                            textNode.Text = ReplaceUrlsInCss(textNode.Text, scheme, domain);
                        }
                    }
                }
            }

            // replace urls in css style attributes
            var nodesWithStyle = doc.DocumentNode.SelectNodes("//*/@style");
            if (nodesWithStyle != null)
            {
                foreach (var node in nodesWithStyle)
                {
                    node.SetAttributeValue("style", ReplaceUrlsInCss(node.GetAttributeValue("style", null), scheme, domain));
                }
            }

            // replace urls in javascript in  <script> tags
            var scriptNodes = doc.DocumentNode.SelectNodes("//script[@type='text/javascript']");
            if (scriptNodes != null)
            {
                foreach (var node in scriptNodes)
                {
                    foreach (HtmlNode child in node.ChildNodes)
                    {
                        if (child is HtmlTextNode textNode)
                        {
                            textNode.Text = ReplaceUrlsInJavascript(textNode.Text, scheme, domain);
                        }
                    }
                }
            }

            // replace urls in javascript in event handler attributes
            foreach (var eventAttributeName in eventAttributes)
            {
                var nodesWithEventHandler = doc.DocumentNode.SelectNodes("//*/@" + eventAttributeName);
                if (nodesWithEventHandler != null)
                {
                    foreach (var node in nodesWithEventHandler)
                    {
                        var eventAttributeValue = node.GetAttributeValue(eventAttributeName, null);
                        if (eventAttributeValue != null)
                        {
                            node.SetAttributeValue(eventAttributeName, ReplaceUrlsInJavascript(eventAttributeValue, scheme, domain));
                        }
                    }
                }
            }

            // replace urls in a[href=javascript:]
            var aNodes = doc.DocumentNode.SelectNodes("//a");
            if (aNodes != null)
            {
                foreach (var node in aNodes)
                {
                    var attributeValue = node.GetAttributeValue("href", (string)null);
                    if (attributeValue != null && attributeValue.StartsWith("javascript:"))
                    {
                        node.SetAttributeValue("href", ReplaceUrlsInJavascript(attributeValue, scheme, domain));
                    }

                    // make target="_top" and target="_blank" work nicely with iframe in TranslatePage.aspx 
                    attributeValue = node.GetAttributeValue("target", (string)null);
                    if (attributeValue != null && (attributeValue == "_top" || attributeValue == "_blank"))
                    {
                        node.SetAttributeValue("target", "letsmtTranslatePageIframe");
                    }

                    // remove rel="external", so that don't navigate away from translate proxy iframe
                    attributeValue = node.GetAttributeValue("rel", (string)null);
                    if (attributeValue != null && attributeValue == "external")
                    {
                        node.Attributes.Remove("rel");
                    }
                }
            }
        }


        /// <summary>
        /// Replaces urls in javascript, but only urls starting with http:// or https://
        /// Urls starting with "/" or "//" are widely used,
        /// but are impossible to distinguish from strings used for different purposes.
        /// </summary>
        public string ReplaceUrlsInJavascript(string javascript, string scheme, string domain)
        {
            string replacer(Match match)
            {
                string url = match.Groups[2].Value;
                url = proxyHelper.RealUriToProxyUri(url, scheme, domain);

                return match.Groups[1].Value
                    + url
                    + match.Groups[3].Value;
            }

            javascript = javascriptAbsoluteUrlRegex.Replace(javascript, replacer);
            javascript = javascriptDirectRelativeRedirects.Replace(javascript, replacer);
            return javascript;
        }

        /// <summary>
        /// Replace Urls in CSS
        /// </summary>
        /// <param name="css"></param>
        /// <param name="scheme"></param>
        /// <param name="domain"></param>
        /// <returns></returns>
        public string ReplaceUrlsInCss(string css, string scheme, string domain)
        {
            string replacer(Match match)
            {
                string url = match.Groups[2].Value;
                if (url.StartsWith("http://") || url.StartsWith("https://") || url.StartsWith("/") || url.StartsWith("//")) // relative urls should be ok as-is
                {
                    url = proxyHelper.RealUriToProxyUri(url, scheme, domain, canProxyToSelf: appConfiguration.Configuration.WebsiteProxyProxyStaticAssets);
                }

                return match.Groups[1].Value + url + match.Groups[3].Value;
            }

            css = cssImportRegex.Replace(css, replacer);
            css = cssUrlRegex.Replace(css, replacer);
            return css;
        }

        /// <summary>
        /// Extract text from document
        /// </summary>
        /// <param name="nodes"></param>
        /// <param name="stringBuilder"></param>
        /// <param name="usedEncoding"></param>
        /// <param name="declaredEncoding"></param>
        /// <param name="limit"></param>
        public void ExtractText(HtmlNodeCollection nodes, StringBuilder stringBuilder, Encoding usedEncoding, Encoding declaredEncoding, int? limit = null)
        {
            foreach (var node in nodes)
            {
                if (limit != null && stringBuilder.Length >= limit)
                {
                    stringBuilder.Length = limit.Value;
                    break;
                }
                if (node.NodeType == HtmlNodeType.Element)
                {
                    if (!ignoreTextInTags.Contains(node.Name.ToLowerInvariant()))
                    {
                        ExtractText(node.ChildNodes, stringBuilder, usedEncoding, declaredEncoding, limit);
                    }
                }
                else if (node.NodeType == HtmlNodeType.Text)
                {
                    stringBuilder.Append(' ');
                    string text = ((HtmlTextNode)node).Text;
                    if (usedEncoding != declaredEncoding)
                    {
                        text = declaredEncoding.GetString(usedEncoding.GetBytes(text));
                    }
                    try
                    {
                        text = HtmlEntity.DeEntitize(text);
                    }
                    catch (Exception ex)
                    {
                        //sometimes it fails when text contains "&" that is followed by ";"
                        //but is not an actual entity escape, like &quot;
                        //example: PHP ir pamats (OOP & MVC);
                        //bug should be fixed in the lib, there are similar bugs reported in their bug tracker
                        logger.LogWarning(ex, "Failed to process text: {0}", text);
                    }

                    stringBuilder.Append(text);
                    stringBuilder.Append(' ');
                }
            }
        }
    }
}
