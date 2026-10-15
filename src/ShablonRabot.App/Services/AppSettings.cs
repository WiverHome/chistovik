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

    static string LegacyFilePath => System.IO.Path.Combine(LegacyDir, "settings.json");

    public static AppSettings Load()
    {
        // Данные версии 1.0 переносим, только если своих настроек ещё нет — решает файл, а не папка:
        // папка Chistovik может появиться раньше (журнал ошибок, временные файлы)
        var fromLegacy = !File.Exists(FilePath) && File.Exists(LegacyFilePath);
        if (fromLegacy) MigrateLegacyFiles();
        try
        {
            // Если перенести не удалось, читаем старый файл напрямую: данные пользователя не должны «пропасть»
            var source = File.Exists(FilePath) ? FilePath : fromLegacy ? LegacyFilePath : null;
            var settings = source != null
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(source), ProfileJson.Options) ?? new()
                : new();
            if (fromLegacy)
            {
                // Пути импортированных профилей указывают в старую папку — переводим на новую, если файл там есть
                foreach (var imp in settings.Imported)
                {
                    if (!imp.Path.StartsWith(LegacyDir, StringComparison.OrdinalIgnoreCase)) continue;
                    var moved = Dir + imp.Path[LegacyDir.Length..];
                    if (File.Exists(moved)) imp.Path = moved;
                }
                settings.Save();
            }
            return settings;
        }
        catch (Exception e)
        {
            Log("Не удалось прочитать настройки", e);
            return new(); // повреждённый файл настроек не должен мешать запуску
        }
    }

    /// <summary>
    /// Переносит данные версии 1.0 (%APPDATA%\KonstruktorShablonov) в новую папку.
    /// Если новой папки ещё нет — перемещает целиком, иначе (или если переместить нельзя) копирует файлы.
    /// </summary>
    static void MigrateLegacyFiles()
    {
        if (!Directory.Exists(Dir))
        {
            try { Directory.Move(LegacyDir, Dir); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log("Перенос папки 1.0 не удался, копирую файлы", e); }
        }
        try
        {
            foreach (var file in Directory.EnumerateFiles(LegacyDir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var target = System.IO.Path.Combine(Dir, System.IO.Path.GetRelativePath(LegacyDir, file));
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                    if (!File.Exists(target)) File.Copy(file, target);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log($"Не скопирован {System.IO.Path.GetFileName(file)}", e); }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log("Не удалось прочитать папку 1.0", e); }
    }

    /// <summary>Запись в журнал ошибок (тот же файл, что у App): сбои настроек иначе не видны.</summary>
    static void Log(string what, Exception e)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.AppendAllText(System.IO.Path.Combine(Dir, "errors.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {what}: {e}\n\n");
        }
        catch { /* журнал — не повод для новой ошибки */ }
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
