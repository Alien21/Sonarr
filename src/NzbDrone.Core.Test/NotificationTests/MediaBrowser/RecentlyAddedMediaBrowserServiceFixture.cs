using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.Emby;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
namespace NzbDrone.Core.Test.NotificationTests.MediaBrowser
{
    [TestFixture]
    public class RecentlyAddedMediaBrowserServiceFixture : CoreTest<MediaBrowserService>
    {
        [Test]
        public void should_use_the_existing_connection_for_imported_episode_updates()
        {
            var requests = new List<HttpRequest>();
            Mocker.GetMock<IHttpClient>()
                .Setup(client => client.Get(It.IsAny<HttpRequest>()))
                .Returns<HttpRequest>(request =>
                {
                    requests.Add(request);
                    return new HttpResponse(request, new HttpHeader(), "{\"Items\":[]}");
                });
            Mocker.SetConstant(Mocker.Resolve<MediaBrowserProxy>());
            var settings = new MediaBrowserSettings
            {
                Host = "jellyfin.invalid",
                Port = 8096,
                UrlBase = "/existing-base",
                ApiKey = "existing-connection-key"
            };
            var message = new DownloadMessage
            {
                Series = new Series { TvdbId = 123, Year = 2020 },
                EpisodeInfo = new LocalEpisode
                {
                    Episodes = new List<Episode> { new Episode { SeasonNumber = 1, EpisodeNumber = 1 } }
                }
            };
            Subject.MarkAsRecentlyAdded(settings, message);
            requests.Should().ContainSingle();
            requests[0].Url.FullUri.Should().StartWith("http://jellyfin.invalid:8096/existing-base/Items?");
            requests[0].Headers["X-MediaBrowser-Token"].Should().Be("existing-connection-key");
            Mocker.GetMock<IHttpClient>()
                .Verify(client => client.Post(It.IsAny<HttpRequest>()), Times.Never());
        }
    }
}
