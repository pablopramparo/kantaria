// SPDX-License-Identifier: GPL-3.0-or-later
using Kantor.Core.Menu;
using Xunit;

namespace Kantor.Tests;

public class MenuTextsTests
{
    [Fact]
    public void SpanishAndEnglishLabels()
    {
        Assert.Equal("CANTAR", MenuTexts.Sing("es"));
        Assert.Equal("SING", MenuTexts.Sing("en"));
        Assert.Equal("SALIR", MenuTexts.Exit("es"));
        Assert.Equal("EXIT", MenuTexts.Exit("en"));
        Assert.Equal("OPCIONES", MenuTexts.Options("es"));
        Assert.Equal("OPTIONS", MenuTexts.Options("en"));
    }

    [Fact]
    public void EveryOptionsKeyHasBothLanguages()
    {
        foreach (var key in OptionsTexts.Keys)
        {
            var es = OptionsTexts.Get("es", key);
            var en = OptionsTexts.Get("en", key);
            Assert.False(string.IsNullOrWhiteSpace(es), key);
            Assert.False(string.IsNullOrWhiteSpace(en), key);
            Assert.NotEqual(key, es);
        }

        foreach (var key in PlayersTexts.Keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(PlayersTexts.Get("es", key)), key);
            Assert.False(string.IsNullOrWhiteSpace(PlayersTexts.Get("en", key)), key);
        }

        Assert.Equal("JUGADORES", MenuTexts.Players("es"));
        Assert.Equal("MIC 2", PlayersTexts.Format("en", "s.slot", 2));
        Assert.Equal("nope", OptionsTexts.Get("es", "nope"));
        Assert.Equal("Micrófono 3", OptionsTexts.Microphone("es", 3));
        Assert.Equal("Microphone 3", OptionsTexts.Microphone("en", 3));
    }

    [Fact]
    public void UnknownLanguageFallsBackToSpanish() => Assert.Equal("CANTAR", MenuTexts.Sing("fr"));

    [Fact]
    public void FinishPromptTextsExistInBothLanguages()
    {
        foreach (var language in new[] { "es", "en" })
        {
            foreach (var text in new[]
                     {
                         MenuTexts.FinishTitle(language), MenuTexts.FinishInfo(language, true), MenuTexts.FinishInfo(language, false),
                         MenuTexts.FinishSave(language), MenuTexts.FinishDiscard(language), MenuTexts.FinishView(language),
                         MenuTexts.FinishContinue(language), MenuTexts.NotSaved(language),
                     })
            {
                Assert.False(string.IsNullOrWhiteSpace(text));
            }
        }

        Assert.NotEqual(MenuTexts.FinishSave("es"), MenuTexts.FinishSave("en"));
        Assert.NotEqual(MenuTexts.FinishInfo("es", true), MenuTexts.FinishInfo("es", false));
    }
}
