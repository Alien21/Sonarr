using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Web;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.Plex;
using NzbDrone.Core.Notifications.Plex.Server;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.NotificationTests.Plex
{
    [TestFixture]
    public class RecentlyAddedPlexProxyFixture : CoreTest<PlexServerProxy>
    {
        private List<HttpRequest> _requests;
        private List<JsonObject> _shows;
        private List<JsonObject> _episodes;
        private PlexServerSettings _settings;
        private DownloadMessage _message;
        private int _pageSize;
        private int _sectionCount;
        private bool _repeatPage;
        private bool _globalMiss;
        private bool _legacy;
        private HttpStatusCode? _cachedStatus;
        private bool _cachedMismatch;
        private bool _writeNotFound;
        private List<JsonObject> _matchCandidates;
        private HashSet<int> _emptySections;
        private bool _ignoredGuidFilter;
        private int? _globalTotal;

        [SetUp]
        public void Setup()
        {
            Mocker.GetMock<IConfigService>().SetupGet(config => config.PlexClientIdentifier).Returns("test-client");
            Mocker.SetConstant<IMediaServerItemLinkStore>(new MediaServerItemLinkStoreStub());
            _cachedStatus = null;
            _cachedMismatch = false;
            _writeNotFound = false;
            _matchCandidates = new List<JsonObject> { new JsonObject { ["type"] = "show", ["guid"] = "plex://show/modern-id" } };
            _emptySections = new HashSet<int>();
            _ignoredGuidFilter = false;
            _globalTotal = null;
            _requests = new List<HttpRequest>();
            _shows = new List<JsonObject> { Show("10", "tvdb://433384") };
            _episodes = new List<JsonObject> { EpisodeMetadata("20", 2) };
            _pageSize = 200;
            _sectionCount = 1;
            _repeatPage = false;
            _globalMiss = false;
            _legacy = false;
            _settings = new PlexServerSettings { Host = "plex.local", Port = 32400, UseSsl = true, UrlBase = "/plex", AuthToken = "test-token" };
            _message = new DownloadMessage
            {
                Series = new Series { TvdbId = 433384, Year = 2020, Title = "Localized series title" },
                EpisodeInfo = new LocalEpisode { Episodes = new List<Episode> { new Episode { SeasonNumber = 1, EpisodeNumber = 2 } } },
                EpisodeFile = new EpisodeFile { Path = "/media/Series/new-quality-name.mkv" },
                OldFiles = new List<DeletedEpisodeFile> { new DeletedEpisodeFile(new EpisodeFile { Path = "/media/Series/old-quality-name.mkv" }, null) }
            };
            Mocker.GetMock<IHttpClient>().Setup(c => c.Execute(It.IsAny<HttpRequest>()))
                .Returns<HttpRequest>(Respond);
        }

        private HttpResponse Respond(HttpRequest request)
        {
            _requests.Add(request);
            string response;
            var uri = new Uri(request.Url.FullUri);
            var query = HttpUtility.ParseQueryString(uri.Query);

            if (request.Method == HttpMethod.Put)
            {
                if (_writeNotFound)
                {
                    _writeNotFound = false;
                    throw new HttpException(request, new HttpResponse(request, new HttpHeader(), string.Empty, HttpStatusCode.NotFound));
                }

                response = string.Empty;
            }
            else if (uri.AbsolutePath.EndsWith("/matches", StringComparison.Ordinal))
            {
                var container = new JsonObject
                {
                    [_legacy ? "_children" : "SearchResult"] = new JsonArray(_matchCandidates.Select(item => item.DeepClone()).ToArray())
                };
                response = _legacy ? container.ToJsonString() : new JsonObject { ["MediaContainer"] = container }.ToJsonString();
            }
            else if (uri.AbsolutePath.StartsWith("/plex/library/metadata/", StringComparison.Ordinal) && !uri.AbsolutePath.Contains("allLeaves", StringComparison.Ordinal))
            {
                if (_cachedStatus.HasValue)
                {
                    var status = _cachedStatus.Value;
                    _cachedStatus = null;
                    throw new HttpException(request, new HttpResponse(request, new HttpHeader(), string.Empty, status));
                }

                var ids = Uri.UnescapeDataString(uri.Segments.Last()).Split(',');
                var items = _shows.Concat(_episodes).Where(item => ids.Contains(item["ratingKey"].ToString()))
                    .Select(item => item.DeepClone().AsObject()).ToList();
                if (_cachedMismatch)
                {
                    foreach (var item in items.Where(item => item["type"].ToString() == "episode"))
                    {
                        item["grandparentRatingKey"] = "999";
                    }
                }

                response = new JsonObject { ["MediaContainer"] = new JsonObject { ["Metadata"] = new JsonArray(items.ToArray()), ["size"] = items.Count } }.ToJsonString();
            }
            else if (uri.AbsolutePath == "/plex/library/sections")
            {
                var sections = Enumerable.Range(1, _sectionCount).Select(section => (JsonNode)new JsonObject
                {
                    ["key"] = section,
                    ["type"] = "show",
                    ["agent"] = "tv.plex.agents.series",
                    ["language"] = "cs-CZ",
                    [_legacy ? "_children" : "Location"] = new JsonArray()
                }).ToArray();
                response = _legacy ? new JsonObject { ["_children"] = new JsonArray(sections) }.ToJsonString() :
                    new JsonObject { ["MediaContainer"] = new JsonObject { ["Directory"] = new JsonArray(sections) } }.ToJsonString();
            }
            else if (uri.AbsolutePath.StartsWith("/plex/library/sections/", StringComparison.Ordinal))
            {
                var section = int.Parse(uri.Segments[^2].TrimEnd('/'));
                var items = _emptySections.Contains(section) ? new JsonArray() :
                    new JsonArray(new JsonObject { ["ratingKey"] = "900", ["type"] = "show", ["year"] = 1998 });
                var container = new JsonObject { [_legacy ? "_children" : "Metadata"] = items, ["size"] = items.Count };
                response = _legacy ? container.ToJsonString() : new JsonObject { ["MediaContainer"] = container }.ToJsonString();
            }
            else
            {
                var offset = int.Parse(request.Headers["X-Plex-Container-Start"]);
                var leaves = uri.AbsolutePath.Contains("allLeaves", StringComparison.Ordinal);
                var parentId = leaves ? uri.Segments[^2].TrimEnd('/') : null;
                var items = leaves ? _episodes.Where(item => item["grandparentRatingKey"].ToString() == parentId).ToList() :
                    _globalMiss ? new List<JsonObject>() : _shows.Where(item => _ignoredGuidFilter || item["guid"]?.ToString() == query["guid"]).ToList();
                var actualOffset = _repeatPage ? 0 : offset;
                var page = items.Skip(actualOffset).Take(_pageSize).ToList();
                var container = new JsonObject
                {
                    ["offset"] = actualOffset,
                    ["size"] = page.Count,
                    ["totalSize"] = leaves ? items.Count : _globalTotal ?? items.Count,
                    [_legacy ? "_children" : "Metadata"] = new JsonArray(page.Select(item => item.DeepClone()).ToArray())
                };
                response = _legacy ? container.ToJsonString() : new JsonObject { ["MediaContainer"] = container }.ToJsonString();
            }

            return new HttpResponse(request, new HttpHeader(), response);
        }

        private static JsonObject Show(string id, string guid, int section = 1)
        {
            return new JsonObject
            {
                ["ratingKey"] = id,
                ["type"] = "show",
                ["librarySectionID"] = section,
                ["guid"] = "plex://show/modern-id",
                ["Guid"] = new JsonArray(new JsonObject { ["id"] = guid })
            };
        }

        private static JsonObject EpisodeMetadata(string id, int number, int season = 1, string parent = "10", int section = 1)
        {
            return new JsonObject { ["ratingKey"] = id, ["type"] = "episode", ["parentIndex"] = season, ["index"] = number, ["grandparentRatingKey"] = parent, ["librarySectionID"] = section };
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_reuse_persisted_identity_without_library_or_episode_discovery(bool missingYear)
        {
            Subject.MarkAsRecentlyAdded(_settings, _message);
            _requests.Clear();
            _message.EpisodeFile.Path = "/media/Series/another-replacement-name.mkv";
            _message.Series.Year = missingYear ? 0 : 2020;

            Subject.MarkAsRecentlyAdded(_settings, _message);

            _requests.Should().HaveCount(2);
            _requests[0].Method.Should().Be(HttpMethod.Get);
            new Uri(_requests[0].Url.FullUri).AbsolutePath.Should().Be("/plex/library/metadata/10,20");
            _requests[1].Method.Should().Be(HttpMethod.Put);
        }

        [Test]
        public void should_reuse_series_identity_for_another_episode()
        {
            Subject.MarkAsRecentlyAdded(_settings, _message);
            _requests.Clear();
            _message.EpisodeInfo.Episodes = new List<Episode> { new Episode { SeasonNumber = 1, EpisodeNumber = 3 } };
            _episodes.Add(EpisodeMetadata("21", 3));

            Subject.MarkAsRecentlyAdded(_settings, _message);

            _requests.Should().HaveCount(3);
            _requests.Should().NotContain(request => new Uri(request.Url.FullUri).AbsolutePath == "/plex/library/sections");
            HttpUtility.ParseQueryString(new Uri(_requests.Last().Url.FullUri).Query)["id"].Should().Be("21");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_replace_stale_binding_with_new_guids_and_item_ids(bool notFound)
        {
            Subject.MarkAsRecentlyAdded(_settings, _message);
            _requests.Clear();
            _cachedStatus = notFound ? HttpStatusCode.NotFound : null;
            _shows[0] = Show("12", "tvdb://433384");
            _shows[0]["guid"] = "plex://show/replacement";
            _episodes[0] = EpisodeMetadata("22", 2, parent: "12");
            _matchCandidates[0]["guid"] = "plex://show/replacement";

            Subject.MarkAsRecentlyAdded(_settings, _message);

            _requests.Count(request => new Uri(request.Url.FullUri).AbsolutePath == "/plex/library/sections").Should().Be(1);
            HttpUtility.ParseQueryString(new Uri(_requests.Single(request => request.Method == HttpMethod.Put).Url.FullUri).Query)["id"].Should().Be("22");
            _requests.Clear();
            Subject.MarkAsRecentlyAdded(_settings, _message);
            _requests.Should().HaveCount(2);
            new Uri(_requests[0].Url.FullUri).AbsolutePath.Should().Be("/plex/library/metadata/12,22");
            _requests[1].Method.Should().Be(HttpMethod.Put);
        }

        [Test]
        public void should_rediscover_if_a_cached_episode_belongs_to_another_parent()
        {
            Subject.MarkAsRecentlyAdded(_settings, _message);
            _requests.Clear();
            _cachedMismatch = true;

            Subject.MarkAsRecentlyAdded(_settings, _message);

            _requests.Count(request => new Uri(request.Url.FullUri).AbsolutePath == "/plex/library/sections").Should().Be(1);
            _requests.Count(request => request.Method == HttpMethod.Put).Should().Be(1);
        }

        [TestCase(HttpStatusCode.Unauthorized)]
        [TestCase(HttpStatusCode.InternalServerError)]
        public void should_not_discover_or_write_after_cached_authentication_or_server_failure(HttpStatusCode status)
        {
            Subject.MarkAsRecentlyAdded(_settings, _message);
            _requests.Clear();
            _cachedStatus = status;

            Assert.Catch<PlexException>(() => Subject.MarkAsRecentlyAdded(_settings, _message));

            _requests.Should().ContainSingle();
            _requests[0].Method.Should().Be(HttpMethod.Get);
        }

        [Test]
        public void should_rediscover_once_if_cached_episode_disappears_before_write()
        {
            Subject.MarkAsRecentlyAdded(_settings, _message);
            _requests.Clear();
            _writeNotFound = true;

            Subject.MarkAsRecentlyAdded(_settings, _message);

            _requests.Count(request => new Uri(request.Url.FullUri).AbsolutePath == "/plex/library/sections").Should().Be(1);
            _requests.Count(request => request.Method == HttpMethod.Put).Should().Be(2);
        }

        [Test]
        public void should_invalidate_connection_identity_when_credentials_change()
        {
            Subject.MarkAsRecentlyAdded(_settings, _message);
            _requests.Clear();
            _settings.AuthToken = "changed-token";

            Subject.MarkAsRecentlyAdded(_settings, _message);

            _requests.Count(request => new Uri(request.Url.FullUri).AbsolutePath == "/plex/library/sections").Should().Be(1);
        }

        [Test]
        public void should_edit_only_the_imported_episode_added_date_before_any_refresh()
        {
            var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            Subject.MarkAsRecentlyAdded(_settings, _message);

            var put = _requests.Single(request => request.Method == HttpMethod.Put);
            var uri = new Uri(put.Url.FullUri);
            var query = HttpUtility.ParseQueryString(uri.Query);
            uri.GetLeftPart(UriPartial.Path).Should().Be("https://plex.local:32400/plex/library/sections/1/all");
            query["type"].Should().Be("4");
            query["id"].Should().Be("20");
            long.Parse(query["addedAt.value"]).Should().BeInRange(before, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            query["addedAt.locked"].Should().BeNull();
            query["X-Plex-Token"].Should().Be("test-token");
            _requests.Should().NotContain(request => request.Url.FullUri.Contains("/refresh", StringComparison.Ordinal));
        }

        [Test]
        public void should_page_shows_and_leaves_using_headers()
        {
            _pageSize = 1;
            _shows.Insert(0, Show("11", "tvdb://999"));
            _episodes.Insert(0, EpisodeMetadata("21", 1));

            Subject.MarkAsRecentlyAdded(_settings, _message);

            _requests.Where(request => request.Url.FullUri.Contains("allLeaves", StringComparison.Ordinal))
                .Select(request => request.Headers["X-Plex-Container-Start"]).Should().Equal("0", "1");
            _requests.Where(request => request.Method == HttpMethod.Put).Should().ContainSingle();
            _requests.Where(request => new Uri(request.Url.FullUri).AbsolutePath == "/plex/library/all" ||
                new Uri(request.Url.FullUri).AbsolutePath.Contains("allLeaves", StringComparison.Ordinal))
                .Should().OnlyContain(request => request.Headers["X-Plex-Container-Size"] == "200");
        }

        [TestCase("wrong-show-type")]
        [TestCase("wrong-episode-type")]
        [TestCase("no-show")]
        [TestCase("wrong-provider-id")]
        [TestCase("no-episode")]
        [TestCase("wrong-season")]
        [TestCase("ambiguous-episode")]
        public void should_not_edit_unresolved_or_ambiguous_items(string scenario)
        {
            switch (scenario)
            {
                case "wrong-show-type":
                    _shows[0]["type"] = "season";
                    break;
                case "wrong-episode-type":
                    _episodes[0]["type"] = "movie";
                    break;
                case "no-show":
                    _shows.Clear();
                    break;
                case "wrong-provider-id":
                    _shows[0] = Show("10", "tvdb://4333840");
                    break;
                case "no-episode":
                    _episodes.Clear();
                    break;
                case "wrong-season":
                    _episodes[0] = EpisodeMetadata("20", 2, 2);
                    break;
                case "ambiguous-episode":
                    _episodes.Add(EpisodeMetadata("21", 2));
                    break;
            }

            Subject.MarkAsRecentlyAdded(_settings, _message);

            _requests.Should().NotContain(request => request.Method == HttpMethod.Put);
        }

        [TestCase("com.plexapp.agents.thetvdb://433384?lang=en", true)]
        [TestCase("com.plexapp.agents.thetvdb://4333840?lang=en", false)]
        [TestCase("plex://show/433384", false)]
        public void should_match_exact_legacy_provider_guids(string guid, bool matched)
        {
            _shows[0]["Guid"] = new JsonArray(new JsonObject { ["id"] = guid });

            Subject.MarkAsRecentlyAdded(_settings, _message);

            _requests.Count(request => request.Method == HttpMethod.Put).Should().Be(matched ? 1 : 0);
        }

        [Test]
        public void should_batch_only_imported_episodes_once()
        {
            _message.EpisodeInfo.Episodes.Add(new Episode { SeasonNumber = 1, EpisodeNumber = 2 });
            _message.EpisodeInfo.Episodes.Add(new Episode { SeasonNumber = 1, EpisodeNumber = 3 });
            _episodes.Add(EpisodeMetadata("21", 3));
            _episodes.Add(EpisodeMetadata("22", 4));

            Subject.MarkAsRecentlyAdded(_settings, _message);

            _requests.Where(request => request.Method == HttpMethod.Put)
                .Select(request => HttpUtility.ParseQueryString(new Uri(request.Url.FullUri).Query)["id"])
                .Should().Equal("20,21");
        }

        [TestCase("tvdb-433384", "tvdb://433384")]
        [TestCase("imdb-tt123", "imdb://tt123")]
        [TestCase("tmdb-123", "tmdb://123")]
        public void should_resolve_provider_id_through_one_bounded_dummy_and_exact_global_guid(string title, string providerGuid)
        {
            _message.Series.Year = 0;
            _message.Series.TvdbId = title.StartsWith("tvdb", StringComparison.Ordinal) ? 433384 : 0;
            _message.Series.ImdbId = title.StartsWith("imdb", StringComparison.Ordinal) ? "tt123" : null;
            _message.Series.TmdbId = title.StartsWith("tmdb", StringComparison.Ordinal) ? 123 : 0;
            _shows[0]["Guid"] = new JsonArray(new JsonObject { ["id"] = providerGuid });
            _matchCandidates[0]["SearchResult"] = new JsonArray(new JsonObject { ["type"] = "season", ["guid"] = "plex://season/child" });

            Subject.MarkAsRecentlyAdded(_settings, _message);

            var dummy = _requests.Single(request => new Uri(request.Url.FullUri).AbsolutePath == "/plex/library/sections/1/all" && request.Method == HttpMethod.Get);
            dummy.Headers["X-Plex-Container-Size"].Should().Be("1");
            var match = _requests.Single(request => new Uri(request.Url.FullUri).AbsolutePath.EndsWith("/matches", StringComparison.Ordinal));
            var query = HttpUtility.ParseQueryString(new Uri(match.Url.FullUri).Query);
            query["title"].Should().Be(title);
            query["year"].Should().Be("1998");
            query["agent"].Should().Be("tv.plex.agents.series");
            query["language"].Should().Be("cs-CZ");
            var global = _requests.Single(request => new Uri(request.Url.FullUri).AbsolutePath == "/plex/library/all");
            HttpUtility.ParseQueryString(new Uri(global.Url.FullUri).Query)["guid"].Should().Be("plex://show/modern-id");
            _requests.Count(request => request.Method == HttpMethod.Put).Should().Be(1);
        }

        [Test]
        public void should_skip_empty_sample_sections_and_stop_at_first_nonempty_section()
        {
            _sectionCount = 3;
            _emptySections.Add(1);

            Subject.MarkAsRecentlyAdded(_settings, _message);

            var samples = _requests.Where(request => request.Method == HttpMethod.Get && new Uri(request.Url.FullUri).AbsolutePath.StartsWith("/plex/library/sections/", StringComparison.Ordinal)).ToList();
            samples.Select(request => new Uri(request.Url.FullUri).AbsolutePath).Should().Equal("/plex/library/sections/1/all", "/plex/library/sections/2/all");
            samples.Should().OnlyContain(request => request.Headers["X-Plex-Container-Size"] == "1");
        }

        [TestCase("empty-sections")]
        [TestCase("empty-matches")]
        [TestCase("missing-guid")]
        public void should_skip_cold_misses_without_scanning_the_library(string scenario)
        {
            if (scenario == "empty-sections")
            {
                _emptySections.Add(1);
            }
            else if (scenario == "empty-matches")
            {
                _matchCandidates.Clear();
            }
            else
            {
                _globalMiss = true;
            }

            Subject.MarkAsRecentlyAdded(_settings, _message);

            _requests.Should().NotContain(request => request.Method == HttpMethod.Put);
            _requests.Where(request => request.Method == HttpMethod.Get && new Uri(request.Url.FullUri).AbsolutePath.Contains("/sections/", StringComparison.Ordinal))
                .Should().OnlyContain(request => request.Headers["X-Plex-Container-Size"] == "1");
        }

        [Test]
        public void should_check_external_identity_instead_of_trusting_the_first_match_candidate()
        {
            var wrong = Show("11", "tvdb://999");
            wrong["guid"] = "plex://show/wrong";
            _shows.Add(wrong);
            _matchCandidates.Insert(0, new JsonObject { ["type"] = "show", ["guid"] = "plex://show/wrong" });

            Subject.MarkAsRecentlyAdded(_settings, _message);

            _requests.Where(request => request.Method == HttpMethod.Put)
                .Select(request => HttpUtility.ParseQueryString(new Uri(request.Url.FullUri).Query)["id"]).Should().Equal("20");
        }

        [TestCase(1)]
        [TestCase(2)]
        public void should_update_all_exact_copies_and_reuse_their_parent_identities(int section)
        {
            _sectionCount = section;
            _shows.Add(Show("11", "tvdb://433384", section));
            _episodes.Add(EpisodeMetadata("21", 2, parent: "11", section: section));

            Subject.MarkAsRecentlyAdded(_settings, _message);
            _requests.Clear();
            Subject.MarkAsRecentlyAdded(_settings, _message);

            _requests.Count(request => request.Method == HttpMethod.Get).Should().Be(1);
            _requests.Where(request => request.Method == HttpMethod.Put)
                .SelectMany(request => HttpUtility.ParseQueryString(new Uri(request.Url.FullUri).Query)["id"].Split(','))
                .Should().BeEquivalentTo(new[] { "20", "21" });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_stop_on_an_ignored_guid_filter_or_unbounded_result(bool excessiveTotal)
        {
            if (excessiveTotal)
            {
                _globalTotal = 1001;
            }
            else
            {
                _ignoredGuidFilter = true;
                _shows[0]["guid"] = "plex://show/unrelated";
            }

            Assert.Throws<PlexException>(() => Subject.MarkAsRecentlyAdded(_settings, _message));

            _requests.Count(request => new Uri(request.Url.FullUri).AbsolutePath == "/plex/library/all").Should().Be(1);
            _requests.Should().NotContain(request => request.Method == HttpMethod.Put);
        }

        [Test]
        public void should_not_query_without_an_external_provider_id()
        {
            _message.Series.TvdbId = 0;
            _message.Series.ImdbId = null;
            _message.Series.TmdbId = 0;

            Subject.MarkAsRecentlyAdded(_settings, _message);

            _requests.Should().BeEmpty();
        }

        [Test]
        public void should_read_legacy_metadata_collections()
        {
            _legacy = true;

            Subject.MarkAsRecentlyAdded(_settings, _message);

            _requests.Count(request => request.Method == HttpMethod.Put).Should().Be(1);
        }

        [Test]
        public void should_report_plex_errors_returned_with_a_success_status()
        {
            Mocker.GetMock<IHttpClient>().Setup(c => c.Execute(It.IsAny<HttpRequest>()))
                .Returns<HttpRequest>(request => request.Method == HttpMethod.Put
                    ? new HttpResponse(request, new HttpHeader(), "{\"MediaContainer\":{\"error\":\"Not authorized to edit metadata\"}}")
                    : Respond(request));

            Assert.Throws<PlexException>(() => Subject.MarkAsRecentlyAdded(_settings, _message));
        }

        [Test]
        public void should_stop_if_plex_ignores_pagination_headers()
        {
            _pageSize = 1;
            _repeatPage = true;
            _shows.Add(Show("11", "tvdb://999"));

            Assert.Throws<PlexException>(() => Subject.MarkAsRecentlyAdded(_settings, _message));
            _requests.Should().NotContain(request => request.Method == HttpMethod.Put);
        }
    }
}
