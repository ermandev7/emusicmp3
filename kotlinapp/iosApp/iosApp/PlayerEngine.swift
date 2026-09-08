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

    /// Progreso 0...1 para la barrita del mini reproductor.
    var progress: Double {
        guard duration > 0 else { return 0 }
        return min(max(position / duration, 0), 1)
    }

    // MARK: Cola

    private(set) var queue: [Track] = []
    private(set) var index: Int = 0

    private let player = AVPlayer()
    private let network = SharedClients.shared.network
    private let radio = SharedClients.shared.radio

    /// URL ya resuelta del proximo tema, para no esperar la llamada de red al pasar.
    private var prefetchedNextURL: (videoId: String, url: URL)?

    private var timeObserver: Any?
    private var endObserver: NSObjectProtocol?
    private var failObserver: NSObjectProtocol?
    private var itemStatusCancellable: AnyCancellable?
    private var stallWatchdog: Task<Void, Never>?

    /// Formato elegido para el tema actual; se usa en los mensajes de error.
    private var currentStreamSummary: String = ""

    /// Identifica la carga en curso: si el usuario toca otra cancion mientras se resuelve
    /// la anterior, la respuesta vieja se descarta.
    private var loadToken = UUID()

    private init() {
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
        Task { await load(trackAt: index, autoPlay: true) }
    }

    func togglePlayPause() {
        guard currentTrack != nil else { return }
        if isPlaying {
            player.pause()
            isPlaying = false
        } else {
            player.play()
            isPlaying = true
        }
        updateNowPlayingPlaybackState()
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

        let url: URL?
        if let cached = prefetchedNextURL, cached.videoId == track.videoId {
            url = cached.url
            currentStreamSummary = "prefetch"
        } else {
            url = await resolveURL(for: track)
        }
        prefetchedNextURL = nil

        // Si mientras se resolvia la URL el usuario cambio de cancion, descartamos.
        guard loadToken == token else { return }

        guard let url else {
            fail("No se pudo obtener el audio de «\(track.title)». \(currentStreamSummary)")
            return
        }

        let item = AVPlayerItem(url: url)
        observeStatus(of: item, token: token, track: track)
        player.replaceCurrentItem(with: item)

        if autoPlay {
            player.play()
            isPlaying = true
        }

        startStallWatchdog(token: token)
        updateNowPlayingInfo(for: track)
        await prefetchNext()
        await extendRadioIfNeeded()
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

    private func prefetchNext() async {
        let nextIndex = index + 1
        guard queue.indices.contains(nextIndex) else { return }
        let nextTrack = queue[nextIndex]
        guard let url = await resolveURL(for: nextTrack) else { return }
        prefetchedNextURL = (nextTrack.videoId, url)
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
                    self.isBuffering = false
                    self.stallWatchdog?.cancel()
                    if let itemDuration = item.duration.seconds.isFinite ? item.duration.seconds : nil,
                       itemDuration > 0 {
                        self.duration = itemDuration
                    }
                case .failed:
                    let reason = item.error?.localizedDescription ?? "formato no soportado"
                    self.fail("No se pudo reproducir «\(track.title)»: \(reason). \(self.currentStreamSummary)")
                default:
                    break
                }
            }
    }

    /// Si a los 15 s no arrancó ni falló, avisamos igual en vez de girar indefinidamente.
    private func startStallWatchdog(token: UUID) {
        stallWatchdog = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 15_000_000_000)
            guard let self, !Task.isCancelled, self.loadToken == token, self.isBuffering else { return }
            self.fail("El audio no empezó a sonar. \(self.currentStreamSummary)")
        }
    }

    private func fail(_ message: String) {
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
            Task { @MainActor in self?.next() }
        }

        failObserver = NotificationCenter.default.addObserver(
            forName: .AVPlayerItemFailedToPlayToEndTime,
            object: nil,
            queue: .main
        ) { [weak self] note in
            let error = note.userInfo?[AVPlayerItemFailedToPlayToEndTimeErrorKey] as? Error
            Task { @MainActor in
                self?.fail("Se cortó la reproducción: \(error?.localizedDescription ?? "error desconocido")")
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
