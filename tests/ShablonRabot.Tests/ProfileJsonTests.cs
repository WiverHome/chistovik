using ShablonRabot.Core;

namespace ShablonRabot.Tests;

/// <summary>Профили .json: наследование extends, импорт профиля кафедры с понятными ошибками, подстановка полей.</summary>
public class ProfileJsonTests
{
    [Theory]
    [MemberData(nameof(TestFiles.ProfileIds), MemberType = typeof(TestFiles))]
    public void СохранённыйПрофильЧитаетсяОбратно(string id)
    {
        var p = BuiltInProfiles.Load(id);
        using var dir = new TempDir();
        var path = dir.File("profile.json");
        ProfileJson.Save(p, path);
        var back = ProfileJson.LoadForImport(path);
        Assert.Equal(ProfileJson.Serialize(p), ProfileJson.Serialize(back));
    }

    [Fact]
    public void НаследованиеExtendsПереопределяетТолькоУказанное()
    {
        using var dir = new TempDir();
        ProfileJson.Save(BuiltInProfiles.Load("kursovaya"), dir.File("base.json"));
        File.WriteAllText(dir.File("child.json"), """
            { "extends": "base.json", "name": "Кафедра", // комментарии и висячие запятые допустимы
              "page": { "margins_mm": { "left": 25, } } }
            """);
        var p = ProfileJson.LoadForImport(dir.File("child.json"));
        Assert.Equal("Кафедра", p.Name);
        Assert.Equal(25, p.Page.MarginsMm.Left);
        Assert.Equal(15, p.Page.MarginsMm.Right); // остальное — из базового
        Assert.NotEmpty(p.Structure);
    }

    [Fact]
    public void ИмпортБезБазовогоФайла()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("child.json"), """{ "extends": "kursovaya.json", "name": "X" }""");
        var e = Assert.Throws<ProfileImportException>(() => ProfileJson.LoadForImport(dir.File("child.json")));
        Assert.Contains("kursovaya.json", e.Message);
    }

    [Fact]
    public void ИмпортБезСтруктуры()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("p.json"), """{ "name": "X", "title_page": { "blocks": [] } }""");
        var e = Assert.Throws<ProfileImportException>(() => ProfileJson.LoadForImport(dir.File("p.json")));
        Assert.Contains("structure", e.Message);
    }

    [Fact]
    public void ИмпортПовреждённогоФайла()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("p.json"), "{ \"name\": ");
        var e = Assert.Throws<ProfileImportException>(() => ProfileJson.LoadForImport(dir.File("p.json")));
        Assert.Contains("повреждён", e.Message);
    }

    [Fact]
    public void КопияПрофиляНезависима()
    {
        var p = BuiltInProfiles.Load("kursovaya");
        var c = p.Clone();
        c.Page.MarginsMm.Left = 99;
        c.Structure.Clear();
        Assert.NotEqual(99, p.Page.MarginsMm.Left);
        Assert.NotEmpty(p.Structure);
    }

    [Fact]
    public void ПодстановкаПолей()
    {
        var fields = new Dictionary<string, string> { ["department"] = "информатики" };
        Assert.Equal("Кафедра информатики", Placeholders.Fill("Кафедра {department}", fields));
        Assert.Equal("Кафедра {unknown}", Placeholders.Fill("Кафедра {unknown}", fields));
        Assert.True(Placeholders.IsPlaceholder(""));
        Assert.True(Placeholders.IsPlaceholder(" [Тема работы] "));
        Assert.True(Placeholders.IsPlaceholder("И. О. Фамилия"));
        Assert.False(Placeholders.IsPlaceholder("Иванов И. И."));
    }

    [Fact]
    public void ГодПоУмолчаниюТекущий()
    {
        var p = BuiltInProfiles.Load("kursovaya");
        p.Fields["year"] = "";
        Assert.Equal(DateTime.Today.Year.ToString(), Placeholders.Resolve(p)["year"]);
        Assert.DoesNotContain(Placeholders.Resolve(p).Keys, k => k.StartsWith('_'));
    }
}
