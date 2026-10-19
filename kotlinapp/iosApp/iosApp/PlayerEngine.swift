import AVFoundation
import Combine
import MediaPlayer
import SwiftUI
import shared

/// Reproductor de eMusic en iOS. Es el equivalente de `MusicService.kt` (Media3/ExoPlayer)
/// en Android: mantiene la cola, resuelve la URL de cada tema y expone el estado a la UI.
///
/// Diferencia de diseno respecto de Android, a proposito: alla se encolan `MediaItem` con
/// una URI placeholder que se resuelve al vuelo. Aca la cola es un `[Track]` propio y se usa
/// un solo `AVPlayer`, resolviendo la URL del stream justo antes de reproducir cada tema.
/// Es mas simple y encaja con `getStream(videoId:)`, que hay que llamar por cancion. Para
/// que el salto entre temas no se note, la URL del siguiente se resuelve por adelantado.
@MainActor
final class PlayerEngine: ObservableObject {

    static let shared = PlayerEngine()

    // MARK: Estado observable

    @Published private(set) var currentTrack: Track?
    @Published private(set) var isPlaying = false
    @Published private(set) var isBuffering = false
    @Published private(set) var position: Double = 0
    @Published private(set) var duration: Double = 0
    @Published private(set) var errorMessage: String?

    /// Aleatorio y repeticion, con los mismos tres estados que ExoPlayer.
    @Published private(set) var shuffleEnabled = false
    @Published private(set) var repeatMode: RepeatMode = .off

    /// Si el tema actual esta en favoritos. Se consulta al backend en cada cambio.
    @Published private(set) var isFavorite = false

    enum RepeatMode {
        case off, all, one
    }

    /// Progreso 0...1 para la barrita del mini reproductor.
    var progress: Double {
        guard duration > 0 else { return 0 }
        return min(max(position / duration, 0), 1)
    }

    // MARK: Cola

    /// Publicadas para que la hoja de "Cola" se refresque sola cuando la radio la estira.
    @Published private(set) var queue: [Track] = []
    @Published private(set) var index: Int = 0

    private let player = AVPlayer()
    private let network = SharedClients.shared.network
    private let api = SharedClients.shared.api
    private let radio = SharedClients.shared.radio

    /// Orden original de la cola, para poder deshacer el aleatorio.
    private var unshuffledQueue: [Track] = []

    /// Item del proximo tema YA construido y precargando su cabecera, para que al pasar
    /// de cancion no haya ni llamada de red ni apertura de stream. Es el equivalente de
    /// tener toda la cola encolada en ExoPlayer.
    private var preparedNext: (videoId: String, item: AVPlayerItem)?

    /// Saltos automaticos consecutivos tras error; se resetea al sonar de verdad.
    /// Mismo mecanismo que `autoSkipCount` en `MusicService.kt`.
    private var autoSkipCount = 0

    private var timeObserver: Any?
    private var endObserver: NSObjectProtocol?
    private var failObserver: NSObjectProtocol?
    private var itemStatusCancellable: AnyCancellable?
    private var timeControlCancellable: AnyCancellable?
    private var stallWatchdog: Task<Void, Never>?

    /// Formato elegido para el tema actual; se usa en los mensajes de error.
    private var currentStreamSummary: String = ""

    /// Identifica la carga en curso: si el usuario toca otra cancion mientras se resuelve
    /// la anterior, la respuesta vieja se descarta.
    private var loadToken = UUID()

    private init() {
        // CLAVE PARA EL ARRANQUE. Por defecto AVPlayer espera a tener buffer suficiente
        // para estimar que puede reproducir el tema entero sin cortes; contra la Pi eso
        // son varios segundos de espera con la UI girando. ExoPlayer arranca con ~2.5 s
        // de buffer y por eso Android suena antes. Poniendolo en false, AVPlayer empieza
        // en cuanto tiene datos, igual que Android.
        player.automaticallyWaitsToMinimizeStalling = false

        configureAudioSession()
        observePlayer()
        configureRemoteCommands()
    }

    // MARK: - API publica

    /// Reproduce `track`. Si `radioSeed` es true se activa el modo radio: la cola arranca con
    /// una sola cancion y `RadioEngine` (modulo compartido) la va extendiendo sola. Si es
    /// false se encola `contextQueue` tal cual (secciones curadas: favoritos, playlists...).
    func play(track: Track, contextQueue: [Track]? = nil, radioSeed: Bool = false) {
        if radioSeed || contextQueue == nil {
            queue = [track]
            index = 0
            radio.start()
        } else {
            queue = contextQueue ?? [track]
            index = queue.firstIndex { $0.videoId == track.videoId } ?? 0
            radio.stop()
        }
        prefetchUpcoming()
        Task { await load(trackAt: index, autoPlay: true) }
    }

    /// Le pide al backend que vaya resolviendo los 3 siguientes de la cola, igual que
    /// `PlayerViewModel.playTrack` en Android. Es lo que evita que cada cambio de
    /// cancion dispare una extraccion en frio de yt-dlp en la Pi.
    private func prefetchUpcoming() {
        // Los que ya estan descargados se excluyen: hacer trabajar a la Pi por un tema que
        // vamos a leer del disco es gasto puro.
        let ids = queue.dropFirst(index + 1).prefix(3)
            .map(\.videoId)
            .filter { !$0.isEmpty && !DownloadStore.shared.isDownloaded($0) }
        guard !ids.isEmpty else { return }
        Task { try? await SharedClients.shared.api.prefetch(videoIds: Array(ids)) }
    }

    /// No toca `isPlaying` a mano: solo ordena, y el observador de `timeControlStatus`
    /// actualiza el estado cuando el reproductor realmente arranca o para.
    func togglePlayPause() {
        guard currentTrack != nil else { return }
        if player.timeControlStatus == .paused {
            player.play()
        } else {
            player.pause()
        }
    }

    func next() {
        guard index + 1 < queue.count else { return }
        index += 1
        Task { await load(trackAt: index, autoPlay: true) }
    }

    func previous() {
        // Igual que en la mayoria de reproductores: si ya avanzo, vuelve al principio.
        if position > 3 || index == 0 {
            seek(to: 0)
            return
        }
        index -= 1
        Task { await load(trackAt: index, autoPlay: true) }
    }

    func seek(to seconds: Double) {
        player.seek(to: CMTime(seconds: seconds, preferredTimescale: 600))
        position = seconds
        updateNowPlayingPlaybackState()
    }

    /// Vuelve a intentar el tema actual (boton "Reintentar" de la pantalla de reproductor).
    func retry() {
        Task { await load(trackAt: index, autoPlay: true) }
    }

    /// Salta a una posicion concreta de la cola (lista de "Cola" del reproductor).
    func seekToIndex(_ i: Int) {
        guard queue.indices.contains(i), i != index else { return }
        index = i
        Task { await load(trackAt: i, autoPlay: true) }
    }

    // MARK: - Aleatorio y repeticion

    /// Igual que `shuffleModeEnabled` en ExoPlayer: la cancion en curso no se mueve, se
    /// baraja lo que queda por sonar. Al desactivarlo se recupera el orden original.
    func toggleShuffle() {
        shuffleEnabled.toggle()
        guard !queue.isEmpty, queue.indices.contains(index) else { return }
        let current = queue[index]

        if shuffleEnabled {
            unshuffledQueue = queue
            var rest = queue
            rest.remove(at: index)
            queue = [current] + rest.shuffled()
            index = 0
        } else {
            // La radio pudo haber añadido temas mientras estaba barajada: se conservan.
            let knownIds = Set(unshuffledQueue.map(\.videoId))
            let added = queue.filter { !knownIds.contains($0.videoId) }
            let restored = unshuffledQueue.isEmpty ? queue : unshuffledQueue + added
            queue = restored
            index = restored.firstIndex { $0.videoId == current.videoId } ?? 0
            unshuffledQueue = []
        }

        // El "siguiente" ya no es el mismo: se descarta lo precargado y se rehace.
        preparedNext = nil
        let token = loadToken
        Task { await self.prepareNext(token: token) }
    }

    /// off → all → one → off, el mismo ciclo que `MusicController.cycleRepeatMode`.
    func cycleRepeat() {
        switch repeatMode {
        case .off: repeatMode = .all
        case .all: repeatMode = .one
        case .one: repeatMode = .off
        }
    }

    /// Que hacer cuando un tema termina solo. Con "repetir uno" vuelve a empezar; con
    /// "repetir todo" da la vuelta al llegar al final de la cola.
    private func advanceAtEnd() {
        switch repeatMode {
        case .one:
            seek(to: 0)
            player.play()
        case .all:
            if index + 1 < queue.count {
                next()
            } else if !queue.isEmpty {
                index = 0
                Task { await load(trackAt: 0, autoPlay: true) }
            }
        case .off:
            next()
        }
    }

    // MARK: - Favoritos

    /// Consulta al backend si el tema actual esta en favoritos.
    private func refreshFavorite(for track: Track) async {
        guard !track.videoId.isEmpty else {
            isFavorite = false
            return
        }
        let value = try? await api.isFavorite(videoId: track.videoId)
        guard currentTrack?.videoId == track.videoId else { return }
        isFavorite = value?.boolValue ?? false
    }

    /// Optimista: se pinta el corazon al instante y se manda al backend por detras,
    /// igual que `PlayerViewModel.toggleFavorite` en Android.
    func toggleFavorite() {
        guard let track = currentTrack, !track.videoId.isEmpty else { return }
        let wasFavorite = isFavorite
        isFavorite = !wasFavorite

        Task {
            if wasFavorite {
                try? await api.removeFavorite(videoId: track.videoId)
            } else {
                try? await api.addFavorite(
                    request: AddFavoriteRequest(
                        title: track.title,
                        artist: track.displayArtist,
                        thumbnailUrl: track.displayThumbnail,
                        duration: track.duration,
                        videoId: track.videoId
                    )
                )
            }
        }
    }

    /// El historial es lo que alimenta "Mas escuchadas" y todo el recomendador, asi que
    /// se registra en cuanto empieza a cargarse el tema — igual que `PlayerViewModel`.
    private func recordHistory(for track: Track) {
        guard !track.videoId.isEmpty else { return }
        Task {
            try? await api.addHistory(
                request: AddHistoryRequest(
                    title: track.title,
                    artist: track.displayArtist,
                    thumbnailUrl: track.displayThumbnail,
                    duration: track.duration,
                    videoId: track.videoId,
                    isDownloaded: false
                )
            )
        }
    }

    // MARK: - Carga de un tema

    private func load(trackAt i: Int, autoPlay: Bool) async {
        guard queue.indices.contains(i) else { return }
        let track = queue[i]
        let token = UUID()
        loadToken = token

        currentTrack = track
        isBuffering = true
        errorMessage = nil
        position = 0
        duration = Double(track.duration)
        stallWatchdog?.cancel()

        recordHistory(for: track)
        Task { await self.refreshFavorite(for: track) }

        let item: AVPlayerItem?
        if let local = DownloadStore.shared.localURL(for: track.videoId) {
            // DESCARGADO: se reproduce del disco y no se toca la red. Vale para CUALQUIER
            // lista —favoritos, historial, radio, recomendadas—, no solo para la pestaña
            // Descargas: al ir por videoId, cualquier cola aprovecha el archivo local.
            // En Android eso no pasa, porque alli lo descargado se identifica por su URI
            // de MediaStore y solo se reproduce desde su propia pestaña.
            item = makeItem(url: local)
            currentStreamSummary = "archivo descargado"
        } else if let ready = preparedNext, ready.videoId == track.videoId {
            // Ya resuelto y con la cabecera descargada: arranca practicamente al instante.
            item = ready.item
            currentStreamSummary = "precargado"
        } else if let url = await resolveURL(for: track) {
            item = makeItem(url: url)
        } else {
            item = nil
        }
        preparedNext = nil

        // Si mientras se resolvia la URL el usuario cambio de cancion, descartamos.
        guard loadToken == token else { return }

        guard let item else {
            // Igual que `onPlayerError` en Android: una pista que no resuelve no corta
            // la reproduccion, se salta a la siguiente.
            handleFailure("No se pudo obtener el audio de «\(track.title)». \(currentStreamSummary)")
            return
        }

        observeStatus(of: item, token: token, track: track)
        player.replaceCurrentItem(with: item)

        // Solo se da la orden. `isPlaying` lo pone el observador de timeControlStatus
        // cuando el reproductor arranca de verdad — marcarlo aqui a mano hacia que la
        // UI y el audio se desincronizaran.
        if autoPlay { player.play() }

        startStallWatchdog(token: token)
        updateNowPlayingInfo(for: track)

        // Ninguna de las dos debe retrasar el arranque: van en su propia tarea.
        Task { await self.prepareNext(token: token) }
        Task { await self.extendRadioIfNeeded() }
    }

    /// Construye el item pidiendole a AVFoundation que no precalcule la duracion exacta
    /// (obliga a leer el fichero entero en algunos contenedores) y que se conforme con
    /// unos segundos de buffer por delante en vez de tirar de todo el tema.
    private func makeItem(url: URL) -> AVPlayerItem {
        let asset = AVURLAsset(
            url: url,
            options: [AVURLAssetPreferPreciseDurationAndTimingKey: false]
        )
        let item = AVPlayerItem(asset: asset)
        item.preferredForwardBufferDuration = 5
        return item
    }

    /// Backend propio y, si falla, las instancias publicas de Piped — todo eso ya lo
    /// resuelve `EMusicNetworkClient` del modulo compartido.
    private func resolveURL(for track: Track) async -> URL? {
        do {
            guard let info = try await network.getStream(videoId: track.videoId) else {
                currentStreamSummary = "el servidor no devolvió streams"
                return nil
            }
            currentStreamSummary = "formatos: \(info.audioStreamsDescription)"
            return info.bestAudioURL
        } catch {
            currentStreamSummary = "error de red: \(error.localizedDescription)"
            return nil
        }
    }

    /// Deja el siguiente tema listo del todo: URL resuelta e item creado con su cabecera
    /// ya descargada. Es lo que en Android hace ExoPlayer solo al tener la cola entera
    /// encolada; aca hay que hacerlo a mano porque solo tenemos un AVPlayer.
    private func prepareNext(token: UUID) async {
        let nextIndex = index + 1
        guard queue.indices.contains(nextIndex) else { return }
        let nextTrack = queue[nextIndex]

        // Si el siguiente ya esta en disco no hay nada que resolver: se prepara directo.
        if let local = DownloadStore.shared.localURL(for: nextTrack.videoId) {
            let item = makeItem(url: local)
            guard loadToken == token else { return }
            preparedNext = (nextTrack.videoId, item)
            return
        }

        // `resolveURL` escribe currentStreamSummary, que aqui hablaria del tema
        // equivocado; se guarda y se restaura.
        let summary = currentStreamSummary
        let url = await resolveURL(for: nextTrack)
        currentStreamSummary = summary

        guard loadToken == token, let url else { return }

        let item = makeItem(url: url)
        preparedNext = (nextTrack.videoId, item)

        // Forzar la carga de la cabecera ahora, para que el cambio de cancion no
        // tenga que abrir la conexion desde cero.
        if let asset = item.asset as? AVURLAsset {
            Task { _ = try? await asset.load(.isPlayable) }
        }
    }

    // MARK: - Diagnostico de la reproduccion

    /// Sin esto un formato que iOS no puede decodificar (tipico: Opus/WebM) deja el
    /// reproductor girando para siempre y sin mensaje.
    private func observeStatus(of item: AVPlayerItem, token: UUID, track: Track) {
        itemStatusCancellable = item.publisher(for: \.status)
            .receive(on: DispatchQueue.main)
            .sink { [weak self] status in
                guard let self, self.loadToken == token else { return }
                switch status {
                case .readyToPlay:
                    // El buffering NO se apaga aca: de eso se encarga timeControlStatus,
                    // que es quien sabe si ademas hay datos suficientes para sonar.
                    if let itemDuration = item.duration.seconds.isFinite ? item.duration.seconds : nil,
                       itemDuration > 0 {
                        self.duration = itemDuration
                    }
                case .failed:
                    let reason = item.error?.localizedDescription ?? "formato no soportado"
                    self.handleFailure("No se pudo reproducir «\(track.title)»: \(reason). \(self.currentStreamSummary)")
                default:
                    break
                }
            }
    }

    /// Si a los 15 s no arrancó ni falló, avisamos en vez de girar indefinidamente.
    /// La condicion mira el estado REAL del reproductor: si esta sonando no hay nada
    /// que reportar, por lento que haya sido el arranque.
    private func startStallWatchdog(token: UUID) {
        stallWatchdog = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 15_000_000_000)
            guard let self, !Task.isCancelled, self.loadToken == token else { return }
            guard self.player.timeControlStatus != .playing else { return }
            self.handleFailure("El audio no empezó a sonar. \(self.currentStreamSummary)")
        }
    }

    /// Puerto de `onPlayerError` en `MusicService.kt`: si una pista falla (no resuelve,
    /// formato ilegible, se corta la red) se salta a la siguiente en vez de parar la
    /// musica. Acotado al tamaño de la cola para no entrar en bucle si la red esta caida,
    /// y `autoSkipCount` vuelve a cero en cuanto algo suena de verdad.
    private func handleFailure(_ message: String) {
        if index + 1 < queue.count, autoSkipCount < queue.count {
            autoSkipCount += 1
            stallWatchdog?.cancel()
            next()
            return
        }
        fail(message)
    }

    /// Ante un fallo se para el reproductor de verdad, para que el estado que ve el
    /// usuario y lo que suena no puedan divergir.
    private func fail(_ message: String) {
        player.pause()
        isBuffering = false
        isPlaying = false
        errorMessage = message
    }

    // MARK: - AVPlayer

    private func configureAudioSession() {
        do {
            // `.playback` permite seguir sonando con la pantalla bloqueada y en segundo plano
            // (requiere ademas UIBackgroundModes=audio en Info.plist).
            try AVAudioSession.sharedInstance().setCategory(.playback, mode: .default)
            try AVAudioSession.sharedInstance().setActive(true)
        } catch {
            errorMessage = "No se pudo configurar el audio: \(error.localizedDescription)"
        }
    }

    private func observePlayer() {
        // FUENTE DE VERDAD del estado de reproduccion. AVPlayer publica aqui lo que
        // realmente esta haciendo, asi que el boton del reproductor nunca puede quedar
        // mostrando play mientras suena musica (ni al reves).
        timeControlCancellable = player.publisher(for: \.timeControlStatus)
            .receive(on: DispatchQueue.main)
            .sink { [weak self] status in
                guard let self else { return }
                switch status {
                case .playing:
                    self.isPlaying = true
                    self.isBuffering = false
                    self.stallWatchdog?.cancel()
                    // Sonó: se olvida la racha de saltos por error (STATE_READY en Android).
                    self.autoSkipCount = 0
                case .waitingToPlayAtSpecifiedRate:
                    // Quiere sonar pero le faltan datos: eso SI es buffering real.
                    self.isPlaying = false
                    self.isBuffering = self.currentTrack != nil
                case .paused:
                    self.isPlaying = false
                @unknown default:
                    break
                }
                self.updateNowPlayingPlaybackState()
            }

        let interval = CMTime(seconds: 0.5, preferredTimescale: 600)
        timeObserver = player.addPeriodicTimeObserver(forInterval: interval, queue: .main) { [weak self] time in
            guard let self else { return }
            Task { @MainActor in
                self.position = time.seconds.isFinite ? time.seconds : 0
                if let itemDuration = self.player.currentItem?.duration.seconds,
                   itemDuration.isFinite, itemDuration > 0 {
                    self.duration = itemDuration
                }
                self.updateNowPlayingPlaybackState()
            }
        }

        endObserver = NotificationCenter.default.addObserver(
            forName: .AVPlayerItemDidPlayToEndTime,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            Task { @MainActor in self?.advanceAtEnd() }
        }

        failObserver = NotificationCenter.default.addObserver(
            forName: .AVPlayerItemFailedToPlayToEndTime,
            object: nil,
            queue: .main
        ) { [weak self] note in
            let error = note.userInfo?[AVPlayerItemFailedToPlayToEndTimeErrorKey] as? Error
            Task { @MainActor in
                self?.handleFailure("Se cortó la reproducción: \(error?.localizedDescription ?? "error desconocido")")
            }
        }
    }

    // MARK: - Modo radio

    /// Mismo contrato que `MusicService.maybeExtendRadio` en Android: se le pregunta al
    /// `RadioEngine` compartido si hay que estirar la cola y se encola lo que devuelva.
    private func extendRadioIfNeeded() async {
        guard radio.isActive else { return }
        let remaining = Int32(queue.count - index)
        guard radio.shouldExtend(remainingInQueue: remaining) else { return }

        let seedArtist = currentTrack?.displayArtist ?? ""
        let existingIds = Set(queue.map(\.videoId))
        do {
            let batch = try await radio.nextBatch(seedArtist: seedArtist, existingIds: existingIds)
            guard !batch.isEmpty else { return }
            queue.append(contentsOf: batch)
        } catch {
            // Un fallo de red no debe cortar la reproduccion, igual que en Android.
        }
    }

    // MARK: - Pantalla bloqueada / centro de control

    private func configureRemoteCommands() {
        let center = MPRemoteCommandCenter.shared()

        center.playCommand.addTarget { [weak self] _ in
            guard let self, !self.isPlaying else { return .commandFailed }
            self.togglePlayPause()
            return .success
        }
        center.pauseCommand.addTarget { [weak self] _ in
            guard let self, self.isPlaying else { return .commandFailed }
            self.togglePlayPause()
            return .success
        }
        center.nextTrackCommand.addTarget { [weak self] _ in
            self?.next()
            return .success
        }
        center.previousTrackCommand.addTarget { [weak self] _ in
            self?.previous()
            return .success
        }
        center.changePlaybackPositionCommand.addTarget { [weak self] event in
            guard let self, let event = event as? MPChangePlaybackPositionCommandEvent else {
                return .commandFailed
            }
            self.seek(to: event.positionTime)
            return .success
        }
    }

    private func updateNowPlayingInfo(for track: Track) {
        var info: [String: Any] = [
            MPMediaItemPropertyTitle: track.title,
            MPMediaItemPropertyArtist: track.displayArtist,
            MPMediaItemPropertyPlaybackDuration: duration,
            MPNowPlayingInfoPropertyElapsedPlaybackTime: position,
            MPNowPlayingInfoPropertyPlaybackRate: isPlaying ? 1.0 : 0.0,
        ]
        MPNowPlayingInfoCenter.default().nowPlayingInfo = info

        let thumbnail = track.hqThumbnail
        Task {
            guard let url = URL(string: thumbnail),
                  let (data, _) = try? await URLSession.shared.data(from: url),
                  let image = UIImage(data: data) else { return }
            let artwork = MPMediaItemArtwork(boundsSize: image.size) { _ in image }
            info[MPMediaItemPropertyArtwork] = artwork
            if self.currentTrack?.videoId == track.videoId {
                MPNowPlayingInfoCenter.default().nowPlayingInfo = info
            }
        }
    }

    private func updateNowPlayingPlaybackState() {
        guard var info = MPNowPlayingInfoCenter.default().nowPlayingInfo else { return }
        info[MPNowPlayingInfoPropertyElapsedPlaybackTime] = position
        info[MPMediaItemPropertyPlaybackDuration] = duration
        info[MPNowPlayingInfoPropertyPlaybackRate] = isPlaying ? 1.0 : 0.0
        MPNowPlayingInfoCenter.default().nowPlayingInfo = info
    }
}
