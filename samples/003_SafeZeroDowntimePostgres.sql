-- Zero-Downtime Safe PostgreSQL Migration Example
-- Adding a nullable column and creating an index CONCURRENTLY produces zero risk findings.

ALTER TABLE "public"."orders"
    ADD COLUMN IF NOT EXISTS "shipping_notes" TEXT NULL;

CREATE INDEX CONCURRENTLY IF NOT EXISTS "idx_orders_shipping_notes"
    ON "public"."orders" ("shipping_notes")
    WHERE "shipping_notes" IS NOT NULL;
