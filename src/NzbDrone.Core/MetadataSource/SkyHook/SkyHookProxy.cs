using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Cloud;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.DataAugmentation.DailySeries;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MetadataSource.SkyHook.Resource;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.MetadataSource.SkyHook
{
    public class SkyHookProxy : IProvideSeriesInfo, ISearchForNewSeries
    {
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;
        private readonly ISeriesService _seriesService;
        private readonly IDailySeriesService _dailySeriesService;
        private readonly IHttpRequestBuilderFactory _requestBuilder;
        private readonly IConfigService _configService;
        private readonly IFetchSeriesTranslations _seriesTranslationProxy;

        public SkyHookProxy(IHttpClient httpClient,
                            ISonarrCloudRequestBuilder requestBuilder,
                            ISeriesService seriesService,
                            IDailySeriesService dailySeriesService,
                            IConfigService configService,
                            IFetchSeriesTranslations seriesTranslationProxy,
                            Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _seriesService = seriesService;
            _dailySeriesService = dailySeriesService;
            _requestBuilder = requestBuilder.SkyHookTvdb;
            _configService = configService;
            _seriesTranslationProxy = seriesTranslationProxy;
        }

        public Tuple<Series, List<Episode>> GetSeriesInfo(int tvdbSeriesId)
        {
            return GetSeriesInfo(tvdbSeriesId, GetSeriesInfoSearchLanguage(), SeasonType.Official);
        }

        public Tuple<Series, List<Episode>> GetSeriesInfo(int tvdbSeriesId, Language language, string seasonType)
        {
            // Legacy series-info localization keeps the English SkyHook episode titles for naming.
            language = _configService.UseSeriesInfoLanguage ? Language.English : language ?? GetSeriesInfoSearchLanguage();
            var isoLanguage = IsoLanguages.Get(language) ?? IsoLanguages.Get(Language.English);

            var httpRequest = _requestBuilder.Create()
                                             .SetSegment("route", "shows")
                                             .Resource($"{isoLanguage.ThreeLetterCode}/{tvdbSeriesId}/{seasonType ?? SeasonType.Official}")
                                             .Build();

            httpRequest.AllowAutoRedirect = true;
            httpRequest.SuppressHttpError = true;

            var httpResponse = _httpClient.Get<ShowResource>(httpRequest);

            if (httpResponse.HasHttpError)
            {
                if (httpResponse.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new SeriesNotFoundException(tvdbSeriesId);
                }
                else
                {
                    throw new HttpException(httpRequest, httpResponse);
                }
            }

            var episodes = httpResponse.Resource.Episodes.Select(MapEpisode);
            var series = MapSeries(httpResponse.Resource);

            return new Tuple<Series, List<Episode>>(series, episodes.ToList());
        }

        public List<Series> SearchForNewSeriesByImdbId(string imdbId)
        {
            return SearchForNewSeriesByImdbId(imdbId, GetSeriesInfoSearchLanguage());
        }

        public List<Series> SearchForNewSeriesByImdbId(string imdbId, Language language)
        {
            imdbId = Parser.Parser.NormalizeImdbId(imdbId);

            if (imdbId == null)
            {
                return new List<Series>();
            }

            var results = SearchForNewSeries($"imdb:{imdbId}", language);

            return results;
        }

        public List<Series> SearchForNewSeriesByAniListId(int aniListId)
        {
            return SearchForNewSeriesByAniListId(aniListId, GetSeriesInfoSearchLanguage());
        }

        public List<Series> SearchForNewSeriesByAniListId(int aniListId, Language language)
        {
            var results = SearchForNewSeries($"anilist:{aniListId}", language);

            return results;
        }

        public List<Series> SearchForNewSeriesByTmdbId(int tmdbId)
        {
            return SearchForNewSeriesByTmdbId(tmdbId, GetSeriesInfoSearchLanguage());
        }

        public List<Series> SearchForNewSeriesByTmdbId(int tmdbId, Language language)
        {
            var results = SearchForNewSeries($"tmdb:{tmdbId}", language);

            return results;
        }

        public List<Series> SearchForNewSeriesByMyAnimeListId(int malId)
        {
            return SearchForNewSeriesByMyAnimeListId(malId, GetSeriesInfoSearchLanguage());
        }

        public List<Series> SearchForNewSeriesByMyAnimeListId(int malId, Language language)
        {
            var results = SearchForNewSeries($"mal:{malId}", language);

            return results;
        }

        public List<Series> SearchForNewSeries(string title)
        {
            return SearchForNewSeries(title, GetSeriesInfoSearchLanguage());
        }

        public List<Series> SearchForNewSeries(string title, Language language)
        {
            language ??= GetSeriesInfoSearchLanguage();
            if (title.IsPathValid(PathValidationType.AnyOs))
            {
                throw new InvalidSearchTermException("Invalid search term '{0}'", title);
            }

            try
            {
                var searchTerm = Regex.Replace(title, @"\s+", " ").Trim();
                var lowerTitle = searchTerm.ToLowerInvariant();

                if (lowerTitle.StartsWith("tvdb:") || lowerTitle.StartsWith("tvdbid:"))
                {
                    var slug = lowerTitle.Split(':')[1].Trim();

                    if (slug.IsNullOrWhiteSpace() || slug.Any(char.IsWhiteSpace) || !int.TryParse(slug, out var tvdbId) || tvdbId <= 0)
                    {
                        return new List<Series>();
                    }

                    try
                    {
                        var existingSeries = _seriesService.FindByTvdbId(tvdbId);

                        if (existingSeries != null)
                        {
                            return new List<Series> { existingSeries };
                        }

                        return new List<Series> { GetSeriesInfo(tvdbId, language, SeasonType.Official).Item1 };
                    }
                    catch (SeriesNotFoundException)
                    {
                        return new List<Series>();
                    }
                }

                var isoLanguage = IsoLanguages.Get(language) ?? IsoLanguages.Get(Language.English);
                var httpRequest = _requestBuilder.Create()
                                                 .SetSegment("route", "search")
                                                 .AddQueryParam("language", isoLanguage.ThreeLetterCode)
                                                 .AddQueryParam("term", searchTerm.ToLowerInvariant())
                                                 .Build();

                var httpResponse = _httpClient.Get<List<ShowResource>>(httpRequest);

                var results = httpResponse.Resource.SelectList(MapSearchResult);
                AddSeriesInfoLanguageSearchResults(results, searchTerm, language);

                return results;
            }
            catch (HttpException ex)
            {
                _logger.Warn(ex);
                throw new SkyHookException("Search for '{0}' failed. Unable to communicate with SkyHook. {1}", ex, title, ex.Message);
            }
            catch (WebException ex)
            {
                _logger.Warn(ex);
                throw new SkyHookException("Search for '{0}' failed. Unable to communicate with SkyHook. {1}", ex, title, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex);
                throw new SkyHookException("Search for '{0}' failed. Invalid response received from SkyHook. {1}", ex, title, ex.Message);
            }
        }

        private void AddSeriesInfoLanguageSearchResults(List<Series> results, string searchTerm, Language language)
        {
            if (language == Language.English || IsIdSearch(searchTerm))
            {
                return;
            }

            try
            {
                var existingTvdbIds = results.Select(s => s.TvdbId).ToHashSet();

                foreach (var tvdbId in _seriesTranslationProxy.SearchSeries(searchTerm, language))
                {
                    if (!existingTvdbIds.Add(tvdbId))
                    {
                        continue;
                    }

                    try
                    {
                        var existingSeries = _seriesService.FindByTvdbId(tvdbId);
                        results.Add(existingSeries ?? GetSeriesInfo(tvdbId, language, SeasonType.Official).Item1);
                    }
                    catch (SeriesNotFoundException)
                    {
                        _logger.Debug("Unable to add localized TheTVDB search result for tvdbid {0}: series was not found in SkyHook.", tvdbId);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "Unable to add localized TheTVDB search result for tvdbid {0}.", tvdbId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to search TheTVDB with configured series info language.");
            }
        }

        private static bool IsIdSearch(string searchTerm)
        {
            var lowerSearchTerm = searchTerm.ToLowerInvariant();

            return lowerSearchTerm.StartsWith("imdb:") ||
                   lowerSearchTerm.StartsWith("tmdb:") ||
                   lowerSearchTerm.StartsWith("anilist:") ||
                   lowerSearchTerm.StartsWith("mal:");
        }

        private Language GetSeriesInfoSearchLanguage()
        {
            if (!_configService.UseSeriesInfoLanguage)
            {
                return Language.English;
            }

            var language = (Language)_configService.SeriesInfoLanguage;

            return language == Language.Original ? Language.English : language;
        }

        private static string GetExactSearchTitle(string title)
        {
            var parserResult = Parser.Parser.ParseTitle(title);

            return parserResult?.SeriesTitleInfo?.TitleWithoutYear ??
                   parserResult?.SeriesTitle ??
                   title;
        }

        private static bool IsExactTitleMatch(string cleanTitle, string candidateTitle)
        {
            return candidateTitle.IsNotNullOrWhiteSpace() && Parser.Parser.CleanSeriesTitle(candidateTitle) == cleanTitle;
        }

        private static bool IsExactDefaultTitleMatch(string cleanTitle, Series series)
        {
            return (series.CleanTitle.IsNotNullOrWhiteSpace() && series.CleanTitle == cleanTitle) ||
                   (series.CleanOriginalTitle.IsNotNullOrWhiteSpace() && series.CleanOriginalTitle == cleanTitle) ||
                   IsExactTitleMatch(cleanTitle, series.Title) ||
                   IsExactTitleMatch(cleanTitle, series.OriginalTitle);
        }

        private static bool IsExactLocalizedTitleMatch(string cleanTitle, Language seriesInfoLanguage, Series series)
        {
            return series.Translations?.Any(t =>
                t.Language == seriesInfoLanguage &&
                ((t.CleanTitle.IsNotNullOrWhiteSpace() && t.CleanTitle == cleanTitle) ||
                 IsExactTitleMatch(cleanTitle, t.Title))) == true;
        }

        private static string FormatExactSearchMatches(IEnumerable<Series> series)
        {
            var matches = series
                .Select(s => $"{s.Title} ({s.Year}) tvdbid: {s.TvdbId}")
                .ToList();

            return matches.Any() ? string.Join(", ", matches) : "none";
        }

        public Series SearchForNewSeriesByExactTitle(string title, int year, List<Series> candidates)
        {
            try
            {
                var parserResult = Parser.Parser.ParseTitle(title);

                if (year <= 1800 && parserResult?.SeriesTitleInfo?.Year > 1800)
                {
                    year = parserResult.SeriesTitleInfo.Year;
                }

                if (year <= 1800)
                {
                    return null;
                }

                var exactSearchTitle = GetExactSearchTitle(title);
                var cleanTitle = Parser.Parser.CleanSeriesTitle(exactSearchTitle);

                if (cleanTitle.IsNullOrWhiteSpace())
                {
                    return null;
                }

                var candidatesInYear = candidates.Where(s => s.Year == year).ToList();
                var defaultTitleMatches = candidatesInYear
                    .Where(s => IsExactDefaultTitleMatch(cleanTitle, s))
                    .ToList();
                var localizedTitleMatches = new List<Series>();

                var seriesInfoLanguage = GetSeriesInfoSearchLanguage();

                if (seriesInfoLanguage != Language.English)
                {
                    localizedTitleMatches = candidatesInYear
                        .Where(s => IsExactLocalizedTitleMatch(cleanTitle, seriesInfoLanguage, s))
                        .ToList();
                }

                var uniqueMatches = defaultTitleMatches
                    .Concat(localizedTitleMatches)
                    .DistinctBy(s => s.TvdbId)
                    .ToList();

                _logger.Debug("Exact search for '{0}' normalized to '{1}' using language '{2}' found {3} default-title matches [{4}] and {5} localized-title matches [{6}] ({7} unique tvdb ids).",
                    title,
                    cleanTitle,
                    seriesInfoLanguage,
                    defaultTitleMatches.Count,
                    FormatExactSearchMatches(defaultTitleMatches),
                    localizedTitleMatches.Count,
                    FormatExactSearchMatches(localizedTitleMatches),
                    uniqueMatches.Count);

                if (uniqueMatches.Count != 1)
                {
                    if (uniqueMatches.Count > 1)
                    {
                        _logger.Debug("Exact search for '{0}' refused automatic matching because multiple distinct series matched across default and localized search branches: {1}",
                            title,
                            FormatExactSearchMatches(uniqueMatches));
                    }

                    return null;
                }

                return uniqueMatches.Single();
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Exact search for '{0}' failed.", title);
                return null;
            }
        }

        private Series MapSearchResult(ShowResource show)
        {
            var series = _seriesService.FindByTvdbId(show.TvdbId);

            if (series == null)
            {
                series = MapSeries(show);
            }

            return series;
        }

        private Series MapSeries(ShowResource show)
        {
            var series = new Series();
            series.TvdbId = show.TvdbId;

            if (show.TvRageId.HasValue)
            {
                series.TvRageId = show.TvRageId.Value;
            }

            if (show.TvMazeId.HasValue)
            {
                series.TvMazeId = show.TvMazeId.Value;
            }

            if (show.TmdbId.HasValue)
            {
                series.TmdbId = show.TmdbId.Value;
            }

            var originalTitle = show.Translations?.FirstOrDefault(t => t.Language == show.OriginalLanguage)?.Title;
            var cleanOriginalTitle = originalTitle.IsNotNullOrWhiteSpace() ? originalTitle.CleanSeriesTitle() : null;

            if (cleanOriginalTitle.IsNullOrWhiteSpace())
            {
                cleanOriginalTitle = null;
            }

            series.ImdbId = show.ImdbId;
            series.MalIds = show.MalIds;
            series.AniListIds = show.AniListIds;
            series.Title = show.Title;
            series.CleanTitle = show.Title.CleanSeriesTitle();
            series.OriginalTitle = originalTitle;
            series.CleanOriginalTitle = cleanOriginalTitle;
            series.SortTitle = SeriesTitleNormalizer.Normalize(show.Title, show.TvdbId);

            series.OriginalLanguage = show.OriginalLanguage.IsNotNullOrWhiteSpace() ?
                IsoLanguages.Find(show.OriginalLanguage.ToLower())?.Language ?? Language.English :
                Language.English;

            if (show.FirstAired != null)
            {
                series.FirstAired = DateTime.ParseExact(show.FirstAired, "yyyy-MM-dd", DateTimeFormatInfo.InvariantInfo, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                series.Year = series.FirstAired.Value.Year;
            }

            if (show.LastAired != null)
            {
                series.LastAired = DateTime.ParseExact(show.LastAired, "yyyy-MM-dd", DateTimeFormatInfo.InvariantInfo, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            }

            series.Overview = show.Overview;

            if (show.Runtime != null)
            {
                series.Runtime = show.Runtime.Value;
            }

            series.Network = show.Network;

            if (show.TimeOfDay != null)
            {
                series.AirTime = string.Format("{0:00}:{1:00}", show.TimeOfDay.Hours, show.TimeOfDay.Minutes);
            }

            series.TitleSlug = show.Slug;
            series.Status = MapSeriesStatus(show.Status);
            series.Ratings = MapRatings(show.Rating);
            series.Genres = show.Genres;
            series.OriginalCountry = show.OriginalCountry;

            if (show.ContentRating.IsNotNullOrWhiteSpace())
            {
                series.Certification = show.ContentRating.ToUpper();
            }

            if (_dailySeriesService.IsDailySeries(series.TvdbId))
            {
                series.SeriesType = SeriesTypes.Daily;
            }

            series.Actors = show.Actors.Select(MapActors).ToList();
            series.Seasons = show.Seasons.Select(MapSeason).DistinctBy(s => s.SeasonNumber).ToList();
            series.Images = show.Images.Select(MapImage).ToList();

            series.Monitored = true;

            series.SeasonTypes = show.SeasonTypes.Select(MapSeasonType).ToList();
            series.Translations = MapTranslations(show.Translations);

            var seriesInfoLanguage = GetSeriesInfoLanguage(series);

            if (seriesInfoLanguage != Language.English && !series.Translations.Any(t => t.Language == seriesInfoLanguage))
            {
                series.Translations.AddRange(_seriesTranslationProxy.GetTranslations(series.TvdbId, seriesInfoLanguage));
            }

            return series;
        }

        private Language GetSeriesInfoLanguage(Series series)
        {
            if (!_configService.UseSeriesInfoLanguage)
            {
                return Language.English;
            }

            var language = (Language)_configService.SeriesInfoLanguage;

            if (language == Language.Original)
            {
                return series.OriginalLanguage;
            }

            return language;
        }

        private static Actor MapActors(ActorResource arg)
        {
            var newActor = new Actor
            {
                Name = arg.Name,
                Character = arg.Character
            };

            if (arg.Image != null)
            {
                newActor.Images = new List<MediaCover.MediaCover>
                {
                    new MediaCover.MediaCover(MediaCoverTypes.Headshot, arg.Image)
                };
            }

            return newActor;
        }

        private static Episode MapEpisode(EpisodeResource oracleEpisode)
        {
            var episode = new Episode();
            episode.TvdbId = oracleEpisode.TvdbId;
            episode.Overview = oracleEpisode.Overview;
            episode.SeasonNumber = oracleEpisode.SeasonNumber;
            episode.EpisodeNumber = oracleEpisode.EpisodeNumber;
            episode.AbsoluteEpisodeNumber = oracleEpisode.AbsoluteEpisodeNumber;
            episode.Title = oracleEpisode.Title;
            episode.AiredAfterSeasonNumber = oracleEpisode.AiredAfterSeasonNumber;
            episode.AiredBeforeSeasonNumber = oracleEpisode.AiredBeforeSeasonNumber;
            episode.AiredBeforeEpisodeNumber = oracleEpisode.AiredBeforeEpisodeNumber;

            episode.AirDate = oracleEpisode.AirDate;
            episode.AirDateUtc = oracleEpisode.AirDateUtc;
            episode.Runtime = oracleEpisode.Runtime;
            episode.FinaleType = oracleEpisode.FinaleType;

            episode.Ratings = MapRatings(oracleEpisode.Rating);

            // Don't include series fanart images as episode screenshot
            if (oracleEpisode.Image != null)
            {
                episode.Images.Add(new MediaCover.MediaCover(MediaCoverTypes.Screenshot, oracleEpisode.Image));
            }

            return episode;
        }

        private static Season MapSeason(SeasonResource seasonResource)
        {
            return new Season
            {
                SeasonNumber = seasonResource.SeasonNumber,
                Title = seasonResource.Title,
                Images = seasonResource.Images.Select(MapImage).ToList(),
                Monitored = seasonResource.SeasonNumber > 0
            };
        }

        private static SeriesStatusType MapSeriesStatus(string status)
        {
            if (status.Equals("ended", StringComparison.InvariantCultureIgnoreCase))
            {
                return SeriesStatusType.Ended;
            }

            if (status.Equals("upcoming", StringComparison.InvariantCultureIgnoreCase))
            {
                return SeriesStatusType.Upcoming;
            }

            return SeriesStatusType.Continuing;
        }

        private static Ratings MapRatings(RatingResource rating)
        {
            if (rating == null)
            {
                return new Ratings();
            }

            return new Ratings
            {
                Votes = rating.Count,
                Value = rating.Value
            };
        }

        private static MediaCover.MediaCover MapImage(ImageResource arg)
        {
            return new MediaCover.MediaCover
            {
                RemoteUrl = arg.Url,
                CoverType = MapCoverType(arg.CoverType)
            };
        }

        private static MediaCoverTypes MapCoverType(string coverType)
        {
            switch (coverType.ToLower())
            {
                case "poster":
                    return MediaCoverTypes.Poster;
                case "banner":
                    return MediaCoverTypes.Banner;
                case "fanart":
                    return MediaCoverTypes.Fanart;
                case "clearlogo":
                    return MediaCoverTypes.Clearlogo;
                default:
                    return MediaCoverTypes.Unknown;
            }
        }

        private static SeasonType MapSeasonType(SeasonTypeResource seasonType)
        {
            return new SeasonType
            {
                Name = seasonType.Name,
                Type = seasonType.Type,
                SeasonNumbers = seasonType.SeasonNumbers,
                EpisodeCount = seasonType.EpisodeCount
            };
        }

        private static List<SeriesTranslation> MapTranslations(List<TranslationResource> translations)
        {
            var result = new List<SeriesTranslation>();

            (translations ?? new List<TranslationResource>()).ForEach(t =>
            {
                var language = IsoLanguages.Find(t.Language);

                if (language != null && t.Title.IsNotNullOrWhiteSpace())
                {
                    result.Add(new SeriesTranslation
                    {
                        Language = language.Language,
                        Title = t.Title,
                        CleanTitle = t.Title.CleanSeriesTitle(),
                        Overview = t.Overview
                    });
                }
            });

            return result;
        }
    }
}
