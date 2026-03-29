# Quality Polish — Architecture & Layout Changes

These are larger changes that go beyond visual consistency. They affect layout structure, navigation, and connection management. Not part of the current Phase 1 Fix — this is Phase 5+.

---

## 1. Merge Menu Bar + View Tabs into One Row

**Goal**: Go from 3 bars to 2 bars before the editor. The main view tabs (Query Editor, Version History, etc.) move into the same row as the menu bar.

**Current layout (3 bars)**:
```
File  Edit  Query  Help                                          [⚙]     ← bar 1: menu
Query Editor | Version History | Compare Databases | Exec Plan           ← bar 2: view tabs
[Query 1 ×] [+]                    Database: [TestDB ▾] [Run] [Stop]    ← bar 3: query tabs + toolbar
```

**New layout (2 bars)**:
```
File  Edit  Help    |    Editor   History   Compare   Exec Plan   Settings    ← bar 1: menus + view tabs
[Query 1 🟢 ×] [+]                Database: [TestDB ▾] [Run] [Stop]  [⚡][🕐] ← bar 2: query tabs + toolbar
```

### Changes required:

**A. Move the RadioButtons (QueryEditorTab, VersionHistoryTab, CompareTab, PlanTab) into the same row as the Menu.** In MainWindow.axaml, the Menu is in Grid.Row="0" and the tabs are in Grid.Row="2" inside the title bar Border. Merge them into one row — menus left-aligned, tabs right-aligned, with a subtle vertical separator between the last menu item and the first tab.

**B. Shorten tab labels:**
- "Query Editor" → "Editor"
- "Version History" → "History"
- "Compare Databases" → "Compare"
- "Execution Plan" → "Exec Plan"

**C. Kill the "Query" menu entirely.** It only has Run, Stop, and Change Connection. Run and Stop are already buttons in the toolbar row. "Change Connection" moves to File menu. That leaves just: File, Edit, Help.

**D. Replace the gear icon with "Settings" as a text tab/button** on the right side of the view tabs. It should look like a tab but be visually separated (subtle separator or slight gap). Clicking it opens the Settings dialog same as before.

**E. The IsVisible bindings on the content views (QueryEditorHost, VersionHistory content, CompareView, PlanView) do NOT change.** They still reference the same RadioButton names. This is purely a layout move — the RadioButtons just live in a different row now. Zero ViewModel changes needed.

**F. Both menus and tabs share the 28px bar.** Menus use their existing styling (dropdown on click). Tabs use their existing RadioButton.TabButton styling (active underline). They're visually distinct by their behavior — menus drop down, tabs switch content.

---

## 2. Connection Architecture — "Each View Owns Its Connection"

### The Problem

Right now each main tab (Editor, History, Compare) manages connections somewhat independently, which creates confusion: you might be on PROD in Editor but DEV in History, and the status bar / color stripe doesn't clearly reflect which one is active.

### The Model

**Every view remembers its own connection. The app-level indicators (color stripe, status bar) simply mirror whichever view you're currently looking at. Switching views never reloads or changes another view's state.**

Think of it like browser tabs. Each has its own URL. The address bar shows the active tab's URL. Switching tabs doesn't reload anything — it just updates the address bar.

### How it works:

1. **Query Editor tabs each own a connection.** When you create a new query tab, it inherits the current connection. From then on, that tab is stamped with that connection. Switching to DEV on one tab doesn't affect other tabs. Running a query always executes on THAT tab's connection. This prevents the catastrophic scenario: you write a sproc on DEV, switch to PROD for something else, switch back, hit Run — it MUST run on DEV because that tab owns DEV.

2. **History view owns its own connection.** You can switch History to look at DEV independently. When you switch away from History and come back, it still shows DEV data — no reload, no change.

3. **Compare view owns its connections.** Source and target are both set within Compare. The source defaults to whatever the current app connection is when you first open Compare, but once set, it's sticky. Target is always Compare's own thing.

4. **The app-level display (color stripe + status bar) mirrors the active view.** When you're on Editor looking at Query 1 (PROD), the stripe is PROD-colored. Switch to History (which was on DEV) — stripe changes to DEV. Switch back to Query 1 — stripe goes back to PROD. No data changes, no reloads, just the mirror updates.

5. **Within Editor, the active query tab drives the app display.** Click Query 1 (PROD) → stripe is PROD. Click Query 2 (DEV) → stripe is DEV. Each tab switch updates the mirror.

### Changing connections:

- **In Editor**: the Database dropdown in the toolbar row changes the active query tab's connection. Other tabs are unaffected.
- **In History/Compare**: a connection button or dropdown (location TBD — maybe next to Settings in the top bar, or within the view's own toolbar) lets you switch that view's connection. This does NOT affect any query tabs.
- **Switching back to Editor** always snaps the app display to whatever query tab was last active in Editor.

### Key rule: views never write to each other's connection state. The app-level display is read-only — it only reads from the active view, never pushes to other views.

---

## 3. Query Tab Connection Indicators

Each query tab needs a visual indicator of which environment it's connected to.

**Colored dot after the tab name.** Example: "Query 1 🟢 ×" where the dot matches the connection color (green for PROD, blue for DEV, etc.). The dot is always visible on both active and inactive tabs.

- The dot uses the same color as the connection's assigned color (which already exists for the status bar badges).
- Dot size: ~6px, vertically centered, positioned between the tab name and the close button.
- This is the critical safety feature — at a glance you know: "Query 1 is PROD, Query 2 is DEV, don't mix them up."

History and Compare don't need dots because there's only one of each — the app-level color stripe is enough for those.

---

## 4. Connection Color Stripe — Soften It

The current `ConnectionStripe` is a 3px solid color bar. It's aggressive.

**Change to gradient fade at both horizontal ends.** The stripe stays 3px tall and keeps the full connection color in the center, but fades to transparent at the left and right edges. This makes it feel like a subtle glow rather than a hard painted line.

Implementation: replace the solid `Background` on the `ConnectionStripe` Border with a `LinearGradientBrush` going horizontal:
- Offset 0.0: transparent
- Offset 0.15: connection color (full opacity)
- Offset 0.85: connection color (full opacity)
- Offset 1.0: transparent

The fade zone is ~15% on each side. The center 70% is still solid color so it's clearly visible. The edges just bleed away instead of cutting hard.

---

## 5. Database Dropdown Relocation

Currently the Database dropdown sits in the Editor's toolbar row (query tab row). This is fine for Editor. But when the connection architecture changes (each view owns its connection), History and Compare also need a way to switch connections.

Options:
- **A.** Each view has its own connection indicator/switcher in its own toolbar area. Editor has it in the query tab row, History has it in its own header, Compare has its source/target pickers.
- **B.** A universal connection switcher in the top bar (next to Settings) that changes the CURRENT VIEW's connection only.

Option A is probably cleaner — each view handles its own connection UI. The top bar stays clean and the connection is always contextual to what you're looking at.

---

## Implementation Notes

- The menu + tab merge (Section 1) is purely a MainWindow.axaml layout change. The RadioButtons move rows. The IsVisible bindings on content views stay the same. No ViewModel changes.
- The connection architecture (Section 2) is the big one. It requires: per-query-tab connection state, per-view connection memory, and the app-level display becoming a mirror instead of a source of truth. This touches MainWindowViewModel, QueryTabViewModel, connection management services, and the status bar bindings.
- Sections 3 and 4 are small visual changes that can be done alongside or after Section 2.
- Section 5 depends on Section 2 being implemented first.

### Priority within this doc:
1. Section 1 (menu merge) — layout only, can be done independently
2. Section 4 (color stripe softening) — small visual, can be done independently
3. Section 3 (query tab dots) — small visual, needs connection-per-tab from Section 2
4. Section 2 (connection architecture) — the big refactor
5. Section 5 (per-view connection UI) — depends on Section 2
