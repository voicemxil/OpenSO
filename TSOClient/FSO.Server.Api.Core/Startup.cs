using FSO.Server.Api.Core.Utils;
using FSO.Server.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FSO.Server.Api.Core
{
    public class Startup : IAPILifetime
    {
        public IHostApplicationLifetime AppLifetime;

        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddCors(options =>
            {
               options.AddDefaultPolicy(
                   builder =>
                   {

                       builder.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader().WithExposedHeaders("content-disposition");
                   });

                options.AddPolicy("AdminAppPolicy",
                    builder =>
                    {
                        builder.WithOrigins("https://admin.openso.org", "https://freeso.org", "http://localhost:8080").AllowAnyMethod().AllowAnyHeader().AllowCredentials().WithExposedHeaders("content-disposition", "X-Total-Count", "X-Offset");
                    });
            }).AddMvc(options =>
            {
                options.EnableEndpointRouting = false;

                // Default-deny backstop for the admin HTTP area: any controller under
                // ...Controllers.Admin is staff-only at the framework level, independent of the
                // per-action DemandModerator/DemandAdmin calls, so a future admin endpoint can't be
                // left unprotected by a forgotten check. No-op for every non-admin route.
                options.Filters.Add(new AdminAreaAuthorizationFilter());
            }).AddJsonOptions(options =>
            {
                // The admin API's request DTOs (ShutdownModel, AnnouncementModel, etc.) use public FIELDS,
                // not properties. System.Text.Json ignores fields by default, so model binding silently left
                // every value at its default — e.g. POST /admin/shards/shutdown always bound timeout=0,
                // restart=false, update=false, turning every requested restart/update into an immediate
                // blank SHUTDOWN. Enable field (de)serialization so these payloads bind correctly.
                options.JsonSerializerOptions.IncludeFields = true;
            });
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, IHostApplicationLifetime appLifetime)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseHsts();
            }
            app.UseCors();
            // Unhandled exceptions (including DemandAdmin/DemandModerator failures) would otherwise
            // surface as an empty 500 with no CORS headers — browsers report those to JS as an opaque
            // "failed to fetch", hiding the real error from the admin webapp. Catch them here and write
            // a JSON error body WITHOUT clearing the response, so CORS headers already applied by the
            // MVC CORS filter / middleware survive.
            app.Use(async (context, next) =>
            {
                try
                {
                    await next();
                }
                catch (System.Exception ex)
                {
                    if (context.Response.HasStarted) throw;
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        error = "internal_error",
                        error_description = ex.Message
                    }));
                }
            });
            //app.UseHttpsRedirection();
            app.UseMvc();
            AppLifetime = appLifetime;
        }

        public void Stop()
        {
            AppLifetime.StopApplication();
        }
    }
}
