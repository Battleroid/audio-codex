# whisper.cpp (speech-to-text)

The app's transcription / voice-line search feature shells out to **whisper.cpp**'s
`whisper-cli`, exactly like `tools/vgmstream` does for `vgmstream-cli`. Drop two files
here so they get bundled into the build output (`tools/whisper/` next to the exe):

| File | What it is | Where to get it |
|------|------------|-----------------|
| `whisper-cli.exe` | whisper.cpp command-line binary (Windows x64) | Build from <https://github.com/ggml-org/whisper.cpp> or grab a prebuilt release. Include any DLLs it needs (e.g. `ggml*.dll`, `whisper.dll`) alongside it. |
| `ggml-base.en.bin` | English-only base GGML model (~140 MB) | `models/download-ggml-model.sh base.en`, or download from the whisper.cpp HF model repo. |

Until both files are present, `AppState.Whisper.Available` is false and the
transcription commands are disabled (the UI says so) — everything else still works.

## How it's invoked

```
whisper-cli -m ggml-base.en.bin -t <threads> -l en -oj -of <tmpbase> -f <16kHz-mono.wav>
```

The app decodes a sound (vgmstream → WAV), resamples it to 16 kHz mono via NAudio, runs
the command above, then reads the `<tmpbase>.json` sidecar for the transcript + segment
timestamps. Results are cached to `%APPDATA%/MarathonAudio/transcript-cache.bin` so work
is never repeated and interrupted runs resume.

## Model / concurrency

- Default model is `ggml-base.en.bin`. To use a different one, change the model file name
  in `AppState` (`whisperModel`).
- Transcription runs multiple `whisper-cli` processes in parallel. By default it adapts to
  the CPU (≈ `cores/4` workers × the remaining threads each); override in **Settings →
  Transcription**.
