# Transcoder HTTP Foundation v31

v31 adds active-hours scheduling on top of the v30 HTTP foundation. The historical notes below are kept for upgrade context.

## New in v16

- Probe completion now builds a structured transcode plan.
- Plans are stored on the media item with a SHA-256 plan hash.
- PlanReview jobs are queued when the library policy requires review and processing mode is `PlanAndReview` or higher.
- PlanReview workers re-probe the file and check file size, modified time, and planned stream indexes.
- Media moves to `ReadyToTranscode` after approved plan review.
- Ambiguous/blocking planning cases create `NeedsReview` items.
- Library default original language can be entered in the Add Library UI.
- Media page now shows probe/plan state and has Plan/Transcode action buttons.
- Review page has Approve/Skip buttons.

## Notes

Original language is still metadata enrichment. v16 can use a library default original language, but Sonarr/Radarr/TMDb matching is not implemented yet.

Transcode execution is still not implemented. v16 intentionally stops at planning/review unless you manually queue a transcode job.

Because MediaItems gained plan columns, delete the dev SQLite DB before testing this version.

## v17 metadata integrations

v17 adds Radarr, Sonarr and TMDb integration settings under the built-in Settings page.

Recommended setup:

1. Add a TMDb integration using `https://api.themoviedb.org` and your TMDb API key or bearer token.
2. Add Radarr and/or Sonarr integrations with their base URLs and API keys.
3. Add path mappings so Transcoder's server path can be converted to the path seen by Radarr/Sonarr, for example:
   - Integration path: `/movies`
   - Server path: `/mnt/movies`
4. Set a library metadata priority such as `Radarr,Tmdb,FileProbe` for Movies or `Sonarr,Tmdb,FileProbe` for TV.
5. Use Refresh Metadata on a library to fill per-media original language before planning.

The metadata matcher deliberately avoids fuzzy title matching in this pass. It matches Radarr/Sonarr by path first, then uses their external IDs to ask TMDb for `original_language`.

After applying v17, delete the development SQLite database or add the new tables/columns manually. The new DB objects are `Integrations` and extra metadata columns on `MediaItems`.

## v19 metadata language fixes

v19 fixes original-language enrichment and UI display:

- Sonarr path matches now prefer TMDb original language when a TVDb/IMDb ID can be resolved.
- Sonarr/Radarr numeric language IDs are normalised to ISO-639-2 style language codes instead of being stored as raw numbers.
- Media UI now shows friendly language text, e.g. `English (eng)`, plus the metadata source and refresh time.
- Static UI assets now include a `?v=19` cache-buster.

If you already have bad test data such as `OriginalLanguage = '1'`, clear it and refresh metadata again.

```sql
UPDATE MediaItems
SET OriginalLanguage = NULL,
    OriginalLanguageSource = 0,
    MetadataRefreshedUtc = NULL,
    MetadataError = NULL
WHERE OriginalLanguage = '1';
```

## v20 notes

Adds first safe Transcode-to-Staging implementation:

- PlanReview approval now queues Transcode automatically when processing mode is `TranscodeToStaging` or `ReplaceApproved`.
- Media page includes a `View Plan` action showing stream mappings, warnings/blocking reasons, staging path, and generated FFmpeg arguments.
- Worker can execute `Transcode` jobs using server-provided FFmpeg args.
- Worker writes active FFmpeg output to `Transcoder:LocalWorkingRoot`, validates it with ffprobe, then copies to mapped staging path.
- Transcode completion marks media as `Staged` and does not replace originals.

For first tests, use a tiny library and keep `MaxTranscodeJobs = 1`.

## v21 cleanup/remux planning

v21 adds cleanup as a first processing strategy before heavy video transcoding:

- Library policy now has `ProcessingStrategy` with `CleanupOnly`, `CleanupThenTranscode`, and `TranscodeOnly` UI choices.
- `CleanupOnly` and the first phase of `CleanupThenTranscode` build a cleanup plan that copies video/audio/subtitle streams with `-c copy` and removes unwanted mapped streams.
- A new `Cleanup` job type runs the remux through the worker's normal local scratch -> staging flow.
- Plan details now show per-stream actions, estimated stream size, estimated saving for removed streams, and the generated FFmpeg command.
- Media list shows estimated cleanup saving from the current plan.
- Completed cleanup jobs mark media as `StagedCleaned`; originals are still untouched.

Stream saving estimates use ffprobe data when available. Matroska files often expose `NUMBER_OF_BYTES` or `BPS` tags per stream, but not every file/container does. When a removed stream has no byte-size/bitrate data, the UI shows the known estimate plus `+ unknown`.

## v22 safe execution controls

v22 makes planning/review the safe default and stops automatic staging work after plan review:

- New servers with no saved mode now default to `PlanAndReview` instead of `TranscodeToStaging`.
- PlanReview approval now marks media as `ReadyToCleanup` or `ReadyToTranscode` only; it does not auto-create Cleanup/Transcode jobs.
- Media page has explicit `Queue Cleanup` or `Queue Transcode` buttons based on the current plan type.
- Libraries page has bulk `Queue Cleanup` and `Queue Transcode` actions for approved existing plans.
- Queue actions require an approved plan when the policy requires plan review.
- `TranscodeToStaging` now means workers are allowed to run explicitly queued Cleanup/Transcode jobs; it no longer causes jobs to be created automatically from PlanReview.
- Settings page explains the mode and warns when staging execution is enabled.

Suggested workflow:

1. Keep mode as `PlanAndReview` while scanning/probing/planning.
2. Inspect estimated cleanup savings and FFmpeg args in `View Plan`.
3. Use `Queue Cleanup` for one item or a library when ready.
4. Switch mode to `TranscodeToStaging` to let workers run queued staging jobs.

## v23 CPU/GPU transcode policy

v23 adds explicit CPU/GPU transcode selection and worker gating:

- Worker capabilities now report CPU encoders, GPU encoders, and whether CPU/GPU encoding is enabled by local worker config.
- Worker config supports:
  - `Transcoder:Transcoding:AllowCpuEncoding`
  - `Transcoder:Transcoding:AllowGpuEncoding`
  - configured CPU/GPU encoder name lists.
- Library video policy now has `TranscodeEngine`:
  - `PreferGpu`
  - `GpuOnly`
  - `PreferCpu`
  - `CpuOnly`
  - `Either`
- Planning records the selected encoder, required encoder engine, policy, and selection reason in the plan details.
- Transcode jobs are leased only to workers that satisfy the required engine and encoder.
- Cleanup/remux jobs are unaffected because they use stream copy and do not require CPU or GPU video encoding.
- The Workers, Libraries, Jobs, and Plan UI now show the engine/capability decisions.

Example worker config for Intel ARC/QSV-only transcoding:

```json
"Transcoding": {
  "AllowCpuEncoding": false,
  "AllowGpuEncoding": true,
  "GpuEncoders": [ "hevc_qsv", "av1_qsv" ]
}
```

Example CPU-only worker config:

```json
"Transcoding": {
  "AllowCpuEncoding": true,
  "AllowGpuEncoding": false,
  "CpuEncoders": [ "libx265", "libsvtav1" ]
}
```

The duplicate local `out var libraryId` issue has also been avoided in this build; no `out var libraryId` declarations are present in the patched source.


## v25 premium audio cleanup and grouped library UI

v25 refines cleanup planning for multi-track movie files and makes the library policy screen easier to use.

- Adds premium audio policy modes:
  - KeepAllPremium
  - KeepBestPremiumOnly
  - KeepBestPremiumAndCompatibility
  - KeepBestTwoPremiumAndCompatibility
  - KeepCompatibilityOnly
- Adds premium ranking options:
  - PreferAtmosThenTrueHd
  - PreferAtmosThenDts
  - PreferTrueHd
  - PreferDts
- Default cleanup behaviour now keeps the best premium track per language plus a compatibility fallback when available.
- Duplicate premium tracks are removed unless the library policy says to keep two or keep all.
- The plan view explains why a premium track was selected or removed.
- The Add Library UI is now grouped into Library, Video, Audio, Subtitles, Metadata, and Watch / Scan sections.
- UI assets use ?v=25.

Example default result for English DTS 7.1 + TrueHD 7.1 + AC3 5.1:

- Keep TrueHD according to the premium ranking.
- Keep AC3 as the compatibility fallback.
- Remove DTS as duplicate premium audio.

Change the library premium mode to KeepBestTwoPremiumAndCompatibility or KeepAllPremium if you want to keep DTS as well.


## v26 automation, staging safety, and media browser

v26 makes cleanup/transcode operation more autonomous while keeping library-level safety controls.

- Server automation settings were added under Settings:
  - Auto queue cleanup jobs
  - Auto queue transcode jobs
  - Require PlanReview before auto queue
- Each library now has generation switches:
  - Generate cleanup jobs
  - Generate transcode jobs
- A job is created automatically only when both levels allow it. For example, the server can be set to run staging work, while a library can generate cleanup jobs but not transcode jobs.
- Worker staging writes are now safer:
  - output is created in local scratch first
  - output is copied to staging as `.partial-<leaseId>`
  - the staged partial file is ffprobe-validated
  - the partial is moved into the final staging filename
  - a `.complete.json` marker is written next to the staged output
  - the job result includes `stagingTransferComplete = true`
- Local worker scratch is deleted after a successful staged validation by default. Set `Transcoder:KeepLocalJobFilesOnSuccess=true` to keep successful job files/logs for debugging.
- Actual saved bytes are now recorded from the before/after sizes when cleanup/transcode completes.
- Dashboard shows total actual saved space and completed cleanup/transcode counts.
- Media page includes a folder-style browser. Pick a library, drill into folders/seasons, and see original size, actual saved size, and estimated cleanup savings per folder.

Staged files are still separate from originals. Replacement/original-swap is not automatic in this version.


## v27 cleanup concurrency and clearer execution wording

v27 separates cleanup/remux capacity from real transcode capacity and makes the Settings wording clearer.

- Workers now have a separate `MaxCleanupJobs` limit. Default is 4.
- Cleanup/remux jobs no longer consume the `MaxTranscodeJobs` slot.
- Transcode jobs still use `MaxTranscodeJobs` and default to 1.
- Configure cleanup concurrency with:

```json
"Limits": {
  "MaxProbeJobs": 1,
  "MaxPlanReviewJobs": 1,
  "MaxCleanupJobs": 4,
  "MaxTranscodeJobs": 1,
  "MaxValidationJobs": 1
}
```

or environment variable:

```bash
Transcoder__Limits__MaxCleanupJobs=4
```

- Successful worker scratch cleanup now also removes empty parent `job-*` folders.
- Settings now labels `TranscodeToStaging` as “Run staged work - cleanup/transcode to staging” so it is clearer that this is an execution gate, not a transcode-only mode.
- UI assets use `?v=27`.


## v28 UI workflow cleanup

v28 focuses on usability and observability:

- Dashboard separates active jobs from finished jobs.
- Dashboard includes a compact library overview showing strategy and cleanup/transcode generation.
- Jobs page includes type/status/worker filters and a clear-finished-jobs action.
- Libraries table is condensed into grouped summary cards instead of huge top-down columns.
- Settings/integration/library pages avoid polling refreshes while a form control is being edited.
- Integrations can be tested before saving.
- Media rows show cleanup, transcode, and total actual savings separately where available.

## v29 replace-original workflow and UI tightening

- Adds a server-side Replace Original workflow for staged cleanup/transcode outputs.
- Replacements require a completed staging marker and an unchanged original file size/modified time.
- Original files are moved to `.transcoder/originals/...` quarantine before the staged file is moved back to the original filename/path.
- Savings history is kept in media metadata as a ledger so cleanup/transcode/replace totals are retained after replacement.
- Replace can be triggered per media item, bulk per library, or automatically when processing mode is `ReplaceApproved` and the library has `Output.ReplaceOriginals=true`.
- After replacement, stale probe/plan/review data is cleared so the file can be freshly probed before any later transcode phase.
- Library UI checkboxes were tightened so the checkbox stays fixed-size and the text gets the available space.

## v30 notes

- Settings changes now immediately reconcile existing planned media and queue eligible cleanup/transcode jobs.
- A background reconciler periodically catches media that becomes eligible after settings/library changes.
- Dashboard compact jobs now keep the Worker column visible.
- Navigation order now places Libraries above Jobs.
- Server/worker logging is quieter for SQL/HTTP noise and includes higher-value queue/job lifecycle messages.
- Review items now include library and media path details.
- Worker runtime path mappings can be edited from the UI. The worker persists them in `worker-runtime-settings.json` under its local working root and applies them without restart. Container workers still need the paths mounted into the container.

## v31 active-hours job gate

v31 adds a time-based execution gate for long staged jobs so the system does not start cleanup/transcode work around a scheduled NAS shutdown.

Default active-hours settings are tuned for a NAS that turns off at 02:00 and comes back at 08:00:

- Active-hours gate: enabled by default.
- Time zone: `Europe/London`.
- Start work: `08:30`.
- Stop/NAS shutdown time: `02:00`.
- Stop new work before stop: `30` minutes.

With those defaults, cleanup/transcode leases can start between 08:30 and 01:30. From 01:30 until 08:30, queued cleanup/transcode jobs stay queued but are not handed to workers. Probe and PlanReview jobs are not blocked by the active-hours gate.

What is gated:

- Server job leasing for `Cleanup`, `Transcode`, and staged validation-style work.
- Automatic cleanup/transcode queue reconciliation.
- Automatic replace-original processing in `ReplaceApproved` mode.
- Immediate replacement after a staged job completes is deferred if active hours have closed.

Existing running jobs are not killed. The gate prevents new long staged work from starting; it does not interrupt a job that was already running before the stop-new-work boundary. For large transcodes, set the stop-new-work guard higher than 30 minutes if you want more drain time before the NAS powers down.

You can change the schedule in the UI under Settings → Active Hours, or through configuration defaults:

```json
"Transcoder": {
  "ActiveHours": {
    "Enabled": true,
    "TimeZoneId": "Europe/London",
    "Start": "08:30",
    "Stop": "02:00",
    "StopNewWorkMinutesBefore": 30
  }
}
```

Environment variable example:

```bash
Transcoder__ActiveHours__Enabled=true
Transcoder__ActiveHours__TimeZoneId=Europe/London
Transcoder__ActiveHours__Start=08:30
Transcoder__ActiveHours__Stop=02:00
Transcoder__ActiveHours__StopNewWorkMinutesBefore=30
```
