using System.Collections.Generic;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.MetadataSource
{
    public interface ISearchForNewSeries
    {
        List<Series> SearchForNewSeries(string title);
        List<Series> SearchForNewSeries(string title, Language language);
        List<Series> SearchForNewSeriesByImdbId(string imdbId);
        List<Series> SearchForNewSeriesByImdbId(string imdbId, Language language);
        List<Series> SearchForNewSeriesByAniListId(int aniListId);
        List<Series> SearchForNewSeriesByAniListId(int aniListId, Language language);
        List<Series> SearchForNewSeriesByTmdbId(int tmdbId);
        List<Series> SearchForNewSeriesByTmdbId(int tmdbId, Language language);
        List<Series> SearchForNewSeriesByMyAnimeListId(int malId);
        List<Series> SearchForNewSeriesByMyAnimeListId(int malId, Language language);
        Series SearchForNewSeriesByExactTitle(string title, int year, List<Series> candidates);
    }
}
