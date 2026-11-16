IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF SCHEMA_ID(N'shop') IS NULL EXEC(N'CREATE SCHEMA [shop];');

CREATE TABLE [shop].[categories] (
    [id] bigint NOT NULL IDENTITY,
    [name] nvarchar(100) NOT NULL,
    [parent_id] bigint NULL,
    CONSTRAINT [PK_categories] PRIMARY KEY ([id]),
    CONSTRAINT [FK_categories_categories_parent_id] FOREIGN KEY ([parent_id]) REFERENCES [shop].[categories] ([id]) ON DELETE NO ACTION
);

CREATE TABLE [shop].[users] (
    [id] bigint NOT NULL IDENTITY,
    [email] nvarchar(320) NOT NULL,
    [first_name] nvarchar(100) NOT NULL,
    [last_name] nvarchar(100) NOT NULL,
    [phone] nvarchar(30) NULL,
    [date_of_birth] date NULL,
    [created_at] datetimeoffset NOT NULL,
    [home_street] nvarchar(200) NULL,
    [home_city] nvarchar(100) NULL,
    CONSTRAINT [PK_users] PRIMARY KEY ([id])
);

CREATE TABLE [shop].[products] (
    [id] bigint NOT NULL IDENTITY,
    [name] nvarchar(200) NOT NULL,
    [sku] varchar(20) NOT NULL,
    [price] decimal(10,2) NOT NULL,
    [stock] int NOT NULL,
    [category_id] bigint NULL,
    CONSTRAINT [PK_products] PRIMARY KEY ([id]),
    CONSTRAINT [FK_products_categories_category_id] FOREIGN KEY ([category_id]) REFERENCES [shop].[categories] ([id])
);

CREATE TABLE [shop].[orders] (
    [id] bigint NOT NULL IDENTITY,
    [user_id] bigint NOT NULL,
    [total] decimal(10,2) NOT NULL,
    [status] nvarchar(20) NOT NULL,
    [created_at] datetimeoffset NOT NULL,
    CONSTRAINT [PK_orders] PRIMARY KEY ([id]),
    CONSTRAINT [FK_orders_users_user_id] FOREIGN KEY ([user_id]) REFERENCES [shop].[users] ([id]) ON DELETE CASCADE
);

CREATE TABLE [shop].[order_items] (
    [id] bigint NOT NULL IDENTITY,
    [order_id] bigint NOT NULL,
    [product_id] bigint NOT NULL,
    [quantity] int NOT NULL,
    [unit_price] decimal(10,2) NOT NULL,
    CONSTRAINT [PK_order_items] PRIMARY KEY ([id]),
    CONSTRAINT [FK_order_items_orders_order_id] FOREIGN KEY ([order_id]) REFERENCES [shop].[orders] ([id]) ON DELETE CASCADE,
    CONSTRAINT [FK_order_items_products_product_id] FOREIGN KEY ([product_id]) REFERENCES [shop].[products] ([id]) ON DELETE NO ACTION
);

CREATE INDEX [IX_categories_parent_id] ON [shop].[categories] ([parent_id]);

CREATE INDEX [IX_order_items_order_id] ON [shop].[order_items] ([order_id]);

CREATE INDEX [IX_order_items_product_id] ON [shop].[order_items] ([product_id]);

CREATE INDEX [ix_orders_status] ON [shop].[orders] ([status]);

CREATE INDEX [IX_orders_user_id] ON [shop].[orders] ([user_id]);

CREATE INDEX [IX_products_category_id] ON [shop].[products] ([category_id]);

CREATE UNIQUE INDEX [IX_users_email] ON [shop].[users] ([email]);

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261116001629_Init', N'9.0.23');

COMMIT;
GO

