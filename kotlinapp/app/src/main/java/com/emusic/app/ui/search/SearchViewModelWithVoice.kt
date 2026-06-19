package com.emusic.app.ui.search

import androidx.lifecycle.ViewModel
import com.emusic.app.voice.VoiceAssistant
import dagger.hilt.android.lifecycle.HiltViewModel
import javax.inject.Inject

@HiltViewModel
class SearchViewModelWithVoice @Inject constructor(
    val voiceAssistant: VoiceAssistant
) : ViewModel()
