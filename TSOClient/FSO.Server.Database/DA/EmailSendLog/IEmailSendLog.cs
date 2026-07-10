namespace FSO.Server.Database.DA.EmailSendLog
{
    /// <summary>
    /// Durable, per-email counter of verification / password-reset email SEND ATTEMPTS, used to rate limit
    /// how many such emails a single address can trigger inside a rolling window. Backed by a small DB table
    /// (fso_email_send_log) so the cap survives restarts and is shared across API instances, unlike the
    /// in-memory confirm-attempt limiter in RegistrationController.
    /// </summary>
    public interface IEmailSendLog
    {
        /// <summary>
        /// Records one send attempt against a normalized email + purpose and returns how many attempts have
        /// now been made inside the current window. The email is normalized (trim + lowercase) so casing or
        /// surrounding whitespace can't be used to bypass the key. When the window has elapsed the counter
        /// rolls over and this returns 1. Callers compare the result against their per-window cap.
        /// </summary>
        /// <param name="email">The raw email address; normalized internally.</param>
        /// <param name="purpose">Purpose discriminator (matches ConfirmationType: 1 = email, 2 = password).</param>
        /// <param name="window">Window length in seconds.</param>
        int RecordAttempt(string email, int purpose, uint window);
    }
}
