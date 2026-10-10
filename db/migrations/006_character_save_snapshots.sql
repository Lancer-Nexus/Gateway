-- Gateway-owned, encrypted Freelancer SaveGame snapshots with monotone revisions.
-- Apply through the deployment migration runner. Existing characters have no snapshot
-- until LLServer writes revision 1 under its current character lease.

CREATE TABLE character_save_snapshots (
    character_id BIGINT NOT NULL,
    snapshot_revision BIGINT UNSIGNED NOT NULL,
    lease_version BIGINT UNSIGNED NOT NULL,
    schema_version SMALLINT UNSIGNED NOT NULL,
    key_id VARCHAR(64) NOT NULL,
    snapshot_hash BINARY(32) NOT NULL,
    protected_snapshot LONGBLOB NOT NULL,
    updated_at_utc DATETIME(6) NOT NULL,
    PRIMARY KEY (character_id),
    CONSTRAINT fk_character_save_snapshots_character
        FOREIGN KEY (character_id) REFERENCES characters (character_id),
    CONSTRAINT ck_character_save_snapshots_revision CHECK (snapshot_revision > 0),
    CONSTRAINT ck_character_save_snapshots_lease_version CHECK (lease_version > 0),
    CONSTRAINT ck_character_save_snapshots_schema_version CHECK (schema_version > 0)
) ENGINE=InnoDB;
