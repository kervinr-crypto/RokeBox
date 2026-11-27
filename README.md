<h1 align="center">RokeBox</h1>

<p align="center">
  <b>Documentación del proyecto original (PhoneBox):</b>
  <a href="docs/README.tr.md">Türkçe</a> ·
  <a href="docs/README.de.md">Deutsch</a> ·
  <a href="docs/README.fr.md">Français</a> ·
  <a href="docs/README.es.md">Español</a> ·
  <a href="docs/README.it.md">Italiano</a> ·
  <a href="docs/README.pt.md">Português</a> ·
  <a href="docs/README.pt-BR.md">Português (Brasil)</a> ·
  <a href="docs/README.nl.md">Nederlands</a> ·
  <a href="docs/README.pl.md">Polski</a> ·
  <a href="docs/README.ru.md">Русский</a> ·
  <a href="docs/README.el.md">Ελληνικά</a> ·
  <a href="docs/README.vi.md">Tiếng Việt</a> ·
  <a href="docs/README.zh.md">中文 (简体)</a> ·
  <a href="docs/README.ja.md">日本語</a> ·
  <a href="docs/README.ko.md">한국어</a> ·
  <a href="docs/README.id.md">Bahasa Indonesia</a> ·
  <a href="docs/README.th.md">ไทย</a> ·
  <a href="docs/README.uk.md">Українська</a> ·
  <a href="docs/README.hi.md">हिन्दी</a> ·
  <a href="docs/README.cs.md">Čeština</a> ·
  <a href="docs/README.ar.md">العربية</a> ·
  <a href="docs/README.zh-Hant.md">中文（繁體）</a>
</p>

<p align="center"><b>Convierte un teléfono Android antiguo en un panel de escritorio para tu PC.</b><br>
Estadísticas del PC, sesiones de IA, música y controles táctiles por USB o Wi-Fi.</p>

<p align="center">
  <a href="https://kervinr-crypto.github.io/RokeBox/"><b>Demo en vivo</b></a> ·
  <a href="https://github.com/kervinr-crypto/RokeBox/releases/latest"><b>Descargar</b></a> ·
  <a href="ROKEBOX.md"><b>Desarrollo</b></a>
</p>

RokeBox es un fork de [PhoneBox](https://github.com/ersinkoc/phonebox), de Ersin KOÇ (licencia MIT). Une una
**app Android** a pantalla completa con un pequeño **agente de escritorio**: **142 widgets**, **33 templates**
(plantillas), varias páginas y un editor visual en el navegador. Tiene su propio identificador de app, puertos y
configuración, así que se puede instalar y usar junto a PhoneBox.

## Instalación (Windows)

1. Descarga del [último release](https://github.com/kervinr-crypto/RokeBox/releases/latest)
   `RokeBoxAgent-<versión>-win-x64.zip` y `RokeBox-<versión>.apk`.
2. Descomprime el zip y ejecuta `RokeBoxAgent.exe`. El agente no está firmado, así que SmartScreen puede avisar.
3. En el teléfono, activa **Opciones de desarrollador › Depuración USB**, conéctalo y acepta la autorización.
4. Instala la app: `adb install RokeBox-<versión>.apk` (o copia el APK al teléfono y ábrelo).
5. Abre los ajustes en <http://127.0.0.1:8775> (o doble clic en el icono de la bandeja) y elige una plantilla
   en **Pages**.

Con el teléfono conectado, el agente actualiza la app y a sí mismo desde los releases de este repositorio.
Los scripts `setup.ps1` y `setup.sh` todavía instalan PhoneBox original: no los uses para RokeBox.

| | PhoneBox | RokeBox |
|---|---|---|
| App Android | `net.oxog.phonebox.deskpanel` | `com.syroke.rokebox` |
| Ajustes del PC | `127.0.0.1:8765` | `127.0.0.1:8775` |
| Wi-Fi | puerto 8766 | puerto 8776 |
| Configuración | `%APPDATA%\PhoneBox` | `%APPDATA%\RokeBox` |

## Documentación

- [ROKEBOX.md](ROKEBOX.md): qué cambia respecto a PhoneBox, cómo compilar, firmar y publicar, y hoja de ruta.
- La guía de uso, widgets, Control Deck y seguridad del original sigue valiendo:
  [guía](docs/GUIDE.md) · [instalación y conexiones](docs/SETUP.md) · [apariencia](docs/APPEARANCE.md) ·
  [Control Deck](docs/CONTROL-DECK.md) · [privacidad](PRIVACY.md). Cambia "PhoneBox" por "RokeBox" y los
  puertos 8765/8766 por 8775/8776.

## Seguridad y privacidad

Igual que PhoneBox: el agente escucha en `127.0.0.1`, el USB va por un túnel `adb reverse` con TLS fijado y la clave de
la app, y el Wi-Fi (desactivado por defecto) usa HTTPS con certificado emparejado. Sin cuentas ni telemetría. Las
actualizaciones se consultan en `api.github.com/repos/kervinr-crypto/RokeBox`.

## Licencia

MIT. © 2026 Ersin KOÇ (PhoneBox); los cambios de RokeBox se publican con la misma licencia. Ver [LICENSE](LICENSE).
