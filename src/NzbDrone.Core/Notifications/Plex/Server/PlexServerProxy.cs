using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Notifications.Plex.Server
{
    public interface IPlexServerProxy
    {
        List<PlexSection> GetTvSections(PlexServerSettings settings);
        string Version(PlexServerSettings settings);
        void Update(int sectionId, string path, PlexServerSettings settings);
        void MarkAsRecentlyAdded(PlexServerSettings settings, DownloadMessage message);
    }

    public class PlexServerProxy : IPlexServerProxy
    {
        private readonly IHttpClient _httpClient;
        private readonly IConfigService _configService;
        private readonly IMediaServerItemLinkStore _itemLinks;
        private readonly Logger _logger;

        public PlexServerProxy(IHttpClient httpClient, IConfigService configService, IMediaServerItemLinkStore itemLinks, Logger logger)
        {
            _httpClient = httpClient;
            _itemLinks = itemLinks;
            _configService = configService;
            _logger = logger;
        }

        public List<PlexSection> GetTvSections(PlexServerSettings settings)
        {
            var request = BuildRequest("library/sections", HttpMethod.Get, settings);
            var response = ProcessRequest(request);

            CheckForError(response);

            if (response.Contains("_children"))
            {
                return Json.Deserialize<PlexMediaContainerLegacy>(response)
                    .Sections
                    .Where(d => d.Type == "show")
                    .Select(s => new PlexSection
                                 {
                                     Id = s.Id,
                                     Language = s.Language,
                                     Agent = s.Agent,
                                     Locations = s.Locations,
                                     Type = s.Type
                                 })
                    .ToList();
            }

            return Json.Deserialize<PlexResponse<PlexSectionsContainer>>(response)
                       .MediaContainer
                       .Sections
                       .Where(d => d.Type == "show")
                       .ToList();
        }

        public void Update(int sectionId, string path, PlexServerSettings settings)
        {
            var resource = $"library/sections/{sectionId}/refresh";
            var request = BuildRequest(resource, HttpMethod.Get, settings);

            request.AddQueryParam("path", path);

            var response = ProcessRequest(request);

            CheckForError(response);
        }

        public void MarkAsRecentlyAdded(PlexServerSettings settings, DownloadMessage message)
        {
            MarkAsRecentlyAdded(settings, message, true);
        }

        private void MarkAsRecentlyAdded(PlexServerSettings settings, DownloadMessage message, bool retryMissingLink)
        {
            var series = message.Series;
            var episodes = message.EpisodeInfo?.Episodes?.DistinctBy(episode => (episode.SeasonNumber, episode.EpisodeNumber)).ToList();
            if (series == null || episodes?.Any() != true)
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

            var cachedSeries = _itemLinks.TryGet(seriesKey, out var seriesLinks);
            if (cachedSeries)
            {
                try
                {
                    cachedSeries = ValidateLinks(settings, series, episodes, episodeKeys, seriesLinks, episodeLinks);
                }
                catch (PlexItemNotFoundException)
                {
                    cachedSeries = false;
                }

                if (!cachedSeries)
                {
                    _itemLinks.Remove(seriesKey);
                    foreach (var key in episodeKeys.Values)
                    {
                        _itemLinks.Remove(key);
                    }

                    episodeLinks.Clear();
                }
            }

            if (!cachedSeries)
            {
                episodeLinks.Clear();
                foreach (var key in episodeKeys.Values)
                {
                    _itemLinks.Remove(key);
                }

                seriesLinks = DiscoverSeriesLinks(settings, series);
                if (seriesLinks.Count == 0)
                {
                    _itemLinks.SetNegative(seriesKey);
                    return;
                }

                _itemLinks.Set(seriesKey, seriesLinks);
            }

            var missing = episodes.Where(episode => !episodeLinks.ContainsKey(episodeKeys[(episode.SeasonNumber, episode.EpisodeNumber)]) &&
                !_itemLinks.IsNegative(episodeKeys[(episode.SeasonNumber, episode.EpisodeNumber)])).ToList();
            if (missing.Count > 0)
            {
                var leavesBySeries = seriesLinks.ToDictionary(link => link.ItemId, link => GetMetadataPages($"library/metadata/{link.ItemId}/allLeaves", settings));
                foreach (var episode in missing)
                {
                    var links = new List<MediaServerItemLink>();
                    foreach (var seriesLink in seriesLinks)
                    {
                        var matches = leavesBySeries[seriesLink.ItemId].Where(item => MatchesEpisode(item, episode))
                            .DistinctBy(item => item["ratingKey"]?.ToString()).ToList();
                        if (matches.Count == 1 && TryRatingKey(matches[0], out var episodeId))
                        {
                            links.Add(new MediaServerItemLink { SectionId = seriesLink.SectionId, ItemId = episodeId });
                        }
                    }

                    var key = episodeKeys[(episode.SeasonNumber, episode.EpisodeNumber)];
                    if (links.Count > 0)
                    {
                        episodeLinks[key] = links;
                        _itemLinks.Set(key, links);
                    }
                    else
                    {
                        _itemLinks.SetNegative(key);
                    }
                }
            }

            var addedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var section in episodeLinks.Values.SelectMany(links => links).GroupBy(link => link.SectionId.Value))
            {
                var ids = section.Select(link => link.ItemId).Distinct();
                var request = BuildRequest($"library/sections/{section.Key}/all", HttpMethod.Put, settings)
                    .AddQueryParam("type", 4)
                    .AddQueryParam("id", string.Join(",", ids))
                    .AddQueryParam("addedAt.value", addedAt);
                try
                {
                    CheckForError(ProcessRequest(request));
                }
                catch (PlexItemNotFoundException)
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

                    throw;
                }
            }
        }

        private bool ValidateLinks(
            PlexServerSettings settings,
            Series series,
            List<Episode> episodes,
            Dictionary<(int SeasonNumber, int EpisodeNumber), string> episodeKeys,
            List<MediaServerItemLink> seriesLinks,
            Dictionary<string, List<MediaServerItemLink>> episodeLinks)
        {
            var links = seriesLinks.Concat(episodeLinks.Values.SelectMany(items => items)).ToList();
            if (seriesLinks.Count == 0 || links.Any(link => link.SectionId is not > 0 || !long.TryParse(link.ItemId, out var id) || id <= 0))
            {
                return false;
            }

            var metadata = GetMetadataPages($"library/metadata/{string.Join(",", links.Select(link => link.ItemId).Distinct())}", settings);
            foreach (var link in seriesLinks)
            {
                var matches = metadata.Where(item => item["ratingKey"]?.ToString() == link.ItemId).ToList();
                if (matches.Count != 1 || matches[0]["type"]?.ToString() != "show" || !MatchesSeries(matches[0], series) ||
                    ReadInt(matches[0]["librarySectionID"], -1) != link.SectionId)
                {
                    return false;
                }
            }

            foreach (var episode in episodes)
            {
                if (!episodeLinks.TryGetValue(episodeKeys[(episode.SeasonNumber, episode.EpisodeNumber)], out var cached))
                {
                    continue;
                }

                if (cached.Count == 0)
                {
                    return false;
                }

                foreach (var link in cached)
                {
                    var matches = metadata.Where(item => item["ratingKey"]?.ToString() == link.ItemId).ToList();
                    var parentId = matches.Count == 1 ? matches[0]["grandparentRatingKey"]?.ToString() : null;
                    var parent = seriesLinks.SingleOrDefault(item => item.SectionId == link.SectionId && item.ItemId == parentId);
                    if (parent == null || matches.Count != 1 || !MatchesEpisode(matches[0], episode) ||
                        matches[0]["grandparentRatingKey"]?.ToString() != parent.ItemId || ReadInt(matches[0]["librarySectionID"], -1) != link.SectionId)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private List<MediaServerItemLink> DiscoverSeriesLinks(PlexServerSettings settings, Series series)
        {
            var title = series.TvdbId > 0 ? $"tvdb-{series.TvdbId}" :
                series.ImdbId.IsNotNullOrWhiteSpace() ? $"imdb-{series.ImdbId}" :
                series.TmdbId > 0 ? $"tmdb-{series.TmdbId}" : null;
            if (title == null)
            {
                return new List<MediaServerItemLink>();
            }

            var sections = GetTvSections(settings);
            foreach (var section in sections.Where(section => section.Id > 0 && section.Agent.IsNotNullOrWhiteSpace()).Take(20))
            {
                var dummyRequest = BuildRequest($"library/sections/{section.Id}/all", HttpMethod.Get, settings)
                    .AddQueryParam("type", 2)
                    .SetHeader("X-Plex-Container-Start", "0")
                    .SetHeader("X-Plex-Container-Size", "1");
                var response = ProcessRequest(dummyRequest);
                CheckForError(response);
                var root = JsonNode.Parse(response);
                var container = root?["MediaContainer"] ?? root;
                var items = container?["Metadata"] as JsonArray ?? container?["_children"] as JsonArray;
                if (items?.Count > 1 || ReadInt(container?["size"], 0) > 1)
                {
                    throw new PlexException("Plex ignored the bounded sample request");
                }

                var dummy = items?.OfType<JsonObject>().FirstOrDefault(item => item["type"]?.ToString() == "show" && TryRatingKey(item, out _));
                if (dummy == null)
                {
                    continue;
                }

                var matchRequest = BuildRequest($"library/metadata/{dummy["ratingKey"]}/matches", HttpMethod.Get, settings)
                    .AddQueryParam("manual", 1)
                    .AddQueryParam("agent", section.Agent)
                    .AddQueryParam("title", title)
                    .AddQueryParam("year", ReadInt(dummy["year"], 0))
                    .AddQueryParam("language", section.Language.IsNullOrWhiteSpace() ? "en" : section.Language);
                response = ProcessRequest(matchRequest);
                CheckForError(response);
                root = JsonNode.Parse(response);
                container = root?["MediaContainer"] ?? root;
                var candidates = container?["SearchResult"] as JsonArray ?? container?["Metadata"] as JsonArray ?? container?["_children"] as JsonArray;
                var guids = candidates?.OfType<JsonObject>().Where(item => item["type"]?.ToString() == "show").Select(item => item["guid"]?.ToString())
                    .Where(guid => guid?.StartsWith("plex://show/", StringComparison.Ordinal) == true)
                    .Distinct().Take(20).ToList() ?? new List<string>();
                var links = new List<MediaServerItemLink>();
                foreach (var guid in guids)
                {
                    links.AddRange(GetShowsByGuid(settings, series, sections, guid));
                }

                return links.DistinctBy(link => (link.SectionId, link.ItemId)).ToList();
            }

            return new List<MediaServerItemLink>();
        }

        private List<MediaServerItemLink> GetShowsByGuid(PlexServerSettings settings, Series series, List<PlexSection> sections, string guid)
        {
            const int pageSize = 200;
            const int maxResults = 1000;
            var start = 0;
            var seen = new HashSet<string>();
            var links = new List<MediaServerItemLink>();
            while (start < maxResults)
            {
                var request = BuildRequest("library/all", HttpMethod.Get, settings)
                    .AddQueryParam("type", 2)
                    .AddQueryParam("guid", guid)
                    .AddQueryParam("includeGuids", 1)
                    .SetHeader("X-Plex-Container-Start", start.ToString(CultureInfo.InvariantCulture))
                    .SetHeader("X-Plex-Container-Size", pageSize.ToString(CultureInfo.InvariantCulture));
                var response = ProcessRequest(request);
                CheckForError(response);
                var root = JsonNode.Parse(response);
                var container = root?["MediaContainer"] ?? root;
                var metadata = container?["Metadata"] as JsonArray ?? container?["_children"] as JsonArray;
                var total = ReadInt(container?["totalSize"], -1);
                var size = ReadInt(container?["size"], metadata?.Count ?? 0);
                if (total > maxResults || size > pageSize || (metadata == null && size > 0))
                {
                    throw new PlexException("Plex returned an unbounded GUID lookup result");
                }

                if (metadata == null || metadata.Count == 0)
                {
                    break;
                }

                if (ReadInt(container?["offset"], start) != start || metadata.Count > pageSize || size != metadata.Count || metadata.Any(item => item is not JsonObject))
                {
                    throw new PlexException("Plex returned invalid GUID lookup pagination");
                }

                foreach (var item in metadata.OfType<JsonObject>())
                {
                    if (item["guid"]?.ToString() != guid)
                    {
                        throw new PlexException("Plex ignored the exact GUID lookup filter");
                    }

                    if (!TryRatingKey(item, out var id) || !seen.Add(id))
                    {
                        throw new PlexException("Plex returned invalid or repeated GUID lookup metadata");
                    }

                    var sectionId = ReadInt(item["librarySectionID"], -1);
                    if (item["type"]?.ToString() == "show" && MatchesSeries(item, series) && sectionId > 0 && sections.Any(section => section.Id == sectionId))
                    {
                        links.Add(new MediaServerItemLink { SectionId = sectionId, ItemId = id });
                    }
                }

                start += metadata.Count;
                if ((total >= 0 && start >= total) || (total < 0 && metadata.Count < pageSize))
                {
                    return links;
                }
            }

            if (start >= maxResults)
            {
                throw new PlexException("Plex GUID lookup exceeded its bounded page limit");
            }

            return links;
        }

        private static bool MatchesEpisode(JsonObject item, Episode episode)
        {
            return item["type"]?.ToString() == "episode" && ReadInt(item["parentIndex"], -1) == episode.SeasonNumber &&
                ReadInt(item["index"], -1) == episode.EpisodeNumber;
        }

        private static string GetLinkKey(PlexServerSettings settings, Series series, Episode episode = null)
        {
            var url = $"{(settings.UseSsl ? "https" : "http")}://{settings.Host.ToUrlHost()}:{settings.Port}{settings.UrlBase}";
            var identity = new[]
            {
                series.TvdbId.ToString(CultureInfo.InvariantCulture), series.ImdbId ?? string.Empty, series.TmdbId.ToString(CultureInfo.InvariantCulture),
                episode == null ? "series" : $"episode:{episode.SeasonNumber}:{episode.EpisodeNumber}"
            };
            return MediaServerItemLinkKey.Create("plex", url, settings.AuthToken, settings.MapFrom, settings.MapTo, identity);
        }

        private List<JsonObject> GetMetadataPages(string resource, PlexServerSettings settings, int? type = null, int? year = null)
        {
            const int pageSize = 200;
            var offset = 0;
            var results = new List<JsonObject>();
            var seen = new HashSet<string>();

            while (true)
            {
                var request = BuildRequest(resource, HttpMethod.Get, settings)
                    .AddQueryParam("includeGuids", 1)
                    .SetHeader("X-Plex-Container-Start", offset.ToString(CultureInfo.InvariantCulture))
                    .SetHeader("X-Plex-Container-Size", pageSize.ToString(CultureInfo.InvariantCulture));

                if (type.HasValue)
                {
                    request.AddQueryParam("type", type.Value);
                }

                if (year.HasValue)
                {
                    request.AddQueryParam("year", year.Value);
                }

                var response = ProcessRequest(request);
                CheckForError(response);
                var root = JsonNode.Parse(response) as JsonObject;
                var container = root?["MediaContainer"] as JsonObject ?? root;
                var metadata = container?["Metadata"] as JsonArray ?? container?["_children"] as JsonArray;

                if (metadata == null)
                {
                    throw new PlexException("Plex returned no metadata collection for {0}", resource);
                }

                var page = metadata.OfType<JsonObject>().ToList();

                if (page.Count == 0)
                {
                    break;
                }

                var newItems = page.Where(item => seen.Add(item["ratingKey"]?.ToString() ?? item.ToJsonString())).ToList();

                if (newItems.Count == 0)
                {
                    throw new PlexException("Plex repeated a metadata page for {0}", resource);
                }

                results.AddRange(newItems);
                var actualOffset = ReadInt(container["offset"], offset);
                var size = ReadInt(container["size"], page.Count);
                var total = ReadInt(container["totalSize"], -1);
                var next = actualOffset + size;

                if (actualOffset != offset || next <= offset)
                {
                    throw new PlexException("Plex returned invalid metadata pagination for {0}", resource);
                }

                if ((total >= 0 && next >= total) || (total < 0 && page.Count < pageSize))
                {
                    break;
                }

                offset = next;
            }

            return results;
        }

        private static bool MatchesSeries(JsonObject item, Series series)
        {
            var guids = (item["Guid"] as JsonArray)?.OfType<JsonObject>()
                .Select(g => g["id"]?.ToString()) ?? Enumerable.Empty<string>();
            guids = guids.Append(item["guid"]?.ToString());

            return guids.Any(guid => MatchesGuid(guid, "tvdb", "thetvdb", series.TvdbId > 0 ? series.TvdbId.ToString(CultureInfo.InvariantCulture) : null) ||
                                     MatchesGuid(guid, "imdb", "imdb", series.ImdbId) ||
                                     MatchesGuid(guid, "tmdb", "themoviedb", series.TmdbId > 0 ? series.TmdbId.ToString(CultureInfo.InvariantCulture) : null));
        }

        private static bool MatchesGuid(string guid, string provider, string legacyProvider, string id)
        {
            if (guid.IsNullOrWhiteSpace() || id.IsNullOrWhiteSpace())
            {
                return false;
            }

            return guid.Equals($"{provider}://{id}", StringComparison.OrdinalIgnoreCase) ||
                   Regex.IsMatch(guid, $"^com\\.plexapp\\.agents\\.{Regex.Escape(legacyProvider)}://{Regex.Escape(id)}(?:[/?]|$)", RegexOptions.IgnoreCase);
        }

        private static int ReadInt(JsonNode value, int fallback)
        {
            return int.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : fallback;
        }

        private static bool TryRatingKey(JsonObject item, out string key)
        {
            key = item["ratingKey"]?.ToString();
            return long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0;
        }

        public string Version(PlexServerSettings settings)
        {
            var request = BuildRequest("identity", HttpMethod.Get, settings);
            var response = ProcessRequest(request);

            CheckForError(response);

            if (response.Contains("_children"))
            {
                return Json.Deserialize<PlexIdentity>(response)
                           .Version;
            }

            return Json.Deserialize<PlexResponse<PlexIdentity>>(response)
                       .MediaContainer
                       .Version;
        }

        private HttpRequestBuilder BuildRequest(string resource, HttpMethod method, PlexServerSettings settings)
        {
            var scheme = settings.UseSsl ? "https" : "http";

            var requestBuilder = new HttpRequestBuilder($"{scheme}://{settings.Host.ToUrlHost()}:{settings.Port}{settings.UrlBase}")
                                 .Accept(HttpAccept.Json)
                                 .AddQueryParam("X-Plex-Client-Identifier", _configService.PlexClientIdentifier)
                                 .AddQueryParam("X-Plex-Product", BuildInfo.AppName)
                                 .AddQueryParam("X-Plex-Platform", "Windows")
                                 .AddQueryParam("X-Plex-Platform-Version", "7")
                                 .AddQueryParam("X-Plex-Device-Name", BuildInfo.AppName)
                                 .AddQueryParam("X-Plex-Version", BuildInfo.Version.ToString());

            if (settings.AuthToken.IsNotNullOrWhiteSpace())
            {
                requestBuilder.AddQueryParam("X-Plex-Token", settings.AuthToken);
            }

            requestBuilder.ResourceUrl = resource;
            requestBuilder.Method = method;

            return requestBuilder;
        }

        private string ProcessRequest(HttpRequestBuilder requestBuilder)
        {
            var httpRequest = requestBuilder.Build();

            HttpResponse response;

            _logger.Debug("Url: {0}", httpRequest.Url);

            try
            {
                response = _httpClient.Execute(httpRequest);
            }
            catch (HttpException ex)
            {
                if (ex.Response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new PlexAuthenticationException("Unauthorized - AuthToken is invalid");
                }

                if (ex.Response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new PlexItemNotFoundException();
                }

                throw new PlexException("Unable to connect to Plex Media Server. Status Code: {0}", ex.Response.StatusCode);
            }
            catch (WebException ex)
            {
                if (ex.Status == WebExceptionStatus.TrustFailure)
                {
                    throw new PlexException("Unable to connect to Plex Media Server, certificate validation failed.", ex);
                }

                throw new PlexException($"Unable to connect to Plex Media Server, {ex.Message}", ex);
            }

            return response.Content;
        }

        private sealed class PlexItemNotFoundException : PlexException
        {
            public PlexItemNotFoundException()
                : base("Plex item no longer exists")
            {
            }
        }

        private void CheckForError(string response)
        {
            _logger.Trace("Checking for error");

            if (response.IsNullOrWhiteSpace())
            {
                _logger.Trace("No response body returned, no error detected");
                return;
            }

            var error = response.Contains("_children") ?
                        Json.Deserialize<PlexError>(response) :
                        Json.Deserialize<PlexResponse<PlexError>>(response).MediaContainer;

            if (error != null && !error.Error.IsNullOrWhiteSpace())
            {
                throw new PlexException(error.Error);
            }

            _logger.Trace("No error detected");
        }
    }
}
