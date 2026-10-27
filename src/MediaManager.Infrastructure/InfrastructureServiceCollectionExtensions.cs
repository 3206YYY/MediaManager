using MediaManager.Core.Services;
using MediaManager.Infrastructure.Database;
using MediaManager.Infrastructure.FileSystem;
using MediaManager.Infrastructure.Media;
using MediaManager.Infrastructure.Portable;
using MediaManager.Infrastructure.Thumbnails;
using MediaManager.Infrastructure.Vault;
using Microsoft.Extensions.DependencyInjection;

namespace MediaManager.Infrastructure;

/// <summary>Infrastructure 层依赖注入注册。</summary>
public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        // 便携模式
        services.AddSingleton<IPortableModeDetector, PortableModeDetector>();

        // 数据库
        services.AddSingleton<IDbConnectionFactory, SqliteConnectionFactory>();
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<IMediaRepository, MediaRepository>();
        services.AddSingleton<IDiaryRepository, DiaryRepository>();
        services.AddSingleton<IDatabaseBackupService, DatabaseBackupService>();
        services.AddSingleton<IDatabaseSnapshotService, DatabaseSnapshotService>();
        services.AddSingleton<IFullBackupService, FullBackupService>();

        // 配置
        services.AddSingleton<IAppConfigService, AppConfigService>();

        // 服务
        services.AddSingleton<IScanService, ScanService>();
        services.AddSingleton<IMediaImportService, MediaImportService>();
        services.AddSingleton<IThumbnailService, ThumbnailService>();
        services.AddSingleton<IFileOperationService, FileOperationService>();
        services.AddSingleton<IOperationLogService, OperationLogService>();
        services.AddSingleton<IExifService, ExifService>();
        services.AddSingleton<IFileWatcherService, FileWatcherService>();
        services.AddSingleton<IDuplicateService, DuplicateService>();
        services.AddSingleton<Media.IImageEditService, Media.ImageEditService>();

        // 私密空间
        services.AddSingleton<IVaultService, VaultService>();

        return services;
    }
}
