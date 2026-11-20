BEGIN TRANSACTION;
GO

DECLARE @var0 sysname;
SELECT @var0 = [d].[name]
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[shop].[users]') AND [c].[name] = N'date_of_birth');
IF @var0 IS NOT NULL EXEC(N'ALTER TABLE [shop].[users] DROP CONSTRAINT [' + @var0 + '];');
ALTER TABLE [shop].[users] DROP COLUMN [date_of_birth];
GO

INSERT INTO [shop].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261120194150_DropUserDateOfBirth', N'8.0.34');
GO

COMMIT;
GO

