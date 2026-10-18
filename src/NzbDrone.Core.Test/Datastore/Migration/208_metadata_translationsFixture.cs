using System;
using System.Linq;
using Dapper;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Datastore.Migration;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Datastore.Migration;

[TestFixture]
public class metadata_translationsFixture : MigrationTest<metadata_translations>
{
    [Test]
    public void should_create_translations_for_an_upstream_database()
    {
        var db = WithMigrationTestDb();

        db.QueryScalar<int>("SELECT COUNT(*) FROM \"SeriesTranslations\"").Should().Be(0);
    }

    [Test]
    public void should_preserve_fork_translations_when_218_is_already_recorded()
    {
        var before = WithTestDb(new MigrationContext(MigrationType, 207)
        {
            BeforeMigration = _ => { }
        });

        using (var connection = before.OpenConnection())
        {
            connection.Execute(@"CREATE TABLE ""SeriesTranslations"" (
                ""Id"" INTEGER NOT NULL PRIMARY KEY,
                ""SeriesId"" INTEGER NOT NULL,
                ""Title"" TEXT NULL,
                ""CleanTitle"" TEXT NULL,
                ""Overview"" TEXT NULL,
                ""Language"" INTEGER NOT NULL)");
            connection.Execute(@"INSERT INTO ""SeriesTranslations""
                (""Id"", ""SeriesId"", ""Title"", ""CleanTitle"", ""Overview"", ""Language"")
                VALUES (1, 42, 'Český název', 'ceskynazev', 'Původní český popis', @Language)",
                new { Language = Language.Czech.Id });
            connection.Execute(@"INSERT INTO ""VersionInfo"" (""Version"", ""AppliedOn"", ""Description"")
                VALUES (218, @AppliedOn, 'add_series_translations')", new { AppliedOn = DateTime.UtcNow });
        }

        var db = WithMigrationTestDb();
        var translation = db.Query<ForkTranslation208>("SELECT * FROM \"SeriesTranslations\"").Single();

        translation.SeriesId.Should().Be(42);
        translation.Title.Should().Be("Český název");
        translation.Overview.Should().Be("Původní český popis");
        translation.Language.Should().Be(Language.Czech.Id);
        db.QueryScalar<int>("SELECT COUNT(*) FROM \"VersionInfo\" WHERE \"Version\" = 208").Should().Be(1);
        db.QueryScalar<int>("SELECT COUNT(*) FROM \"VersionInfo\" WHERE \"Version\" = 218").Should().Be(1);
    }

    private class ForkTranslation208
    {
        public int SeriesId { get; set; }
        public string Title { get; set; }
        public string Overview { get; set; }
        public int Language { get; set; }
    }
}
