using FluentMigrator;

namespace NzbDrone.Core.Datastore.Migration
{
    [Maintenance(MigrationStage.AfterAll)]
    public class ReconcileForkTelegramLinkPreview : ForwardOnlyMigration
    {
        public override void Up()
        {
            // The v4 fork recorded version 218 for series translations. Reconcile
            // skipped Telegram settings without reserving another upstream version.
            if (Schema.Table("Notifications").Exists())
            {
                Execute.WithConnection(telegram_link_preview.SetMissingLinkPreview);
            }
        }
    }
}
