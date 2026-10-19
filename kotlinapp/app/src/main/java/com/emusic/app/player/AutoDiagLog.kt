package com.emusic.app.player

import android.content.Context
import android.util.Log
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * Registro de diagnóstico de lo que pide Android Auto (raíces, hijos, tiempos), en
 * un archivo de la app que sobrevive a la desconexión del coche. El logcat del
 * teléfono rota rápido; esto permite revisar una prueba hecha en el coche horas
 * después con `adb exec-out run-as com.emusic.app cat files/auto_diag.log`.
 * Se queda con las últimas [MAX_LINES] líneas.
 */
class AutoDiagLog(context: Context) {

    private val file = File(context.filesDir, "auto_diag.log")
    private val format = SimpleDateFormat("MM-dd HH:mm:ss.SSS", Locale.US)

    @Synchronized
    fun log(message: String) {
        Log.d("AutoDiag", message)
        try {
            val lines = if (file.exists()) file.readLines() else emptyList()
            val updated = (lines + "${format.format(Date())} $message").takeLast(MAX_LINES)
            file.writeText(updated.joinToString("\n", postfix = "\n"))
        } catch (e: Exception) {
            Log.e("AutoDiag", "No se pudo escribir el registro", e)
        }
    }

    private companion object { const val MAX_LINES = 400 }
}
