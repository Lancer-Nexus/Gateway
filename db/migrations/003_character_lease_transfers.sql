-- Idempotency journal for Gateway-owned, fenced character lease handoffs.
-- Apply after 002_refresh_token_rotation.sql with the deployment migration runner.

CREATE TABLE character_lease_transfers (
    transfer_id CHAR(36) NOT NULL,
    session_id CHAR(36) NOT NULL,
    character_id BIGINT NOT NULL,
    source_instance_id VARCHAR(96) NOT NULL,
    target_instance_id VARCHAR(96) NOT NULL,
    expected_lease_version BIGINT UNSIGNED NOT NULL,
    committed_lease_version BIGINT UNSIGNED NOT NULL,
    target_lease_token_hash VARBINARY(64) NOT NULL,
    committed_at_utc DATETIME(6) NOT NULL,
    PRIMARY KEY (transfer_id),
    KEY ix_character_lease_transfers_character (character_id, committed_at_utc),
    CONSTRAINT fk_character_lease_transfers_character
        FOREIGN KEY (character_id) REFERENCES characters (character_id),
    CONSTRAINT fk_character_lease_transfers_session
        FOREIGN KEY (session_id) REFERENCES gateway_sessions (session_id)
) ENGINE=InnoDB;
