// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
namespace Kantor.Core.Settings;

/// <summary>
/// Dónde guarda Kantor sus datos: ajustes, índice de la biblioteca e historial. Por omisión la carpeta de datos
/// de la aplicación del usuario (<c>%APPDATA%\Kantor</c>); la variable de entorno <c>KANTOR_DATA_DIR</c> la
/// reemplaza (pruebas, o una instalación portátil).
/// </summary>
public static class AppPaths
{
    public const string DataDirVariable = "KANTOR_DATA_DIR";

    public static string DataDirectory()
    {
        var overridden = Environment.GetEnvironmentVariable(DataDirVariable);
        return !string.IsNullOrWhiteSpace(overridden)
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kantor");
    }
}
