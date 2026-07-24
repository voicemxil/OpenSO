-- Price of an Edit A Sim appearance change (head/body/skin/gender), in simoleons.
-- UpdateAvatarAppearanceHandler reads this via Tuning.AllCategory("edit_sim", 0) and falls back to 0
-- when the row is absent, which is why the makeover has been free since the feature shipped.
--
-- Renames are deliberately NOT priced off this row: a name change alone costs nothing (it is instead
-- rate limited to once per day, see 0037). The charge applies only when the look actually changes.
INSERT INTO `fso_tuning` (`tuning_type`, `tuning_table`, `tuning_index`, `value`, `owner_type`)
VALUES ('edit_sim', 0, 0, 1000, 'STATIC')
ON DUPLICATE KEY UPDATE `value` = VALUES(`value`);
