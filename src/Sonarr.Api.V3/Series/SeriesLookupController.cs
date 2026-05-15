using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.SeriesStats;
using Sonarr.Http;

namespace Sonarr.Api.V3.Series
{
    [V3ApiController("series/lookup")]
    public class SeriesLookupController : Controller
    {
        private readonly ISearchForNewSeries _searchProxy;
        private readonly IBuildFileNames _fileNameBuilder;
        private readonly IMapCoversToLocal _coverMapper;
        private readonly ISeriesResourceService _seriesResourceService;

        public SeriesLookupController(ISearchForNewSeries searchProxy,
                                      IBuildFileNames fileNameBuilder,
                                      IMapCoversToLocal coverMapper,
                                      ISeriesResourceService seriesResourceService)
        {
            _searchProxy = searchProxy;
            _fileNameBuilder = fileNameBuilder;
            _coverMapper = coverMapper;
            _seriesResourceService = seriesResourceService;
        }

        [HttpGet]
        public IEnumerable<SeriesResource> Search([FromQuery] string term)
        {
            var tvDbResults = _searchProxy.SearchForNewSeries(term);
            return MapToResource(tvDbResults);
        }

        private IEnumerable<SeriesResource> MapToResource(IEnumerable<NzbDrone.Core.Tv.Series> series)
        {
            foreach (var currentSeries in series)
            {
                var resource = _seriesResourceService.ToResource(currentSeries);

                _coverMapper.ConvertToLocalUrls(resource.Id, resource.Images);

                var poster = currentSeries.Images.FirstOrDefault(c => c.CoverType == MediaCoverTypes.Poster);

                if (poster != null)
                {
                    resource.RemotePoster = poster.RemoteUrl;
                }

                resource.Folder = _fileNameBuilder.GetSeriesFolder(currentSeries);
                resource.Statistics = new SeriesStatistics().ToResource(resource.Seasons);

                yield return resource;
            }
        }
    }
}
