BEGIN TRANSACTION;
GO

CREATE TABLE [shop].[payments] (
    [id] bigint NOT NULL IDENTITY,
    [order_id] bigint NOT NULL,
    [amount] decimal(10,2) NOT NULL,
    [kind] nvarchar(10) NOT NULL,
    [iban] nvarchar(34) NULL,
    [card_last4] nvarchar(4) NULL,
    CONSTRAINT [PK_payments] PRIMARY KEY ([id]),
    CONSTRAINT [FK_payments_orders_order_id] FOREIGN KEY ([order_id]) REFERENCES [shop].[orders] ([id]) ON DELETE CASCADE
);
GO

CREATE INDEX [ix_products_sku] ON [shop].[products] ([sku]);
GO

CREATE INDEX [IX_payments_order_id] ON [shop].[payments] ([order_id]);
GO

INSERT INTO [shop].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261120194145_AddPayments', N'8.0.34');
GO

COMMIT;
GO

