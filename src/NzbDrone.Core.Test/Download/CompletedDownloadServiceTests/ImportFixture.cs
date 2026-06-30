using System.Collections.Generic;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.EpisodeImport;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.Download.CompletedDownloadServiceTests
{
    [TestFixture]
    public class ImportFixture : CoreTest<CompletedDownloadService>
    {
        private TrackedDownload _trackedDownload;
        private Episode _episode1;
        private Episode _episode2;
        private Episode _episode3;

        [SetUp]
        public void Setup()
        {
            _episode1 = new Episode { Id = 1, SeasonNumber = 1, EpisodeNumber = 1 };
            _episode2 = new Episode { Id = 2, SeasonNumber = 1, EpisodeNumber = 2 };
            _episode3 = new Episode { Id = 2, SeasonNumber = 1, EpisodeNumber = 3 };

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

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.MostRecentForDownloadId(_trackedDownload.DownloadItem.DownloadId))
                  .Returns(new EpisodeHistory());

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.GetSeries("Drone.S01E01.HDTV"))
                  .Returns(remoteEpisode.Series);

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId(It.IsAny<string>()))
                  .Returns(new List<EpisodeHistory>());

            Mocker.GetMock<IProvideImportItemService>()
                  .Setup(s => s.ProvideImportItem(It.IsAny<DownloadClientItem>(), It.IsAny<DownloadClientItem>()))
                  .Returns<DownloadClientItem, DownloadClientItem>((i, p) => i);

            Mocker.GetMock<IEpisodeService>()
                .Setup(s => s.GetEpisodes(It.IsAny<IEnumerable<int>>()))
                .Returns(new List<Episode>());
        }

        private RemoteEpisode BuildRemoteEpisode()
        {
            return new RemoteEpisode
            {
                Series = new Series(),
                Episodes = new List<Episode>
                {
                    _episode1
                }
            };
        }

        private void GivenABadlyNamedDownload()
        {
            _trackedDownload.DownloadItem.DownloadId = "1234";
            _trackedDownload.DownloadItem.Title = "Droned Pilot"; // Set a badly named download
            Mocker.GetMock<IHistoryService>()
               .Setup(s => s.MostRecentForDownloadId(It.Is<string>(i => i == "1234")))
               .Returns(new EpisodeHistory() { SourceTitle = "Droned S01E01" });

            Mocker.GetMock<IParsingService>()
               .Setup(s => s.GetSeries(It.IsAny<string>()))
               .Returns((Series)null);

            Mocker.GetMock<IParsingService>()
                .Setup(s => s.GetSeries("Droned S01E01"))
                .Returns(BuildRemoteEpisode().Series);
        }

        private void GivenSeriesMatch()
        {
            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.GetSeries(It.IsAny<string>()))
                  .Returns(_trackedDownload.RemoteEpisode.Series);
        }

        [Test]
        public void should_not_mark_as_imported_if_all_files_were_rejected()
        {
            Mocker.GetMock<IDownloadedEpisodesImportService>()
                  .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Series>(), It.IsAny<DownloadClientItem>()))
                  .Returns(new List<ImportResult>
                           {
                               new ImportResult(
                                   new ImportDecision(
                                       new LocalEpisode { Path = @"C:\TestPath\Droned.S01E01.mkv", Episodes = { _episode1 } }, new ImportRejection(ImportRejectionReason.Unknown, "Rejected!")), "Test Failure"),

                               new ImportResult(
                                   new ImportDecision(
                                       new LocalEpisode { Path = @"C:\TestPath\Droned.S01E02.mkv", Episodes = { _episode2 } }, new ImportRejection(ImportRejectionReason.Unknown, "Rejected!")), "Test Failure")
                           });

            Subject.Import(_trackedDownload);

            Mocker.GetMock<IEventAggregator>()
                .Verify(v => v.PublishEvent<DownloadCompletedEvent>(It.IsAny<DownloadCompletedEvent>()), Times.Never());

            AssertNotImported();
        }

        [Test]
        public void should_not_mark_as_imported_if_no_episodes_were_parsed()
        {
            Mocker.GetMock<IDownloadedEpisodesImportService>()
                  .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Series>(), It.IsAny<DownloadClientItem>()))
                  .Returns(new List<ImportResult>
                           {
                               new ImportResult(
                                   new ImportDecision(
                                       new LocalEpisode { Path = @"C:\TestPath\Droned.S01E01.mkv", Episodes = { _episode1 } }, new ImportRejection(ImportRejectionReason.Unknown, "Rejected!")), "Test Failure"),

                               new ImportResult(
                                   new ImportDecision(
                                       new LocalEpisode { Path = @"C:\TestPath\Droned.S01E02.mkv", Episodes = { _episode2 } }, new ImportRejection(ImportRejectionReason.Unknown, "Rejected!")), "Test Failure")
                           });

            _trackedDownload.RemoteEpisode.Episodes.Clear();

            Subject.Import(_trackedDownload);

            AssertNotImported();
        }

        [Test]
        public void should_not_mark_as_imported_if_all_files_were_skipped()
        {
            Mocker.GetMock<IDownloadedEpisodesImportService>()
                  .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Series>(), It.IsAny<DownloadClientItem>()))
                  .Returns(new List<ImportResult>
                           {
                               new ImportResult(new ImportDecision(new LocalEpisode { Path = @"C:\TestPath\Droned.S01E01.mkv", Episodes = { _episode1 } }), "Test Failure"),
                               new ImportResult(new ImportDecision(new LocalEpisode { Path = @"C:\TestPath\Droned.S01E02.mkv", Episodes = { _episode2 } }), "Test Failure")
                           });

            Subject.Import(_trackedDownload);

            AssertNotImported();
        }

        [Test]
        public void should_not_auto_import_if_episode_already_has_file_when_enabled()
        {
            Mocker.GetMock<IConfigService>()
                  .SetupGet(v => v.BlockAutoImportForExistingEpisodeFiles)
                  .Returns(true);

            _trackedDownload.RemoteEpisode.Episodes = new List<Episode>
                                                      {
                                                          new Episode { Id = 1, EpisodeFileId = 1 }
                                                      };

            Subject.Import(_trackedDownload);

            Mocker.GetMock<IDownloadedEpisodesImportService>()
                  .Verify(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Series>(), It.IsAny<DownloadClientItem>()), Times.Never());

            AssertNotImported();
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_mark_as_imported_if_existing_episode_block_finds_all_episodes_imported_in_history()
        {
            Mocker.GetMock<IConfigService>()
                  .SetupGet(v => v.BlockAutoImportForExistingEpisodeFiles)
                  .Returns(true);

            var outputFolder = @"C:\DropFolder\MyDownload".AsOsAgnostic();
            var sourcePath = @"C:\DropFolder\MyDownload\Droned.S01E01.mkv".AsOsAgnostic();
            var fileSize = 1234L;
            var episodeFile = new EpisodeFile { Id = 10, Size = fileSize };
            var episode = new Episode
            {
                Id = 1,
                SeasonNumber = 1,
                EpisodeNumber = 1,
                EpisodeFileId = episodeFile.Id
            };

            _trackedDownload.DownloadItem.OutputPath = new OsPath(outputFolder);
            _trackedDownload.RemoteEpisode.Episodes = new List<Episode> { episode };

            var history = Builder<EpisodeHistory>.CreateListOfSize(1)
                                                .All()
                                                .With(h => h.EpisodeId = episode.Id)
                                                .With(h => h.EventType = EpisodeHistoryEventType.DownloadFolderImported)
                                                .BuildList();

            history[0].Data["FileId"] = episodeFile.Id.ToString();
            history[0].Data["DroppedPath"] = sourcePath;
            history[0].Data["Size"] = fileSize.ToString();

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId(It.IsAny<string>()))
                  .Returns(history);

            Mocker.GetMock<ITrackedDownloadAlreadyImported>()
                  .Setup(s => s.IsImported(_trackedDownload, It.IsAny<List<EpisodeHistory>>()))
                  .Returns(true);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FolderExists(outputFolder))
                  .Returns(true);

            Mocker.GetMock<IDiskScanService>()
                  .Setup(s => s.GetVideoFiles(outputFolder, It.IsAny<bool>()))
                  .Returns(new[] { sourcePath });

            Mocker.GetMock<IDiskScanService>()
                  .Setup(s => s.FilterPaths(outputFolder, It.IsAny<IEnumerable<string>>(), It.IsAny<bool>()))
                  .Returns(new List<string> { sourcePath });

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.GetFileSize(sourcePath))
                  .Returns(fileSize);

            Mocker.GetMock<IEpisodeService>()
                  .Setup(s => s.GetEpisodes(It.IsAny<IEnumerable<int>>()))
                  .Returns(new List<Episode> { episode });

            Mocker.GetMock<IMediaFileService>()
                  .Setup(s => s.GetFiles(It.IsAny<IEnumerable<int>>()))
                  .Returns(new List<EpisodeFile> { episodeFile });

            Subject.Import(_trackedDownload);

            Mocker.GetMock<IDownloadedEpisodesImportService>()
                  .Verify(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Series>(), It.IsAny<DownloadClientItem>()), Times.Never());

            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.IsAny<DownloadCompletedEvent>()), Times.Once());

            _trackedDownload.State.Should().Be(TrackedDownloadState.Imported);
        }

        [Test]
        public void should_not_mark_as_imported_from_history_if_remaining_file_size_differs()
        {
            Mocker.GetMock<IConfigService>()
                  .SetupGet(v => v.BlockAutoImportForExistingEpisodeFiles)
                  .Returns(true);

            var outputFolder = @"C:\DropFolder\MyDownload".AsOsAgnostic();
            var sourcePath = @"C:\DropFolder\MyDownload\Droned.S01E01.mkv".AsOsAgnostic();
            var importedSize = 1234L;
            var episode = new Episode
            {
                Id = 1,
                SeasonNumber = 1,
                EpisodeNumber = 1,
                EpisodeFileId = 10
            };

            _trackedDownload.DownloadItem.OutputPath = new OsPath(outputFolder);
            _trackedDownload.RemoteEpisode.Episodes = new List<Episode> { episode };

            var history = Builder<EpisodeHistory>.CreateListOfSize(1)
                                                .All()
                                                .With(h => h.EpisodeId = episode.Id)
                                                .With(h => h.EventType = EpisodeHistoryEventType.DownloadFolderImported)
                                                .BuildList();

            history[0].Data["DroppedPath"] = sourcePath;
            history[0].Data["Size"] = importedSize.ToString();

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId(It.IsAny<string>()))
                  .Returns(history);

            Mocker.GetMock<ITrackedDownloadAlreadyImported>()
                  .Setup(s => s.IsImported(_trackedDownload, It.IsAny<List<EpisodeHistory>>()))
                  .Returns(true);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FolderExists(outputFolder))
                  .Returns(true);

            Mocker.GetMock<IDiskScanService>()
                  .Setup(s => s.GetVideoFiles(outputFolder, It.IsAny<bool>()))
                  .Returns(new[] { sourcePath });

            Mocker.GetMock<IDiskScanService>()
                  .Setup(s => s.FilterPaths(outputFolder, It.IsAny<IEnumerable<string>>(), It.IsAny<bool>()))
                  .Returns(new List<string> { sourcePath });

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.GetFileSize(sourcePath))
                  .Returns(importedSize + 1);

            Mocker.GetMock<IMakeImportDecision>()
                  .Setup(s => s.GetImportDecisions(It.IsAny<List<string>>(), It.IsAny<Series>(), It.IsAny<DownloadClientItem>(), It.IsAny<ParsedEpisodeInfo>(), true, false))
                  .Returns(new List<ImportDecision>());

            Subject.Import(_trackedDownload);

            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.IsAny<DownloadCompletedEvent>()), Times.Never());

            AssertNotImported();
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_bypass_existing_episode_auto_import_block_for_preferred_dual_audio_upgrade()
        {
            Mocker.GetMock<IConfigService>()
                .SetupGet(v => v.BlockAutoImportForExistingEpisodeFiles)
                .Returns(true);

            Mocker.GetMock<IConfigService>()
                .SetupGet(v => v.PreferDualAudio)
                .Returns(true);

            var outputPath = @"C:\DropFolder\MyDownload.mkv".AsOsAgnostic();
            _trackedDownload.DownloadItem.OutputPath = new OsPath(outputPath);

            var existingEpisodeFile = new EpisodeFile();
            var episode = new Episode
            {
                Id = 1,
                SeasonNumber = 1,
                EpisodeNumber = 1,
                EpisodeFileId = 1,
                EpisodeFile = new LazyLoaded<EpisodeFile>(existingEpisodeFile)
            };

            _trackedDownload.RemoteEpisode.Episodes = new List<Episode> { episode };

            var localEpisode = new LocalEpisode
            {
                Path = outputPath,
                Series = _trackedDownload.RemoteEpisode.Series,
                Episodes = new List<Episode> { episode }
            };

            Mocker.GetMock<IDiskProvider>()
                .Setup(v => v.FileExists(outputPath))
                .Returns(true);

            Mocker.GetMock<IMakeImportDecision>()
                .Setup(v => v.GetImportDecisions(It.IsAny<List<string>>(), _trackedDownload.RemoteEpisode.Series, _trackedDownload.DownloadItem, null, true, false))
                .Returns(new List<ImportDecision>
                {
                    new ImportDecision(localEpisode)
                });

            Mocker.GetMock<IDualAudioImportPreference>()
                .Setup(v => v.Evaluate(localEpisode, existingEpisodeFile))
                .Returns(new DualAudioImportPreferenceResult
                {
                    Applies = true,
                    IsPreferredUpgrade = true
                });

            Mocker.GetMock<IDownloadedEpisodesImportService>()
                .Setup(v => v.ProcessPath(outputPath, ImportMode.Auto, _trackedDownload.RemoteEpisode.Series, _trackedDownload.DownloadItem))
                .Returns(new List<ImportResult>
                {
                    new ImportResult(new ImportDecision(localEpisode))
                });

            Subject.Import(_trackedDownload);

            AssertImported();
        }

        [Test]
        public void should_report_import_decision_rejection_when_existing_episode_auto_import_upgrade_is_blocked()
        {
            Mocker.GetMock<IConfigService>()
                .SetupGet(v => v.BlockAutoImportForExistingEpisodeFiles)
                .Returns(true);

            Mocker.GetMock<IConfigService>()
                .SetupGet(v => v.PreferDualAudio)
                .Returns(true);

            var outputPath = @"C:\DropFolder\MyDownload.mkv".AsOsAgnostic();
            _trackedDownload.DownloadItem.OutputPath = new OsPath(outputPath);

            var existingEpisodeFile = new EpisodeFile();
            var episode = new Episode
            {
                Id = 1,
                SeasonNumber = 1,
                EpisodeNumber = 1,
                EpisodeFileId = 1,
                EpisodeFile = new LazyLoaded<EpisodeFile>(existingEpisodeFile)
            };

            _trackedDownload.RemoteEpisode.Episodes = new List<Episode> { episode };

            var localEpisode = new LocalEpisode
            {
                Path = outputPath,
                Series = _trackedDownload.RemoteEpisode.Series,
                Episodes = new List<Episode> { episode }
            };

            Mocker.GetMock<IDiskProvider>()
                .Setup(v => v.FileExists(outputPath))
                .Returns(true);

            Mocker.GetMock<IMakeImportDecision>()
                .Setup(v => v.GetImportDecisions(It.IsAny<List<string>>(), _trackedDownload.RemoteEpisode.Series, _trackedDownload.DownloadItem, null, true, false))
                .Returns(new List<ImportDecision>
                {
                    new ImportDecision(localEpisode, new ImportRejection(ImportRejectionReason.MinimumFreeSpace, "Not enough free space"))
                });

            Mocker.GetMock<IDualAudioImportPreference>()
                .Setup(v => v.Evaluate(localEpisode, existingEpisodeFile))
                .Returns(new DualAudioImportPreferenceResult
                {
                    Applies = true,
                    IsPreferredUpgrade = true
                });

            Subject.Import(_trackedDownload);

            Mocker.GetMock<IDownloadedEpisodesImportService>()
                .Verify(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Series>(), It.IsAny<DownloadClientItem>()), Times.Never());

            _trackedDownload.StatusMessages.Should().ContainSingle();
            _trackedDownload.StatusMessages[0].Messages.Should().ContainSingle("Auto-import blocked: Not enough free space.");

            AssertNotImported();
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_report_dual_audio_manual_review_rejection_when_existing_episode_auto_import_upgrade_is_blocked()
        {
            Mocker.GetMock<IConfigService>()
                .SetupGet(v => v.BlockAutoImportForExistingEpisodeFiles)
                .Returns(true);

            var outputPath = @"C:\DropFolder\MyDownload.mkv".AsOsAgnostic();
            _trackedDownload.DownloadItem.OutputPath = new OsPath(outputPath);

            var episode = new Episode
            {
                Id = 1,
                SeasonNumber = 1,
                EpisodeNumber = 1,
                EpisodeFileId = 1,
                EpisodeFile = new LazyLoaded<EpisodeFile>(new EpisodeFile())
            };

            _trackedDownload.RemoteEpisode.Episodes = new List<Episode> { episode };

            var localEpisode = new LocalEpisode
            {
                Path = outputPath,
                Series = _trackedDownload.RemoteEpisode.Series,
                Episodes = new List<Episode> { episode }
            };

            Mocker.GetMock<IDiskProvider>()
                .Setup(v => v.FileExists(outputPath))
                .Returns(true);

            Mocker.GetMock<IMakeImportDecision>()
                .Setup(v => v.GetImportDecisions(It.IsAny<List<string>>(), _trackedDownload.RemoteEpisode.Series, _trackedDownload.DownloadItem, null, true, false))
                .Returns(new List<ImportDecision>
                {
                    new ImportDecision(localEpisode, new ImportRejection(ImportRejectionReason.DualAudioUpgradeManualReview, "Dual-audio candidate is more than 30% smaller than the existing episode file; manual review required."))
                });

            Subject.Import(_trackedDownload);

            Mocker.GetMock<IDownloadedEpisodesImportService>()
                .Verify(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Series>(), It.IsAny<DownloadClientItem>()), Times.Never());

            _trackedDownload.StatusMessages.Should().ContainSingle();
            _trackedDownload.StatusMessages[0].Messages.Should().ContainSingle("Auto-import blocked: Dual-audio candidate is more than 30% smaller than the existing episode file; manual review required.");

            AssertNotImported();
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_not_mark_as_imported_if_some_of_episodes_were_not_imported()
        {
            _trackedDownload.RemoteEpisode.Episodes = new List<Episode>
            {
                new Episode(),
                new Episode(),
                new Episode()
            };

            Mocker.GetMock<IDownloadedEpisodesImportService>()
                  .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Series>(), It.IsAny<DownloadClientItem>()))
                  .Returns(new List<ImportResult>
                           {
                               new ImportResult(new ImportDecision(new LocalEpisode { Path = @"C:\TestPath\Droned.S01E01.mkv" })),
                               new ImportResult(new ImportDecision(new LocalEpisode { Path = @"C:\TestPath\Droned.S01E01.mkv" }), "Test Failure"),
                               new ImportResult(new ImportDecision(new LocalEpisode { Path = @"C:\TestPath\Droned.S01E01.mkv" }), "Test Failure")
                           });

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId(It.IsAny<string>()))
                  .Returns(new List<EpisodeHistory>());

            Subject.Import(_trackedDownload);

            AssertNotImported();
        }

        [Test]
        public void should_not_mark_as_imported_if_some_of_episodes_were_not_imported_including_history()
        {
            _trackedDownload.RemoteEpisode.Episodes = new List<Episode>
                                                      {
                                                          new Episode(),
                                                          new Episode(),
                                                          new Episode()
                                                      };

            Mocker.GetMock<IDownloadedEpisodesImportService>()
                  .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Series>(), It.IsAny<DownloadClientItem>()))
                  .Returns(new List<ImportResult>
                           {
                               new ImportResult(new ImportDecision(new LocalEpisode { Path = @"C:\TestPath\Droned.S01E01.mkv" })),
                               new ImportResult(new ImportDecision(new LocalEpisode { Path = @"C:\TestPath\Droned.S01E01.mkv" }), "Test Failure"),
                               new ImportResult(new ImportDecision(new LocalEpisode { Path = @"C:\TestPath\Droned.S01E01.mkv" }), "Test Failure")
                           });

            var history = Builder<EpisodeHistory>.CreateListOfSize(2)
                                                  .BuildList();

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId(It.IsAny<string>()))
                  .Returns(history);

            Mocker.GetMock<ITrackedDownloadAlreadyImported>()
                  .Setup(s => s.IsImported(_trackedDownload, history))
                  .Returns(true);

            Subject.Import(_trackedDownload);

            AssertNotImported();
        }

        [Test]
        public void should_mark_as_imported_if_all_episodes_were_imported()
        {
            var episode1 = new Episode { Id = 1 };
            var episode2 = new Episode { Id = 2 };
            _trackedDownload.RemoteEpisode.Episodes = new List<Episode> { episode1, episode2 };

            Mocker.GetMock<IDownloadedEpisodesImportService>()
                  .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Series>(), It.IsAny<DownloadClientItem>()))
                  .Returns(new List<ImportResult>
                           {
                               new ImportResult(
                                   new ImportDecision(
                                       new LocalEpisode { Path = @"C:\TestPath\Droned.S01E01.mkv", Episodes = new List<Episode> { episode1 } })),

                               new ImportResult(
                                   new ImportDecision(
                                       new LocalEpisode { Path = @"C:\TestPath\Droned.S01E02.mkv", Episodes = new List<Episode> { episode2 } }))
                           });

            Subject.Import(_trackedDownload);

            AssertImported();
        }

        [Test]
        public void should_mark_as_imported_if_all_episodes_were_imported_including_history()
        {
            var episode1 = new Episode { Id = 1 };
            var episode2 = new Episode { Id = 2 };
            _trackedDownload.RemoteEpisode.Episodes = new List<Episode> { episode1, episode2 };

            Mocker.GetMock<IDownloadedEpisodesImportService>()
                .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Series>(), It.IsAny<DownloadClientItem>()))
                .Returns(new List<ImportResult>
                {
                    new ImportResult(
                        new ImportDecision(
                            new LocalEpisode { Path = @"C:\TestPath\Droned.S01E01.mkv", Episodes = new List<Episode> { episode1 } })),

                    new ImportResult(
                        new ImportDecision(
                            new LocalEpisode { Path = @"C:\TestPath\Droned.S01E02.mkv", Episodes = new List<Episode> { episode2 } }), "Test Failure")
                });

            var history = Builder<EpisodeHistory>.CreateListOfSize(2)
                                                  .BuildList();

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId(It.IsAny<string>()))
                  .Returns(history);

            Mocker.GetMock<ITrackedDownloadAlreadyImported>()
                  .Setup(s => s.IsImported(It.IsAny<TrackedDownload>(), It.IsAny<List<EpisodeHistory>>()))
                  .Returns(true);

            Subject.Import(_trackedDownload);

            AssertImported();
        }

        [Test]
        public void should_mark_as_imported_if_double_episode_file_is_imported()
        {
            var episode1 = new Episode { Id = 1 };
            var episode2 = new Episode { Id = 2 };
            _trackedDownload.RemoteEpisode.Episodes = new List<Episode> { episode1, episode2 };

            Mocker.GetMock<IDownloadedEpisodesImportService>()
                  .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Series>(), It.IsAny<DownloadClientItem>()))
                  .Returns(new List<ImportResult>
                           {
                               new ImportResult(
                                   new ImportDecision(
                                       new LocalEpisode { Path = @"C:\TestPath\Droned.S01E01-E02.mkv", Episodes = new List<Episode> { episode1, episode2 } }))
                           });

            Subject.Import(_trackedDownload);

            AssertImported();
        }

        [Test]
        public void should_mark_as_imported_if_all_episodes_were_imported_but_extra_files_were_not()
        {
            GivenSeriesMatch();

            _trackedDownload.RemoteEpisode.Episodes = new List<Episode>
                                                      {
                                                          new Episode()
                                                      };

            Mocker.GetMock<IDownloadedEpisodesImportService>()
                  .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Series>(), It.IsAny<DownloadClientItem>()))
                  .Returns(new List<ImportResult>
                           {
                               new ImportResult(new ImportDecision(new LocalEpisode { Path = @"C:\TestPath\Droned.S01E01.mkv", Episodes = _trackedDownload.RemoteEpisode.Episodes })),
                               new ImportResult(new ImportDecision(new LocalEpisode { Path = @"C:\TestPath\Droned.S01E01.mkv" }), "Test Failure")
                           });

            Subject.Import(_trackedDownload);

            AssertImported();
        }

        [Test]
        public void should_mark_as_imported_if_the_download_can_be_tracked_using_the_source_seriesid()
        {
            GivenABadlyNamedDownload();

            Mocker.GetMock<IDownloadedEpisodesImportService>()
                  .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Series>(), It.IsAny<DownloadClientItem>()))
                  .Returns(new List<ImportResult>
                           {
                               new ImportResult(new ImportDecision(new LocalEpisode { Path = @"C:\TestPath\Droned.S01E01.mkv", Episodes = _trackedDownload.RemoteEpisode.Episodes }))
                           });

            Mocker.GetMock<ISeriesService>()
                  .Setup(v => v.GetSeries(It.IsAny<int>()))
                  .Returns(BuildRemoteEpisode().Series);

            Subject.Import(_trackedDownload);

            AssertImported();
        }

        private void AssertNotImported()
        {
            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.IsAny<DownloadCompletedEvent>()), Times.Never());

            _trackedDownload.State.Should().Be(TrackedDownloadState.ImportBlocked);
        }

        private void AssertImported()
        {
            Mocker.GetMock<IDownloadedEpisodesImportService>()
                .Verify(v => v.ProcessPath(_trackedDownload.DownloadItem.OutputPath.FullPath, ImportMode.Auto, _trackedDownload.RemoteEpisode.Series, _trackedDownload.DownloadItem), Times.Once());

            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.IsAny<DownloadCompletedEvent>()), Times.Once());

            _trackedDownload.State.Should().Be(TrackedDownloadState.Imported);
        }
    }
}
