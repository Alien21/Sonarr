using System;
using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.Emby;
using NzbDrone.Core.Notifications.Plex.Server;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.NotificationTests
{
    [TestFixture]
    public class RecentlyAddedNotificationFixture : CoreTest<NotificationService>
    {
        private Mock<INotification> _notification;
        private Mock<IRecentlyAddedNotification> _recentlyAdded;
        private NotificationDefinition _definition;
        private EpisodeImportedEvent _imported;
        private List<string> _calls;

        [SetUp]
        public void Setup()
        {
            _calls = new List<string>();
            _definition = new NotificationDefinition { Id = 1, Name = "Media server", OnDownload = true };
            _notification = new Mock<INotification>();
            _recentlyAdded = _notification.As<IRecentlyAddedNotification>();
            _notification.SetupGet(n => n.Definition).Returns(_definition);
            _recentlyAdded.Setup(n => n.MarkAsRecentlyAdded(It.IsAny<DownloadMessage>()))
                .Callback(() => _calls.Add("date"));
            _notification.Setup(n => n.OnDownload(It.IsAny<DownloadMessage>()))
                .Callback(() => _calls.Add("download"));
            Mocker.GetMock<INotificationFactory>().Setup(f => f.OnDownloadEnabled(true))
                .Returns(new List<INotification> { _notification.Object });
            Mocker.GetMock<IConfigService>().SetupGet(c => c.MarkImportedMediaAsRecentlyAdded).Returns(true);
            _imported = new EpisodeImportedEvent(
                new LocalEpisode
                {
                    Series = new Series { Id = 1, Title = "Series" },
                    Episodes = new List<Episode> { new Episode { SeasonNumber = 1, EpisodeNumber = 2, Title = "Episode" } },
                    Quality = new QualityModel(Quality.HDTV720p)
                },
                new EpisodeFile(),
                new List<DeletedEpisodeFile>(),
                true,
                null);
        }

        [TestCase(true, false, false, "date,download")]
        [TestCase(true, false, true, "date")]
        [TestCase(true, true, true, "date,download")]
        [TestCase(false, false, false, "download")]
        [TestCase(false, false, true, "")]
        public void should_mark_before_existing_download_gate(bool enabled, bool upgradeNotifications, bool replacement, string expected)
        {
            Mocker.GetMock<IConfigService>().SetupGet(c => c.MarkImportedMediaAsRecentlyAdded).Returns(enabled);
            _definition.OnUpgrade = upgradeNotifications;

            if (replacement)
            {
                _imported.OldFiles.Add(new DeletedEpisodeFile(new EpisodeFile(), null));
            }

            Subject.Handle(_imported);

            _calls.Should().Equal(expected.Split(',', StringSplitOptions.RemoveEmptyEntries));
        }

        [Test]
        public void should_not_mark_existing_library_rescans()
        {
            Subject.Handle(new EpisodeImportedEvent(_imported.EpisodeInfo, _imported.ImportedEpisode, _imported.OldFiles, false, null));

            _calls.Should().BeEmpty();
            Mocker.GetMock<INotificationFactory>().Verify(f => f.OnDownloadEnabled(true), Times.Never());
        }

        [Test]
        public void should_respect_connection_tags()
        {
            _definition.Tags.Add(99);

            Subject.Handle(_imported);

            _calls.Should().BeEmpty();
        }

        [Test]
        public void date_failure_should_not_prevent_the_original_download_notification()
        {
            _recentlyAdded.Setup(n => n.MarkAsRecentlyAdded(It.IsAny<DownloadMessage>()))
                .Throws(new InvalidOperationException("offline"));

            Subject.Handle(_imported);

            _calls.Should().Equal("download");
            Mocker.GetMock<INotificationStatusService>().Verify(status => status.RecordFailure(It.IsAny<int>()), Times.Never());
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_leave_notifications_without_the_capability_unchanged()
        {
            var regular = new Mock<INotification>();
            regular.SetupGet(n => n.Definition).Returns(_definition);
            Mocker.GetMock<INotificationFactory>().Setup(f => f.OnDownloadEnabled(true))
                .Returns(new List<INotification> { regular.Object });

            Subject.Handle(_imported);

            regular.Verify(n => n.OnDownload(It.IsAny<DownloadMessage>()), Times.Once());
            _calls.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class RecentlyAddedProviderFixture : CoreTest
    {
        [Test]
        public void plex_capability_should_only_delegate_the_date_update()
        {
            var provider = Mocker.Resolve<PlexServer>();
            var settings = new PlexServerSettings { UpdateLibrary = false };
            provider.Definition = new NotificationDefinition { Settings = settings };
            var message = new DownloadMessage();

            ((IRecentlyAddedNotification)provider).MarkAsRecentlyAdded(message);

            Mocker.GetMock<IPlexServerService>().Verify(s => s.MarkAsRecentlyAdded(settings, message), Times.Once());
            Mocker.GetMock<IPlexServerService>().Verify(s => s.UpdateLibrary(It.IsAny<Series>(), settings), Times.Never());
        }

        [Test]
        public void jellyfin_capability_should_only_delegate_the_date_update()
        {
            var provider = Mocker.Resolve<NzbDrone.Core.Notifications.Emby.MediaBrowser>();
            var settings = new MediaBrowserSettings { UpdateLibrary = false, Notify = false };
            provider.Definition = new NotificationDefinition { Settings = settings };
            var message = new DownloadMessage();

            ((IRecentlyAddedNotification)provider).MarkAsRecentlyAdded(message);

            Mocker.GetMock<IMediaBrowserService>().Verify(s => s.MarkAsRecentlyAdded(settings, message), Times.Once());
            Mocker.GetMock<IMediaBrowserService>().Verify(s => s.Update(settings, It.IsAny<Series>(), It.IsAny<string>()), Times.Never());
        }
    }
}
