using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Cloud;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MetadataSource.SkyHook;
using NzbDrone.Core.MetadataSource.SkyHook.Resource;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
namespace NzbDrone.Core.Test.MetadataSource.SkyHook
{
    [TestFixture]
    public class SkyHookProxySeriesLanguageFixture : CoreTest<SkyHookProxy>
    {
        [TestCase(true, "eng", "English Episode")]
        [TestCase(false, "ces", "Ceska epizoda")]
        public void should_preserve_episode_naming_language_from_one_metadata_request(bool useSeriesInfoLanguage, string expectedLanguage, string expectedEpisodeTitle)
        {
            Mocker.GetMock<IConfigService>()
                .SetupGet(s => s.UseSeriesInfoLanguage)
                .Returns(useSeriesInfoLanguage);
            Mocker.GetMock<IConfigService>()
                .SetupGet(s => s.SeriesInfoLanguage)
                .Returns(Language.Czech.Id);
            Mocker.GetMock<ISonarrCloudRequestBuilder>()
                .SetupGet(s => s.SkyHookTvdb)
                .Returns(new HttpRequestBuilder("https://skyhook.example/v2/tvdb/{route}/").CreateFactory());
            var requests = new List<HttpRequest>();
            Mocker.GetMock<IHttpClient>()
                .Setup(s => s.Get<ShowResource>(It.IsAny<HttpRequest>()))
                .Returns<HttpRequest>(request =>
                {
                    requests.Add(request);
                    var englishPayload = request.Url.FullUri.Contains("/shows/eng/");
                    var show = new ShowResource
                    {
                        TvdbId = 123,
                        Title = englishPayload ? "Series Name" : "Cesky serial",
                        Slug = "series-name",
                        Status = "continuing",
                        OriginalLanguage = "ja",
                        Episodes = new List<EpisodeResource>
                        {
                            new()
                            {
                                TvdbId = 456,
                                SeasonNumber = 1,
                                EpisodeNumber = 1,
                                Title = englishPayload ? "English Episode" : "Ceska epizoda"
                            }
                        },
                        Translations = new List<TranslationResource>
                        {
                            new() { Language = "eng", Title = "Series Name" },
                            new() { Language = "ces", Title = "Cesky serial" },
                            new() { Language = "ja", Title = "Original Series" }
                        }
                    };
                    return new HttpResponse<ShowResource>(new HttpResponse(request, new HttpHeader(), show.ToJson()));
                });
            Mocker.GetMock<IQualityDefinitionService>()
                .Setup(s => s.Get(It.IsAny<Quality>()))
                .Returns<Quality>(quality => Quality.DefaultQualityDefinitions.First(q => q.Quality == quality));
            var result = Subject.GetSeriesInfo(123, Language.Czech, SeasonType.Official);
            requests.Should().ContainSingle().Which.Url.FullUri.Should()
                .Be($"https://skyhook.example/v2/tvdb/shows/{expectedLanguage}/123/official");
            Mocker.GetMock<IHttpClient>()
                .Verify(s => s.Get<ShowResource>(It.IsAny<HttpRequest>()), Times.Once());
            result.Item2.Should().ContainSingle().Which.Title.Should().Be(expectedEpisodeTitle);
            result.Item1.OriginalTitle.Should().Be("Original Series");
            var naming = NamingConfig.Default;
            naming.RenameEpisodes = true;
            naming.StandardEpisodeFormat = "{Episode Title}";
            var fileName = Mocker.Resolve<FileNameBuilder>().BuildFileName(
                result.Item2,
                result.Item1,
                new EpisodeFile { Quality = new QualityModel(Quality.HDTV720p) },
                namingConfig: naming,
                customFormats: new List<CustomFormat>());
            fileName.Should().Be(expectedEpisodeTitle);
        }
    }
}
