# Final FFmpeg compatibility worker fix

Replace `src/Transcoder.Worker/Services/FfmpegRunner.cs`, rebuild, and restart every worker.

Fixes:
- unsupported/unknown mapped subtitle streams such as S_TEXT/WEBVTT: remove only the bad subtitle maps and retry once
- HEVC output to `.m4v`: if ffmpeg's iPod/M4V muxer rejects HEVC at header creation, retry once with `-f mp4` while keeping the `.m4v` filename

No server rebuild or database migration is required. Existing queued jobs can use the fallback without replanning.
