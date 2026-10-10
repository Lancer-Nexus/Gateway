-- Allocate Gateway-owned character IDs when an authenticated game instance first
-- resolves a locally selected character. Existing IDs are retained; allocation
-- continues above the current maximum.
ALTER TABLE characters
    MODIFY character_id BIGINT NOT NULL AUTO_INCREMENT;
