using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Tv;
using Sonarr.Http;

namespace Sonarr.Api.V5.Series
{
    [V5ApiController("series/import")]
    public class SeriesImportController : Controller
    {
        private readonly IAddSeriesService _addSeriesService;
        private readonly ISeriesResourceService _seriesResourceService;

        public SeriesImportController(IAddSeriesService addSeriesService, ISeriesResourceService seriesResourceService)
        {
            _addSeriesService = addSeriesService;
            _seriesResourceService = seriesResourceService;
        }

        [HttpPost]
        [Consumes("application/json")]
        [Produces("application/json")]
        public Ok<List<SeriesResource>> Import([FromBody] List<SeriesResource> resource)
        {
            var newSeries = resource.ToModel();

            return TypedResults.Ok(_seriesResourceService.ToResource(_addSeriesService.AddSeries(newSeries)));
        }
    }
}
