using System;
using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
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

            queue.Should().HaveCount(3);

            queue.All(v => v.Id > 0).Should().BeTrue();

            var distinct = queue.Select(v => v.Id).Distinct().ToArray();

            distinct.Should().HaveCount(3);
        }

        [Test]
        public void should_map_queue_item_with_series_but_no_parsed_episode_info()
        {
            _trackedDownloads.First().RemoteEpisode.ParsedEpisodeInfo = null;

            Subject.Handle(new TrackedDownloadRefreshedEvent(_trackedDownloads));

            var queue = Subject.GetQueue();

            queue.Should().HaveCount(3);
            queue.First().Series.Should().NotBeNull();
            queue.Should().OnlyContain(q => q.Quality.Quality == Quality.Unknown);
        }

        [Test]
        public void should_use_analyzed_episode_file_size_for_episode_queue_items()
        {
            var trackedDownload = _trackedDownloads.First();
            var episodes = trackedDownload.RemoteEpisode.Episodes;

            trackedDownload.DownloadItem.TotalSize = 3400;
            trackedDownload.AnalyzedEpisodeFiles = new Dictionary<int, AnalyzedDownloadFile>
            {
                { episodes[0].Id, new AnalyzedDownloadFile { Size = 1800 } },
                { episodes[1].Id, new AnalyzedDownloadFile { Size = 1600 } }
            };

            Subject.Handle(new TrackedDownloadRefreshedEvent(_trackedDownloads));

            var queue = Subject.GetQueue();

            queue.Should().HaveCount(3);
            queue.Single(q => q.Episode.Id == episodes[0].Id).Size.Should().Be(1800);
            queue.Single(q => q.Episode.Id == episodes[1].Id).Size.Should().Be(1600);
            queue.Single(q => q.Episode.Id == episodes[2].Id).Size.Should().Be(3400);
        }
    }
}
