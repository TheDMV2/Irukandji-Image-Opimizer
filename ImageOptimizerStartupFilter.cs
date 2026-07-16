using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Irukandji.ImageOptimizer.Middleware
{
    public class ImageOptimizerStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return builder =>
            {
                builder.UseMiddleware<ImageOptimizerMiddleware>();
                next(builder);
            };
        }
    }
}