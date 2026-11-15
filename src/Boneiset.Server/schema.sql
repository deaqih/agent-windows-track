CREATE TABLE IF NOT EXISTS devices (
    id uuid PRIMARY KEY, machine_name text NOT NULL, key_hash text NOT NULL,
    enrolled_at timestamptz NOT NULL DEFAULT now(), revoked boolean NOT NULL DEFAULT false,
    asset_tag text UNIQUE NOT NULL, owner_name text NOT NULL DEFAULT '', department text NOT NULL DEFAULT '',
    branch text NOT NULL DEFAULT '', lifecycle text NOT NULL DEFAULT 'In use',
    last_received timestamptz, last_captured timestamptz, location_status text NOT NULL DEFAULT 'unavailable',
    inventory jsonb, inventory_at timestamptz
);
CREATE TABLE IF NOT EXISTS enrollment_tokens (
    hash text PRIMARY KEY, expires_at timestamptz NOT NULL, used_at timestamptz, created_by text NOT NULL
);
CREATE TABLE IF NOT EXISTS reports (
    device_id uuid NOT NULL REFERENCES devices(id), report_id uuid NOT NULL, captured_at timestamptz NOT NULL,
    received_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY (device_id, report_id)
);
CREATE TABLE IF NOT EXISTS locations (
    device_id uuid NOT NULL, report_id uuid NOT NULL, measured_at timestamptz NOT NULL,
    latitude double precision NOT NULL CHECK (latitude BETWEEN -90 AND 90),
    longitude double precision NOT NULL CHECK (longitude BETWEEN -180 AND 180),
    accuracy double precision NOT NULL CHECK (accuracy >= 0), source text NOT NULL,
    PRIMARY KEY (device_id,report_id), FOREIGN KEY (device_id,report_id) REFERENCES reports(device_id,report_id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS locations_device_time ON locations(device_id, measured_at DESC);
CREATE INDEX IF NOT EXISTS reports_capture ON reports(captured_at);
CREATE TABLE IF NOT EXISTS software_scopes (
    device_id uuid NOT NULL REFERENCES devices(id), scope text NOT NULL, captured_at timestamptz NOT NULL,
    complete boolean NOT NULL, PRIMARY KEY (device_id,scope)
);
CREATE TABLE IF NOT EXISTS software (
    device_id uuid NOT NULL REFERENCES devices(id), scope text NOT NULL, app_key text NOT NULL,
    name text NOT NULL, version text NOT NULL, publisher text NOT NULL,
    first_seen timestamptz NOT NULL, last_seen timestamptz NOT NULL, present boolean NOT NULL DEFAULT true,
    PRIMARY KEY (device_id,scope,app_key)
);
CREATE INDEX IF NOT EXISTS software_device ON software(device_id, present);
CREATE TABLE IF NOT EXISTS software_events (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, device_id uuid NOT NULL REFERENCES devices(id),
    observed_at timestamptz NOT NULL, scope text NOT NULL, name text NOT NULL, kind text NOT NULL, detail text NOT NULL
);
CREATE TABLE IF NOT EXISTS software_policy (name text PRIMARY KEY, created_by text NOT NULL, created_at timestamptz NOT NULL DEFAULT now());
CREATE TABLE IF NOT EXISTS audit (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, at timestamptz NOT NULL DEFAULT now(),
    actor text NOT NULL, action text NOT NULL, device_id uuid, detail text NOT NULL
);
CREATE TABLE IF NOT EXISTS dashboard_users (
    id uuid PRIMARY KEY, username text UNIQUE NOT NULL, password_hash text NOT NULL,
    role text NOT NULL CHECK (role IN ('Admin','Viewer')), enabled boolean NOT NULL DEFAULT true,
    session_stamp uuid NOT NULL, failed_attempts integer NOT NULL DEFAULT 0,
    locked_until timestamptz, created_at timestamptz NOT NULL DEFAULT now(), last_login timestamptz,
    CHECK (username = lower(username))
);
