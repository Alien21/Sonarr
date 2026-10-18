using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Tv;
using V3Mapper = Sonarr.Api.V3.Series.SeriesResourceMapper;
using V3Service = Sonarr.Api.V3.Series.SeriesResourceService;
using V5Mapper = Sonarr.Api.V5.Series.SeriesResourceMapper;
using V5Service = Sonarr.Api.V5.Series.SeriesResourceService;

namespace NzbDrone.Api.Test.SeriesTests
{
    [TestFixture(3)]
    [TestFixture(5)]
    public class SeriesResourceServiceFixture
    {
        private readonly int _apiVersion;
        private Mock<IConfigService> _config;
        private Mock<ISeriesTranslationService> _translations;
        private List<SeriesTranslation> _storedTranslations;

        public SeriesResourceServiceFixture(int apiVersion)
        {
            _apiVersion = apiVersion;
        }

        [SetUp]
        public void Setup()
        {
            _config = new Mock<IConfigService>();
            _config.SetupGet(c => c.SeriesInfoLanguage).Returns((int)Language.Czech);
            _translations = new Mock<ISeriesTranslationService>();
            _storedTranslations = new List<SeriesTranslation>();
            _translations.Setup(t => t.GetTranslations()).Returns(() => _storedTranslations);
            _translations.Setup(t => t.GetTranslations(It.IsAny<int>()))
                .Returns<int>(id => _storedTranslations.Where(t => t.SeriesId == id).ToList());
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_keep_english_default_and_original_title_independent_of_localized_title(bool legacyLocalization)
        {
            _config.SetupGet(c => c.UseSeriesInfoLanguage).Returns(legacyLocalization);
            var series = GivenLocalizedSeries();
            series.Translations.Add(EnglishTranslation(series.Id));

            Project(series).Should().Be(("Český název", "English SkyHook Title", "日本語の原題"));
            _translations.Verify(t => t.GetTranslations(It.IsAny<int>()), Times.Never());
        }

        [Test]
        public void should_project_english_when_legacy_preference_is_english_and_native_title_is_localized()
        {
            _config.SetupGet(c => c.UseSeriesInfoLanguage).Returns(true);
            _config.SetupGet(c => c.SeriesInfoLanguage).Returns((int)Language.English);
            var series = GivenLocalizedSeries();
            series.Translations.Add(EnglishTranslation(series.Id));

            Project(series).Should().Be(("English SkyHook Title", "English SkyHook Title", "日本語の原題"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_load_existing_english_translation_for_default_title(bool legacyLocalization)
        {
            _config.SetupGet(c => c.UseSeriesInfoLanguage).Returns(legacyLocalization);
            var series = GivenLocalizedSeries();
            _storedTranslations.Add(EnglishTranslation(series.Id));

            Project(series).Should().Be(("Český název", "English SkyHook Title", "日本語の原題"));
            _translations.Verify(t => t.GetTranslations(series.Id), Times.Once());
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_fall_back_to_native_title_when_english_translation_is_absent(bool legacyLocalization)
        {
            _config.SetupGet(c => c.UseSeriesInfoLanguage).Returns(legacyLocalization);
            var series = GivenLocalizedSeries();
            series.Translations.Add(new SeriesTranslation { Language = Language.English, Title = " " });

            Project(series).Should().Be(("Český název", "Český název", "日本語の原題"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_load_english_translations_once_for_a_series_list(bool legacyLocalization)
        {
            _config.SetupGet(c => c.UseSeriesInfoLanguage).Returns(legacyLocalization);
            var series = new[] { GivenLocalizedSeries(1), GivenLocalizedSeries(2) };
            _storedTranslations.AddRange(series.Select(s => EnglishTranslation(s.Id)));

            ProjectList(series).Should().Equal(Enumerable.Repeat(("Český název", "English SkyHook Title", "日本語の原題"), 2));
            _translations.Verify(t => t.GetTranslations(), Times.Once());
            _translations.Verify(t => t.GetTranslations(It.IsAny<int>()), Times.Never());
        }

        [Test]
        public void direct_mapper_should_use_embedded_english_translation_without_replacing_original_title()
        {
            var series = GivenLocalizedSeries();
            series.Translations.Add(EnglishTranslation(series.Id));

            var title = _apiVersion == 3 ? V3Mapper.ToResource(series).DefaultTitle : V5Mapper.ToResource(series).DefaultTitle;

            title.Should().Be("English SkyHook Title");
            series.Title.Should().Be("Český název");
            series.OriginalTitle.Should().Be("日本語の原題");
        }

        private static Series GivenLocalizedSeries(int id = 1)
        {
            return new Series
            {
                Id = id,
                Title = "Český název",
                OriginalTitle = "日本語の原題",
                OriginalLanguage = Language.Japanese,
                Language = Language.Czech,
                Translations = new List<SeriesTranslation>
                {
                    new SeriesTranslation { SeriesId = id, Language = Language.Czech, Title = "Český název" }
                }
            };
        }

        private static SeriesTranslation EnglishTranslation(int seriesId)
        {
            return new SeriesTranslation { SeriesId = seriesId, Language = Language.English, Title = "English SkyHook Title" };
        }

        private (string Title, string DefaultTitle, string OriginalTitle) Project(Series series)
        {
            if (_apiVersion == 3)
            {
                var resource = new V3Service(_config.Object, _translations.Object).ToResource(series);
                return (resource.Title, resource.DefaultTitle, resource.OriginalTitle);
            }

            var v5Resource = new V5Service(_config.Object, _translations.Object).ToResource(series);
            return (v5Resource.Title, v5Resource.DefaultTitle, v5Resource.OriginalTitle);
        }

        private List<(string Title, string DefaultTitle, string OriginalTitle)> ProjectList(IEnumerable<Series> series)
        {
            if (_apiVersion == 3)
            {
                return new V3Service(_config.Object, _translations.Object).ToResource(series)
                    .Select(r => (r.Title, r.DefaultTitle, r.OriginalTitle)).ToList();
            }

            return new V5Service(_config.Object, _translations.Object).ToResource(series)
                .Select(r => (r.Title, r.DefaultTitle, r.OriginalTitle)).ToList();
        }
    }
}
