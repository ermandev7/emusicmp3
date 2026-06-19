package com.emusic.app

import android.app.Application
import android.util.Log
import dagger.hilt.android.HiltAndroidApp
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

@HiltAndroidApp
class EMusicApp : Application() {

    override fun onCreate() {
        super.onCreate()
        installCrashLogger()
    }

    /**
     * Guarda los crashes no capturados en files/crash_log.txt para poder
     * diagnosticar fallos que ocurren sin el depurador conectado (ej. Android Auto).
     */
    private fun installCrashLogger() {
        val previous = Thread.getDefaultUncaughtExceptionHandler()
        Thread.setDefaultUncaughtExceptionHandler { thread, throwable ->
            try {
                val stamp = SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.US).format(Date())
                val file = File(filesDir, "crash_log.txt")
                file.appendText(
                    "\n===== CRASH $stamp en hilo ${thread.name} =====\n" +
                        Log.getStackTraceString(throwable) + "\n"
                )
            } catch (_: Exception) {
                // No empeorar el crash
            }
            previous?.uncaughtException(thread, throwable)
        }
    }
}
