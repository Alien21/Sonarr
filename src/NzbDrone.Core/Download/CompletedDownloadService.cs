using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentValidation;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.EpisodeImport;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Tags;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Download
{
    public interface ICompletedDownloadService
    {
        void Check(TrackedDownload trackedDownload);
        void Import(TrackedDownload trackedDownload);
        bool VerifyImport(TrackedDownload trackedDownload, List<ImportResult> importResults);
    }

    public class CompletedDownloadService : ICompletedDownloadService
    {
        private readonly IEventAggregator _eventAggregator;
        private readonly IHistoryService _historyService;
        private readonly IProvideImportItemService _provideImportItemService;
        private readonly IDownloadedEpisodesImportService _downloadedEpisodesImportService;
        private readonly IParsingService _parsingService;
        private readonly ISeriesService _seriesService;
        private readonly ITrackedDownloadAlreadyImported _trackedDownloadAlreadyImported;
        private readonly IEpisodeService _episodeService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IRejectedImportService _rejectedImportService;
        private readonly IConfigService _configService;
        private readonly ISearchForNewSeries _searchProxy;
        private readonly IAddSeriesService _addSeriesService;
        private readonly IQualityProfileRepository _qualityProfileRepository;
        private readonly ITagService _tagService;
        private readonly IProvideSeriesInfo _seriesInfo;
        private readonly IRefreshEpisodeService _refreshEpisodeService;
        private readonly Logger _logger;

        public CompletedDownloadService(IEventAggregator eventAggregator,
                                        IHistoryService historyService,
                                        IProvideImportItemService provideImportItemService,
                                        IDownloadedEpisodesImportService downloadedEpisodesImportService,
                                        IParsingService parsingService,
                                        ISeriesService seriesService,
                                        ITrackedDownloadAlreadyImported trackedDownloadAlreadyImported,
                                        IEpisodeService episodeService,
                                        IMediaFileService mediaFileService,
                                        IRejectedImportService rejectedImportService,
                                        ISearchForNewSeries searchProxy,
                                        IAddSeriesService addSeriesService,
                                        IQualityProfileRepository qualityProfileRepository,
                                        ITagService tagService,
                                        IProvideSeriesInfo seriesInfo,
                                        IRefreshEpisodeService refreshEpisodeService,
                                        IConfigService configService,
                                        Logger logger)
        {
            _eventAggregator = eventAggregator;
            _historyService = historyService;
            _provideImportItemService = provideImportItemService;
            _downloadedEpisodesImportService = downloadedEpisodesImportService;
            _parsingService = parsingService;
            _seriesService = seriesService;
            _trackedDownloadAlreadyImported = trackedDownloadAlreadyImported;
            _episodeService = episodeService;
            _mediaFileService = mediaFileService;
            _rejectedImportService = rejectedImportService;
            _searchProxy = searchProxy;
            _addSeriesService = addSeriesService;
            _qualityProfileRepository = qualityProfileRepository;
            _tagService = tagService;
            _seriesInfo = seriesInfo;
            _refreshEpisodeService = refreshEpisodeService;
            _configService = configService;
            _logger = logger;
        }

        public void Check(TrackedDownload trackedDownload)
        {
            if (trackedDownload.DownloadItem.Status != DownloadItemStatus.Completed)
            {
                return;
            }

            SetImportItem(trackedDownload);

            // Only process tracked downloads that are still downloading or have been blocked for importing due to an issue with matching
            if (trackedDownload.State != TrackedDownloadState.Downloading && trackedDownload.State != TrackedDownloadState.ImportBlocked)
            {
                return;
            }

            var grabbedHistories = _historyService.FindByDownloadId(trackedDownload.DownloadItem.DownloadId).Where(h => h.EventType == EpisodeHistoryEventType.Grabbed).ToList();
            var historyItem = grabbedHistories.MaxBy(h => h.Date);

            if (historyItem == null && trackedDownload.DownloadItem.Category.IsNullOrWhiteSpace())
            {
                trackedDownload.Warn("Download wasn't grabbed by Sonarr and not in a category, Skipping.");
                return;
            }

            if (!ValidatePath(trackedDownload))
            {
                return;
            }

            var series = _parsingService.GetSeries(trackedDownload.DownloadItem.Title);

            if (series != null)
            {
                AttachExistingSeries(trackedDownload, series);
            }

            if (series == null && historyItem != null)
            {
                series = _seriesService.GetSeries(historyItem.SeriesId);

                if (series != null)
                {
                    Enum.TryParse(historyItem.Data.GetValueOrDefault(EpisodeHistory.SERIES_MATCH_TYPE, SeriesMatchType.Unknown.ToString()), out SeriesMatchType seriesMatchType);
                    Enum.TryParse(historyItem.Data.GetValueOrDefault(EpisodeHistory.RELEASE_SOURCE, ReleaseSourceType.Unknown.ToString()), out ReleaseSourceType releaseSource);

                    // Show a warning if the release was matched by ID and the source is not interactive search
                    if (seriesMatchType == SeriesMatchType.Id && releaseSource != ReleaseSourceType.InteractiveSearch)
                    {
                        trackedDownload.Warn("Found matching series via grab history, but release was matched to series by ID. Automatic import is not possible. See the FAQ for details.");
                        SetStateToImportBlocked(trackedDownload);

                        return;
                    }

                    AttachExistingSeries(trackedDownload, series);
                }
            }

            if (trackedDownload.RemoteEpisode == null)
            {
                var parsed = Parser.Parser.ParseTitle(trackedDownload.DownloadItem.Title, _configService.ParseTvdbIdFromReleaseName);

                if (parsed != null)
                {
                    trackedDownload.RemoteEpisode = _parsingService.Map(parsed, 0, 0, null);
                }
            }

            if (trackedDownload.RemoteEpisode == null)
            {
                trackedDownload.Warn("Auto-import blocked: unable to resolve {0} download to a series.", trackedDownload.ImportItem?.Title);
                _logger.Debug("Auto-import blocked: unable to resolve {0} download to a series.", trackedDownload.ImportItem?.Title);
                SetStateToImportBlocked(trackedDownload);

                return;
            }

            if (series == null)
            {
                if (string.IsNullOrWhiteSpace(_configService.DefaultRootFolderForAutoImport))
                {
                    trackedDownload.Warn("Series title mismatch; automatic import is not possible. Check the download troubleshooting entry on the wiki for common causes.");
                    SetStateToImportBlocked(trackedDownload);

                    return;
                }

                series = AddSeriesForAutoImport(trackedDownload);

                if (series == null)
                {
                    return;
                }
            }

            if (BlockAutoImportForExistingEpisodeFiles(trackedDownload))
            {
                return;
            }

            _logger.Debug("Set State='{0}' for series '{1}' tvdbid: {2}", TrackedDownloadState.ImportPending, series.Title, series.TvdbId);

            trackedDownload.State = TrackedDownloadState.ImportPending;
        }

        public void Import(TrackedDownload trackedDownload)
        {
            SetImportItem(trackedDownload);

            if (!ValidatePath(trackedDownload))
            {
                return;
            }

            if (trackedDownload.RemoteEpisode == null)
            {
                trackedDownload.Warn("Unable to parse download, automatic import is not possible.");
                SetStateToImportBlocked(trackedDownload);

                return;
            }

            if (BlockAutoImportForExistingEpisodeFiles(trackedDownload))
            {
                return;
            }

            trackedDownload.State = TrackedDownloadState.Importing;

            var outputPath = trackedDownload.ImportItem.OutputPath.FullPath;
            var importResults = _downloadedEpisodesImportService.ProcessPath(outputPath,
                ImportMode.Auto,
                trackedDownload.RemoteEpisode.Series,
                trackedDownload.ImportItem);

            if (VerifyImport(trackedDownload, importResults))
            {
                return;
            }

            trackedDownload.State = TrackedDownloadState.ImportPending;

            if (importResults.Empty())
            {
                trackedDownload.Warn("No files found are eligible for import in {0}", outputPath);

                return;
            }

            if (importResults.Count == 1)
            {
                var firstResult = importResults.First();

                if (_rejectedImportService.Process(trackedDownload, firstResult))
                {
                    return;
                }
            }

            var statusMessages = new List<TrackedDownloadStatusMessage>
                                 {
                                    new TrackedDownloadStatusMessage("One or more episodes expected in this release were not imported or missing from the release", new List<string>())
                                 };

            if (importResults.Any(c => c.Result != ImportResultType.Imported))
            {
                statusMessages.AddRange(
                    importResults
                        .Where(v => v.Result != ImportResultType.Imported && v.ImportDecision.LocalEpisode != null)
                        .OrderBy(v => v.ImportDecision.LocalEpisode.Path)
                        .Select(v =>
                            new TrackedDownloadStatusMessage(Path.GetFileName(v.ImportDecision.LocalEpisode.Path),
                                v.Errors)));
            }

            if (statusMessages.Any())
            {
                trackedDownload.Warn(statusMessages.ToArray());
                SetStateToImportBlocked(trackedDownload);
            }
        }

        public bool VerifyImport(TrackedDownload trackedDownload, List<ImportResult> importResults)
        {
            var allEpisodesImported = importResults.Where(c => c.Result == ImportResultType.Imported)
                                                   .SelectMany(c => c.ImportDecision.LocalEpisode.Episodes)
                                                   .Count() >= Math.Max(1,
                                          trackedDownload.RemoteEpisode.Episodes.Count);

            var historyItems = _historyService.FindByDownloadId(trackedDownload.DownloadItem.DownloadId)
                .OrderByDescending(h => h.Date)
                .ToList();

            var grabbedHistory = historyItems.Where(h => h.EventType == EpisodeHistoryEventType.Grabbed).ToList();
            var releaseInfo = grabbedHistory.Count > 0 ? new GrabbedReleaseInfo(grabbedHistory) : null;

            if (allEpisodesImported)
            {
                _logger.Debug("All episodes were imported for {0}", trackedDownload.DownloadItem.Title);
                trackedDownload.State = TrackedDownloadState.Imported;

                _eventAggregator.PublishEvent(new DownloadCompletedEvent(trackedDownload,
                    trackedDownload.RemoteEpisode.Series.Id,
                    importResults.Where(c => c.Result == ImportResultType.Imported).Select(c => c.EpisodeFile).ToList(),
                    releaseInfo));

                return true;
            }

            // Double check if all episodes were imported by checking the history if at least one
            // file was imported. This will allow the decision engine to reject already imported
            // episode files and still mark the download complete when all files are imported.

            // EDGE CASE: This process relies on EpisodeIds being consistent between executions, if a series is updated
            // and an episode is removed, but later comes back with a different ID then Sonarr will treat it as incomplete.
            // Since imports should be relatively fast and these types of data changes are infrequent this should be quite
            // safe, but commenting for future benefit.

            var atLeastOneEpisodeImported = importResults.Any(c => c.Result == ImportResultType.Imported);
            var allEpisodesImportedInHistory = _trackedDownloadAlreadyImported.IsImported(trackedDownload, historyItems);

            if (allEpisodesImportedInHistory)
            {
                // Log different error messages depending on the circumstances, but treat both as fully imported, because that's the reality.
                // The second message shouldn't be logged in most cases, but continued reporting would indicate an ongoing issue.

                if (atLeastOneEpisodeImported)
                {
                    _logger.Debug("All episodes were imported in history for {0}", trackedDownload.DownloadItem.Title);
                }
                else
                {
                    _logger.ForDebugEvent()
                           .Message("No Episodes were just imported, but all episodes were previously imported, possible issue with download history.")
                           .Property("SeriesId", trackedDownload.RemoteEpisode.Series.Id)
                           .Property("DownloadId", trackedDownload.DownloadItem.DownloadId)
                           .Property("Title", trackedDownload.DownloadItem.Title)
                           .Property("Path", trackedDownload.ImportItem.OutputPath.ToString())
                           .WriteSentryWarn("DownloadHistoryIncomplete")
                           .Log();
                }

                var episodes = _episodeService.GetEpisodes(trackedDownload.RemoteEpisode.Episodes.Select(e => e.Id));
                var files = _mediaFileService.GetFiles(episodes.Select(e => e.EpisodeFileId).Where(i => i > 0).Distinct());

                trackedDownload.State = TrackedDownloadState.Imported;
                _eventAggregator.PublishEvent(new DownloadCompletedEvent(trackedDownload, trackedDownload.RemoteEpisode.Series.Id, files, releaseInfo));

                return true;
            }

            _logger.Debug("Not all episodes have been imported for the release '{0}'", trackedDownload.DownloadItem.Title);
            return false;
        }

        private void SetStateToImportBlocked(TrackedDownload trackedDownload)
        {
            trackedDownload.State = TrackedDownloadState.ImportBlocked;

            if (!trackedDownload.HasNotifiedManualInteractionRequired)
            {
                var grabbedHistories = _historyService.FindByDownloadId(trackedDownload.DownloadItem.DownloadId).Where(h => h.EventType == EpisodeHistoryEventType.Grabbed).ToList();

                trackedDownload.HasNotifiedManualInteractionRequired = true;

                var releaseInfo = grabbedHistories.Count > 0 ? new GrabbedReleaseInfo(grabbedHistories) : null;
                var manualInteractionEvent = new ManualInteractionRequiredEvent(trackedDownload, releaseInfo);

                _eventAggregator.PublishEvent(manualInteractionEvent);
            }
        }

        private void SetImportItem(TrackedDownload trackedDownload)
        {
            trackedDownload.ImportItem = _provideImportItemService.ProvideImportItem(trackedDownload.DownloadItem, trackedDownload.ImportItem);
        }

        private Series AddSeriesForAutoImport(TrackedDownload trackedDownload)
        {
            if (string.IsNullOrWhiteSpace(_configService.DefaultRootFolderForAutoImport))
            {
                trackedDownload.Warn("Auto-import blocked: no default root folder configured for auto-import.");
                _logger.Debug("Auto-import blocked: no default root folder configured for auto-import.");
                SetStateToImportBlocked(trackedDownload);
                return null;
            }

            QualityProfile profile;
            profile = _configService.DefaultProfileForAutoImport == -1 ? _qualityProfileRepository.All().FirstOrDefault() : _qualityProfileRepository.Get(_configService.DefaultProfileForAutoImport);

            if (profile == null)
            {
                trackedDownload.Warn("Auto-import blocked: default quality profile not found (id: {0}).", _configService.DefaultProfileForAutoImport);
                _logger.Debug("Auto-import blocked: default quality profile not found (id: {0}).", _configService.DefaultProfileForAutoImport);
                SetStateToImportBlocked(trackedDownload);
                return null;
            }

            var parsedEpisodeInfo = trackedDownload.RemoteEpisode.ParsedEpisodeInfo;
            var series = FindSeriesForAutoImport(trackedDownload, parsedEpisodeInfo);

            if (series == null)
            {
                return null;
            }

            var existingSeries = _seriesService.FindByTvdbId(series.TvdbId);

            if (existingSeries != null)
            {
                _logger.Debug("Joining series '{0}' tvdbid: {1} to existing series '{2}'", series.Title, series.TvdbId, existingSeries.Path);

                AttachExistingSeries(trackedDownload, existingSeries);
                return existingSeries;
            }

            _logger.Debug("Autocreate series '{0}' tvdbid: {1}", series.Title, series.TvdbId);

            var tag = _tagService.All().Where(t => t.Label.EqualsIgnoreCase("autocreated")).ToList().FirstOrDefault();
            if (tag == null)
            {
                tag = new Tag
                {
                    Label = "autocreated"
                };
                tag = _tagService.Add(tag);
            }

            series.Monitored = true;
            series.MonitorNewItems = NewItemMonitorTypes.All;
            series.Tags.Add(tag.Id);
            series.QualityProfile = profile;
            series.QualityProfileId = profile.Id;
            series.RootFolderPath = _configService.DefaultRootFolderForAutoImport;
            series.SeasonFolder = true;
            series.AddOptions = new AddSeriesOptions
            {
                Monitor = MonitorTypes.All,
                SearchForMissingEpisodes = false,
                SearchForCutoffUnmetEpisodes = false
            };

            try
            {
                var newSeries = _addSeriesService.AddSeries(series);

                if (newSeries != null)
                {
                    newSeries.QualityProfile = profile;
                    newSeries.QualityProfileId = profile.Id;

                    RefreshEpisodesForNewSeries(newSeries);
                    EnsureRemoteEpisode(trackedDownload, newSeries);
                    trackedDownload.ClearStatus();

                    return newSeries;
                }
            }
            catch (ValidationException ex)
            {
                trackedDownload.Warn("Auto-import blocked: failed to add series '{0}' (tvdbid: {1}). {2}", series.Title, series.TvdbId, ex.Message);
                _logger.Debug(ex, "Auto-import blocked: failed to add series '{0}' tvdbid: {1}.", series.Title, series.TvdbId);
                SetStateToImportBlocked(trackedDownload);
                return null;
            }

            trackedDownload.Warn("Auto-import blocked: failed to add series '{0}' (tvdbid: {1}).", series.Title, series.TvdbId);
            _logger.Debug("Auto-import blocked: failed to add series '{0}' tvdbid: {1}.", series.Title, series.TvdbId);
            SetStateToImportBlocked(trackedDownload);
            return null;
        }

        private Series FindSeriesForAutoImport(TrackedDownload trackedDownload, ParsedEpisodeInfo parsedEpisodeInfo)
        {
            if (parsedEpisodeInfo?.TvdbId != null)
            {
                var tvdbSeries = _seriesService.FindByTvdbId(parsedEpisodeInfo.TvdbId.Value);

                if (tvdbSeries != null)
                {
                    return tvdbSeries;
                }

                var tvdbMatches = _searchProxy.SearchForNewSeries($"tvdb:{parsedEpisodeInfo.TvdbId.Value}");

                if (tvdbMatches.Count == 1)
                {
                    return tvdbMatches.First();
                }

                trackedDownload.Warn("Auto-import blocked: no unique series match for '{0}' (tvdbid: {1}).", trackedDownload.DownloadItem.Title, parsedEpisodeInfo.TvdbId.Value);
                _logger.Debug("Auto-import blocked: no unique series match for '{0}' (tvdbid: {1}).", trackedDownload.DownloadItem.Title, parsedEpisodeInfo.TvdbId.Value);
                SetStateToImportBlocked(trackedDownload);
                return null;
            }

            var searchTerm = GetSeriesSearchTerm(trackedDownload, parsedEpisodeInfo);
            var series = _searchProxy.SearchForNewSeries(searchTerm);

            if (series == null || series.Count <= 0)
            {
                trackedDownload.Warn("Auto-import blocked: no series match found for '{0}'.", trackedDownload.DownloadItem.Title);
                _logger.Debug("Auto-import blocked: no series match found for '{0}'.", trackedDownload.DownloadItem.Title);
                SetStateToImportBlocked(trackedDownload);
                return null;
            }

            var parsedYear = parsedEpisodeInfo?.SeriesTitleInfo?.Year ?? 0;
            Series match = null;

            if (parsedYear > 1890 && series.Count(s => s.Year == parsedYear) == 1)
            {
                match = series.First(s => s.Year == parsedYear);
            }

            if (match == null && series.Count == 1)
            {
                match = series.First();
            }

            if (match == null)
            {
                trackedDownload.Warn("Auto-import blocked: no unique series match for '{0}' (parsed year: {1}).", trackedDownload.DownloadItem.Title, parsedYear);
                _logger.Debug("Auto-import blocked: no unique series match for '{0}' (parsed year: {1}).", trackedDownload.DownloadItem.Title, parsedYear);
                SetStateToImportBlocked(trackedDownload);
                return null;
            }

            return match;
        }

        private string GetSeriesSearchTerm(TrackedDownload trackedDownload, ParsedEpisodeInfo parsedEpisodeInfo)
        {
            if (parsedEpisodeInfo?.SeriesTitleInfo?.TitleWithoutYear.IsNotNullOrWhiteSpace() == true)
            {
                return parsedEpisodeInfo.SeriesTitleInfo.TitleWithoutYear;
            }

            if (parsedEpisodeInfo?.SeriesTitle.IsNotNullOrWhiteSpace() == true)
            {
                return parsedEpisodeInfo.SeriesTitle;
            }

            return Path.GetFileName(trackedDownload.DownloadItem.Title);
        }

        private void RefreshEpisodesForNewSeries(Series series)
        {
            try
            {
                var seriesInfo = _seriesInfo.GetSeriesInfo(series.TvdbId);
                _refreshEpisodeService.RefreshEpisodeInfo(series, seriesInfo.Item2);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to refresh episodes for auto-created series '{0}' before import.", series.Title);
            }
        }

        private void AttachExistingSeries(TrackedDownload trackedDownload, Series series)
        {
            EnsureRemoteEpisode(trackedDownload, series);

            trackedDownload.ClearStatus();

            trackedDownload.State = TrackedDownloadState.ImportPending;
        }

        private void EnsureRemoteEpisode(TrackedDownload trackedDownload, Series series)
        {
            var release = trackedDownload.RemoteEpisode?.Release;
            var customFormats = trackedDownload.RemoteEpisode?.CustomFormats;
            var parsedEpisodeInfo = trackedDownload.RemoteEpisode?.ParsedEpisodeInfo ?? Parser.Parser.ParseTitle(trackedDownload.DownloadItem.Title, _configService.ParseTvdbIdFromReleaseName);

            if (parsedEpisodeInfo != null)
            {
                var remoteEpisode = _parsingService.Map(parsedEpisodeInfo, series);

                if (remoteEpisode != null)
                {
                    trackedDownload.RemoteEpisode = remoteEpisode;
                }
            }

            if (trackedDownload.RemoteEpisode == null)
            {
                trackedDownload.RemoteEpisode = new RemoteEpisode();
            }

            trackedDownload.RemoteEpisode.Series ??= series;
            trackedDownload.RemoteEpisode.Release ??= release;
            trackedDownload.RemoteEpisode.CustomFormats = customFormats ?? trackedDownload.RemoteEpisode.CustomFormats;
        }

        private bool BlockAutoImportForExistingEpisodeFiles(TrackedDownload trackedDownload)
        {
            if (!_configService.BlockAutoImportForExistingEpisodeFiles ||
                trackedDownload.RemoteEpisode?.Episodes == null ||
                trackedDownload.RemoteEpisode.Episodes.None(e => e.HasFile))
            {
                return false;
            }

            trackedDownload.Warn("Auto-import blocked: one or more matched episodes already have files in library.");
            _logger.Warn("Auto-import blocked for '{0}': one or more matched episodes already have files in library.", trackedDownload.DownloadItem.Title);
            SetStateToImportBlocked(trackedDownload);

            return true;
        }

        private bool ValidatePath(TrackedDownload trackedDownload)
        {
            var downloadItemOutputPath = trackedDownload.ImportItem.OutputPath;

            if (downloadItemOutputPath.IsEmpty)
            {
                trackedDownload.Warn("Download doesn't contain intermediate path, Skipping.");
                return false;
            }

            if ((OsInfo.IsWindows && !downloadItemOutputPath.IsWindowsPath) ||
                (OsInfo.IsNotWindows && !downloadItemOutputPath.IsUnixPath))
            {
                trackedDownload.Warn("[{0}] is not a valid local path. You may need a Remote Path Mapping. Check the download troubleshooting entry on the wiki for details.", downloadItemOutputPath);
                return false;
            }

            return true;
        }
    }
}
