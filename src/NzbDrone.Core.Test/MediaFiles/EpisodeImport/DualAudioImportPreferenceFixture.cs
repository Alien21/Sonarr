using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.EpisodeImport;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.MediaFiles.EpisodeImport
{
    [TestFixture]
    public class DualAudioImportPreferenceFixture : CoreTest<DualAudioImportPreference>
    {
        private Series _series;
        private LocalEpisode _localEpisode;
        private EpisodeFile _existingEpisodeFile;

        [SetUp]
        public void Setup()
        {
            _series = new Series
            {
                QualityProfile = new QualityProfile
                {
                    Items = Qualities.QualityFixture.GetDefaultQualities()
                }
            };

            _localEpisode = new LocalEpisode
            {
                Series = _series,
                Size = 800,
                Quality = new QualityModel(Quality.WEBDL1080p),
                Languages = new List<Language> { Language.Czech, Language.English }
            };

            _existingEpisodeFile = new EpisodeFile
            {
                Size = 1000,
                Quality = new QualityModel(Quality.WEBDL1080p),
                Languages = new List<Language> { Language.English }
            };

            Mocker.GetMock<IConfigService>()
                .SetupGet(s => s.PreferDualAudio)
                .Returns(true);

            Mocker.GetMock<IConfigService>()
                .SetupGet(s => s.SeriesInfoLanguage)
                .Returns((int)Language.Czech);
        }

        [Test]
        public void should_not_apply_when_disabled()
        {
            Mocker.GetMock<IConfigService>()
                .SetupGet(s => s.PreferDualAudio)
                .Returns(false);

            Subject.Evaluate(_localEpisode, _existingEpisodeFile).Applies.Should().BeFalse();
        }

        [Test]
        public void should_prefer_candidate_with_series_info_language_and_another_audio_language()
        {
            var result = Subject.Evaluate(_localEpisode, _existingEpisodeFile);

            result.Applies.Should().BeTrue();
            result.IsPreferredUpgrade.Should().BeTrue();
            result.RequiresManualReview.Should().BeFalse();
        }

        [Test]
        public void should_not_apply_when_existing_file_already_has_preferred_dual_audio()
        {
            _existingEpisodeFile.Languages = new List<Language> { Language.Czech, Language.English };

            Subject.Evaluate(_localEpisode, _existingEpisodeFile).Applies.Should().BeFalse();
        }

        [Test]
        public void should_require_manual_review_when_candidate_is_lower_quality()
        {
            _localEpisode.Quality = new QualityModel(Quality.HDTV720p);
            _existingEpisodeFile.Quality = new QualityModel(Quality.WEBDL1080p);

            var result = Subject.Evaluate(_localEpisode, _existingEpisodeFile);

            result.Applies.Should().BeTrue();
            result.IsPreferredUpgrade.Should().BeFalse();
            result.RequiresManualReview.Should().BeTrue();
            result.ManualReviewReason.Should().Contain("lower quality");
        }

        [Test]
        public void should_require_manual_review_when_candidate_is_more_than_30_percent_smaller()
        {
            _localEpisode.Size = 699;

            var result = Subject.Evaluate(_localEpisode, _existingEpisodeFile);

            result.Applies.Should().BeTrue();
            result.IsPreferredUpgrade.Should().BeFalse();
            result.RequiresManualReview.Should().BeTrue();
            result.ManualReviewReason.Should().Contain("30% smaller");
        }

        [Test]
        public void should_count_unknown_secondary_audio_from_media_info()
        {
            _localEpisode.Languages = new List<Language> { Language.Czech };
            _localEpisode.MediaInfo = new MediaInfoModel
            {
                AudioLanguages = new List<string> { "ces", "pirate" }
            };

            var result = Subject.Evaluate(_localEpisode, _existingEpisodeFile);

            result.IsPreferredUpgrade.Should().BeTrue();
        }
    }
}
