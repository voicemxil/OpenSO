using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using FSO.Server.Api.Core;
using FSO.Server.Api.Core.Controllers;
using FSO.Server.Api.Core.Controllers.Admin;
using FSO.Server.Api.Core.Utils;
using FSO.Server.Servers.Api.JsonWebToken;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace FSO.Server.Api.Core.Tests
{
    /// <summary>
    /// Behavioural proof for <see cref="AdminAreaAuthorizationFilter"/> — the default-deny backstop that
    /// makes every controller under ...Controllers.Admin staff-only at the framework level. These assert
    /// the exact property the "no auth check for admin" claim is about: an admin endpoint reached without
    /// a valid staff token is rejected before the action runs, regardless of the per-action checks.
    /// </summary>
    public class AdminAuthorizationTests
    {
        private static Api NewApiWithJwt()
        {
            var api = new Api(); // ctor assigns Api.INSTANCE = this
            api.Config = new ApiConfig();
            api.JWT = new JWTFactory(new JWTConfiguration
            {
                Key = Encoding.UTF8.GetBytes("unit-test-signing-secret-0123456789abcdef")
            });
            return api;
        }

        private static string MintToken(Api api, params string[] claims)
        {
            return api.JWT.CreateToken(new JWTUser
            {
                UserID = 1,
                UserName = "tester",
                Claims = claims.ToList()
            }).Token;
        }

        /// <summary>Runs the filter against a real controller/method and returns the short-circuit result
        /// it set (null = the request was allowed through to the action).</summary>
        private static IActionResult RunFilter(System.Type controller, string methodName, string bearerToken)
        {
            var http = new DefaultHttpContext();
            http.Request.Method = "GET";
            if (bearerToken != null) http.Request.Headers["Authorization"] = "bearer " + bearerToken;

            ActionDescriptor descriptor = new ControllerActionDescriptor
            {
                ControllerTypeInfo = controller.GetTypeInfo(),
                MethodInfo = controller.GetMethods().First(m => m.Name == methodName)
            };

            var actionContext = new ActionContext(http, new RouteData(), descriptor);
            var ctx = new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>());
            new AdminAreaAuthorizationFilter().OnAuthorization(ctx);
            return ctx.Result;
        }

        private static int? StatusOf(IActionResult result) => (result as StatusCodeResult)?.StatusCode;

        [Fact]
        public void AdminEndpoint_NoToken_IsRejected401()
        {
            NewApiWithJwt();
            var result = RunFilter(typeof(AdminHostsController), "Get", null);
            Assert.Equal(401, StatusOf(result));
        }

        [Fact]
        public void AdminEndpoint_GarbageToken_IsRejected401()
        {
            NewApiWithJwt();
            var result = RunFilter(typeof(AdminHostsController), "Get", "not-a-real-jwt");
            Assert.Equal(401, StatusOf(result));
        }

        [Fact]
        public void AdminEndpoint_ValidNonStaffToken_IsForbidden403()
        {
            var api = NewApiWithJwt();
            // A correctly-signed token with no staff claims — e.g. a game/user token from userapi/oauth.
            var token = MintToken(api /* no claims */);
            var result = RunFilter(typeof(AdminHostsController), "Get", token);
            Assert.Equal(403, StatusOf(result));
        }

        [Fact]
        public void AdminEndpoint_ValidStaffToken_IsAllowed()
        {
            var api = NewApiWithJwt();
            var token = MintToken(api, "moderator");
            var result = RunFilter(typeof(AdminHostsController), "Get", token);
            Assert.Null(result); // passed the backstop; the action's own DemandAdmin still runs next
        }

        [Fact]
        public void StaffLoginEndpoint_IsExemptEvenWithoutToken()
        {
            NewApiWithJwt();
            // AdminOAuthController is [AdminAllowAnonymous] — it must issue tokens before the caller has one.
            var result = RunFilter(typeof(AdminOAuthController), "Post", null);
            Assert.Null(result);
        }

        [Fact]
        public void NonAdminEndpoint_IsUntouchedByTheFilter()
        {
            NewApiWithJwt();
            // A public game-data controller: the filter must be a no-op here, token or not.
            var result = RunFilter(typeof(AvatarInfoController), "GetByID", null);
            Assert.Null(result);
        }
    }
}
