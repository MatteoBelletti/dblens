START TRANSACTION;

ALTER TABLE shop.users DROP COLUMN date_of_birth;

INSERT INTO shop."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261120194127_DropUserDateOfBirth', '8.0.34');

COMMIT;

