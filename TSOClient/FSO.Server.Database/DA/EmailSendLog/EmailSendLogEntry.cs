namespace FSO.Server.Database.DA.EmailSendLog
{
    /// <summary>
    /// One rate-limit counter row: the number of email send <see cref="attempts"/> made for a normalized
    /// <see cref="email"/> and <see cref="purpose"/> since <see cref="window_start"/>.
    /// </summary>
    public class EmailSendLogEntry
    {
        /// <summary>
        /// Normalized (trimmed + lowercased) email address the attempts are counted against.
        /// </summary>
        public string email { get; set; }
        /// <summary>
        /// Purpose discriminator (matches ConfirmationType: 1 = email confirmation, 2 = password reset), so
        /// the two flows are limited independently.
        /// </summary>
        public int purpose { get; set; }
        /// <summary>
        /// Unix timestamp (seconds) marking the start of the current counting window.
        /// </summary>
        public uint window_start { get; set; }
        /// <summary>
        /// Number of send attempts recorded in the current window.
        /// </summary>
        public uint attempts { get; set; }
    }
}
