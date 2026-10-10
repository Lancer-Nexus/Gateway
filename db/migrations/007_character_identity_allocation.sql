-- Allocate globally unique Gateway character IDs above all existing IDs.
CREATE TABLE gateway_character_id_allocator (
    character_id BIGINT NOT NULL AUTO_INCREMENT,
    PRIMARY KEY (character_id)
) ENGINE=InnoDB;

INSERT INTO gateway_character_id_allocator (character_id)
SELECT MAX(character_id) FROM characters HAVING COUNT(*) > 0;
