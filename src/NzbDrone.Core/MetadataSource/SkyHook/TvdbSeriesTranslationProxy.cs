using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Tv.Translations;

namespace NzbDrone.Core.MetadataSource.SkyHook
{
    public interface IFetchSeriesTranslations
    {
        List<SeriesTranslation> GetTranslations(int tvdbId, Language language);
        List<int> SearchSeries(string title, Language language);
    }

    public class TvdbSeriesTranslationProxy : IFetchSeriesTranslations
    {
        private readonly IHttpClient _httpClient;
        private readonly IConfigService _configService;
        private readonly ICached<string> _tokenCache;
        private readonly ICached<List<SeriesTranslation>> _cache;
        private readonly ICached<List<int>> _searchCache;
        private readonly Logger _logger;

        public TvdbSeriesTranslationProxy(IHttpClient httpClient,
                                          IConfigService configService,
                                          ICacheManager cacheManager,
                                          Logger logger)
        {
            _httpClient = httpClient;
            _configService = configService;
            _tokenCache = cacheManager.GetCache<string>(GetType(), "token");
            _cache = cacheManager.GetCache<List<SeriesTranslation>>(GetType(), "translations");
            _searchCache = cacheManager.GetCache<List<int>>(GetType(), "search");
            _logger = logger;
        }

        public List<SeriesTranslation> GetTranslations(int tvdbId, Language language)
        {
            var isoLanguage = IsoLanguages.Get(language);

            if (isoLanguage == null)
            {
                return new List<SeriesTranslation>();
            }

            return _cache.Get($"{tvdbId}-{isoLanguage.ThreeLetterCode}", () => FetchTranslation(tvdbId, isoLanguage.ThreeLetterCode), TimeSpan.FromHours(12));
        }

        public List<int> SearchSeries(string title, Language language)
        {
            var isoLanguage = IsoLanguages.Get(language);

            if (title.IsNullOrWhiteSpace() || isoLanguage == null)
            {
                return new List<int>();
            }

            return _searchCache.Get($"{isoLanguage.ThreeLetterCode}-{title.ToLowerInvariant()}", () => FetchSearch(title, isoLanguage.ThreeLetterCode), TimeSpan.FromHours(12));
        }

        private List<SeriesTranslation> FetchTranslation(int tvdbId, string languageCode)
        {
            var token = GetToken();

            if (token.IsNullOrWhiteSpace())
            {
                return new List<SeriesTranslation>();
            }

            var response = GetTranslationResponse(tvdbId, languageCode, token);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _tokenCache.Remove("token");
                token = GetToken();

                if (token.IsNotNullOrWhiteSpace())
                {
                    response = GetTranslationResponse(tvdbId, languageCode, token);
                }
            }

            if (response.HasHttpError)
            {
                _logger.Debug("Unable to fetch TheTVDB translation for tvdbid {0} language {1}: HTTP {2}", tvdbId, languageCode, response.StatusCode);
                return new List<SeriesTranslation>();
            }

            var translation = response.Resource?.Data;

            if (translation == null || translation.Name.IsNullOrWhiteSpace())
            {
                return new List<SeriesTranslation>();
            }

            var title = WebUtility.HtmlDecode(translation.Name);

            return new List<SeriesTranslation>
            {
                new SeriesTranslation
                {
                    Title = title,
                    CleanTitle = Parser.Parser.CleanSeriesTitle(title),
                    Overview = WebUtility.HtmlDecode(translation.Overview),
                    Language = IsoLanguages.Find(translation.Language)?.Language
                }
            };
        }

        private List<int> FetchSearch(string title, string languageCode)
        {
            var token = GetToken();

            if (token.IsNullOrWhiteSpace())
            {
                return new List<int>();
            }

            var response = GetSearchResponse(title, languageCode, token);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _tokenCache.Remove("token");
                token = GetToken();

                if (token.IsNotNullOrWhiteSpace())
                {
                    response = GetSearchResponse(title, languageCode, token);
                }
            }

            if (response.HasHttpError)
            {
                _logger.Debug("Unable to search TheTVDB for series title {0} language {1}: HTTP {2}", title, languageCode, response.StatusCode);
                return new List<int>();
            }

            return response.Resource?.Data?
                .Where(r => r.Type.IsNullOrWhiteSpace() || r.Type.Equals("series", StringComparison.InvariantCultureIgnoreCase))
                .Select(r => int.TryParse(r.TvdbId, out var tvdbId) ? tvdbId : 0)
                .Where(tvdbId => tvdbId > 0)
                .Distinct()
                .ToList() ?? new List<int>();
        }

        private HttpResponse<TvdbApiResponse<TvdbTranslationResource>> GetTranslationResponse(int tvdbId, string languageCode, string token)
        {
            var request = new HttpRequestBuilder("https://api4.thetvdb.com/v4/")
                .Resource($"series/{tvdbId}/translations/{languageCode}")
                .Accept(HttpAccept.Json)
                .SetHeader("Authorization", $"Bearer {token}")
                .Build();

            request.SuppressHttpError = true;

            return _httpClient.Get<TvdbApiResponse<TvdbTranslationResource>>(request);
        }

        private HttpResponse<TvdbApiResponse<List<TvdbSearchResource>>> GetSearchResponse(string title, string languageCode, string token)
        {
            var request = new HttpRequestBuilder("https://api4.thetvdb.com/v4/")
                .Resource("search")
                .AddQueryParam("query", title)
                .AddQueryParam("type", "series")
                .AddQueryParam("language", languageCode)
                .Accept(HttpAccept.Json)
                .SetHeader("Authorization", $"Bearer {token}")
                .Build();

            request.SuppressHttpError = true;

            return _httpClient.Get<TvdbApiResponse<List<TvdbSearchResource>>>(request);
        }

        private string GetToken()
        {
            if (_configService.TheTvdbApiKey.IsNullOrWhiteSpace())
            {
                _logger.Debug("TheTVDB API key is not configured, skipping series translations");
                return null;
            }

            var token = _tokenCache.Find("token");

            if (token.IsNotNullOrWhiteSpace())
            {
                return token;
            }

            token = Login();

            if (token.IsNotNullOrWhiteSpace())
            {
                _tokenCache.Set("token", token, TimeSpan.FromDays(25));
            }

            return token;
        }

        private string Login()
        {
            var payload = new Dictionary<string, string>
            {
                { "apikey", _configService.TheTvdbApiKey }
            };

            var request = new HttpRequestBuilder("https://api4.thetvdb.com/v4/")
                .Resource("login")
                .Accept(HttpAccept.Json)
                .Post()
                .Build();

            request.Headers.ContentType = "application/json";
            request.SuppressHttpError = true;
            request.SetContent(payload.ToJson());

            var response = _httpClient.Post<TvdbApiResponse<TvdbLoginResource>>(request);

            if (response.HasHttpError || response.Resource?.Data == null || response.Resource.Data.Token.IsNullOrWhiteSpace())
            {
                _logger.Warn("Unable to authenticate with TheTVDB API. Check the configured API key.");
                return null;
            }

            return response.Resource.Data.Token;
        }

        public class TvdbApiResponse<T>
            where T : new()
        {
            public T Data { get; set; }
        }

        public class TvdbLoginResource
        {
            public string Token { get; set; }
        }

        public class TvdbTranslationResource
        {
            public string Name { get; set; }
            public string Overview { get; set; }
            public string Language { get; set; }
        }

        public class TvdbSearchResource
        {
            [JsonProperty("tvdb_id")]
            public string TvdbId { get; set; }

            public string Type { get; set; }
        }
    }
}
