using FileSync.BLL.Interface;
using FileSync.BLL.Service;
using FileSync.DOL.Setting;
using FileSync.Transfer;
using System.Net;

public class Program
{
    public static void Main(string[] args)
    {
        CreateHostBuilder(args).Build().Run();
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

                services.AddSingleton(services => services.GetRequiredService<IConfiguration>().GetSection(nameof(TransferSetting)).Get<TransferSetting>());
                services.AddSingleton(services =>
                {
                    var endPointSetting = services.GetRequiredService<IConfiguration>().GetSection(nameof(IPEndPointSetting)).Get<IPEndPointSetting>();

                    return new IPEndPoint(IPAddress.Parse(endPointSetting.HostNameOrAddress), endPointSetting.Port);
                });
                services.AddSingleton<IFileHashService, FileHashService>();
                services.AddSingleton<IEnumerateFilesService, EnumerateFileService>();
                services.AddHostedService<Worker>();
            });
}