package com.emusic.shared

import android.os.Build

actual fun currentPlatformName(): String = "Android ${Build.VERSION.SDK_INT}"
