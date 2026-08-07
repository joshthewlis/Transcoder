# v32 workflow hardening + gaming guard

Files included:

- `src/Transcoder.Server/Controllers/MediaWorkflowController.cs`
  - bulk replan folder/library
  - queue folder via server-side logic
  - repair missing review limbo
  - approve all reviews
- `src/Transcoder.Server/wwwroot/js/media-workflow-controls.js`
  - adds Media Browser buttons:
    - Replan Current
    - Replan + Queue Cleanup
    - Replan + Queue Transcode
    - Queue Cleanup Fixed
    - Queue Transcode Fixed
  - adds Review buttons:
    - Repair Missing Reviews
    - Approve All Reviews
- `src/Transcoder.Server/wwwroot/css/media-workflow-controls.css`
- `src/Transcoder.Server/wwwroot/index.html`
  - same as current index with the new script/css tags added
- `src/Transcoder.Worker/Configuration/WorkerOptions.cs`
  - adds `GamingGuard` options
- `src/Transcoder.Worker/Services/WorkerLoopService.cs`
  - local gaming guard drain mode

## Gaming guard env example

Add this only on the Bazzite/gaming worker:

```yaml
Transcoder__GamingGuard__Enabled: "true"
Transcoder__GamingGuard__PollSeconds: "15"
Transcoder__GamingGuard__GameRunningSecondsBeforeDrain: "60"
Transcoder__GamingGuard__NoGameSecondsBeforeResume: "1200"
```

Optional extra detection:

```yaml
Transcoder__GamingGuard__ProcessNames__0: "lego"
Transcoder__GamingGuard__ProcessNames__1: "retroarch"
Transcoder__GamingGuard__CommandLineContains__0: "/steamapps/common/"
Transcoder__GamingGuard__CommandLineContains__1: "SteamGameId="
```

Or use a custom command. Exit code `0` means game detected:

```yaml
Transcoder__GamingGuard__DetectionCommand: "pgrep -fa '/steamapps/common/' >/dev/null"
```

When a game is detected for 60s, the worker stops advertising capacity and stops leasing new jobs. Existing jobs continue until they finish. After 20 minutes with no game detected, the worker starts leasing again.
