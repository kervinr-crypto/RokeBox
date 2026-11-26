# RokeBox

RokeBox es un fork de [PhoneBox](https://github.com/ersinkoc/phonebox) (licencia MIT, © 2026 Ersin KOÇ; el aviso
de copyright de `LICENSE` debe conservarse). Convierte un teléfono Android en un panel de escritorio para el PC:
una app Android (Kotlin + WebView con widgets en JavaScript) y un agente de escritorio (.NET 10).

- Repositorio: <https://github.com/kervinr-crypto/RokeBox> (rama `main`; el remoto `upstream` es PhoneBox).
- Demo y página de emparejamiento Wi-Fi: <https://kervinr-crypto.github.io/RokeBox/>.

RokeBox cambia solo la **identidad** de la app, para que RokeBox y PhoneBox se puedan instalar y usar a la vez y para
que traer mejoras del proyecto original siga siendo barato. El código interno (espacios de nombres `PhoneBox.Agent`,
paquete Kotlin `net.oxog.phonebox.deskpanel`, API `window.phonebox` de los widgets HTML, cabeceras `X-PhoneBox-*`) no
cambia a propósito.

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
| Actualizaciones | releases de `ersinkoc/phonebox` | releases de `kervinr-crypto/RokeBox` |
| Enlace de emparejamiento Wi-Fi | `https://ersinkoc.github.io/phonebox/pair/#…` | `https://kervinr-crypto.github.io/RokeBox/pair/#…` |
| Variables de firma del APK | `PHONEBOX_STORE_FILE`, … | `ROKEBOX_STORE_FILE`, … |

Pendiente de adaptar:

- **Instaladores** `setup.ps1` / `setup.sh` e instalador de Windows: siguen instalando PhoneBox.
- **Tests de interfaz** (`tools/ui-tests`, Playwright): varios esperan textos con "PhoneBox".
- **Linux, macOS e iOS:** los releases de RokeBox solo incluyen Windows x64; los identificadores de bundle de macOS
  e iOS siguen siendo los de PhoneBox.
- Algunos textos que el agente genera en C# (errores de Discord, cabeceras User-Agent) siguen diciendo PhoneBox.

## Convivencia con PhoneBox

Las dos apps se instalan una al lado de la otra en el teléfono, y los dos agentes pueden ejecutarse a la vez en el PC
porque usan puertos, carpetas e instancia única distintos. Cada agente solo gestiona su propia regla `adb reverse`.
Si el agente de PhoneBox tiene activada la opción de abrir el panel al conectar el cable, puede traer PhoneBox al
frente: desactívala o cierra PhoneBox mientras usas RokeBox.

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
| Publicar releases | GitHub CLI | `winget install GitHub.cli` y `gh auth login` |
| Vista previa del panel sin teléfono | Python 3 | `python tools/demo-server.py` |

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
   ```

   Un APK debug no se instala encima de uno de release (firmas distintas): para probarlo, desinstala antes RokeBox
   del teléfono. En compilaciones debug, el WebView se puede inspeccionar desde Chrome en el PC: `chrome://inspect`.
3. **Agente de PC**: ejecútalo desde el código.

   ```powershell
   dotnet run --project agent/PhoneBox.Agent -f net10.0-windows10.0.19041.0
   dotnet test agent/PhoneBox.Agent.Tests
   ```

   Los ajustes quedan en <http://127.0.0.1:8775>.

Antes de subir cambios, ejecuta también `.\gradlew.bat lintDebug` y `python tools/check-release.py`. El panel debe
seguir funcionando en Chromium 83, así que evita sintaxis moderna como `||=`.

El CI de RokeBox (`.github/workflows/rokebox-ci.yml`) ejecuta esas comprobaciones, la compilación de Android y los
tests del agente en cada push. Los workflows del original siguen en el repositorio pero están desactivados en GitHub.

## Firma y publicación

La clave de firma está en `C:\Users\SyRoke\keys\rokebox-release.jks` y su contraseña en
`C:\Users\SyRoke\.gradle\gradle.properties` (`ROKEBOX_*`), fuera del repositorio. **Haz una copia de las dos cosas**
(por ejemplo en un gestor de contraseñas): sin ellas no podrás publicar actualizaciones que se instalen encima de la
app actual.

Para publicar una versión nueva:

1. Sube la versión en `agent/PhoneBox.Agent/PhoneBox.Agent.csproj` (`<Version>`) y en
   `android/app/build.gradle.kts` (`versionCode` +1 y `versionName`), haz commit y `git push`.
2. Ejecuta `.\scripts\rokebox-release.ps1 -Publish`. Compila el APK firmado y el agente de Windows, genera
   `SHA256SUMS-<versión>.txt` y crea el release `v<versión>`. El script se detiene si el APK está firmado con la
   clave debug.

Los agentes instalados buscan releases cada 6 horas y actualizan la app del teléfono por USB y a sí mismos.

## Traer mejoras de PhoneBox

El remoto `upstream` apunta al proyecto original, que publica cambios casi a diario.

```sh
git fetch upstream
git merge upstream/main
```

Para que estas fusiones sigan siendo fáciles, pon lo propio de RokeBox en archivos nuevos (por ejemplo
`widgets/roke-*.js`) y evita reordenar o reformatear archivos del original. En un conflicto en `README.md`, quédate
con el de RokeBox. Si un merge trae textos nuevos con "PhoneBox", cámbialos a mano. Cuando el original suba de
versión, publica la misma versión de RokeBox.

## Hoja de ruta

Por orden de impacto:

1. **Cargar solo el idioma activo.** Los diccionarios ocupan 4,0 MB de los 4,8 MB del panel, e `index.html` carga
   los 12 archivos de idioma más `i18n.js` en cada arranque. En un teléfono modesto (por ejemplo un Galaxy A10s) ese
   análisis de JavaScript retrasa la apertura del panel. Cargarlos bajo demanda acortaría el arranque.
2. **Adaptar los tests de interfaz** a los textos de RokeBox y añadirlos al CI.
3. **Instalador propio** (`setup.ps1`) que descargue los releases de RokeBox.
4. **Widgets propios** para tus otros proyectos mediante la API push (`/api/push/<clave>`) o el widget Custom HTML,
   sin tocar el núcleo.
5. **Español como idioma de referencia** y revisión de los textos en español.
6. **Releases para Linux** (el agente ya compila para `linux-x64` y `linux-arm64`).
7. **Minificar los assets JS** en la compilación (manteniendo la sintaxis compatible con Chromium 83).
