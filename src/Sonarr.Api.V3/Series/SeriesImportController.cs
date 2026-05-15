using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Tv;
using Sonarr.Http;

namespace Sonarr.Api.V3.Series
{
    [V3ApiController("series/import")]
    public class SeriesImportController : Controller
    {
        private readonly IAddSeriesService _addSeriesService;
        private readonly ISeriesResourceService _seriesResourceService;

        public SeriesImportController(IAddSeriesService addSeriesService,
                                      ISeriesResourceService seriesResourceService)
        {
            _addSeriesService = addSeriesService;
            _seriesResourceService = seriesResourceService;
        }

        [HttpPost]
        public object Import([FromBody] List<SeriesResource> resource)
        {
            var newSeries = resource.ToModel();

            return _seriesResourceService.ToResource(_addSeriesService.AddSeries(newSeries));
        }
    }
}
