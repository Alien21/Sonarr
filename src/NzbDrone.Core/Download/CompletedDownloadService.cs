using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentValidation;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Extras.Subtitles;
using NzbDrone.Core.History;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.EpisodeImport;
using NzbDrone.Core.MediaFiles.EpisodeImport.Aggregation;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Tags;
using NzbDrone.Core.Tv;
using NzbDrone.Core.Tv.Translations;

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
        private readonly IDiskProvider _diskProvider;
        private readonly IDiskScanService _diskScanService;
        private readonly IMakeImportDecision _importDecisionMaker;
        private readonly IAggregationService _aggregationService;
        private readonly IDualAudioImportPreference _dualAudioImportPreference;
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
                                        IDiskProvider diskProvider,
                                        IDiskScanService diskScanService,
                                        IMakeImportDecision importDecisionMaker,
                                        IAggregationService aggregationService,
                                        IDualAudioImportPreference dualAudioImportPreference,
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
            _diskProvider = diskProvider;
            _diskScanService = diskScanService;
            _importDecisionMaker = importDecisionMaker;
            _aggregationService = aggregationService;
            _dualAudioImportPreference = dualAudioImportPreference;
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
                AnalyzeCompletedDownloadFile(trackedDownload);
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
                        AnalyzeCompletedDownloadFile(trackedDownload);

                        trackedDownload.Warn("Found matching series via grab history, but release was matched to series by ID. Automatic import is not possible. See the FAQ for details.");
                        SetStateToImportBlocked(trackedDownload);

                        return;
                    }

                    AttachExistingSeries(trackedDownload, series);
                    AnalyzeCompletedDownloadFile(trackedDownload);
                }
            }

            if (trackedDownload.RemoteEpisode == null)
            {
                var parsed = Parser.Parser.ParseTitle(trackedDownload.DownloadItem.Title, _configService.ParseTvdbIdFromReleaseName, _configService.ParseEpisodeNumberOnlyAsSeasonOne);

                if (parsed != null)
                {
                    trackedDownload.RemoteEpisode = _parsingService.Map(parsed, 0, 0, null);
                }
            }

            if (trackedDownload.RemoteEpisode == null)
            {
                AnalyzeCompletedDownloadFile(trackedDownload);

                trackedDownload.Warn("Auto-import blocked: unable to resolve {0} download to a series.", trackedDownload.ImportItem?.Title);
                _logger.Debug("Auto-import blocked: unable to resolve {0} download to a series.", trackedDownload.ImportItem?.Title);
                SetStateToImportBlocked(trackedDownload);

                return;
            }

            if (series == null)
            {
                if (!AllowAutomaticImport(trackedDownload))
                {
                    AnalyzeCompletedDownloadFile(trackedDownload);
                    return;
                }

                if (string.IsNullOrWhiteSpace(_configService.DefaultRootFolderForAutoImport))
                {
                    AnalyzeCompletedDownloadFile(trackedDownload);

                    trackedDownload.Warn("Series title mismatch; automatic import is not possible. Check the download troubleshooting entry on the wiki for common causes.");
                    SetStateToImportBlocked(trackedDownload);

                    return;
                }

                series = AddSeriesForAutoImport(trackedDownload);

                if (series == null)
                {
                    AnalyzeCompletedDownloadFile(trackedDownload);
                    return;
                }
            }

            if (BlockAutoImportForExistingEpisodeFiles(trackedDownload))
            {
                return;
            }

            _logger.Debug("Set State='{0}' for series '{1}' tvdbid: {2}", TrackedDownloadState.ImportPending, series.Title, series.TvdbId);

            AnalyzeCompletedDownloadFile(trackedDownload);

            trackedDownload.State = TrackedDownloadState.ImportPending;
        }

        public void Import(TrackedDownload trackedDownload)
        {
            SetImportItem(trackedDownload);

            if (!ValidatePath(trackedDownload))
            {
                return;
            }

            AnalyzeCompletedDownloadFile(trackedDownload);

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
            if (!AllowAutomaticImport(trackedDownload))
            {
                return null;
            }

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

        private bool AllowAutomaticImport(TrackedDownload trackedDownload)
        {
            if (_configService.AllowAutomaticImport)
            {
                return true;
            }

            trackedDownload.Warn("Auto-import blocked: automatic import is disabled.");
            _logger.Debug("Auto-import blocked: automatic import is disabled.");
            SetStateToImportBlocked(trackedDownload);
            return false;
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

            if (parsedYear > 1890)
            {
                var seriesInYear = series.Where(s => s.Year == parsedYear).ToList();

                if (seriesInYear.Count == 1)
                {
                    match = seriesInYear.First();
                }
                else if (seriesInYear.Count > 1)
                {
                    _logger.Debug("Auto-import found {0} candidate series for '{1}' in year {2}; trying exact default/localized title match.",
                        seriesInYear.Count,
                        trackedDownload.DownloadItem.Title,
                        parsedYear);

                    match = _searchProxy.SearchForNewSeriesByExactTitle(searchTerm, parsedYear, seriesInYear);

                    if (match != null)
                    {
                        _logger.Debug("Auto-import exact title match for '{0}' resolved to '{1}' tvdbid: {2}", trackedDownload.DownloadItem.Title, match.Title, match.TvdbId);
                    }
                    else
                    {
                        _logger.Debug("Auto-import exact default/localized title match did not resolve '{0}' for year {1}.", trackedDownload.DownloadItem.Title, parsedYear);
                    }
                }
            }
            else if (series.Count > 1)
            {
                match = FindSingleExactTitleMatchWithoutPrefixConflicts(searchTerm, series);

                if (match != null)
                {
                    _logger.Debug("Auto-import exact title match for '{0}' resolved to '{1}' tvdbid: {2}", trackedDownload.DownloadItem.Title, match.Title, match.TvdbId);
                }
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

        private Series FindSingleExactTitleMatchWithoutPrefixConflicts(string searchTerm, List<Series> candidates)
        {
            var cleanTitle = Parser.Parser.CleanSeriesTitle(searchTerm);

            if (cleanTitle.IsNullOrWhiteSpace())
            {
                return null;
            }

            var exactMatches = candidates
                .Where(s => HasExactCleanTitle(s, cleanTitle))
                .DistinctBy(s => s.TvdbId)
                .ToList();

            if (exactMatches.Count != 1)
            {
                if (exactMatches.Count > 1)
                {
                    _logger.Debug("Auto-import exact title match for '{0}' refused because multiple exact candidate series matched: {1}", searchTerm, FormatCandidateSeries(exactMatches));
                }

                return null;
            }

            var exactMatch = exactMatches.Single();
            var prefixConflicts = candidates
                .Where(s => s.TvdbId != exactMatch.TvdbId)
                .Where(s => GetCleanTitles(s).Any(t => t.StartsWith(cleanTitle, StringComparison.Ordinal)))
                .DistinctBy(s => s.TvdbId)
                .ToList();

            if (prefixConflicts.Any())
            {
                _logger.Debug("Auto-import exact title match for '{0}' refused because candidate title prefixes also matched: {1}", searchTerm, FormatCandidateSeries(prefixConflicts));
                return null;
            }

            return exactMatch;
        }

        private static bool HasExactCleanTitle(Series series, string cleanTitle)
        {
            return GetCleanTitles(series).Any(t => t == cleanTitle);
        }

        private static IEnumerable<string> GetCleanTitles(Series series)
        {
            if (series.CleanTitle.IsNotNullOrWhiteSpace())
            {
                yield return series.CleanTitle;
            }

            if (series.Title.IsNotNullOrWhiteSpace())
            {
                yield return Parser.Parser.CleanSeriesTitle(series.Title);
            }

            foreach (var translation in series.Translations ?? Enumerable.Empty<SeriesTranslation>())
            {
                if (translation.CleanTitle.IsNotNullOrWhiteSpace())
                {
                    yield return translation.CleanTitle;
                }

                if (translation.Title.IsNotNullOrWhiteSpace())
                {
                    yield return Parser.Parser.CleanSeriesTitle(translation.Title);
                }
            }
        }

        private static string FormatCandidateSeries(IEnumerable<Series> series)
        {
            var matches = series
                .Select(s => $"{s.Title} ({s.Year}) tvdbid: {s.TvdbId}")
                .ToList();

            return matches.Any() ? string.Join(", ", matches) : "none";
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

        private void AnalyzeCompletedDownloadFile(TrackedDownload trackedDownload)
        {
            if (!_configService.AnalyzeCompletedDownloadFiles ||
                trackedDownload.DownloadItem.Status != DownloadItemStatus.Completed ||
                trackedDownload.ImportItem == null)
            {
                return;
            }

            var outputPath = trackedDownload.ImportItem.OutputPath.FullPath;
            var seriesId = trackedDownload.RemoteEpisode?.Series?.Id;

            if (outputPath.IsNullOrWhiteSpace() ||
                (trackedDownload.AnalyzedMediaInfoPath?.Equals(outputPath) == true &&
                 trackedDownload.AnalyzedMediaInfoSeriesId == seriesId))
            {
                return;
            }

            trackedDownload.AnalyzedMediaInfoPath = outputPath;
            trackedDownload.AnalyzedMediaInfoSeriesId = seriesId;

            try
            {
                var localEpisodes = GetCompletedDownloadQueueEpisodes(trackedDownload, outputPath);

                if (localEpisodes.Empty())
                {
                    _logger.Debug("Completed download file analysis did not find a media file for queue item '{0}'", trackedDownload.DownloadItem.Title);
                    return;
                }

                ApplyCompletedDownloadFileAnalysis(trackedDownload, localEpisodes);

                var queueEpisode = localEpisodes.FirstOrDefault(HasQueueMetadata);
                var mediaInfo = queueEpisode?.MediaInfo;

                var externalSubtitles = GetExternalSubtitleFiles(queueEpisode);

                _logger.Debug("Completed download file analysis updated queue item '{0}' from file '{1}'. Quality: '{2}', queue languages: '{3}', queue subtitles: '{4}', media title: '{5}', embedded audio: '{6}', embedded subtitles: '{7}', external subtitles: '{8}', matched episodes: '{9}'",
                    trackedDownload.DownloadItem.Title,
                    queueEpisode?.Path ?? "none",
                    queueEpisode?.Quality?.ToString() ?? "none",
                    FormatValues(queueEpisode?.Languages),
                    FormatValues(trackedDownload.AnalyzedSubtitleLanguages),
                    mediaInfo?.Title ?? "none",
                    FormatValues(mediaInfo?.AudioLanguages),
                    FormatValues(mediaInfo?.Subtitles),
                    FormatExternalSubtitleFiles(externalSubtitles),
                    FormatEpisodes(queueEpisode?.Episodes));
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to analyze completed download file for queue item '{0}'", trackedDownload.DownloadItem.Title);
            }
        }

        private void ApplyCompletedDownloadFileAnalysis(TrackedDownload trackedDownload, List<LocalEpisode> localEpisodes)
        {
            var queueEpisode = localEpisodes.FirstOrDefault(HasQueueMetadata);

            if (queueEpisode == null)
            {
                return;
            }

            if (queueEpisode.Quality != null &&
                queueEpisode.Quality.Quality != Quality.Unknown)
            {
                trackedDownload.AnalyzedQuality = queueEpisode.Quality;
            }

            if (HasKnownLanguages(queueEpisode.Languages))
            {
                trackedDownload.AnalyzedLanguages = queueEpisode.Languages;
            }

            var subtitleLanguages = GetSubtitleLanguages(queueEpisode);
            if (subtitleLanguages.Any())
            {
                trackedDownload.AnalyzedSubtitleLanguages = subtitleLanguages;
            }

            var analyzedEpisodeFiles = localEpisodes
                .Where(HasQueueMetadata)
                .Select(localEpisode => new
                {
                    LocalEpisode = localEpisode,
                    File = ToAnalyzedDownloadFile(localEpisode)
                })
                .Where(item => item.LocalEpisode.Episodes?.Any() == true)
                .SelectMany(item => item.LocalEpisode.Episodes.Select(episode => new
                {
                    episode.Id,
                    item.File
                }))
                .GroupBy(item => item.Id)
                .ToDictionary(item => item.Key, item => item.First().File);

            if (analyzedEpisodeFiles.Empty())
            {
                return;
            }

            trackedDownload.AnalyzedEpisodeFiles ??= new Dictionary<int, AnalyzedDownloadFile>();

            foreach (var analyzedEpisodeFile in analyzedEpisodeFiles)
            {
                trackedDownload.AnalyzedEpisodeFiles[analyzedEpisodeFile.Key] = analyzedEpisodeFile.Value;
            }
        }

        private AnalyzedDownloadFile ToAnalyzedDownloadFile(LocalEpisode localEpisode)
        {
            return new AnalyzedDownloadFile
            {
                Path = localEpisode.Path,
                Quality = localEpisode.Quality,
                Languages = localEpisode.Languages,
                SubtitleLanguages = GetSubtitleLanguages(localEpisode)
            };
        }

        private List<LocalEpisode> GetCompletedDownloadQueueEpisodes(TrackedDownload trackedDownload, string outputPath)
        {
            var series = trackedDownload.RemoteEpisode?.Series;

            if (!TryGetCompletedDownloadVideoFiles(outputPath, out var folderInfo, out var videoFiles))
            {
                return new List<LocalEpisode>();
            }

            if (series != null)
            {
                return _importDecisionMaker.GetImportDecisions(videoFiles, series, trackedDownload.ImportItem, folderInfo, true, false)
                                           .Select(decision => decision.LocalEpisode)
                                           .Where(HasQueueMetadata)
                                           .OrderBy(localEpisode => localEpisode.Path)
                                           .ToList();
            }

            var otherVideoFiles = videoFiles.Count > 1;

            return videoFiles
                .Select(videoFile => GetCompletedDownloadQueueEpisode(trackedDownload, videoFile, folderInfo, otherVideoFiles))
                .Where(HasQueueMetadata)
                .Take(1)
                .OrderBy(localEpisode => localEpisode.Path)
                .ToList();
        }

        private List<ImportDecision> GetCompletedDownloadImportDecisions(TrackedDownload trackedDownload, string outputPath)
        {
            var series = trackedDownload.RemoteEpisode?.Series;

            if (series == null ||
                !TryGetCompletedDownloadVideoFiles(outputPath, out var folderInfo, out var videoFiles))
            {
                return new List<ImportDecision>();
            }

            return _importDecisionMaker.GetImportDecisions(videoFiles, series, trackedDownload.ImportItem, folderInfo, true, false);
        }

        private bool TryGetCompletedDownloadVideoFiles(string outputPath, out ParsedEpisodeInfo folderInfo, out List<string> videoFiles)
        {
            folderInfo = null;

            if (_diskProvider.FolderExists(outputPath))
            {
                var directoryInfo = new DirectoryInfo(outputPath);
                folderInfo = Parser.Parser.ParseTitle(GetCleanedUpFolderName(directoryInfo.Name), _configService.ParseTvdbIdFromReleaseName, _configService.ParseEpisodeNumberOnlyAsSeasonOne);
                videoFiles = _diskScanService.FilterPaths(directoryInfo.FullName, _diskScanService.GetVideoFiles(directoryInfo.FullName))
                                            .OrderBy(path => path)
                                            .ToList();

                return true;
            }

            if (_diskProvider.FileExists(outputPath) &&
                MediaFileExtensions.Extensions.Contains(Path.GetExtension(outputPath)))
            {
                videoFiles = new List<string> { outputPath };
                return true;
            }

            videoFiles = new List<string>();
            return false;
        }

        private LocalEpisode GetCompletedDownloadQueueEpisode(TrackedDownload trackedDownload, string videoFile, ParsedEpisodeInfo folderInfo, bool otherVideoFiles)
        {
            var fileInfo = Parser.Parser.ParsePath(videoFile, _configService.ParseTvdbIdFromReleaseName, _configService.ParseEpisodeNumberOnlyAsSeasonOne);
            var downloadClientEpisodeInfo = trackedDownload.RemoteEpisode?.ParsedEpisodeInfo ??
                                            Parser.Parser.ParseTitle(trackedDownload.DownloadItem.Title, _configService.ParseTvdbIdFromReleaseName, _configService.ParseEpisodeNumberOnlyAsSeasonOne);

            var localEpisode = new LocalEpisode
            {
                Path = videoFile,
                Series = trackedDownload.RemoteEpisode?.Series,
                DownloadItem = trackedDownload.ImportItem,
                DownloadClientEpisodeInfo = downloadClientEpisodeInfo,
                FolderEpisodeInfo = folderInfo,
                FileEpisodeInfo = fileInfo ?? GetFallbackFileEpisodeInfo(videoFile),
                ExistingFile = trackedDownload.RemoteEpisode?.Series?.Path.IsParentPath(videoFile) ?? false,
                SceneSource = true,
                OtherVideoFiles = otherVideoFiles
            };

            return _aggregationService.Augment(localEpisode, trackedDownload.ImportItem);
        }

        private ParsedEpisodeInfo GetFallbackFileEpisodeInfo(string path)
        {
            return new ParsedEpisodeInfo
            {
                ReleaseTitle = Path.GetFileNameWithoutExtension(path),
                SeriesTitle = Path.GetFileNameWithoutExtension(path),
                Quality = QualityParser.ParseQuality(path),
                Languages = LanguageParser.ParseLanguages(path)
            };
        }

        private bool HasQueueMetadata(LocalEpisode localEpisode)
        {
            return localEpisode != null &&
                   (localEpisode.MediaInfo != null ||
                    (localEpisode.Quality != null && localEpisode.Quality.Quality != Quality.Unknown) ||
                    HasKnownLanguages(localEpisode.Languages));
        }

        private bool HasKnownLanguages(List<Language> languages)
        {
            return languages?.Any() == true && languages.Any(l => l != Language.Unknown);
        }

        private List<Language> GetSubtitleLanguages(LocalEpisode localEpisode)
        {
            var languages = new List<Language>();

            var embeddedSubtitleLanguages = localEpisode.MediaInfo?.Subtitles?
                                                        .Where(language => language.IsNotNullOrWhiteSpace())
                                                        .Distinct()
                                                        .ToList() ?? new List<string>();

            foreach (var subtitleLanguage in embeddedSubtitleLanguages)
            {
                languages.AddIfNotNull(IsoLanguages.Find(subtitleLanguage)?.Language);
            }

            foreach (var subtitleFile in GetExternalSubtitleFiles(localEpisode))
            {
                languages.AddIfNotNull(subtitleFile.Info?.Language);
            }

            return languages
                .Where(language => language != Language.Unknown)
                .GroupBy(language => language.Id)
                .Select(group => group.First())
                .ToList();
        }

        private List<ExternalSubtitleFile> GetExternalSubtitleFiles(LocalEpisode localEpisode)
        {
            if (localEpisode?.Path.IsNullOrWhiteSpace() != false)
            {
                return new List<ExternalSubtitleFile>();
            }

            var sourceFolder = _diskProvider.GetParentFolder(localEpisode.Path);

            if (sourceFolder.IsNullOrWhiteSpace() || !_diskProvider.FolderExists(sourceFolder))
            {
                return new List<ExternalSubtitleFile>();
            }

            var subtitleFiles = _diskProvider.GetFiles(sourceFolder, false)
                                             .Where(file => SubtitleFileExtensions.Extensions.Contains(Path.GetExtension(file)))
                                             .ToList();

            if (subtitleFiles.Empty())
            {
                return new List<ExternalSubtitleFile>();
            }

            var sourceFileName = Path.GetFileNameWithoutExtension(localEpisode.Path);
            var matchingFiles = subtitleFiles
                .Where(file => Path.GetFileNameWithoutExtension(file).StartsWithIgnoreCase(sourceFileName))
                .ToList();

            if (matchingFiles.Empty() && localEpisode.FileEpisodeInfo != null)
            {
                matchingFiles = subtitleFiles
                    .Where(file => SubtitleMatchesLocalEpisode(file, localEpisode.FileEpisodeInfo))
                    .ToList();
            }

            if (matchingFiles.Empty())
            {
                var videoFiles = _diskProvider.GetFiles(sourceFolder, false)
                                              .Where(file => MediaFileExtensions.Extensions.Contains(Path.GetExtension(file)))
                                              .ToList();

                if (videoFiles.Count == 1)
                {
                    matchingFiles = subtitleFiles;
                }
            }

            return matchingFiles
                .Select(file => new ExternalSubtitleFile
                {
                    Path = file,
                    Info = LanguageParser.ParseSubtitleLanguageInformation(file)
                })
                .ToList();
        }

        private bool SubtitleMatchesLocalEpisode(string subtitleFile, ParsedEpisodeInfo fileEpisodeInfo)
        {
            var subtitleEpisodeInfo = Parser.Parser.ParsePath(subtitleFile, _configService.ParseTvdbIdFromReleaseName, _configService.ParseEpisodeNumberOnlyAsSeasonOne);

            if (subtitleEpisodeInfo == null ||
                !string.Equals(subtitleEpisodeInfo.SeriesTitle, fileEpisodeInfo.SeriesTitle, StringComparison.InvariantCultureIgnoreCase) ||
                subtitleEpisodeInfo.SeasonNumber != fileEpisodeInfo.SeasonNumber)
            {
                return false;
            }

            return (fileEpisodeInfo.EpisodeNumbers.Any() &&
                    subtitleEpisodeInfo.EpisodeNumbers.SequenceEqual(fileEpisodeInfo.EpisodeNumbers)) ||
                   (fileEpisodeInfo.AbsoluteEpisodeNumbers.Any() &&
                    subtitleEpisodeInfo.AbsoluteEpisodeNumbers.SequenceEqual(fileEpisodeInfo.AbsoluteEpisodeNumbers));
        }

        private string FormatValues<T>(IEnumerable<T> values)
        {
            var formattedValues = values?.Select(value => value?.ToString())
                                         .Where(value => value.IsNotNullOrWhiteSpace())
                                         .Distinct()
                                         .ToList();

            return formattedValues?.Any() == true ? string.Join(", ", formattedValues) : "none";
        }

        private string FormatExternalSubtitleFiles(List<ExternalSubtitleFile> subtitleFiles)
        {
            if (subtitleFiles.Empty())
            {
                return "none";
            }

            return string.Join("; ", subtitleFiles.Select(file =>
            {
                var info = file.Info;
                var details = new List<string>
                {
                    $"language: {info.Language}"
                };

                if (info.LanguageTags?.Any() == true)
                {
                    details.Add($"tags: {string.Join(", ", info.LanguageTags)}");
                }

                if (info.Title.IsNotNullOrWhiteSpace())
                {
                    details.Add($"title: {info.Title}");
                }

                if (info.Copy > 0)
                {
                    details.Add($"copy: {info.Copy}");
                }

                return $"{file.Path} [{string.Join(", ", details)}]";
            }));
        }

        private string FormatEpisodes(List<Episode> episodes)
        {
            if (episodes?.Any() != true)
            {
                return "none";
            }

            return string.Join(", ", episodes.Select(episode => $"{episode.SeasonNumber}x{episode.EpisodeNumber:00}"));
        }

        private string GetCleanedUpFolderName(string folder)
        {
            return folder.Replace("_UNPACK_", "")
                         .Replace("_FAILED_", "");
        }

        private void EnsureRemoteEpisode(TrackedDownload trackedDownload, Series series)
        {
            var release = trackedDownload.RemoteEpisode?.Release;
            var customFormats = trackedDownload.RemoteEpisode?.CustomFormats;
            var parsedEpisodeInfo = trackedDownload.RemoteEpisode?.ParsedEpisodeInfo ?? Parser.Parser.ParseTitle(trackedDownload.DownloadItem.Title, _configService.ParseTvdbIdFromReleaseName, _configService.ParseEpisodeNumberOnlyAsSeasonOne);

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

            AnalyzeCompletedDownloadFile(trackedDownload);

            if (ShouldBypassExistingEpisodeAutoImportBlock(trackedDownload))
            {
                return false;
            }

            trackedDownload.Warn("Auto-import blocked: one or more matched episodes already have files in library.");
            _logger.Warn("Auto-import blocked for '{0}': one or more matched episodes already have files in library.", trackedDownload.DownloadItem.Title);
            SetStateToImportBlocked(trackedDownload);

            return true;
        }

        private bool ShouldBypassExistingEpisodeAutoImportBlock(TrackedDownload trackedDownload)
        {
            if (trackedDownload.ImportItem == null ||
                trackedDownload.ImportItem.OutputPath.FullPath.IsNullOrWhiteSpace())
            {
                return false;
            }

            var decisions = GetCompletedDownloadImportDecisions(trackedDownload, trackedDownload.ImportItem.OutputPath.FullPath);
            var qualityUpgradeDecision = GetApprovedQualityUpgradeDecision(decisions);

            if (qualityUpgradeDecision != null)
            {
                _logger.Info("Auto-import block bypassed: '{0}' has an approved quality upgrade '{1}'.", trackedDownload.DownloadItem.Title, qualityUpgradeDecision.LocalEpisode.Path);
                return true;
            }

            if (!_configService.PreferDualAudio)
            {
                return false;
            }

            var preferredDualAudioDecision = decisions.FirstOrDefault(decision =>
            {
                if (!decision.Approved)
                {
                    return false;
                }

                return HasPreferredDualAudioUpgrade(decision.LocalEpisode);
            });

            if (preferredDualAudioDecision == null)
            {
                return false;
            }

            _logger.Info("Auto-import block bypassed: '{0}' has an approved preferred dual-audio upgrade '{1}'.", trackedDownload.DownloadItem.Title, preferredDualAudioDecision.LocalEpisode.Path);
            return true;
        }

        private ImportDecision GetApprovedQualityUpgradeDecision(List<ImportDecision> decisions)
        {
            return decisions.FirstOrDefault(decision =>
                decision.Approved &&
                IsQualityUpgradeForExistingEpisodeFiles(decision.LocalEpisode));
        }

        private bool IsQualityUpgradeForExistingEpisodeFiles(LocalEpisode localEpisode)
        {
            if (localEpisode?.Series?.QualityProfile == null ||
                localEpisode.Quality == null)
            {
                return false;
            }

            var existingEpisodeFiles = localEpisode.Episodes?
                .Where(episode => episode.EpisodeFileId > 0)
                .Select(episode => episode.EpisodeFile?.Value)
                .Where(episodeFile => episodeFile != null)
                .ToList();

            if (existingEpisodeFiles.Empty())
            {
                return false;
            }

            var qualityComparer = new QualityModelComparer(localEpisode.Series.QualityProfile.Value);

            if (ReplacesPreferredDualAudioWithNonDual(localEpisode, existingEpisodeFiles))
            {
                return false;
            }

            return existingEpisodeFiles.All(episodeFile =>
                episodeFile.Quality != null &&
                qualityComparer.Compare(localEpisode.Quality, episodeFile.Quality) > 0);
        }

        private bool ReplacesPreferredDualAudioWithNonDual(LocalEpisode localEpisode, List<EpisodeFile> existingEpisodeFiles)
        {
            if (!_configService.PreferDualAudio)
            {
                return false;
            }

            var preferredLanguage = (Language)_configService.SeriesInfoLanguage;

            if (!IsKnownLanguage(preferredLanguage) ||
                existingEpisodeFiles.None(episodeFile => HasPreferredDualAudio(episodeFile.MediaInfo, episodeFile.Languages, preferredLanguage)))
            {
                return false;
            }

            return !HasPreferredDualAudio(localEpisode.MediaInfo, localEpisode.Languages, preferredLanguage);
        }

        private static bool HasPreferredDualAudio(MediaInfoModel mediaInfo, List<Language> parsedLanguages, Language preferredLanguage)
        {
            var audioLanguages = GetAudioLanguages(mediaInfo, parsedLanguages);

            return audioLanguages.KnownLanguages.Contains(preferredLanguage) &&
                   audioLanguages.DistinctAudioLanguages.Count > 1;
        }

        private static AudioLanguageSet GetAudioLanguages(MediaInfoModel mediaInfo, List<Language> parsedLanguages)
        {
            var languages = new AudioLanguageSet();

            foreach (var audioLanguage in mediaInfo?.AudioLanguages ?? new List<string>())
            {
                AddRawLanguage(languages, audioLanguage);
            }

            foreach (var language in parsedLanguages ?? new List<Language>())
            {
                AddKnownLanguage(languages, language);
            }

            return languages;
        }

        private static void AddRawLanguage(AudioLanguageSet languages, string rawLanguage)
        {
            if (rawLanguage.IsNullOrWhiteSpace())
            {
                return;
            }

            var language = ParseLanguage(rawLanguage);

            if (IsKnownLanguage(language))
            {
                AddKnownLanguage(languages, language);
                return;
            }

            languages.DistinctAudioLanguages.Add(rawLanguage.Trim().ToLowerInvariant());
        }

        private static void AddKnownLanguage(AudioLanguageSet languages, Language language)
        {
            if (!IsKnownLanguage(language))
            {
                return;
            }

            languages.KnownLanguages.Add(language);
            languages.DistinctAudioLanguages.Add($"language:{language.Id}");
        }

        private static Language ParseLanguage(string rawLanguage)
        {
            var trimmedLanguage = rawLanguage.Trim();

            return IsoLanguages.Find(trimmedLanguage)?.Language ??
                   IsoLanguages.FindByName(trimmedLanguage)?.Language;
        }

        private static bool IsKnownLanguage(Language language)
        {
            return language is { Id: > 0 };
        }

        private bool HasPreferredDualAudioUpgrade(LocalEpisode localEpisode)
        {
            return localEpisode?.Episodes?
                .Where(episode => episode.EpisodeFileId > 0)
                .Any(episode =>
                {
                    var episodeFile = episode.EpisodeFile?.Value;
                    var dualAudioPreference = _dualAudioImportPreference.Evaluate(localEpisode, episodeFile);

                    return dualAudioPreference?.IsPreferredUpgrade == true;
                }) == true;
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

        private class ExternalSubtitleFile
        {
            public string Path { get; set; }
            public SubtitleTitleInfo Info { get; set; }
        }

        private class AudioLanguageSet
        {
            public HashSet<Language> KnownLanguages { get; } = new HashSet<Language>();
            public HashSet<string> DistinctAudioLanguages { get; } = new HashSet<string>();
        }
    }
}
