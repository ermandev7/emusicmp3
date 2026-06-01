# eMusic: Reproductor de Música Híbrido Avanzado 🎵

¡Bienvenido al repositorio de **eMusic**! Este proyecto es un reproductor de música de alto nivel que emula características avanzadas (como descargas offline reales y crossfade matemático) utilizando una arquitectura **Híbrida Web-Nativa**.

## 🗺️ Estructura del Repositorio

El repositorio está dividido en dos proyectos principales que se comunican entre sí:

1. **`eMusic/` (Frontend - React)**: La interfaz gráfica de usuario. Construida con React, Vite y Zustand. Es una Progressive Web App (PWA) rápida y reactiva que controla la experiencia del usuario y le da las instrucciones de reproducción al dispositivo móvil.
2. **`eMusicApp/` (Backend Móvil - .NET MAUI)**: Una aplicación móvil nativa (Android, iOS) que encapsula la web en un `BlazorWebView` y expone capacidades de hardware directamente a la web a través de un puente nativo.

---

## 🏗️ Mapa Conceptual de la Arquitectura

```mermaid
graph TD
    %% Frontend
    subgraph Frontend [🌐 React (eMusic/)]
        UI[Interfaz Gráfica]
        Store[Zustand Store]
        Queue[Gestión de Cola]
        API_Piped[Cliente API Piped]
    end

    %% Puente Híbrido
    subgraph Bridge [🌉 Puente Híbrido (emusic://)]
        P_Play[emusic://play]
        P_Next[emusic://preparenext]
        P_Crossfade[emusic://startcrossfade]
        P_Download[emusic://download]
        JS_Callbacks[window.onNativeTrackEnded]
    end

    %% Backend Nativo
    subgraph Backend [📱 MAUI (eMusicApp/)]
        MainPage[MainPage.xaml.cs]
        AutoMedia[AutoMediaService Android]
        Downloader[DownloadManager C#]
        Storage[(Almacenamiento Local)]
        
        PlayerA(Reproductor A)
        PlayerB(Reproductor B)
    end

    %% Conexiones
    UI --> Store
    Store --> Queue
    Queue --> API_Piped
    
    Queue -- 1. Envia URL/ID --> P_Play
    Queue -- 15s antes del final --> P_Next
    Queue -- 3s antes del final --> P_Crossfade
    UI -- "Descargar" --> P_Download
    
    P_Play --> MainPage
    P_Next --> MainPage
    P_Crossfade --> MainPage
    P_Download --> MainPage
    
    MainPage --> Downloader
    Downloader --> Storage
    
    MainPage --> AutoMedia
    AutoMedia --> PlayerA
    AutoMedia --> PlayerB
    AutoMedia -- "Fin de pista" --> JS_Callbacks
    JS_Callbacks --> Store
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
