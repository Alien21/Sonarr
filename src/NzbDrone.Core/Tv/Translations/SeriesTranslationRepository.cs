using System.Collections.Generic;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Tv.Translations
{
    public interface ISeriesTranslationRepository : IBasicRepository<SeriesTranslation>
    {
        List<SeriesTranslation> FindBySeriesId(int seriesId);
        List<SeriesTranslation> FindByLanguage(Language language);
        void DeleteForSeries(List<int> seriesIds);
    }

    public class SeriesTranslationRepository : BasicRepository<SeriesTranslation>, ISeriesTranslationRepository
    {
        public SeriesTranslationRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public List<SeriesTranslation> FindBySeriesId(int seriesId)
        {
            return Query(x => x.SeriesId == seriesId);
        }

        public List<SeriesTranslation> FindByLanguage(Language language)
        {
            return Query(x => x.Language == language);
        }

        public void DeleteForSeries(List<int> seriesIds)
        {
            Delete(x => seriesIds.Contains(x.SeriesId));
        }
    }
}
