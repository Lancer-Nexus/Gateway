-- Encrypted, durable handoff staging for source-frozen Freelancer save snapshots.
-- Keep rows until the transfer is recovered or operationally finalized.

CREATE TABLE character_transfer_snapshots (
    transfer_id CHAR(36) NOT NULL,
    source_instance_id VARCHAR(96) NOT NULL,
    target_instance_id VARCHAR(96) NOT NULL,
    character_id BIGINT NOT NULL,
    lease_version BIGINT UNSIGNED NOT NULL,
    key_id VARCHAR(64) NOT NULL,
    snapshot_hash BINARY(32) NOT NULL,
    protected_snapshot LONGBLOB NOT NULL,
    created_at_utc DATETIME(6) NOT NULL,
    PRIMARY KEY (transfer_id),
    KEY ix_character_transfer_snapshots_target (target_instance_id, created_at_utc),
    CONSTRAINT fk_character_transfer_snapshots_character
        FOREIGN KEY (character_id) REFERENCES characters (character_id)
) ENGINE=InnoDB;
