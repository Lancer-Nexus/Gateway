-- Gateway schema v2: separate, one-time refresh-token hash.
ALTER TABLE gateway_sessions
    ADD COLUMN refresh_token_hash VARBINARY(64) NULL AFTER token_nonce_hash;
