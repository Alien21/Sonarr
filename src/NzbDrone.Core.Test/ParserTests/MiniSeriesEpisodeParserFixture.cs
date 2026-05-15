using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.ParserTests
{
    [TestFixture]
    public class MiniSeriesEpisodeParserFixture : CoreTest
    {
        [TestCase("The.Big.Series.Leader.Part.2.DSR.XviD-SYS", "The Big Series Leader", 2)]
        [TestCase("kill-roy-was-here-e07-720p", "kill-roy-was-here", 7)]
        [TestCase("Series and Show 2012 Part 1 REPACK 720p HDTV x264 2HD", "Series and Show 2012", 1)]
        [TestCase("Series Show.2016.E04.Power.720p.WEB-DL.DD5.1.H.264-MARS", "Series Show 2016", 4)]

        // [TestCase("Killroy.Jumped.And.Was.Here.EP02.Episode.Title.DVDRiP.XviD-DEiTY", "Killroy.Jumped.And.Was.Here", 2)]
        // [TestCase("", "", 0)]
        public void should_parse_mini_series_episode(string postTitle, string title, int episodeNumber)
        {
            var result = Parser.Parser.ParseTitle(postTitle);
            result.Should().NotBeNull();
            result.EpisodeNumbers.Should().HaveCount(1);
            result.SeasonNumber.Should().Be(1);
            result.EpisodeNumbers.First().Should().Be(episodeNumber);
            result.SeriesTitle.Should().Be(title);
            result.AbsoluteEpisodeNumbers.Should().BeEmpty();
            result.FullSeason.Should().BeFalse();
        }

        [TestCase("Killroy.Jumped.And.Was.Here.EP2.Episode.Title.DVDRiP.XviD-DEiTY", "Killroy Jumped And Was Here", 2)]
        [TestCase("Killroy.Jumped.And.Was.Here.EP02.Episode.Title.DVDRiP.XviD-DEiTY", "Killroy Jumped And Was Here", 2)]
        [TestCase("Killroy.Jumped.And.Was.Here.EP123.Episode.Title.DVDRiP.XviD-DEiTY", "Killroy Jumped And Was Here", 123)]
        [TestCase("Killroy.Jumped.And.Was.Here.E2.Episode.Title.DVDRiP.XviD-DEiTY", "Killroy Jumped And Was Here", 2)]
        [TestCase("Killroy.Jumped.And.Was.Here.E02.Episode.Title.DVDRiP.XviD-DEiTY", "Killroy Jumped And Was Here", 2)]
        [TestCase("Killroy.Jumped.And.Was.Here.E123.Episode.Title.DVDRiP.XviD-DEiTY", "Killroy Jumped And Was Here", 123)]
        public void should_parse_episode_only_release_as_season_one_when_enabled(string postTitle, string title, int episodeNumber)
        {
            var result = Parser.Parser.ParseTitle(postTitle, false, true);
            result.Should().NotBeNull();
            result.EpisodeNumbers.Should().HaveCount(1);
            result.SeasonNumber.Should().Be(1);
            result.EpisodeNumbers.First().Should().Be(episodeNumber);
            result.SeriesTitle.Should().Be(title);
            result.AbsoluteEpisodeNumbers.Should().BeEmpty();
            result.FullSeason.Should().BeFalse();
        }

        [TestCase("Deti z Kourove hory E01-E13 SK 720p HEVC 1979", "Deti z Kourove hory", new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 })]
        [TestCase("Killroy.Jumped.And.Was.Here.EP02-EP04.DVDRiP.XviD-DEiTY", "Killroy Jumped And Was Here", new[] { 2, 3, 4 })]
        public void should_parse_episode_only_range_as_season_one_when_enabled(string postTitle, string title, int[] episodeNumbers)
        {
            var result = Parser.Parser.ParseTitle(postTitle, false, true);
            result.Should().NotBeNull();
            result.SeasonNumber.Should().Be(1);
            result.EpisodeNumbers.Should().BeEquivalentTo(episodeNumbers);
            result.SeriesTitle.Should().Be(title);
            result.AbsoluteEpisodeNumbers.Should().BeEmpty();
            result.FullSeason.Should().BeFalse();
        }

        [Test]
        public void should_not_parse_episode_only_release_when_disabled()
        {
            Parser.Parser.ParseTitle("Killroy.Jumped.And.Was.Here.EP2.Episode.Title.DVDRiP.XviD-DEiTY").Should().BeNull();
        }

        [Test]
        public void should_keep_explicit_season_episode_when_episode_only_parsing_enabled()
        {
            var result = Parser.Parser.ParseTitle("Series.Title.S02E03.720p.HDTV.x264-GROUP", false, true);

            result.Should().NotBeNull();
            result.SeasonNumber.Should().Be(2);
            result.EpisodeNumbers.Single().Should().Be(3);
            result.SeriesTitle.Should().Be("Series Title");
        }

        [TestCase("It's a Series Title.E56.190121.720p-NEXT.mp4", "It's a Series Title", 56, "2019-01-21")]
        [TestCase("My Only Series Title.E37.190120.1080p-NEXT.mp4", "My Only Series Title", 37, "2019-01-20")]
        [TestCase("Series.E191.190121.720p-NEXT.mp4", "Series", 191, "2019-01-21")]
        [TestCase("The Series Title Challenge.E932.190120.720p-NEXT.mp4", "The Series Title Challenge", 932, "2019-01-20")]

        // [TestCase("", "", 0, "")]
        public void should_parse_korean_series_episode(string postTitle, string title, int episodeNumber, string airdate)
        {
            var result = Parser.Parser.ParseTitle(postTitle);
            result.Should().NotBeNull();
            result.EpisodeNumbers.Should().HaveCount(1);
            result.SeasonNumber.Should().Be(1);
            result.EpisodeNumbers.First().Should().Be(episodeNumber);
            result.SeriesTitle.Should().Be(title);
            result.AbsoluteEpisodeNumbers.Should().BeEmpty();
            result.FullSeason.Should().BeFalse();

            // We don't support both SxxExx and airdate yet
            // result.AirDate.Should().Be(airdate);
        }
    }
}
