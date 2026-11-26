# RokeBox

RokeBox es un fork de [PhoneBox](https://github.com/ersinkoc/phonebox) (licencia MIT, © 2026 Ersin KOÇ; el aviso
de copyright de `LICENSE` debe conservarse). Convierte un teléfono Android en un panel de escritorio para el PC:
una app Android (Kotlin + WebView con widgets en JavaScript) y un agente de escritorio (.NET 10).

Esta rama (`rokebox`) cambia solo la **identidad** de la app, para que RokeBox y PhoneBox se puedan instalar y usar a la
vez y para que traer mejoras del proyecto original siga siendo barato. El código interno (espacios de nombres
`PhoneBox.Agent`, paquete Kotlin `net.oxog.phonebox.deskpanel`, API `window.phonebox` de los widgets HTML) no cambia a
propósito.

## Qué cambia respecto a PhoneBox

| | PhoneBox | RokeBox |
|---|---|---|
| Id de la app Android | `net.oxog.phonebox.deskpanel` | `com.syroke.rokebox` |
| Nombre e icono | PhoneBox, barras de colores | RokeBox, "R" rosa sobre violeta |
| Puerto de ajustes del PC | 8765 | 8775 |
| Puerto del túnel USB en el teléfono | 8765 | 8775 |
| Puerto Wi-Fi | 8766 | 8776 |
| Configuración y registros | `%APPDATA%\PhoneBox`, `%LOCALAPPDATA%\PhoneBox` | `%APPDATA%\RokeBox`, `%LOCALAPPDATA%\RokeBox` |
| Ejecutable / instancia única | `PhoneBoxAgent.exe`, `Local\PhoneBox.Agent` | `RokeBoxAgent.exe`, `Local\RokeBox.Agent` |
| Actualizaciones automáticas | releases de `ersinkoc/phonebox` | desactivadas; sin canal hasta que `updates.repo` apunte a los releases de RokeBox |
| Enlace de emparejamiento Wi-Fi | `https://ersinkoc.github.io/phonebox/pair/#…` | `rokebox://pair?…` |
| Variables de firma del APK | `PHONEBOX_STORE_FILE`, … | `ROKEBOX_STORE_FILE`, … |

Limitaciones conocidas de esta primera fase:

- **iOS y macOS** no están adaptados (identificadores de bundle, `IosUsbBridge` usa el puerto 8765 del teléfono).
- **Emparejamiento Wi-Fi por QR:** el QR contiene `rokebox://pair?…`. Algunos lectores de QR no abren enlaces
  con esquema propio. Para que funcione como en PhoneBox hay que publicar una página de emparejamiento propia
  (copia de `tools/pages/pair.html`) en GitHub Pages y volver a añadir el filtro `https` en el manifiesto. Por USB
  no hace falta nada de esto.
- Algunos textos que el agente genera en C# (mensajes de error de Discord, cabeceras User-Agent) siguen diciendo
  PhoneBox.

## Convivencia con PhoneBox

Las dos apps se instalan una al lado de la otra en el teléfono, y los dos agentes pueden ejecutarse a la vez en el PC
porque usan puertos, carpetas e instancia única distintos. Cada agente solo gestiona su propia regla `adb reverse`.
Si el agente de PhoneBox tiene activada la opción de abrir el panel al conectar el cable, puede traer PhoneBox al
frente: desactívala o cierra PhoneBox mientras pruebas RokeBox.

**Hooks de aprobación (Claude Code, Codex, Cursor, WorkBuddy):** cada agente reconoce como suyo cualquier hook que
llame a `127.0.0.1:<puerto>/api/hooks/…`, sea cual sea el puerto. Instalarlos desde RokeBox sustituye los de PhoneBox
(y al revés). Instálalos solo desde uno de los dos agentes. Ninguno de los dos los instala sin que pulses el botón en
Ajustes.

## Herramientas

| Para | Herramienta | Notas |
|---|---|---|
| Compilar el APK | JDK 17 + Android SDK (plataforma 35) | Gradle 8.11 no funciona con el JDK 25 de Android Studio: instala `winget install EclipseAdoptium.Temurin.17.JDK` o elige un JDK 17 en Android Studio › Settings › Build Tools › Gradle › Gradle JDK. Gradle descarga la plataforma 35 por sí mismo. |
| Instalar y depurar en el teléfono | `adb` (Android SDK platform-tools) | Depuración USB activada en el teléfono. |
| Compilar y probar el agente | .NET 10 SDK | `winget install Microsoft.DotNet.SDK.10` |
| Vista previa del panel sin teléfono | Python 3 | `python tools/demo-server.py` |
| Tests de navegador | Node.js 22 + Playwright | `tools/ui-tests` |

No hacen falta `apktool`, `jadx` ni editores de APK: esas herramientas sirven para apps sin código fuente. Aquí el
APK se compila desde `android/`.

## Cómo trabajar

Elige el ciclo más corto para cada tipo de cambio:

1. **Aspecto del panel y widgets** (`android/app/src/main/assets/www/`, HTML/CSS/JS): sin compilar nada.
   `python tools/demo-server.py` y abre <http://127.0.0.1:8790/panel/> con la ventana a 780 × 360 (la pantalla
   del teléfono). Recarga para ver cada cambio.
2. **Código Android** (Kotlin, manifiesto, recursos): compila e instala.

   ```powershell
   cd android
   .\gradlew.bat assembleDebug
   adb install -r app\build\outputs\apk\debug\app-debug.apk
   adb logcat --pid=$(adb shell pidof com.syroke.rokebox)
   ```

   En compilaciones debug, el WebView se puede inspeccionar desde Chrome en el PC: `chrome://inspect`.
3. **Agente de PC**: ejecútalo desde el código.

   ```powershell
   dotnet run --project agent/PhoneBox.Agent -f net10.0-windows10.0.19041.0
   dotnet test agent/PhoneBox.Agent.Tests
   ```

   Los ajustes quedan en <http://127.0.0.1:8775>.

Antes de publicar, ejecuta también `.\gradlew.bat lintDebug`: marca como error las llamadas que harían fallar la app en
Android antiguos. El panel debe seguir funcionando en Chromium 83, así que evita sintaxis moderna como `||=`
(`tools/ui-tests/syntax-level.spec.js` lo comprueba).

## Firma del APK de release

Sin clave propia, `assembleRelease` firma con la clave debug sin avisar. Crea una clave una sola vez y guárdala fuera
del repositorio (si la pierdes, no podrás actualizar la app instalada sin desinstalarla):

```powershell
keytool -genkeypair -v -keystore $HOME\keys\rokebox-release.jks -alias rokebox -keyalg RSA -keysize 4096 -validity 10000
```

Y en `%USERPROFILE%\.gradle\gradle.properties`:

```properties
ROKEBOX_STORE_FILE=C:/Users/<tú>/keys/rokebox-release.jks
ROKEBOX_STORE_PASSWORD=…
ROKEBOX_KEY_ALIAS=rokebox
ROKEBOX_KEY_PASSWORD=…
```

`.\scripts\build.ps1` genera `dist/RokeBox-<versión>.apk` y los zips `RokeBoxAgent-<versión>-<rid>.zip`.

## Traer mejoras de PhoneBox

El remoto `upstream` apunta al proyecto original, que publica cambios casi a diario.

```sh
git fetch upstream
git merge upstream/main
```

Para que estas fusiones sigan siendo fáciles, pon lo propio de RokeBox en archivos nuevos (por ejemplo
`widgets/roke-*.js`) y evita reordenar o reformatear archivos del original. Si un merge trae textos nuevos con
"PhoneBox", vuelve a pasar el script de cambio de marca o corrígelos a mano.

## Hoja de ruta

Por orden de impacto:

1. **Cargar solo el idioma activo.** Los diccionarios ocupan 4,0 MB de los 4,8 MB del panel, e `index.html` carga
   los 12 archivos de idioma más `i18n.js` en cada arranque. En un teléfono modesto (por ejemplo un Galaxy A10s) ese
   análisis de JavaScript retrasa la apertura del panel. Cargarlos bajo demanda acortaría el arranque.
2. **Que la release falle sin clave propia** en lugar de firmar con la clave debug sin avisar.
3. **Canal de releases propio:** repositorio de GitHub para RokeBox, `updates.repo` apuntando a él y el workflow
   `release.yml` adaptado a los nombres `RokeBox-*.apk` / `RokeBoxAgent-*.zip`.
4. **Página de emparejamiento Wi-Fi propia** en GitHub Pages, para que el QR abra la app con cualquier lector.
5. **Widgets propios** para tus otros proyectos mediante la API push (`/api/push/<clave>`) o el widget Custom HTML,
   sin tocar el núcleo.
6. **Español como idioma de referencia** y revisión de los textos en español.
7. **Minificar los assets JS** en la compilación (manteniendo la sintaxis compatible con Chromium 83).
