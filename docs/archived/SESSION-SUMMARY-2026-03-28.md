# Conversation Summary — March 28, 2026

## Local Dev Environment
- Docker Desktop installed on MacBook Air M4 16GB
- Two SQL Server containers running:
  - **PROD**: localhost,1433 / sa / Omer0370!
  - **DEV**: localhost,1434 / sa / Omer0370!
- Both have TestDB with realistic sample data (6 tables, 3 views, 4 sprocs, 2 functions, 1 trigger, 1 sequence)
- DEV has intentional schema drift, code drift, and data drift from PROD for testing all three compare modes
- Project cloned to /Users/omer/Documents/Projects/SqlVersionControl
- LOCAL-DEV-NOTES.md has all Docker commands and credentials

## Design System (DESIGN-SYSTEM.md in project root)

Full design document was created and CC executed Phase 1. Here are ALL specific design decisions made during the conversation:

### Bar Heights — THE BIG ONE
Every horizontal bar/strip in the app must be **28px tall**. No exceptions. This includes:
- Menu bar (File, Edit, Query, Help)
- Main tab strip (Query Editor, Version History, Compare Databases, Execution Plan)
- Query sub-tab strip (Query 1 ×, +)
- Toolbar row (Database dropdown, Run, Stop)
- Results tab strip (Result 1, Messages, Export, Edit)
- Status bar

Right now these are all different heights which makes it look amateurish. Uniform 28px everywhere.

### Grid
- Default row height: **22px** (not the Avalonia default which is taller)
- Row height should be **user-configurable** in Settings → Appearance (range 20-32px)
- Horizontal grid lines only, no vertical grid lines
- Alternating row colors: barely visible difference

### Editor ↔ Results
- **No separator line** between the editor and the results grid
- The results tab bar (Result 1 / Messages) IS the visual separator — no additional border needed
- Ömer's screenshot showed this looks cleaner than having an explicit line

### Object Explorer
- Needs **left padding** (10px from panel edge for root items) — was too tight against the edge
- **Indent guide lines**: faint vertical lines showing parent-child hierarchy (like VS Code/SSMS)
- **Separator lines** between category headers (Tables, Views, Stored Procedures) — NOT between every item
- **"Object Explorer" header text should be clickable** to collapse/expand the panel (no separate arrow button)
- CC's Phase 1 changes made OE **worse** — too spread out, lost its density. Items are too far apart. Needs to be dialed BACK toward original density, keeping only the left padding and indent guides. The density was a feature, not a bug.

### Buttons & Icons
- Corner radius: **2px everywhere** (set globally in App.axaml)
- **AC button and history button** (top right): replace with clean monochrome SVG icons matching the gear icon style. **Transparent background** until hovered. Currently they have visible rectangular backgrounds and the history one uses an emoji — both look terrible.
- **"Ready" text** in the toolbar row: remove from toolbar. It's status info and belongs in the status bar at the bottom, or just remove it entirely. Toolbar should only have actions (Run, Stop) and the Database dropdown.

### Dialogs
- 24px padding on all sides
- **No separate header background** — ConnectionDialog currently has a darker header strip, remove it. One uniform background for the entire dialog.
- Title: 18px SemiBold max (ConnectionDialog currently uses 22px — reduce)
- Button groups: right-aligned, primary button LAST (rightmost), 8px spacing between buttons
- No emoji anywhere in UI

### Spacing Philosophy
- Related elements grouped by tighter spacing, unrelated zones separated by larger gaps
- The gap between query tab row and toolbar should be TIGHTER than gap between toolbar and editor
- Currently everything is evenly spaced which makes it feel like a stack of unrelated rows instead of grouped zones

### Light Theme
- NOT white. Warm cream/paper tones like Claude's UI
- Base cream: #f5f0e8 (content areas)
- Chrome/panels: #e8e2d8 (warm beige)
- Borders: #d4cec3 (warm grey)
- Text: #3a3632 (dark brown-grey, NOT black)
- Full color mapping with all tokens is in DESIGN-SYSTEM.md
- Syntax highlighting colors independently chosen for light bg (not inverted dark)
- "Ink on warm paper" feeling

### Logo
- **Closed eye** — simple line-art, monochrome
- Double meaning: trust (handle your DB with eyes closed) + nod to original icon (sleeping boss)
- Works at all sizes (16px favicon to 512px app icon)
- Both themes: light lines on dark bg, dark lines on cream bg
- If app is named "Lookout" — the irony of a closed eye is intentional

## Naming
- "SQL Version Control" undersells the app — version control is ~15% of features
- Names discussed: SqlForge, QueryDesk, Ironclad, Anvil, SqlPilot, **Lookout**
- **Lookout** + closed eye logo was the strongest combo
- No final decision yet

## Git Integration (Major Insight)
- Git should be the storage backend for version history instead of the app's internal database
- The app already tracks definitions and shows diffs — swapping storage from DB to git repo is a plumbing change
- **User flow**: admin points app at a network share (e.g. \\\\10.0.95.31\\documents\\history), app initializes git repo there, exports .sql files per object, commits on sync
- Team members just point their app at the same folder — the app IS the UI, nobody touches git directly
- Self-hosted version control with zero infrastructure (no GitLab, no server setup)
- Network share = only reachable on company network / VPN = KVKK compliant
- This was the "why didn't we think of this from the start" moment of the session

## DDL Audit Configuration
- Currently hardcoded to VMAuditDb.dbo.DDL_Log (Gratis-specific) — causes error on any other setup
- Move to Settings → Version History section with fields: Server, Database, Table Name
- When not configured: show hint text, not error
- "Create Audit Infrastructure" button: preview trigger script for DBA review, or "Copy Script" to clipboard
- First-run setup flow (later): admin mode creates DDL trigger + git folder, user mode joins existing setup

## Phase Status
- **Phase 1 (visual consistency)**: CC executed but result is mixed. OE got worse (too spread out). Needs review and iteration.
- **Phase 2 (light theme)**: not started
- **Phase 3 (settings additions)**: not started — grid row height config, DDL audit config, git export path
- **Phase 4 (logo)**: not started

## Feature Ideas (Not Started)
- Query snippets/templates
- Scheduled query runner with change notifications
- Data masking on PROD→UAT copy (compliance/enterprise feature)
- Index analysis (unused + missing index suggestions)
- Object dependency viewer ("what breaks if I change this column?")
- Query history with diff
- Export to .sql files in git-friendly folder structure

## Selling Strategy
- One-time purchase **$49-99**, no subscription, free updates forever
- Landing page + demo video/GIFs + proper README with screenshots
- Distribution: own website, r/SQLServer, dba.stackexchange, Show HN, consultant word-of-mouth
- GitHub releases need to match actual versions (stuck at v1.1.0, app is v1.7.8)
- Need professional icon (retire the sleeping boss photo)

## Misc Context
- Anthropic vs Pentagon discussion (Feb-March 2026) — Anthropic refused unrestricted military AI use, got designated supply chain risk, won preliminary injunction March 24. OpenAI took the deal hours later. Claude hit #1 on App Store.
- Qwen Image "facing her back" interpretation issue — likely Chinese-English translation bias in training data
- Skills feature in Claude: custom SKILL.md instruction files, basically fancy if-then with vibes-based triggering
- We played a yes/no game where "bitch" meant "guidelines won't let me answer" — worked well

## Files in Project
- DESIGN-SYSTEM.md — full design bible for CC
- LOCAL-DEV-NOTES.md — Docker commands and credentials  
- LOCALWORK-v1.7.8.md — session planning doc (less detailed than this summary)
- SESSION-SUMMARY-2026-03-28.md — this file
