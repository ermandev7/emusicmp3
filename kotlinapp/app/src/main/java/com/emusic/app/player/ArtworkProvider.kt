package com.emusic.app.player

import android.content.ContentProvider
import android.content.ContentValues
import android.database.Cursor
import android.net.Uri
import android.os.ParcelFileDescriptor
import android.util.Base64
import android.util.Log
import java.io.File
import java.io.FileNotFoundException
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest

/**
 * Sirve las carátulas a Android Auto como `content://`. Auto no carga artwork desde
 * `https://`: solo acepta URIs locales (content:// o android.resource://), así que
 * con la URL de YouTube las listas y la tarjeta de sugerencias salían sin carátula
 * o directamente vacías.
 *
 * La URI se construye al instante ([contentUri]) y la imagen se descarga la primera
 * vez que Auto la abre ([openFile], que puede bloquear: Auto muestra un "cargando"
 * mientras). Se cachea en disco. Solo hosts de imágenes de YouTube/Google, para que
 * el provider (exportado, Auto tiene que poder leerlo) no sirva para descargar
 * cualquier cosa.
 */
class ArtworkProvider : ContentProvider() {

    companion object {
        const val AUTHORITY = "com.emusic.app.artwork"
        private const val TAG = "ArtworkProvider"
        private val ALLOWED_HOST_SUFFIXES = listOf("ytimg.com", "ggpht.com", "googleusercontent.com")

        /** https://i.ytimg.com/vi/VIDEO_ID/CALIDAD.jpg → grupo 1 = hasta el VIDEO_ID. */
        private val YT_THUMB = Regex("""^(https?://i\d*\.ytimg\.com/vi(?:_webp)?/[^/]+)/[a-z]+default\.(?:jpg|webp)""")

        /** content:// equivalente a [remoteUrl], o null si no hay o no es de un host permitido. */
        fun contentUri(remoteUrl: String?): Uri? {
            if (remoteUrl.isNullOrBlank() || !isAllowed(remoteUrl)) return null
            val encoded = Base64.encodeToString(
                remoteUrl.toByteArray(), Base64.URL_SAFE or Base64.NO_WRAP or Base64.NO_PADDING
            )
            return Uri.Builder().scheme("content").authority(AUTHORITY).appendPath(encoded).build()
        }

        private fun isAllowed(url: String): Boolean = try {
            val u = URL(url)
            (u.protocol == "https" || u.protocol == "http") &&
                ALLOWED_HOST_SUFFIXES.any { u.host == it || u.host.endsWith(".$it") }
        } catch (_: Exception) {
            false
        }
    }

    override fun onCreate(): Boolean = true

    override fun openFile(uri: Uri, mode: String): ParcelFileDescriptor {
        val encoded = uri.lastPathSegment ?: throw FileNotFoundException("Sin ruta")
        val remote = try {
            String(Base64.decode(encoded, Base64.URL_SAFE or Base64.NO_WRAP or Base64.NO_PADDING))
        } catch (e: IllegalArgumentException) {
            throw FileNotFoundException("URI de carátula inválida")
        }
        if (!isAllowed(remote)) throw FileNotFoundException("Host no permitido")
        AutoDiagLog(context!!).log("Carátula pedida por ${callingPackage ?: "?"}: ${remote.takeLast(40)}")

        // "artwork-best": la caché anterior ("artwork") tenía versiones de 480×360.
        val dir = File(context!!.cacheDir, "artwork-best").apply { mkdirs() }
        val file = File(dir, sha1(remote))
        if (!file.exists() || file.length() == 0L) downloadBest(remote, file)
        return ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY)
    }

    /**
     * Carátulas de YouTube (i.ytimg.com/vi/ID/CALIDAD.jpg): se intenta la mejor
     * resolución y se baja si no existe. maxresdefault (1280×720) falta en muchos
     * vídeos (404); hqdefault (480×360) existe siempre pero en la tarjeta grande de
     * Android Auto se veía borrosa.
     */
    private fun downloadBest(remote: String, target: File) {
        val m = YT_THUMB.find(remote)
        if (m == null) {
            download(remote, target)
            return
        }
        val base = m.groupValues[1]
        var last: Exception? = null
        for (quality in listOf("maxresdefault", "sddefault", "hqdefault")) {
            try {
                download("$base/$quality.jpg", target)
                return
            } catch (e: FileNotFoundException) {
                last = e
            }
        }
        throw last ?: FileNotFoundException("Sin carátula")
    }

    private fun download(remote: String, target: File) {
        val tmp = File(target.parentFile, target.name + ".tmp")
        try {
            val conn = URL(remote).openConnection() as HttpURLConnection
            conn.connectTimeout = 10_000
            conn.readTimeout = 10_000
            conn.inputStream.use { input -> tmp.outputStream().use { input.copyTo(it) } }
            if (!tmp.renameTo(target)) throw FileNotFoundException("No se pudo guardar la carátula")
        } catch (e: Exception) {
            tmp.delete()
            Log.w(TAG, "No se pudo descargar $remote: ${e.message}")
            throw FileNotFoundException("Descarga fallida: ${e.message}")
        }
    }

    private fun sha1(s: String): String =
        MessageDigest.getInstance("SHA-1").digest(s.toByteArray()).joinToString("") { "%02x".format(it) }

    override fun getType(uri: Uri): String = "image/jpeg"
    override fun query(uri: Uri, p: Array<out String>?, s: String?, a: Array<out String>?, o: String?): Cursor? = null
    override fun insert(uri: Uri, values: ContentValues?): Uri? = null
    override fun delete(uri: Uri, s: String?, a: Array<out String>?): Int = 0
    override fun update(uri: Uri, v: ContentValues?, s: String?, a: Array<out String>?): Int = 0
}
