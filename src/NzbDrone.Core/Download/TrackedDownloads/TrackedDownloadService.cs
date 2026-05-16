using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download.Aggregation;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Tv;
using NzbDrone.Core.Tv.Events;

namespace NzbDrone.Core.Download.TrackedDownloads
{
    public interface ITrackedDownloadService
    {
        TrackedDownload Find(string downloadId);
        void StopTracking(string downloadId);
        void StopTracking(List<string> downloadIds);
        TrackedDownload TrackDownload(DownloadClientDefinition downloadClient, DownloadClientItem downloadItem);
        List<TrackedDownload> GetTrackedDownloads();
        void UpdateTrackable(List<TrackedDownload> trackedDownloads);
    }

    public class TrackedDownloadService : ITrackedDownloadService,
                                          IHandle<EpisodeInfoRefreshedEvent>,
                                          IHandle<SeriesAddedEvent>,
                                          IHandle<SeriesDeletedEvent>
    {
        private readonly IParsingService _parsingService;
        private readonly IHistoryService _historyService;
        private readonly IEventAggregator _eventAggregator;
        private readonly IDownloadHistoryService _downloadHistoryService;
        private readonly ITrackedDownloadAlreadyImported _trackedDownloadAlreadyImported;
        private readonly IRemoteEpisodeAggregationService _aggregationService;
        private readonly ICustomFormatCalculationService _formatCalculator;
        private readonly IConfigService _configService;
        private readonly Logger _logger;
        private readonly ICached<TrackedDownload> _cache;

        public TrackedDownloadService(IParsingService parsingService,
                                      ICacheManager cacheManager,
                                      IHistoryService historyService,
                                      ICustomFormatCalculationService formatCalculator,
                                      IEventAggregator eventAggregator,
                                      IDownloadHistoryService downloadHistoryService,
                                      ITrackedDownloadAlreadyImported trackedDownloadAlreadyImported,
                                      IRemoteEpisodeAggregationService aggregationService,
                                      IConfigService configService,
                                      Logger logger)
        {
            _parsingService = parsingService;
            _historyService = historyService;
            _formatCalculator = formatCalculator;
            _eventAggregator = eventAggregator;
            _downloadHistoryService = downloadHistoryService;
            _trackedDownloadAlreadyImported = trackedDownloadAlreadyImported;
            _aggregationService = aggregationService;
            _configService = configService;
            _cache = cacheManager.GetCache<TrackedDownload>(GetType());
            _logger = logger;
        }

        public TrackedDownload Find(string downloadId)
        {
            return _cache.Find(downloadId);
        }

        public void StopTracking(string downloadId)
        {
            var trackedDownload = _cache.Find(downloadId);

            _cache.Remove(downloadId);
            _eventAggregator.PublishEvent(new TrackedDownloadsRemovedEvent(new List<TrackedDownload> { trackedDownload }));
        }

        public void StopTracking(List<string> downloadIds)
        {
            var trackedDownloads = new List<TrackedDownload>();

            foreach (var downloadId in downloadIds)
            {
                var trackedDownload = _cache.Find(downloadId);
                _cache.Remove(downloadId);
                trackedDownloads.Add(trackedDownload);
            }

            _eventAggregator.PublishEvent(new TrackedDownloadsRemovedEvent(trackedDownloads));
        }

        public TrackedDownload TrackDownload(DownloadClientDefinition downloadClient, DownloadClientItem downloadItem)
        {
            var existingItem = Find(downloadItem.DownloadId);

            if (existingItem != null &&
                existingItem.State != TrackedDownloadState.Downloading &&
                existingItem.State != TrackedDownloadState.Imported)
            {
                LogItemChange(existingItem, existingItem.DownloadItem, downloadItem);

                existingItem.DownloadItem = downloadItem;
                existingItem.IsTrackable = true;

                UpdateCachedCompletedDownloadState(existingItem);

                return existingItem;
            }

            var trackedDownload = new TrackedDownload
            {
                DownloadClient = downloadClient.Id,
                DownloadItem = downloadItem,
                Protocol = downloadClient.Protocol,
                IsTrackable = true,
                HasNotifiedManualInteractionRequired = existingItem?.HasNotifiedManualInteractionRequired ?? false
            };

            try
            {
                var historyItems = _historyService.FindByDownloadId(downloadItem.DownloadId)
                    .OrderByDescending(h => h.Date)
                    .ToList();

                var downloadHistory = _downloadHistoryService.GetLatestDownloadHistoryItem(downloadItem.DownloadId);

                var parsedEpisodeInfo = Parser.Parser.ParseTitle(trackedDownload.DownloadItem.Title, _configService.ParseTvdbIdFromReleaseName, _configService.ParseEpisodeNumberOnlyAsSeasonOne);

                if (parsedEpisodeInfo != null)
                {
                    trackedDownload.RemoteEpisode = _parsingService.Map(parsedEpisodeInfo, 0, 0, null);
                }

                if (historyItems.Any())
                {
                    var firstHistoryItem = historyItems.First();
                    var grabbedEvent = historyItems.FirstOrDefault(v => v.EventType == EpisodeHistoryEventType.Grabbed);

                    trackedDownload.Indexer = grabbedEvent?.Data?.GetValueOrDefault("indexer");
                    trackedDownload.Added = grabbedEvent?.Date;

                    if (parsedEpisodeInfo == null ||
                        trackedDownload.RemoteEpisode?.Series == null ||
                        trackedDownload.RemoteEpisode.Episodes.Empty())
                    {
                        // Try parsing the original source title and if that fails, try parsing it as a special
                        // TODO: Pass the TVDB ID and TVRage IDs in as well so we have a better chance for finding the item
                        parsedEpisodeInfo = Parser.Parser.ParseTitle(firstHistoryItem.SourceTitle, _configService.ParseTvdbIdFromReleaseName, _configService.ParseEpisodeNumberOnlyAsSeasonOne) ??
                                            _parsingService.ParseSpecialEpisodeTitle(parsedEpisodeInfo, firstHistoryItem.SourceTitle, 0, 0, null);

                        if (parsedEpisodeInfo != null)
                        {
                            trackedDownload.RemoteEpisode = _parsingService.Map(parsedEpisodeInfo,
                                firstHistoryItem.SeriesId,
                                historyItems.Where(v => v.EventType == EpisodeHistoryEventType.Grabbed)
                                    .Select(h => h.EpisodeId).Distinct());
                        }
                    }

                    if (trackedDownload.RemoteEpisode != null)
                    {
                        trackedDownload.RemoteEpisode.Release ??= new ReleaseInfo();
                        trackedDownload.RemoteEpisode.Release.Indexer = trackedDownload.Indexer;
                        trackedDownload.RemoteEpisode.Release.Title = trackedDownload.RemoteEpisode.ParsedEpisodeInfo?.ReleaseTitle;

                        if (Enum.TryParse(grabbedEvent?.Data?.GetValueOrDefault("indexerFlags"), true, out IndexerFlags flags))
                        {
                            trackedDownload.RemoteEpisode.Release.IndexerFlags = flags;
                        }

                        if (downloadHistory != null)
                        {
                            trackedDownload.RemoteEpisode.Release.IndexerId = downloadHistory.IndexerId;
                        }
                    }
                }

                if (trackedDownload.RemoteEpisode != null)
                {
                    _aggregationService.Augment(trackedDownload.RemoteEpisode);

                    // Calculate custom formats
                    trackedDownload.RemoteEpisode.CustomFormats = _formatCalculator.ParseCustomFormat(trackedDownload.RemoteEpisode, downloadItem.TotalSize);
                }

                if (downloadHistory != null)
                {
                    trackedDownload.State = GetStateFromHistory(downloadHistory.EventType, trackedDownload, historyItems);
                }

                // Track it so it can be displayed in the queue even though we can't determine which series it is for
                if (trackedDownload.RemoteEpisode == null)
                {
                    _logger.Trace("No Episode found for download '{0}'", trackedDownload.DownloadItem.Title);
                }
            }
            catch (MultipleSeriesFoundException e)
            {
                _logger.Debug(e, "Found multiple series for " + downloadItem.Title);

                trackedDownload.Warn("Unable to import automatically, found multiple series: {0}", string.Join(", ", e.Series));
            }
            catch (Exception e)
            {
                _logger.Debug(e, "Failed to find episode for " + downloadItem.Title);

                trackedDownload.Warn("Unable to parse episodes from title");
            }

            LogItemChange(trackedDownload, existingItem?.DownloadItem, trackedDownload.DownloadItem);

            _cache.Set(trackedDownload.DownloadItem.DownloadId, trackedDownload);
            return trackedDownload;
        }

        public List<TrackedDownload> GetTrackedDownloads()
        {
            return _cache.Values.ToList();
        }

        public void UpdateTrackable(List<TrackedDownload> trackedDownloads)
        {
            var untrackable = GetTrackedDownloads().ExceptBy(t => t.DownloadItem.DownloadId, trackedDownloads, t => t.DownloadItem.DownloadId, StringComparer.CurrentCulture).ToList();

            foreach (var trackedDownload in untrackable)
            {
                trackedDownload.IsTrackable = false;
            }
        }

        private void LogItemChange(TrackedDownload trackedDownload, DownloadClientItem existingItem, DownloadClientItem downloadItem)
        {
            if (existingItem == null ||
                existingItem.Status != downloadItem.Status ||
                existingItem.CanBeRemoved != downloadItem.CanBeRemoved ||
                existingItem.CanMoveFiles != downloadItem.CanMoveFiles)
            {
                _logger.Debug("Tracking '{0}:{1}': ClientState={2}{3} SonarrStage={4} Episode='{5}' OutputPath={6}.",
                    downloadItem.DownloadClientInfo.Name,
                    downloadItem.Title,
                    downloadItem.Status,
                    downloadItem.CanBeRemoved ? "" : downloadItem.CanMoveFiles ? " (busy)" : " (readonly)",
                    trackedDownload.State,
                    trackedDownload.RemoteEpisode?.ParsedEpisodeInfo,
                    downloadItem.OutputPath);
            }
        }

        private void UpdateCachedItem(TrackedDownload trackedDownload)
        {
            var parsedEpisodeInfo = Parser.Parser.ParseTitle(trackedDownload.DownloadItem.Title, _configService.ParseTvdbIdFromReleaseName, _configService.ParseEpisodeNumberOnlyAsSeasonOne);

            trackedDownload.RemoteEpisode = parsedEpisodeInfo == null ? null : _parsingService.Map(parsedEpisodeInfo, 0, 0, null);

            _aggregationService.Augment(trackedDownload.RemoteEpisode);
        }

        private void UpdateCachedCompletedDownloadState(TrackedDownload trackedDownload)
        {
            if (trackedDownload.DownloadItem.Status != DownloadItemStatus.Completed)
            {
                return;
            }

            var downloadHistory = _downloadHistoryService.GetLatestDownloadHistoryItem(trackedDownload.DownloadItem.DownloadId);

            if (downloadHistory == null)
            {
                return;
            }

            var historyItems = _historyService.FindByDownloadId(trackedDownload.DownloadItem.DownloadId)
                .OrderByDescending(h => h.Date)
                .ToList();

            var state = GetStateFromHistory(downloadHistory.EventType, trackedDownload, historyItems);

            if (!IsTerminalState(state))
            {
                return;
            }

            if (trackedDownload.State != state)
            {
                _logger.Debug("Updating cached completed download '{0}' from {1} to {2} based on download history.",
                    trackedDownload.DownloadItem.Title,
                    trackedDownload.State,
                    state);
            }

            trackedDownload.State = state;

            if (state == TrackedDownloadState.Imported)
            {
                trackedDownload.ClearStatus();
            }
        }

        private static bool IsTerminalState(TrackedDownloadState state)
        {
            return state == TrackedDownloadState.Imported ||
                   state == TrackedDownloadState.Failed ||
                   state == TrackedDownloadState.Ignored;
        }

        private TrackedDownloadState GetStateFromHistory(DownloadHistoryEventType eventType, TrackedDownload trackedDownload, List<EpisodeHistory> historyItems)
        {
            switch (eventType)
            {
                case DownloadHistoryEventType.DownloadImported:
                    return GetImportedStateFromHistory(trackedDownload, historyItems);
                case DownloadHistoryEventType.DownloadFailed:
                    return TrackedDownloadState.Failed;
                case DownloadHistoryEventType.DownloadIgnored:
                    return TrackedDownloadState.Ignored;
                default:
                    return TrackedDownloadState.Downloading;
            }
        }

        private TrackedDownloadState GetImportedStateFromHistory(TrackedDownload trackedDownload, List<EpisodeHistory> historyItems)
        {
            if (trackedDownload.Protocol != DownloadProtocol.Torrent)
            {
                return TrackedDownloadState.Imported;
            }

            if (trackedDownload.RemoteEpisode == null ||
                trackedDownload.RemoteEpisode.Episodes.Empty())
            {
                _logger.Debug("Ignoring imported download history for '{0}' because it does not map to any episodes.", trackedDownload.DownloadItem.Title);
                return TrackedDownloadState.Downloading;
            }

            if (_trackedDownloadAlreadyImported.IsImported(trackedDownload, historyItems))
            {
                return TrackedDownloadState.Imported;
            }

            _logger.Debug("Ignoring imported download history for '{0}' because the current episode files do not match this download.", trackedDownload.DownloadItem.Title);
            return TrackedDownloadState.Downloading;
        }

        public void Handle(EpisodeInfoRefreshedEvent message)
        {
            var needsToUpdate = false;

            foreach (var episode in message.Removed)
            {
                var cachedItems = _cache.Values.Where(t =>
                                            t.RemoteEpisode?.Episodes != null &&
                                            t.RemoteEpisode.Episodes.Any(e => e.Id == episode.Id))
                                        .ToList();

                if (cachedItems.Any())
                {
                    needsToUpdate = true;
                }

                cachedItems.ForEach(UpdateCachedItem);
            }

            if (needsToUpdate)
            {
                _eventAggregator.PublishEvent(new TrackedDownloadRefreshedEvent(GetTrackedDownloads()));
            }
        }

        public void Handle(SeriesAddedEvent message)
        {
            var cachedItems = _cache.Values
                .Where(t =>
                    t.RemoteEpisode?.Series == null ||
                    message.Series?.TvdbId == t.RemoteEpisode.Series.TvdbId)
                .ToList();

            if (cachedItems.Any())
            {
                cachedItems.ForEach(UpdateCachedItem);

                _eventAggregator.PublishEvent(new TrackedDownloadRefreshedEvent(GetTrackedDownloads()));
            }
        }

        public void Handle(SeriesDeletedEvent message)
        {
            var cachedItems = _cache.Values
                .Where(t =>
                    t.RemoteEpisode?.Series != null &&
                    message.Series.Any(s => s.Id == t.RemoteEpisode.Series.Id || s.TvdbId == t.RemoteEpisode.Series.TvdbId))
                .ToList();

            if (cachedItems.Any())
            {
                cachedItems.ForEach(UpdateCachedItem);

                _eventAggregator.PublishEvent(new TrackedDownloadRefreshedEvent(GetTrackedDownloads()));
            }
        }
    }
}
