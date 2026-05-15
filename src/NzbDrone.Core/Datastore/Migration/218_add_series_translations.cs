using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(218)]
    public class add_series_translations : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Create.TableForModel("SeriesTranslations")
                .WithColumn("SeriesId").AsInt32().NotNullable()
                .WithColumn("Title").AsString().Nullable()
                .WithColumn("CleanTitle").AsString().Nullable()
                .WithColumn("Overview").AsString().Nullable()
                .WithColumn("Language").AsInt32().NotNullable();

            Create.Index("IX_SeriesTranslations_SeriesId_Language")
                .OnTable("SeriesTranslations")
                .OnColumn("SeriesId").Ascending()
                .OnColumn("Language").Ascending()
                .WithOptions().Unique();

            Create.Index().OnTable("SeriesTranslations").OnColumn("Language");
            Create.Index().OnTable("SeriesTranslations").OnColumn("CleanTitle");
        }
    }
}
