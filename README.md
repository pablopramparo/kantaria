<p align="center">
  <img src="src/Kantor.App/Assets/Branding/kantor-logo.png" alt="Kantor" width="520">
</p>

<p align="center">
  <b>Karaoke local para Windows, compatible con canciones UltraStar.</b><br>
  Sin cuenta, sin nube, sin internet. Hasta cuatro micrófonos a la vez.
</p>

<p align="center">
  <img alt="Windows 10/11 x64" src="https://img.shields.io/badge/Windows-10%20%2F%2011%20x64-0078D6?logo=windows&logoColor=white">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white">
  <img alt="Licencia GPL-3.0-or-later" src="https://img.shields.io/badge/licencia-GPL--3.0--or--later-blue">
  <img alt="Estado: en desarrollo" src="https://img.shields.io/badge/estado-en%20desarrollo-orange">
</p>

---

## Qué es

Kantor es un karaoke de escritorio hecho en C# / .NET 10 / WPF para cantar en casa con amigos. Lee las
canciones en formato **UltraStar** (`.txt` + audio y, si existe, video), puntúa lo que cantás en tiempo real y
guarda el historial de cada jugador. Todo funciona en tu PC: no necesita cuenta ni conexión.

Tiene una interfaz propia de estilo arcade de 16 bits, pensada para verse bien en una pantalla grande y manejarse
con teclado, mouse o el control remoto opcional.

## Características

- **Canciones UltraStar** desde las carpetas que elijas. Nunca modifica tus archivos.
- **Puntuación por beat**: notas normales, doradas, rap y freestyle, con tres dificultades y bono de línea.
  Detección de tono con YIN.
- **Hasta 4 micrófonos simultáneos**, cada uno con su jugador, su carril y su señal de entrada en pantalla.
- **Duetos** con una voz por jugador.
- **Mensajes por frase** mientras cantás ("¡PERFECTO!", "¡UPS!"…), en español e inglés.
- **Jugadores, partidas e historial** guardados localmente (SQLite), con rankings y comentarios en los resultados.
- **Avatares**: elegí uno de la galería o cargá los tuyos, con recorte de la cara.
- **Cola de canciones** y vista previa al navegar la biblioteca.
- **Video de fondo** si la canción lo trae.
- **Audio**: motor de Windows (WASAPI) o ASIO, con calibración del retardo de voz.
- **Control remoto opcional** por pipe local, apagado por omisión.

## Requisitos

- Windows 10 u 11, 64 bits.
- Uno o más micrófonos para cantar (se puede explorar la biblioteca y mirar sin ellos).
- Tus propias canciones en formato UltraStar. Kantor **no incluye canciones**.

## Descargar

Los paquetes están en [Releases](../../releases) (carpeta `.zip` autocontenida: no hace falta instalar .NET).
Descomprimí y ejecutá `Kantor.App.exe`.

> El ejecutable todavía no está firmado: Windows SmartScreen puede mostrar un aviso la primera vez.
> Elegí "Más información" → "Ejecutar de todas formas".

## Compilar desde el código

Necesitás el [SDK de .NET 10](https://dotnet.microsoft.com/download).

```powershell
dotnet build Kantor.sln
dotnet test  Kantor.sln
dotnet run --project src/Kantor.App
```

Para armar el paquete distribuible (carpeta + zip en `dist\`):

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
```

Los PNG de los avatares de fábrica no están en el repositorio: sin ellos la aplicación funciona y muestra un
avatar genérico. Más detalle en [docs/DISTRIBUCION.md](docs/DISTRIBUCION.md).

## Documentación

| Documento | Contenido |
|---|---|
| [docs/ESTADO.md](docs/ESTADO.md) | Estado del proyecto, decisiones, mediciones, pendientes y trampas |
| [docs/PUNTUACION_WORLDPARTY.md](docs/PUNTUACION_WORLDPARTY.md) | Cómo puntúa WorldParty y qué sigue Kantor |
| [docs/DISTRIBUCION.md](docs/DISTRIBUCION.md) | Cómo se arma el paquete y el camino a la Microsoft Store |
| [docs/AVYN_CONTRATO.md](docs/AVYN_CONTRATO.md) | Protocolo del control remoto Avyn |

## Licencia y marca

Copyright (C) 2026 Pablo Pramparo

El **código** de Kantor es software libre: podés redistribuirlo y/o modificarlo bajo los términos de la
Licencia Pública General de GNU, versión 3 o (a tu elección) cualquier versión posterior. Se distribuye sin
ninguna garantía. Ver [LICENSE](LICENSE).

El **nombre Kantor, su logo y los personajes** no están bajo la GPL: son del autor y no tienen licencia de
reutilización. Si publicás un fork, usá otro nombre y reemplazá esos recursos. Detalle en
[src/Kantor.App/Assets/LICENSE-ASSETS.md](src/Kantor.App/Assets/LICENSE-ASSETS.md).

Las canciones y medios que uses con Kantor tienen sus propias licencias y no se incluyen.

### Componentes de terceros

La lista completa, con sus licencias, está en [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). En resumen:

- **Jersey 15** (tipografía): SIL Open Font License 1.1.
- **LibVLC / LibVLCSharp** (video de fondo): LGPL-2.1-or-later, como bibliotecas dinámicas.
- **NAudio**, **Microsoft.Data.Sqlite**: MIT. **SQLitePCLRaw**: Apache-2.0.

Kantor es un proyecto independiente, inspirado en UltraStar Deluxe y UltraStar WorldParty, con los que no tiene
relación oficial.
