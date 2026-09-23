-- Lancer Nexus Gateway schema v1.
-- Migration owner: Gateway. Apply with the deployment migration runner only.
-- This file assumes the target database has already been selected.

CREATE TABLE accounts (
    account_id CHAR(36) NOT NULL,
    email VARCHAR(320) NOT NULL,
    email_normalized VARCHAR(320) NOT NULL,
    password_hash VARCHAR(255) NOT NULL,
    status ENUM('active', 'locked', 'disabled') NOT NULL DEFAULT 'active',
    created_at_utc DATETIME(6) NOT NULL,
    updated_at_utc DATETIME(6) NOT NULL,
    PRIMARY KEY (account_id),
    UNIQUE KEY uq_accounts_email_normalized (email_normalized)
) ENGINE=InnoDB;

CREATE TABLE gateway_sessions (
    session_id CHAR(36) NOT NULL,
    account_id CHAR(36) NOT NULL,
    token_nonce_hash VARBINARY(64) NOT NULL,
    created_at_utc DATETIME(6) NOT NULL,
    last_seen_at_utc DATETIME(6) NOT NULL,
    expires_at_utc DATETIME(6) NOT NULL,
    revoked_at_utc DATETIME(6) NULL,
    PRIMARY KEY (session_id),
    KEY ix_gateway_sessions_account (account_id),
    KEY ix_gateway_sessions_expiry (expires_at_utc),
    CONSTRAINT fk_gateway_sessions_account
        FOREIGN KEY (account_id) REFERENCES accounts (account_id)
) ENGINE=InnoDB;

CREATE TABLE characters (
    character_id BIGINT NOT NULL,
    account_id CHAR(36) NOT NULL,
    display_name VARCHAR(96) NOT NULL,
    created_at_utc DATETIME(6) NOT NULL,
    updated_at_utc DATETIME(6) NOT NULL,
    PRIMARY KEY (character_id),
    UNIQUE KEY uq_characters_display_name (display_name),
    KEY ix_characters_account (account_id),
    CONSTRAINT fk_characters_account
        FOREIGN KEY (account_id) REFERENCES accounts (account_id)
) ENGINE=InnoDB;

CREATE TABLE character_leases (
    character_id BIGINT NOT NULL,
    session_id CHAR(36) NOT NULL,
    instance_id VARCHAR(96) NOT NULL,
    lease_token_hash VARBINARY(64) NOT NULL,
    lease_version BIGINT UNSIGNED NOT NULL DEFAULT 0,
    valid_until_utc DATETIME(6) NOT NULL,
    PRIMARY KEY (character_id),
    KEY ix_character_leases_session (session_id),
    KEY ix_character_leases_expiry (valid_until_utc),
    CONSTRAINT fk_character_leases_character
        FOREIGN KEY (character_id) REFERENCES characters (character_id),
    CONSTRAINT fk_character_leases_session
        FOREIGN KEY (session_id) REFERENCES gateway_sessions (session_id)
) ENGINE=InnoDB;
