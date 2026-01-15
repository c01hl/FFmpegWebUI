using System.Diagnostics;
using FFmpegWebUI.Data;
using FFmpegWebUI.Models;

namespace FFmpegWebUI.Services;

/// <summary>设置服务实现</summary>
public class SettingsService(ILiteDbContext db) : ISettingsService
{
    public Task<AppSettings> GetSettingsAsync()
    {
        var settings = db.Settings.FindAll().FirstOrDefault();
        if (settings == null)
        {
            settings = CreateDefaultSettings();
            db.Settings.Insert(settings);
        }
        return Task.FromResult(settings);
    }

    public Task SaveSettingsAsync(AppSettings settings)
    {
        settings.Id = db.Settings.FindAll().FirstOrDefault()?.Id ?? settings.Id;
        db.Settings.Upsert(settings);
        return Task.CompletedTask;
    }

    public Task ResetToDefaultAsync()
    {
        db.Settings.DeleteAll();
        var settings = CreateDefaultSettings();
        db.Settings.Insert(settings);
        return Task.CompletedTask;
    }

    public async Task<bool> ValidateFFmpegPathAsync(string path)
    {
        try
        {
            var ffmpegPath = string.IsNullOrEmpty(path) ? "ffmpeg" : path;

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static AppSettings CreateDefaultSettings()
    {
        return new AppSettings
        {
            FFmpegPath = string.Empty,
            FFprobePath = string.Empty,
            DefaultOutputDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            OutputNaming = OutputNamingRule.Suffix,
            OutputSuffix = "_converted",
            FileExistsAction = FileExistsAction.Ask,
            PreferHardwareAcceleration = true,
            TaskHistoryRetentionDays = 30,
            Theme = "System"
        };
    }
}
