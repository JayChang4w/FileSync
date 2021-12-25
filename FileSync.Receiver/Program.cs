using FileSync.BLL.Interface;
using FileSync.BLL.Service;
using FileSync.DOL.Setting;
using FileSync.Receiver;

public class Program
{
    public static async Task Main(string[] args)
    {
        IHost host = CreateHostBuilder(args).Build();
        
        await host.RunAsync();
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .UseWindowsService()
            .ConfigureServices((hostContext, services) =>
            {
                IConfiguration configuration = services.BuildServiceProvider().GetRequiredService<IConfiguration>();

                services.AddLogging(builder =>
                {
                    if (hostContext.HostingEnvironment.IsDevelopment())
                    {
                        builder.AddDebug().AddConsole();
                    }
                    else
                    {
                            //事件檢視器
                            builder.AddEventLog(options =>
                        {
                            options.Filter = (category, level) => (level is LogLevel.Error or LogLevel.Critical);
                        });
                    }

                    if (configuration.GetSection("EnableFileLog").Get<bool>())
                    {
                        builder.AddFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs/{Date}.txt"));
                    }
                });

                services.AddSingleton(services => services.GetRequiredService<IConfiguration>().GetSection(nameof(ReceiverSetting)).Get<ReceiverSetting>());
                services.AddSingleton(services => services.GetRequiredService<IConfiguration>().GetSection(nameof(IPEndPointSetting)).Get<IPEndPointSetting>());
                services.AddSingleton<IFileHashService, FileHashService>();

                services.AddHostedService<Worker>();
            });
}