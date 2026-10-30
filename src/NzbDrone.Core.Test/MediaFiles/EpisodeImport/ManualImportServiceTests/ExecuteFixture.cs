using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.EpisodeImport;
using NzbDrone.Core.MediaFiles.EpisodeImport.Aggregation;
using NzbDrone.Core.MediaFiles.EpisodeImport.Manual;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles.EpisodeImport.ManualImportServiceTests
{
    [TestFixture]
    public class ExecuteFixture : CoreTest<ManualImportService>
    {
        private Dictionary<int, Series> _series;
        private Dictionary<int, Episode> _episodes;
        private Dictionary<string, TrackedDownload> _trackedDownloads;
        private Dictionary<string, Func<ImportDecision, ImportResult>> _importResults;
        private List<LocalEpisode> _attemptedImports;
        private List<DownloadCompletedEvent> _completedDownloads;
        private List<Dictionary<string, TrackedDownloadState>> _queueUpdates;
        private ManualImportCommand _command;
        private TrackedDownload _trackedDownload;
        private ManualImportFile _file;

        [SetUp]
        public void Setup()
        {
            _series = new Dictionary<int, Series>();
            _episodes = new Dictionary<int, Episode>();
            _trackedDownloads = new Dictionary<string, TrackedDownload>();
            _importResults = new Dictionary<string, Func<ImportDecision, ImportResult>>();
            _attemptedImports = new List<LocalEpisode>();
            _completedDownloads = new List<DownloadCompletedEvent>();
            _queueUpdates = new List<Dictionary<string, TrackedDownloadState>>();
            _command = new ManualImportCommand { Files = new List<ManualImportFile>(), ImportMode = ImportMode.Copy };

            Mocker.GetMock<ISeriesService>()
                  .Setup(s => s.GetSeries(It.IsAny<int>()))
                  .Returns<int>(id => _series[id]);

            Mocker.GetMock<IEpisodeService>()
                  .Setup(s => s.GetEpisodes(It.IsAny<IEnumerable<int>>()))
                  .Returns<IEnumerable<int>>(ids => ids.Select(id => _episodes[id]).ToList());

            Mocker.GetMock<ITrackedDownloadService>()
                  .Setup(s => s.Find(It.IsAny<string>()))
                  .Returns<string>(id => _trackedDownloads.GetValueOrDefault(id));

            Mocker.GetMock<ITrackedDownloadService>()
                  .Setup(s => s.GetTrackedDownloads())
                  .Returns(() => _trackedDownloads.Values.ToList());

            Mocker.GetMock<IAggregationService>()
                  .Setup(s => s.Augment(It.IsAny<LocalEpisode>(), It.IsAny<DownloadClientItem>()))
                  .Returns<LocalEpisode, DownloadClientItem>((episode, item) => episode);

            Mocker.GetMock<IImportApprovedEpisodes>()
                  .Setup(s => s.Import(It.IsAny<List<ImportDecision>>(), true, It.IsAny<DownloadClientItem>(), It.IsAny<ImportMode>()))
                  .Returns<List<ImportDecision>, bool, DownloadClientItem, ImportMode>((decisions, newDownload, item, mode) =>
                  {
                      return decisions.Select(decision =>
                      {
                          _attemptedImports.Add(decision.LocalEpisode);

                          return _importResults.TryGetValue(decision.LocalEpisode.Path, out var result)
                              ? result(decision)
                              : SuccessfulImport(decision);
                      }).ToList();
                  });

            Mocker.GetMock<IEventAggregator>()
                  .Setup(s => s.PublishEvent(It.IsAny<DownloadCompletedEvent>()))
                  .Callback<DownloadCompletedEvent>(message => _completedDownloads.Add(message));

            Mocker.GetMock<IEventAggregator>()
                  .Setup(s => s.PublishEvent(It.IsAny<TrackedDownloadRefreshedEvent>()))
                  .Callback<TrackedDownloadRefreshedEvent>(message => _queueUpdates.Add(
                      message.TrackedDownloads.ToDictionary(download => download.DownloadItem.DownloadId, download => download.State)));

            _trackedDownload = GivenTrackedDownload("download-1", 1);
            _file = GivenFile(_trackedDownload, 1);
        }

        private TrackedDownload GivenTrackedDownload(string downloadId, int seriesId)
        {
            var series = new Series
            {
                Id = seriesId,
                Title = "Series " + seriesId,
                Path = Path.Combine(@"C:\TV".AsOsAgnostic(), "Series " + seriesId)
            };

            _series[seriesId] = series;

            var item = new DownloadClientItem
            {
                DownloadId = downloadId,
                Title = "Series.S01",
                Status = DownloadItemStatus.Completed,
                OutputPath = new OsPath(Path.Combine(@"C:\Downloads".AsOsAgnostic(), downloadId)),
                CanMoveFiles = false
            };

            var trackedDownload = new TrackedDownload
            {
                DownloadItem = item,
                ImportItem = item,
                State = TrackedDownloadState.ImportPending,
                IsTrackable = true,
                RemoteEpisode = new RemoteEpisode { Series = series, Episodes = new List<Episode>() }
            };

            _trackedDownloads.Add(downloadId, trackedDownload);
            return trackedDownload;
        }

        private ManualImportFile GivenFile(TrackedDownload trackedDownload, int episodeId)
        {
            var episode = new Episode
            {
                Id = episodeId,
                SeriesId = trackedDownload.RemoteEpisode.Series.Id,
                SeasonNumber = 1,
                EpisodeNumber = episodeId
            };

            _episodes[episodeId] = episode;
            trackedDownload.RemoteEpisode.Episodes.Add(episode);

            var file = new ManualImportFile
            {
                Path = Path.Combine(trackedDownload.DownloadItem.OutputPath.FullPath, $"Series.S01E{episodeId:00}.mkv"),
                SeriesId = episode.SeriesId,
                EpisodeIds = new List<int> { episodeId },
                DownloadId = trackedDownload.DownloadItem.DownloadId,
                Quality = new QualityModel(Quality.HDTV720p),
                Languages = new List<Language> { Language.English },
                ReleaseGroup = "GROUP"
            };

            _command.Files.Add(file);
            return file;
        }

        private ImportResult SuccessfulImport(ImportDecision decision)
        {
            var episode = decision.LocalEpisode.Episodes.First();
            var file = new EpisodeFile
            {
                Id = 100 + episode.Id,
                SeriesId = decision.LocalEpisode.Series.Id,
                SeasonNumber = episode.SeasonNumber,
                Path = decision.LocalEpisode.Path,
                RelativePath = Path.GetFileName(decision.LocalEpisode.Path)
            };

            episode.EpisodeFileId = file.Id;
            return new ImportResult(decision, file);
        }

        private void GivenFailedImport(ManualImportFile file, string error)
        {
            _importResults[file.Path] = decision => new ImportResult(decision, error);
        }

        [Test]
        public void should_show_failed_file_errors_and_release_importing_state()
        {
            const string error = "Failed to import episode, unable to move existing file to the Recycle Bin.";
            GivenFailedImport(_file, error);

            Subject.Execute(_command);

            _trackedDownload.State.Should().Be(TrackedDownloadState.ImportBlocked);
            _trackedDownload.Status.Should().Be(TrackedDownloadStatus.Warning);
            _trackedDownload.StatusMessages.SelectMany(message => message.Messages).Should().Contain(error);
            _completedDownloads.Should().BeEmpty();
            _queueUpdates.Last()[_trackedDownload.DownloadItem.DownloadId].Should().Be(TrackedDownloadState.ImportBlocked);
        }

        [Test]
        public void should_keep_successful_episode_when_another_file_fails()
        {
            var secondFile = GivenFile(_trackedDownload, 2);
            GivenFailedImport(secondFile, "Not enough free space");

            Subject.Execute(_command);

            _trackedDownload.State.Should().Be(TrackedDownloadState.ImportBlocked);
            _trackedDownload.Status.Should().Be(TrackedDownloadStatus.Warning);
            _trackedDownload.StatusMessages.SelectMany(message => message.Messages).Should().Contain("Not enough free space");
            _episodes[1].EpisodeFileId.Should().Be(101);
            _episodes[2].EpisodeFileId.Should().Be(0);
            _attemptedImports.Select(episode => episode.Path).Should().Equal(_file.Path, secondFile.Path);
            _completedDownloads.Should().BeEmpty();
            Mocker.GetMock<IMediaFileService>().Verify(s => s.Delete(It.IsAny<EpisodeFile>(), It.IsAny<DeleteMediaFileReason>()), Times.Never());
        }

        [Test]
        public void should_release_all_attempted_downloads_when_a_later_import_throws()
        {
            var secondDownload = GivenTrackedDownload("download-2", 2);
            var secondFile = GivenFile(secondDownload, 2);
            var unrelatedDownload = GivenTrackedDownload("unrelated-download", 3);
            unrelatedDownload.State = TrackedDownloadState.Importing;
            var exception = new IOException("Download storage became unavailable");
            _importResults[secondFile.Path] = decision => throw exception;

            Action act = () => Subject.Execute(_command);

            act.Should().Throw<IOException>().Which.Should().BeSameAs(exception);
            _trackedDownload.State.Should().Be(TrackedDownloadState.ImportBlocked);
            secondDownload.State.Should().Be(TrackedDownloadState.ImportBlocked);
            secondDownload.Status.Should().Be(TrackedDownloadStatus.Warning);
            secondDownload.StatusMessages.SelectMany(message => message.Messages).Should().Contain(message => message.Contains(exception.Message));
            unrelatedDownload.State.Should().Be(TrackedDownloadState.Importing);
            _completedDownloads.Should().BeEmpty();
            _queueUpdates.Last()[_trackedDownload.DownloadItem.DownloadId].Should().Be(TrackedDownloadState.ImportBlocked);
            _queueUpdates.Last()[secondDownload.DownloadItem.DownloadId].Should().Be(TrackedDownloadState.ImportBlocked);
        }

        [Test]
        public void should_release_previous_attempt_when_preparing_a_later_file_throws()
        {
            var secondDownload = GivenTrackedDownload("download-2", 2);
            var secondFile = GivenFile(secondDownload, 2);
            var exception = new IOException("Unable to analyze the next file");

            Mocker.GetMock<IAggregationService>()
                  .Setup(s => s.Augment(It.Is<LocalEpisode>(episode => episode.Path == secondFile.Path), It.IsAny<DownloadClientItem>()))
                  .Throws(exception);

            Action act = () => Subject.Execute(_command);

            act.Should().Throw<IOException>().Which.Should().BeSameAs(exception);
            _trackedDownload.State.Should().Be(TrackedDownloadState.ImportBlocked);
            secondDownload.State.Should().Be(TrackedDownloadState.ImportPending);
            _completedDownloads.Should().BeEmpty();
        }

        [Test]
        public void should_mark_successful_download_as_imported_and_clear_previous_error()
        {
            _trackedDownload.Warn("Previous import failed");

            Subject.Execute(_command);

            _trackedDownload.State.Should().Be(TrackedDownloadState.Imported);
            _trackedDownload.Status.Should().Be(TrackedDownloadStatus.Ok);
            _trackedDownload.StatusMessages.Should().BeEmpty();
            _completedDownloads.Should().ContainSingle();
            _completedDownloads.Single().TrackedDownload.Should().BeSameAs(_trackedDownload);
            _completedDownloads.Single().EpisodeFiles.Should().ContainSingle(file => file.Id == 101);
            _queueUpdates.Last()[_trackedDownload.DownloadItem.DownloadId].Should().Be(TrackedDownloadState.Imported);
        }

        [Test]
        public void should_allow_successful_manual_retry_after_failed_import()
        {
            GivenFailedImport(_file, "Not enough free space");
            Subject.Execute(_command);

            _importResults.Remove(_file.Path);
            Subject.Execute(_command);

            _trackedDownload.State.Should().Be(TrackedDownloadState.Imported);
            _trackedDownload.Status.Should().Be(TrackedDownloadStatus.Ok);
            _trackedDownload.StatusMessages.Should().BeEmpty();
            _completedDownloads.Should().ContainSingle();
        }

        [Test]
        public void should_allow_automatic_import_after_failed_manual_import_is_rechecked()
        {
            GivenFailedImport(_file, "Not enough free space");
            Subject.Execute(_command);
            _trackedDownload.State.Should().Be(TrackedDownloadState.ImportBlocked);

            _trackedDownload.DownloadItem.Category = "tv";

            Mocker.GetMock<IProvideImportItemService>()
                  .Setup(s => s.ProvideImportItem(It.IsAny<DownloadClientItem>(), It.IsAny<DownloadClientItem>()))
                  .Returns<DownloadClientItem, DownloadClientItem>((item, previous) => item);

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId(It.IsAny<string>()))
                  .Returns(new List<EpisodeHistory>());

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.GetSeries(_trackedDownload.DownloadItem.Title))
                  .Returns(_trackedDownload.RemoteEpisode.Series);

            Mocker.GetMock<IConfigService>()
                  .SetupGet(s => s.AnalyzeCompletedDownloadFiles)
                  .Returns(false);

            Mocker.GetMock<IConfigService>()
                  .SetupGet(s => s.BlockAutoImportForExistingEpisodeFiles)
                  .Returns(false);

            Mocker.GetMock<IDownloadedEpisodesImportService>()
                  .Setup(s => s.ProcessPath(_trackedDownload.ImportItem.OutputPath.FullPath,
                      ImportMode.Auto,
                      _trackedDownload.RemoteEpisode.Series,
                      _trackedDownload.ImportItem))
                  .Returns(() => new List<ImportResult> { SuccessfulImport(new ImportDecision(_attemptedImports.Single())) });

            var completedDownloadService = Mocker.Resolve<CompletedDownloadService>();
            completedDownloadService.Check(_trackedDownload);

            _trackedDownload.State.Should().Be(TrackedDownloadState.ImportPending);

            completedDownloadService.Import(_trackedDownload);

            _trackedDownload.State.Should().Be(TrackedDownloadState.Imported);
            _trackedDownload.Status.Should().Be(TrackedDownloadStatus.Ok);
            _trackedDownload.StatusMessages.Should().BeEmpty();
            _completedDownloads.Should().ContainSingle();
        }

        [Test]
        public void should_not_leave_importing_when_folder_cleanup_fails_after_import()
        {
            var outputPath = _trackedDownload.ImportItem.OutputPath.FullPath;
            _trackedDownload.DownloadItem.CanMoveFiles = true;

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FolderExists(outputPath))
                  .Returns(true);

            Mocker.GetMock<IDownloadedEpisodesImportService>()
                  .Setup(s => s.ShouldDeleteFolder(It.IsAny<DirectoryInfo>(), _trackedDownload.RemoteEpisode.Series))
                  .Returns(true);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.DeleteFolder(outputPath, true))
                  .Throws(new IOException("Unable to remove the completed download folder"));

            try
            {
                Subject.Execute(_command);
            }
            catch (IOException)
            {
            }

            _trackedDownload.State.Should().NotBe(TrackedDownloadState.Importing);
            _episodes[1].EpisodeFileId.Should().Be(101);
            _queueUpdates.Last()[_trackedDownload.DownloadItem.DownloadId].Should().NotBe(TrackedDownloadState.Importing);
        }

        [Test]
        public void should_complete_successful_download_independently_of_failed_download_in_same_batch()
        {
            GivenFailedImport(_file, "Recycle Bin is full");
            var secondDownload = GivenTrackedDownload("download-2", 2);
            GivenFile(secondDownload, 2);

            Subject.Execute(_command);

            _trackedDownload.State.Should().Be(TrackedDownloadState.ImportBlocked);
            _trackedDownload.StatusMessages.SelectMany(message => message.Messages).Should().Contain("Recycle Bin is full");
            secondDownload.State.Should().Be(TrackedDownloadState.Imported);
            secondDownload.StatusMessages.Should().BeEmpty();
            _completedDownloads.Should().ContainSingle();
            _completedDownloads.Single().TrackedDownload.Should().BeSameAs(secondDownload);
            _completedDownloads.Single().SeriesId.Should().Be(secondDownload.RemoteEpisode.Series.Id);
            _completedDownloads.Single().EpisodeFiles.Should().ContainSingle(file => file.SeriesId == secondDownload.RemoteEpisode.Series.Id);
        }
    }
}
