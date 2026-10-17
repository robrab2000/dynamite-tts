#!/usr/bin/env python3
"""Time Lemonade's streaming Kokoro endpoint on this Mac.

Writes nothing. Prints time to the first PCM byte, how long the body took,
and how much audio came back. A second short request checks that Stop-style
cancellation (closing the stream) does not wedge the server.
"""

import json
import sys
import time
import urllib.request

URL = "http://127.0.0.1:13305/api/v1/audio/speech"
PARAGRAPH = (
    "The quick brown fox jumps over the lazy dog. This is a benchmark of local text to speech synthesis. "
    "We want to know how long it takes before the first audio segment is ready, and how long the whole "
    "paragraph takes to render. A typical highlighted paragraph from a web page is roughly this long, "
    "with a handful of sentences of varying length, some short, and some that run on for a little while "
    "before finally coming to an end. That should be enough text to make the numbers meaningful."
)


def run(label: str, text: str, cancel_after: float | None = None) -> None:
    body = json.dumps(
        {
            "model": "kokoro-v1",
            "input": text,
            "voice": "af_heart",
            "speed": 1.0,
            "stream_format": "audio",
            "response_format": "pcm",
        }
    ).encode()
    request = urllib.request.Request(URL, data=body, headers={"Content-Type": "application/json"})
    started = time.perf_counter()
    first = None
    total = 0
    with urllib.request.urlopen(request, timeout=180) as response:
        content_type = response.headers.get("Content-Type", "")
        while True:
            chunk = response.read(8192)
            if not chunk:
                break
            if first is None:
                first = time.perf_counter() - started
                print(f"{label} content_type={content_type} first_byte_ms={first * 1000:.0f}")
            total += len(chunk)
            if cancel_after is not None and (time.perf_counter() - started) > cancel_after:
                print(f"{label} cancelled_after_s={cancel_after:.1f} bytes={total}")
                return
    elapsed = time.perf_counter() - started
    audio = total / 2 / 24000
    realtime = audio / elapsed if elapsed else 0
    print(
        f"{label} total_ms={elapsed * 1000:.0f} bytes={total} "
        f"audio_s={audio:.2f} realtime={realtime:.2f}"
    )


def main() -> None:
    run("warm-short", "Hello from Dynamite on this Mac.")
    run("paragraph", PARAGRAPH)
    run("cancel", PARAGRAPH, cancel_after=0.4)
    run("after-cancel", "Stop, then speak again.")


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(f"spike failed: {error}", file=sys.stderr)
        sys.exit(1)
