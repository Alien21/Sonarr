using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Tv;
using CoreSeries = NzbDrone.Core.Tv.Series;

namespace Sonarr.Api.V5.Series;

public interface ISeriesResourceService
{
    SeriesResource ToResource(CoreSeries series, bool includeSeasonImages = false);
    List<SeriesResource> ToResource(IEnumerable<CoreSeries> series, bool includeSeasonImages = false);
}

public class SeriesResourceService : ISeriesResourceService
{
    private readonly IConfigService _configService;
    private readonly ISeriesTranslationService _seriesTranslationService;

    public SeriesResourceService(IConfigService configService, ISeriesTranslationService seriesTranslationService)
    {
        _configService = configService;
        _seriesTranslationService = seriesTranslationService;
    }

    public SeriesResource ToResource(CoreSeries series, bool includeSeasonImages = false)
    {
        var language = GetConfiguredLanguage(series);
        var translations = series.Id > 0 && NeedsStoredTranslations(series, language)
            ? _seriesTranslationService.GetTranslations(series.Id)
            : new List<SeriesTranslation>();

        return MapToResource(series, translations, language, includeSeasonImages);
    }

    public List<SeriesResource> ToResource(IEnumerable<CoreSeries> series, bool includeSeasonImages = false)
    {
        var models = series.ToList();
        var translations = models.Any(s => s.Id > 0 && NeedsStoredTranslations(s, GetConfiguredLanguage(s)))
            ? _seriesTranslationService.GetTranslations()
            : new List<SeriesTranslation>();
        var translationsBySeries = translations.ToLookup(t => t.SeriesId);

        return models.Select(s => MapToResource(s, translationsBySeries[s.Id], GetConfiguredLanguage(s), includeSeasonImages)).ToList();
    }

    private bool NeedsStoredTranslations(CoreSeries series, Language language)
    {
        var translations = series.Translations ?? new List<SeriesTranslation>();

        return !translations.Any(t => t.Language == Language.English && t.Title.IsNotNullOrWhiteSpace()) ||
               (_configService.UseSeriesInfoLanguage && !translations.Any(t => t.Language == language));
    }

    private SeriesResource MapToResource(CoreSeries series, IEnumerable<SeriesTranslation> storedTranslations, Language language, bool includeSeasonImages)
    {
        var translations = (series.Translations ?? new List<SeriesTranslation>()).Concat(storedTranslations).ToList();
        var translation = _configService.UseSeriesInfoLanguage
            ? translations.FirstOrDefault(t => t.Language == language)
            : null;
        var defaultTitle = translations.FirstOrDefault(t => t.Language == Language.English && t.Title.IsNotNullOrWhiteSpace())?.Title;

        return series.ToResource(translation, includeSeasonImages, defaultTitle);
    }

    private Language GetConfiguredLanguage(CoreSeries series)
    {
        var language = _configService.UseSeriesInfoLanguage ? (Language)_configService.SeriesInfoLanguage : Language.English;

        return language == Language.Original ? series.OriginalLanguage : language;
    }
}
