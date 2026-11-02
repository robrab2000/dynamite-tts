"""Speech lifecycle: cleanup, optional Summary / Explain through the chat model, then streamed Kokoro playback.

All public methods are called on the GTK main thread. Network work runs on a worker
thread; results hop back through GLib.idle_add. A generation counter makes stale
callbacks from a cancelled request harmless.
"""
from __future__ import annotations

import threading

from gi.repository import GLib

from . import applog, text as textutil
from .lemonade import KOKORO_MODEL, LemonadeClient, LemonadeError, PCMStream
from .phonon import PhononError, PhononStream
from .playback import PCMPlayer, PlaybackError
from .settings import SettingsStore, SpeakMode


class _Cancelled(Exception):
    pass


class SpeechSession:
    def __init__(self, store: SettingsStore):
        self.store = store
        self.player = PCMPlayer()
        self.on_phase = None  # callable(str | None)
        self.on_notice = None  # callable(str)
        self._client = LemonadeClient()
        self._stream: PCMStream | PhononStream | None = None
        self._generation = 0
        self._stream_finished = False
        self._received_audio = False
        self.player.on_drained = lambda: GLib.idle_add(self._drained)

    # -- public ----------------------------------------------------------------
    def speak(self, raw: str, context: str | None = None, mode: SpeakMode | None = None) -> None:
        text = textutil.sanitize(raw)
        if not text:
            self._notice("No text selected")
            return
        self.stop(play_chime=False)
        self._generation += 1
        token = self._generation
        active_mode = mode or self.store.current.speak_mode
        current = self.store.current
        applog.write(
            f"speak chars={len(text)} mode={active_mode.value} engine={current.engine.value} "
            f"voice={current.phonon_voice if current.uses_phonon else current.voice}"
        )
        threading.Thread(
            target=self._run_guarded, args=(text, context, active_mode, token), name="speech", daemon=True
        ).start()

    def stop(self, play_chime: bool) -> None:
        self._generation += 1
        if self._stream is not None:
            self._stream.cancel()
            self._stream = None
        chime = play_chime and self.store.current.play_sound_on_stop and self._received_audio
        self.player.stop(play_chime=chime)
        self._received_audio = False
        self._stream_finished = False
        self._phase(None)

    # -- worker ----------------------------------------------------------------
    def _run_guarded(self, text: str, context: str | None, mode: SpeakMode, token: int) -> None:
        try:
            self._run(text, context, mode, token)
        except _Cancelled:
            return
        except (LemonadeError, PhononError, PlaybackError) as error:
            applog.write(f"speak failed: {error}")
            self._fail(token, str(error))
        except Exception as error:  # noqa: BLE001 - keep the app alive and report
            applog.write(f"speak crashed: {error!r}")
            self._fail(token, f"Unexpected error: {error}")

    def _check(self, token: int) -> None:
        if token != self._generation:
            raise _Cancelled()

    def _run(self, text: str, context: str | None, mode: SpeakMode, token: int) -> None:
        settings = self.store.current
        base_url = settings.base_url
        needs_lemonade = mode in (SpeakMode.SUMMARY, SpeakMode.EXPLAIN) or not settings.uses_phonon
        if needs_lemonade:
            if not base_url.startswith(("http://", "https://")):
                raise LemonadeError("The Lemonade address is not a valid URL.")
            self._phase_async(token, "Starting Lemonade…")
            self._client.ensure_server(base_url, cancelled=lambda: token != self._generation)
            self._check(token)
            applog.write(f"lemonade ready {base_url}")

        spoken = text
        if mode in (SpeakMode.SUMMARY, SpeakMode.EXPLAIN):
            self._phase_async(token, "Summarizing…" if mode == SpeakMode.SUMMARY else "Explaining…")
            model = self._client.pick_chat_model(base_url, settings.chat_model)
            self._client.ensure_model(base_url, model)
            self._check(token)
            if mode == SpeakMode.SUMMARY:
                spoken = self._client.chat(base_url, model, textutil.SUMMARY_PROMPT, text, None)
            else:
                sentences = settings.clamped_explain_sentences
                spoken = self._client.chat(
                    base_url,
                    model,
                    textutil.explain_prompt(settings.explain_audience, sentences),
                    textutil.explain_user_message(text, context),
                    max(128, sentences * 96),
                )
            spoken = textutil.sanitize(spoken)
            if not spoken:
                raise LemonadeError("The chat model returned nothing to speak.")

        self._check(token)
        self._phase_async(token, "Synthesizing…")
        if settings.uses_phonon:
            GLib.idle_add(self._start_phonon_stream, token, spoken, settings)
            return
        self._client.ensure_model(base_url, KOKORO_MODEL)
        self._check(token)
        applog.write("kokoro ready")
        GLib.idle_add(self._start_stream, token, spoken, settings.voice, settings.speed, settings.audio_device)

    # -- main thread ------------------------------------------------------------
    def _start_stream(self, token: int, spoken: str, voice: str, speed: float, device: str) -> bool:
        if token != self._generation:
            return False
        applog.write(f"player prepare device={device or 'default'}")
        try:
            self.player.prepare(device)
        except PlaybackError as error:
            self._fail(token, str(error))
            return False
        self._stream_finished = False
        self._received_audio = False

        first = [True]

        def on_pcm(data: bytes) -> None:
            if token != self._generation:
                return
            self.player.enqueue(data)
            if first[0]:
                first[0] = False
                GLib.idle_add(self._first_audio, token, len(data))

        def on_complete(error) -> None:
            GLib.idle_add(self._complete, token, error)

        stream = PCMStream(on_pcm, on_complete)
        self._stream = stream
        applog.write(f"speech request chars={len(spoken)} voice={voice} speed={speed}")
        stream.start(self.store.current.base_url, spoken, voice, speed)
        return False

    def _start_phonon_stream(self, token: int, spoken: str, settings) -> bool:
        if token != self._generation:
            return False
        applog.write(f"player prepare device={settings.audio_device or 'default'}")
        try:
            self.player.prepare(settings.audio_device)
        except PlaybackError as error:
            self._fail(token, str(error))
            return False
        self._stream_finished = False
        self._received_audio = False

        first = [True]

        def on_pcm(data: bytes) -> None:
            if token != self._generation:
                return
            self.player.enqueue(data)
            if first[0]:
                first[0] = False
                GLib.idle_add(self._first_audio, token, len(data))

        def on_complete(error) -> None:
            GLib.idle_add(self._complete, token, error)

        stream = PhononStream(on_pcm, on_complete)
        self._stream = stream
        applog.write(
            f"phonon request chars={len(spoken)} voice={settings.phonon_voice} speed={settings.speed} lang={settings.phonon_language}"
        )
        stream.start(spoken, settings.phonon_voice, settings.speed, settings.phonon_language, settings.phonon_model_dir)
        return False

    def _first_audio(self, token: int, size: int) -> bool:
        if token != self._generation:
            return False
        self._received_audio = True
        applog.write(f"first audio bytes={size}")
        self._phase("Speaking")
        return False

    def _complete(self, token: int, error) -> bool:
        if token != self._generation:
            return False
        self._stream_finished = True
        if error is not None:
            self._notice(str(error))
            self.player.stop(play_chime=False)
            self._phase(None)
        elif not self._received_audio:
            self._notice("Phonon returned no audio." if self.store.current.uses_phonon else "Lemonade returned no audio.")
            self.player.stop(play_chime=False)
            self._phase(None)
        else:
            self.player.finish()
        return False

    def _drained(self) -> bool:
        if self._stream_finished:
            applog.write("playback drained")
            self._received_audio = False
            self._stream_finished = False
            self._phase(None)
        return False

    def _fail(self, token: int, message: str) -> None:
        def report() -> bool:
            if token == self._generation:
                self._notice(message)
                self._phase(None)
            return False

        GLib.idle_add(report)

    def _phase_async(self, token: int, phase: str | None) -> None:
        def report() -> bool:
            if token == self._generation:
                self._phase(phase)
            return False

        GLib.idle_add(report)

    def _phase(self, phase: str | None) -> None:
        if self.on_phase:
            self.on_phase(phase)

    def _notice(self, message: str) -> None:
        if self.on_notice:
            self.on_notice(message)
