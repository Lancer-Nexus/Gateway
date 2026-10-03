-- Apply after 003/004, with Gateway transfer writers stopped during migration.
-- One permanent decision per transfer fences future commits after rollback.
CREATE TABLE character_transfer_decisions (
    transfer_id CHAR(36) NOT NULL,
    session_id CHAR(36) NOT NULL,
    character_id BIGINT NOT NULL,
    source_instance_id VARCHAR(96) NOT NULL,
    target_instance_id VARCHAR(96) NOT NULL,
    expected_lease_version BIGINT UNSIGNED NOT NULL,
    decision TINYINT UNSIGNED NOT NULL,
    decided_at_utc DATETIME(6) NOT NULL,
    PRIMARY KEY (transfer_id),
    CONSTRAINT ck_character_transfer_decision CHECK (decision IN (6, 22)),
    CONSTRAINT fk_transfer_decision_character FOREIGN KEY (character_id) REFERENCES characters (character_id),
    CONSTRAINT fk_transfer_decision_session FOREIGN KEY (session_id) REFERENCES gateway_sessions (session_id)
) ENGINE=InnoDB;

INSERT INTO character_transfer_decisions
    (transfer_id, session_id, character_id, source_instance_id, target_instance_id,
     expected_lease_version, decision, decided_at_utc)
SELECT transfer_id, session_id, character_id, source_instance_id, target_instance_id,
    expected_lease_version, 6, committed_at_utc FROM character_lease_transfers;
