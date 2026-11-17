using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Plugins.TuneIn.Configuration;
using Moq;
using Xunit;

namespace MediaBrowser.Plugins.TuneIn.Tests
{
    public class BrowsePaginationTests
    {
        [Theory]
        [InlineData("http", "s:~#")]
        [InlineData("http", "p:~#")]
        [InlineData("http", "s%3A~#")]
        [InlineData("http", "p%3A~#")]
        [InlineData("https", "s:~#")]
        public async Task MoreStationsPreservesHashAlphabetFilter(string scheme, string filter)
        {
            var url = scheme + "://opml.radiotime.com/Browse.ashx?offset=26&id=c57941&filter=" + filter;
            var request = await BrowseProviderLink(url);

            Assert.Equal(url.Substring(0, url.Length - 1) + "%23", request);
            Assert.Equal("", new Uri(request).Fragment);
            Assert.Equal(HttpUtility.UrlDecode(filter),
                HttpUtility.ParseQueryString(new Uri(request).Query)["filter"]);
        }

        [Theory]
        [InlineData("http://opml.radiotime.com/Browse.ashx?offset=26&id=c57941&filter=s:~%23")]
        [InlineData("https://opml.radiotime.com/Browse.ashx?filter=p%3A~%23&id=c57941&offset=26")]
        [InlineData("http://opml.radiotime.com/Browse.ashx?offset=26&id=c57941&filter=s:~A")]
        [InlineData("http://opml.radiotime.com/Browse.ashx?offset=26&id=c57941&filter=s:~%C3%89")]
        [InlineData("http://opml.radiotime.com/Browse.ashx?offset=26&id=c57941&filter=s:~#section")]
        [InlineData("http://opml.radiotime.com/Browse.ashx?filter=s:~&id=c57941#")]
        [InlineData("http://opml.radiotime.com/Browse.ashx?filter=s:~&filter=s:~#")]
        [InlineData("http://example.com/Browse.ashx?filter=s:~#")]
        [InlineData("http://opml.radiotime.com/Tune.ashx?filter=s:~#")]
        public async Task OtherProviderLinksAreUnchanged(string url)
        {
            Assert.Equal(url, await BrowseProviderLink(url));
        }

        private static async Task<string> BrowseProviderLink(string url)
        {
            var paths = new Mock<IApplicationPaths>();
            paths.SetupGet(p => p.PluginConfigurationsPath)
                .Returns(Path.Combine(Path.GetTempPath(), "tunein-tests-" + Guid.NewGuid().ToString("N")));
            var serializer = new Mock<IXmlSerializer>();
            serializer.Setup(s => s.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>()))
                .Returns(new PluginConfiguration());
            var plugin = new Plugin(paths.Object, serializer.Object);
            plugin.SetAttributes(typeof(Plugin).Assembly.Location, paths.Object.PluginConfigurationsPath,
                new Version(1, 2, 8, 0));

            var logger = new Mock<ILogManager>();
            logger.Setup(m => m.GetLogger(It.IsAny<string>())).Returns(Mock.Of<ILogger>());
            var appHost = new Mock<IApplicationHost>();
            appHost.SetupGet(h => h.SystemId).Returns("test");
            var client = new Mock<IHttpClient>();
            var calls = 0;
            string requestedUrl = null;
            client.Setup(c => c.SendAsync(It.IsAny<HttpRequestOptions>(), "GET"))
                .Returns((HttpRequestOptions options, string method) =>
                {
                    requestedUrl = options.Url;
                    var xml = calls++ == 0
                        ? "<opml><body><outline type='link' text='More Stations' url='"
                            + url.Replace("&", "&amp;") + "'/></body></opml>"
                        : "<opml><body/></opml>";
                    return Task.FromResult(new HttpResponseInfo
                    {
                        Content = new MemoryStream(Encoding.UTF8.GetBytes(xml))
                    });
                });

            var channel = new TuneInChannel(client.Object, logger.Object, appHost.Object);
            var page = await channel.GetChannelItems(new InternalChannelItemQuery
            {
                FolderId = "subcat_http://opml.radiotime.com/Browse.ashx?id=c57941"
            }, CancellationToken.None);
            var next = Assert.Single(page.Items);
            await channel.GetChannelItems(new InternalChannelItemQuery
            {
                FolderId = next.Id
            }, CancellationToken.None);
            Assert.Equal(2, calls);
            return requestedUrl;
        }
    }
}
