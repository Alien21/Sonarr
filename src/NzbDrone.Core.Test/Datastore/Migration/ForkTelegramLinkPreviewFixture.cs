using System;
using System.Linq;
using System.Reflection;
using Dapper;
using FluentAssertions;
using FluentMigrator;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Datastore.Migration;

[TestFixture]
[Category("DbMigrationTest")]
public class ForkTelegramLinkPreviewFixture : DbTest
{
    [SetUp]
    public override void SetupDb()
    {
        SetupContainer();
    }

    private IDirectDataMapper MigrateDatabase()
    {
        return WithTestDb(new MigrationContext(MigrationType)
        {
            BeforeMigration = _ => { }
        }).GetDirectDataMapper();
    }

    [TestCase("{}", -1)]
    [TestCase("{\"linkPreview\":null,\"metadataLinks\":[1,2]}", 2)]
    [TestCase("{\"metadataLinks\":[1]}", -1)]
    [TestCase("{\"metadataLinks\":[1,2]}", 2)]
    [TestCase("{\"metadataLinks\":[0,1]}", 0)]
    public void should_repair_telegram_when_fork_migration_218_is_already_recorded(string settings, int expectedPreview)
    {
        PrepareForkDatabase(settings);

        var db = MigrateDatabase();
        var notification = db.Query<ForkTelegramNotification>("SELECT \"Id\", \"Settings\" FROM \"Notifications\"").Single();

        JObject.Parse(notification.Settings)["linkPreview"].Value<int>().Should().Be(expectedPreview);
        db.QueryScalar<int>("SELECT COUNT(*) FROM \"VersionInfo\" WHERE \"Version\" = 218").Should().Be(1);
        var latestVersion = typeof(NzbDroneMigrationBase).Assembly.GetTypes()
            .Select(t => t.GetCustomAttribute<MigrationAttribute>()?.Version ?? 0).Max();
        db.QueryScalar<int>("SELECT MAX(\"Version\") FROM \"VersionInfo\"").Should().Be((int)latestVersion);
    }

    [TestCase(-1)]
    [TestCase(0)]
    [TestCase(2)]
    public void should_preserve_an_existing_link_preview_setting(int preview)
    {
        var settings = new JObject
        {
            ["metadataLinks"] = new JArray(1, 0),
            ["linkPreview"] = preview,
            ["botToken"] = "preserved-token"
        }.ToString();

        PrepareForkDatabase(settings);

        var db = MigrateDatabase();
        var notification = db.Query<ForkTelegramNotification>("SELECT \"Id\", \"Settings\" FROM \"Notifications\"").Single();

        notification.Settings.Should().Be(settings);
    }

    [Test]
    public void should_preserve_user_choices_when_the_runner_starts_again()
    {
        PrepareForkDatabase("{\"metadataLinks\":[1,2]}");
        var db = MigrateDatabase();
        db.Query("UPDATE \"Notifications\" SET \"Settings\" = '{\"metadataLinks\":[1,2],\"linkPreview\":0}'");

        var afterRestart = MigrateDatabase();
        var notification = afterRestart.Query<ForkTelegramNotification>("SELECT \"Id\", \"Settings\" FROM \"Notifications\"").Single();

        JObject.Parse(notification.Settings)["linkPreview"].Value<int>().Should().Be(0);
    }

    [Test]
    public void should_skip_telegram_reconciliation_for_the_log_database()
    {
        var log = WithTestDb(new MigrationContext(MigrationType.Log)
        {
            BeforeMigration = _ => { }
        });

        log.GetDirectDataMapper().QueryScalar<int>("SELECT COUNT(*) FROM \"Logs\"").Should().Be(0);
    }

    private void PrepareForkDatabase(string settings)
    {
        var before = WithTestDb(new MigrationContext(MigrationType, 217)
        {
            BeforeMigration = _ => { }
        });

        using var connection = before.OpenConnection();
        connection.Execute(@"INSERT INTO ""Notifications""
            (""Name"", ""Implementation"", ""ConfigContract"", ""Settings"", ""Tags"",
             ""OnGrab"", ""OnDownload"", ""OnUpgrade"", ""OnHealthIssue"", ""IncludeHealthWarnings"", ""OnRename"")
            VALUES ('Telegram Sonarr', 'Telegram', 'TelegramSettings', @Settings, '[]',
                    @Enabled, @Enabled, @Enabled, @Enabled, @Enabled, @Enabled)",
            new { Settings = settings, Enabled = true });
        connection.Execute(@"INSERT INTO ""VersionInfo"" (""Version"", ""AppliedOn"", ""Description"")
            VALUES (218, @AppliedOn, 'add_series_translations')",
            new { AppliedOn = DateTime.UtcNow });
    }

    private class ForkTelegramNotification
    {
        public int Id { get; set; }
        public string Settings { get; set; }
    }
}
