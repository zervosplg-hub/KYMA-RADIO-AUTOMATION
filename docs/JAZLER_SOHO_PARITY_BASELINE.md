# KYMA — Jazler SOHO parity baseline (source: Greek Jazler SOHO manual, 132-page PDF)

## Rule
Implement against the original Jazler workflow, not an invented KYMA workflow. No UI-only placeholders may be reported as complete. Keep working on-air playout intact. Verify with actual screenshots and behavioral tests before marking parity PASS.

## Original top-level structure (manual table of contents)
1. Studio (chapter 4): main on-air console, layouts, on-air actions.
2. Βάσεις / Databases (chapter 5):
   - Τραγούδια (5.1): category editing; import; play history/statistics; reports; export to playlister; bulk edit; category/song/artist transmission schedules; separation rules; music selection rules; album art.
   - Spots/Διαφημίσεις (5.2): break hours and properties; hard-timed breaks; campaign scheduling; presets; proof-of-play; programming and daily reports; break duration limits.
   - Jingles (5.3): categories, import, playlister IDs, export, scheduling.
   - Λοιπά assets (5.4): categories, import, playlister IDs, export, scheduling, prerecorded programs.
   - Sweepers (5.5): placement, categories, import, scheduling.
   - Instant Jingles (5.6): palettes, palette audio, independent output.
   - Voice tracks (5.7): positions, categories, import, recording/multitrack, scheduling.
   - Κείμενα (5.8): categories, text database, playlister IDs.
3. Προγραμματισμός / Scheduling (chapter 6):
   - Playlists (6.1): creation, calendar scheduling, text imports, edit/preview, visual audio editor, ad break references.
   - Αυτόματο πρόγραμμα (6.2): classic/new auto modes, clocks, clock assignments, selection rules.
   - Force clocks (6.3): forced time/day music.
   - Αναμετάδοση (6.4): feeds, schedule, failover.
   - RDS (6.5).
   - Ημερήσια playlist (6.6): generation, edits, commercials, program archive and as-run.
4. Ρυθμίσεις (chapter 7): audio outputs, studio, paths/logs, users, SQL and path mappings.
5. Βοηθήματα (chapter 8): now-playing exports, streaming, file integrity.

## Current KYMA vs Jazler: verified source-code audit (2026-10-16)
- Main studio playout: PARTIAL. KYMA has WPF on-air controls, queue, history and cartwall. No screen-by-screen equivalence test yet.
- Songs database: PARTIAL. KYMA has imported tracks, category overrides, search and per-track JSON metadata editor; Jazler-specific separation, artist scheduling, play statistics, category management and bulk editing are not established.
- Spots database: NOT IMPLEMENTED at Jazler parity. The current COMMERCIALS button opens a generic text document, not campaigns, flight dates, break constraints or proof-of-play.
- Jingles: PARTIAL. Filtered library exists, but full category/scheduling database is not established.
- Assets, Sweepers, Voice Tracks, Text database: NOT IMPLEMENTED at Jazler parity.
- Instant Jingles: PARTIAL. Nine cart players exist; palette editor and output routing are not established.
- Playlists: PARTIAL. Queue exists; Jazler calendar and editing workflow not established.
- Automatic program/clocks: PARTIAL. An offline text-driven scheduler exists; it is not a Jazler-equivalent on-air automation engine.
- Force clocks, relay/failover, RDS: NOT IMPLEMENTED at Jazler parity.
- Daily playlist / Final Log: PARTIAL. Offline log generation exists; current FINAL LOG button is only a text viewer, not the Jazler daily-playlist workflow.
- Settings/utility parity: NOT VERIFIED.

## Next development gate (before another release)
A. Obtain original Studio, Songs database and category-editor screenshots / executable walkthrough.
B. Capture exact field labels, control states, action flows and persistence behavior.
C. Implement Songs database with real categories and per-song scheduling/separation, not unrelated standalone forms.
D. Test imports of ~55k tracks, persisted metadata, restart, search performance and playback regressions.
E. Mark PASS only when Windows Actions build passes and user verifies side-by-side behavior.

This file is a source-grounded requirements audit, NOT a claim of executable Jazler parity.
