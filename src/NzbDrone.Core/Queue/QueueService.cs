using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Crypto;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Queue
{
    public interface IQueueService
    {
        List<Queue> GetQueue();
        Queue Find(int id);
        void Remove(int id);
    }

    public class QueueService : IQueueService, IHandle<TrackedDownloadRefreshedEvent>
    {
        private readonly IEventAggregator _eventAggregator;
        private static List<Queue> _queue = new();

        public QueueService(IEventAggregator eventAggregator)
        {
            _eventAggregator = eventAggregator;
        }

        public List<Queue> GetQueue()
        {
            return _queue;
        }

        public Queue Find(int id)
        {
            return _queue.SingleOrDefault(q => q.Id == id);
        }

        public void Remove(int id)
        {
            _queue.Remove(Find(id));
        }

        private IEnumerable<Queue> MapQueue(TrackedDownload trackedDownload)
        {
            if (trackedDownload.RemoteEpisode?.Episodes != null && trackedDownload.RemoteEpisode.Episodes.Any())
            {
                yield return MapQueueItem(trackedDownload, trackedDownload.RemoteEpisode.Episodes);
            }
            else
            {
                yield return MapQueueItem(trackedDownload, []);
            }
        }

        private Queue MapQueueItem(TrackedDownload trackedDownload, List<Episode> episodes)
        {
            var episode = episodes.Count == 1 ? episodes[0] : null;
            var languages = GetQueueLanguages(trackedDownload, episode);
            var subtitleLanguages = GetQueueSubtitleLanguages(trackedDownload, episode);
            var quality = GetQueueQuality(trackedDownload, episode);
            var size = GetQueueSize(trackedDownload, episode);

            var queue = new Queue
            {
                Series = trackedDownload.RemoteEpisode?.Series,
                SeasonNumber = trackedDownload.RemoteEpisode?.MappedSeasonNumber,
                Episodes = episodes,
                Languages = languages,
                SubtitleLanguages = subtitleLanguages,
                Quality = quality,
                Title = FileExtensions.RemoveFileExtension(trackedDownload.DownloadItem.Title),
                Size = size,
                SizeLeft = trackedDownload.DownloadItem.RemainingSize,
                TimeLeft = trackedDownload.DownloadItem.RemainingTime,
                Status = Enum.TryParse(trackedDownload.DownloadItem.Status.ToString(), out QueueStatus outValue) ? outValue : QueueStatus.Unknown,
                TrackedDownloadStatus = trackedDownload.Status,
                TrackedDownloadState = trackedDownload.State,
                StatusMessages = trackedDownload.StatusMessages.ToList(),
                ErrorMessage = trackedDownload.DownloadItem.Message,
                RemoteEpisode = trackedDownload.RemoteEpisode,
                DownloadId = trackedDownload.DownloadItem.DownloadId,
                Protocol = trackedDownload.Protocol,
                DownloadClient = trackedDownload.DownloadItem.DownloadClientInfo.Name,
                Indexer = trackedDownload.Indexer,
                OutputPath = trackedDownload.DownloadItem.OutputPath.ToString(),
                Added = trackedDownload.Added,
                DownloadClientHasPostImportCategory = trackedDownload.DownloadItem.DownloadClientInfo.HasPostImportCategory
            };

            queue.Id = HashConverter.GetHashInt31($"trackedDownload-{trackedDownload.DownloadClient}-{trackedDownload.DownloadItem.DownloadId}");

            if (queue.TimeLeft.HasValue)
            {
                queue.EstimatedCompletionTime = DateTime.UtcNow.Add(queue.TimeLeft.Value);
            }

            return queue;
        }

        private List<Language> GetQueueLanguages(TrackedDownload trackedDownload, Episode episode)
        {
            if (episode != null &&
                trackedDownload.AnalyzedEpisodeFiles?.TryGetValue(episode.Id, out var analyzedEpisode) == true &&
                HasKnownLanguages(analyzedEpisode.Languages))
            {
                return analyzedEpisode.Languages;
            }

            if (HasKnownLanguages(trackedDownload.AnalyzedLanguages))
            {
                return trackedDownload.AnalyzedLanguages;
            }

            var languages = trackedDownload.RemoteEpisode?.Languages;
            if (HasKnownLanguages(languages))
            {
                return languages;
            }

            return new List<Language> { Language.Unknown };
        }

        private List<Language> GetQueueSubtitleLanguages(TrackedDownload trackedDownload, Episode episode)
        {
            if (episode != null &&
                trackedDownload.AnalyzedEpisodeFiles?.TryGetValue(episode.Id, out var analyzedEpisode) == true &&
                HasKnownLanguages(analyzedEpisode.SubtitleLanguages))
            {
                return analyzedEpisode.SubtitleLanguages;
            }

            if (HasKnownLanguages(trackedDownload.AnalyzedSubtitleLanguages))
            {
                return trackedDownload.AnalyzedSubtitleLanguages;
            }

            return new List<Language>();
        }

        private QualityModel GetQueueQuality(TrackedDownload trackedDownload, Episode episode)
        {
            if (episode != null &&
                trackedDownload.AnalyzedEpisodeFiles?.TryGetValue(episode.Id, out var analyzedEpisode) == true &&
                analyzedEpisode.Quality != null &&
                analyzedEpisode.Quality.Quality != Quality.Unknown)
            {
                return analyzedEpisode.Quality;
            }

            if (trackedDownload.AnalyzedQuality != null &&
                trackedDownload.AnalyzedQuality.Quality != Quality.Unknown)
            {
                return trackedDownload.AnalyzedQuality;
            }

            var quality = trackedDownload.RemoteEpisode?.ParsedEpisodeInfo?.Quality;
            if (quality != null && quality.Quality != Quality.Unknown)
            {
                return quality;
            }

            return new QualityModel(Quality.Unknown);
        }

        private decimal GetQueueSize(TrackedDownload trackedDownload, Episode episode)
        {
            if (episode != null &&
                trackedDownload.AnalyzedEpisodeFiles?.TryGetValue(episode.Id, out var analyzedEpisode) == true &&
                analyzedEpisode.Size > 0)
            {
                return analyzedEpisode.Size;
            }

            if (episode == null && trackedDownload.AnalyzedEpisodeFiles != null)
            {
                var analyzedFiles = trackedDownload.AnalyzedEpisodeFiles.Values
                    .Where(file => file.Size > 0 && !string.IsNullOrWhiteSpace(file.Path))
                    .GroupBy(file => file.Path, StringComparer.Ordinal)
                    .Select(files => files.First())
                    .ToList();
                if (analyzedFiles.Any())
                {
                    return analyzedFiles.Sum(file => (decimal)file.Size);
                }
            }

            return trackedDownload.DownloadItem.TotalSize;
        }

        private bool HasKnownLanguages(List<Language> languages)
        {
            return languages?.Any() == true && languages.Any(l => l != Language.Unknown);
        }

        public void Handle(TrackedDownloadRefreshedEvent message)
        {
            _queue = message.TrackedDownloads
                .Where(t => t.IsTrackable)
                .OrderBy(c => c.DownloadItem.RemainingTime)
                .SelectMany(MapQueue)
                .ToList();

            _eventAggregator.PublishEvent(new QueueUpdatedEvent());
        }
    }
}
