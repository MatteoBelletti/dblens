-- INTENTIONAL ISSUES (do not fix) — these are targets for future dblens analysis tools:
--   1. users.email          — used in WHERE but NO index (performance issue)
--   2. orders.user_id       — FK without explicit index (performance issue)
--   3. addresses.user_id    — FK without explicit index (performance issue)
--   4. audit_logs.user_id   — FK without explicit index (performance issue)
--   5. No deleted_at / soft-delete on users (GDPR: right to erasure is messy)
--   6. No retention_until or temporal policy on audit_logs
--   7. users.fiscal_code, users.phone, users.date_of_birth — PII in plaintext, no encryption hint
--   8. payments.card_last4  — borderline PCI/PII
--   9. audit_logs.ip_address — PII under GDPR (personal identifiable data)
--  10. categories.parent_id — recursive FK without index
--  11. orders.status        — no CHECK constraint (any string accepted)

CREATE DATABASE dblens_demo;
GO

USE dblens_demo;
GO

CREATE TABLE users (
    id            BIGINT IDENTITY(1,1) PRIMARY KEY,
    email         NVARCHAR(254)  NOT NULL UNIQUE,
    password_hash NVARCHAR(256)  NOT NULL,
    first_name    NVARCHAR(100)  NOT NULL,
    last_name     NVARCHAR(100)  NOT NULL,
    phone         NVARCHAR(20),
    date_of_birth DATE,
    fiscal_code   NVARCHAR(16),
    created_at    DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at    DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

CREATE TABLE addresses (
    id          BIGINT IDENTITY(1,1) PRIMARY KEY,
    user_id     BIGINT        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    street      NVARCHAR(200) NOT NULL,
    city        NVARCHAR(100) NOT NULL,
    postal_code NVARCHAR(10)  NOT NULL,
    country     NVARCHAR(2)   NOT NULL DEFAULT 'IT',
    is_default  BIT           NOT NULL DEFAULT 0
    -- INTENTIONAL: no index on user_id
);
GO

CREATE TABLE categories (
    id        BIGINT IDENTITY(1,1) PRIMARY KEY,
    name      NVARCHAR(100) NOT NULL,
    parent_id BIGINT        REFERENCES categories(id)
    -- INTENTIONAL: no index on parent_id
);
GO

CREATE TABLE products (
    id          BIGINT IDENTITY(1,1) PRIMARY KEY,
    name        NVARCHAR(200)  NOT NULL,
    description NVARCHAR(MAX),
    price       DECIMAL(10,2)  NOT NULL,
    stock       INT            NOT NULL DEFAULT 0,
    category_id BIGINT         REFERENCES categories(id),
    created_at  DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

CREATE TABLE orders (
    id         BIGINT IDENTITY(1,1) PRIMARY KEY,
    user_id    BIGINT        NOT NULL REFERENCES users(id),
    total      DECIMAL(10,2) NOT NULL,
    status     NVARCHAR(50)  NOT NULL,  -- INTENTIONAL: no CHECK constraint
    created_at DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
    -- INTENTIONAL: no index on user_id
);
GO

CREATE TABLE order_items (
    id         BIGINT IDENTITY(1,1) PRIMARY KEY,
    order_id   BIGINT        NOT NULL REFERENCES orders(id),
    product_id BIGINT        NOT NULL REFERENCES products(id),
    quantity   INT           NOT NULL,
    unit_price DECIMAL(10,2) NOT NULL
);
GO

CREATE TABLE payments (
    id             BIGINT IDENTITY(1,1) PRIMARY KEY,
    order_id       BIGINT        NOT NULL REFERENCES orders(id),
    amount         DECIMAL(10,2) NOT NULL,
    card_last4     NVARCHAR(4),           -- INTENTIONAL: borderline PCI/PII
    payment_method NVARCHAR(50)  NOT NULL,
    processed_at   DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

CREATE TABLE audit_logs (
    id          BIGINT IDENTITY(1,1) PRIMARY KEY,
    user_id     BIGINT,       -- nullable: system actions have no user
    action      NVARCHAR(100) NOT NULL,
    entity_type NVARCHAR(100) NOT NULL,
    entity_id   BIGINT,
    ip_address  VARCHAR(45),  -- INTENTIONAL: PII under GDPR
    created_at  DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
    -- INTENTIONAL: no index on user_id
    -- INTENTIONAL: no retention_until / temporal policy
);
GO
