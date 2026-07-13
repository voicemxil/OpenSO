using FSO.Server.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FSO.Server.Api.Core
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var host = CreateHostBuilder(args).Build();
            host.Run();
        }

        public static IAPILifetime RunAsync(string[] args)
        {
            var host = CreateHostBuilder(args).Build();
            var lifetime = new APIControl((IHostApplicationLifetime)host.Services.GetService(typeof(IHostApplicationLifetime)));
            host.Start();
            return lifetime;
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureLogging(x =>
                {
                    x.SetMinimumLevel(LogLevel.None);
                })
                .ConfigureWebHostDefaults(web => web
                    .UseUrls(args[0])
                    .UseKestrel(options =>
                    {
                        options.Limits.MaxRequestBodySize = 500000000;
                    })
                    .SuppressStatusMessages(true)
                    .UseStartup<Startup>());
    }

    public class APIControl : IAPILifetime
    {
        private IHostApplicationLifetime Lifetime;

        public APIControl(IHostApplicationLifetime lifetime)
        {
            Lifetime = lifetime;
        }

        public void Stop()
        {
            Lifetime.StopApplication();
        }
    }
}
