using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Tv.Translations;

namespace Sonarr.Api.V3.Series
{
    public interface ISeriesResourceService
    {
        SeriesResource ToResource(NzbDrone.Core.Tv.Series series, bool includeSeasonImages = false);
        List<SeriesResource> ToResource(List<NzbDrone.Core.Tv.Series> series, bool includeSeasonImages = false);
    }

    public class SeriesResourceService : ISeriesResourceService
    {
        private readonly IConfigService _configService;
        private readonly ISeriesTranslationService _seriesTranslationService;

        public SeriesResourceService(IConfigService configService,
                                     ISeriesTranslationService seriesTranslationService)
        {
            _configService = configService;
            _seriesTranslationService = seriesTranslationService;
        }

        public SeriesResource ToResource(NzbDrone.Core.Tv.Series series, bool includeSeasonImages = false)
        {
            if (series == null)
            {
                return null;
            }

            return series.ToResource(GetSeriesTranslation(series), includeSeasonImages);
        }

        public List<SeriesResource> ToResource(List<NzbDrone.Core.Tv.Series> series, bool includeSeasonImages = false)
        {
            if (series == null)
            {
                return null;
            }

            var language = GetConfiguredLanguage();

            if (language == Language.English)
            {
                return series.ToResource(includeSeasonImages);
            }

            if (language == Language.Original)
            {
                return series.Select(s => ToResource(s, includeSeasonImages)).ToList();
            }

            var translations = _seriesTranslationService
                .GetAllTranslationsForLanguage(language)
                .ToDictionaryIgnoreDuplicates(x => x.SeriesId);

            return series.Select(s => s.ToResource(GetTranslationFromDict(translations, s, language), includeSeasonImages)).ToList();
        }

        private Language GetConfiguredLanguage()
        {
            if (!_configService.UseSeriesInfoLanguage)
            {
                return Language.English;
            }

            return (Language)_configService.SeriesInfoLanguage;
        }

        private SeriesTranslation GetSeriesTranslation(NzbDrone.Core.Tv.Series series)
        {
            var language = GetConfiguredLanguage();

            if (language == Language.Original)
            {
                language = series.OriginalLanguage;
            }

            if (language == Language.English)
            {
                return null;
            }

            return series.Translations?.FirstOrDefault(t => t.Language == language) ??
                   (series.Id > 0 ? _seriesTranslationService.GetAllTranslationsForSeries(series.Id).FirstOrDefault(t => t.Language == language) : null);
        }

        private SeriesTranslation GetTranslationFromDict(Dictionary<int, SeriesTranslation> translations, NzbDrone.Core.Tv.Series series, Language language)
        {
            if (language == Language.English)
            {
                return null;
            }

            var embeddedTranslation = series.Translations?.FirstOrDefault(t => t.Language == language);

            if (embeddedTranslation != null)
            {
                return embeddedTranslation;
            }

            if (!translations.TryGetValue(series.Id, out var translation))
            {
                return null;
            }

            return translation;
        }
    }
}
