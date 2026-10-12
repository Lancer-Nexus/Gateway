-- Keep the current world nickname beside the lease-fenced SaveGame revision.
-- Older snapshots remain NULL until the owning instance writes a revision.
ALTER TABLE character_save_snapshots
    ADD COLUMN system_id VARCHAR(96) CHARACTER SET ascii COLLATE ascii_bin NULL AFTER character_id;
