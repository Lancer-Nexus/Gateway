-- Retain character identity and foreign-key history while hiding deleted characters.
-- Deleted display names stay reserved so old leases/transfers can never alias a new pilot.
ALTER TABLE characters
    ADD COLUMN deleted_at_utc DATETIME(6) NULL AFTER updated_at_utc,
    ADD KEY ix_characters_account_active (account_id, deleted_at_utc, character_id);
