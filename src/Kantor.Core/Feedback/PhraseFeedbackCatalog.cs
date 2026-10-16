// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
namespace Kantor.Core.Feedback;

/// <summary>
/// Mensajes de valoración por nivel y mensajes especiales. Cada uno tiene hasta tres palabras. Está separado de la
/// lógica a propósito: otro idioma es otra instancia de esta clase (<see cref="For"/>), sin tocar el motor.
/// </summary>
public sealed class PhraseFeedbackCatalog
{
    public required IReadOnlyDictionary<PhraseRating, IReadOnlyList<string>> ByRating { get; init; }

    /// <summary>Racha de frases buenas seguidas.</summary>
    public required IReadOnlyList<string> Streak { get; init; }

    /// <summary>Buena frase después de varias flojas.</summary>
    public required IReadOnlyList<string> Recovery { get; init; }

    /// <summary>Dos jugadores con frase perfecta a la vez.</summary>
    public required IReadOnlyList<string> Duel { get; init; }

    /// <summary>
    /// Catálogo del idioma de la interfaz ("es", "en", "en-US"…). Sin catálogo para ese idioma se usa el español,
    /// que es el idioma base de Kantor. Los catálogos son independientes: no se corresponden frase por frase.
    /// </summary>
    public static PhraseFeedbackCatalog For(string? language) =>
        language is not null && language.StartsWith("en", StringComparison.OrdinalIgnoreCase) &&
        (language.Length == 2 || language[2] is '-' or '_')
            ? English
            : Spanish;

    public static readonly PhraseFeedbackCatalog Spanish = new()
    {
        ByRating = new Dictionary<PhraseRating, IReadOnlyList<string>>
        {
            [PhraseRating.Perfect] =
            [
                "¡PERFECTO!", "¡IMPECABLE!", "¡MAGISTRAL!", "¡CLAVADÍSIMO!", "¡SOBERBIO!", "¡INSUPERABLE!",
                "¡DE LUJO!", "¡QUÉ PRECISIÓN!", "¡BRILLANTE!", "¡LEGENDARIO!", "¡INMACULADO!", "¡DE OTRO PLANETA!",
            ],
            [PhraseRating.Excellent] =
            [
                "¡BRUTAL!", "¡TREMENDO!", "¡ESPECTACULAR!", "¡QUÉ NIVEL!", "¡FORMIDABLE!", "¡EXCELSO!",
                "¡UNA LOCURA!", "¡FENOMENAL!", "¡DESCOMUNAL!", "¡QUÉ TALENTO!", "¡MONUMENTAL!", "¡IMPARABLE!",
            ],
            [PhraseRating.VeryGood] =
            [
                "¡GENIAL!", "¡GRANDIOSO!", "¡MUY BIEN!", "¡QUÉ VOZ!", "¡EXCELENTE!", "¡NOTABLE!",
                "¡BUENÍSIMO!", "¡ASÍ SE HACE!", "¡GRAN TRABAJO!", "¡QUÉ BIEN!", "¡ESTÁS VOLANDO!", "¡ALTO NIVEL!",
            ],
            [PhraseRating.Good] =
            [
                "¡BIEN AHÍ!", "¡VAMOS!", "¡NADA MAL!", "¡BUENA!", "¡MUY DIGNO!", "¡BIEN HECHO!",
                "¡SEGUÍ ASÍ!", "¡APROBADO!", "¡BUEN RITMO!", "¡SALE BIEN!", "¡ESO ES!", "¡DALE NOMÁS!",
            ],
            [PhraseRating.Acceptable] =
            [
                "¡ZAFÓ!", "¡PONELE!", "¡CASI CASI!", "¡PASABLE!", "¡AHÍ VAMOS!", "¡NO ESTUVO MAL!",
                "¡SE DEFIENDE!", "¡MÁS O MENOS!", "¡VAMOS TODAVÍA!", "¡ALGO SALIÓ!", "¡CON ACTITUD!", "¡SEGUIMOS!",
            ],
            [PhraseRating.Poor] =
            [
                "¡CASI!", "¡UPS!", "¡DERRAPE!", "¡FALTÓ POQUITO!", "¡A REMARLA!", "¡AY, AY!",
                "¡OTRA VEZ!", "¡SE ESCAPÓ!", "¡QUÉ PASÓ!", "¡COMPLICADO!", "¡HAY QUE AFILAR!", "¡SE INTENTÓ!",
            ],
            [PhraseRating.Bad] =
            [
                "¡AY, NO!", "¡SOCORRO!", "¡AUXILIO!", "¡QUÉ DESASTRE!", "¡EN PROBLEMAS!", "¡FUERA DE ÓRBITA!",
                "¡SIN GPS!", "¡DESCARRILÓ!", "¡QUÉ MOMENTO!", "¡NO ERA!", "¡SE FUE!", "¡TURBULENCIA!",
            ],
            [PhraseRating.Disaster] =
            [
                "¡MAMITA!", "¡TERREMOTO!", "¡CATÁSTROFE!", "¡OTRA TONALIDAD!", "¡LLAMEN AL MÉDICO!",
                "¡MICRÓFONO EN SHOCK!", "¡SIN SEÑAL!", "¡APAGÓN MUSICAL!", "¡QUÉ FUE ESO!", "¡ALARMA GENERAL!",
                "¡NOTAS DESAPARECIDAS!", "¡SILENCIO INCÓMODO!",
            ],
        },
        Streak = ["¡EN RACHA!", "¡IMPARABLE!", "¡NO AFLOJES!", "¡ON FIRE!"],
        Recovery = ["¡REMONTADA!", "¡AHORA SÍ!", "¡VOLVIÓ!", "¡DESPERTÓ!"],
        Duel = ["¡DOBLE PERFECTO!", "¡DUELO ÉPICO!", "¡LOS DOS!", "¡SIN PIEDAD!"],
    };

    public static readonly PhraseFeedbackCatalog English = new()
    {
        ByRating = new Dictionary<PhraseRating, IReadOnlyList<string>>
        {
            [PhraseRating.Perfect] =
            [
                "PERFECT!", "FLAWLESS!", "NAILED IT!", "LEGENDARY!", "GODLIKE!", "SPOT ON!",
                "MASTERFUL!", "UNTOUCHABLE!", "ABSOLUTE PERFECTION!", "BULLSEYE!", "PHENOMENAL!", "ICONIC!",
            ],
            [PhraseRating.Excellent] =
            [
                "AMAZING!", "EPIC!", "BRILLIANT!", "INCREDIBLE!", "UNREAL!", "OUTSTANDING!",
                "SPECTACULAR!", "INSANE!", "STUNNING!", "SUPERB!", "WHAT A VOICE!", "YOU'RE FLYING!",
            ],
            [PhraseRating.VeryGood] =
            [
                "AWESOME!", "GREAT JOB!", "NICE ONE!", "YOU ROCK!", "FANTASTIC!", "SWEET!",
                "LOVELY!", "IMPRESSIVE!", "WELL PLAYED!", "LOOK AT YOU!", "SOLID GOLD!", "KEEP ROCKING!",
            ],
            [PhraseRating.Good] =
            [
                "GOOD JOB!", "NOT BAD!", "WELL DONE!", "NICE!", "KEEP GOING!", "SOLID!",
                "GOOD STUFF!", "THAT WORKS!", "PRETTY GOOD!", "WAY TO GO!", "YOU GOT THIS!", "RESPECTABLE!",
            ],
            [PhraseRating.Acceptable] =
            [
                "CLOSE ENOUGH!", "SORT OF!", "ALMOST!", "FAIR ENOUGH!", "IT COUNTS!", "MEH!",
                "GOOD ENOUGH!", "COULD BE WORSE!", "SOMEWHAT!", "WE'LL TAKE IT!", "TECHNICALLY OK!", "I'LL ALLOW IT!",
            ],
            [PhraseRating.Poor] =
            [
                "OOPS!", "ROUGH ONE!", "SO CLOSE!", "YIKES!", "KEEP TRYING!", "WHOOPS!",
                "NOT QUITE!", "ALMOST THERE!", "BIT SHAKY!", "THAT SLIPPED!", "NEEDS WORK!", "NICE TRY!",
            ],
            [PhraseRating.Bad] =
            [
                "OH NO!", "MAYDAY!", "OFF TRACK!", "BIG OOF!", "SEND HELP!", "TOTAL CHAOS!",
                "YIKES AGAIN!", "WRONG TURN!", "NOT EVEN CLOSE!", "LOST CONTROL!", "ROUGH LANDING!", "WHAT HAPPENED?!",
            ],
            [PhraseRating.Disaster] =
            [
                "ABORT!", "WRONG PLANET!", "MIC DOWN!", "NO SIGNAL!", "WHAT WAS THAT?!", "RIP EARS!",
                "SYSTEM FAILURE!", "TOTAL MELTDOWN!", "WRONG DIMENSION!", "CALL NASA!", "SOUND NOT FOUND!",
                "MUSIC.EXE CRASHED!",
            ],
        },
        Streak =
        [
            "ON FIRE!", "UNSTOPPABLE!", "HOT STREAK!", "KEEP IT UP!", "CAN'T STOP!", "FULL POWER!",
            "CRUSHING IT!", "STILL ROLLING!",
        ],
        Recovery =
        [
            "COMEBACK!", "BACK ON TRACK!", "THERE YOU GO!", "BACK IN BUSINESS!", "MUCH BETTER!",
            "THAT'S BETTER!", "REDEMPTION!", "YOU'RE BACK!",
        ],
        Duel =
        [
            "DOUBLE PERFECT!", "BOTH NAILED IT!", "PERFECT DUO!", "DOUBLE TROUBLE!", "WHAT A DUO!",
            "TWO LEGENDS!", "MATCHED PERFECTION!", "DOUBLE MAGIC!",
        ],
    };
}
