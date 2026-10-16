// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Windows.Media;
using Kantor.Core.Characters;
using Kantor.Core.Results;

namespace Kantor.App.Results;

/// <summary>Resultado de un jugador listo para mostrarse: el comentario ya viene resuelto al idioma activo.</summary>
public sealed record PlayerResult(
    int Number,
    int Score,
    ResultRank Rank,
    string Comment,
    Color Accent,
    string? Name = null,
    ImageSource? Avatar = null,
    bool IsWinner = false,
    bool IsTie = false,
    CharacterTuning? Tuning = null);

/// <summary>Colores del sello de cada rango: más intensos cuanto mejor el puntaje.</summary>
public static class RankPalette
{
    public readonly record struct Style(Color Fill, Color Border, Color Text);

    public static Style For(ResultRank rank) => rank switch
    {
        ResultRank.SystemSurvived => new(C(0x1A, 0x1F, 0x3D), C(0x9A, 0xA3, 0xC7), C(0xC9, 0xD0, 0xF0)),
        ResultRank.NiceTry => new(C(0x1B, 0x21, 0x50), C(0x7C, 0x8C, 0xFF), C(0xDD, 0xE2, 0xFF)),
        ResultRank.Acceptable => new(C(0x0E, 0x2F, 0x3A), C(0x3F, 0xD6, 0xC1), C(0xCF, 0xFF, 0xF6)),
        ResultRank.NiceWork => new(C(0x0F, 0x2F, 0x1A), C(0x5B, 0xE5, 0x6B), C(0xD6, 0xFF, 0xD9)),
        ResultRank.Impressive => new(C(0x0B, 0x2A, 0x4A), C(0x19, 0xD3, 0xFF), C(0xE3, 0xF8, 0xFF)),
        ResultRank.Superstar => new(C(0x3A, 0x0F, 0x40), C(0xFF, 0x3D, 0xB8), C(0xFF, 0xE0, 0xF4)),
        ResultRank.Legendary => new(C(0x4A, 0x2A, 0x05), C(0xFF, 0xC9, 0x33), C(0xFF, 0xF1, 0xB8)),
        _ => new(C(0xFF, 0xC9, 0x33), C(0xFF, 0xF6, 0xC8), C(0x2A, 0x15, 0x00)),
    };

    private static Color C(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
}

/// <summary>Colores identificatorios de jugador (1 a 4), compatibles con la paleta cian/magenta/oro.</summary>
public static class PlayerColors
{
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x19, 0xD3, 0xFF), // J1 cian
        Color.FromRgb(0xFF, 0x3D, 0xB8), // J2 magenta
        Color.FromRgb(0x5B, 0xE5, 0x6B), // J3 verde
        Color.FromRgb(0xA0, 0x6B, 0xFF), // J4 violeta
    ];

    public static Color For(int playerNumber) => Palette[Math.Clamp(playerNumber, 1, Palette.Length) - 1];
}
