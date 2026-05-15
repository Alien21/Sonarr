using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Tv.Events;

namespace NzbDrone.Core.Tv.Translations
{
    public interface ISeriesTranslationService
    {
        List<SeriesTranslation> GetAllTranslationsForSeries(int seriesId);
        List<SeriesTranslation> GetAllTranslationsForLanguage(Language language);
        List<SeriesTranslation> UpdateTranslations(List<SeriesTranslation> translations, Series series);
    }

    public class SeriesTranslationService : ISeriesTranslationService, IHandleAsync<SeriesDeletedEvent>
    {
        private readonly ISeriesTranslationRepository _translationRepo;
        private readonly Logger _logger;

        public SeriesTranslationService(ISeriesTranslationRepository translationRepo,
                                        Logger logger)
        {
            _translationRepo = translationRepo;
            _logger = logger;
        }

        public List<SeriesTranslation> GetAllTranslationsForSeries(int seriesId)
        {
            return _translationRepo.FindBySeriesId(seriesId).ToList();
        }

        public List<SeriesTranslation> GetAllTranslationsForLanguage(Language language)
        {
            return _translationRepo.FindByLanguage(language).ToList();
        }

        public List<SeriesTranslation> UpdateTranslations(List<SeriesTranslation> translations, Series series)
        {
            var seriesId = series.Id;

            translations ??= new List<SeriesTranslation>();
            translations.ForEach(t => t.SeriesId = seriesId);
            translations = translations.Where(t => t.Language != null).DistinctBy(t => t.Language).ToList();

            var existingTranslations = _translationRepo.FindBySeriesId(seriesId);

            var updateList = new List<SeriesTranslation>();
            var addList = new List<SeriesTranslation>();
            var upToDateCount = 0;

            foreach (var translation in translations)
            {
                var existingTranslation = existingTranslations.FirstOrDefault(x => x.Language == translation.Language);

                if (existingTranslation != null)
                {
                    existingTranslations.Remove(existingTranslation);

                    translation.Id = existingTranslation.Id;

                    if (IsChanged(translation, existingTranslation))
                    {
                        updateList.Add(translation);
                    }
                    else
                    {
                        upToDateCount++;
                    }
                }
                else
                {
                    addList.Add(translation);
                }
            }

            _translationRepo.DeleteMany(existingTranslations);
            _translationRepo.UpdateMany(updateList);
            _translationRepo.InsertMany(addList);

            _logger.Debug("[{0}] {1} translations up to date; Updating {2}, Adding {3}, Deleting {4} entries.", series.Title, upToDateCount, updateList.Count, addList.Count, existingTranslations.Count);

            return translations;
        }

        private static bool IsChanged(SeriesTranslation translation, SeriesTranslation existingTranslation)
        {
            return translation.SeriesId != existingTranslation.SeriesId ||
                   translation.Title != existingTranslation.Title ||
                   translation.CleanTitle != existingTranslation.CleanTitle ||
                   translation.Overview != existingTranslation.Overview ||
                   translation.Language?.Id != existingTranslation.Language?.Id;
        }

        public void HandleAsync(SeriesDeletedEvent message)
        {
            _translationRepo.DeleteForSeries(message.Series.Select(s => s.Id).ToList());
        }
    }
}
