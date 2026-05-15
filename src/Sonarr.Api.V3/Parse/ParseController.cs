using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download.Aggregation;
using NzbDrone.Core.Parser;
using Sonarr.Api.V3.CustomFormats;
using Sonarr.Api.V3.Episodes;
using Sonarr.Api.V3.Series;
using Sonarr.Http;

namespace Sonarr.Api.V3.Parse
{
    [V3ApiController]
    public class ParseController : Controller
    {
        private readonly IParsingService _parsingService;
        private readonly IRemoteEpisodeAggregationService _aggregationService;
        private readonly ICustomFormatCalculationService _formatCalculator;
        private readonly ISeriesResourceService _seriesResourceService;
        private readonly IConfigService _configService;

        public ParseController(IParsingService parsingService,
                               IRemoteEpisodeAggregationService aggregationService,
                               ICustomFormatCalculationService formatCalculator,
                               ISeriesResourceService seriesResourceService,
                               IConfigService configService)
        {
            _parsingService = parsingService;
            _aggregationService = aggregationService;
            _formatCalculator = formatCalculator;
            _seriesResourceService = seriesResourceService;
            _configService = configService;
        }

        [HttpGet]
        [Produces("application/json")]
        public ParseResource Parse(string title, string path)
        {
            if (title.IsNullOrWhiteSpace())
            {
                return null;
            }

            var parsedEpisodeInfo = path.IsNotNullOrWhiteSpace()
                ? Parser.ParsePath(path, _configService.ParseTvdbIdFromReleaseName, _configService.ParseEpisodeNumberOnlyAsSeasonOne)
                : Parser.ParseTitle(title, _configService.ParseTvdbIdFromReleaseName, _configService.ParseEpisodeNumberOnlyAsSeasonOne);

            if (parsedEpisodeInfo == null)
            {
                return new ParseResource
                {
                    Title = title
                };
            }

            var remoteEpisode = _parsingService.Map(parsedEpisodeInfo, 0, 0, null);

            if (remoteEpisode != null)
            {
                _aggregationService.Augment(remoteEpisode);

                remoteEpisode.CustomFormats = _formatCalculator.ParseCustomFormat(remoteEpisode, 0);
                remoteEpisode.CustomFormatScore = remoteEpisode?.Series?.QualityProfile?.Value.CalculateCustomFormatScore(remoteEpisode.CustomFormats) ?? 0;

                return new ParseResource
                {
                    Title = title,
                    ParsedEpisodeInfo = remoteEpisode.ParsedEpisodeInfo,
                    Series = _seriesResourceService.ToResource(remoteEpisode.Series),
                    Episodes = remoteEpisode.Episodes.ToResource(),
                    Languages = remoteEpisode.Languages,
                    CustomFormats = remoteEpisode.CustomFormats?.ToResource(false),
                    CustomFormatScore = remoteEpisode.CustomFormatScore
                };
            }
            else
            {
                return new ParseResource
                {
                    Title = title,
                    ParsedEpisodeInfo = parsedEpisodeInfo
                };
            }
        }
    }
}
