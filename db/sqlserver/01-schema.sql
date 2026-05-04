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

-- CASCADE on parent_id: deleting a category deletes its sub-categories (self-referencing cascade).
CREATE TABLE categories (
    id        BIGINT IDENTITY(1,1) PRIMARY KEY,
    name      NVARCHAR(100) NOT NULL,
    parent_id BIGINT        REFERENCES categories(id) ON DELETE CASCADE
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

-- CASCADE on user_id: deleting a user deletes their orders (chain: users → orders → order_items → item_reviews).
CREATE TABLE orders (
    id         BIGINT IDENTITY(1,1) PRIMARY KEY,
    user_id    BIGINT        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    total      DECIMAL(10,2) NOT NULL,
    status     NVARCHAR(50)  NOT NULL,  -- INTENTIONAL: no CHECK constraint
    created_at DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
    -- INTENTIONAL: no index on user_id
);
GO

CREATE TABLE order_items (
    id         BIGINT IDENTITY(1,1) PRIMARY KEY,
    order_id   BIGINT        NOT NULL REFERENCES orders(id) ON DELETE CASCADE,
    product_id BIGINT        NOT NULL REFERENCES products(id),
    quantity   INT           NOT NULL,
    unit_price DECIMAL(10,2) NOT NULL
);
GO

-- Extension table for order_items: deepens the cascade chain to depth 3 from users.
CREATE TABLE item_reviews (
    id            BIGINT IDENTITY(1,1) PRIMARY KEY,
    order_item_id BIGINT        NOT NULL REFERENCES order_items(id) ON DELETE CASCADE,
    rating        INT           NOT NULL,
    review_text   NVARCHAR(MAX),
    created_at    DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
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

-- SET NULL on user_id: deleting a user nulls out audit_log references (data retained, not deleted).
CREATE TABLE audit_logs (
    id          BIGINT IDENTITY(1,1) PRIMARY KEY,
    user_id     BIGINT,       -- nullable
    action      NVARCHAR(100) NOT NULL,
    entity_type NVARCHAR(100) NOT NULL,
    entity_id   BIGINT,
    ip_address  VARCHAR(45),  -- INTENTIONAL: PII under GDPR
    created_at  DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
    -- INTENTIONAL: no index on user_id
    -- INTENTIONAL: no retention_until / temporal policy
);
GO

-- Add FK with SET NULL after table creation (SQL Server requires the table to exist first)
ALTER TABLE audit_logs
    ADD CONSTRAINT fk_audit_logs_user_id
    FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE SET NULL;
GO

-- INTENTIONAL: deliberately unused index for testing the unused-index rule.
--  12. idx_orders_status — non-unique, never scanned after seeding (no SELECT WHERE status = ...)
CREATE INDEX idx_orders_status ON orders(status);
GO

-- ============================================================
-- demo_good schema: clean design with no intentional issues.
-- Used by AnalyzeIndexes tests to assert Findings = [] on a healthy schema.
-- ============================================================
CREATE SCHEMA demo_good;
GO

CREATE TABLE demo_good.departments (
    id   BIGINT IDENTITY(1,1) PRIMARY KEY,
    name NVARCHAR(200) NOT NULL
);
GO

CREATE TABLE demo_good.employees (
    id            BIGINT IDENTITY(1,1) PRIMARY KEY,
    department_id BIGINT        NOT NULL REFERENCES demo_good.departments(id),
    name          NVARCHAR(200) NOT NULL,
    email         NVARCHAR(254) NOT NULL UNIQUE
);
GO
CREATE INDEX idx_demo_good_employees_department_id ON demo_good.employees(department_id);
GO

CREATE TABLE demo_good.projects (
    id         BIGINT IDENTITY(1,1) PRIMARY KEY,
    owner_id   BIGINT        NOT NULL REFERENCES demo_good.employees(id),
    name       NVARCHAR(200) NOT NULL,
    started_at DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
CREATE INDEX idx_demo_good_projects_owner_id ON demo_good.projects(owner_id);
GO
