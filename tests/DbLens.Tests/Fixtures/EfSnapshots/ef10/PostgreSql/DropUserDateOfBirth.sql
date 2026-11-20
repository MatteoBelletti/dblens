START TRANSACTION;
ALTER TABLE shop.users DROP COLUMN date_of_birth;

INSERT INTO shop."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261120194318_DropUserDateOfBirth', '10.0.15');

COMMIT;

