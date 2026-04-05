# Local Work Session — v1.7.8+ Planning

## Date: March 28, 2026

## Environment
- **Machine:** MacBook Air M4 16GB
- **PROD server:** localhost,1433 (Docker, sa / Omer0370!)
- **DEV server:** localhost,1434 (Docker, sa / Omer0370!)
- **TestDB** on both, with schema/data drift between them for testing compare features

## Current State (v1.7.8)
- Query editor, object explorer, version history, database compare (code, table structure, table data), execution plan, SQL Agent Jobs — all functional
- DDL audit log path is hardcoded to VMAuditDb.dbo.DDL_Log (company-specific, needs to be configurable)
- App runs on macOS and Windows via Avalonia/.NET 9

## Goals for This Session

### UI/Visual Polish
- [ ] Make the app look professional and sellable — not just functional
- [ ] Review all screens for consistency, spacing, colors, typography
- [ ] Clean up dark/light theme implementation

### Configuration & Setup
- [ ] Make DDL audit log path configurable (settings page)
- [ ] First-run setup flow: admin creates DDL trigger + git folder, user joins existing setup
- [ ] Git integration: export object definitions as .sql files to a local/network git repo

### New Feature Ideas (from discussion)
- [ ] Query snippets/templates — save and organize frequently used queries
- [ ] Scheduled query runner — run on timer, notify on change
- [ ] Data masking on PROD→UAT copy — anonymize sensitive columns
- [ ] Index analysis — unused indexes + missing index suggestions
- [ ] Object dependency viewer — "what breaks if I change this column?"
- [ ] Query history with diff — track what you ran and when

### Selling Prep (later)
- [ ] Pick a real product name
- [ ] Professional app icon (retire the sleeping boss)
- [ ] Screenshots and GIF demos for README
- [ ] Landing page
- [ ] One-time purchase model, $49-99, no subscription
- [ ] GitHub releases aligned with actual version numbers

## Notes
- Git as storage backend for version history is the big architectural insight — app becomes the UI, git is the dumb storage layer, network share = self-hosted version control with zero infrastructure
- Velopack handles auto-updates but NOT custom setup wizards — first-run config belongs inside the app
- No Oracle support yet — database layer abstraction TBD
