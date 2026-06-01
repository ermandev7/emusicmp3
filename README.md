# eMusic: Reproductor de Música Híbrido Avanzado 🎵

¡Bienvenido al repositorio de **eMusic**! Este proyecto es un reproductor de música de alto nivel que emula características avanzadas (como descargas offline reales y crossfade matemático) utilizando una arquitectura **Híbrida Web-Nativa**.

## 🗺️ Estructura del Repositorio

El repositorio está dividido en dos proyectos principales que se comunican entre sí:

1. **`eMusic/` (Frontend - React)**: La interfaz gráfica de usuario. Construida con React, Vite y Zustand. Es una Progressive Web App (PWA) rápida y reactiva que controla la experiencia del usuario y le da las instrucciones de reproducción al dispositivo móvil.
2. **`eMusicApp/` (Backend Móvil - .NET MAUI)**: Una aplicación móvil nativa (Android, iOS) que encapsula la web en un `BlazorWebView` y expone capacidades de hardware directamente a la web a través de un puente nativo.

---

## 🏗️ Mapa Conceptual de la Arquitectura

```mermaid
graph LR
    %% Frontend
    subgraph Web ["🌐 React (Frontend Web)"]
        UI["📱 Interfaz Visual"]
        Queue["🎵 Gestor de Canciones"]
    end

    %% Puente Híbrido
    subgraph Bridge ["🌉 Puente de Comunicación"]
        PlayCmd("▶️ Iniciar (emusic://play)")
        NextCmd("⏳ Precargar (emusic://preparenext)")
        FadeCmd("🎚️ Mezclar (emusic://startcrossfade)")
        DownloadCmd("⬇️ Guardar (emusic://download)")
    end

    %% Backend Nativo
    subgraph Native ["⚙️ App Nativa (MAUI C#)"]
        Offline["💾 Almacenamiento Offline"]
        PlayerA["🔊 Reproductor A"]
        PlayerB["🔊 Reproductor B (Crossfade)"]
    end

    %% Flujo Principal
    UI -->|"Descargar"| DownloadCmd
    DownloadCmd --> Offline

    Queue -->|"Dar Play"| PlayCmd
    PlayCmd --> PlayerA

    Queue -.->|"Faltan 15 seg"| NextCmd
    NextCmd --> PlayerB

    Queue -.->|"Faltan 3 seg"| FadeCmd
    FadeCmd -.->|"Fade In/Out"| PlayerA
    FadeCmd -.->|"Fade In/Out"| PlayerB
```

---

## 🚀 ¿Qué hace cada proyecto?

### 1. `eMusic` (Web / Frontend)
Es el "cerebro visual" de la aplicación.
- Construido con **React + Vite**.
- Se sirve estáticamente en una Raspberry Pi (o cualquier hosting).
- Monitoriza en tiempo real los segundos restantes de la canción actual.
- Faltando **15 segundos**, busca silenciosamente la próxima canción y manda la orden `emusic://preparenext`.
- Faltando **3 segundos**, manda la orden `emusic://startcrossfade`.

#### Compilación:
```bash
cd eMusic
npm run build
# Luego subir la carpeta 'dist' a tu servidor web (/var/www/html/emusic).
```

### 2. `eMusicApp` (App Móvil / MAUI)
Es el "músculo de hardware" de la aplicación.
- Descarga la web remotamente.
- Escucha los comandos `emusic://` y los traduce a funciones nativas de Android/iOS.
- **Modo Offline:** Gestiona la descarga de archivos a la carpeta segura `AppDataDirectory` e intercepta el puente web para reproducir los archivos de manera local sin gastar datos.
- **Crossfade:** Administra dos reproductores multimedia asíncronos para mezclar el audio a nivel de hardware del sistema operativo sin pausas.

#### Compilación (Para Android e iOS):
```bash
cd eMusicApp
# Compilar Android APK Release:
dotnet publish -f net9.0-android -c Release

# Compilar para iPhone (macOS requerido):
dotnet build -t:Run -f net9.0-ios
```

---

## ✨ Características Nivel Spotify Integradas
- **Descargas 100% Nativas:** Sin depender de cachés web inestables. Descargas veloces directas a disco.
- **Reproducción Híbrida Inteligente:** El backend verifica si existe el archivo offline. Si existe, no gasta internet. Si no, hace un stream limpio.
- **Gapless Playback y Crossfade:** Eliminación total de silencios entre canciones.
- **Controles de Pantalla de Bloqueo:** Integración con la sesión multimedia de Android nativa.
