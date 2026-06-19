package com.emusic.app.voice

import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.speech.RecognitionListener
import android.speech.RecognizerIntent
import android.speech.SpeechRecognizer
import android.util.Log
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import javax.inject.Inject
import javax.inject.Singleton

data class VoiceState(
    val isListening: Boolean = false,
    val recognizedText: String = "",
    val command: VoiceCommand? = null,
    val commandId: Int = 0,
    val error: String? = null
)

@Singleton
class VoiceAssistant @Inject constructor(
    @ApplicationContext private val context: Context
) {
    private val _state = MutableStateFlow(VoiceState())
    val state: StateFlow<VoiceState> = _state.asStateFlow()

    private val mainHandler = Handler(Looper.getMainLooper())
    private var recognizer: SpeechRecognizer? = null
    private var isDirectSearchMode = false
    private var commandCounter = 0

    private val recognitionListener = object : RecognitionListener {
        override fun onResults(results: Bundle) {
            val matches = results.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION) ?: return
            val text = matches.firstOrNull() ?: return
            Log.d("VoiceAssistant", "onResults: '$text' directSearch=$isDirectSearchMode")
            handleRecognizedText(text)
        }

        override fun onPartialResults(partialResults: Bundle) {
            val partial = partialResults
                .getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION)
                ?.firstOrNull() ?: return
            Log.d("VoiceAssistant", "onPartial: '$partial'")
            _state.value = _state.value.copy(recognizedText = partial)
        }

        override fun onError(error: Int) {
            val msg = when (error) {
                SpeechRecognizer.ERROR_NO_MATCH -> "No se entendió, intenta de nuevo"
                SpeechRecognizer.ERROR_SPEECH_TIMEOUT -> "No se detectó voz"
                SpeechRecognizer.ERROR_AUDIO -> "Error de micrófono"
                SpeechRecognizer.ERROR_NETWORK -> "Sin conexión a internet"
                SpeechRecognizer.ERROR_NETWORK_TIMEOUT -> "Timeout de red"
                SpeechRecognizer.ERROR_CLIENT -> "Error interno"
                SpeechRecognizer.ERROR_INSUFFICIENT_PERMISSIONS -> "Permiso de micrófono denegado"
                SpeechRecognizer.ERROR_SERVER -> "Error del servidor de voz"
                SpeechRecognizer.ERROR_RECOGNIZER_BUSY -> "Reconocedor ocupado"
                else -> "Error desconocido ($error)"
            }
            Log.e("VoiceAssistant", "onError: $error - $msg")
            _state.value = _state.value.copy(isListening = false, error = msg)
            isDirectSearchMode = false
        }

        override fun onReadyForSpeech(params: Bundle) {
            Log.d("VoiceAssistant", "onReadyForSpeech")
            _state.value = _state.value.copy(isListening = true, error = null)
        }

        override fun onEndOfSpeech() {
            Log.d("VoiceAssistant", "onEndOfSpeech")
        }

        override fun onBeginningOfSpeech() {}
        override fun onRmsChanged(rmsdB: Float) {}
        override fun onBufferReceived(buffer: ByteArray?) {}
        override fun onEvent(eventType: Int, params: Bundle?) {}
    }

    fun startDirectSearch() {
        Log.d("VoiceAssistant", "startDirectSearch called")
        if (!SpeechRecognizer.isRecognitionAvailable(context)) {
            _state.value = _state.value.copy(error = "Reconocimiento de voz no disponible")
            return
        }
        isDirectSearchMode = true
        _state.value = _state.value.copy(isListening = true, recognizedText = "", error = null, command = null)
        startSpeechRecognizer()
    }

    fun startListening() {
        if (!SpeechRecognizer.isRecognitionAvailable(context)) {
            _state.value = _state.value.copy(error = "Reconocimiento de voz no disponible")
            return
        }
        isDirectSearchMode = false
        startSpeechRecognizer()
    }

    private fun startSpeechRecognizer() {
        mainHandler.post {
            try {
                recognizer?.destroy()
                recognizer = SpeechRecognizer.createSpeechRecognizer(context)
                recognizer?.setRecognitionListener(recognitionListener)

                val intent = Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH).apply {
                    putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_FREE_FORM)
                    putExtra(RecognizerIntent.EXTRA_LANGUAGE, "es-ES")
                    putExtra(RecognizerIntent.EXTRA_LANGUAGE_PREFERENCE, "es-ES")
                    putExtra(RecognizerIntent.EXTRA_PARTIAL_RESULTS, true)
                    putExtra(RecognizerIntent.EXTRA_MAX_RESULTS, 3)
                }
                recognizer?.startListening(intent)
                Log.d("VoiceAssistant", "SpeechRecognizer started OK")
            } catch (e: Exception) {
                Log.e("VoiceAssistant", "Failed to start recognizer", e)
                _state.value = _state.value.copy(
                    isListening = false,
                    error = "No se pudo iniciar el micrófono: ${e.message}"
                )
            }
        }
    }

    private fun handleRecognizedText(text: String) {
        Log.d("VoiceAssistant", "handleRecognizedText: '$text' directSearch=$isDirectSearchMode")

        if (isDirectSearchMode) {
            isDirectSearchMode = false
            commandCounter++
            // Reconocer comandos de transporte ("siguiente", "pausa", "anterior"…);
            // si no lo es, tratar la frase como búsqueda quitando el verbo
            // ("reproduce bon jovi" → query "bon jovi").
            val transport = VoiceCommandParser.parseTransport(text)
            val command = if (transport != null) VoiceCommand(transport, "", null, null)
                          else VoiceCommandParser.parseSearch(text)
            Log.d("VoiceAssistant", "Emitting command #$commandCounter: action=${command.action} query='${command.query}'")
            _state.value = VoiceState(
                isListening = false,
                recognizedText = text,
                command = command,
                commandId = commandCounter,
                error = null
            )
            return
        }

        // Modo wake word (para Android Auto / uso global)
        val afterWake = VoiceCommandParser.extractAfterWakeWord(text)
        if (afterWake != null && afterWake.isNotEmpty()) {
            commandCounter++
            val command = VoiceCommandParser.parse(afterWake)
            _state.value = VoiceState(
                isListening = false,
                recognizedText = text,
                command = command,
                commandId = commandCounter,
                error = null
            )
        } else {
            _state.value = _state.value.copy(isListening = false, recognizedText = text)
        }
    }

    fun stopListening() {
        mainHandler.post {
            try {
                recognizer?.stopListening()
                recognizer?.destroy()
            } catch (_: Exception) {}
            recognizer = null
        }
        _state.value = _state.value.copy(isListening = false)
        isDirectSearchMode = false
    }

    fun clearCommand() {
        _state.value = _state.value.copy(command = null)
    }

    fun destroy() {
        stopListening()
    }
}
