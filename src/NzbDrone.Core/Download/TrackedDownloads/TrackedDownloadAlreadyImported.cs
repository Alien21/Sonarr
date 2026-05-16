using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Download.TrackedDownloads
{
    public interface ITrackedDownloadAlreadyImported
    {
        bool IsImported(TrackedDownload trackedDownload, List<EpisodeHistory> historyItems);
    }

    public class TrackedDownloadAlreadyImported : ITrackedDownloadAlreadyImported
    {
        private readonly IMediaFileService _mediaFileService;
        private readonly Logger _logger;

        public TrackedDownloadAlreadyImported(IMediaFileService mediaFileService, Logger logger)
        {
            _mediaFileService = mediaFileService;
            _logger = logger;
        }

        public bool IsImported(TrackedDownload trackedDownload, List<EpisodeHistory> historyItems)
        {
            _logger.Trace("Checking if all episodes for '{0}' have been imported", trackedDownload.DownloadItem.Title);

            if (historyItems.Empty())
            {
                _logger.Trace("No history for {0}", trackedDownload.DownloadItem.Title);
                return false;
            }

            var allEpisodesImportedInHistory = trackedDownload.RemoteEpisode.Episodes.All(e =>
            {
                var lastHistoryItem = historyItems.FirstOrDefault(h => h.EpisodeId == e.Id);

                if (lastHistoryItem == null)
                {
                    _logger.Trace("No history for episode: S{0:00}E{1:00} [{2}]", e.SeasonNumber, e.EpisodeNumber, e.Id);
                    return false;
                }

                _logger.Trace("Last event for episode: S{0:00}E{1:00} [{2}] is: {3}", e.SeasonNumber, e.EpisodeNumber, e.Id, lastHistoryItem.EventType);

                return lastHistoryItem.EventType == EpisodeHistoryEventType.DownloadFolderImported &&
                       ExistingFileMatchesImportHistory(e, lastHistoryItem);
            });

            _logger.Trace("All episodes for '{0}' have been imported: {1}", trackedDownload.DownloadItem.Title, allEpisodesImportedInHistory);

            return allEpisodesImportedInHistory;
        }

        private bool ExistingFileMatchesImportHistory(Episode episode, EpisodeHistory historyItem)
        {
            var importedSize = GetImportedSize(historyItem);

            if (importedSize <= 0)
            {
                _logger.Trace("No imported file size recorded for episode: S{0:00}E{1:00} [{2}]", episode.SeasonNumber, episode.EpisodeNumber, episode.Id);
                return false;
            }

            var episodeFileId = GetImportedFileId(historyItem) ?? episode.EpisodeFileId;

            if (episodeFileId <= 0)
            {
                _logger.Trace("No current episode file for episode: S{0:00}E{1:00} [{2}]", episode.SeasonNumber, episode.EpisodeNumber, episode.Id);
                return false;
            }

            var episodeFile = _mediaFileService.GetFiles(new[] { episodeFileId }).FirstOrDefault();

            if (episodeFile == null)
            {
                _logger.Trace("Current episode file {0} was not found for episode: S{1:00}E{2:00} [{3}]", episodeFileId, episode.SeasonNumber, episode.EpisodeNumber, episode.Id);
                return false;
            }

            if (episodeFile.Size != importedSize)
            {
                _logger.Trace("Current episode file size {0} does not match imported size {1} for episode: S{2:00}E{3:00} [{4}]", episodeFile.Size, importedSize, episode.SeasonNumber, episode.EpisodeNumber, episode.Id);
                return false;
            }

            return true;
        }

        private static int? GetImportedFileId(EpisodeHistory historyItem)
        {
            if (historyItem.Data == null ||
                !historyItem.Data.TryGetValue("FileId", out var fileIdText) ||
                !int.TryParse(fileIdText, out var fileId))
            {
                return null;
            }

            return fileId;
        }

        private static long GetImportedSize(EpisodeHistory historyItem)
        {
            if (historyItem.Data == null ||
                !historyItem.Data.TryGetValue("Size", out var sizeText) ||
                !long.TryParse(sizeText, out var size))
            {
                return 0;
            }

            return size;
        }
    }
}
