BEGIN TRANSACTION;
DECLARE @var nvarchar(max);
SELECT @var = QUOTENAME([d].[name])
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[shop].[users]') AND [c].[name] = N'date_of_birth');
IF @var IS NOT NULL EXEC(N'ALTER TABLE [shop].[users] DROP CONSTRAINT ' + @var + ';');
ALTER TABLE [shop].[users] DROP COLUMN [date_of_birth];

INSERT INTO [shop].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261120194342_DropUserDateOfBirth', N'10.0.15');

COMMIT;
GO

