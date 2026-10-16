# Distribución de Kantor

## Hoy: carpeta autocontenida + zip (sin instalador)

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1        # tests + publicación + zip
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -SkipTests
```

Salida en `dist\` (ignorado por git):

- `Kantor-<versión>-win-x64\` — `Kantor.App.exe` + runtime .NET 10 incluido (no hace falta instalar .NET).
- `Kantor-<versión>-win-x64.zip` — lo mismo comprimido (~113 MB; la carpeta pesa ~262 MB).

### Instalador (Inno Setup)

```powershell
winget install JRSoftware.InnoSetup                                   # una sola vez
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Installer
```

Genera además `dist\Kantor-Setup-<versión>.exe` (~84 MB), a partir de `installer\kantor.iss`:

- Instala por usuario, **sin administrador**, en `%LOCALAPPDATA%\Programs\Kantor`; crea el acceso del menú Inicio
  (y el del escritorio, opcional) y se desinstala desde "Aplicaciones instaladas".
- Para actualizar se ejecuta el instalador nuevo encima: reemplaza los archivos (borra antes `libvlc` y las `.dll`
  viejas) y conserva los datos de `%APPDATA%\Kantor`. Si Kantor está abierto, lo cierra.
- Instalación silenciosa: `Kantor-Setup-<versión>.exe /SILENT`.
- Sin firmar: SmartScreen puede avisar la primera vez.

La versión sale de `<Version>` en `src/Kantor.App/Kantor.App.csproj` (hoy 0.1.0). Subirla antes de cada entrega.

Qué incluye / qué se saca a propósito:

- Solo **x64**. Se descartan `libvlc\win-x86`, las `.lib` de enlace, los `.pdb` y las carpetas de idiomas de WPF.
- LibVLC va como carpeta `libvlc\win-x64` (LGPL, reemplazable). Los avisos de terceros están en
  `THIRD-PARTY-NOTICES.md`, que viaja en la distribución junto con `LICENSE` y `README.md`.
- Datos del usuario en `%APPDATA%\Kantor` (ajustes, historial, personajes propios): **no** se tocan al actualizar
  ni al borrar la carpeta de la aplicación. `KANTOR_DATA_DIR` permite una instalación portátil.

Limitaciones actuales:

- **Sin firmar**: Windows SmartScreen avisa ("Windows protegió su PC") la primera vez; hay que "Más información →
  Ejecutar de todas formas". Firmar con un certificado de código lo evita.
- **Sin instalador**: se descomprime y se ejecuta; no crea accesos directos ni se registra en "Aplicaciones".
- Requiere Windows 10/11 x64. La verificación en una PC limpia (sin SDK ni runtime) sigue pendiente.

## Después: Microsoft Store (MSIX)

Lo que hay que resolver (nada de esto está hecho):

1. **Cuenta de desarrollador** en Partner Center (pago único para particulares) y **reservar el nombre** "Kantor".
   De ahí salen `Identity Name`, `Publisher` y `PublisherDisplayName` del manifiesto.
2. **Paquete MSIX**: `Package.appxmanifest` con tipo de aplicación de escritorio (`runFullTrust`) y capacidad de
   micrófono. La Store firma el paquete al publicarlo, así que no hace falta comprar un certificado para eso.
   Para probar el MSIX localmente sí hace falta un certificado de prueba.
3. **Imágenes de la Store**: logos cuadrados (44, 150, 310, 50 px y variantes de escala), capturas (mínimo 1) y, si
   se quiere, ícono grande. Hoy solo hay `kantor-logo.png` y `kantor.ico`.
4. **Política de privacidad** (URL pública): la app usa el micrófono; hay que declarar que no se envía nada.
5. **Clasificación de edad** (cuestionario) y descripción en es/en.
6. **Biblioteca de canciones**: el usuario las elige desde cualquier carpeta; con `runFullTrust` se pueden leer
   igual. Revisar que la virtualización de `%APPDATA%` del paquete no rompa "abrir la carpeta de datos" ni la
   copia de seguridad del historial.
7. **Licencia**: Kantor es GPL-3.0-or-later. Los términos de la Store añaden condiciones al uso del paquete que
   pueden chocar con la GPL (restricciones de redistribución). Conviene decidirlo antes de publicar: por ejemplo,
   ofrecer el código fuente y el zip libre en paralelo, y dejar claro en la ficha que la Store es una vía de
   conveniencia. Es una decisión del titular del copyright.
8. **Certificación**: la Store prueba la app en una PC limpia; ahí se verá cualquier dependencia faltante
   (por ejemplo, que no haya micrófono o que no exista la carpeta de canciones: la app ya debe arrancar igual).

Conviene hacer primero la prueba en PC limpia con el zip actual: es la misma verificación que pedirá la Store.

## Ediciones: local y Store

`scripts\publish.ps1` arma la edición **Local** por omisión (instalador, zip, todos los avatares). Con
`-Edition Store` arma `dist\Kantor-<versión>-store-win-x64\` (y su zip) **sin** los avatares de
`src\Kantor.Core\Characters\store-excluded.txt` (por licencias): ni se muestran ni se embeben. La Store es siempre
un subconjunto de la local. No hay instalador Inno Setup para la Store (va como MSIX). El script limpia `obj`
antes de publicar porque los recursos de WPF no se regeneran solos al cambiar de edición; compilar a mano con
`-p:Edition=Store` exige lo mismo (borrar `src\Kantor.App\obj` y `src\Kantor.Core\obj` al volver a la local).

## Paquete MSIX de la Store

`scripts\publish.ps1 -Edition Store -Msix` arma `dist\Kantor-<versión>-store.msix` (sin firmar: la Store lo firma al
publicarlo) con `makeappx` del Windows SDK. Usa `installer\store\AppxManifest.xml`, con la identidad reservada en
Partner Center (Name `PabloPramparo.Kantor`, Publisher `CN=A1B8C5FA-571D-4C85-8594-85319C35543A`, PublisherDisplayName
`Pablo Pramparo`, Id. de Store `9P8ZKZBJ4RHB`, PFN `PabloPramparo.Kantor_zz18sectgfr1r`), `runFullTrust` y micrófono, y los
logos de `installer\store\Assets` (los genera `installer\store\make-assets.ps1` desde el logo; cuadrados con el logo
completo, ancho 310×150, a escala 100/200/400 y tamaños fijos). Versión del paquete: la de `Kantor.App.csproj` + `.0`.
- Comprobado: `makeappx` valida el manifiesto y arma el paquete (132 MB). **Sin comprobar**: instalarlo (hace falta firmarlo
  con un certificado de prueba cuyo asunto sea el Publisher), la certificación de la Store (WACK) y el comportamiento de la
  app empaquetada (virtualización de `%APPDATA%`, micrófono, el servidor de invitados y el Firewall).
- Pendiente para publicar: política de privacidad (URL), clasificación de edad, descripciones, capturas y definir la versión (la lista de avatares ya está: 25, 29, 30 y 31 excluidos).

