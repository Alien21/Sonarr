using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Notifications.Emby
{
    public class MediaBrowserProxy
    {
        private const string RecentlyAddedMetadataFields =
            "DateCreated,Settings,Tags,Genres,Overview,OriginalTitle,ProviderIds,People,Studios," +
            "ProductionLocations,CustomRating,Taglines,SortName,SpecialEpisodeNumbers,Path";

        private readonly IHttpClient _httpClient;
        private readonly IMediaServerItemLinkStore _itemLinks;
        private readonly Logger _logger;

        public MediaBrowserProxy(IHttpClient httpClient, IMediaServerItemLinkStore itemLinks, Logger logger)
        {
            _httpClient = httpClient;
            _itemLinks = itemLinks;
            _logger = logger;
        }

        public void MarkAsRecentlyAdded(MediaBrowserSettings settings, DownloadMessage message)
        {
            MarkAsRecentlyAdded(settings, message, true);
        }

        private void MarkAsRecentlyAdded(MediaBrowserSettings settings, DownloadMessage message, bool retryMissingLink)
        {
            try
            {
                var series = message.Series;
                var episodes = message.EpisodeInfo?.Episodes?.DistinctBy(episode => (episode.SeasonNumber, episode.EpisodeNumber)).ToList();
                if (series == null || episodes?.Any() != true ||
                    (series.TvdbId <= 0 && series.ImdbId.IsNullOrWhiteSpace() && series.TmdbId <= 0))
                {
                    return;
                }

                var seriesKey = GetLinkKey(settings, series);
                if (_itemLinks.IsNegative(seriesKey))
                {
                    return;
                }

                var episodeKeys = episodes.ToDictionary(episode => (episode.SeasonNumber, episode.EpisodeNumber), episode => GetLinkKey(settings, series, episode));
                var episodeLinks = new Dictionary<string, List<MediaServerItemLink>>();
                foreach (var key in episodeKeys.Values)
                {
                    if (_itemLinks.TryGet(key, out var links))
                    {
                        episodeLinks[key] = links;
                    }
                }

                var fresh = new List<JsonObject>();
                var cachedSeries = _itemLinks.TryGet(seriesKey, out var seriesLinks);
                if (cachedSeries && (seriesLinks.Count != 1 || seriesLinks[0].ItemId.IsNullOrWhiteSpace() ||
                    episodeLinks.Values.Any(links => links.Count != 1 || links[0].ItemId.IsNullOrWhiteSpace())))
                {
                    _itemLinks.Remove(seriesKey);
                    cachedSeries = false;
                }

                if (cachedSeries)
                {
                    try
                    {
                        var ids = seriesLinks.Concat(episodeLinks.Values.SelectMany(links => links)).Select(link => link.ItemId).Distinct();
                        fresh = GetRawItems(BuildMetadataRequest(settings, string.Join(",", ids)));
                        cachedSeries = ValidateLinks(series, episodes, episodeKeys, seriesLinks, episodeLinks, fresh);
                    }
                    catch (HttpException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
                    {
                        cachedSeries = false;
                    }

                    if (!cachedSeries)
                    {
                        _itemLinks.Remove(seriesKey);
                    }
                }

                if (!cachedSeries)
                {
                    fresh.Clear();
                    episodeLinks.Clear();
                    foreach (var key in episodeKeys.Values)
                    {
                        _itemLinks.Remove(key);
                    }

                    if (series.Year <= 0)
                    {
                        _logger.Debug("Skipping Jellyfin added date lookup for series {0}: no year or saved binding is available", series.Id);
                        return;
                    }

                    var matchingSeries = FindSeriesItems(settings, series);

                    var remoteSeries = SelectUniqueItem(matchingSeries, MapPath(series.Path, settings));
                    if (remoteSeries == null)
                    {
                        _itemLinks.SetNegative(seriesKey);
                        return;
                    }

                    seriesLinks = new List<MediaServerItemLink> { new MediaServerItemLink { ItemId = GetString(remoteSeries["Id"]) } };
                    _itemLinks.Set(seriesKey, seriesLinks);
                }

                var seriesId = seriesLinks.Single().ItemId;
                var importedPath = message.EpisodeFile?.Path;
                if (importedPath.IsNullOrWhiteSpace() && message.EpisodeFile?.RelativePath.IsNotNullOrWhiteSpace() == true)
                {
                    importedPath = System.IO.Path.Combine(series.Path, message.EpisodeFile.RelativePath);
                }

                var mappedFilePath = MapPath(importedPath, settings);
                var missing = episodes.Where(episode => !episodeLinks.ContainsKey(episodeKeys[(episode.SeasonNumber, episode.EpisodeNumber)]) &&
                    !_itemLinks.IsNegative(episodeKeys[(episode.SeasonNumber, episode.EpisodeNumber)]));
                foreach (var season in missing.GroupBy(episode => episode.SeasonNumber))
                {
                    var request = BuildRequest($"/Shows/{Uri.EscapeDataString(seriesId)}/Episodes", settings)
                        .AddQueryParam("season", season.Key)
                        .AddQueryParam("fields", "Path,ProviderIds")
                        .Build();
                    var remoteEpisodes = GetRawItems(request);
                    foreach (var episode in season)
                    {
                        var matches = remoteEpisodes.Where(item => MatchesEpisode(item, season.Key, episode.EpisodeNumber)).ToList();
                        var remoteEpisode = SelectUniqueItem(matches, mappedFilePath);
                        var key = episodeKeys[(episode.SeasonNumber, episode.EpisodeNumber)];
                        if (remoteEpisode == null)
                        {
                            _itemLinks.SetNegative(key);
                            continue;
                        }

                        var links = new List<MediaServerItemLink> { new MediaServerItemLink { ItemId = GetString(remoteEpisode["Id"]) } };
                        episodeLinks[key] = links;
                        _itemLinks.Set(key, links);
                    }
                }

                foreach (var itemId in episodeLinks.Values.SelectMany(links => links).Select(link => link.ItemId).Distinct())
                {
                    try
                    {
                        UpdateAddedDate(settings, itemId, fresh.SingleOrDefault(item => GetString(item["Id"]) == itemId));
                    }
                    catch (HttpException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
                    {
                        _itemLinks.Remove(seriesKey);
                        foreach (var key in episodeKeys.Values)
                        {
                            _itemLinks.Remove(key);
                        }

                        if (cachedSeries && retryMissingLink)
                        {
                            MarkAsRecentlyAdded(settings, message, false);
                            return;
                        }

                        _logger.Warn(ex, "Unable to mark Jellyfin episode {0} as recently added", itemId);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, "Unable to mark Jellyfin episode {0} as recently added", itemId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to mark imported Jellyfin episodes as recently added");
            }
        }

        private static bool ValidateLinks(
            Series series,
            List<Episode> episodes,
            Dictionary<(int SeasonNumber, int EpisodeNumber), string> episodeKeys,
            List<MediaServerItemLink> seriesLinks,
            Dictionary<string, List<MediaServerItemLink>> episodeLinks,
            List<JsonObject> metadata)
        {
            if (seriesLinks.Count != 1 || seriesLinks[0].ItemId.IsNullOrWhiteSpace())
            {
                return false;
            }

            var seriesId = seriesLinks[0].ItemId;
            var parents = metadata.Where(item => GetString(item["Id"]) == seriesId).ToList();
            if (parents.Count != 1 || !MatchesSeries(parents[0], series))
            {
                return false;
            }

            foreach (var episode in episodes)
            {
                if (!episodeLinks.TryGetValue(episodeKeys[(episode.SeasonNumber, episode.EpisodeNumber)], out var links))
                {
                    continue;
                }

                if (links.Count != 1 || links[0].ItemId.IsNullOrWhiteSpace())
                {
                    return false;
                }

                var matches = metadata.Where(item => GetString(item["Id"]) == links[0].ItemId).ToList();
                if (matches.Count != 1 || !MatchesEpisode(matches[0], episode.SeasonNumber, episode.EpisodeNumber) || GetString(matches[0]["SeriesId"]) != seriesId)
                {
                    return false;
                }
            }

            return true;
        }

        private string GetLinkKey(MediaBrowserSettings settings, Series series, Episode episode = null)
        {
            var identity = new[]
            {
                series.TvdbId.ToString(CultureInfo.InvariantCulture), series.ImdbId ?? string.Empty, series.TmdbId.ToString(CultureInfo.InvariantCulture),
                episode == null ? "series" : $"episode:{episode.SeasonNumber}:{episode.EpisodeNumber}"
            };
            return MediaServerItemLinkKey.Create("jellyfin", GetUrl(settings), settings.ApiKey, settings.MapFrom, settings.MapTo, identity);
        }

        private static bool MatchesSeries(JsonObject item, Series series)
        {
            return GetString(item["Type"]) == "Series" &&
                ((series.TvdbId > 0 && GetString(item["ProviderIds"]?["Tvdb"]) == series.TvdbId.ToString(CultureInfo.InvariantCulture)) ||
                (series.ImdbId.IsNotNullOrWhiteSpace() && GetString(item["ProviderIds"]?["Imdb"]) == series.ImdbId) ||
                (series.TmdbId > 0 && GetString(item["ProviderIds"]?["Tmdb"]) == series.TmdbId.ToString(CultureInfo.InvariantCulture)));
        }

        public void TestConnection(MediaBrowserSettings settings)
        {
            var path = "/System/Configuration";
            var request = BuildRequest(path, settings).Build();

            var response = _httpClient.Get(request);
            _logger.Trace("Response: {0}", response.Content);
        }

        public void Notify(MediaBrowserSettings settings, string title, string message)
        {
            var path = "/Notifications/Admin";
            var request = BuildRequest(path, settings).Build();
            request.Headers.ContentType = "application/json";
            request.LogHttpError = false;

            request.SetContent(new
                           {
                               Name = title,
                               Description = message,
                               ImageUrl = "https://raw.github.com/Sonarr/Sonarr/develop/Logo/64.png"
                           }.ToJson());

            ProcessRequest(request);
        }

        public HashSet<string> GetPaths(MediaBrowserSettings settings, Series series)
        {
            var path = "/Items";

            var request = BuildRequest(path, settings)
                .AddQueryParam("recursive", "true")
                .AddQueryParam("includeItemTypes", "Series")
                .AddQueryParam("fields", "Path,ProviderIds")
                .AddQueryParam("years", series.Year)
                .Build();

            try
            {
                var paths = ProcessGetRequest<MediaBrowserItems>(request).Items.GroupBy(item =>
                {
                    if (item is { ProviderIds.Tvdb: int tvdbid } && tvdbid != 0 && tvdbid == series.TvdbId)
                    {
                        return MediaBrowserMatchQuality.Id;
                    }

                    if (item is { ProviderIds.Imdb: string imdbid } && imdbid == series.ImdbId)
                    {
                        return MediaBrowserMatchQuality.Id;
                    }

                    if (item is { ProviderIds.TvMaze: int tvmazeid } && tvmazeid != 0 && tvmazeid == series.TvMazeId)
                    {
                        return MediaBrowserMatchQuality.Id;
                    }

                    if (item is { ProviderIds.Tmdb: int tmdbid } && tmdbid != 0 && tmdbid == series.TmdbId)
                    {
                        return MediaBrowserMatchQuality.Id;
                    }

                    if (item is { ProviderIds.TvRage: int tvrageid } && tvrageid != 0 && tvrageid == series.TvRageId)
                    {
                        return MediaBrowserMatchQuality.Id;
                    }

                    if (item is { Name: var name } && name == series.Title)
                    {
                        return MediaBrowserMatchQuality.Name;
                    }

                    return MediaBrowserMatchQuality.None;
                },
                    item => item.Path).OrderBy(group => (int)group.Key).First();

                if (paths.Key == MediaBrowserMatchQuality.None)
                {
                    _logger.Trace("Could not find series by name");

                    return new HashSet<string>();
                }

                _logger.Trace("Found series by name/id: {0}", string.Join(" ", paths));

                return paths.ToHashSet();
            }
            catch (InvalidOperationException)
            {
                _logger.Trace("Could not find series by name.");

                return new HashSet<string>();
            }
        }

        public void Update(MediaBrowserSettings settings, string seriesPath, string updateType)
        {
            var path = "/Library/Media/Updated";
            var request = BuildRequest(path, settings).Build();
            request.Headers.ContentType = "application/json";

            request.SetContent(new
            {
                Updates = new[]
                {
                    new
                    {
                        Path = seriesPath,
                        UpdateType = updateType
                    }
                }
            }.ToJson());

            ProcessRequest(request);
        }

        private List<JsonObject> FindSeriesItems(MediaBrowserSettings settings, Series series)
        {
            const int pageSize = 200;
            var matching = new List<JsonObject>();
            var seenIds = new HashSet<string>();
            var startIndex = 0;
            while (true)
            {
                var builder = BuildRequest("/Items", settings)
                    .AddQueryParam("recursive", "true")
                    .AddQueryParam("includeItemTypes", "Series")
                    .AddQueryParam("collapseBoxSetItems", "false")
                    .AddQueryParam("fields", "Path,ProviderIds")
                    .AddQueryParam("enableUserData", "false")
                    .AddQueryParam("enableImages", "false")
                    .AddQueryParam("startIndex", startIndex)
                    .AddQueryParam("limit", pageSize);
                builder.AddQueryParam("years", series.Year);

                var items = GetRawItems(builder.Build(), out var total, out var responseStart);
                if (responseStart.HasValue && responseStart != startIndex)
                {
                    throw new InvalidOperationException("Jellyfin returned an unexpected page of series");
                }

                var newIdCount = items.Select(item => GetString(item["Id"]))
                    .Where(id => id.IsNotNullOrWhiteSpace())
                    .Count(id => seenIds.Add(id));
                if (items.Count > 0 && newIdCount == 0)
                {
                    throw new InvalidOperationException("Jellyfin repeated a page of series");
                }

                matching.AddRange(items.Where(item => GetString(item["Type"]) == "Series" &&
                    ((series.TvdbId > 0 && GetString(item["ProviderIds"]?["Tvdb"]) == series.TvdbId.ToString(CultureInfo.InvariantCulture)) ||
                    (series.ImdbId.IsNotNullOrWhiteSpace() && GetString(item["ProviderIds"]?["Imdb"]) == series.ImdbId) ||
                    (series.TmdbId > 0 && GetString(item["ProviderIds"]?["Tmdb"]) == series.TmdbId.ToString(CultureInfo.InvariantCulture)))));
                startIndex += items.Count;
                if (items.Count == 0 || (total.HasValue && startIndex >= total) || (!total.HasValue && items.Count < pageSize))
                {
                    return matching;
                }
            }
        }

        private HttpRequest BuildMetadataRequest(MediaBrowserSettings settings, string ids)
        {
            return BuildRequest("/Items", settings)
                .AddQueryParam("ids", ids)
                .AddQueryParam("collapseBoxSetItems", "false")
                .AddQueryParam("recursive", "true")
                .AddQueryParam("fields", RecentlyAddedMetadataFields)
                .AddQueryParam("enableUserData", "false")
                .AddQueryParam("enableImages", "false")
                .Build();
        }

        private void UpdateAddedDate(MediaBrowserSettings settings, string itemId, JsonObject item = null)
        {
            if (item == null)
            {
                var items = GetRawItems(BuildMetadataRequest(settings, itemId));
                if (items.Count != 1)
                {
                    throw new InvalidOperationException("Jellyfin item metadata was not uniquely resolved");
                }

                item = items[0];
            }

            if (GetString(item["Id"]) != itemId || GetString(item["Name"]) == null || GetString(item["Type"]) != "Episode" ||
                item["LockData"] is not JsonValue lockData || !lockData.TryGetValue<bool>(out _) ||
                item["LockedFields"] is not JsonArray || item["Tags"] is not JsonArray || item["Genres"] is not JsonArray)
            {
                throw new InvalidOperationException("Jellyfin returned incomplete or mismatched episode metadata");
            }

            var payload = (JsonObject)item.DeepClone();
            payload["DateCreated"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            var request = BuildRequest($"/Items/{Uri.EscapeDataString(itemId)}", settings).Build();
            request.Headers.ContentType = "application/json";
            request.SetContent(payload.ToJsonString());
            ProcessRequest(request);
        }

        private List<JsonObject> GetRawItems(HttpRequest request)
        {
            return GetRawItems(request, out _, out _);
        }

        private List<JsonObject> GetRawItems(HttpRequest request, out int? totalRecordCount, out int? startIndex)
        {
            var response = _httpClient.Get(request);
            CheckForError(response);
            var root = JsonNode.Parse(response.Content);
            totalRecordCount = GetInteger(root?["TotalRecordCount"]);
            startIndex = GetInteger(root?["StartIndex"]);
            if (root?["Items"] is not JsonArray items)
            {
                throw new InvalidOperationException("Jellyfin did not return an Items collection");
            }

            return items.OfType<JsonObject>().ToList();
        }

        private static JsonObject SelectUniqueItem(List<JsonObject> items, string preferredPath)
        {
            items = items.Where(item => GetString(item["Id"]).IsNotNullOrWhiteSpace())
                .GroupBy(item => GetString(item["Id"]))
                .Select(group => group.First())
                .ToList();
            if (items.Count == 1)
            {
                return items[0];
            }

            var matchingPaths = items.Where(item => preferredPath.IsNotNullOrWhiteSpace() &&
                GetString(item["Path"]).IsNotNullOrWhiteSpace() && GetString(item["Path"]).PathEquals(preferredPath)).ToList();
            return matchingPaths.Count == 1 ? matchingPaths[0] : null;
        }

        private static bool MatchesEpisode(JsonObject item, int season, int episode)
        {
            var itemSeason = GetInteger(item["ParentIndexNumber"]);
            var firstEpisode = GetInteger(item["IndexNumber"]);
            var lastEpisode = GetInteger(item["IndexNumberEnd"]) ?? firstEpisode;
            return GetString(item["Type"]) == "Episode" && itemSeason == season && firstEpisode.HasValue &&
                firstEpisode <= episode && lastEpisode >= episode;
        }

        private static string MapPath(string path, MediaBrowserSettings settings)
        {
            if (path.IsNullOrWhiteSpace())
            {
                return null;
            }

            var mappedPath = new OsPath(path);
            if (settings.MapTo.IsNotNullOrWhiteSpace())
            {
                mappedPath = new OsPath(settings.MapTo) + (mappedPath - new OsPath(settings.MapFrom));
            }

            return mappedPath.ToString();
        }

        private static string GetString(JsonNode node)
        {
            return node is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;
        }

        private static int? GetInteger(JsonNode node)
        {
            return node is JsonValue value && value.TryGetValue<int>(out var result) ? result : null;
        }

        private T ProcessGetRequest<T>(HttpRequest request)
            where T : new()
        {
            var response = _httpClient.Get<T>(request);
            _logger.Trace("Response: {0}", response.Content);

            CheckForError(response);

            return response.Resource;
        }

        private string ProcessRequest(HttpRequest request)
        {
            var response = _httpClient.Post(request);
            _logger.Trace("Response: {0}", response.Content);

            CheckForError(response);

            return response.Content;
        }

        private string GetUrl(MediaBrowserSettings settings)
        {
            var scheme = settings.UseSsl ? "https" : "http";
            return $@"{scheme}://{settings.Address}";
        }

        private HttpRequestBuilder BuildRequest(string path, MediaBrowserSettings settings)
        {
            var url = GetUrl(settings);
            var request = new HttpRequestBuilder(url).Resource(path);

            request.Headers.Add("X-MediaBrowser-Token", settings.ApiKey);
            request.Headers.Add("Authorization", $"MediaBrowser Token=\"{settings.ApiKey}\"");

            return request;
        }

        private void CheckForError(HttpResponse response)
        {
            _logger.Debug("Looking for error in response: {0}", response);

            // TODO: actually check for the error
        }
    }
}
