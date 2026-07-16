using Maieutics;
using Microsoft.Extensions.Hosting;

await MaieuticsHost.CreateApplicationBuilder(args).Build().RunAsync();