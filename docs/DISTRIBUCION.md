# Distribución de Kantor

## Hoy: carpeta autocontenida + zip (sin instalador)

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1        # tests + publicación + zip
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -SkipTests
```

Salida en `dist\` (ignorado por git):

- `Kantor-<versión>-win-x64\` — `Kantor.App.exe` + runtime .NET 10 incluido (no hace falta instalar .NET).
- `Kantor-<versión>-win-x64.zip` — lo mismo comprimido (~113 MB; la carpeta pesa ~262 MB).

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
