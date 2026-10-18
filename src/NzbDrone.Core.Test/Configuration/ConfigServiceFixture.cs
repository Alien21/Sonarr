using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Languages;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.Configuration
{
    [TestFixture]
    public class ConfigServiceFixture : TestBase<ConfigService>
    {
        [SetUp]
        public void SetUp()
        {
        }

        [Test]
        public void Add_new_value_to_database()
        {
            const string key = "RssSyncInterval";
            const int value = 12;

            Subject.RssSyncInterval = value;

            AssertUpsert(key, value);
        }

        [Test]
        public void Get_value_should_return_default_when_no_value()
        {
            Subject.RssSyncInterval.Should().Be(15);
        }

        [Test]
        public void get_value_with_persist_should_store_default_value()
        {
            var salt = Subject.HmacSalt;
            salt.Should().NotBeNullOrWhiteSpace();
            AssertUpsert("HmacSalt", salt);
        }

        [Test]
        public void get_value_with_out_persist_should_not_store_default_value()
        {
            var interval = Subject.RssSyncInterval;
            interval.Should().Be(15);
            Mocker.GetMock<IConfigRepository>().Verify(c => c.Insert(It.IsAny<Config>()), Times.Never());
        }

        [Test]
        public void PreferredMetadataLanguage_should_default_to_english()
        {
            Subject.PreferredMetadataLanguage.Should().Be((int)Language.English);
        }

        [Test]
        public void PreferredMetadataLanguage_should_store_value()
        {
            var value = (int)Language.French;

            Subject.PreferredMetadataLanguage = value;

            AssertUpsert("PreferredMetadataLanguage", value);
        }

        [Test]
        public void should_read_legacy_series_info_language_when_preferred_language_is_missing()
        {
            Mocker.GetMock<IConfigRepository>().Setup(c => c.All()).Returns(new List<Config>
            {
                new Config { Key = "seriesinfolanguage", Value = Language.Czech.Id.ToString() }
            });

            Subject.PreferredMetadataLanguage.Should().Be(Language.Czech.Id);
            Subject.SeriesInfoLanguage.Should().Be(Language.Czech.Id);
        }

        [TestCase("SeriesInfoLanguage")]
        [TestCase("seriesinfolanguage")]
        public void should_save_legacy_language_settings_to_the_preferred_language_key(string key)
        {
            Mocker.GetMock<IConfigRepository>().Setup(c => c.All()).Returns(new List<Config>
            {
                new Config { Key = "seriesinfolanguage", Value = Language.Czech.Id.ToString() },
                new Config { Key = "preferredmetadatalanguage", Value = Language.German.Id.ToString() }
            });

            Subject.SeriesInfoLanguage.Should().Be(Language.German.Id);
            Subject.SaveConfigDictionary(new Dictionary<string, object> { [key] = Language.French.Id });

            AssertUpsert("PreferredMetadataLanguage", Language.French.Id);
            Mocker.GetMock<IConfigRepository>().Verify(c => c.Upsert("seriesinfolanguage", It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void should_prefer_the_canonical_language_when_both_setting_names_are_saved()
        {
            Subject.SaveConfigDictionary(new Dictionary<string, object>
            {
                [nameof(ConfigService.SeriesInfoLanguage)] = Language.Czech.Id,
                [nameof(ConfigService.PreferredMetadataLanguage)] = Language.French.Id
            });

            AssertUpsert("PreferredMetadataLanguage", Language.French.Id);
            Mocker.GetMock<IConfigRepository>().Verify(c => c.Upsert("preferredmetadatalanguage", It.IsAny<string>()), Times.Once());
            Mocker.GetMock<IConfigRepository>().Verify(c => c.Upsert("seriesinfolanguage", It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void should_store_the_legacy_language_property_using_the_canonical_key()
        {
            Subject.SeriesInfoLanguage = Language.Czech.Id;

            AssertUpsert("PreferredMetadataLanguage", Language.Czech.Id);
        }

        [Test]
        public void recently_added_imports_should_default_to_disabled()
        {
            Subject.MarkImportedMediaAsRecentlyAdded.Should().BeFalse();
        }

        [TestCase(false, true)]
        [TestCase(true, false)]
        public void should_persist_recently_added_import_preference(bool previous, bool enabled)
        {
            Mocker.GetMock<IConfigRepository>().Setup(c => c.All()).Returns(new List<Config>
            {
                new Config { Key = "markimportedmediaasrecentlyadded", Value = previous.ToString() }
            });

            Subject.SaveConfigDictionary(new Dictionary<string, object>
            {
                [nameof(ConfigService.MarkImportedMediaAsRecentlyAdded)] = enabled
            });

            AssertUpsert(nameof(ConfigService.MarkImportedMediaAsRecentlyAdded), enabled);
        }

        private void AssertUpsert(string key, object value)
        {
            Mocker.GetMock<IConfigRepository>().Verify(c => c.Upsert(key.ToLowerInvariant(), value.ToString()));
        }

        [Test]
        [Description("This test will use reflection to ensure each config property read/writes to a unique key")]
        public void config_properties_should_write_and_read_using_same_key()
        {
            var configProvider = Subject;

            // The legacy language property intentionally aliases PreferredMetadataLanguage.
            var allProperties = typeof(ConfigService).GetProperties()
                .Where(p => p.GetSetMethod() != null && p.Name != nameof(ConfigService.SeriesInfoLanguage)).ToList();

            var keys = new List<string>();
            var values = new List<Config>();

            Mocker.GetMock<IConfigRepository>().Setup(c => c.Upsert(It.IsAny<string>(), It.IsAny<string>())).Callback<string, string>((key, value) =>
            {
                keys.Add(key);
                values.Add(new Config { Key = key, Value = value });
            });

            Mocker.GetMock<IConfigRepository>().Setup(c => c.All()).Returns(values);

            foreach (var propertyInfo in allProperties)
            {
                object value = null;

                if (propertyInfo.PropertyType == typeof(string))
                {
                    value = Guid.NewGuid().ToString();
                }
                else if (propertyInfo.PropertyType == typeof(int))
                {
                    value = DateTime.Now.Millisecond;
                }
                else if (propertyInfo.PropertyType == typeof(double))
                {
                    value = (double)DateTime.Now.Millisecond;
                }
                else if (propertyInfo.PropertyType == typeof(bool))
                {
                    value = true;
                }
                else if (propertyInfo.PropertyType.BaseType == typeof(Enum))
                {
                    value = 0;
                }

                propertyInfo.GetSetMethod().Invoke(configProvider, new[] { value });
                var returnValue = propertyInfo.GetGetMethod().Invoke(configProvider, null);

                if (propertyInfo.PropertyType.BaseType == typeof(Enum))
                {
                    returnValue = (int)returnValue;
                }

                returnValue.Should().Be(value, propertyInfo.Name);
            }

            keys.Should().OnlyHaveUniqueItems();
        }

        [Test]
        public void should_ignore_null_properties()
        {
            Mocker.GetMock<IConfigRepository>()
                  .Setup(v => v.Get("downloadedepisodesfolder"))
                  .Returns(new Config { Id = 1, Key = "DownloadedEpisodesFolder", Value = @"C:\test".AsOsAgnostic() });

            var dict = new Dictionary<string, object>();
            dict.Add("DownloadedEpisodesFolder", null);
            Subject.SaveConfigDictionary(dict);

            Mocker.GetMock<IConfigRepository>().Verify(c => c.Upsert("downloadedepisodesfolder", It.IsAny<string>()), Times.Never());
        }
    }
}
