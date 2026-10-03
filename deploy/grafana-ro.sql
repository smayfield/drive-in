-- The read-only role Grafana (Postgres data source) and postgres-exporter use. Run by deploy.sh on every deploy,
-- after migrations, so new tables and columns are picked up: psql reads the password from $GRAFANA_DB_PASSWORD.
-- It can read business data but not secrets or personal details: no password hashes, tokens, bearer codes (ticket and
-- gift card codes), emails, image bytes, or what people write in in-app messages. Sessions are read-only with a
-- statement timeout.
\set ON_ERROR_STOP on
\getenv pw GRAFANA_DB_PASSWORD

SELECT NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'grafana_ro') AS create_role \gset
\if :create_role
CREATE ROLE grafana_ro LOGIN;
\endif
ALTER ROLE grafana_ro WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD :'pw';
ALTER ROLE grafana_ro SET default_transaction_read_only = on;
ALTER ROLE grafana_ro SET statement_timeout = '30s';
GRANT pg_monitor TO grafana_ro;
GRANT CONNECT ON DATABASE drivein TO grafana_ro;
GRANT USAGE ON SCHEMA public TO grafana_ro;

DO $$
DECLARE
    t record;
    cols text;
BEGIN
    -- Start clean, so a column that becomes sensitive (or a dropped grant) doesn't linger.
    EXECUTE 'REVOKE ALL ON ALL TABLES IN SCHEMA public FROM grafana_ro';
    FOR t IN
        SELECT table_name FROM information_schema.tables
        WHERE table_schema = 'public' AND table_type = 'BASE TABLE'
          AND table_name NOT IN ('user_claims', 'user_logins', 'user_passkeys', 'user_tokens')
    LOOP
        SELECT string_agg(quote_ident(column_name), ', ' ORDER BY ordinal_position) INTO cols
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = t.table_name
          AND CASE WHEN t.table_name = 'users'
                   THEN column_name IN ('id', 'created_at', 'email_confirmed', 'employee_theater_id', 'lockout_end',
                                        'two_factor_enabled')
                   ELSE column_name NOT IN ('code', 'short_code', 'token_hash', 'password_hash', 'security_stamp',
                                            'data', 'payment_reference', 'cdn_key')
                        AND column_name NOT LIKE '%email%'
                        -- What people write to each other in the app (and notification titles naming them).
                        AND (t.table_name, column_name) NOT IN (('messages', 'body'), ('conversations', 'subject'),
                                                                ('notifications', 'title'),
                                                                -- Theaters' unpublished drafts are theirs alone.
                                                                ('theater_pages', 'body_html'),
                                                                ('theater_pages', 'summary'))
              END;
        IF cols IS NOT NULL THEN
            EXECUTE format('GRANT SELECT (%s) ON %I TO grafana_ro', cols, t.table_name);
        END IF;
    END LOOP;
END $$;
