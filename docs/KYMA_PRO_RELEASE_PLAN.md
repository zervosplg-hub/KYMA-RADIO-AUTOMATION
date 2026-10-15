# KYMA RADIO AUTOMATION PRO — Product specification and release gates

Status: implementation specification, NOT a claim of completed functionality.
Baseline: KYMA SOHO v3.5 BETA (.NET 8 WPF, Windows x64, NAudio).
Delivery target: one consolidated KYMA PRO 1.0 release candidate after development, followed by user acceptance fixes. Documentation commits do not trigger Windows builds.

## Design principles
- Native Windows desktop, Greek-first with English switch; full dark theme with high-contrast hover/selection/popup styles.
- One main Studio screen and separate configuration/management windows; no clipped controls, including Cartwall, at minimum supported window size.
- Jazler-like operator workflow, but original implementation and branding; no dependence on Jazler.
- Do not block the UI or the on-air engine with SMB file checks, large library operations, waveform analysis, reports or backups.
- All scheduling and play-out actions auditable, with manual override and operator-visible status.
- A feature is complete only after end-to-end tests with actual audio/devices where applicable.

## Functional scope and acceptance criteria

### 1. Studio / Playout
NOW/NEXT, manual/auto, play/pause/stop/skip, crossfade, silence/mix points, waveform, seek, countdown, history, playlist edits, preview/cue, cartwall, voice tracking.
Acceptance: unattended multi-hour playback; consistent transitions including <5s jingles; manual override; no dead air on recoverable media failures; clearly logged unrecoverable failures.

### 2. Library and metadata
Music/jingles/spots/voice tracks grouped by type and category; title/artist/album/genre/year/BPM; bulk import; dedupe; missing-file detection; metadata editing; intro/outro/cue/mix-out points.
Acceptance: >=55k NAS tracks load/search without UI stalls; background scan is cancellable; inaccessible network shares do not trigger destructive cleanup.

### 3. Music scheduler / clocks
Hourly clocks, category slots, artist/title separation rules, repetition history, rotation weights, weekday/holiday templates, generated schedules, overrides, fallback slots.
Acceptance: deterministic preview and explainable track choices; no duplicate/artist rule violation unless explicitly allowed; schedule reproducible from stored configuration.

### 4. Final Log / 24-hour operations
Timeline with songs, jingles, ads, IDs, fixed-time events; lock/edit/regenerate slots; live deviations; as-run reconciliation.
Acceptance: exact planned-vs-played report, clear exceptions, schedule edits cannot silently interrupt on-air output.

### 5. Commercial manager
Clients, campaigns, audio spots, flight dates, weekdays/dayparts, rotation, break templates, separation rules, expiry, proof-of-play, exportable reports.
Acceptance: never schedule expired spots; deterministic spot-break generation; log skipped/unavailable items and commercial compliance exceptions.

### 6. Jingles / sweepers / voice tracking
Jingle palettes, hotkeys, timed IDs, beds/sweepers, recording and insertion of voice tracks, ducking/overlap settings.
Acceptance: preview is isolated from program output; levels and timing are predictable; cue edits do not alter source audio.

### 7. Audio routing and processing
Program/preview separation, multiple audio devices, device fallback, gain and loudness analysis, limiter, optional ASIO and plugin processing with isolation.
Acceptance: test each supported hardware backend separately; never label ASIO/VST complete based on UI controls alone; avoid sudden output level jumps.

### 8. External feeds / rebroadcast
Scheduled input switching, silence watchdog, timed local return, network stream monitoring, manual takeover.
Acceptance: switching and failback demonstrated with actual disconnected feed; state restored after restart.

### 9. Streaming / metadata / RDS
Optional streaming encoder, now-playing exports, metadata endpoints, configurable RDS integration through documented hardware/protocol.
Acceptance: live encoder stream tested, reconnection observed, no invented RDS hardware support.

### 10. Administration / diagnostics
Backup/restore, user roles, diagnostics, error logs, startup recovery, audit trail, system health, configurable retention.
Acceptance: tested restore to a clean installation; non-admin actions restricted; exportable reports and safe cancellation.

## Architecture migration
- Keep current verified v3.5 playout baseline intact until a replacement passes regression tests.
- Separate domains: PlayoutEngine, AudioRouting, MediaLibrary, Scheduler, Commercials, VoiceTracking, Integrations, Persistence, Diagnostics.
- Introduce durable SQLite schema with migrations and automatic backup before migrations; avoid rewriting tens of thousands of records in UI collections.
- Background work: cancellable jobs with bounded concurrency; audio-critical operations highest priority.
- Event-based audit trail for schedule changes, playback, ad proof-of-play, errors, and operator actions.
- Keep backward-compatible import/migration from existing KYMA state; verify with an actual v3.5 data copy.
- Feature flags for unverified audio backends/integrations; don't expose placeholder buttons as functioning features.

## Mandatory release verification
1. Windows x64 restore/build/publish; EXE starts on clean Windows 11.
2. XAML parse, all event handlers, C# compile, navigation and dark UI styles verified.
3. All Studio controls visible at supported resolutions; Cartwall not clipped.
4. 55k-track NAS import/search/clean; unplug/replug SMB share during playback.
5. At least 24h unattended playout with mixed WAV/MP3, short jingles, bad files, expired spots, scheduled events.
6. Crossfade accuracy, no dead-air regression, preview/program routing, manual override, audio recovery.
7. Clock rules, Final Log generation, commercials and as-run reconciliation tests.
8. Streaming/ASIO/VST/RDS/voice-tracking tests only when implemented and hardware/environment available.
9. Backup/restore and migration tests; installer/package integrity.
10. Greek/English translation coverage, error messages and operator manual.
11. ZIP integrity and downloadable GitHub Actions artifact verified.
12. Explicit feature matrix: PASS / PARTIAL / NOT IMPLEMENTED / HARDWARE NOT TESTED.

Release naming: KYMA RADIO AUTOMATION PRO 1.0 RC only after mandatory software gates pass; production release only after user acceptance. No claim of full Jazler feature parity without a separately verified comparison matrix.
