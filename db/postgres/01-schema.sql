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

-- CASCADE on parent_id: deleting a category deletes its sub-categories (self-referencing cascade).
CREATE TABLE categories (
    id        BIGSERIAL PRIMARY KEY,
    name      TEXT   NOT NULL,
    parent_id BIGINT REFERENCES categories(id) ON DELETE CASCADE
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

-- CASCADE on user_id: deleting a user deletes their orders (chain: users → orders → order_items → item_reviews).
CREATE TABLE orders (
    id         BIGSERIAL    PRIMARY KEY,
    user_id    BIGINT       NOT NULL REFERENCES users(id) ON DELETE CASCADE,
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

-- Extension table for order_items: deepens the cascade chain to depth 3 from users.
CREATE TABLE item_reviews (
    id            BIGSERIAL    PRIMARY KEY,
    order_item_id BIGINT       NOT NULL REFERENCES order_items(id) ON DELETE CASCADE,
    rating        INT          NOT NULL,
    review_text   TEXT,
    created_at    TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);

CREATE TABLE payments (
    id             BIGSERIAL    PRIMARY KEY,
    order_id       BIGINT       NOT NULL REFERENCES orders(id),
    amount         NUMERIC(10,2) NOT NULL,
    card_last4     TEXT,          -- INTENTIONAL: borderline PCI/PII
    payment_method TEXT         NOT NULL,
    processed_at   TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);

-- SET NULL on user_id: deleting a user nulls out audit_log references (data retained, not deleted).
CREATE TABLE audit_logs (
    id          BIGSERIAL   PRIMARY KEY,
    user_id     BIGINT      REFERENCES users(id) ON DELETE SET NULL,
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
-- Denormalization targets (INTENTIONAL — do not fix).
-- Each table below exercises one rule of the analyze_denormalization tool.
--  13. customer_profiles.phone1/2/3   — repeating group (1NF violation)
--  14. customer_profiles.full_name    — concatenation of first_name/last_name
--  15. customer_profiles.tags         — collection packed into a scalar text column
--  16. customer_profiles.order_count  — cached aggregate over a child table
--  17. profile_notes.fiscal_code      — non-generic column copied from users, FK-linked
-- ============================================================
CREATE TABLE customer_profiles (
    id          BIGSERIAL   PRIMARY KEY,
    user_id     BIGINT      NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    first_name  TEXT        NOT NULL,
    last_name   TEXT        NOT NULL,
    full_name   TEXT,                    -- INTENTIONAL: derived from first_name + last_name
    phone1      TEXT,                    -- INTENTIONAL: repeating group
    phone2      TEXT,
    phone3      TEXT,
    tags        TEXT,                    -- INTENTIONAL: CSV packed into one column
    order_count INT         NOT NULL DEFAULT 0,  -- INTENTIONAL: cached aggregate
    fiscal_code TEXT                     -- INTENTIONAL: copied from users.fiscal_code
);
CREATE INDEX idx_customer_profiles_user_id ON customer_profiles(user_id);

CREATE TABLE profile_notes (
    id          BIGSERIAL   PRIMARY KEY,
    profile_id  BIGINT      NOT NULL REFERENCES customer_profiles(id) ON DELETE CASCADE,
    fiscal_code TEXT,                    -- INTENTIONAL: copied again from the parent profile
    note        TEXT        NOT NULL
);
CREATE INDEX idx_profile_notes_profile_id ON profile_notes(profile_id);


-- ============================================================
-- Relationship fixture (INTENTIONAL — do not fix): references with NO declared foreign key.
--  18. shipments.order_id    -> orders.id    every value resolves      (Verified)
--  19. shipments.user_id     -> users.id     1 of 6 values orphaned    (VerifiedWithOrphans)
--  20. shipments.product_id  -> products.id  5 of 6 values orphaned    (contradicted: dropped)
--  21. shipments.warehouse_id                no warehouses table       (no candidate at all)
--  22. shipments.ship_to     -> addresses.id no naming hint; only the shipment_destinations
--                                            view joins on it            (observed-join, Verified)
-- ============================================================
CREATE TABLE shipments (
    id           BIGSERIAL PRIMARY KEY,
    order_id     BIGINT NOT NULL,
    user_id      BIGINT,
    product_id   BIGINT,
    warehouse_id BIGINT,
    ship_to      BIGINT,
    carrier      TEXT   NOT NULL
);

CREATE VIEW shipment_destinations AS
SELECT s.id AS shipment_id, s.carrier, a.city, a.country
FROM shipments s
JOIN addresses a ON a.id = s.ship_to;

-- Documented meanings, so build_glossary has something to report besides structure.
COMMENT ON TABLE orders IS 'Customer orders, one row per checkout';
COMMENT ON COLUMN orders.total IS 'Order total in EUR, VAT included';

-- pgstattuple backs the analyze_fragmentation tool on PostgreSQL. Without it the tool
-- returns StatsAvailable = false rather than fragmentation findings.
CREATE EXTENSION IF NOT EXISTS pgstattuple;

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
