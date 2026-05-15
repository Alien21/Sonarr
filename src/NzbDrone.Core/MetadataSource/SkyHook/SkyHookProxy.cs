using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Cloud;
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
using NzbDrone.Core.Tv.Translations;

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
            _requestBuilder = requestBuilder.SkyHookTvdb;
            _logger = logger;
            _seriesService = seriesService;
            _dailySeriesService = dailySeriesService;
            _configService = configService;
            _seriesTranslationProxy = seriesTranslationProxy;
        }

        public Tuple<Series, List<Episode>> GetSeriesInfo(int tvdbSeriesId)
        {
            var httpRequest = _requestBuilder.Create()
                                             .SetSegment("route", "shows")
                                             .Resource(tvdbSeriesId.ToString())
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
            imdbId = Parser.Parser.NormalizeImdbId(imdbId);

            if (imdbId == null)
            {
                return new List<Series>();
            }

            var results = SearchForNewSeries($"imdb:{imdbId}");

            return results;
        }

        public List<Series> SearchForNewSeriesByAniListId(int aniListId)
        {
            var results = SearchForNewSeries($"anilist:{aniListId}");

            return results;
        }

        public List<Series> SearchForNewSeriesByMyAnimeListId(int malId)
        {
            var results = SearchForNewSeries($"mal:{malId}");

            return results;
        }

        public List<Series> SearchForNewSeriesByTmdbId(int tmdbId)
        {
            var results = SearchForNewSeries($"tmdb:{tmdbId}");

            return results;
        }

        public List<Series> SearchForNewSeries(string title)
        {
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

                        return new List<Series> { GetSeriesInfo(tvdbId).Item1 };
                    }
                    catch (SeriesNotFoundException)
                    {
                        return new List<Series>();
                    }
                }

                var httpRequest = _requestBuilder.Create()
                                                 .SetSegment("route", "search")
                                                 .AddQueryParam("term", searchTerm.ToLowerInvariant())
                                                 .Build();

                var httpResponse = _httpClient.Get<List<ShowResource>>(httpRequest);

                var results = httpResponse.Resource.SelectList(MapSearchResult);
                AddSeriesInfoLanguageSearchResults(results, searchTerm);

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

        private void AddSeriesInfoLanguageSearchResults(List<Series> results, string searchTerm)
        {
            var language = GetSeriesInfoSearchLanguage();

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
                        results.Add(existingSeries ?? GetSeriesInfo(tvdbId).Item1);
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

            series.ImdbId = show.ImdbId;
            series.MalIds = show.MalIds;
            series.AniListIds = show.AniListIds;
            series.Title = show.Title;
            series.CleanTitle = Parser.Parser.CleanSeriesTitle(show.Title);
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

            if (show.ContentRating.IsNotNullOrWhiteSpace())
            {
                series.Certification = show.ContentRating.ToUpper();
            }

            if (_dailySeriesService.IsDailySeries(series.TvdbId))
            {
                series.SeriesType = SeriesTypes.Daily;
            }

            series.Actors = show.Actors.Select(MapActors).ToList();
            series.Seasons = show.Seasons.Select(MapSeason).ToList();
            series.Images = show.Images.Select(MapImage).ToList();

            var seriesInfoLanguage = GetSeriesInfoLanguage(series);
            series.Translations = seriesInfoLanguage == Language.English ? new List<SeriesTranslation>() : _seriesTranslationProxy.GetTranslations(series.TvdbId, seriesInfoLanguage);
            series.Monitored = true;

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
    }
}
