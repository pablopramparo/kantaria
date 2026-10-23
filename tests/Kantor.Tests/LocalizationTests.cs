// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Reflection;
using System.Text.RegularExpressions;
using Kantor.Core.Humor;
using Kantor.Core.Localization;
using Kantor.Core.Menu;
using Kantor.Core.Results;
using Kantor.Core.Settings;

namespace Kantor.Tests;

public class LocalizationTests
{
    // ---- Idiomas ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("es", "es")]
    [InlineData("es-AR", "es-AR")]
    [InlineData("es_ar", "es-AR")]
    [InlineData("ES-AR", "es-AR")]
    [InlineData("es-MX", "es")]
    [InlineData("es-ES", "es")]
    [InlineData("en", "en")]
    [InlineData("en-US", "en")]
    [InlineData("EN_gb", "en")]
    [InlineData("eng", "es")]
    [InlineData("end", "es")]
    [InlineData("fr", "es")]
    [InlineData("", "es")]
    [InlineData(null, "es")]
    public void Any_tag_is_taken_to_one_of_the_three_languages(string? tag, string expected) =>
        Assert.Equal(expected, AppLanguage.Normalize(tag));

    [Theory]
    [InlineData("es-AR", "es")]
    [InlineData("es", "es")]
    [InlineData("en", "en")]
    public void The_base_language_has_no_variant(string tag, string expected) =>
        Assert.Equal(expected, AppLanguage.Base(tag));

    // ---- Voseo → tuteo ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Elegí quién canta.", "Elige quién canta.")]
    [InlineData("Probá con otra palabra.", "Prueba con otra palabra.")]
    [InlineData("¿Querés guardar los resultados?", "¿Quieres guardar los resultados?")]
    [InlineData("Si cargás solo el cuerpo, recortás la cara de ahí.", "Si cargas solo el cuerpo, recortas la cara de ahí.")]
    [InlineData("Podés crear uno nuevo acá abajo.", "Puedes crear uno nuevo acá abajo.")]
    [InlineData("ESCANEÁ Y ELEGÍ CANCIONES", "ESCANEA Y ELIGE CANCIONES")]
    [InlineData("SUMATE AL KARAOKE", "SÚMATE AL KARAOKE")]
    [InlineData("Si cerrás Kantaria, la cola se conserva.", "Si cierras Kantaria, la cola se conserva.")]
    [InlineData("Tenés que volver y seguí con calma.", "Tienes que volver y sigue con calma.")]
    [InlineData("Dejá la propina.", "Deja la propina.")]
    [InlineData("Hacé una pausa.", "Haz una pausa.")]
    [InlineData("Ahora sos responsable del estribillo.", "Ahora eres responsable del estribillo.")]
    [InlineData("Vos trajiste una alternativa.", "Tú trajiste una alternativa.")]
    [InlineData("El micrófono aceptó seguir con vos.", "El micrófono aceptó seguir contigo.")]
    [InlineData("La melodía confía en vos.", "La melodía confía en ti.")]
    [InlineData("Gracias a vos.", "Gracias a ti.")]
    [InlineData("Entre vos y la canción.", "Entre tú y la canción.")]
    [InlineData("Sos vos.", "Eres tú.")]
    public void Voseo_becomes_tuteo(string voseo, string expected) =>
        Assert.Equal(expected, Neutralizer.ToNeutral(voseo));

    [Theory]
    [InlineData("Todavía no hay jugadores.")]
    [InlineData("Ya están todos los jugadores del sistema.")]
    [InlineData("¿Está abierta? Será mañana.")]
    [InlineData("Vas en el puesto 2 y estés donde estés.")]
    [InlineData("")]
    public void Text_without_voseo_is_left_alone(string text) => Assert.Equal(text, Neutralizer.ToNeutral(text));

    [Fact]
    public void Pick_chooses_the_text_for_each_language()
    {
        Assert.Equal("Elegí", Localize.Pick("es-AR", "Elegí", "Pick"));
        Assert.Equal("Elige", Localize.Pick("es", "Elegí", "Pick"));
        Assert.Equal("Pick", Localize.Pick("en", "Elegí", "Pick"));
        Assert.Equal("Elige", Localize.Pick("fr", "Elegí", "Pick"));
    }

    // ---- Ajustes ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("es", true, "es-AR")]
    [InlineData("es", false, "es")]
    [InlineData("es", null, "es")]
    [InlineData("en", true, "en")]
    [InlineData("es-AR", false, "es-AR")]
    public void The_old_dialect_setting_is_folded_into_the_language(string language, bool? argentine, string expected)
    {
        var settings = new UserSettings { CommentLanguage = language, ArgentinePhrases = argentine }.Normalized();
        Assert.Equal(expected, settings.CommentLanguage);
        Assert.Null(settings.ArgentinePhrases);
    }

    [Fact]
    public void An_old_settings_file_is_migrated_and_saved_without_the_old_key()
    {
        var path = Path.Combine(Path.GetTempPath(), "kantor-loc-" + Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ \"CommentLanguage\": \"es\", \"ArgentinePhrases\": true }");

            var loaded = UserSettingsStore.Load(path);
            Assert.Equal("es-AR", loaded.CommentLanguage);

            Assert.True(UserSettingsStore.Save(path, loaded));
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("ArgentinePhrases", text);
            Assert.Equal("es-AR", UserSettingsStore.Load(path).CommentLanguage);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    // ---- Que no quede voseo en el español neutro --------------------------------------------------------------------

    private static readonly Regex Voseo = new(
        @"\b(" + string.Join("|", Neutralizer.KnownForms.Select(Regex.Escape)) + @"|vos)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static void AssertNoVoseo(string where, string text)
    {
        var found = Voseo.Match(text);
        Assert.False(found.Success, $"{where}: queda voseo (\"{found.Value}\") en \"{text}\"");
    }

    [Fact]
    public void The_neutral_comments_have_no_voseo_and_the_argentine_ones_keep_it()
    {
        var neutral = HumorBank.Load("es");
        var argentine = HumorBank.Load("es-AR");
        foreach (var id in neutral.AllIds)
        {
            AssertNoVoseo("comentario " + id, neutral.Text(id));
        }

        Assert.Contains(argentine.AllIds, id => Voseo.IsMatch(argentine.Text(id)));
    }

    [Fact]
    public void The_neutral_texts_of_the_options_and_players_screens_have_no_voseo()
    {
        foreach (var key in OptionsTexts.Keys)
        {
            AssertNoVoseo("opciones " + key, OptionsTexts.Get("es", key));
        }

        foreach (var key in PlayersTexts.Keys)
        {
            AssertNoVoseo("jugadores " + key, PlayersTexts.Get("es", key));
        }
    }

    [Fact]
    public void Every_screen_text_in_neutral_spanish_has_no_voseo()
    {
        var classes = new[] { typeof(MenuTexts), typeof(GuestTexts), typeof(QueueTexts), typeof(Kantor.Core.Library.LibraryTexts), typeof(ResultTexts) };
        var checkedTexts = 0;
        foreach (var method in classes.SelectMany(c => c.GetMethods(BindingFlags.Public | BindingFlags.Static))
                     .Where(m => m.ReturnType == typeof(string) && m.GetParameters().Length >= 1
                                 && m.GetParameters()[0].ParameterType == typeof(string)))
        {
            var args = method.GetParameters().Skip(1).Select(p => p.ParameterType == typeof(string) ? "x"
                : p.ParameterType == typeof(int) ? (object)2
                : p.ParameterType == typeof(bool) ? true
                : p.ParameterType == typeof(double) ? 1.5
                : throw new NotSupportedException(method.Name)).ToArray();
            var text = (string)method.Invoke(null, [.. new object[] { "es" }.Concat(args)])!;
            AssertNoVoseo($"{method.DeclaringType!.Name}.{method.Name}", text);
            checkedTexts++;
        }

        Assert.True(checkedTexts > 40, $"se revisaron solo {checkedTexts} textos");
        foreach (var rank in Enum.GetValues<ResultRank>())
        {
            AssertNoVoseo("sello " + rank, ResultRanks.Label(rank, "es"));
        }
    }

    [Fact]
    public void The_neutral_and_argentine_spanish_differ_where_there_is_voseo()
    {
        Assert.Equal("Elegí una canción", Localize.Spanish("es-AR", "Elegí una canción"));
        Assert.NotEqual(MenuTexts.FinishInfo("es-AR", true), MenuTexts.FinishInfo("es", true));
    }

    [Fact]
    public void The_guest_page_neutralizer_knows_every_voseo_form_the_page_uses()
    {
        var assembly = typeof(Neutralizer).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("guest.js", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
        var script = reader.ReadToEnd();
        var table = Regex.Match(script, @"var VOSEO = \{(.*?)\};", RegexOptions.Singleline).Groups[1].Value;
        var inTable = Regex.Matches(table, @"'([^']+)':").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // todo lo que el conversor de C# convertiría y que aparece en los textos de la página debe estar en la tabla de JS
        var texts = script[..script.IndexOf("var VOSEO", StringComparison.Ordinal)];
        var used = Regex.Matches(texts, @"[\p{L}]+").Select(m => m.Value)
            .Where(w => Neutralizer.KnownForms.Contains(w, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase);
        Assert.All(used, word => Assert.True(inTable.Contains(word), $"guest.js: falta \"{word}\" en VOSEO"));
    }
}
