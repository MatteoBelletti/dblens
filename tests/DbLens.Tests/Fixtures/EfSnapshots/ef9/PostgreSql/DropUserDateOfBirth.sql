START TRANSACTION;
ALTER TABLE shop.users DROP COLUMN date_of_birth;

INSERT INTO shop."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261120194220_DropUserDateOfBirth', '9.0.23');

COMMIT;

