using System.Collections.Generic;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Configuration;
using NzbDrone.Test.Common;
using Sonarr.Api.V3.Config;
using Sonarr.Api.V5.Settings;

namespace NzbDrone.Api.Test.SettingsTests
{
    [TestFixture(3)]
    [TestFixture(5)]
    public class RecentlyAddedSettingsFixture : TestBase
    {
        private readonly int _apiVersion;
        private bool _enabled;

        public RecentlyAddedSettingsFixture(int apiVersion)
        {
            _apiVersion = apiVersion;
        }

        [SetUp]
        public void Setup()
        {
            _enabled = false;
            Mocker.GetMock<IConfigService>().SetupGet(config => config.MarkImportedMediaAsRecentlyAdded)
                .Returns(() => _enabled);
            Mocker.GetMock<IConfigService>().Setup(config => config.SaveConfigDictionary(It.IsAny<Dictionary<string, object>>()))
                .Callback<Dictionary<string, object>>(values => _enabled = (bool)values[nameof(IConfigService.MarkImportedMediaAsRecentlyAdded)]);
        }

        [Test]
        public void should_expose_disabled_default_in_general_settings()
        {
            ReadSetting().Should().BeFalse();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_round_trip_the_setting_through_save_and_get(bool enabled)
        {
            _enabled = !enabled;
            var json = new JsonObject
            {
                ["id"] = 1,
                ["trustedNetworks"] = string.Empty,
                ["markImportedMediaAsRecentlyAdded"] = enabled
            }.ToJsonString();

            if (_apiVersion == 3)
            {
                Mocker.Resolve<HostConfigController>().SaveHostConfig(STJson.Deserialize<HostConfigResource>(json));
            }
            else
            {
                var controller = Mocker.Resolve<GeneralSettingsController>();
                controller.Url = new Mock<IUrlHelper>().Object;
                controller.SaveSettings(STJson.Deserialize<GeneralSettingsResource>(json));
            }

            ReadSetting().Should().Be(enabled);
            Mocker.GetMock<IConfigService>().Verify(
                config => config.SaveConfigDictionary(
                    It.Is<Dictionary<string, object>>(values => (bool)values[nameof(IConfigService.MarkImportedMediaAsRecentlyAdded)] == enabled)),
                Times.Once());
        }

        private bool ReadSetting()
        {
            object resource = _apiVersion == 3
                ? Mocker.Resolve<HostConfigController>().GetHostConfig()
                : Mocker.Resolve<GeneralSettingsController>().GetConfig().Value;

            return JsonNode.Parse(STJson.ToJson(resource))["markImportedMediaAsRecentlyAdded"].GetValue<bool>();
        }
    }
}
