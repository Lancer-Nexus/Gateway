-- Durable Gateway side of the market-trade saga. The intent is committed before
-- Coordinator reservation, then snapshot, ledger and resolve outbox commit together.
CREATE TABLE gateway_market_trade_intents (
    trade_id BINARY(16) NOT NULL,
    correlation_id BINARY(16) NOT NULL,
    request_sha256 BINARY(32) NOT NULL,
    instance_id VARCHAR(96) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    lease_token_hash BINARY(32) NOT NULL,
    character_id BIGINT NOT NULL,
    lease_version BIGINT UNSIGNED NOT NULL,
    snapshot_revision BIGINT UNSIGNED NOT NULL,
    snapshot_schema_version SMALLINT UNSIGNED NOT NULL,
    snapshot_key_id VARCHAR(64) NOT NULL,
    snapshot_hash BINARY(32) NOT NULL,
    protected_snapshot LONGBLOB NOT NULL,
    market_id VARCHAR(96) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    system_id VARCHAR(96) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    good_id VARCHAR(96) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    trade_side TINYINT UNSIGNED NOT NULL,
    quantity BIGINT UNSIGNED NOT NULL,
    expected_market_revision BIGINT UNSIGNED NOT NULL,
    expected_reputation_revision BIGINT UNSIGNED NOT NULL,
    reputation_price_factor_parts_per_million BIGINT UNSIGNED NOT NULL,
    quote_unit_price BIGINT UNSIGNED NOT NULL,
    total_price BIGINT UNSIGNED NOT NULL,
    market_faction_id VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    state TINYINT UNSIGNED NOT NULL,
    attempt_count INT UNSIGNED NOT NULL DEFAULT 0,
    next_attempt_at_utc DATETIME(6) NOT NULL,
    created_at_utc DATETIME(6) NOT NULL,
    updated_at_utc DATETIME(6) NOT NULL,
    PRIMARY KEY (trade_id),
    KEY ix_gateway_market_trade_character (character_id, created_at_utc),
    KEY ix_gateway_market_trade_recovery (state, next_attempt_at_utc),
    CONSTRAINT fk_gateway_market_trade_character
        FOREIGN KEY (character_id) REFERENCES characters (character_id),
    CONSTRAINT ck_gateway_market_trade_values CHECK (
        lease_version > 0 AND snapshot_revision > 0 AND snapshot_schema_version > 0 AND
        trade_side IN (1, 2) AND quantity > 0 AND expected_market_revision > 0 AND
        expected_reputation_revision > 0 AND reputation_price_factor_parts_per_million BETWEEN 900000 AND 1100000 AND
        state IN (1, 2, 3, 4)
    )
) ENGINE=InnoDB;

CREATE TABLE gateway_market_trade_ledger (
    trade_id BINARY(16) NOT NULL,
    request_sha256 BINARY(32) NOT NULL,
    character_id BIGINT NOT NULL,
    snapshot_revision BIGINT UNSIGNED NOT NULL,
    market_id VARCHAR(96) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    system_id VARCHAR(96) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    good_id VARCHAR(96) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    trade_side TINYINT UNSIGNED NOT NULL,
    quantity BIGINT UNSIGNED NOT NULL,
    total_price BIGINT UNSIGNED NOT NULL,
    committed_at_utc DATETIME(6) NOT NULL,
    PRIMARY KEY (trade_id),
    KEY ix_gateway_market_ledger_character (character_id, committed_at_utc),
    CONSTRAINT fk_gateway_market_ledger_intent
        FOREIGN KEY (trade_id) REFERENCES gateway_market_trade_intents (trade_id),
    CONSTRAINT ck_gateway_market_ledger_values CHECK (
        snapshot_revision > 0 AND trade_side IN (1, 2) AND quantity > 0
    )
) ENGINE=InnoDB;

CREATE TABLE gateway_market_trade_outbox (
    trade_id BINARY(16) NOT NULL,
    correlation_id BINARY(16) NOT NULL,
    attempt_count INT UNSIGNED NOT NULL DEFAULT 0,
    next_attempt_at_utc DATETIME(6) NOT NULL,
    delivered_at_utc DATETIME(6) NULL,
    created_at_utc DATETIME(6) NOT NULL,
    PRIMARY KEY (trade_id),
    KEY ix_gateway_market_outbox_pending (delivered_at_utc, next_attempt_at_utc),
    CONSTRAINT fk_gateway_market_outbox_ledger
        FOREIGN KEY (trade_id) REFERENCES gateway_market_trade_ledger (trade_id)
) ENGINE=InnoDB;
