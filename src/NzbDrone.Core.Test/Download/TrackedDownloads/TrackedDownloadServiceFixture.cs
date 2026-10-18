using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.TorrentRss;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
using NzbDrone.Core.Tv.Events;

namespace NzbDrone.Core.Test.Download.TrackedDownloads
{
    [TestFixture]
    public class TrackedDownloadServiceFixture : CoreTest<TrackedDownloadService>
    {
        private void GivenDownloadHistory()
        {
            Mocker.GetMock<IHistoryService>()
                .Setup(s => s.FindByDownloadId(It.Is<string>(sr => sr == "35238")))
                .Returns([
                    new EpisodeHistory()
                    {
                        DownloadId = "35238",
                        SourceTitle = "TV Series S01",
                        SeriesId = 5,
                        EpisodeId = 4
                    }
                ]);
        }

        [Test]
        public void should_track_downloads_using_the_source_title_if_it_cannot_be_found_using_the_download_title()
        {
            GivenDownloadHistory();

            var remoteEpisode = new RemoteEpisode
            {
                Series = new Series() { Id = 5 },
                Episodes = [new Episode { Id = 4 }],
                ParsedEpisodeInfo = new ParsedEpisodeInfo()
                {
                    SeriesTitle = "TV Series",
                    SeasonNumbers = [1]
                },
                MappedSeasonNumber = 1
            };

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.Map(It.Is<ParsedEpisodeInfo>(i => i.SeasonNumbers.Contains(1) && i.SeriesTitle == "TV Series"), It.IsAny<int>(), It.IsAny<IEnumerable<int>>()))
                  .Returns(remoteEpisode);

            var client = new DownloadClientDefinition()
            {
                Id = 1,
                Protocol = DownloadProtocol.Torrent
            };

            var item = new DownloadClientItem()
            {
                Title = "The torrent release folder",
                DownloadId = "35238",
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Protocol = client.Protocol,
                    Id = client.Id,
                    Name = client.Name
                }
            };

            var trackedDownload = Subject.TrackDownload(client, item);

            trackedDownload.Should().NotBeNull();
            trackedDownload.RemoteEpisode.Should().NotBeNull();
            trackedDownload.RemoteEpisode.Series.Should().NotBeNull();
            trackedDownload.RemoteEpisode.Series.Id.Should().Be(5);
            trackedDownload.RemoteEpisode.Episodes.First().Id.Should().Be(4);
            trackedDownload.RemoteEpisode.ParsedEpisodeInfo.SeasonNumbers.Should().Equal(1);
            trackedDownload.RemoteEpisode.MappedSeasonNumber.Should().Be(1);
        }

        [Test]
        public void should_not_mark_download_as_imported_when_imported_history_does_not_match_current_files()
        {
            var remoteEpisode = new RemoteEpisode
            {
                Series = new Series { Id = 5 },
                Episodes = new List<Episode>
                {
                    new Episode { Id = 4 },
                    new Episode { Id = 5 }
                },
                ParsedEpisodeInfo = new ParsedEpisodeInfo
                {
                    SeriesTitle = "TV Series",
                    SeasonNumber = 1,
                    EpisodeNumbers = new[] { 1, 2 }
                },
                MappedSeasonNumber = 1
            };

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId("35238"))
                  .Returns(new List<EpisodeHistory>());

            Mocker.GetMock<IDownloadHistoryService>()
                  .Setup(s => s.GetLatestDownloadHistoryItem("35238"))
                  .Returns(new DownloadHistory
                  {
                      DownloadId = "35238",
                      EventType = DownloadHistoryEventType.DownloadImported
                  });

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.Map(It.IsAny<ParsedEpisodeInfo>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), null))
                  .Returns(remoteEpisode);

            Mocker.GetMock<ISeriesService>()
                  .Setup(s => s.GetSeries(It.IsAny<int>()))
                  .Returns(remoteEpisode.Series);

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.Map(It.IsAny<ParsedEpisodeInfo>(), remoteEpisode.Series))
                  .Returns(remoteEpisode);

            Mocker.GetMock<ITrackedDownloadAlreadyImported>()
                  .Setup(s => s.IsImported(It.IsAny<TrackedDownload>(), It.IsAny<List<EpisodeHistory>>()))
                  .Returns(false);

            var client = new DownloadClientDefinition
            {
                Id = 1,
                Protocol = DownloadProtocol.Torrent
            };

            var item = new DownloadClientItem
            {
                Title = "TV.Series.S01E01-E02.720p.HDTV",
                DownloadId = "35238",
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Protocol = client.Protocol,
                    Id = client.Id,
                    Name = client.Name
                }
            };

            var trackedDownload = Subject.TrackDownload(client, item);

            trackedDownload.State.Should().Be(TrackedDownloadState.Downloading);
        }

        [Test]
        public void should_keep_imported_usenet_history_imported_without_file_revalidation()
        {
            var remoteEpisode = new RemoteEpisode
            {
                Series = new Series { Id = 5 },
                Episodes = new List<Episode> { new Episode { Id = 4 } },
                ParsedEpisodeInfo = new ParsedEpisodeInfo
                {
                    SeriesTitle = "TV Series",
                    SeasonNumber = 1,
                    EpisodeNumbers = new[] { 1 }
                },
                MappedSeasonNumber = 1
            };

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId("35238"))
                  .Returns(new List<EpisodeHistory>());

            Mocker.GetMock<IDownloadHistoryService>()
                  .Setup(s => s.GetLatestDownloadHistoryItem("35238"))
                  .Returns(new DownloadHistory
                  {
                      DownloadId = "35238",
                      EventType = DownloadHistoryEventType.DownloadImported
                  });

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.Map(It.IsAny<ParsedEpisodeInfo>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), null))
                  .Returns(remoteEpisode);

            Mocker.GetMock<ISeriesService>()
                  .Setup(s => s.GetSeries(It.IsAny<int>()))
                  .Returns(remoteEpisode.Series);

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.Map(It.IsAny<ParsedEpisodeInfo>(), remoteEpisode.Series))
                  .Returns(remoteEpisode);

            var client = new DownloadClientDefinition
            {
                Id = 1,
                Protocol = DownloadProtocol.Usenet
            };

            var item = new DownloadClientItem
            {
                Title = "TV.Series.S01E01.720p.HDTV",
                DownloadId = "35238",
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Protocol = client.Protocol,
                    Id = client.Id,
                    Name = client.Name
                }
            };

            var trackedDownload = Subject.TrackDownload(client, item);

            trackedDownload.State.Should().Be(TrackedDownloadState.Imported);
            Mocker.GetMock<ITrackedDownloadAlreadyImported>()
                  .Verify(s => s.IsImported(It.IsAny<TrackedDownload>(), It.IsAny<List<EpisodeHistory>>()), Times.Never());
        }

        [Test]
        public void should_update_cached_completed_usenet_download_to_imported_from_history()
        {
            var remoteEpisode = new RemoteEpisode
            {
                Series = new Series { Id = 5 },
                Episodes = new List<Episode> { new Episode { Id = 4 } },
                ParsedEpisodeInfo = new ParsedEpisodeInfo
                {
                    SeriesTitle = "TV Series",
                    SeasonNumber = 1,
                    EpisodeNumbers = new[] { 1 }
                },
                MappedSeasonNumber = 1
            };

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId("35238"))
                  .Returns(new List<EpisodeHistory>());

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.Map(It.IsAny<ParsedEpisodeInfo>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), null))
                  .Returns(remoteEpisode);

            Mocker.GetMock<ISeriesService>()
                  .Setup(s => s.GetSeries(It.IsAny<int>()))
                  .Returns(remoteEpisode.Series);

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.Map(It.IsAny<ParsedEpisodeInfo>(), remoteEpisode.Series))
                  .Returns(remoteEpisode);

            var client = new DownloadClientDefinition
            {
                Id = 1,
                Protocol = DownloadProtocol.Usenet
            };

            var item = new DownloadClientItem
            {
                Title = "TV.Series.S01E01.720p.HDTV",
                DownloadId = "35238",
                Status = DownloadItemStatus.Completed,
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Protocol = client.Protocol,
                    Id = client.Id,
                    Name = client.Name
                }
            };

            var trackedDownload = Subject.TrackDownload(client, item);

            trackedDownload.State = TrackedDownloadState.ImportBlocked;
            trackedDownload.Warn("Unable to import automatically");

            Mocker.GetMock<IDownloadHistoryService>()
                  .Setup(s => s.GetLatestDownloadHistoryItem("35238"))
                  .Returns(new DownloadHistory
                  {
                      DownloadId = "35238",
                      EventType = DownloadHistoryEventType.DownloadImported
                  });

            var updatedTrackedDownload = Subject.TrackDownload(client, item);

            updatedTrackedDownload.Should().BeSameAs(trackedDownload);
            updatedTrackedDownload.State.Should().Be(TrackedDownloadState.Imported);
            updatedTrackedDownload.Status.Should().Be(TrackedDownloadStatus.Ok);
            updatedTrackedDownload.StatusMessages.Should().BeEmpty();
        }

        [Test]
        public void should_mark_download_as_imported_when_imported_history_matches_current_files()
        {
            var historyItems = new List<EpisodeHistory>();
            var remoteEpisode = new RemoteEpisode
            {
                Series = new Series { Id = 5 },
                Episodes = new List<Episode> { new Episode { Id = 4 } },
                ParsedEpisodeInfo = new ParsedEpisodeInfo
                {
                    SeriesTitle = "TV Series",
                    SeasonNumber = 1,
                    EpisodeNumbers = new[] { 1 }
                },
                MappedSeasonNumber = 1
            };

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId("35238"))
                  .Returns(historyItems);

            Mocker.GetMock<IDownloadHistoryService>()
                  .Setup(s => s.GetLatestDownloadHistoryItem("35238"))
                  .Returns(new DownloadHistory
                  {
                      DownloadId = "35238",
                      EventType = DownloadHistoryEventType.DownloadImported
                  });

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.Map(It.IsAny<ParsedEpisodeInfo>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), null))
                  .Returns(remoteEpisode);

            Mocker.GetMock<ISeriesService>()
                  .Setup(s => s.GetSeries(It.IsAny<int>()))
                  .Returns(remoteEpisode.Series);

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.Map(It.IsAny<ParsedEpisodeInfo>(), remoteEpisode.Series))
                  .Returns(remoteEpisode);

            Mocker.GetMock<ITrackedDownloadAlreadyImported>()
                  .Setup(s => s.IsImported(It.IsAny<TrackedDownload>(), historyItems))
                  .Returns(true);

            var client = new DownloadClientDefinition
            {
                Id = 1,
                Protocol = DownloadProtocol.Torrent
            };

            var item = new DownloadClientItem
            {
                Title = "TV.Series.S01E01.720p.HDTV",
                DownloadId = "35238",
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Protocol = client.Protocol,
                    Id = client.Id,
                    Name = client.Name
                }
            };

            var trackedDownload = Subject.TrackDownload(client, item);

            trackedDownload.State.Should().Be(TrackedDownloadState.Imported);
        }

        [Test]
        public void should_set_indexer()
        {
            var episodeHistory = new EpisodeHistory()
            {
                DownloadId = "35238",
                SourceTitle = "TV Series S01",
                SeriesId = 5,
                EpisodeId = 4,
                EventType = EpisodeHistoryEventType.Grabbed,
            };
            episodeHistory.Data.Add("indexer", "MyIndexer (Prowlarr)");
            Mocker.GetMock<IHistoryService>()
                .Setup(s => s.FindByDownloadId(It.Is<string>(sr => sr == "35238")))
                .Returns([
                    episodeHistory
                ]);

            var indexerDefinition = new IndexerDefinition
            {
                Id = 1,
                Name = "MyIndexer (Prowlarr)",
                Settings = new TorrentRssIndexerSettings { MultiLanguages = new List<int> { Language.Original.Id, Language.French.Id } }
            };
            Mocker.GetMock<IIndexerFactory>()
                .Setup(v => v.Get(indexerDefinition.Id))
                .Returns(indexerDefinition);
            Mocker.GetMock<IIndexerFactory>()
                .Setup(v => v.All())
                .Returns([indexerDefinition]);

            var remoteEpisode = new RemoteEpisode
            {
                Series = new Series() { Id = 5 },
                Episodes = [new Episode { Id = 4 }],
                ParsedEpisodeInfo = new ParsedEpisodeInfo()
                {
                    SeriesTitle = "TV Series",
                    SeasonNumbers = [1]
                },
                MappedSeasonNumber = 1
            };

            Mocker.GetMock<IParsingService>()
                .Setup(s => s.Map(It.IsAny<ParsedEpisodeInfo>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), null))
                .Returns(remoteEpisode);

            var client = new DownloadClientDefinition()
            {
                Id = 1,
                Protocol = DownloadProtocol.Torrent
            };

            var item = new DownloadClientItem()
            {
                Title = "TV.Series.S01.MULTi.1080p.WEB.H265-RlsGroup",
                DownloadId = "35238",
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Protocol = client.Protocol,
                    Id = client.Id,
                    Name = client.Name
                }
            };

            var trackedDownload = Subject.TrackDownload(client, item);

            trackedDownload.Should().NotBeNull();
            trackedDownload.RemoteEpisode.Should().NotBeNull();
            trackedDownload.RemoteEpisode.Release.Should().NotBeNull();
            trackedDownload.RemoteEpisode.Release.Indexer.Should().Be("MyIndexer (Prowlarr)");
        }

        [Test]
        public void should_parse_as_special_when_source_title_parsing_fails()
        {
            var remoteEpisode = new RemoteEpisode
            {
                Series = new Series() { Id = 5 },
                Episodes = [new Episode { Id = 4 }],
                ParsedEpisodeInfo = new ParsedEpisodeInfo()
                {
                    SeriesTitle = "TV Series",
                    SeasonNumbers = [0],
                    EpisodeNumbers = [1]
                },
                MappedSeasonNumber = 0
            };

            Mocker.GetMock<IHistoryService>()
                .Setup(s => s.FindByDownloadId(It.Is<string>(sr => sr == "35238")))
                .Returns([
                    new EpisodeHistory()
                    {
                        DownloadId = "35238",
                        SourceTitle = "TV Series Special",
                        SeriesId = 5,
                        EpisodeId = 4
                    }
                ]);

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.Map(It.Is<ParsedEpisodeInfo>(i => i.SeasonNumbers.Contains(0) && i.SeriesTitle == "TV Series"), It.IsAny<int>(), It.IsAny<IEnumerable<int>>()))
                  .Returns(remoteEpisode);

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.ParseSpecialEpisodeTitle(It.IsAny<ParsedEpisodeInfo>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), null))
                  .Returns(remoteEpisode.ParsedEpisodeInfo);

            var client = new DownloadClientDefinition()
            {
                Id = 1,
                Protocol = DownloadProtocol.Torrent
            };

            var item = new DownloadClientItem()
            {
                Title = "The torrent release folder",
                DownloadId = "35238",
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Protocol = client.Protocol,
                    Id = client.Id,
                    Name = client.Name
                }
            };

            var trackedDownload = Subject.TrackDownload(client, item);

            trackedDownload.Should().NotBeNull();
            trackedDownload.RemoteEpisode.Should().NotBeNull();
            trackedDownload.RemoteEpisode.Series.Should().NotBeNull();
            trackedDownload.RemoteEpisode.Series.Id.Should().Be(5);
            trackedDownload.RemoteEpisode.Episodes.First().Id.Should().Be(4);
            trackedDownload.RemoteEpisode.ParsedEpisodeInfo.SeasonNumbers.Should().Equal(0);
            trackedDownload.RemoteEpisode.MappedSeasonNumber.Should().Be(0);
        }

        [Test]
        public void should_unmap_tracked_download_if_episode_deleted()
        {
            GivenDownloadHistory();

            var remoteEpisode = new RemoteEpisode
                                {
                                    Series = new Series() { Id = 5 },
                                    Episodes = [new Episode { Id = 4 }],
                                    ParsedEpisodeInfo = new ParsedEpisodeInfo()
                                                        {
                                                            SeriesTitle = "TV Series",
                                                            SeasonNumbers = [1],
                                                            EpisodeNumbers = new[] { 1 }
                                                        },
                                    MappedSeasonNumber = 0
                                };

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.Map(It.IsAny<ParsedEpisodeInfo>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), null))
                  .Returns(remoteEpisode);

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId(It.IsAny<string>()))
                  .Returns([]);

            var client = new DownloadClientDefinition()
            {
                Id = 1,
                Protocol = DownloadProtocol.Torrent
            };

            var item = new DownloadClientItem()
            {
                Title = "TV Series - S01E01",
                DownloadId = "12345",
                DownloadClientInfo = new DownloadClientItemClientInfo
                                     {
                                         Id = 1,
                                         Type = "Blackhole",
                                         Name = "Blackhole Client",
                                         Protocol = DownloadProtocol.Torrent
                                     }
            };

            Subject.TrackDownload(client, item);
            Subject.GetTrackedDownloads().Should().HaveCount(1);

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.Map(It.IsAny<ParsedEpisodeInfo>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), null))
                  .Returns(default(RemoteEpisode));

            Subject.Handle(new EpisodeInfoRefreshedEvent(remoteEpisode.Series, new List<Episode>(), new List<Episode>(), remoteEpisode.Episodes));

            var trackedDownloads = Subject.GetTrackedDownloads();
            trackedDownloads.Should().HaveCount(1);
            trackedDownloads.First().RemoteEpisode.Should().BeNull();
        }

        [Test]
        public void should_not_throw_when_processing_deleted_episodes()
        {
            GivenDownloadHistory();

            var remoteEpisode = new RemoteEpisode
            {
                Series = new Series() { Id = 5 },
                Episodes = [new Episode { Id = 4 }],
                ParsedEpisodeInfo = new ParsedEpisodeInfo()
                {
                    SeriesTitle = "TV Series",
                    SeasonNumbers = [1],
                    EpisodeNumbers = [1]
                },
                MappedSeasonNumber = 0
            };

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.Map(It.IsAny<ParsedEpisodeInfo>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), null))
                  .Returns(default(RemoteEpisode));

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId(It.IsAny<string>()))
                  .Returns([]);

            var client = new DownloadClientDefinition()
            {
                Id = 1,
                Protocol = DownloadProtocol.Torrent
            };

            var item = new DownloadClientItem()
            {
                Title = "TV Series - S01E01",
                DownloadId = "12345",
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Id = 1,
                    Type = "Blackhole",
                    Name = "Blackhole Client",
                    Protocol = DownloadProtocol.Torrent
                }
            };

            Subject.TrackDownload(client, item);
            Subject.GetTrackedDownloads().Should().HaveCount(1);

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.Map(It.IsAny<ParsedEpisodeInfo>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), null))
                  .Returns(default(RemoteEpisode));

            Subject.Handle(new EpisodeInfoRefreshedEvent(remoteEpisode.Series, new List<Episode>(), new List<Episode>(), remoteEpisode.Episodes));

            var trackedDownloads = Subject.GetTrackedDownloads();
            trackedDownloads.Should().HaveCount(1);
            trackedDownloads.First().RemoteEpisode.Should().BeNull();
        }

        [Test]
        public void should_not_throw_when_processing_deleted_series()
        {
            GivenDownloadHistory();

            var remoteEpisode = new RemoteEpisode
            {
                Series = new Series() { Id = 5 },
                Episodes = [new Episode { Id = 4 }],
                ParsedEpisodeInfo = new ParsedEpisodeInfo()
                {
                    SeriesTitle = "TV Series",
                    SeasonNumbers = [1],
                    EpisodeNumbers = [1]
                },
                MappedSeasonNumber = 0
            };

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.Map(It.IsAny<ParsedEpisodeInfo>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), null))
                  .Returns(default(RemoteEpisode));

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId(It.IsAny<string>()))
                  .Returns([]);

            var client = new DownloadClientDefinition()
            {
                Id = 1,
                Protocol = DownloadProtocol.Torrent
            };

            var item = new DownloadClientItem()
            {
                Title = "TV Series - S01E01",
                DownloadId = "12345",
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Id = 1,
                    Type = "Blackhole",
                    Name = "Blackhole Client",
                    Protocol = DownloadProtocol.Torrent
                }
            };

            Subject.TrackDownload(client, item);
            Subject.GetTrackedDownloads().Should().HaveCount(1);

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.Map(It.IsAny<ParsedEpisodeInfo>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), null))
                  .Returns(default(RemoteEpisode));

            Subject.Handle(new SeriesDeletedEvent([remoteEpisode.Series], true, true));

            var trackedDownloads = Subject.GetTrackedDownloads();
            trackedDownloads.Should().HaveCount(1);
            trackedDownloads.First().RemoteEpisode.Should().BeNull();
        }

        [Test]
        public void should_retrack_as_downloading_when_grabbed_again_after_being_imported()
        {
            var history = new List<EpisodeHistory>
            {
                new()
                {
                    EpisodeId = 4,
                    EventType = EpisodeHistoryEventType.DownloadFolderImported,
                    Data = new Dictionary<string, string>
                    {
                        { "FileId", "10" },
                        { "Size", "1024" }
                    }
                }
            };
            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.GetFiles(It.IsAny<IEnumerable<int>>()))
                .Returns(new List<EpisodeFile> { new() { Id = 10, Size = 1024 } });
            Mocker.SetConstant<ITrackedDownloadAlreadyImported>(new TrackedDownloadAlreadyImported(
                Mocker.GetMock<IMediaFileService>().Object, TestLogger));

            Mocker.GetMock<IHistoryService>()
                .Setup(s => s.FindByDownloadId(It.Is<string>(sr => sr == "35238")))
                .Returns(history);

            Mocker.GetMock<IDownloadHistoryService>()
                .Setup(s => s.GetLatestDownloadHistoryItem(It.Is<string>(sr => sr == "35238")))
                .Returns(new DownloadHistory
                {
                    SeriesId = 5,
                    EventType = DownloadHistoryEventType.DownloadImported,
                });

            var remoteEpisode = new RemoteEpisode
            {
                Series = new Series { Id = 5 },
                Episodes = [new Episode { Id = 4 }],
                ParsedEpisodeInfo = new ParsedEpisodeInfo
                {
                    SeriesTitle = "TV Series",
                    SeasonNumbers = [1]
                },
                MappedSeasonNumber = 1
            };

            Mocker.GetMock<IParsingService>()
                .Setup(s => s.Map(It.Is<ParsedEpisodeInfo>(i => i.SeasonNumbers.Contains(1) && i.SeriesTitle == "TV Series"), It.IsAny<Series>()))
                .Returns(remoteEpisode);

            var client = new DownloadClientDefinition
            {
                Id = 1,
                Protocol = DownloadProtocol.Torrent
            };

            var item = new DownloadClientItem
            {
                Title = "TV Series - S01E01",
                DownloadId = "35238",
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Protocol = client.Protocol,
                    Id = client.Id,
                    Name = client.Name
                }
            };

            var imported = Subject.TrackDownload(client, item);
            imported.State.Should().Be(TrackedDownloadState.Imported);
            Mocker.GetMock<IMediaFileService>()
                .Verify(s => s.GetFiles(It.Is<IEnumerable<int>>(ids => ids.SequenceEqual(new[] { 10 }))), Times.Once());

            Subject.Handle(new EpisodeGrabbedEvent(remoteEpisode) { DownloadId = "35238" });

            Mocker.GetMock<IDownloadHistoryService>()
                .Setup(s => s.GetLatestDownloadHistoryItem(It.Is<string>(sr => sr == "35238")))
                .Returns(new DownloadHistory
                {
                    SeriesId = 5,
                    EventType = DownloadHistoryEventType.DownloadGrabbed,
                });

            var regrabbed = Subject.TrackDownload(client, item);
            regrabbed.State.Should().Be(TrackedDownloadState.Downloading);
        }

        [Test]
        public void should_track_downloads_using_the_series_id_for_already_imported_downloads()
        {
            Mocker.GetMock<IHistoryService>()
                .Setup(s => s.FindByDownloadId(It.Is<string>(sr => sr == "35238")))
                .Returns([]);

            Mocker.GetMock<IDownloadHistoryService>()
                .Setup(s => s.GetLatestDownloadHistoryItem(It.Is<string>(sr => sr == "35238")))
                .Returns(new DownloadHistory
                {
                    SeriesId = 5,
                    EventType = DownloadHistoryEventType.DownloadImported,
                });

            var remoteEpisode = new RemoteEpisode
            {
                Series = new Series { Id = 5 },
                Episodes = [new Episode { Id = 4 }],
                ParsedEpisodeInfo = new ParsedEpisodeInfo
                {
                    SeriesTitle = "TV Series",
                    SeasonNumbers = [1]
                },
                MappedSeasonNumber = 1
            };

            Mocker.GetMock<IParsingService>()
                .Setup(s => s.Map(It.Is<ParsedEpisodeInfo>(i => i.SeasonNumbers.Contains(1) && i.SeriesTitle == "TV Series"), It.IsAny<Series>()))
                .Returns(remoteEpisode);

            var client = new DownloadClientDefinition
            {
                Id = 1,
                Protocol = DownloadProtocol.Torrent
            };

            var item = new DownloadClientItem
            {
                Title = "TV Series - S01E01",
                DownloadId = "35238",
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Protocol = client.Protocol,
                    Id = client.Id,
                    Name = client.Name
                }
            };

            var trackedDownload = Subject.TrackDownload(client, item);

            trackedDownload.Should().NotBeNull();
            trackedDownload.RemoteEpisode.Should().NotBeNull();
            trackedDownload.RemoteEpisode.Series.Should().NotBeNull();
            trackedDownload.RemoteEpisode.Series.Id.Should().Be(5);
            trackedDownload.RemoteEpisode.Episodes.First().Id.Should().Be(4);
            trackedDownload.RemoteEpisode.ParsedEpisodeInfo.SeasonNumbers.Should().Equal(1);
            trackedDownload.RemoteEpisode.MappedSeasonNumber.Should().Be(1);
        }

        [Test]
        public void should_stop_tracking_downloads_missing_from_their_download_client()
        {
            GivenTrackedDownload(1, "present");
            GivenTrackedDownload(1, "missing");
            GivenTrackedDownload(2, "other");

            Subject.StopTrackingMissing(1, new List<string> { "present" });

            Subject.Find("present").Should().NotBeNull();
            Subject.Find("missing").Should().BeNull();
            Subject.Find("other").Should().NotBeNull();
        }

        private void GivenTrackedDownload(int downloadClientId, string downloadId)
        {
            var client = new DownloadClientDefinition
            {
                Id = downloadClientId,
                Protocol = DownloadProtocol.Torrent
            };

            var item = new DownloadClientItem
            {
                Title = "Unparsable",
                DownloadId = downloadId,
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Protocol = client.Protocol,
                    Id = client.Id
                }
            };

            Subject.TrackDownload(client, item);
        }
    }
}
