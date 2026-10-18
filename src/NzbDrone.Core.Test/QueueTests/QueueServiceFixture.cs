using System;
using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Queue;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.QueueTests
{
    [TestFixture]
    public class QueueServiceFixture : CoreTest<QueueService>
    {
        private List<TrackedDownload> _trackedDownloads;

        [SetUp]
        public void SetUp()
        {
            var downloadClientInfo = Builder<DownloadClientItemClientInfo>.CreateNew().Build();

            var downloadItem = Builder<NzbDrone.Core.Download.DownloadClientItem>.CreateNew()
                                        .With(v => v.RemainingTime = TimeSpan.FromSeconds(10))
                                        .With(v => v.DownloadClientInfo = downloadClientInfo)
                                        .Build();

            var series = Builder<Series>.CreateNew()
                                        .Build();

            var episodes = Builder<Episode>.CreateListOfSize(3)
                                          .All()
                                          .With(e => e.SeriesId = series.Id)
                                          .Build();

            var remoteEpisode = Builder<RemoteEpisode>.CreateNew()
                                                   .With(r => r.Series = series)
                                                   .With(r => r.Episodes = new List<Episode>(episodes))
                                                   .With(r => r.ParsedEpisodeInfo = new ParsedEpisodeInfo())
                                                   .Build();

            _trackedDownloads = Builder<TrackedDownload>.CreateListOfSize(1)
                .All()
                .With(v => v.IsTrackable = true)
                .With(v => v.DownloadItem = downloadItem)
                .With(v => v.RemoteEpisode = remoteEpisode)
                .Build()
                .ToList();
        }

        [Test]
        public void queue_items_should_have_id()
        {
            Subject.Handle(new TrackedDownloadRefreshedEvent(_trackedDownloads));

            var queue = Subject.GetQueue();

            queue.Should().HaveCount(1);

            queue.All(v => v.Id > 0).Should().BeTrue();
        }

        [Test]
        public void should_map_queue_item_with_series_but_no_parsed_episode_info()
        {
            _trackedDownloads.First().RemoteEpisode.ParsedEpisodeInfo = null;

            Subject.Handle(new TrackedDownloadRefreshedEvent(_trackedDownloads));

            var queue = Subject.GetQueue();

            queue.Should().HaveCount(1);
            queue.First().Series.Should().NotBeNull();
            queue.Should().OnlyContain(q => q.Quality.Quality == Quality.Unknown);
        }

        [Test]
        public void should_sum_analyzed_file_sizes_for_download_queue_item()
        {
            var trackedDownload = _trackedDownloads.First();
            var episodes = trackedDownload.RemoteEpisode.Episodes;

            trackedDownload.DownloadItem.TotalSize = 10000;
            trackedDownload.AnalyzedEpisodeFiles = new Dictionary<int, AnalyzedDownloadFile>
            {
                { episodes[0].Id, new AnalyzedDownloadFile { Path = "episode1.mkv", Size = 1800 } },
                { episodes[1].Id, new AnalyzedDownloadFile { Path = "episode2.mkv", Size = 1600 } }
            };

            Subject.Handle(new TrackedDownloadRefreshedEvent(_trackedDownloads));

            var queue = Subject.GetQueue();

            queue.Should().HaveCount(1);
            queue.Single().Size.Should().Be(3400);
        }

        [Test]
        public void should_count_a_file_mapped_to_multiple_episodes_only_once()
        {
            var trackedDownload = _trackedDownloads.First();
            var episodes = trackedDownload.RemoteEpisode.Episodes;
            trackedDownload.DownloadItem.TotalSize = 3400;
            trackedDownload.AnalyzedEpisodeFiles = new Dictionary<int, AnalyzedDownloadFile>
            {
                { episodes[0].Id, new AnalyzedDownloadFile { Path = "season.mkv", Size = 1800 } },
                { episodes[1].Id, new AnalyzedDownloadFile { Path = "season.mkv", Size = 1800 } }
            };
            Subject.Handle(new TrackedDownloadRefreshedEvent(_trackedDownloads));
            Subject.GetQueue().Should().ContainSingle().Which.Size.Should().Be(1800);
        }

        [Test]
        public void should_preserve_analyzed_metadata_for_a_single_episode_download()
        {
            var trackedDownload = _trackedDownloads.First();
            var episode = trackedDownload.RemoteEpisode.Episodes.First();
            trackedDownload.RemoteEpisode.Episodes = new List<Episode> { episode };
            trackedDownload.AnalyzedEpisodeFiles = new Dictionary<int, AnalyzedDownloadFile>
            {
                {
                    episode.Id,
                    new AnalyzedDownloadFile
                    {
                        Path = "episode.mkv",
                        Size = 1800,
                        Quality = new QualityModel(Quality.Bluray1080p),
                        Languages = new List<Language> { Language.Czech, Language.English },
                        SubtitleLanguages = new List<Language> { Language.Czech }
                    }
                }
            };
            Subject.Handle(new TrackedDownloadRefreshedEvent(_trackedDownloads));
            var item = Subject.GetQueue().Should().ContainSingle().Which;
            item.Episodes.Should().ContainSingle().Which.Id.Should().Be(episode.Id);
            item.Size.Should().Be(1800);
            item.Quality.Quality.Should().Be(Quality.Bluray1080p);
            item.Languages.Should().Equal(Language.Czech, Language.English);
            item.SubtitleLanguages.Should().Equal(Language.Czech);
        }
    }
}
