using MeuralImageBackup.Core.Data;
using MeuralImageBackup.Core.Files;
using MeuralImageBackup.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MeuralImageBackup.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMeuralCore(this IServiceCollection services, StoragePaths storage, bool useFrameFolder)
    {
        services.AddSingleton(storage);
        services.AddSingleton(new ImagePaths(storage, useFrameFolder));
        services.AddDbContextFactory<MeuralDb>(o => o.UseSqlite(storage.ConnectionString));
        services.AddSingleton<LibraryService>();
        services.AddSingleton<ThumbnailService>();

        // The downloads from Meural's servers. Their firewall turns away requests without a User-Agent.
        services.AddHttpClient<UploadService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(90);
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"MeuralImageBackup/{AppInfo.ApiVersion}");
        });
        return services;
    }
}
