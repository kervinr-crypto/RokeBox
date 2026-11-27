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
| Nombre e icono | PhoneBox, barras de colores | RokeBox: placa de grafito con una pantalla, una "R", una luz rosa y la escala de páginas |
| Puerto de ajustes del PC | 8765 | 8775 |
| Puerto del túnel USB en el teléfono | 8765 | 8775 |
| Puerto Wi-Fi | 8766 | 8776 |
| Configuración y registros | `%APPDATA%\PhoneBox`, `%LOCALAPPDATA%\PhoneBox` | `%APPDATA%\RokeBox`, `%LOCALAPPDATA%\RokeBox` |
| Ejecutable / instancia única | `PhoneBoxAgent.exe`, `Local\PhoneBox.Agent` | `RokeBoxAgent.exe`, `Local\RokeBox.Agent` |
| Actualizaciones | releases de `ersinkoc/phonebox` | releases de `kervinr-crypto/RokeBox` |
| Enlace de emparejamiento Wi-Fi | `https://ersinkoc.github.io/phonebox/pair/#…` | `https://kervinr-crypto.github.io/RokeBox/pair/#…` |
| Variables de firma del APK | `PHONEBOX_STORE_FILE`, … | `ROKEBOX_STORE_FILE`, … |
| Tema por defecto | `default` (turquesa) | `rokebox`, diseño "Chasis" (ver más abajo) |
| Aspecto de la página de ajustes y de la web | el de PhoneBox | el mismo diseño "Chasis" (`roke-settings.css`, `tools/pages/`) |
| Instalador `setup.ps1` | instala PhoneBox | instala RokeBox al lado de PhoneBox, sin tocarlo |
| Ciudad del clima por defecto | Tallinn (la del autor) | ninguna: el panel pide elegirla en Ajustes › Clima |
| Widget y plantilla **Gaming** | no existe | el juego de Steam en curso y lo que consume |
| Widgets **Ficha del PC**, **Estado del PC**, **Consumo de datos**, **Temporizador de energía** | no existen | ver más abajo |
| Medidor de red | cuenta cada adaptador una vez más por cada filtro NDIS | cuenta solo el adaptador real |

Pendiente de adaptar:

- **Instaladores** `setup.sh` (Linux/macOS) e instalador de Windows (Inno Setup): siguen instalando PhoneBox.
  `setup.ps1` ya instala RokeBox.
- **Tests de interfaz** (`tools/ui-tests`, Playwright): algunos esperan textos con "PhoneBox" (los de la portada y
  el emparejamiento ya están adaptados).
- **Linux, macOS e iOS:** los releases de RokeBox solo incluyen Windows x64; los identificadores de bundle de macOS
  e iOS siguen siendo los de PhoneBox.
- Algunos textos que el agente genera en C# (errores de Discord, cabeceras User-Agent) siguen diciendo PhoneBox.

## Diseño "Chasis"

El aspecto propio de RokeBox (desde 0.9.30.3), hecho siguiendo la skill `frontend-design` de
[anthropics/skills](https://github.com/anthropics/skills) y revisado con las
[Web Interface Guidelines](https://github.com/vercel-labs/web-interface-guidelines) de
[vercel-labs/agent-skills](https://github.com/vercel-labs/agent-skills). La idea: el teléfono es una pieza de hardware
de escritorio.

- **Las pantallas se hunden, los controles suben.** El fondo es una placa de grafito con grano; cada widget es una
  ventana negra hundida en ella; lo que se pulsa (atajos, botón grande, play, preajustes, Control Deck, las respuestas
  al aviso de permiso) es una tecla en relieve que baja al tocarla.
- **El color es dato.** La placa y el texto son neutros. El color sale de las lecturas (CPU, GPU, RAM, red), de la
  portada del disco y de una sola marca encendida (la página actual, el botón de play), en el color de acento.
- **La anchura marca la jerarquía.** Mona Sans (licencia SIL OFL, `www/roke-mona-*.woff2` y `roke-mona-OFL.txt`):
  cifras condensadas para el reloj y las lecturas, etiquetas expandidas sin mayúsculas espaciadas.
- **Navegación:** los puntos de página son una escala grabada de marcas, la actual encendida; el nombre de la página
  aparece como una etiqueta pegada en la placa.

| Dónde | Archivo |
|---|---|
| Panel (tema `rokebox`, el resto de temas no cambia) | `android/app/src/main/assets/www/roke.css` |
| Página de ajustes del agente (riel de grafito, placas, campos hundidos, teclas; aluminio en modo claro) | `www/roke-settings.css`, cargado desde `settings.html` como `../panel/roke-settings.css` |
| Portada y emparejamiento Wi-Fi (GitHub Pages) | `tools/pages/index.html`, `tools/pages/pair.html` |
| Icono | `res/drawable/ic_launcher_fg.xml` (Android), `tools/make-icon.ps1` → `phonebox.ico` (Windows) |
| Pantallas nativas (Phone bridge, fondo mientras carga) | `BridgeTheme.kt`, `res/values*/themes.xml` |

El panel arranca ya con `data-theme="rokebox"` en `index.html`, así no muestra otro aspecto mientras llega la
configuración. Para ver el aspecto anterior de PhoneBox basta con elegir otro tema en Ajustes › Pantalla.

## Pestaña Gaming

El widget `gaming` (y la plantilla del mismo nombre) muestra el juego de Steam que está en marcha. Los datos salen del
propio PC, sin cuentas ni servicios externos:

| Dato | De dónde sale |
|---|---|
| Juego, tamaño en disco, carpeta | `RunningAppID` del registro de Steam y su `appmanifest_<id>.acf` |
| Portada | la caché local de Steam (`appcache/librarycache`), la misma que usa el widget Biblioteca de Steam |
| Procesos del juego | los programas que se ejecutan desde la carpeta del juego |
| CPU, RAM, disco del juego | `GetProcessTimes`, `GetProcessMemoryInfo` y `GetProcessIoCounters` de esos procesos |
| GPU y VRAM del juego | contadores de Windows `GPU Engine` y `GPU Process Memory` filtrados por PID |
| Red | **la del PC entero** (los adaptadores que cuenta el medidor de red) y lo descargado y subido desde que empezó el juego |

Límites: Windows no permite medir la red de un solo proceso sin permisos de administrador, por eso la red es la del
PC. Los FPS no se miden (harían falta PresentMon o ETW). Solo Steam: los juegos de otras tiendas no se detectan.
El agente solo lee todo esto mientras un widget Gaming está en una página activa.

## Control e información del sistema

Inspirados en aplicaciones parecidas (AIDA64 y HWiNFO para la ficha técnica, GlassWire y NetWorx para el consumo de
datos, Unified Remote y los paneles de control para el temporizador). Plantillas **PC** y **Control**. Todo se lee en
este PC sin permisos de administrador y solo mientras el widget está en una página activa; nada sale del PC.

| Widget | Qué muestra | De dónde sale |
|---|---|---|
| `sysinfo` Ficha del PC | Windows (versión, build, instalación), tiempo encendido, CPU (núcleos/hilos), RAM (módulos, tipo, velocidad, fabricante), placa base y BIOS, drivers de GPU, pantallas (resolución y Hz), discos físicos (NVMe/SATA/USB, SSD/HDD), volúmenes, adaptadores de red | registro de Windows, tabla SMBIOS del firmware, `EnumDisplaySettings`, IOCTL de almacenamiento sin derechos de acceso |
| `pchealth` Estado del PC | espacio libre, memoria, temperaturas de CPU y GPU, días sin reiniciar, reinicio pendiente de Windows Update, papelera | `DriveInfo`, `GlobalMemoryStatusEx`, claves de Windows Update, `SHQueryRecycleBin` |
| `datausage` Consumo de datos | hoy, 7 días y ciclo mensual (día de corte configurable) contra un límite opcional, gráfico diario | contadores de los adaptadores que cuenta el medidor de red, guardados en `%LOCALAPPDATA%\RokeBox\data-usage.json` (400 días) |
| `powertimer` Temporizador de energía | suspender, apagar o reiniciar en 15–120 min con cuenta atrás y cancelación | `shutdown.exe /s` o `/r /t` (Windows lo anuncia y se puede cancelar); suspender con el temporizador del agente |

Lo que cambia el PC es opcional: **vaciar la papelera** exige activar "Let the phone empty the Recycle Bin" en ese
widget, y el **temporizador** exige los controles de energía del Control Deck activados en Ajustes (los mismos que los
botones de apagado). Los dos piden un segundo toque para confirmar. Apagar o reiniciar cierra los programas abiertos.

El consumo de datos empieza a contar cuando añades el widget y solo mientras está en una página activa.

## Control del PC desde el teléfono

Desde 0.9.30.3. Se suman a lo que ya traía PhoneBox (Control Deck: audio, salidas, planes de energía, escritorios
virtuales, atajos, trackpad, dictado, procesos…). Plantillas **Remote** (pantalla y ventanas), **Keyboard & mouse**
(teclado y trackpad) y **Quick settings** (acciones rápidas, plan de energía, salida de sonido). Código:
`agent/PhoneBox.Agent/RokeControl.cs`, `www/widgets/roke-control.js`, textos en `www/locale-zz-roke.js` (inglés,
español, portugués).

| Widget | Qué hace | Interruptor (en los ajustes del widget, apagado por defecto) |
|---|---|---|
| `windows` Ventanas | lista las ventanas abiertas (como Alt+Tab) con el icono de su programa; tocar = traer al frente; minimizar, maximizar/restaurar, cerrar (`WM_CLOSE`, como su X; dos toques) | "Let the phone switch, minimize and close windows" (la lista se ve sin él) |
| `keyboard` Teclado | escribe texto (una línea; los saltos de línea no son Enter) y pulsa teclas de una **lista cerrada**; las flechas y el borrado se repiten al mantener | "Type and press keys on the computer" |
| `screen` Pantalla del PC | JPEG de un monitor cada N s (1–60) solo mientras la página está en pantalla, con un anillo en el puntero; tocar = clic, mantener = clic derecho, "2×" = doble clic | dos: "Let the phone see this computer's screen" y "Click where the picture is tapped" |
| `quickactions` Acciones rápidas | modo de pantalla (`DisplaySwitch.exe`; dos toques), tema claro/oscuro (registro del usuario), escritorio, captura, recorte, Explorador, Administrador de tareas, Configuración, Xbox Game Bar | "Let the phone run these actions" |
| `gaming` (botón) | Cerrar juego (pide a sus ventanas que se cierren) y, si no responde, Forzar cierre (termina sus procesos; se pierde lo no guardado) | "Let the phone close the game" |

Reglas de seguridad, como el dictado y "start games" de la biblioteca de Steam: el agente solo actúa para el widget
que el teléfono nombra, en una página activa de ese teléfono y con **su propio** interruptor encendido; el teléfono
no puede encender un interruptor (se hace en el PC). Nada se escribe en una app que se ejecute como administrador
(Windows lo bloquearía en silencio). Solo se actúa sobre ventanas de la lista, solo teclas y acciones de las listas
fijas y solo clics dentro de la imagen. Entre dos capturas pasan al menos 400 ms. No se registran textos, títulos de
ventana ni imágenes. Las rutas son `/api/rk/*`, así que funcionan por USB y por Wi-Fi. Todo es solo Windows salvo el
teclado (Windows, Linux y macOS).

## Convivencia con PhoneBox

Las dos apps se instalan una al lado de la otra en el teléfono, y los dos agentes pueden ejecutarse a la vez en el PC
porque usan puertos, carpetas e instancia única distintos. Cada agente solo gestiona su propia regla `adb reverse`.
Si el agente de PhoneBox tiene activada la opción de abrir el panel al conectar el cable, puede traer PhoneBox al
frente: desactívala o cierra PhoneBox mientras usas RokeBox.

**Hooks de aprobación (Claude Code, Codex, Cursor, WorkBuddy):** cada agente reconoce como suyo cualquier hook que
llame a `127.0.0.1:<puerto>/api/hooks/…`, sea cual sea el puerto. Instalarlos desde RokeBox sustituye los de PhoneBox
(y al revés). Instálalos solo desde uno de los dos agentes. Ninguno de los dos los instala sin que pulses el botón en
Ajustes.

**adb:** cierra los agentes desde el icono de la bandeja, no a la fuerza. Un agente cerrado a la fuerza a mitad de una
orden puede dejar colgado el servidor adb; al minuto, el vigilante de adb del otro agente lo reinicia y cualquier
instalación en curso falla (basta con repetirla).

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
3. **Widgets propios** para tus otros proyectos mediante la API push (`/api/push/<clave>`) o el widget Custom HTML,
   sin tocar el núcleo.
4. **Español como idioma de referencia** y revisión de los textos en español.
5. **Releases para Linux** (el agente ya compila para `linux-x64` y `linux-arm64`).
6. **FPS en la pestaña Gaming** (con PresentMon) y soporte de otras tiendas.
7. **Minificar los assets JS** en la compilación (manteniendo la sintaxis compatible con Chromium 83).
