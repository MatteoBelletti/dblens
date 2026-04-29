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

CREATE TABLE users (
    id             BIGSERIAL PRIMARY KEY,
    email          TEXT        NOT NULL UNIQUE,
    password_hash  TEXT        NOT NULL,
    first_name     TEXT        NOT NULL,
    last_name      TEXT        NOT NULL,
    phone          TEXT,
    date_of_birth  DATE,
    fiscal_code    TEXT,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE addresses (
    id          BIGSERIAL PRIMARY KEY,
    user_id     BIGINT      NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    street      TEXT        NOT NULL,
    city        TEXT        NOT NULL,
    postal_code TEXT        NOT NULL,
    country     TEXT        NOT NULL DEFAULT 'IT',
    is_default  BOOLEAN     NOT NULL DEFAULT FALSE
    -- INTENTIONAL: no index on user_id
);

CREATE TABLE categories (
    id        BIGSERIAL PRIMARY KEY,
    name      TEXT   NOT NULL,
    parent_id BIGINT REFERENCES categories(id)
    -- INTENTIONAL: no index on parent_id
);

CREATE TABLE products (
    id          BIGSERIAL    PRIMARY KEY,
    name        TEXT         NOT NULL,
    description TEXT,
    price       NUMERIC(10,2) NOT NULL,
    stock       INT           NOT NULL DEFAULT 0,
    category_id BIGINT        REFERENCES categories(id),
    created_at  TIMESTAMPTZ   NOT NULL DEFAULT NOW()
);

CREATE TABLE orders (
    id         BIGSERIAL    PRIMARY KEY,
    user_id    BIGINT       NOT NULL REFERENCES users(id),
    total      NUMERIC(10,2) NOT NULL,
    status     TEXT         NOT NULL,   -- INTENTIONAL: no CHECK constraint
    created_at TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ  NOT NULL DEFAULT NOW()
    -- INTENTIONAL: no index on user_id
);

CREATE TABLE order_items (
    id         BIGSERIAL    PRIMARY KEY,
    order_id   BIGINT       NOT NULL REFERENCES orders(id) ON DELETE CASCADE,
    product_id BIGINT       NOT NULL REFERENCES products(id),
    quantity   INT          NOT NULL,
    unit_price NUMERIC(10,2) NOT NULL
);

CREATE TABLE payments (
    id             BIGSERIAL    PRIMARY KEY,
    order_id       BIGINT       NOT NULL REFERENCES orders(id),
    amount         NUMERIC(10,2) NOT NULL,
    card_last4     TEXT,          -- INTENTIONAL: borderline PCI/PII
    payment_method TEXT         NOT NULL,
    processed_at   TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);

CREATE TABLE audit_logs (
    id          BIGSERIAL   PRIMARY KEY,
    user_id     BIGINT,     -- nullable: system actions have no user
    action      TEXT        NOT NULL,
    entity_type TEXT        NOT NULL,
    entity_id   BIGINT,
    ip_address  INET,       -- INTENTIONAL: PII under GDPR
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
    -- INTENTIONAL: no index on user_id
    -- INTENTIONAL: no retention_until / temporal policy
);

-- INTENTIONAL: deliberately unused index for testing the unused-index rule.
--  12. idx_orders_status — non-unique, never scanned after seeding (no SELECT WHERE status = ...)
CREATE INDEX idx_orders_status ON orders(status);

-- ============================================================
-- demo_good schema: clean design with no intentional issues.
-- Used by AnalyzeIndexes tests to assert Findings = [] on a healthy schema.
-- ============================================================
CREATE SCHEMA demo_good;

CREATE TABLE demo_good.departments (
    id   BIGSERIAL    PRIMARY KEY,
    name TEXT         NOT NULL
);

CREATE TABLE demo_good.employees (
    id            BIGSERIAL    PRIMARY KEY,
    department_id BIGINT       NOT NULL REFERENCES demo_good.departments(id),
    name          TEXT         NOT NULL,
    email         TEXT         NOT NULL UNIQUE
);
CREATE INDEX idx_demo_good_employees_department_id ON demo_good.employees(department_id);

CREATE TABLE demo_good.projects (
    id         BIGSERIAL    PRIMARY KEY,
    owner_id   BIGINT       NOT NULL REFERENCES demo_good.employees(id),
    name       TEXT         NOT NULL,
    started_at TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_demo_good_projects_owner_id ON demo_good.projects(owner_id);
