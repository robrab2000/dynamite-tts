# Metal Kokoro on this M1 Max

Host: Apple M1 Max, 10 cores (8 performance), 64 GB, macOS 27.0.1.
Server: Lemonade 2026.42.1 embeddable, `lemond` on `127.0.0.1:13305`.
Backend log: `KokoroServer Setting LD_LIBRARY_PATH=.../kokoro/metal`.
Request: `POST /api/v1/audio/speech` with `stream_format=audio`, `response_format=pcm`, voice `af_heart`.
Response: `audio/l16;rate=24000;endianness=little-endian`.

| Run | First PCM | Wall time | Audio | Realtime |
|---|---:|---:|---:|---:|
| Warm short sentence | 450–610 ms | ~0.6 s | 2.0–2.5 s | ~4.1x |
| ~30 s paragraph | 823–888 ms | 4.0–4.3 s | 31.3 s | ~7.5x |
| Cancel after 0.4 s, then a new sentence | next request still returns audio (about 1.4 s, the server was still finishing the cancelled clip) | | 2.0 s | |

Windows in-process CPU, for scale: about 0.35 s to first audio and about 5x realtime. This Metal stream starts a little later and then runs ahead of that CPU path. It is the verbatim engine. An in-process MLX port was not added.

Chat for Summary and Explain is `Llama-3.2-1B-Instruct-GGUF` with `LlamaCpp Backend: metal`. The Windows default `Qwen2.5-0.5B-Instruct` is a Ryzen NPU recipe and is not in the Mac catalog.
