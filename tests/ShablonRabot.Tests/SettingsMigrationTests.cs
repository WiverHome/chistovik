using System.Text.Json;
using ShablonRabot.App.Services;
using ShablonRabot.Core;

namespace ShablonRabot.Tests;

/// <summary>
/// Настройки между запусками и перенос данных версии 1.0 («Конструктор шаблонов работ», папка KonstruktorShablonov).
/// Всё во временных папках — настоящие %APPDATA% и ключ ИИ пользователя не трогаются.
/// </summary>
public class SettingsMigrationTests
{
    /// <summary>Папка 1.0: settings.json, импортированный профиль и журнал — как у настоящей версии 1.0.</summary>
    static string MakeLegacy(TempDir root)
    {
        var legacy = root.File("KonstruktorShablonov");
        Directory.CreateDirectory(Path.Combine(legacy, "profiles"));
        var imported = Path.Combine(legacy, "profiles", "kafedra.json");
        File.WriteAllText(imported, ProfileJson.Serialize(BuiltInProfiles.Load("kursovaya")));
        var settings = new
        {
            fields = new Dictionary<string, string> { ["student"] = "Петров П. П.", ["university"] = "Проверочный университет" },
            profiles = new Dictionary<string, object> { ["kursovaya"] = new { fields = new Dictionary<string, string> { ["topic"] = "Тема из 1.0" } } },
            imported = new[] { new { id = "kafedra", name = "Методичка кафедры", path = imported, original_file_name = "kafedra.json", imported_at = DateTime.Now } },
            last_profile = "kursovaya",
            theme = "dark",
            first_run_done = true,
        };
        File.WriteAllText(Path.Combine(legacy, "settings.json"), JsonSerializer.Serialize(settings, ProfileJson.Options));
        File.WriteAllText(Path.Combine(legacy, "errors.log"), "старый журнал");
        return legacy;
    }

    static void AssertLegacyData(AppSettings s, string dir)
    {
        Assert.Equal("Петров П. П.", s.Fields["student"]);
        Assert.Equal("Тема из 1.0", s.Profiles["kursovaya"].Fields["topic"]);
        Assert.Equal("kursovaya", s.LastProfile);
        Assert.Equal("dark", s.Theme);
        Assert.True(s.FirstRunDone);
        var imp = Assert.Single(s.Imported);
        // Путь импортированного профиля переведён в новую папку, и файл там есть
        Assert.Equal(Path.Combine(dir, "profiles", "kafedra.json"), imp.Path);
        Assert.True(File.Exists(imp.Path));
        Assert.Equal("Методичка кафедры", imp.Name);
        Assert.Equal(BuiltInProfiles.Load("kursovaya").Name, ProfileJson.Load(imp.Path).Name);
    }

    [Fact]
    public void ПапкаВерсии10ПереноситсяЦеликом()
    {
        using var root = new TempDir();
        var legacy = MakeLegacy(root);
        var dir = root.File("Chistovik");

        var s = AppSettings.Load(dir, legacy);

        AssertLegacyData(s, dir);
        Assert.False(Directory.Exists(legacy));
        Assert.True(File.Exists(Path.Combine(dir, "settings.json")));
        // Новый settings.json уже с переведёнными путями — при следующем запуске перенос не повторяется
        var again = AppSettings.Load(dir, legacy);
        AssertLegacyData(again, dir);
    }

    [Fact]
    public void ЕслиНоваяПапкаУжеЕстьФайлыКопируются()
    {
        using var root = new TempDir();
        var legacy = MakeLegacy(root);
        var dir = root.File("Chistovik");
        // Папка Chistovik появилась раньше (журнал ошибок), а настроек в ней ещё нет
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "errors.log"), "новый журнал");

        var s = AppSettings.Load(dir, legacy);

        AssertLegacyData(s, dir);
        Assert.True(Directory.Exists(legacy)); // при копировании старая папка остаётся
        Assert.StartsWith("новый журнал", File.ReadAllText(Path.Combine(dir, "errors.log"))); // свои файлы не затираются
    }

    [Fact]
    public void СвоиНастройкиВажнееСтарых()
    {
        using var root = new TempDir();
        var legacy = MakeLegacy(root);
        var dir = root.File("Chistovik");
        var own = AppSettings.Load(dir, null);
        own.Fields["student"] = "Сидоров С. С.";
        own.Save();

        var s = AppSettings.Load(dir, legacy);

        Assert.Equal("Сидоров С. С.", s.Fields["student"]);
        Assert.Empty(s.Imported);
        Assert.True(Directory.Exists(legacy));
    }

    [Fact]
    public void БезПапкиВерсии10ПереносаНет()
    {
        using var root = new TempDir();
        var legacy = MakeLegacy(root);
        var dir = root.File("Chistovik");

        var s = AppSettings.Load(dir, legacyDir: null);

        Assert.Empty(s.Fields);
        Assert.True(Directory.Exists(legacy));
        Assert.False(File.Exists(Path.Combine(dir, "settings.json")));
    }

    [Fact]
    public void ПовреждённыеНастройкиНеМешаютЗапуску()
    {
        using var root = new TempDir();
        var dir = root.File("Chistovik");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{ это не json");

        var s = AppSettings.Load(dir, null);

        Assert.Empty(s.Fields);
        Assert.Equal("system", s.Theme);
        Assert.Contains("Не удалось прочитать настройки", File.ReadAllText(Path.Combine(dir, "errors.log")));
        // Сохранение после сбоя идёт в ту же папку
        s.Fields["student"] = "Новый";
        s.Save();
        Assert.Equal("Новый", AppSettings.Load(dir, null).Fields["student"]);
    }

    [Fact]
    public void НастройкиСохраняютсяИЧитаютсяОбратно()
    {
        using var root = new TempDir();
        var dir = root.File("Chistovik");
        var s = AppSettings.Load(dir, null);
        s.Fields["group"] = "ИВТ-21";
        s.For("vkr").Fields["topic"] = "Тема ВКР";
        s.For("vkr").Drafts = new() { ["intro"] = "Черновик введения" };
        s.Ai.Provider = "openai";
        s.Ai.Model = "model-x";
        s.EducationLevel = "spo";
        s.Window = new WindowPlacement { Left = 10, Top = 20, Width = 800, Height = 600, Maximized = true };
        s.Save();

        Assert.False(File.Exists(Path.Combine(dir, "settings.json.tmp")));
        var r = AppSettings.Load(dir, null);
        Assert.Equal("ИВТ-21", r.Fields["group"]);
        Assert.Equal("Тема ВКР", r.Profiles["vkr"].Fields["topic"]);
        Assert.Equal("Черновик введения", r.Profiles["vkr"].Drafts!["intro"]);
        Assert.Equal("openai", r.Ai.Provider);
        Assert.Equal("spo", r.EducationLevel);
        Assert.True(r.Window!.Maximized);
    }
}
