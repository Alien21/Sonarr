using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.Webhook;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Tags;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.NotificationTests.Webhook
{
    [TestFixture]
    public class OnManualInteractionRequiredFixture : CoreTest<Notifications.Webhook.Webhook>
    {
        private ManualInteractionRequiredMessage _message;
        private WebhookManualInteractionPayload _payload;

        [SetUp]
        public void Setup()
        {
            var trackedDownload = new TrackedDownload
            {
                DownloadItem = new DownloadClientItem
                {
                    Title = "Unmatched series - [tvdb-433384][1080p]",
                    DownloadId = "download-id",
                    TotalSize = 1024
                },
                State = TrackedDownloadState.ImportBlocked
            };

            trackedDownload.Warn("Unable to resolve download to a series");

            _message = new ManualInteractionRequiredMessage
            {
                TrackedDownload = trackedDownload,
                Quality = new QualityModel(Quality.HDTV720p),
                DownloadId = trackedDownload.DownloadItem.DownloadId
            };

            _payload = null;
            Subject.Definition = new NotificationDefinition { Settings = new WebhookSettings() };

            Mocker.GetMock<IWebhookProxy>()
                .Setup(s => s.SendWebhook(It.IsAny<WebhookPayload>(), It.IsAny<WebhookSettings>()))
                .Callback<WebhookPayload, WebhookSettings>((payload, settings) => _payload = (WebhookManualInteractionPayload)payload);

            Mocker.GetMock<ITagRepository>()
                .Setup(s => s.GetTags(It.IsAny<HashSet<int>>()))
                .Returns(new List<Tag>());
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_send_download_details_when_series_is_unresolved(bool hasRemoteEpisode)
        {
            _message.Episode = hasRemoteEpisode ? new RemoteEpisode() : null;

            Subject.OnManualInteractionRequired(_message);

            Mocker.GetMock<IWebhookProxy>()
                .Verify(v => v.SendWebhook(It.IsAny<WebhookPayload>(), It.IsAny<WebhookSettings>()), Times.Once());

            _payload.EventType.Should().Be(WebhookEventType.ManualInteractionRequired);
            _payload.Series.Should().BeNull();
            _payload.Episodes.Should().BeEmpty();
            _payload.CustomFormatInfo.CustomFormats.Should().BeEmpty();
            _payload.CustomFormatInfo.CustomFormatScore.Should().Be(0);
            _payload.DownloadId.Should().Be("download-id");
            _payload.DownloadInfo.Title.Should().Be(_message.TrackedDownload.DownloadItem.Title);
            _payload.DownloadInfo.Size.Should().Be(1024);
            _payload.DownloadStatus.Should().Be("Warning");
            _payload.DownloadStatusMessages.Should().ContainSingle();
        }

        [Test]
        public void should_preserve_matched_series_episodes_and_custom_formats()
        {
            _message.Series = new Series { Id = 42, Title = "Matched series", TvdbId = 433384 };
            _message.Episode = new RemoteEpisode
            {
                Series = _message.Series,
                Episodes = new List<Episode>
                {
                    new Episode { Id = 123, SeriesId = 42, SeasonNumber = 1, EpisodeNumber = 2, Title = "Matched episode" }
                },
                CustomFormats = new List<CustomFormat> { new CustomFormat { Id = 7, Name = "Czech audio" } },
                CustomFormatScore = 100
            };

            Subject.OnManualInteractionRequired(_message);

            _payload.Series.Id.Should().Be(42);
            _payload.Series.TvdbId.Should().Be(433384);
            _payload.Episodes.Should().ContainSingle().Which.Id.Should().Be(123);
            _payload.CustomFormatInfo.CustomFormats.Should().ContainSingle().Which.Id.Should().Be(7);
            _payload.CustomFormatInfo.CustomFormatScore.Should().Be(100);
        }
    }
}
