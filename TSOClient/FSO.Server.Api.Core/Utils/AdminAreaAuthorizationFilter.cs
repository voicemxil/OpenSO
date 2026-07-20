using System;
using System.Linq;
using System.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FSO.Server.Api.Core.Utils
{
    /// <summary>
    /// Opt-out marker for the <see cref="AdminAreaAuthorizationFilter"/>. Put this on an admin-area
    /// controller or action that must be reachable WITHOUT a staff token — currently just the staff
    /// login endpoint that issues tokens (it can't require the token it's about to mint). Anything in
    /// the admin namespace WITHOUT this attribute is staff-only by default.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
    public class AdminAllowAnonymousAttribute : Attribute { }

    /// <summary>
    /// Default-deny backstop for the admin HTTP area, registered as a global MVC filter.
    ///
    /// Every endpoint whose controller lives in the <c>...Controllers.Admin</c> namespace requires an
    /// authenticated staff (moderator or admin) token, UNLESS it is explicitly marked
    /// <see cref="AdminAllowAnonymousAttribute"/>. This makes the admin area protected by construction:
    /// a newly added admin controller/action is staff-only even if the author forgets the per-action
    /// <c>DemandModerator</c>/<c>DemandAdmin</c> call. Those per-action calls still run and apply the finer
    /// admin-vs-moderator distinction on top of this moderator-level floor.
    ///
    /// The filter is a no-op for every non-admin endpoint, so it does not touch the public game/city API.
    /// </summary>
    public class AdminAreaAuthorizationFilter : IAuthorizationFilter
    {
        public void OnAuthorization(AuthorizationFilterContext context)
        {
            // CORS preflight carries no Authorization header and is short-circuited by the CORS
            // middleware anyway; never let it reach the auth check.
            if (HttpMethods.IsOptions(context.HttpContext.Request.Method)) return;

            // Only controller actions have a namespace to inspect; anything else isn't the admin area.
            if (!(context.ActionDescriptor is ControllerActionDescriptor descriptor)) return;

            var ns = descriptor.ControllerTypeInfo.Namespace ?? "";
            if (!ns.EndsWith(".Controllers.Admin", StringComparison.Ordinal)) return; // no-op off the admin area

            // Explicit pre-auth opt-out (the staff login endpoint).
            var allowAnonymous =
                descriptor.MethodInfo.GetCustomAttributes(typeof(AdminAllowAnonymousAttribute), true).Length > 0 ||
                descriptor.ControllerTypeInfo.GetCustomAttributes(typeof(AdminAllowAnonymousAttribute), true).Length > 0;
            if (allowAnonymous) return;

            try
            {
                var user = Api.INSTANCE.RequireAuthentication(context.HttpContext.Request);
                if (user?.Claims == null || !user.Claims.Contains("moderator"))
                {
                    // Valid token, but not staff.
                    context.Result = new StatusCodeResult(403);
                }
            }
            catch (SecurityException)
            {
                // Missing / malformed / expired / wrong-signature token.
                context.Result = new StatusCodeResult(401);
            }
            catch (Exception)
            {
                // Any other decode failure is treated as unauthenticated, never as a pass.
                context.Result = new StatusCodeResult(401);
            }
        }
    }
}
