// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kantor.Core.Characters;

/// <summary>Convención de ubicación de los recursos de un personaje (relativa a la carpeta de recursos de la app).</summary>
public static class CharacterResources
{
    public const string Root = "Assets/Characters";
    public const string PortraitFile = "portrait.png";
    public const string FullBodyFile = "fullbody.png";

    public static string PortraitPath(string id) => $"{Root}/{id}/{PortraitFile}";

    public static string FullBodyPath(string id) => $"{Root}/{id}/{FullBodyFile}";
}

/// <summary>
/// Un personaje de Kantor: identidad estable y sus dos recursos gráficos, que comparten identidad.
/// <list type="bullet">
/// <item><b>Portrait</b>: retrato de cabeza y hombros (selección de jugadores, durante la canción); no es el
/// cuerpo entero reducido.</item>
/// <item><b>FullBody</b>: cuerpo entero con transparencia, para la ceremonia de resultados.</item>
/// </list>
/// Si una ruta es <c>null</c> se usa la convención <c>Assets/Characters/{id}/portrait.png</c> y <c>fullbody.png</c>.
/// Un recurso ausente no es un error: la interfaz cae en su placeholder.
/// </summary>
/// <param name="Id">Identificador estable (minúsculas, dígitos, guion y guion bajo); no cambia entre versiones.</param>
/// <param name="Name">Nombre visible del personaje (no es el nombre del jugador).</param>
/// <param name="PortraitPath">Ruta del retrato, o <c>null</c> para la convención.</param>
/// <param name="FullBodyPath">Ruta del cuerpo entero, o <c>null</c> para la convención.</param>
/// <param name="Tuning">Ajustes opcionales del cuerpo entero.</param>
/// <param name="IsUser">Cargado por el usuario: sus rutas son archivos absolutos y se puede borrar.</param>
public sealed record CharacterDefinition(
    string Id,
    string Name,
    string? PortraitPath = null,
    string? FullBodyPath = null,
    CharacterTuning? Tuning = null,
    bool IsUser = false)
{
    public string ResolvedPortraitPath => PortraitPath ?? CharacterResources.PortraitPath(Id);

    public string ResolvedFullBodyPath => FullBodyPath ?? CharacterResources.FullBodyPath(Id);

    public static bool IsValidId(string? id) =>
        !string.IsNullOrEmpty(id) && id.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_');
}
