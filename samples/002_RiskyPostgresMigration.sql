-- Sample PostgreSQL Migration with blocking locks & destructive DDL
ALTER TABLE "public"."user_sessions"
    ADD COLUMN "tenant_id" UUID NOT NULL,
    ALTER COLUMN "payload" SET DATA TYPE VARCHAR(255);

CREATE INDEX "idx_audit_logs_created_at" ON "public"."audit_logs" ("created_at");

TRUNCATE TABLE "public"."temp_exports" CASCADE;
