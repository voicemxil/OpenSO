-- Durable, per-email windowed cap on verification / password-reset email SEND ATTEMPTS.
-- The fixed resend cooldown (RESEND_COOLDOWN_SECS) only bites while a pending code survives, and it is
-- enforced only on the success path: a failing SMTP send deletes its token, so a blocked provider could be
-- hammered by retrying in a tight loop, and a victim could be mailed once per cooldown indefinitely. This
-- table counts ATTEMPTS (not surviving tokens), keyed by the normalized (trim + lowercase) email plus a
-- purpose discriminator (1 = email confirmation, 2 = password reset, matching ConfirmationType), inside a
-- rolling window. RegistrationController / PasswordController record every attempt that reaches the send and
-- refuse once the count exceeds the per-window cap, WITHOUT sending. Unlike the in-memory confirm-attempt
-- limiter this survives restarts and is shared across API instances. The (email, purpose) primary key is the
-- source of truth for one counter row per key; a concurrent opener loses the INSERT race with a duplicate-key
-- error the DA folds back into the increment path.
CREATE TABLE IF NOT EXISTS `fso_email_send_log` (
	`email` VARCHAR(255) NOT NULL,
	`purpose` INT(10) UNSIGNED NOT NULL,
	`window_start` INT(10) UNSIGNED NOT NULL,
	`attempts` INT(10) UNSIGNED NOT NULL DEFAULT 0,
	PRIMARY KEY (`email`, `purpose`)
)
COMMENT='Per-email windowed counter of verification/reset email send attempts (rate limiting).'
COLLATE='latin1_swedish_ci'
ENGINE=InnoDB
;
