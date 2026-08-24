# Unsupported subtitle compatibility retry

Replace:

`src/Transcoder.Worker/Services/FfmpegRunner.cs`

with the file in this bundle and rebuild/redeploy every Worker.

## What it does

If FFmpeg fails at output-header creation because one or more explicitly mapped subtitle streams are reported as `Subtitle: none` / unsupported codec, the worker:

1. extracts only those unsupported subtitle source indexes from the FFmpeg error,
2. removes only their exact `-map 0:N` pairs,
3. deletes the failed partial local output,
4. retries FFmpeg once,
5. leaves video and audio mappings unchanged,
6. logs the omitted subtitle indexes.

This is intentionally a worker-side fallback so already queued jobs with old plan payloads can succeed without replanning.

It does not drop unknown video/audio streams and it does not retry arbitrary FFmpeg failures.
