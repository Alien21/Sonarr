using System.Collections.Generic;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.EpisodeImport;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Tags;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.Download.CompletedDownloadServiceTests
{
    [TestFixture]
    public class ProcessFixture : CoreTest<CompletedDownloadService>
    {
        private TrackedDownload _trackedDownload;

        [SetUp]
        public void Setup()
        {
            var completed = Builder<DownloadClientItem>.CreateNew()
                                                    .With(h => h.Status = DownloadItemStatus.Completed)
                                                    .With(h => h.OutputPath = new OsPath(@"C:\DropFolder\MyDownload".AsOsAgnostic()))
                                                    .With(h => h.Title = "Drone.S01E01.HDTV")
                                                    .Build();

            var remoteEpisode = BuildRemoteEpisode();

            _trackedDownload = Builder<TrackedDownload>.CreateNew()
                    .With(c => c.State = TrackedDownloadState.Downloading)
                    .With(c => c.DownloadItem = completed)
                    .With(c => c.RemoteEpisode = remoteEpisode)
                    .Build();

            Mocker.GetMock<IDownloadClient>()
              .SetupGet(c => c.Definition)
              .Returns(new DownloadClientDefinition { Id = 1, Name = "testClient" });

            Mocker.GetMock<IProvideDownloadClient>()
                  .Setup(c => c.Get(It.IsAny<int>()))
                  .Returns(Mocker.GetMock<IDownloadClient>().Object);

            Mocker.GetMock<IProvideImportItemService>()
                  .Setup(c => c.ProvideImportItem(It.IsAny<DownloadClientItem>(), It.IsAny<DownloadClientItem>()))
                  .Returns((DownloadClientItem item, DownloadClientItem previous) => item);

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId(_trackedDownload.DownloadItem.DownloadId))
                  .Returns(new List<EpisodeHistory>());

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.GetSeries("Drone.S01E01.HDTV"))
                  .Returns(remoteEpisode.Series);
        }

        private RemoteEpisode BuildRemoteEpisode()
        {
            return new RemoteEpisode
            {
                Series = new Series(),
                Episodes = new List<Episode> { new Episode { Id = 1 } }
            };
        }

        private void GivenNoGrabbedHistory()
        {
            Mocker.GetMock<IHistoryService>()
                .Setup(s => s.FindByDownloadId(_trackedDownload.DownloadItem.DownloadId))
                .Returns(new List<EpisodeHistory>());
        }

        private void GivenSeriesMatch()
        {
            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.GetSeries(It.IsAny<string>()))
                  .Returns(_trackedDownload.RemoteEpisode.Series);
        }

        private void GivenAutomaticImportCanAddSeries()
        {
            Mocker.GetMock<IConfigService>()
                  .SetupGet(s => s.AllowAutomaticImport)
                  .Returns(true);

            Mocker.GetMock<IConfigService>()
                  .SetupGet(s => s.DefaultRootFolderForAutoImport)
                  .Returns(@"C:\TV".AsOsAgnostic());

            Mocker.GetMock<IConfigService>()
                  .SetupGet(s => s.DefaultProfileForAutoImport)
                  .Returns(1);

            Mocker.GetMock<IConfigService>()
                  .SetupGet(s => s.AnalyzeCompletedDownloadFiles)
                  .Returns(false);

            Mocker.GetMock<IConfigService>()
                  .SetupGet(s => s.BlockAutoImportForExistingEpisodeFiles)
                  .Returns(false);

            Mocker.GetMock<IQualityProfileRepository>()
                  .Setup(s => s.Get(1))
                  .Returns(new QualityProfile { Id = 1 });

            Mocker.GetMock<ISeriesService>()
                  .Setup(s => s.FindByTvdbId(It.IsAny<int>()))
                  .Returns((Series)null);

            Mocker.GetMock<ITagService>()
                  .Setup(s => s.All())
                  .Returns(new List<Tag>());

            var nextTagId = 1;
            Mocker.GetMock<ITagService>()
                  .Setup(s => s.Add(It.IsAny<Tag>()))
                  .Returns<Tag>(tag =>
                  {
                      tag.Id = nextTagId++;
                      return tag;
                  });

            Mocker.GetMock<IAddSeriesService>()
                  .Setup(s => s.AddSeries(It.IsAny<Series>()))
                  .Returns<Series>(series =>
                  {
                      series.Id = 1;
                      return series;
                  });

            Mocker.GetMock<IProvideSeriesInfo>()
                  .Setup(s => s.GetSeriesInfo(It.IsAny<int>()))
                  .Returns<int>(tvdbId => new System.Tuple<Series, List<Episode>>(new Series { TvdbId = tvdbId }, new List<Episode>()));

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.Map(It.IsAny<ParsedEpisodeInfo>(), It.IsAny<Series>()))
                  .Returns<ParsedEpisodeInfo, Series>((parsedEpisodeInfo, series) => new RemoteEpisode
                  {
                      ParsedEpisodeInfo = parsedEpisodeInfo,
                      Series = series,
                      Episodes = new List<Episode>
                      {
                          new Episode { Id = 1, SeasonNumber = 1, EpisodeNumber = 1 }
                      }
                  });
        }

        private void GivenGoldLandDownload()
        {
            _trackedDownload.DownloadItem.Category = "tv";
            _trackedDownload.DownloadItem.Title = "Gold.Land.S01E01.Betting.2160p.DSNP.WEB-DL.DDP5.1.DV.H.265-SCOPE.mkv";
            _trackedDownload.RemoteEpisode.ParsedEpisodeInfo = new ParsedEpisodeInfo
            {
                ReleaseTitle = _trackedDownload.DownloadItem.Title,
                SeriesTitle = "Gold Land",
                SeriesTitleInfo = new SeriesTitleInfo
                {
                    Title = "Gold Land",
                    TitleWithoutYear = "Gold Land"
                },
                SeasonNumber = 1,
                EpisodeNumbers = new[] { 1 },
                AbsoluteEpisodeNumbers = new int[0]
            };

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.GetSeries(_trackedDownload.DownloadItem.Title))
                  .Returns((Series)null);
        }

        private void GivenABadlyNamedDownload()
        {
            _trackedDownload.DownloadItem.DownloadId = "1234";
            _trackedDownload.DownloadItem.Title = "Droned Pilot"; // Set a badly named download
            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId(It.Is<string>(i => i == "1234")))
                  .Returns(new List<EpisodeHistory>
                  {
                      new EpisodeHistory() { SourceTitle = "Droned S01E01", EventType = EpisodeHistoryEventType.Grabbed }
                  });

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.GetSeries(It.IsAny<string>()))
                  .Returns((Series)null);

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.GetSeries("Droned S01E01"))
                  .Returns(BuildRemoteEpisode().Series);
        }

        [TestCase(DownloadItemStatus.Downloading)]
        [TestCase(DownloadItemStatus.Failed)]
        [TestCase(DownloadItemStatus.Queued)]
        [TestCase(DownloadItemStatus.Paused)]
        [TestCase(DownloadItemStatus.Warning)]
        public void should_not_process_if_download_status_isnt_completed(DownloadItemStatus status)
        {
            _trackedDownload.DownloadItem.Status = status;

            Subject.Check(_trackedDownload);

            AssertNotReadyToImport();
        }

        [Test]
        public void should_not_process_if_matching_history_is_not_found_and_no_category_specified()
        {
            _trackedDownload.DownloadItem.Category = null;
            GivenNoGrabbedHistory();

            Subject.Check(_trackedDownload);

            AssertNotReadyToImport();
        }

        [Test]
        public void should_process_if_matching_history_is_not_found_but_category_specified()
        {
            _trackedDownload.DownloadItem.Category = "tv";
            GivenNoGrabbedHistory();
            GivenSeriesMatch();

            Subject.Check(_trackedDownload);

            AssertReadyToImport();
        }

        [Test]
        public void should_not_process_if_output_path_is_empty()
        {
            _trackedDownload.DownloadItem.OutputPath = default(OsPath);

            Subject.Check(_trackedDownload);

            AssertNotReadyToImport();
        }

        [Test]
        public void should_not_process_if_the_download_cannot_be_tracked_using_the_source_title_as_it_was_initiated_externally()
        {
            GivenABadlyNamedDownload();

            Mocker.GetMock<IDownloadedEpisodesImportService>()
                  .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Series>(), It.IsAny<DownloadClientItem>()))
                  .Returns(new List<ImportResult>
                           {
                               new ImportResult(new ImportDecision(new LocalEpisode { Path = @"C:\TestPath\Droned.S01E01.mkv" }))
                           });

            Subject.Check(_trackedDownload);

            AssertNotReadyToImport();
        }

        [Test]
        public void should_not_process_when_there_is_a_title_mismatch()
        {
            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.GetSeries("Drone.S01E01.HDTV"))
                  .Returns((Series)null);

            Subject.Check(_trackedDownload);

            AssertNotReadyToImport();
        }

        [Test]
        public void should_not_auto_import_unmatched_download_when_automatic_import_is_disabled()
        {
            Mocker.GetMock<IConfigService>()
                  .SetupGet(s => s.AllowAutomaticImport)
                  .Returns(false);

            Mocker.GetMock<IConfigService>()
                  .SetupGet(s => s.DefaultRootFolderForAutoImport)
                  .Returns(@"C:\TV".AsOsAgnostic());

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.GetSeries("Drone.S01E01.HDTV"))
                  .Returns((Series)null);

            Subject.Check(_trackedDownload);

            AssertNotReadyToImport();

            Mocker.GetMock<ISearchForNewSeries>()
                  .Verify(v => v.SearchForNewSeries(It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void should_auto_import_unmatched_download_when_multiple_lookup_results_have_one_exact_title_and_no_prefix_conflicts()
        {
            GivenGoldLandDownload();
            GivenAutomaticImportCanAddSeries();

            Mocker.GetMock<ISearchForNewSeries>()
                  .Setup(s => s.SearchForNewSeries("Gold Land"))
                  .Returns(new List<Series>
                  {
                      new Series { Title = "Gold Land", CleanTitle = "goldland", Year = 2026, TvdbId = 457275 },
                      new Series { Title = "Gold Panda", CleanTitle = "goldpanda", Year = 2019, TvdbId = 407323 },
                      new Series { Title = "Gold Panning", CleanTitle = "goldpanning", Year = 2022, TvdbId = 411195 }
                  });

            Subject.Check(_trackedDownload);

            AssertReadyToImport();

            Mocker.GetMock<IAddSeriesService>()
                  .Verify(s => s.AddSeries(It.Is<Series>(series => series.TvdbId == 457275 &&
                                                                    series.Tags.Contains(1) &&
                                                                    series.Tags.Contains(2))), Times.Once());
        }

        [Test]
        public void should_not_auto_import_unmatched_download_when_exact_title_has_prefix_conflicts()
        {
            GivenGoldLandDownload();
            GivenAutomaticImportCanAddSeries();

            Mocker.GetMock<ISearchForNewSeries>()
                  .Setup(s => s.SearchForNewSeries("Gold Land"))
                  .Returns(new List<Series>
                  {
                      new Series { Title = "Gold Land", CleanTitle = "goldland", Year = 2026, TvdbId = 457275 },
                      new Series { Title = "GoldLand Stories", CleanTitle = "goldlandstories", Year = 2025, TvdbId = 457276 }
                  });

            Subject.Check(_trackedDownload);

            AssertImportBlocked();

            Mocker.GetMock<IAddSeriesService>()
                  .Verify(s => s.AddSeries(It.IsAny<Series>()), Times.Never());
        }

        private void AssertImportBlocked()
        {
            _trackedDownload.State.Should().Be(TrackedDownloadState.ImportBlocked);
        }

        private void AssertNotReadyToImport()
        {
            _trackedDownload.State.Should().NotBe(TrackedDownloadState.ImportPending);
        }

        private void AssertReadyToImport()
        {
            _trackedDownload.State.Should().Be(TrackedDownloadState.ImportPending);
        }
    }
}
