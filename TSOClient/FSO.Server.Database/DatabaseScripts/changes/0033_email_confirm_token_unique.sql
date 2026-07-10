-- The pre-fix token allocation (SqlEmailConfirmations.Create) checked for an existing token via a
-- SELECT COUNT(*) and then INSERTed separately, with no DB constraint backing it up. Two concurrent
-- registration/resend requests could both pass that check for the same 6-digit code before either
-- committed, leaving two rows usable with the same confirmation token. Dedupe any rows already left
-- over from that race before adding the constraint: for each token shared by more than one row, keep
-- only the most-recently issued row (highest `expires`, then `email`, then `type` as a deterministic
-- tie-break since this table has no primary key) and delete the rest.
DELETE c1 FROM `fso_email_confirm` c1
INNER JOIN `fso_email_confirm` c2
	ON c1.`token` = c2.`token`
	AND c1.`token` IS NOT NULL
	AND (
		c1.`expires` < c2.`expires`
		OR (c1.`expires` = c2.`expires` AND c1.`email` < c2.`email`)
		OR (c1.`expires` = c2.`expires` AND c1.`email` = c2.`email` AND c1.`type` < c2.`type`)
	);

-- Enforce token uniqueness at the DB level going forward. NULL tokens are unaffected (MySQL treats
-- each NULL as distinct under a UNIQUE index), so this only guards real, issued codes.
ALTER TABLE `fso_email_confirm`
ADD UNIQUE INDEX `token_UNIQUE` (`token` ASC);
