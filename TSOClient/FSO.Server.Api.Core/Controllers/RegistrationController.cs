using FSO.Server.Api.Core.Utils;
using FSO.Server.Common;
using FSO.Server.Database.DA.EmailConfirmation;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace FSO.Server.Api.Core.Controllers
{
    /// <summary>
    /// Controller for user registrations.
    /// Supports email confirmation if enabled in config.json.
    /// </summary>

    [EnableCors]
    [Route("userapi/registration")]
    [ApiController]
    public class RegistrationController : ControllerBase
    {
        private const int REGISTER_THROTTLE_SECS = 60;
        private const int EMAIL_CONFIRMATION_EXPIRE = 2 * 60 * 60; // 2 hrs
        private const int RESEND_COOLDOWN_SECS = 60;            // min seconds between (re)sends of a code for one address
        private const int CONFIRM_MAX_FAILS = 8;                // wrong-code tries per IP per window before lockout
        private const int CONFIRM_FAIL_WINDOW = 10 * 60;        // 10 minutes
        // Wrong-email attempts allowed against a single pending code before it is invalidated and the
        // user must request a fresh one. Caps distributed (multi-IP) attacks per-target instead of the
        // old global all-IP counter, which let an attacker lock out ALL registrations by burning it.
        private const int CONFIRM_MAX_TRIES_PER_CODE = 5;

        // Per-IP wrong-code attempt tracker (in memory; lost on restart, which is fine — codes expire anyway).
        // Throttles brute-forcing the small 6-digit code space.
        private static readonly object ConfirmLock = new object();
        private static readonly System.Collections.Generic.Dictionary<string, (int count, uint window)> ConfirmFails
            = new System.Collections.Generic.Dictionary<string, (int, uint)>();

        private static bool IsConfirmLocked(string ip)
        {
            lock (ConfirmLock)
                return ConfirmFails.TryGetValue(ip, out var e) && Epoch.Now - e.window <= CONFIRM_FAIL_WINDOW && e.count >= CONFIRM_MAX_FAILS;
        }
        private static void RecordConfirmFail(string ip)
        {
            lock (ConfirmLock)
            {
                var now = Epoch.Now;
                if (ConfirmFails.TryGetValue(ip, out var e) && now - e.window <= CONFIRM_FAIL_WINDOW)
                    ConfirmFails[ip] = (e.count + 1, e.window);
                else
                    ConfirmFails[ip] = (1, now);
            }
        }

        /// <summary>
        /// Alphanumeric (lowercase), no whitespace or special chars, cannot start with an underscore.
        /// </summary>
        private static Regex USERNAME_VALIDATION = new Regex("^([a-z0-9]){1}([a-z0-9_]){2,23}$");

        #region Registration
        [HttpPost]
        public IActionResult CreateUser([FromForm] RegistrationModel user)
        {
            var api = Api.INSTANCE;

            if(api.Config.SmtpEnabled)
            {
                return ApiResponse.Json(HttpStatusCode.OK, new RegistrationError()
                {
                    error = "registration_failed",
                    error_description = "missing_confirmation_token"
                });
            }

            var ip = ApiUtils.GetIP(Request);

            user.username = user.username ?? "";
            user.username = user.username.ToLowerInvariant();
            user.email = user.email ?? "";
            user.key = user.key ?? "";

            string failReason = null;
            if (user.username.Length < 3) failReason = "user_short";
            else if (user.username.Length > 24) failReason = "user_long";
            else if (!USERNAME_VALIDATION.IsMatch(user.username ?? "")) failReason = "user_invalid";
            else if ((user.password?.Length ?? 0) == 0) failReason = "pass_required";

            try
            {
                var addr = new System.Net.Mail.MailAddress(user.email);
            }
            catch
            {
                failReason = "email_invalid";
            }

            if (failReason != null)
            {
                return ApiResponse.Json(HttpStatusCode.OK, new RegistrationError()
                {
                    error = "bad_request",
                    error_description = failReason
                });
            }

            if (!string.IsNullOrEmpty(api.Config.Regkey) && api.Config.Regkey != user.key)
            {
                return ApiResponse.Json(HttpStatusCode.OK, new RegistrationError()
                {
                    error = "key_wrong",
                    error_description = failReason
                });
            }

            using (var da = api.DAFactory.Get())
            {
                //has this ip been banned?
                var ban = da.Bans.GetByIP(ip);
                if (ban != null)
                {
                    return ApiResponse.Json(HttpStatusCode.OK, new RegistrationError()
                    {
                        error = "registration_failed",
                        error_description = "ip_banned"
                    });
                }

                //has this user registered a new account too soon after their last?
                var now = Epoch.Now;
                var prev = da.Users.GetByRegisterIP(ip);
                if (now - (prev.FirstOrDefault()?.register_date ?? 0) < REGISTER_THROTTLE_SECS)
                {
                    //cannot create a new account this soon.
                    return ApiResponse.Json(HttpStatusCode.OK, new RegistrationError()
                    {
                        error = "registration_failed",
                        error_description = "registrations_too_frequent"
                    });
                }

                var userModel = api.CreateUser(user.username, user.email, user.password, ip);

                if(userModel==null)
                {
                    return ApiResponse.Json(HttpStatusCode.OK, new RegistrationError()
                    {
                        error = "registration_failed",
                        error_description = "user_exists"
                    });
                } else {
                    api.SendEmailConfirmationOKMail(user.username, user.email);
                    return ApiResponse.Json(HttpStatusCode.OK, userModel);
                }
            }
        }

        /// <summary>
        /// Create a confirmation token and send email.
        /// </summary>
        /// <param name="email"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("request")]
        public IActionResult CreateToken([FromForm] ConfirmationCreateTokenModel model)
        {
            Api api = Api.INSTANCE;

            // smtp needs to be configured for this
            if(!api.Config.SmtpEnabled)
            {
                return ApiResponse.Json(HttpStatusCode.OK, new RegistrationError()
                {
                    error = "registration_failed",
                    error_description = "smtp_disabled"
                });
            }

            if(model.confirmation_url==null||model.email==null)
            {
                return ApiResponse.Json(HttpStatusCode.OK, new RegistrationError()
                {
                    error = "registration_failed",
                    error_description = "missing_fields"
                });
            }

            // verify email syntax
            // To do: check if email address is disposable.
            try
            {
                var addr = new System.Net.Mail.MailAddress(model.email);
            }
            catch
            {
                return ApiResponse.Json(HttpStatusCode.OK, new RegistrationError()
                {
                    error = "registration_failed",
                    error_description = "email_invalid"
                });
            }

            using (var da = api.DAFactory.Get())
            {
                // email is taken
                if(da.Users.GetByEmail(model.email)!=null)
                {
                    return ApiResponse.Json(HttpStatusCode.OK, new RegistrationError()
                    {
                        error = "registration_failed",
                        error_description = "email_taken"
                    });
                }

                EmailConfirmation confirm = da.EmailConfirmations.GetByEmail(model.email, ConfirmationType.email);

                // Already a pending confirmation for this email: resend (gated by a cooldown) rather than
                // refusing, so the in-client "resend code" and website retry buttons work. Past the cooldown
                // we invalidate the old code and issue a fresh one below.
                if (confirm != null)
                {
                    var created = confirm.expires - (uint)EMAIL_CONFIRMATION_EXPIRE;
                    if (Epoch.Now - created < RESEND_COOLDOWN_SECS)
                    {
                        return ApiResponse.Json(HttpStatusCode.OK, new RegistrationError()
                        {
                            error = "registration_failed",
                            error_description = "resend_cooldown"
                        });
                    }
                    da.EmailConfirmations.Remove(confirm.token);
                }

                uint expires = Epoch.Now + EMAIL_CONFIRMATION_EXPIRE;

                // create new email confirmation
                string token = da.EmailConfirmations.Create(new EmailConfirmation
                {
                    type = ConfirmationType.email,
                    email = model.email,
                    expires = expires
                });

                // send email with recently generated token
                bool sent = api.SendEmailConfirmationMail(model.email, token, model.confirmation_url, expires);
                 
                if(sent)
                {
                    return ApiResponse.Json(HttpStatusCode.OK, new
                    {
                        status = "success"
                    });
                }

                return ApiResponse.Json(HttpStatusCode.OK, new
                {
                    status = "email_failed"
                });
               
            }
        }

        /// <summary>
        /// Create a user with a valid email confirmation token.
        /// </summary>
        /// <param name="user"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("confirm")]
        public IActionResult CreateUserWithToken([FromForm] RegistrationUseTokenModel user)
        {
            Api api = Api.INSTANCE;

            if (user == null)
            {
                return ApiResponse.Json(HttpStatusCode.OK, new RegistrationError()
                {
                    error = "registration_failed",
                    error_description = "invalid_token"
                });
            }

            using (var da = api.DAFactory.Get())
            {
                var ip = ApiUtils.GetIP(Request);

                // Throttle brute-forcing the 6-digit code: reject once an IP racks up too many wrong codes.
                if (IsConfirmLocked(ip))
                {
                    return ApiResponse.Json(HttpStatusCode.OK, new RegistrationError()
                    {
                        error = "registration_failed",
                        error_description = "too_many_attempts"
                    });
                }

                EmailConfirmation confirmation = da.EmailConfirmations.GetByToken(user.token);

                // Bind the code to the email it was issued for. Without this, a guessed/brute-forced code could
                // bind an attacker's username+password to someone else's already-verified email (account
                // takeover of the pending registration). Both an unknown code AND a code whose bound email does
                // not match the submitted email take the exact same path (invalid_token + brute-force counter),
                // so the response can't be used as an oracle for which of the two was wrong.
                var submittedEmail = (user.email ?? "").Trim();
                if (confirmation == null
                    || !string.Equals(submittedEmail, (confirmation.email ?? "").Trim(), System.StringComparison.OrdinalIgnoreCase))
                {
                    RecordConfirmFail(ip);
                    if (confirmation != null)
                    {
                        // The code exists but was submitted with the wrong email: count the failure against
                        // the code itself, so a distributed (multi-IP) sweep gets at most a handful of guesses
                        // per code. At the cap the pending confirmation is deleted, forcing the legitimate
                        // owner to request a fresh code (the designed recovery path via /request, which is
                        // still subject to RESEND_COOLDOWN). Unknown codes have no row to count against —
                        // blind token guessing stays covered by the per-IP limiter above.
                        var tries = da.EmailConfirmations.IncrementTries(confirmation.token);
                        if (tries >= CONFIRM_MAX_TRIES_PER_CODE)
                        {
                            da.EmailConfirmations.Remove(confirmation.token);
                        }
                    }
                    // Same response for wrong-token, wrong-email and tries-exceeded: no oracle.
                    return ApiResponse.Json(HttpStatusCode.OK, new RegistrationError()
                    {
                        error = "registration_failed",
                        error_description = "invalid_token"
                    });
                }

                user.username = user.username ?? "";
                user.username = user.username.ToLowerInvariant();
                user.key = user.key ?? "";

                string failReason = null;
                if (user.username.Length < 3) failReason = "user_short";
                else if (user.username.Length > 24) failReason = "user_long";
                else if (!USERNAME_VALIDATION.IsMatch(user.username ?? "")) failReason = "user_invalid";
                else if ((user.password?.Length ?? 0) == 0) failReason = "pass_required";

                try
                {
                    var addr = new System.Net.Mail.MailAddress(confirmation.email);
                }
                catch
                {
                    failReason = "email_invalid";
                }

                if (failReason != null)
                {
                    return ApiResponse.Json(HttpStatusCode.OK, new RegistrationError()
                    {
                        error = "bad_request",
                        error_description = failReason
                    });
                }

                if (!string.IsNullOrEmpty(api.Config.Regkey) && api.Config.Regkey != user.key)
                {
                    return ApiResponse.Json(HttpStatusCode.OK, new RegistrationError()
                    {
                        error = "key_wrong",
                        error_description = failReason
                    });
                }

                //has this ip been banned?
                var ban = da.Bans.GetByIP(ip);
                if (ban != null)
                {
                    return ApiResponse.Json(HttpStatusCode.OK, new RegistrationError()
                    {
                        error = "registration_failed",
                        error_description = "ip_banned"
                    });
                }

                //has this user registered a new account too soon after their last?
                var prev = da.Users.GetByRegisterIP(ip);
                if (Epoch.Now - (prev.FirstOrDefault()?.register_date ?? 0) < REGISTER_THROTTLE_SECS)
                {
                    //cannot create a new account this soon.
                    return ApiResponse.Json(HttpStatusCode.OK, new RegistrationError()
                    {
                        error = "registration_failed",
                        error_description = "registrations_too_frequent"
                    });
                }

                //create user in db
                var userModel = api.CreateUser(user.username, confirmation.email, user.password, ip);

                if (userModel == null)
                {
                    return ApiResponse.Json(HttpStatusCode.OK, new RegistrationError()
                    {
                        error = "registration_failed",
                        error_description = "user_exists"
                    });
                }
                else
                {
                    //send OK email
                    api.SendEmailConfirmationOKMail(user.username, confirmation.email);
                    da.EmailConfirmations.Remove(user.token);
                    return ApiResponse.Json(HttpStatusCode.OK, userModel);
                }
            }
        }

        #endregion
    }

    #region Models
    public class RegistrationError
    {
        public string error_description { get; set; }
        public string error { get; set; }
    }

    public class RegistrationModel
    {
        public string username { get; set; }
        public string email { get; set; }
        public string password { get; set; }
        public string key { get; set; }
    }

    /// <summary>
    /// Expected request data when trying to create a token to register.
    /// </summary>
    public class ConfirmationCreateTokenModel
    {
        public string email { get; set; }
        /// <summary>
        /// The link the user will have to go to in order to confirm their token.
        /// If %token% is present in the url, it will be replaced with the user's token.
        /// </summary>
        public string confirmation_url { get; set; }
    }

    /// <summary>
    /// Expected request data when trying to register with a token.
    /// </summary>
    public class RegistrationUseTokenModel
    {
        public string username { get; set; }
        /// <summary>
        /// The email the confirmation code was sent to. Must match the email the code is bound to
        /// server-side; this stops a guessed code from being used against a different (victim) email.
        /// </summary>
        public string email { get; set; }
        /// <summary>
        /// User password.
        /// </summary>
        public string password { get; set; }
        /// <summary>
        /// Registration key.
        /// </summary>
        public string key { get; set; }
        /// <summary>
        /// The unique GUID.
        /// </summary>
        public string token { get; set; }
    }

    #endregion
}