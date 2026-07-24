-- When this avatar was last renamed from Edit A Sim. NULL means "never renamed".
--
-- Renaming is free, so the only thing stopping a player cycling names to dodge reputation or
-- impersonate someone is a rate limit: UpdateAvatarAppearanceHandler refuses a rename within 24h of
-- this timestamp. Stored per avatar rather than in fso_global_cooldowns, which is keyed by object
-- GUID for in-game object cooldowns and has no meaning outside a lot.
ALTER TABLE `fso_avatars` ADD COLUMN `name_change_date` DATETIME NULL DEFAULT NULL;
