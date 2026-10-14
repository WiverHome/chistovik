using System.IO;
using System.Text.Json;
using ShablonRabot.Core;

namespace ShablonRabot.App.Services;

public sealed class ProfileState
{
    /// <summary>Поля, зависящие от вида работы (work_type, министерство…).</summary>
    public Dictionary<string, string> Fields { get; set; } = new();
    /// <summary>Изменённые правила и структура — JSON профиля без данных студента.</summary>
    public string? Edits { get; set; }
    /// <summary>Правила определены по примеру: источник и итог сравнения с ГОСТ (DESIGN.md 17.5, п. 6).</summary>
    public string? DetectedSource { get; set; }
    public string? DetectedVerdict { get; set; }
}

public sealed class ImportedProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Копия файла в папке профилей приложения.</summary>
    public string Path { get; set; } = "";
    public string OriginalFileName { get; set; } = "";
    public DateTime ImportedAt { get; set; }
}

public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>Настройки и данные пользователя между запусками (%APPDATA%\Chistovik\settings.json).</summary>
public sealed class AppSettings
{
    public static readonly string Dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Chistovik");
    /// <summary>Папка версии 1.0, когда программа называлась «Конструктор шаблонов работ».</summary>
    static readonly string LegacyDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KonstruktorShablonov");
    public static readonly string ProfilesDir = System.IO.Path.Combine(Dir, "profiles");
    static readonly string FilePath = System.IO.Path.Combine(Dir, "settings.json");

    /// <summary>Данные титульного листа, общие для всех видов работ (ФИО, вуз, группа…).</summary>
    public Dictionary<string, string> Fields { get; set; } = new();
    public Dictionary<string, ProfileState> Profiles { get; set; } = new();
    public List<ImportedProfile> Imported { get; set; } = new();
    public string? LastProfile { get; set; }
    public string? LastPage { get; set; }
    public string? LastDirectory { get; set; }
    public string? DefaultDirectory { get; set; }
    public string? LastFile { get; set; }
    public bool SavePdf { get; set; }
    public bool OpenAfter { get; set; } = true;
    public bool FirstRunDone { get; set; }
    public string Theme { get; set; } = "system";
    public WindowPlacement? Window { get; set; }

    public ProfileState For(string profileKey)
    {
        if (!Profiles.TryGetValue(profileKey, out var state)) Profiles[profileKey] = state = new ProfileState();
        return state;
    }

    public static AppSettings Load()
    {
        var migrated = MigrateLegacyDir();
        try
        {
            var settings = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), ProfileJson.Options) ?? new()
                : new();
            if (migrated)
            {
                // Импортированные профили лежали в старой папке — пути в настройках указывают туда
                foreach (var imp in settings.Imported)
                    if (imp.Path.StartsWith(LegacyDir, StringComparison.OrdinalIgnoreCase))
                        imp.Path = Dir + imp.Path[LegacyDir.Length..];
                settings.Save();
            }
            return settings;
        }
        catch (Exception)
        {
            return new(); // повреждённый файл настроек не должен мешать запуску
        }
    }

    /// <summary>
    /// Переносит данные версии 1.0 (%APPDATA%\KonstruktorShablonov) в новую папку, если новой ещё нет.
    /// Возвращает true, если перенос был.
    /// </summary>
    static bool MigrateLegacyDir()
    {
        try
        {
            if (Directory.Exists(Dir) || !Directory.Exists(LegacyDir)) return false;
            try { Directory.Move(LegacyDir, Dir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Папку нельзя переместить (открыта в Проводнике, нет прав) — копируем, старая остаётся как есть
                foreach (var file in Directory.EnumerateFiles(LegacyDir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var target = System.IO.Path.Combine(Dir, System.IO.Path.GetRelativePath(LegacyDir, file));
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                        File.Copy(file, target, overwrite: false);
                    }
                    catch (Exception fe) when (fe is IOException or UnauthorizedAccessException) { /* один файл не мешает остальным */ }
                }
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false; // не получилось — начнём с чистых настроек, старая папка не тронута
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, ProfileJson.Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // не критично: в худшем случае данные не запомнятся
        }
    }

    public static void DeleteAll()
    {
        try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
