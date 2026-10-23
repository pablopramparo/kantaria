// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Humor;

namespace Kantor.Core.Menu;

/// <summary>Textos fijos del acceso de invitados por LAN (es/en): pantalla del QR, tarjetas y opciones.</summary>
public static class GuestTexts
{
    public static string Title(string l) => Pick(l, "SUMATE AL KARAOKE", "JOIN THE PARTY");
    public static string Back(string l) => Pick(l, "VOLVER", "BACK");
    public static string Scan(string l) =>
        Pick(l, "Escaneá el QR con la cámara del celular", "Scan the QR with your phone's camera");
    public static string SameWifi(string l) =>
        Pick(l, "El celular tiene que estar en la misma red Wi-Fi que esta PC.",
            "Your phone must be on the same Wi-Fi network as this PC.");
    public static string What(string l) =>
        Pick(l, "Desde el celular se busca una canción, se elige quién la canta y se suma a la cola. Nada más.",
            "From the phone you find a song, pick who sings it and add it to the queue. Nothing else.");
    public static string Active(string l) => Pick(l, "ACCESO ACTIVO", "ACCESS ON");
    public static string Inactive(string l) => Pick(l, "ACCESO APAGADO", "ACCESS OFF");
    public static string TurnOn(string l) => Pick(l, "ACTIVAR", "TURN ON");
    public static string TurnOff(string l) => Pick(l, "APAGAR", "TURN OFF");
    public static string Address(string l, string url) => Pick(l, $"Dirección: {url}", $"Address: {url}");
    public static string OffNote(string l) =>
        Pick(l, "Está apagado: nadie puede entrar hasta que lo actives. Se apaga solo al cerrar Kantaria.",
            "It is off: nobody can join until you turn it on. It switches off when Kantaria closes.");
    public static string OnNote(string l) =>
        Pick(l, "El QR incluye la clave de acceso: quien lo escanee puede sumar canciones hasta que apagues el acceso.",
            "The QR carries the access key: anyone who scans it can add songs until you turn access off.");
    public static string NoNetwork(string l) =>
        Pick(l, "No se encontró una red local. Conectá la PC al Wi-Fi o al cable del router.",
            "No local network found. Connect the PC to Wi-Fi or to your router's cable.");
    public static string Failed(string l, string reason) =>
        Pick(l, $"No se pudo abrir el acceso: {reason}", $"Could not open access: {reason}");
    public static string Hint(string l) =>
        Pick(l, "ENTER ACTIVAR / APAGAR  ·  ESC VOLVER", "ENTER TURN ON / OFF  ·  ESC BACK");

    /// <summary>Tarjeta del menú y de la lista.</summary>
    public static string CardTitle(string l) => Pick(l, "INVITADOS", "GUESTS");
    public static string CardScan(string l) => Pick(l, "ESCANEÁ Y ELEGÍ CANCIONES", "SCAN AND PICK SONGS");
    public static string CardOn(string l) => Pick(l, "QR PARA INVITADOS", "GUEST QR");
    public static string CardEnlarge(string l) => Pick(l, "Tocá para agrandar", "Click to enlarge");

    /// <summary>Opciones › Juego.</summary>
    public static string OptionLabel(string l) => Pick(l, "Red para invitados", "Guest network");
    public static string OptionAuto(string l) => Pick(l, "Automática", "Automatic");
    public static string OptionHelp(string l) =>
        Pick(l, "El acceso de invitados escucha solo en esa red local. Si cambiás de red con el acceso activo, se reabre en la nueva.",
            "Guest access listens only on that local network. Changing it while access is on reopens it on the new one.");

    private static string Pick(string language, string es, string en) =>
        HumorBank.NormalizeLanguage(language) == "en" ? en : es;
}
