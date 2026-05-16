using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Extras.Subtitles;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles.EpisodeImport.Aggregation;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.MediaFiles.EpisodeImport.Manual
{
    public interface IManualImportService
    {
        List<ManualImportItem> GetMediaFiles(int seriesId, int? seasonNumber);
        List<ManualImportItem> GetMediaFiles(string path, string downloadId, int? seriesId, bool filterExistingFiles);
        ManualImportItem ReprocessItem(string path, string downloadId, int seriesId, int? seasonNumber, List<int> episodeIds, string releaseGroup, QualityModel quality, bool qualityManuallySelected, List<Language> languages, int indexerFlags, ReleaseType releaseType);
        void UpdateTrackedDownloadsForQueuedManualImport(ManualImportCommand message);
    }

    public class ManualImportService : IExecute<ManualImportCommand>, IManualImportService
    {
        private readonly IDiskProvider _diskProvider;
        private readonly IParsingService _parsingService;
        private readonly IDiskScanService _diskScanService;
        private readonly IMakeImportDecision _importDecisionMaker;
        private readonly ISeriesService _seriesService;
        private readonly IEpisodeService _episodeService;
        private readonly IImportApprovedEpisodes _importApprovedEpisodes;
        private readonly IAggregationService _aggregationService;
        private readonly ITrackedDownloadService _trackedDownloadService;
        private readonly IDownloadedEpisodesImportService _downloadedEpisodesImportService;
        private readonly IMediaFileService _mediaFileService;
        private readonly ICustomFormatCalculationService _formatCalculator;
        private readonly IEventAggregator _eventAggregator;
        private readonly IConfigService _configService;
        private readonly Logger _logger;

        public ManualImportService(IDiskProvider diskProvider,
                                   IParsingService parsingService,
                                   IDiskScanService diskScanService,
                                   IMakeImportDecision importDecisionMaker,
                                   ISeriesService seriesService,
                                   IEpisodeService episodeService,
                                   IAggregationService aggregationService,
                                   IImportApprovedEpisodes importApprovedEpisodes,
                                   ITrackedDownloadService trackedDownloadService,
                                   IDownloadedEpisodesImportService downloadedEpisodesImportService,
                                   IMediaFileService mediaFileService,
                                   ICustomFormatCalculationService formatCalculator,
                                   IEventAggregator eventAggregator,
                                   IConfigService configService,
                                   Logger logger)
        {
            _diskProvider = diskProvider;
            _parsingService = parsingService;
            _diskScanService = diskScanService;
            _importDecisionMaker = importDecisionMaker;
            _seriesService = seriesService;
            _episodeService = episodeService;
            _aggregationService = aggregationService;
            _importApprovedEpisodes = importApprovedEpisodes;
            _trackedDownloadService = trackedDownloadService;
            _downloadedEpisodesImportService = downloadedEpisodesImportService;
            _mediaFileService = mediaFileService;
            _formatCalculator = formatCalculator;
            _eventAggregator = eventAggregator;
            _configService = configService;
            _logger = logger;
        }

        public List<ManualImportItem> GetMediaFiles(int seriesId, int? seasonNumber)
        {
            var series = _seriesService.GetSeries(seriesId);
            var directoryInfo = new DirectoryInfo(series.Path);
            var seriesFiles = seasonNumber.HasValue ? _mediaFileService.GetFilesBySeason(seriesId, seasonNumber.Value) : _mediaFileService.GetFilesBySeries(seriesId);
            var episodes = _episodeService.GetEpisodeBySeries(series.Id);

            var items = seriesFiles.Select(episodeFile => MapItem(episodeFile, series, directoryInfo.Name, episodes)).ToList();

            if (!seasonNumber.HasValue)
            {
                var mediaFiles = _diskScanService.FilterPaths(series.Path, _diskScanService.GetVideoFiles(series.Path)).ToList();
                var unmappedFiles = MediaFileService.FilterExistingFiles(mediaFiles, seriesFiles, series);

                items.AddRange(unmappedFiles.Select(file =>
                    new ManualImportItem
                    {
                        Path = Path.Combine(series.Path, file),
                        FolderName = directoryInfo.Name,
                        RelativePath = series.Path.GetRelativePath(file),
                        Name = Path.GetFileNameWithoutExtension(file),
                        Series = series,
                        SeasonNumber = null,
                        Episodes = new List<Episode>(),
                        ReleaseGroup = string.Empty,
                        Quality = new QualityModel(Quality.Unknown),
                        Languages = new List<Language> { Language.Unknown },
                        Size = _diskProvider.GetFileSize(file),
                        Rejections = Enumerable.Empty<ImportRejection>()
                    }));
            }

            return items;
        }

        public List<ManualImportItem> GetMediaFiles(string path, string downloadId, int? seriesId, bool filterExistingFiles)
        {
            if (downloadId.IsNotNullOrWhiteSpace())
            {
                var trackedDownload = _trackedDownloadService.Find(downloadId);

                if (trackedDownload == null)
                {
                    return new List<ManualImportItem>();
                }

                path = trackedDownload.ImportItem.OutputPath.FullPath;
            }

            if (!_diskProvider.FolderExists(path))
            {
                if (!_diskProvider.FileExists(path))
                {
                    return new List<ManualImportItem>();
                }

                var rootFolder = Path.GetDirectoryName(path);
                return new List<ManualImportItem> { ProcessFile(rootFolder, rootFolder, path, downloadId) };
            }

            return ProcessFolder(path, path, downloadId, seriesId, filterExistingFiles);
        }

        public ManualImportItem ReprocessItem(string path, string downloadId, int seriesId, int? seasonNumber, List<int> episodeIds, string releaseGroup, QualityModel quality, bool qualityManuallySelected, List<Language> languages, int indexerFlags, ReleaseType releaseType)
        {
            var rootFolder = Path.GetDirectoryName(path);
            var series = _seriesService.GetSeries(seriesId);

            var languageParse = LanguageParser.ParseLanguages(path);
            var selectedLanguages = HasKnownLanguages(languages) ? languages : null;

            if (languageParse.Count <= 1 && languageParse.First() == Language.Unknown && series != null)
            {
                languageParse = new List<Language> { series.OriginalLanguage };
                _logger.Debug("Language couldn't be parsed from release, falling back to series original language: {0}", series.OriginalLanguage.Name);
            }

            if (episodeIds.Any())
            {
                var trackedDownload = GetTrackedDownload(downloadId);
                var downloadClientItem = trackedDownload?.DownloadItem;
                var episodes = _episodeService.GetEpisodes(episodeIds);
                var finalReleaseGroup = releaseGroup.IsNullOrWhiteSpace()
                    ? Parser.Parser.ParseReleaseGroup(path)
                    : releaseGroup;
                var initialQuality = qualityManuallySelected && quality != null && quality.Quality != Quality.Unknown
                    ? quality
                    : QualityParser.ParseQuality(path);
                var initialLanguages = selectedLanguages ?? languageParse;

                var localEpisode = new LocalEpisode();
                localEpisode.Series = series;
                localEpisode.Episodes = episodes;
                localEpisode.FileEpisodeInfo = Parser.Parser.ParsePath(path, _configService.ParseTvdbIdFromReleaseName, _configService.ParseEpisodeNumberOnlyAsSeasonOne);
                localEpisode.DownloadClientEpisodeInfo = downloadClientItem == null ? null : Parser.Parser.ParseTitle(downloadClientItem.Title, _configService.ParseTvdbIdFromReleaseName, _configService.ParseEpisodeNumberOnlyAsSeasonOne);
                localEpisode.DownloadItem = downloadClientItem;
                localEpisode.Path = path;
                localEpisode.SceneSource = SceneSource(series, rootFolder);
                localEpisode.ExistingFile = series.Path.IsParentPath(path);
                localEpisode.Size = _diskProvider.GetFileSize(path);
                localEpisode.ReleaseGroup = finalReleaseGroup;
                localEpisode.Languages = initialLanguages;
                localEpisode.Quality = initialQuality;
                localEpisode.IndexerFlags = (IndexerFlags)indexerFlags;
                localEpisode.ReleaseType = releaseType;

                localEpisode.CustomFormats = _formatCalculator.ParseCustomFormat(localEpisode);
                localEpisode.CustomFormatScore = localEpisode.Series?.QualityProfile?.Value.CalculateCustomFormatScore(localEpisode.CustomFormats) ?? 0;

                ApplyTrackedDownloadAnalysis(localEpisode, trackedDownload);

                if (!HasKnownQuality(localEpisode.Quality) ||
                    !HasKnownLanguages(localEpisode.Languages) ||
                    !HasKnownLanguages(GetTrackedDownloadSubtitleLanguages(trackedDownload, localEpisode)))
                {
                    // Fall back to reading the file only when queue analysis did not already provide the metadata.
                    localEpisode = _aggregationService.Augment(localEpisode, downloadClientItem);
                    ApplyTrackedDownloadAnalysis(localEpisode, trackedDownload);
                }

                // Reapply the user-chosen values.
                localEpisode.Series = series;
                localEpisode.Episodes = episodes;
                localEpisode.ReleaseGroup = finalReleaseGroup;
                localEpisode.IndexerFlags = (IndexerFlags)indexerFlags;
                localEpisode.ReleaseType = releaseType;

                if (qualityManuallySelected && quality != null && quality.Quality != Quality.Unknown)
                {
                    localEpisode.Quality = quality;
                }

                if (selectedLanguages != null)
                {
                    localEpisode.Languages = selectedLanguages;
                }

                localEpisode.CustomFormats = _formatCalculator.ParseCustomFormat(localEpisode);
                localEpisode.CustomFormatScore = localEpisode.Series?.QualityProfile?.Value.CalculateCustomFormatScore(localEpisode.CustomFormats) ?? 0;

                return MapItem(_importDecisionMaker.GetDecision(localEpisode, downloadClientItem), rootFolder, downloadId, null, GetTrackedDownloadSubtitleLanguages(trackedDownload, localEpisode));
            }

            // This case will happen if the user selected a season, but didn't select the episodes in the season then changed the language or quality.
            // Instead of overriding their season selection let it persist and reject it with an appropriate error.

            if (seasonNumber.HasValue)
            {
                var trackedDownload = GetTrackedDownload(downloadId);
                var downloadClientItem = trackedDownload?.DownloadItem;
                var initialQuality = qualityManuallySelected && quality != null && quality.Quality != Quality.Unknown
                    ? quality
                    : QualityParser.ParseQuality(path);
                var initialLanguages = selectedLanguages ?? LanguageParser.ParseLanguages(path);

                var localEpisode = new LocalEpisode
                {
                    Series = series,
                    Episodes = new List<Episode>(),
                    FileEpisodeInfo = Parser.Parser.ParsePath(path, _configService.ParseTvdbIdFromReleaseName, _configService.ParseEpisodeNumberOnlyAsSeasonOne),
                    DownloadClientEpisodeInfo = downloadClientItem == null
                        ? null
                        : Parser.Parser.ParseTitle(downloadClientItem.Title, _configService.ParseTvdbIdFromReleaseName, _configService.ParseEpisodeNumberOnlyAsSeasonOne),
                    DownloadItem = downloadClientItem,
                    Path = path,
                    SceneSource = SceneSource(series, rootFolder),
                    ExistingFile = series.Path.IsParentPath(path),
                    Size = _diskProvider.GetFileSize(path),
                    ReleaseGroup = releaseGroup.IsNullOrWhiteSpace() ? Parser.Parser.ParseReleaseGroup(path) : releaseGroup,
                    Languages = initialLanguages,
                    Quality = initialQuality,
                    IndexerFlags = (IndexerFlags)indexerFlags,
                    ReleaseType = releaseType
                };

                ApplyTrackedDownloadAnalysis(localEpisode, trackedDownload);

                if (qualityManuallySelected && quality != null && quality.Quality != Quality.Unknown)
                {
                    localEpisode.Quality = quality;
                }

                if (selectedLanguages != null)
                {
                    localEpisode.Languages = selectedLanguages;
                }

                return MapItem(
                    new ImportDecision(localEpisode, new ImportRejection(ImportRejectionReason.NoEpisodes, "Episodes not selected")),
                    rootFolder,
                    downloadId,
                    null,
                    GetTrackedDownloadSubtitleLanguages(trackedDownload, localEpisode));
            }

            return ProcessFile(rootFolder, rootFolder, path, downloadId, series);
        }

        private List<ManualImportItem> ProcessFolder(string rootFolder, string baseFolder, string downloadId, int? seriesId, bool filterExistingFiles)
        {
            DownloadClientItem downloadClientItem = null;
            Series series = null;

            var directoryInfo = new DirectoryInfo(baseFolder);

            if (seriesId.HasValue)
            {
                series = _seriesService.GetSeries(seriesId.Value);
            }
            else
            {
                try
                {
                    series = _parsingService.GetSeries(directoryInfo.Name);
                }
                catch (MultipleSeriesFoundException e)
                {
                    _logger.Warn(e, "Unable to find series from title");
                }
            }

            if (downloadId.IsNotNullOrWhiteSpace())
            {
                var trackedDownload = _trackedDownloadService.Find(downloadId);
                downloadClientItem = trackedDownload.DownloadItem;

                if (series == null)
                {
                    series = trackedDownload.RemoteEpisode?.Series;
                }
            }

            if (series == null)
            {
                // Filter paths based on the rootFolder, so files in subfolders that should be ignored are ignored.
                // It will lead to some extra directories being checked for files, but it saves the processing of them and is cleaner than
                // teaching FilterPaths to know whether it's processing a file or a folder and changing it's filtering based on that.

                // If the series is unknown for the directory and there are more than 100 files in the folder don't process the items before returning.
                var files = _diskScanService.FilterPaths(rootFolder, _diskScanService.GetVideoFiles(baseFolder, false));

                if (files.Count > 100)
                {
                    _logger.Warn("Unable to determine series from folder name and found more than 100 files. Skipping parsing");
                    return ProcessDownloadDirectory(rootFolder, files);
                }

                var subfolders = _diskScanService.FilterPaths(rootFolder, _diskProvider.GetDirectories(baseFolder));

                var processedFiles = files.Select(file => ProcessFile(rootFolder, baseFolder, file, downloadId));
                var processedFolders = subfolders.SelectMany(subfolder => ProcessFolder(rootFolder, subfolder, downloadId, null, filterExistingFiles));

                return processedFiles.Concat(processedFolders).Where(i => i != null).ToList();
            }

            var folderInfo = Parser.Parser.ParseTitle(directoryInfo.Name, _configService.ParseTvdbIdFromReleaseName, _configService.ParseEpisodeNumberOnlyAsSeasonOne);
            var seriesFiles = _diskScanService.FilterPaths(rootFolder, _diskScanService.GetVideoFiles(baseFolder).ToList());
            var decisions = _importDecisionMaker.GetImportDecisions(seriesFiles, series, downloadClientItem, folderInfo, SceneSource(series, baseFolder), filterExistingFiles);

            return decisions.Select(decision => MapItem(decision, rootFolder, downloadId, directoryInfo.Name)).ToList();
        }

        private ManualImportItem ProcessFile(string rootFolder, string baseFolder, string file, string downloadId, Series series = null)
        {
            try
            {
                var trackedDownload = GetTrackedDownload(downloadId);
                var relativeFile = baseFolder.GetRelativePath(file);

                if (series == null)
                {
                    _parsingService.GetSeries(relativeFile.Split('\\', '/')[0]);
                }

                if (series == null)
                {
                    series = _parsingService.GetSeries(relativeFile);
                }

                if (trackedDownload != null && series == null)
                {
                    series = trackedDownload?.RemoteEpisode?.Series;
                }

                if (series == null)
                {
                    var relativeParseInfo = Parser.Parser.ParsePath(relativeFile, _configService.ParseTvdbIdFromReleaseName, _configService.ParseEpisodeNumberOnlyAsSeasonOne);

                    if (relativeParseInfo != null)
                    {
                        series = _seriesService.FindByTitle(relativeParseInfo.SeriesTitle);
                    }
                }

                if (series == null)
                {
                    var downloadClientItem = trackedDownload?.ImportItem ?? trackedDownload?.DownloadItem;
                    var directoryInfo = new DirectoryInfo(baseFolder);
                    var fileEpisodeInfo = Parser.Parser.ParsePath(file, _configService.ParseTvdbIdFromReleaseName, _configService.ParseEpisodeNumberOnlyAsSeasonOne);

                    var localEpisode = new LocalEpisode();
                    localEpisode.Path = file;
                    localEpisode.DownloadClientEpisodeInfo = trackedDownload?.RemoteEpisode?.ParsedEpisodeInfo ??
                                                              (downloadClientItem == null ? null : Parser.Parser.ParseTitle(downloadClientItem.Title, _configService.ParseTvdbIdFromReleaseName, _configService.ParseEpisodeNumberOnlyAsSeasonOne));
                    localEpisode.DownloadItem = downloadClientItem;
                    localEpisode.FolderEpisodeInfo = Parser.Parser.ParseTitle(directoryInfo.Name, _configService.ParseTvdbIdFromReleaseName, _configService.ParseEpisodeNumberOnlyAsSeasonOne);
                    localEpisode.FileEpisodeInfo = fileEpisodeInfo ?? GetFallbackFileEpisodeInfo(file);
                    localEpisode.ExistingFile = false;
                    localEpisode.SceneSource = true;
                    localEpisode.ReleaseGroup = Parser.Parser.ParseReleaseGroup(file);
                    localEpisode.Quality = QualityParser.ParseQuality(file);
                    localEpisode.Languages = LanguageParser.ParseLanguages(file);
                    localEpisode.Size = _diskProvider.GetFileSize(file);

                    ApplyTrackedDownloadAnalysis(localEpisode, trackedDownload);

                    if (!HasKnownQuality(localEpisode.Quality) ||
                        !HasKnownLanguages(localEpisode.Languages) ||
                        !HasKnownLanguages(GetTrackedDownloadSubtitleLanguages(trackedDownload, localEpisode)))
                    {
                        localEpisode = _aggregationService.Augment(localEpisode, downloadClientItem);
                        ApplyTrackedDownloadAnalysis(localEpisode, trackedDownload);
                    }

                    return MapItem(new ImportDecision(localEpisode,
                        new ImportRejection(ImportRejectionReason.UnknownSeries, "Unknown Series")),
                        rootFolder,
                        downloadId,
                        null,
                        GetTrackedDownloadSubtitleLanguages(trackedDownload, localEpisode));
                }

                var importDecisions = _importDecisionMaker.GetImportDecisions(new List<string> { file },
                    series,
                    trackedDownload?.DownloadItem,
                    null,
                    SceneSource(series, baseFolder));

                if (importDecisions.Any())
                {
                    return MapItem(importDecisions.First(), rootFolder, downloadId, null);
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to process file: {0}", file);
            }

            return new ManualImportItem
            {
                DownloadId = downloadId,
                Path = file,
                RelativePath = rootFolder.GetRelativePath(file),
                Name = Path.GetFileNameWithoutExtension(file),
                Size = _diskProvider.GetFileSize(file),
                Rejections = new List<ImportRejection>()
            };
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

        private List<ManualImportItem> ProcessDownloadDirectory(string rootFolder, List<string> videoFiles)
        {
            var items = new List<ManualImportItem>();

            foreach (var file in videoFiles)
            {
                var localEpisode = new LocalEpisode();
                localEpisode.Path = file;
                localEpisode.Quality = new QualityModel(Quality.Unknown);
                localEpisode.Languages = new List<Language> { Language.Unknown };
                localEpisode.Size = _diskProvider.GetFileSize(file);

                items.Add(MapItem(new ImportDecision(localEpisode), rootFolder, null, null));
            }

            return items;
        }

        private bool SceneSource(Series series, string folder)
        {
            return !(series.Path.PathEquals(folder) || series.Path.IsParentPath(folder));
        }

        private TrackedDownload GetTrackedDownload(string downloadId)
        {
            if (downloadId.IsNotNullOrWhiteSpace())
            {
                var trackedDownload = _trackedDownloadService.Find(downloadId);

                return trackedDownload;
            }

            return null;
        }

        private ManualImportItem MapItem(ImportDecision decision, string rootFolder, string downloadId, string folderName, List<Language> subtitleLanguages = null)
        {
            var item = new ManualImportItem();

            item.Path = decision.LocalEpisode.Path;
            item.FolderName = folderName;
            item.RelativePath = rootFolder.GetRelativePath(decision.LocalEpisode.Path);
            item.Name = Path.GetFileNameWithoutExtension(decision.LocalEpisode.Path);
            item.DownloadId = downloadId;

            if (decision.LocalEpisode.Episodes.Any() && decision.LocalEpisode.Episodes.Select(c => c.SeasonNumber).Distinct().Count() == 1)
            {
                var seasons = decision.LocalEpisode.Episodes.Select(c => c.SeasonNumber).Distinct().ToList();

                if (seasons.Empty())
                {
                    _logger.Warn("Expected one season, but found none for: {0}", decision.LocalEpisode.Path);
                }
                else if (seasons.Count > 1)
                {
                    _logger.Warn("Expected one season, but found {0} ({1}) for: {2}", seasons.Count, string.Join(", ", seasons), decision.LocalEpisode.Path);
                }
                else
                {
                    item.SeasonNumber = decision.LocalEpisode.SeasonNumber;
                    item.Episodes = decision.LocalEpisode.Episodes;
                }
            }

            item.ReleaseGroup = decision.LocalEpisode.ReleaseGroup;
            item.Quality = decision.LocalEpisode.Quality;
            item.Languages = PrioritizeLanguages(decision.LocalEpisode.Languages);
            item.SubtitleLanguages = subtitleLanguages ?? GetSubtitleLanguages(decision.LocalEpisode);
            item.Size = _diskProvider.GetFileSize(decision.LocalEpisode.Path);
            item.Rejections = decision.Rejections;
            item.IndexerFlags = (int)decision.LocalEpisode.IndexerFlags;
            item.ReleaseType = decision.LocalEpisode.ReleaseType;
            item.ExistingEpisodeFiles = GetExistingEpisodeFiles(decision.LocalEpisode);

            if (decision.LocalEpisode.Series != null)
            {
                item.Series = decision.LocalEpisode.Series;

                item.CustomFormats = _formatCalculator.ParseCustomFormat(decision.LocalEpisode);
                item.CustomFormatScore = item.Series.QualityProfile?.Value.CalculateCustomFormatScore(item.CustomFormats) ?? 0;
            }

            return item;
        }

        private List<ManualImportExistingEpisodeFile> GetExistingEpisodeFiles(LocalEpisode localEpisode)
        {
            if (localEpisode?.Series == null || localEpisode.Episodes == null)
            {
                return new List<ManualImportExistingEpisodeFile>();
            }

            return GetEpisodesWithExistingFiles(localEpisode)
                .Where(episode => episode.EpisodeFileId > 0)
                .Select(episode => episode.EpisodeFile?.Value)
                .Where(episodeFile => episodeFile != null)
                .GroupBy(episodeFile => episodeFile.Id)
                .Select(group => MapExistingEpisodeFile(group.First(), localEpisode.Series))
                .ToList();
        }

        private List<Episode> GetEpisodesWithExistingFiles(LocalEpisode localEpisode)
        {
            var episodeIds = localEpisode.Episodes
                .Where(episode => episode.Id > 0)
                .Select(episode => episode.Id)
                .Distinct()
                .ToList();

            if (episodeIds.Any())
            {
                return _episodeService.GetEpisodes(episodeIds);
            }

            return localEpisode.Episodes
                .Select(episode => _episodeService.FindEpisode(localEpisode.Series.Id, episode.SeasonNumber, episode.EpisodeNumber))
                .Where(episode => episode != null)
                .ToList();
        }

        private ManualImportExistingEpisodeFile MapExistingEpisodeFile(EpisodeFile episodeFile, Series series)
        {
            var path = GetEpisodeFilePath(episodeFile, series);

            return new ManualImportExistingEpisodeFile
            {
                Id = episodeFile.Id,
                RelativePath = episodeFile.RelativePath,
                Size = episodeFile.Size,
                Quality = episodeFile.Quality,
                Languages = PrioritizeLanguages(episodeFile.Languages),
                SubtitleLanguages = GetSubtitleLanguages(new LocalEpisode
                {
                    Path = path,
                    FileEpisodeInfo = Parser.Parser.ParsePath(path, _configService.ParseTvdbIdFromReleaseName, _configService.ParseEpisodeNumberOnlyAsSeasonOne),
                    MediaInfo = episodeFile.MediaInfo
                })
            };
        }

        private string GetEpisodeFilePath(EpisodeFile episodeFile, Series series)
        {
            if (episodeFile.RelativePath.IsNotNullOrWhiteSpace() && series.Path.IsNotNullOrWhiteSpace())
            {
                return Path.Combine(series.Path, episodeFile.RelativePath);
            }

            return episodeFile.Path;
        }

        private ManualImportItem MapItem(EpisodeFile episodeFile, Series series, string folderName, List<Episode> episodes)
        {
            var item = new ManualImportItem();

            item.Path = Path.Combine(series.Path, episodeFile.RelativePath);
            item.FolderName = folderName;
            item.RelativePath = episodeFile.RelativePath;
            item.Name = Path.GetFileNameWithoutExtension(episodeFile.Path);
            item.Series = series;
            item.SeasonNumber = episodeFile.SeasonNumber;
            item.Episodes = episodes.Where(e => e.EpisodeFileId == episodeFile.Id).ToList();
            item.ReleaseGroup = episodeFile.ReleaseGroup;
            item.Quality = episodeFile.Quality;
            item.Languages = PrioritizeLanguages(episodeFile.Languages);
            item.SubtitleLanguages = GetSubtitleLanguages(new LocalEpisode
            {
                Path = item.Path,
                FileEpisodeInfo = Parser.Parser.ParsePath(item.Path, _configService.ParseTvdbIdFromReleaseName, _configService.ParseEpisodeNumberOnlyAsSeasonOne),
                MediaInfo = episodeFile.MediaInfo
            });
            item.IndexerFlags = (int)episodeFile.IndexerFlags;
            item.ReleaseType = episodeFile.ReleaseType;
            item.Size = _diskProvider.GetFileSize(item.Path);
            item.Rejections = Enumerable.Empty<ImportRejection>();
            item.EpisodeFileId = episodeFile.Id;
            item.CustomFormats = _formatCalculator.ParseCustomFormat(episodeFile, series);

            return item;
        }

        private List<Language> PrioritizeLanguages(List<Language> languages)
        {
            var preferredLanguage = Language.FindById(_configService.SeriesInfoLanguage);

            return languages?
                .Select((language, index) => new { Language = language, Index = index })
                .OrderBy(item => GetLanguagePriority(item.Language, preferredLanguage))
                .ThenBy(item => item.Index)
                .Select(item => item.Language)
                .ToList() ?? new List<Language>();
        }

        private bool HasKnownLanguages(List<Language> languages)
        {
            return languages?.Any() == true && languages.Any(language => language != Language.Unknown);
        }

        private bool HasKnownQuality(QualityModel quality)
        {
            return quality != null && quality.Quality != Quality.Unknown;
        }

        private void ApplyTrackedDownloadAnalysis(LocalEpisode localEpisode, TrackedDownload trackedDownload)
        {
            if (localEpisode == null || trackedDownload == null)
            {
                return;
            }

            var analyzedFile = GetTrackedDownloadAnalyzedFile(trackedDownload, localEpisode);
            var quality = analyzedFile?.Quality ?? trackedDownload.AnalyzedQuality;

            if (HasKnownQuality(quality))
            {
                localEpisode.Quality = quality;
            }

            var languages = analyzedFile?.Languages ?? trackedDownload.AnalyzedLanguages;

            if (HasKnownLanguages(languages))
            {
                localEpisode.Languages = languages;
            }
        }

        private List<Language> GetTrackedDownloadSubtitleLanguages(TrackedDownload trackedDownload, LocalEpisode localEpisode)
        {
            if (trackedDownload == null)
            {
                return null;
            }

            var analyzedFile = GetTrackedDownloadAnalyzedFile(trackedDownload, localEpisode);

            if (HasKnownLanguages(analyzedFile?.SubtitleLanguages))
            {
                return PrioritizeLanguages(analyzedFile.SubtitleLanguages);
            }

            if (HasKnownLanguages(trackedDownload.AnalyzedSubtitleLanguages))
            {
                return PrioritizeLanguages(trackedDownload.AnalyzedSubtitleLanguages);
            }

            return null;
        }

        private AnalyzedDownloadFile GetTrackedDownloadAnalyzedFile(TrackedDownload trackedDownload, LocalEpisode localEpisode)
        {
            if (trackedDownload?.AnalyzedEpisodeFiles == null || localEpisode?.Episodes == null)
            {
                return null;
            }

            foreach (var episode in localEpisode.Episodes)
            {
                if (trackedDownload.AnalyzedEpisodeFiles.TryGetValue(episode.Id, out var analyzedFile))
                {
                    return analyzedFile;
                }
            }

            return null;
        }

        private int GetLanguagePriority(Language language, Language preferredLanguage)
        {
            if (language == preferredLanguage)
            {
                return 0;
            }

            if (language == Language.English)
            {
                return 1;
            }

            return 2;
        }

        private List<Language> GetSubtitleLanguages(LocalEpisode localEpisode)
        {
            var languages = GetEmbeddedSubtitleLanguages(localEpisode.MediaInfo);

            foreach (var subtitleFile in GetExternalSubtitleFiles(localEpisode))
            {
                languages.AddIfNotNull(subtitleFile.Info?.Language);
            }

            return PrioritizeLanguages(languages
                .Where(language => language != Language.Unknown)
                .GroupBy(language => language.Id)
                .Select(group => group.First())
                .ToList());
        }

        private List<Language> GetEmbeddedSubtitleLanguages(MediaInfoModel mediaInfo)
        {
            var languages = new List<Language>();
            var embeddedSubtitleLanguages = mediaInfo?.Subtitles?
                                                     .Where(language => language.IsNotNullOrWhiteSpace())
                                                     .Distinct()
                                                     .ToList() ?? new List<string>();

            foreach (var subtitleLanguage in embeddedSubtitleLanguages)
            {
                languages.AddIfNotNull(IsoLanguages.Find(subtitleLanguage)?.Language);
            }

            return languages;
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

        private class ExternalSubtitleFile
        {
            public string Path { get; set; }
            public SubtitleTitleInfo Info { get; set; }
        }

        public void Execute(ManualImportCommand message)
        {
            _logger.ProgressTrace("Manually importing {0} files using mode {1}", message.Files.Count, message.ImportMode);

            var imported = new List<ImportResult>();
            var importedTrackedDownload = new List<ManuallyImportedFile>();
            var manuallyMatchedEpisodesByDownloadId = message.Files
                .Where(f => f.DownloadId.IsNotNullOrWhiteSpace())
                .GroupBy(f => f.DownloadId)
                .ToDictionary(g => g.Key, g => _episodeService.GetEpisodes(g.SelectMany(f => f.EpisodeIds).Distinct().ToList()));

            for (var i = 0; i < message.Files.Count; i++)
            {
                _logger.ProgressTrace("Processing file {0} of {1}", i + 1, message.Files.Count);

                var file = message.Files[i];
                var series = _seriesService.GetSeries(file.SeriesId);
                var episodes = _episodeService.GetEpisodes(file.EpisodeIds);
                var fileEpisodeInfo = Parser.Parser.ParsePath(file.Path, _configService.ParseTvdbIdFromReleaseName, _configService.ParseEpisodeNumberOnlyAsSeasonOne) ?? new ParsedEpisodeInfo();
                var existingFile = series.Path.IsParentPath(file.Path);

                TrackedDownload trackedDownload = null;
                List<Episode> manuallyMatchedEpisodes = null;

                var localEpisode = new LocalEpisode
                {
                    ExistingFile = existingFile,
                    Episodes = episodes,
                    FileEpisodeInfo = fileEpisodeInfo,
                    Path = file.Path,
                    ReleaseGroup = file.ReleaseGroup,
                    Quality = file.Quality,
                    Languages = file.Languages,
                    IndexerFlags = (IndexerFlags)file.IndexerFlags,
                    ReleaseType = file.ReleaseType,
                    Series = series,
                    Size = 0
                };

                if (file.DownloadId.IsNotNullOrWhiteSpace())
                {
                    trackedDownload = _trackedDownloadService.Find(file.DownloadId);
                    manuallyMatchedEpisodesByDownloadId.TryGetValue(file.DownloadId, out manuallyMatchedEpisodes);
                    localEpisode.DownloadClientEpisodeInfo = trackedDownload?.RemoteEpisode?.ParsedEpisodeInfo;
                    localEpisode.DownloadItem = trackedDownload?.DownloadItem;
                }

                if (file.FolderName.IsNotNullOrWhiteSpace())
                {
                    localEpisode.FolderEpisodeInfo = Parser.Parser.ParseTitle(file.FolderName, _configService.ParseTvdbIdFromReleaseName, _configService.ParseEpisodeNumberOnlyAsSeasonOne);
                    localEpisode.SceneSource = !existingFile;
                }

                // Augment episode file so imported files have all additional information an automatic import would
                localEpisode = _aggregationService.Augment(localEpisode, trackedDownload?.DownloadItem);

                // Apply the user-chosen values.
                localEpisode.Series = series;
                localEpisode.Episodes = episodes;
                localEpisode.ReleaseGroup = file.ReleaseGroup;
                localEpisode.Quality = file.Quality;
                localEpisode.Languages = file.Languages;
                localEpisode.IndexerFlags = (IndexerFlags)file.IndexerFlags;
                localEpisode.ReleaseType = file.ReleaseType;

                localEpisode.CustomFormats = _formatCalculator.ParseCustomFormat(localEpisode);
                localEpisode.CustomFormatScore = localEpisode.Series.QualityProfile?.Value.CalculateCustomFormatScore(localEpisode.CustomFormats) ?? 0;

                // TODO: Cleanup non-tracked downloads

                var importDecision = new ImportDecision(localEpisode);

                if (trackedDownload == null)
                {
                    imported.AddRange(_importApprovedEpisodes.Import(new List<ImportDecision> { importDecision }, !existingFile, null, message.ImportMode));
                }
                else
                {
                    UpdateTrackedDownloadForManualImport(trackedDownload, localEpisode, manuallyMatchedEpisodes);

                    var importResult = _importApprovedEpisodes.Import(new List<ImportDecision> { importDecision }, true, trackedDownload.DownloadItem, message.ImportMode).First();

                    imported.Add(importResult);

                    importedTrackedDownload.Add(new ManuallyImportedFile
                    {
                        TrackedDownload = trackedDownload,
                        ImportResult = importResult
                    });
                }
            }

            if (imported.Any())
            {
                _logger.ProgressTrace("Manually imported {0} files", imported.Count);
            }

            var untrackedImports = imported.Where(i => i.Result == ImportResultType.Imported && importedTrackedDownload.FirstOrDefault(t => t.ImportResult != i) == null).ToList();

            if (untrackedImports.Any())
            {
                foreach (var groupedUntrackedImport in untrackedImports.GroupBy(i => new { i.EpisodeFile.SeriesId, i.EpisodeFile.SeasonNumber }))
                {
                    var localEpisodes = groupedUntrackedImport.Select(u => u.ImportDecision.LocalEpisode).ToList();
                    var episodeFiles = groupedUntrackedImport.Select(u => u.EpisodeFile).ToList();
                    var localEpisode = localEpisodes.First();
                    var series = localEpisode.Series;
                    var sourcePath = localEpisodes.Select(l => l.Path).ToList().GetLongestCommonPath();
                    var episodes = localEpisodes.SelectMany(l => l.Episodes).ToList();
                    var parsedEpisodeInfo = localEpisode.FolderEpisodeInfo ?? localEpisode.FileEpisodeInfo;

                    _eventAggregator.PublishEvent(new UntrackedDownloadCompletedEvent(series, episodes, episodeFiles, parsedEpisodeInfo, sourcePath));
                }
            }

            foreach (var groupedTrackedDownload in importedTrackedDownload.GroupBy(i => i.TrackedDownload.DownloadItem.DownloadId).ToList())
            {
                var trackedDownload = groupedTrackedDownload.First().TrackedDownload;
                var importedSeries = imported.First().ImportDecision.LocalEpisode.Series;
                var outputPath = trackedDownload.ImportItem.OutputPath.FullPath;

                if (_diskProvider.FolderExists(outputPath))
                {
                    if (_downloadedEpisodesImportService.ShouldDeleteFolder(
                            new DirectoryInfo(outputPath), importedSeries) &&
                        trackedDownload.DownloadItem.CanMoveFiles)
                    {
                        _diskProvider.DeleteFolder(outputPath, true);
                    }
                }

                var importedResults = groupedTrackedDownload.Select(c => c.ImportResult)
                    .Where(c => c.Result == ImportResultType.Imported)
                    .ToList();

                var allEpisodesImported = importedResults
                                                                .SelectMany(c => c.ImportDecision.LocalEpisode.Episodes).Count() >=
                                                                    Math.Max(1, trackedDownload.RemoteEpisode?.Episodes?.Count ?? 1);

                if (allEpisodesImported)
                {
                    var episodeFiles = importedResults.Select(i => i.EpisodeFile).ToList();

                    trackedDownload.State = TrackedDownloadState.Imported;
                    _eventAggregator.PublishEvent(new DownloadCompletedEvent(trackedDownload, importedSeries.Id, episodeFiles, importedResults.First().ImportDecision.LocalEpisode.Release));
                }
            }
        }

        public void UpdateTrackedDownloadsForQueuedManualImport(ManualImportCommand message)
        {
            if (message.Files == null)
            {
                return;
            }

            var updated = false;

            foreach (var filesByDownload in message.Files.Where(f => f.DownloadId.IsNotNullOrWhiteSpace()).GroupBy(f => f.DownloadId))
            {
                var trackedDownload = _trackedDownloadService.Find(filesByDownload.Key);
                var firstFile = filesByDownload.FirstOrDefault(f => f.SeriesId > 0);

                if (trackedDownload == null || firstFile == null)
                {
                    continue;
                }

                var series = _seriesService.GetSeries(firstFile.SeriesId);
                var episodeIds = filesByDownload
                    .SelectMany(f => f.EpisodeIds ?? new List<int>())
                    .Distinct()
                    .ToList();
                var episodes = episodeIds.Any() ? _episodeService.GetEpisodes(episodeIds) : new List<Episode>();
                var parsedEpisodeInfo = Parser.Parser.ParsePath(firstFile.Path,
                                                                  _configService.ParseTvdbIdFromReleaseName,
                                                                  _configService.ParseEpisodeNumberOnlyAsSeasonOne) ?? new ParsedEpisodeInfo();

                UpdateTrackedDownloadForManualImport(trackedDownload,
                                                     series,
                                                     episodes,
                                                     parsedEpisodeInfo,
                                                     firstFile.Languages,
                                                     TrackedDownloadState.ImportPending);
                updated = true;
            }

            if (updated)
            {
                _eventAggregator.PublishEvent(new TrackedDownloadRefreshedEvent(_trackedDownloadService.GetTrackedDownloads()));
            }
        }

        private void UpdateTrackedDownloadForManualImport(TrackedDownload trackedDownload, LocalEpisode localEpisode, List<Episode> manuallyMatchedEpisodes)
        {
            var episodes = manuallyMatchedEpisodes?.Any() == true ? manuallyMatchedEpisodes : localEpisode.Episodes;

            UpdateTrackedDownloadForManualImport(trackedDownload,
                                                 localEpisode.Series,
                                                 episodes,
                                                 localEpisode.FileEpisodeInfo,
                                                 localEpisode.Languages,
                                                 TrackedDownloadState.Importing,
                                                 localEpisode.CustomFormats,
                                                 localEpisode.CustomFormatScore);

            _eventAggregator.PublishEvent(new TrackedDownloadRefreshedEvent(_trackedDownloadService.GetTrackedDownloads()));
        }

        private void UpdateTrackedDownloadForManualImport(TrackedDownload trackedDownload,
                                                          Series series,
                                                          List<Episode> episodes,
                                                          ParsedEpisodeInfo parsedEpisodeInfo,
                                                          List<Language> languages,
                                                          TrackedDownloadState state,
                                                          List<CustomFormat> customFormats = null,
                                                          int? customFormatScore = null)
        {
            trackedDownload.RemoteEpisode ??= new RemoteEpisode();
            trackedDownload.RemoteEpisode.Series = series;
            trackedDownload.RemoteEpisode.Episodes = episodes;
            trackedDownload.RemoteEpisode.ParsedEpisodeInfo ??= parsedEpisodeInfo;
            trackedDownload.RemoteEpisode.Languages = languages;

            if (customFormats != null)
            {
                trackedDownload.RemoteEpisode.CustomFormats = customFormats;
                trackedDownload.RemoteEpisode.CustomFormatScore = customFormatScore ?? 0;
            }

            trackedDownload.ClearStatus();
            trackedDownload.State = state;
        }
    }
}
