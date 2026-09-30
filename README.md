(100% vibecoded)
# 🎵 YT Music Launcher Standalone

Un cliente standalone moderno para **YouTube Music** desarrollado en C# / WPF con .NET 10 y Microsoft Edge WebView2, equipado con **AdBlocker integrado estilo uBlock Origin**, integrador de descargas avanzadas con **yt-dlp** (canciones y playlists completas), presencia de estado en tiempo real en **Discord Rich Presence**, y una sección de **Biblioteca local** con reproductor integrado.

---

## ✨ Características Principales

### 🎵 1. Cliente Standalone de YouTube Music con AdBlocker (uBlock Origin)
- Carga nativa de `https://music.youtube.com` en una ventana standalone independiente de tu navegador web.
- **Bloqueador de Anuncios Avanzado (AdBlocker Engine)**:
  - **Bloqueo a Nivel de Red**: Intercepta y bloquea peticiones a servidores de anuncios (`googlesyndication.com`, `doubleclick.net`, `googleadservices.com`, `/pagead/`, etc.).
  - **Sanitización de la API de YouTube**: Filtra las respuestas de los endpoints `/youtubei/v1/player` eliminando `adPlacements` y `playerAds` en tiempo real para evitar que se programen anuncios de audio o video.
  - **Auto-Skipper y Eliminación de Popups**: Auto-salta anuncios de vídeo al instante, silencia anuncios breves y elimina automáticamente ventanas emergentes como *"¿Sigues ahí escuchando?"*.

---

### 🎮 2. Discord Rich Presence Integrado (Estilo Spotify)
- Muestra automáticamente en tu perfil de Discord la canción que estás escuchando en tiempo real.
- **Formato Estilo Spotify**: Muestra la barra de progreso deslizante (`01:23 ━━━━━━━⚪────────── 03:45`) y el texto *"Escuchando a"*.
- **Portada e Información**: Muestra la imagen de portada de la canción y la información del artista.
- **Botón Interactivo**: Agrega un botón en tu estado de Discord para que tus amigos puedan abrir la canción directamente en YouTube Music.

---

### ⚡ 3. Integración de Descargas con yt-dlp
- **Boton Rápido "⚡ Descargar Canción Actual"**: Con un solo clic desde la barra superior mientras escuchas YouTube Music, captura la URL activa y comienza la descarga hacia tu Biblioteca.
- **Soporte Completo de Enlaces**: Permite pegar enlaces individuales de canciones o **playlists completas** con control individual de skip por canción.
- **Formatos Disponibles**: MP3 (320 kbps), M4A (AAC), FLAC (Lossless) y WAV.
- **Soporte de Carátula y Metadatos**: Descarga e incrusta automáticamente la carátula oficial en alta resolución y la información del artista/álbum.

---

### 📚 4. Sección "Biblioteca" (Local Music Library)
- **Organización por Playlists**: Separa automáticamente tus canciones por playlists o carpetas.
- **Buscador y Filtros**: Filtra por género, nombre de canción, artista o álbum en tiempo real.
- **Reproductor de Audio Local Integrado**: Sincronizado con teclado (flechas `←` `→` ±5s, Espacio para Pausa).

---

## 🚀 Instalación y Uso

### ⬇️ Para Usuarios (Descargar Ejecutable Listo)
Descarga la versión portable sin instalador desde la sección de **[Releases]([https://github.com/joaquin098/YTlauncher-Downloader/releases))** de GitHub:
1. Descarga `YTMusicLauncher_Portable.zip`
2. Descomprime el archivo y abre `YTMusicLauncher.exe` (No requiere instalar .NET ni nada previo).

---

## 🛠️ Para Desarrolladores (Código Fuente)

### Requisitos
- Windows 10 u 11 (64-bit)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### Compilar y Ejecutar
```bash
git clone https://github.com/TuUsuario/YTMusicLauncher.git
cd YTMusicLauncher
dotnet run
```
