// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Humor;

namespace Kantor.Core.Menu;

/// <summary>Textos fijos de la cola de canciones (es/en).</summary>
public static class QueueTexts
{
    public static string Title(string language) => Pick(language, "COLA", "QUEUE");
    public static string Back(string language) => Pick(language, "VOLVER", "BACK");
    public static string Empty(string language) =>
        Pick(language, "La cola está vacía. Agregá canciones desde la lista (botón + COLA o F5).",
            "The queue is empty. Add songs from the list (+ QUEUE button or F5).");
    public static string Open(string language, int count) =>
        Pick(language, count == 0 ? "COLA" : $"COLA ({count})", count == 0 ? "QUEUE" : $"QUEUE ({count})");
    public static string Add(string language) => Pick(language, "+ COLA", "+ QUEUE");
    public static string Added(string language, string title, int position) =>
        Pick(language, $"“{title}” agregada a la cola (puesto {position}).",
            $"“{title}” added to the queue (position {position}).");
    public static string Full(string language) =>
        Pick(language, "La cola está llena.", "The queue is full.");
    public static string SingNow(string language) => Pick(language, "CANTAR YA", "SING NOW");
    public static string Up(string language) => Pick(language, "SUBIR", "UP");
    public static string Down(string language) => Pick(language, "BAJAR", "DOWN");
    public static string Remove(string language) => Pick(language, "QUITAR", "REMOVE");
    public static string Next(string language, string title) =>
        Pick(language, $"SIGUIENTE: {title}", $"NEXT: {title}");
    public static string Unavailable(string language, string title) =>
        Pick(language, $"“{title}” ya no está en la biblioteca.", $"“{title}” is no longer in the library.");
    public static string AutoAdvance(string language, int seconds) =>
        Pick(language, $"SIGUE SOLA EN {seconds} s · CUALQUIER TECLA CANCELA",
            $"STARTS BY ITSELF IN {seconds} s · ANY KEY CANCELS");
    public static string Hint(string language) => Pick(language,
        "↑ ↓ ← → ELEGIR  ·  ENTER ACEPTAR  ·  ESC VOLVER",
        "↑ ↓ ← → SELECT  ·  ENTER CONFIRM  ·  ESC BACK");
    public static string Scope(string language, bool session) => session
        ? Pick(language, "La cola es de la partida abierta y se conserva si cerrás Kantaria.",
            "The queue belongs to the open session and survives closing Kantaria.")
        : Pick(language, "Sin partida abierta: la cola vale solo mientras Kantaria está abierto.",
            "No open session: the queue only lasts while Kantaria stays open.");

    private static string Pick(string language, string es, string en) =>
        HumorBank.NormalizeLanguage(language) == "en" ? en : es;
}
