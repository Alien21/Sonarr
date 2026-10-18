using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.Emby;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
using NzbDrone.Test.Common;
namespace NzbDrone.Core.Test.NotificationTests.MediaBrowser
{
    [TestFixture]
    public class RecentlyAddedMediaBrowserProxyFixture : CoreTest<MediaBrowserProxy>
    {
        private MediaBrowserSettings _settings;
        private DownloadMessage _message;
        private List<JsonObject> _series;
        private List<JsonObject> _yearFilteredSeries;
        private bool _repeatSeriesPage;
        private bool _omitSeriesPageTotal;
        private List<JsonObject> _episodes;
        private Dictionary<string, JsonObject> _metadata;
        private List<HttpRequest> _requests;
        private List<JsonObject> _posted;
        private HttpStatusCode? _cachedStatus;
        private bool _cachedMismatch;
        private bool _writeNotFound;
        [SetUp]
        public void SetUp()
        {
            Mocker.SetConstant<IMediaServerItemLinkStore>(new MediaServerItemLinkStoreStub());
            _cachedStatus = null;
            _cachedMismatch = false;
            _writeNotFound = false;
            _settings = new MediaBrowserSettings
            {
                Host = "jellyfin.invalid",
                Port = 8920,
                UseSsl = true,
                UrlBase = "/jellyfin",
                ApiKey = "offline-test-key"
            };
            _message = new DownloadMessage
            {
                Series = new Series { TvdbId = 123, ImdbId = "tt123", Year = 2020, Path = "/shows/Test" },
                EpisodeInfo = new LocalEpisode
                {
                    Episodes = new List<Episode> { new Episode { SeasonNumber = 1, EpisodeNumber = 1 } }
                },
                EpisodeFile = new EpisodeFile { Path = "/shows/Test/new-quality.mkv" }
            };
            _series = new List<JsonObject>
            {
                JsonNode.Parse("""{"Id":"series1","Type":"Series","Name":"Test","Path":"/shows/Test","ProviderIds":{"Tvdb":"123","Imdb":"tt123"}}""").AsObject()
            };
            _episodes = new List<JsonObject>
            {
                MakeEpisode("ep1", 1),
                MakeEpisode("ep2", 2)
            };
            _metadata = new Dictionary<string, JsonObject>
            {
                ["ep1"] = MakeMetadata("ep1"),
                ["ep2"] = MakeMetadata("ep2")
            };
            _yearFilteredSeries = null;
            _repeatSeriesPage = false;
            _omitSeriesPageTotal = false;
            _requests = new List<HttpRequest>();
            _posted = new List<JsonObject>();
            Mocker.GetMock<IHttpClient>()
                .Setup(client => client.Get(It.IsAny<HttpRequest>()))
                .Returns<HttpRequest>(Respond);
            Mocker.GetMock<IHttpClient>()
                .Setup(client => client.Post(It.IsAny<HttpRequest>()))
                .Returns<HttpRequest>(request =>
                {
                    _requests.Add(request);
                    if (_writeNotFound)
                    {
                        _writeNotFound = false;
                        throw new HttpException(request, new HttpResponse(request, new HttpHeader(), string.Empty, HttpStatusCode.NotFound));
                    }

                    _posted.Add(JsonNode.Parse(Encoding.UTF8.GetString(request.ContentData)).AsObject());
                    return new HttpResponse(request, new HttpHeader(), string.Empty, HttpStatusCode.NoContent);
                });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_reuse_identity_and_fetch_fresh_metadata_before_writing(bool missingYear)
        {
            Subject.MarkAsRecentlyAdded(_settings, _message);
            _requests.Clear();
            _posted.Clear();
            _metadata["ep1"]["Overview"] = "New server metadata";
            _message.EpisodeFile.Path = "/shows/Test/another-quality-name.mkv";
            _message.Series.Year = missingYear ? 0 : 2020;

            Subject.MarkAsRecentlyAdded(_settings, _message);

            _requests.Should().HaveCount(2);
            Query(_requests[0])["ids"].Should().Be("series1,ep1");
            _posted.Should().ContainSingle();
            _posted[0]["Overview"].GetValue<string>().Should().Be("New server metadata");
        }

        [Test]
        public void should_reuse_series_identity_for_another_episode()
        {
            Subject.MarkAsRecentlyAdded(_settings, _message);
            _requests.Clear();
            _posted.Clear();
            _message.EpisodeInfo.Episodes = new List<Episode> { new Episode { SeasonNumber = 1, EpisodeNumber = 2 } };

            Subject.MarkAsRecentlyAdded(_settings, _message);

            _requests.Should().NotContain(request => Query(request)["includeItemTypes"] == "Series");
            _posted.Select(item => item["Id"].GetValue<string>()).Should().Equal("ep2");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_replace_stale_ids_and_reuse_the_new_saved_binding(bool notFound)
        {
            Subject.MarkAsRecentlyAdded(_settings, _message);
            _requests.Clear();
            _posted.Clear();
            _cachedStatus = notFound ? HttpStatusCode.NotFound : null;
            _series[0]["Id"] = "replacement-series";
            _episodes[0]["Id"] = "replacement-episode";
            _metadata.Remove("ep1");
            _metadata["replacement-episode"] = MakeMetadata("replacement-episode");
            _metadata["replacement-episode"]["SeriesId"] = "replacement-series";

            Subject.MarkAsRecentlyAdded(_settings, _message);

            _requests.Count(request => Query(request)["includeItemTypes"] == "Series").Should().Be(1);
            _posted.Select(item => item["Id"].GetValue<string>()).Should().Equal("replacement-episode");
            _requests.Clear();
            _posted.Clear();
            Subject.MarkAsRecentlyAdded(_settings, _message);
            _requests.Should().HaveCount(2);
            Query(_requests[0])["ids"].Should().Be("replacement-series,replacement-episode");
            _posted.Select(item => item["Id"].GetValue<string>()).Should().Equal("replacement-episode");
        }

        [TestCase(HttpStatusCode.Unauthorized)]
        [TestCase(HttpStatusCode.InternalServerError)]
        public void should_not_discover_or_write_after_cached_authentication_or_server_failure(HttpStatusCode status)
        {
            Subject.MarkAsRecentlyAdded(_settings, _message);
            _requests.Clear();
            _posted.Clear();
            _cachedStatus = status;

            Subject.MarkAsRecentlyAdded(_settings, _message);

            _requests.Should().ContainSingle();
            _posted.Should().BeEmpty();
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_rediscover_once_if_cached_episode_disappears_before_write()
        {
            Subject.MarkAsRecentlyAdded(_settings, _message);
            _requests.Clear();
            _posted.Clear();
            _writeNotFound = true;

            Subject.MarkAsRecentlyAdded(_settings, _message);

            _requests.Count(request => Query(request)["includeItemTypes"] == "Series").Should().Be(1);
            _requests.Count(request => request.Url.Path == "/jellyfin/Items/ep1").Should().Be(2);
            _posted.Should().ContainSingle();
        }

        [Test]
        public void should_avoid_repeated_discovery_for_recently_missing_episodes()
        {
            _episodes.Clear();
            Subject.MarkAsRecentlyAdded(_settings, _message);
            _requests.Clear();

            Subject.MarkAsRecentlyAdded(_settings, _message);

            _requests.Should().ContainSingle();
            Query(_requests[0])["ids"].Should().Be("series1");
            _posted.Should().BeEmpty();
        }

        [Test]
        public void should_preserve_complete_raw_metadata_and_only_change_added_date()
        {
            var original = _metadata["ep1"].DeepClone().AsObject();
            var before = DateTimeOffset.UtcNow;
            Subject.MarkAsRecentlyAdded(_settings, _message);
            _posted.Should().ContainSingle();
            var payload = _posted.Single();
            DateTimeOffset.Parse(payload["DateCreated"].GetValue<string>()).Should().BeOnOrAfter(before);
            payload["OriginalDate"].GetValue<string>().Should().Be("2020-01-02T03:04:05.1234567+05:30");
            payload["UnknownNumber"].ToJsonString().Should().Be("1.23000000000000000001");
            payload["LargeInteger"].ToJsonString().Should().Be("123456789012345678901");
            payload.ContainsKey("UnknownNull").Should().BeTrue();
            payload["UnknownNull"].Should().BeNull();
            payload.Remove("DateCreated");
            original.Remove("DateCreated");
            JsonNode.DeepEquals(payload, original).Should().BeTrue();
            _metadata["ep1"]["DateCreated"].GetValue<string>().Should().Be("2020-01-01T00:00:00Z");
        }

        [Test]
        public void should_keep_authentication_ssl_url_base_and_complete_metadata_fields()
        {
            Subject.MarkAsRecentlyAdded(_settings, _message);
            foreach (var request in _requests)
            {
                request.Url.FullUri.Should().StartWith("https://jellyfin.invalid:8920/jellyfin/");
                request.Headers["X-MediaBrowser-Token"].Should().Be("offline-test-key");
                request.Headers["Authorization"].Should().Be("MediaBrowser Token=\"offline-test-key\"");
            }

            var metadataRequest = _requests.Single(request => Query(request)["ids"] != null);
            var query = Query(metadataRequest);
            query["ids"].Should().Be("ep1");
            query["collapseBoxSetItems"].Should().Be("false");
            _requests.Where(request => Query(request)["includeItemTypes"] == "Series")
                .Should().OnlyContain(request => Query(request)["collapseBoxSetItems"] == "false");
            query["enableUserData"].Should().Be("false");
            query["enableImages"].Should().Be("false");
            query["userId"].Should().BeNull();
            query["fields"].Split(',').Should().Contain(new[]
            {
                "Settings", "DateCreated", "Tags", "Genres", "Overview", "OriginalTitle", "ProviderIds",
                "People", "Studios", "ProductionLocations", "CustomRating", "Taglines", "SortName", "SpecialEpisodeNumbers"
            });
            _requests.Last().Url.Path.Should().Be("/jellyfin/Items/ep1");
            _requests.Last().Headers.ContentType.Should().Be("application/json");
        }

        [Test]
        public void should_match_existing_episode_before_server_knows_new_quality_filename()
        {
            _episodes[0]["Path"] = "/shows/Test/old-quality.mkv";
            Subject.MarkAsRecentlyAdded(_settings, _message);
            _posted.Select(item => item["Id"].GetValue<string>()).Should().Equal("ep1");
        }

        [Test]
        public void should_only_update_imported_episodes()
        {
            _message.EpisodeInfo.Episodes.Add(new Episode { SeasonNumber = 1, EpisodeNumber = 2 });
            _episodes.Add(MakeEpisode("ep3", 3));
            Subject.MarkAsRecentlyAdded(_settings, _message);
            _posted.Select(item => item["Id"].GetValue<string>()).Should().BeEquivalentTo(new[] { "ep1", "ep2" });
        }

        [Test]
        public void should_update_a_multi_episode_item_only_once()
        {
            _message.EpisodeInfo.Episodes.Add(new Episode { SeasonNumber = 1, EpisodeNumber = 2 });
            _episodes.RemoveAt(1);
            _episodes[0]["IndexNumberEnd"] = 2;
            Subject.MarkAsRecentlyAdded(_settings, _message);
            _posted.Should().ContainSingle();
        }

        [Test]
        public void should_not_match_series_by_name_or_year()
        {
            _series[0]["ProviderIds"] = JsonNode.Parse("""{"Tvdb":"999","Imdb":"tt999"}""");
            Subject.MarkAsRecentlyAdded(_settings, _message);
            _posted.Should().BeEmpty();
            _requests.Should().ContainSingle();
        }

        [Test]
        public void should_match_series_by_imdb_without_tvdb()
        {
            _message.Series.TvdbId = 0;
            _series[0]["ProviderIds"] = JsonNode.Parse("""{"Imdb":"tt123"}""");
            Subject.MarkAsRecentlyAdded(_settings, _message);
            _posted.Should().ContainSingle();
        }

        [Test]
        public void should_skip_ambiguous_series()
        {
            _series[0]["Path"] = "/other-one";
            var duplicate = _series[0].DeepClone().AsObject();
            duplicate["Id"] = "series2";
            duplicate["Path"] = "/other-two";
            _series.Add(duplicate);
            Subject.MarkAsRecentlyAdded(_settings, _message);
            _posted.Should().BeEmpty();
        }

        [Test]
        public void should_use_mapped_paths_to_resolve_series_and_episode_ambiguity()
        {
            _settings.MapFrom = "/shows";
            _settings.MapTo = "/jellyfin-shows";
            var otherSeries = _series[0].DeepClone().AsObject();
            otherSeries["Id"] = "other-series";
            otherSeries["Path"] = "/unrelated";
            _series[0]["Path"] = "/jellyfin-shows/Test";
            _series.Add(otherSeries);
            var otherEpisode = _episodes[0].DeepClone().AsObject();
            otherEpisode["Id"] = "other-episode";
            otherEpisode["Path"] = "/unrelated/file.mkv";
            _episodes[0]["Path"] = "/jellyfin-shows/Test/new-quality.mkv";
            _episodes.Add(otherEpisode);
            Subject.MarkAsRecentlyAdded(_settings, _message);
            _posted.Select(item => item["Id"].GetValue<string>()).Should().Equal("ep1");
        }

        [Test]
        public void should_skip_ambiguous_episodes()
        {
            var duplicate = _episodes[0].DeepClone().AsObject();
            duplicate["Id"] = "duplicate";
            _episodes.Add(duplicate);
            Subject.MarkAsRecentlyAdded(_settings, _message);
            _posted.Should().BeEmpty();
        }

        [TestCase(2)]
        [TestCase(null)]
        public void should_skip_wrong_or_missing_season_numbers(int? season)
        {
            _episodes[0]["ParentIndexNumber"] = JsonValue.Create(season);
            Subject.MarkAsRecentlyAdded(_settings, _message);
            _posted.Should().BeEmpty();
        }

        [Test]
        public void should_skip_events_without_imported_episodes()
        {
            _message.EpisodeInfo = null;
            Subject.MarkAsRecentlyAdded(_settings, _message);
            _requests.Should().BeEmpty();
        }

        [TestCase("Name")]
        [TestCase("Type")]
        [TestCase("LockData")]
        [TestCase("LockedFields")]
        [TestCase("Tags")]
        [TestCase("Genres")]
        public void should_skip_incomplete_metadata(string missingField)
        {
            _metadata["ep1"].Remove(missingField);
            Subject.MarkAsRecentlyAdded(_settings, _message);
            _posted.Should().BeEmpty();
            ExceptionVerification.ExpectedWarns(1);
        }

        [TestCase("Id", "wrong-id")]
        [TestCase("Type", "Movie")]
        public void should_skip_metadata_for_a_different_item(string field, string value)
        {
            _metadata["ep1"][field] = value;
            Subject.MarkAsRecentlyAdded(_settings, _message);
            _posted.Should().BeEmpty();
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_isolate_failed_metadata_updates_and_continue_with_other_imported_episodes()
        {
            _message.EpisodeInfo.Episodes.Add(new Episode { SeasonNumber = 1, EpisodeNumber = 2 });
            _metadata["ep1"].Remove("LockedFields");
            Subject.MarkAsRecentlyAdded(_settings, _message);
            _posted.Select(item => item["Id"].GetValue<string>()).Should().Equal("ep2");
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_not_throw_or_write_when_http_lookup_fails()
        {
            Mocker.GetMock<IHttpClient>()
                .Setup(client => client.Get(It.IsAny<HttpRequest>()))
                .Throws(new InvalidOperationException("offline failure"));
            Subject.MarkAsRecentlyAdded(_settings, _message);
            _posted.Should().BeEmpty();
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_not_fall_back_to_unfiltered_discovery_when_year_results_do_not_match()
        {
            _yearFilteredSeries = new List<JsonObject>();

            Subject.MarkAsRecentlyAdded(_settings, _message);

            _posted.Should().BeEmpty();
            _requests.Should().ContainSingle();
            Query(_requests[0])["years"].Should().Be("2020");
        }

        [Test]
        public void should_skip_uncached_series_without_a_year()
        {
            _message.Series.Year = 0;

            Subject.MarkAsRecentlyAdded(_settings, _message);

            _requests.Should().BeEmpty();
            _posted.Should().BeEmpty();
        }

        [Test]
        public void should_not_fetch_all_series_when_provider_id_is_found_in_year_results()
        {
            _message.Series.Year = 2020;
            Subject.MarkAsRecentlyAdded(_settings, _message);
            _posted.Should().ContainSingle();
            _requests.Count(request => Query(request)["includeItemTypes"] == "Series").Should().Be(1);
        }

        [Test]
        public void should_require_the_expected_type_for_series_and_episode_candidates()
        {
            _episodes[0]["Type"] = "Movie";
            Subject.MarkAsRecentlyAdded(_settings, _message);
            _posted.Should().BeEmpty();
        }

        [TestCase(true)]
        [TestCase(false)]
        public void should_stop_when_server_repeats_a_full_page_without_start_index(bool includeTotal)
        {
            _repeatSeriesPage = true;
            _omitSeriesPageTotal = !includeTotal;
            _series = Enumerable.Range(0, 201).Select(number => new JsonObject
            {
                ["Id"] = $"unrelated-{number}",
                ["Type"] = "Series",
                ["ProviderIds"] = new JsonObject { ["Tvdb"] = "999" }
            }).ToList();
            Subject.MarkAsRecentlyAdded(_settings, _message);
            _posted.Should().BeEmpty();
            _requests.Should().HaveCount(2);
            ExceptionVerification.ExpectedWarns(1);
        }

        private HttpResponse Respond(HttpRequest request)
        {
            _requests.Add(request);
            var query = Query(request);
            List<JsonObject> items;
            if (query["includeItemTypes"] == "Series")
            {
                items = query["years"] != null && _yearFilteredSeries != null ? _yearFilteredSeries : _series;
            }
            else if (query["ids"] != null)
            {
                if (_cachedStatus.HasValue)
                {
                    var status = _cachedStatus.Value;
                    _cachedStatus = null;
                    throw new HttpException(request, new HttpResponse(request, new HttpHeader(), string.Empty, status));
                }

                items = query["ids"].Split(',').Select(id => _series.SingleOrDefault(item => item["Id"].GetValue<string>() == id) ?? _metadata.GetValueOrDefault(id))
                    .Where(item => item != null).Select(item => item.DeepClone().AsObject()).ToList();
                if (_cachedMismatch)
                {
                    items.Single(item => item["Type"].GetValue<string>() == "Episode")["SeriesId"] = "different-series";
                }
            }
            else
            {
                items = _episodes;
            }

            var total = items.Count;
            var startIndex = int.TryParse(query["startIndex"], out var requestedStart) ? requestedStart : 0;
            if (int.TryParse(query["limit"], out var limit))
            {
                var offset = _repeatSeriesPage && query["includeItemTypes"] == "Series" ? 0 : startIndex;
                items = items.Skip(offset).Take(limit).ToList();
            }

            var content = new JsonObject
            {
                ["Items"] = new JsonArray(items.Select(item => item.DeepClone()).ToArray()),
                ["TotalRecordCount"] = total,
                ["StartIndex"] = startIndex
            };
            if (_repeatSeriesPage && query["includeItemTypes"] == "Series")
            {
                content.Remove("StartIndex");
                if (_omitSeriesPageTotal)
                {
                    content.Remove("TotalRecordCount");
                }
            }

            return new HttpResponse(request, new HttpHeader(), content.ToJsonString());
        }

        private static System.Collections.Specialized.NameValueCollection Query(HttpRequest request)
        {
            return System.Web.HttpUtility.ParseQueryString(request.Url.Query);
        }

        private static JsonObject MakeEpisode(string id, int number)
        {
            return new JsonObject
            {
                ["Id"] = id,
                ["Type"] = "Episode",
                ["ParentIndexNumber"] = 1,
                ["IndexNumber"] = number,
                ["Path"] = "/shows/Test/old-quality.mkv"
            };
        }

        private static JsonObject MakeMetadata(string id)
        {
            var item = JsonNode.Parse("""
                {
                    "Id":"ep1", "Name":"Existing episode", "Type":"Episode", "DateCreated":"2020-01-01T00:00:00Z",
                    "LockData":true, "LockedFields":["Name","Overview"], "Tags":["keep"], "Genres":["Drama"],
                    "ForcedSortName":"sort this first", "PreferredMetadataLanguage":"cs", "PreferredMetadataCountryCode":"CZ",
                    "IndexNumber":1, "ParentIndexNumber":1, "AirsBeforeEpisodeNumber":2, "AirsBeforeSeasonNumber":2,
                    "AirsAfterSeasonNumber":1, "CustomRating":"keep rating", "OriginalTitle":"Existing original title",
                    "ProviderIds":{"Tvdb":"321"}, "People":[{"Name":"Actor","Type":"Actor"}], "Studios":[{"Name":"Studio"}],
                    "UnknownNull":null, "UnknownNested":{"MixedCaseProperty":[1,{"KeepNull":null}]},
                    "UnknownNumber":1.23000000000000000001, "LargeInteger":123456789012345678901,
                    "OriginalDate":"2020-01-02T03:04:05.1234567+05:30"
                }
                """).AsObject();
            item["Id"] = id;
            item["SeriesId"] = "series1";
            item["IndexNumber"] = id == "ep2" ? 2 : 1;
            return item;
        }
    }
}
