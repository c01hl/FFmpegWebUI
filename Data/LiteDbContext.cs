using LiteDB;
using FFmpegWebUI.Models;

namespace FFmpegWebUI.Data;

/// <summary>LiteDB 数据库上下文接口</summary>
public interface ILiteDbContext : IDisposable
{
    ILiteCollection<CommandTemplate> Templates { get; }
    ILiteCollection<ConversionTask> Tasks { get; }
    ILiteCollection<BatchTask> BatchTasks { get; }
    ILiteCollection<HardwareEncoder> Encoders { get; }
    ILiteCollection<AppSettings> Settings { get; }
}

/// <summary>LiteDB 数据库上下文</summary>
public class LiteDbContext : ILiteDbContext
{
    private readonly LiteDatabase _database;
    private bool _disposed;

    public LiteDbContext(IConfiguration configuration)
    {
        var dbPath = configuration["FFmpegWebUI:DatabasePath"];
        
        if (string.IsNullOrEmpty(dbPath))
        {
            // 默认路径
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var appFolder = Path.Combine(appDataPath, "FFmpegWebUI");
            Directory.CreateDirectory(appFolder);
            dbPath = Path.Combine(appFolder, "data.db");
        }

        _database = new LiteDatabase(dbPath);
        EnsureIndexes();
    }

    public ILiteCollection<CommandTemplate> Templates => _database.GetCollection<CommandTemplate>("templates");
    public ILiteCollection<ConversionTask> Tasks => _database.GetCollection<ConversionTask>("tasks");
    public ILiteCollection<BatchTask> BatchTasks => _database.GetCollection<BatchTask>("batch_tasks");
    public ILiteCollection<HardwareEncoder> Encoders => _database.GetCollection<HardwareEncoder>("encoders");
    public ILiteCollection<AppSettings> Settings => _database.GetCollection<AppSettings>("settings");

    private void EnsureIndexes()
    {
        // Templates 索引
        Templates.EnsureIndex(x => x.Name, unique: true);
        Templates.EnsureIndex(x => x.Type);
        Templates.EnsureIndex(x => x.Category);

        // Tasks 索引
        Tasks.EnsureIndex(x => x.Status);
        Tasks.EnsureIndex(x => x.CreatedAt);
        Tasks.EnsureIndex(x => x.BatchId);

        // BatchTasks 索引
        BatchTasks.EnsureIndex(x => x.Status);
        BatchTasks.EnsureIndex(x => x.CreatedAt);

        // Encoders 索引
        Encoders.EnsureIndex(x => x.Name, unique: true);
        Encoders.EnsureIndex(x => x.Type);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _database.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
