using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.Download.TrackedDownloads
{
    [TestFixture]
    public class TrackedDownloadAlreadyImportedFixture : CoreTest<TrackedDownloadAlreadyImported>
    {
        private List<Episode> _episodes;
        private List<EpisodeFile> _episodeFiles;
        private TrackedDownload _trackedDownload;
        private List<EpisodeHistory> _historyItems;

        [SetUp]
        public void Setup()
        {
            _episodes = new List<Episode>();
            _episodeFiles = new List<EpisodeFile>();

            var remoteEpisode = Builder<RemoteEpisode>.CreateNew()
                                                      .With(r => r.Episodes = _episodes)
                                                      .Build();

            var downloadItem = Builder<DownloadClientItem>.CreateNew()
                                                         .Build();

            _trackedDownload = Builder<TrackedDownload>.CreateNew()
                                                       .With(t => t.RemoteEpisode = remoteEpisode)
                                                       .With(t => t.DownloadItem = downloadItem)
                                                       .Build();

            _historyItems = new List<EpisodeHistory>();

            Mocker.GetMock<IMediaFileService>()
                  .Setup(s => s.GetFiles(It.IsAny<IEnumerable<int>>()))
                  .Returns<IEnumerable<int>>(ids => _episodeFiles.Where(f => ids.Contains(f.Id)).ToList());
        }

        public void GivenEpisodes(int count)
        {
            var start = _episodes.Count + 1;

            for (var i = 0; i < count; i++)
            {
                var episodeFileId = start + i + 1000;

                _episodes.Add(new Episode
                {
                    Id = start + i,
                    SeriesId = 1,
                    SeasonNumber = 1,
                    EpisodeNumber = start + i,
                    EpisodeFileId = episodeFileId
                });

                _episodeFiles.Add(new EpisodeFile
                {
                    Id = episodeFileId,
                    Size = start + i + 1000000
                });
            }
        }

        public void GivenHistoryForEpisode(Episode episode, params EpisodeHistoryEventType[] eventTypes)
        {
            foreach (var eventType in eventTypes)
            {
                var history = Builder<EpisodeHistory>.CreateNew()
                                                     .With(h => h.EpisodeId = episode.Id)
                                                     .With(h => h.EventType = eventType)
                                                     .Build();

                if (eventType == EpisodeHistoryEventType.DownloadFolderImported)
                {
                    var episodeFile = _episodeFiles.Single(f => f.Id == episode.EpisodeFileId);

                    history.Data["FileId"] = episodeFile.Id.ToString();
                    history.Data["Size"] = episodeFile.Size.ToString();
                }

                _historyItems.Add(history);
            }
        }

        [Test]
        public void should_return_false_if_there_is_no_history()
        {
            GivenEpisodes(1);

            Subject.IsImported(_trackedDownload, _historyItems)
                   .Should()
                   .BeFalse();
        }

        [Test]
        public void should_return_false_if_single_episode_download_is_not_imported()
        {
            GivenEpisodes(1);

            GivenHistoryForEpisode(_episodes[0], EpisodeHistoryEventType.Grabbed);

            Subject.IsImported(_trackedDownload, _historyItems)
                   .Should()
                   .BeFalse();
        }

        [Test]
        public void should_return_false_if_no_episode_in_multi_episode_download_is_imported()
        {
            GivenEpisodes(2);

            GivenHistoryForEpisode(_episodes[0], EpisodeHistoryEventType.Grabbed);
            GivenHistoryForEpisode(_episodes[1], EpisodeHistoryEventType.Grabbed);

            Subject.IsImported(_trackedDownload, _historyItems)
                   .Should()
                   .BeFalse();
        }

        [Test]
        public void should_should_return_false_if_only_one_episode_in_multi_episode_download_is_imported()
        {
            GivenEpisodes(2);

            GivenHistoryForEpisode(_episodes[0], EpisodeHistoryEventType.DownloadFolderImported, EpisodeHistoryEventType.Grabbed);
            GivenHistoryForEpisode(_episodes[1], EpisodeHistoryEventType.Grabbed);

            Subject.IsImported(_trackedDownload, _historyItems)
                   .Should()
                   .BeFalse();
        }

        [Test]
        public void should_return_false_if_imported_file_size_does_not_match_existing_file()
        {
            GivenEpisodes(1);

            GivenHistoryForEpisode(_episodes[0], EpisodeHistoryEventType.DownloadFolderImported, EpisodeHistoryEventType.Grabbed);
            _historyItems.First(h => h.EventType == EpisodeHistoryEventType.DownloadFolderImported).Data["Size"] = (_episodeFiles[0].Size + 1).ToString();

            Subject.IsImported(_trackedDownload, _historyItems)
                   .Should()
                   .BeFalse();
        }

        [Test]
        public void should_return_false_if_existing_episode_file_is_missing()
        {
            GivenEpisodes(1);

            GivenHistoryForEpisode(_episodes[0], EpisodeHistoryEventType.DownloadFolderImported, EpisodeHistoryEventType.Grabbed);
            _episodeFiles.Clear();

            Subject.IsImported(_trackedDownload, _historyItems)
                   .Should()
                   .BeFalse();
        }

        [Test]
        public void should_return_true_if_single_episode_download_is_imported()
        {
            GivenEpisodes(1);

            GivenHistoryForEpisode(_episodes[0], EpisodeHistoryEventType.DownloadFolderImported, EpisodeHistoryEventType.Grabbed);

            Subject.IsImported(_trackedDownload, _historyItems)
                   .Should()
                   .BeTrue();
        }

        [Test]
        public void should_return_true_if_multi_episode_download_is_imported()
        {
            GivenEpisodes(2);

            GivenHistoryForEpisode(_episodes[0], EpisodeHistoryEventType.DownloadFolderImported, EpisodeHistoryEventType.Grabbed);
            GivenHistoryForEpisode(_episodes[1], EpisodeHistoryEventType.DownloadFolderImported, EpisodeHistoryEventType.Grabbed);

            Subject.IsImported(_trackedDownload, _historyItems)
                   .Should()
                   .BeTrue();
        }
    }
}
