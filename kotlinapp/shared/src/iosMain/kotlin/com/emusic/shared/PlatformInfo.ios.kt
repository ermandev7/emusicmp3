package com.emusic.shared

import platform.UIKit.UIDevice

actual fun currentPlatformName(): String =
    UIDevice.currentDevice.systemName() + " " + UIDevice.currentDevice.systemVersion
