# Design System — SQL Version Control

## Philosophy

This is a professional database tool. It should look like something a senior DBA would trust with their production server.

**The north star: SSMS meets a modern terminal.** SSMS has a clean, no-nonsense look that developers trust instinctively. Terminals like Ghostty and iTerm2 prove you can be dark-themed and modern without looking like a gaming app. We sit at the intersection — professional density with modern polish.

### Core Principles

1. **Quiet confidence.** The UI disappears. Data is the star — query results, diffs, object trees. Chrome, borders, and buttons stay as invisible as possible while remaining functional.

2. **Zero rounded corners on functional elements.** Buttons, inputs, panels, tabs — 2px radius max. Rounded corners signal consumer/mobile. Square signals professional/desktop. Only exception: connection badge tags in the status bar (4px radius).

3. **Density over whitespace.** DBAs work with lots of data. Every pixel matters. Padding is tight but not cramped — 8px is the base unit. No 20px+ gaps between related elements.

4. **Muted palette, intentional color.** Color only appears for: syntax highlighting, status indicators, diff markers, and action buttons. Everything else is grayscale.

5. **Consistency over beauty.** Two dialogs looking slightly different is worse than both looking slightly ugly. Every dialog, panel, and grid follows the same rules.

6. **Visual grouping through spacing, not lines.** Related elements are grouped by tighter spacing. Unrelated zones separated by larger gaps. Avoid hard separator lines between editor and results — let the results tab bar act as the natural break. Exception: Object Explorer tree items benefit from subtle separators (see below).

---

## Spacing System

Base unit: **4px**. All spacing is a multiple of 4.

| Token | Value | Use |
|-------|-------|-----|
| `xs` | 4px | Inline spacing, icon-to-text gaps |
| `sm` | 8px | Padding inside compact elements (buttons, inputs, cells) |
| `md` | 12px | Standard padding inside panels, between form fields |
| `lg` | 16px | Section separation within a view |
| `xl` | 24px | Major section separation, dialog margins |

### Specific Rules

- **Dialog margins**: 24px all sides
- **Form field spacing**: 12px between label and input, 16px between field groups
- **Button padding**: 12px horizontal, 6px vertical (compact); 20px horizontal, 8px vertical (dialog actions)
- **Panel padding**: 8px inside panels
- **Tab strip item padding**: 12px horizontal, 6px vertical
- **Status bar padding**: 4px vertical, 8px horizontal per item
- **Grid cell padding**: 6px horizontal, 4px vertical
- **Visual zone grouping**: Toolbar row → query tab row should be TIGHTER than query tab row → editor. Related rows feel grouped; the editor starts a new zone with slightly more breathing room.
- **Zero gap between menu bar and main tab strip.** These should be flush — no margin, no padding, no border between them.

---

## Typography

System default font for UI (San Francisco on macOS, Segoe UI on Windows). **Consolas** (Windows) / **Menlo** (macOS) for monospace only.

Monospace is used for: SQL editor, code diffs, grid cell data, connection strings. Everything else is system font.

| Role | Size | Weight | Color |
|------|------|--------|-------|
| Dialog title | 18px | SemiBold | TextBright |
| Section header | 13px | SemiBold | TextPrimary |
| Form label | 12px | SemiBold | TextPrimary |
| Body text | 12px | Regular | TextPrimary |
| Secondary/hint | 11px | Regular | TextSecondary |
| Disabled | 11px | Regular | TextDisabled |
| Monospace data | 12px | Regular | TextPrimary |
| Tab label | 12px | Regular (inactive) / SemiBold (active) | TextSecondary / TextBright |
| Query tab label | 12px | Regular | TextPrimary |
| Grid header | 11px | SemiBold | TextSecondary |
| Status bar | 11px | Regular | TextPrimary |

### Rules
- **No font sizes above 18px** anywhere. This is a tool, not a landing page.
- Current ConnectionDialog uses 22px title — reduce to 18px.
- ALL monospace uses Consolas/Menlo. No mixing.
- **Query tab text ("Query 1", "Query 2")** should be **12px Regular** — same size as Object Explorer item text like "Employees". Currently it's too large relative to the tab container. The tab itself is fine; the text inside is oversized.

---

## Dark Theme (Primary)

All colors from `Styles/AppTheme.axaml`. **NEVER hardcode hex values in AXAML files.**

Current palette is Ghostty Default Dark. Keep it — it's good. These are the semantic mappings:

### When to Use Color

| Color | Use For | Never Use For |
|-------|---------|---------------|
| `ButtonPrimary` (#b7bd73, olive green) | Primary actions: Connect, Run, Save, Apply, Deploy | Navigation, toggles |
| `ButtonDanger` (#bf6b69, muted red) | Destructive: Delete, Drop, Remove, Rollback | Warnings, cancel |
| `AccentBlue` (#83a5d6) | Links, selected states, focus rings | Buttons |
| `ButtonToggleActive` (#88a1bb) | Active toggle, active tab indicator | Primary actions |
| Diff green (#203d20) | Inserted/new lines only | |
| Diff red (#3d2020) | Deleted lines only | |

### Status Colors
- **Connected**: green dot (#b7bd73)
- **Disconnected**: red dot (#bf6b69)
- **Running/loading**: amber (#e9c880)
- **Error text**: #bf6b69
- **Warning text**: #e9c880

---

## Light Theme (Warm)

**NOT white.** Cream/warm paper tones inspired by Claude's UI. The goal is a light theme that doesn't assault your eyes.

Create a second resource dictionary `Styles/AppThemeLight.axaml` with these base colors:

| Key | Dark Value | Light Value | Notes |
|-----|-----------|-------------|-------|
| TitleBarBackground | #1d1f21 | #e8e2d8 | Warm beige chrome |
| MenuBarBackground | #1d1f21 | #e8e2d8 | |
| TabStripBackground | #252729 | #ddd7cc | Slightly darker than content |
| ActiveTabBackground | #292c33 | #f5f0e8 | Matches content area |
| InactiveTabBackground | #1d1f21 | #e8e2d8 | Matches chrome |
| SidebarBackground | #292c33 | #f5f0e8 | |
| EditorBackground | #292c33 | #f5f0e8 | Main content cream |
| ResultsGridBackground | #292c33 | #f5f0e8 | |
| ResultsAlternateRow | #2e3138 | #efe9de | Barely visible alternation |
| PanelHeaderBackground | #1d1f21 | #e8e2d8 | |
| BorderDefault | #3a3d44 | #d4cec3 | Warm grey borders |
| SplitterBackground | #3a3d44 | #d4cec3 | |
| TextPrimary | #c5c8c6 | #3a3632 | Dark brown-grey, NOT black |
| TextSecondary | #666666 | #8a847a | |
| TextDisabled | #4a4a4a | #b5afa5 | |
| TextBright | #eaeaea | #1a1714 | |
| SelectionActive | #3a3d44 | #d4cec3 | |
| HoverBackground | #2e3138 | #ebe5da | |
| ButtonPrimary | #b7bd73 | #7a8040 | Darker olive for contrast on cream |
| ButtonDanger | #bf6b69 | #a85250 | |
| ButtonForeground | #1d1f21 | #f5f0e8 | |
| AccentBlue | #83a5d6 | #4a7ab5 | Darker blue for light bg |


**UPDATED — Chrome colors need more contrast.** The original chrome values (#e8e2d8) are too close to the content area (#f5f0e8). Push the bars/chrome noticeably warmer and darker to create visible separation — like the Obsidian warm theme where the sidebar is clearly tan/light brown and the content is cream. Updated chrome values:

| Key | Old Light Value | New Light Value | Notes |
|-----|----------------|-----------------|-------|
| TitleBarBackground | #e8e2d8 | #d5cbb8 | Warmer, more brown |
| MenuBarBackground | #e8e2d8 | #d5cbb8 | |
| TabStripBackground | #ddd7cc | #cec3af | Noticeably darker than content |
| InactiveTabBackground | #e8e2d8 | #d5cbb8 | Matches chrome |
| PanelHeaderBackground | #e8e2d8 | #d5cbb8 | |
| BorderDefault | #d4cec3 | #c4b9a5 | Warmer borders |
| SplitterBackground | #d4cec3 | #c4b9a5 | |

Content area values (EditorBackground, SidebarBackground, ActiveTabBackground, ResultsGridBackground) stay at #f5f0e8. The goal: bars feel like warm wood/leather, content feels like cream paper. Clear visual separation.


Syntax highlighting for light theme:
| Key | Dark | Light |
|-----|------|-------|
| SyntaxKeyword | #88a1bb | #2c5f8a |
| SyntaxString | #b7bd73 | #5a7028 |
| SyntaxComment | #666666 | #9a9488 |
| SyntaxNumber | #e9c880 | #986a1d |
| SyntaxVariable | #ad95b8 | #7b5a8a |
| SyntaxFunction | #e1c65e | #8a6d1a |
| SyntaxIdentifier | #95bdb7 | #2a7a70 |

**Key principle**: Light theme colors are NOT the inverse of dark. They are independently chosen warm-toned values that feel cohesive on cream. Everything should look like ink on warm paper.

---

## Components

### Buttons

Three tiers:

1. **Primary** (`btn-primary`): One per dialog/panel. Main action. Olive green, dark text.
2. **Secondary** (`btn-secondary`): Everything else. Dark grey background (#3a3d44), light text.
3. **Danger** (`btn-danger`): Destructive only. Muted red. Always requires confirmation.

Rules:
- Corner radius: 2px everywhere
- Min width: 80px for dialog buttons
- Button groups right-aligned in dialogs, 8px spacing, primary button LAST (rightmost): `[Cancel] [Save]`
- No icons on buttons unless the button has no text label
- **Toolbar buttons (Run, Stop, etc.) must fit inside 28px bars.** Max button height: 24px, with 2px vertical margin above and below inside the 28px bar. If buttons are getting clipped or overflowing bars, reduce internal padding — the bar height is sacred, the button shrinks to fit.
- **Run and Stop buttons need enough horizontal padding to breathe.** Same min width for both: 60px. Horizontal padding: 12px. Don't let them get squished — if space is tight, query tabs compress first (truncate with ellipsis), action buttons keep their size.
- **Run button**: label is just "Run", NOT "Run (F5)". Shortcut info goes in the tooltip.
- **Stop button**: same — just "Stop", no shortcut in the label.
- **The AC (autocomplete) and history buttons** in the top right: replace emoji/text with clean monochrome SVG icons. Same style as the gear icon. Background should be transparent until hovered (not the current visible rectangle background). These should feel like toolbar actions, not badges.

### Inputs (TextBox, ComboBox)

- Background: `EditorBackground`
- Border: `BorderDefault`, 1px
- Corner radius: 2px
- Height: 32px standard, 28px compact/inline
- Focus border: `FocusBorder`, 1px
- Watermark: `TextDisabled`
- **ComboBox in toolbar (Database dropdown)**: when inside a 28px bar, the dropdown needs **2px vertical margin** above and below so it doesn't touch the bar edges. Effective dropdown height: 24px inside the 28px bar. The dropdown width is fine as-is — it doesn't need to be wide.

### Data Grids

The most important component. People spend 80% of their time here.

- **Row height**: 22px default. **User-configurable** in Settings under Appearance (range: 20px–32px, stored in settings.json).
- **Header height**: 26px. Background: `PanelHeaderBackground`. Text: 11px SemiBold, `TextSecondary`.
- **Grid lines**: 1px horizontal lines, `BorderDefault`. Vertical lines: very faint (1px, `BorderDefault` at 15-20% opacity) — barely visible column guides, not heavy borders. Row number gutter gets a slightly stronger right border (30% opacity) plus extra right padding to separate from data.
- **Alternating rows**: barely visible. The difference between `EditorBackground` and `ResultsAlternateRow` should be subtle.
- **Selection**: `SelectionActive` background.
- **Hover**: `HoverBackground`.
- **Cell padding**: 6px horizontal, 4px vertical.
- **NULL display**: italic, `TextNull` color, shows `NULL`.
- **Frozen header**: always visible on scroll.
- **No separator line between editor and results grid.** The results tab bar (Result 1 / Messages) IS the visual break. Remove any explicit border-bottom on the editor panel.

### Dialogs

All dialogs follow this structure:

```
┌─────────────────────────────────┐
│ Title (18px SemiBold)           │
│ Subtitle (12px, TextSecondary)  │
│                                 │
│ [Form content]                  │  ← 24px padding all sides
│                                 │
│              [Cancel] [Primary] │
└─────────────────────────────────┘
```

- Background: `SidebarBackground` — ONE background for the entire dialog. No separate header strip. The current ConnectionDialog has a darker header bar — remove it. Title and form content share the same background.
- Corner radius: 2px on the window
- Padding: 24px all around
- Button row: 20px top margin from last form field

### Tabs (Main: Query Editor, Version History, etc.)

- Active: `ActiveTabBackground`, `TextBright`, bottom border 2px `ButtonToggleActive`
- Inactive: `InactiveTabBackground`, `TextSecondary`, no border
- Tab strip: `TabStripBackground`
- Padding: 16px horizontal, 8px vertical
- **Square. No rounded corners on tabs.**

### Query Tabs (Query 1, Query 2, etc.)

- **Text size: 12px Regular** — must match Object Explorer item text size (like "Employees", "AuditLog"). Currently the query tab text is oversized relative to its container. The tab height is correct at 28px, but the text inside needs to shrink to 12px.
- Close button (×): 10px, appears on hover or when tab is active
- (+) new tab button: same 12px text size

### Results Tab Bar

The results tab bar contains two different types of elements — **tabs** and **buttons** — and they must look and behave differently:

**Tabs** (left side): "Result 1 (15 rows)", "Messages"
- These are **tabs, NOT buttons.** Style them identically to the query sub-tabs above — active tab gets `ActiveTabBackground` + `TextBright` with a bottom accent border, inactive gets `InactiveTabBackground` + `TextSecondary`.
- Text: 11px to fit comfortably in the 28px bar
- They switch the content panel below them. They do NOT perform an action.

**Buttons** (right side): "Export", "Edit", and any hidden buttons (Apply, Add Row, Delete Row, Cancel Edit)
- These are **action buttons** and should look like compact toolbar buttons.
- Height: 24px max inside the 28px bar (2px margin top/bottom).
- Text: 11px, compact horizontal padding (8px).
- **Check ALL hidden/conditional buttons** in this bar (Apply, Add Row, Delete Row, Cancel Edit, etc.) — they must all fit within 28px and follow the same compact button styling.

### Object Explorer / Tree Views

This needs the most work. Currently looks like a bare text list. Should look like a proper IDE tree.

- **Item height**: 26px
- **Left padding**: **20px from the panel edge** for root-level expand arrows and items. Currently the arrows literally start at the app border — there's zero breathing room. 20px gives proper visual separation between the panel edge and the tree content.
- **Indent per level**: 16px
- **Indent guides**: faint vertical lines (1px, `BorderDefault` at 30% opacity) connecting parent to children. These are the subtle lines that run down the left side showing tree hierarchy. VS Code and SSMS both use these.
- **Separator lines**: subtle 1px horizontal lines (`BorderDefault` at 20% opacity) between top-level groups (Tables, Views, Stored Procedures, etc.). NOT between every single item — only between category headers.
- **Icon + text gap**: 6px
- **Selected item**: `TreeSelected` background + `TreeSelectedAccent` 2px left border
- **Hover**: `TreeHover` background
- **Expand arrows**: `ExpandArrowForeground`, size 10px. Use proper triangle glyphs (▶ collapsed, ▼ expanded), not text characters.
- **Column type annotations**: `ColumnTypeForeground` (#666666), 11px, positioned after column name with 6px gap
- **PK badge**: `ColumnPKForeground` (#e9c880), bold "PK" text

### Status Bar

- Height: 24px
- Background: `TitleBarBackground`
- Text: 11px
- Connection badges: 4px corner radius, colored background, dark text. Current style is fine.
- Left: connection info with status dot
- Right: row count, execution time, errors
- **DDL error** ("Invalid object name 'VMAuditDb.dbo.DDL_Log'") — this should not show when DDL audit is not configured. Hide it or show a subtle "Version tracking: not configured" hint instead.

### Scrollbars

All scrollbars in the app (Object Explorer, results grid, editor) should use **overlay auto-hide** behavior:

- **Invisible by default.** Scrollbars are hidden when not actively scrolling.
- **Appear on scroll or hover.** When the user scrolls (mousewheel, trackpad, keyboard) or hovers near the scrollbar track edge, the scrollbar fades in.
- **Fade out after ~1.5s of inactivity.** Don't snap-disappear — use a short fade.
- **Thin when idle, slightly wider on hover.** Idle width: 6px. Hover/active width: 8px. This is the macOS overlay scrollbar pattern.
- **Semi-transparent thumb**, no track background. The thumb should be `TextSecondary` at ~40% opacity, increasing to ~70% on hover.
- **No arrow buttons** at the ends of scrollbar tracks.

This applies to: Object Explorer panel, results DataGrid (both horizontal and vertical), SQL editor (if it has its own scrollbar outside AvalonEdit's), and any scrollable dialog content.

In Avalonia, this can be done with `ScrollViewer.HorizontalScrollBarVisibility="Auto"` combined with custom styling on the `ScrollBar` control template to achieve the overlay look. If Avalonia doesn't support true overlay scrollbars natively, use `Auto` visibility and style the scrollbar to be as thin and subtle as possible.

### Tooltips

Tooltips are used for **keyboard shortcuts and non-obvious button functions only.** They should be helpful, not noisy.

- **Show on hover** after a **600ms delay** (longer than default — we don't want tooltips flashing on casual mouse movement).
- **Style**: `SidebarBackground` background, `TextPrimary` text, 1px `BorderDefault` border, 2px corner radius. 8px padding. 11px font size.
- **Content**: short and direct. Format for buttons with shortcuts: `Run query (F5)` or `Stop execution (Ctrl+Break)`. No full sentences, no periods.
- **Never repeat the button label.** If the button says "Run", the tooltip should say `Execute query (F5)`, not `Run (F5)`. Add value or don't show a tooltip at all.
- **Keyboard shortcut format**: use the platform convention. macOS: `⌘+Shift+F`. Windows: `Ctrl+Shift+F`. If cross-platform, show the current platform's convention.
- **Disappear immediately on mouseout.** No linger.

Buttons that should have tooltips:
- Run → `Execute query (F5)`
- Stop → `Cancel execution`
- AC icon → `Toggle autocomplete`
- History icon → `Query history`
- Gear icon → `Settings`
- OE collapse arrow → `Toggle Object Explorer (Ctrl+B)`
- Export → `Export results to CSV`
- Edit → `Edit result rows`

---

## Layout

### Main Window Structure

The editor is the hero. Everything else exists to support it. Minimize the number of horizontal bars between the title bar and the first line of SQL.

**Key layout change: merge the toolbar (Database dropdown, Run, Stop) into the query tab row.** This eliminates one entire 28px bar and pulls the editor up. The query tab row becomes:

```
[Query 1 ×] [+]          Database: [TestDB ▾]  [Run] [Stop]    [⚡] [🕐] [⚙]
```

Left side: query tabs. Right side: database selector and action buttons. Everything on one 28px line.

**The "1 result set(s), 15 total rows" text must NOT be in this row.** That's results metadata and it already shows in the status bar at the bottom. Remove it from the toolbar area entirely.

Updated structure:

```
┌──────────────────────────────────────────┐
│ File  Edit  Query  Help            [⚙]   │  28px — menu bar
│ Query Editor │ Version History │ Compare  │  28px — main tabs (FLUSH against menu, zero gap)
├────────┬─────────────────────────────────┤
│ Object │ [Query 1 ×][+]  [TestDB▾][▶][■]│  28px — query tabs + toolbar merged
│ Explor ├─────────────────────────────────┤
│ er     │                                 │
│        │  SQL Editor                     │  ← THE HERO. Gets majority of vertical space.
│        │  (takes ~70% of remaining       │
│ 220px  │   vertical space by default)    │
│        │                                 │
│        ├─ Result 1 (15) │ Messages ──────┤  28px — results tab bar (IS the separator)
│        │  Data Grid                      │  ← ~30% of remaining vertical space
│        │                                 │
├────────┴─────────────────────────────────┤
│ ● PROD (localhost) │ DEV │ PROD │ 15 rows│  24px — status bar
└──────────────────────────────────────────┘
```

That's **3 bars** (menu, main tabs, query tabs+toolbar) instead of the old 4. The editor starts sooner and gets more room.

### Window Chrome

- **The window MUST be resizable and movable.** If Avalonia custom chrome or `ExtendClientAreaToDecorationsHint` is breaking standard window management (drag to move, resize from edges/corners), fix it. A desktop app that can't be resized or moved is broken.
- Check `CanResize`, `WindowState`, and any custom title bar drag regions.
- Test: drag title bar to move, drag edges/corners to resize, maximize/restore, minimize. All must work.

### Results Panel Behavior

- **Before any query is executed**: results panel is **collapsed**. Only the results tab bar is visible as a thin strip at the bottom of the editor. The editor gets nearly 100% of vertical space. This is the default state for new query tabs.
- **After query execution (F5)**: results panel expands to show data. Default split: **70% editor / 30% results.** User can drag the splitter to adjust.
- **Collapsible**: Ctrl+J toggles results panel. When collapsed, editor takes full height. Results tab bar remains visible as the collapse handle.
- **Double-click the results tab bar** to toggle between collapsed and expanded states.

### Other Layout Rules

- Object Explorer default: 220px, resizable, collapsible (Ctrl+B)
- Splitters: 4px, `SplitterBackground`, no visible grab handle
- Minimum window: 900×600
- **No separator line between editor and results.** The results tab bar row is the visual divider.
- **Zero gap between menu bar and main tab strip.** Flush, no margin, no border between them.

### Object Explorer Filter

The filter box stays (it's useful) but should be **more compact**:
- Height: 24px (down from current ~32px)
- Smaller font: 11px
- Minimal vertical margin: 2px above and below
- Watermark text: "Filter..." (shorter than "Filter objects...")
- The goal is to minimize its footprint while keeping it always visible

---

## Logo Concept

**A closed eye** — simple line-art, monochrome.

Meaning: "You can trust this app to handle your database with your eyes closed." Also a nod to the original icon (the sleeping boss).

Usage:
- App icon (AppIcon.ico / AppIcon.icns)
- About dialog
- Connection dialog header (small, next to app name)
- Splash/loading if we ever add one

Style: single-weight line drawing, works at 16px (favicon), 32px (title bar), 128px (about dialog), and 512px (app icon). Should look good in both dark theme (light lines on dark) and light theme (dark lines on cream).

Generate candidates using AI image gen or SVG hand-drawing. Keep it simple — one curved line for the closed lid, a few lashes. No eyebrow, no detail overkill.

---

## What NOT to Do

1. No gradients. Flat only.
2. No shadows.
3. No animations except loading spinners and scrollbar fade.
4. No color-filled icons. Monochrome line icons only, color only for status dots.
5. No large empty states with illustrations. Single line of TextSecondary hint text, centered.
6. No marketing language. "Run a query to see results here" = good. "Unleash your data!" = no.
7. No tooltips that repeat button labels. Tooltips must add information (shortcut key, clarification) or not exist.
8. No progress bars for operations under 500ms.
9. No confirmation dialogs for non-destructive actions.
10. No horizontal scrolling in forms.
11. **No emoji in the UI.** The history button and any other places using emoji characters — replace with proper SVG icons or unicode glyphs.
12. **No always-visible scrollbars.** Use overlay auto-hide scrollbars everywhere.
13. **No shortcut keys in button labels.** "Run" not "Run (F5)". Shortcuts belong in tooltips.
14. **No results metadata in the toolbar.** "1 result set(s), 15 total rows" belongs in the status bar only.
15. **No broken window management.** The app must be resizable, movable, maximizable, and minimizable at all times.

---

## Settings Additions

Add to SettingsDialog (under a new "Appearance" section):

1. **Grid Row Height** — slider or numeric input, range 20–32px, default 22px. Applies to all DataGrids in the app.
2. **Theme** — already exists (Dark/Light radio), but Light should use the warm cream theme defined above.

Add to SettingsDialog (under a new "Version History" section):

3. **DDL Audit Source** — Server, Database, Table Name fields. Default empty. When empty, Version History tab shows "Not configured" hint instead of an error.
4. **"Create Audit Infrastructure" button** — generates the DDL trigger creation script, shows it in a preview dialog for DBA review before execution. Or "Copy Script" to clipboard so DBA can run manually.

Add to SettingsDialog (under a new "Git Integration" section):

5. **Git Export Path** — folder picker. When set, object definitions are exported as .sql files to this folder on sync.

---

## Priority Implementation Order

### Phase 1: Visual Consistency — DONE (with issues)

CC executed Phase 1. Most changes landed correctly. **However, the Object Explorer spacing regressed badly** — items are too spread out and the panel lost its compact, SSMS-like density. The original was better. Phase 1 Fix (below) must be completed before moving to Phase 2.

What was done in Phase 1:
1. ✅ Button corner radius set to 2px globally
2. ✅ ConnectionDialog header/title/padding normalized
3. ✅ Editor↔results separator line removed
4. ✅ AC and history buttons replaced with monochrome icons
5. ✅ Object Explorer: left padding and indent guides added
6. ✅ Data grid: row height and grid line changes
7. ⚠️ Object Explorer density — BROKEN, too much vertical spacing

---

### Phase 1 Fix + Layout Overhaul ← CC START HERE

This phase fixes Phase 1 regressions AND implements the layout changes discussed after Phase 1 review. Read every item carefully — this is the most impactful set of changes for how the app feels.

#### A. Object Explorer Density + Padding Fix

**Problem**: Phase 1 made OE items too spread out. The original fit all categories (Tables through Jobs) with AuditLog expanded. After Phase 1, it only reaches Stored Procedures. That's a massive density loss.

**The density was a feature, not a bug.**

1. **Revert OE item vertical padding/margin** to original values. If original row height was ~22-24px, restore that. The spec says 26px — if that's too loose, match the original and we'll update the spec.
2. **Keep Phase 1 improvements** that didn't hurt density: indent guide lines, separator lines between category headers.
3. **Increase OE left padding to 20px** from the panel edge for root-level items (expand arrows, icons). Currently the arrows start right at the app border with zero space. 20px gives proper breathing room.
4. **Compact the OE filter box**: height 24px, font 11px, 2px vertical margin, watermark text "Filter..." (shorter).

**Success criteria**: With Employees expanded (all 12 columns visible), OE must show Tables (with AuditLog, Departments collapsed, Employees expanded), EmployeeSalaryHistory, ObjectVersions, ProjectAssignments, Projects, Views, Stored Procedures, Functions, Sequences, and Jobs — all visible without scrolling.

#### B. Merge Toolbar into Query Tab Row

**This is the single biggest layout win.** Currently there are 4 horizontal bars before the editor (menu, main tabs, query tabs, toolbar). We're killing one.

1. **Move Database dropdown, Run button, and Stop button** into the query sub-tab row, right-aligned.
2. The row becomes: `[Query 1 ×] [+]` on the left, `Database: [TestDB ▾] [Run] [Stop]` on the right.
3. **Remove the old toolbar row entirely.** It no longer exists as a separate element.
4. **Move "1 result set(s), 15 total rows"** out of this area completely. It's already in the status bar — remove the duplicate from the toolbar/tab area.
5. AC/history/gear buttons stay top-right where they are (above the query tab row in the main tab area).
6. **Database dropdown** needs 2px vertical margin above and below inside the 28px bar — don't let it touch the bar edges.

#### C. Button Sizing in Toolbar

1. **Max button height: 24px** with 2px margin top and bottom inside any 28px bar.
2. **Run and Stop need room to breathe.** Same min width for both: 60px. Horizontal padding: 12px. These are the primary action buttons — they must not look squished.
3. If horizontal space is tight, **query tabs compress first** (truncate names with ellipsis). Run/Stop/Database never shrink below their minimum.

#### D. Results Tab Bar — Tabs vs Buttons

The results tab bar currently looks wrong because everything is styled as buttons. Fix:

1. **"Result 1 (15 rows)" and "Messages" are TABS**, not buttons. Style them as tabs — active tab gets `ActiveTabBackground` + `TextBright` + bottom accent border, inactive gets `InactiveTabBackground` + `TextSecondary`. Same visual language as the query tabs above.
2. **"Export" and "Edit" are BUTTONS** and stay styled as compact action buttons (24px height, 11px text, 8px horizontal padding).
3. **Audit ALL hidden/conditional buttons** in this bar: Apply, Add Row, Delete Row, Cancel Edit, and any others. Every one of them must: (a) fit within the 28px bar height, (b) use 24px max button height, (c) use 11px text, (d) not clip or overflow. Test with Edit mode active to verify all buttons render correctly.
4. Tab text size: 11px to fit comfortably in 28px.

#### E. Query Tab Text Size

1. **"Query 1", "Query 2" text inside query tabs must be 12px Regular.** Currently oversized. It should match the text size of Object Explorer items like "Employees" or "AuditLog". The tab container/height is fine — only the font size of the label text needs to shrink.

#### F. Bar Heights & Gaps

1. **Verify all bars are 28px**: menu bar, main tab strip, query tab strip (now includes toolbar), results tab strip. Status bar is 24px.
2. **Zero gap between menu bar and main tab strip.** Flush. No margin, no padding, no border between them.
3. **No separator line between editor and results.** Results tab bar IS the separator.

#### G. Results Panel Default State

1. **Before any query is executed**: results panel starts **collapsed**. Only the results tab bar strip is visible at the bottom of the editor. Editor gets ~100% of vertical space.
2. **After F5 / query execution**: results panel expands. Default split: **70% editor / 30% results.**
3. **Ctrl+J** toggles results panel between collapsed and expanded.
4. **Double-click the results tab bar** to toggle collapsed/expanded.

#### H. Button Labels & Tooltips

1. **Remove "(F5)" from the Run button label.** Just "Run".
2. **Remove any shortcut text from all button labels.** Shortcuts go in tooltips only.
3. **Add tooltips** to all toolbar and action buttons. Follow the Tooltip spec in Components section above. 600ms delay, no label repetition, include keyboard shortcut.
4. **Remove "Ready" text** from anywhere in the toolbar area. Status info belongs in the status bar only.

#### I. Scrollbars

1. **Implement overlay auto-hide scrollbars** on all scrollable areas: Object Explorer, results DataGrid (horizontal + vertical), any scrollable dialog content.
2. Follow the Scrollbar spec in Components section: invisible by default, appear on scroll/hover, fade out after ~1.5s, thin thumb with no track background, no arrow buttons.
3. If Avalonia doesn't support true overlay scrollbars, use `Auto` visibility and style the `ScrollBar` template to be as thin and subtle as possible (6px width, semi-transparent thumb, no track).

#### J. Window Resize & Move

**Update**: Resize works now. Moving does NOT — the window can't be dragged by the title bar. This is likely because the macOS traffic lights (close/minimize/maximize) are sitting in the merged menu bar, and the drag region for the title bar got broken during the toolbar merge. Fix the drag region so the window is movable again. The traffic light buttons and the drag-to-move region must coexist.



1. **The app currently can't be resized or moved with the mouse. Fix this.** Whatever is blocking standard window behavior (likely `CanResize`, `ExtendClientAreaToDecorationsHint`, or custom title bar implementation) — find it and fix it.
2. After fix, verify: drag title bar to move, drag edges/corners to resize, double-click title bar to maximize/restore, minimize button works. All must function.
3. Standard window operations are non-negotiable.

#### K. Database Dropdown Border

The dropdown has a white/light border but it's too thick — the bottom edge of the border gets clipped by the bar. Reduce the border to 1px. Ensure all 4 sides of the border are visible within the 28px bar (with the 2px vertical margin, the dropdown is 24px — the 1px border must fit inside that).


#### L. Run/Stop Button Text Centering

Run and Stop button text is not vertically centered within the buttons. The text sits too high or too low. Fix the vertical alignment — text should be perfectly centered. Check `VerticalContentAlignment="Center"` and that padding is symmetric top/bottom.

#### M. Row Number / First Column Separation

The results grid currently has no vertical separation between columns at all. This makes it hard to tell where one field ends and the next begins — especially with text columns like Email running into DepartmentId. Two specific problems:

1. **Row number gutter vs first data column**: Row numbers (1, 2, 3) run directly into EmployeeId (1, 2, 3) and read as "11", "22", "33". The row number gutter needs a subtle right border (1px, `BorderDefault` at 30% opacity) AND a few pixels of right padding/gap.

2. **All data columns need subtle vertical separation.** The design doc says "no vertical grid lines" and that's right for heavy 1px borders between every cell — but there needs to be SOME visual cue. Options: (a) increase horizontal cell padding so there's more whitespace between values, or (b) add very faint vertical lines (1px, `BorderDefault` at 15-20% opacity) between column headers that extend down through the data rows. Option (b) is what SSMS does and it works. The lines should be barely visible — just enough to guide the eye.


#### N. Connection Dialog — Still Not Fixed

The ConnectionDialog was marked as fixed in Phase 1 but it's NOT. Current state:
- Still has a separate darker header bar ("Connect to SQL Server") — this should be REMOVED. One uniform background for the entire dialog.
- Title "SQL Version Control" is still too large — reduce to 18px SemiBold per the Dialogs spec.
- Review the entire dialog against the Dialogs component spec in this document: 24px padding all sides, no separate header strip, button groups right-aligned with primary last.


#### O. Database Dropdown Still Clipped After K Fix

K's border fix was applied but the dropdown is STILL getting clipped at the bottom. The Run/Stop buttons next to it fit perfectly in the bar — the dropdown should be the exact same height. Whatever height Run/Stop are using, match it for the dropdown. If the border is eating into the space, reduce it or shrink the dropdown's internal height. Just make it identical to Run/Stop — they're in the same bar.

#### P. Connection Dialog Still Has Old Styling — POSTPONED

The ConnectionDialog was supposedly fixed in Phase 1 but it still has:
- A separate darker header bar ("Connect to SQL Server") — remove it. One uniform background for the entire dialog per the Dialogs spec.
- Title "SQL Version Control" appears too large — should be 18px SemiBold max.
- Review the entire dialog against the Dialogs component spec: 24px padding all sides, no separate header strip, button groups right-aligned with primary button last.


#### Q. Light Theme — OE Header and Results Bar Wrong Color

The Object Explorer header ("Object Explorer" text area) and the results tab bar (Result 1 / Messages / Export / Edit) are using the content area cream color instead of the chrome/panel header color. These are structural chrome elements and should use PanelHeaderBackground (#d5cbb8 in light theme) to match the top bars. They should feel like they're part of the app frame, not part of the content.


#### R. Light Theme — Results Tab Bar and Grid Header Blend Together

The results tab bar and the grid header row below it are now the same color — you can't tell where the tab bar ends and the column headers begin. The results tab bar should use chrome (#d5cbb8) but the grid header needs slight differentiation. Either make the grid header slightly lighter/darker than the tab bar, or add a subtle 1px border between them.

#### S. Theme Switching Breaks — Dark Colors Leak Into Light Theme

Switching from dark to light and back causes colors from the dark theme to remain in the light theme. The ThemeManager is not properly swapping ALL resource values on theme change. CC needs to:
1. Search the entire codebase for any hardcoded hex color values in .axaml files that should be using DynamicResource tokens instead
2. Ensure the ThemeManager fully replaces the resource dictionary on switch — not merging, REPLACING
3. Test the full cycle: launch in dark → switch to light → switch back to dark → switch to light again. Every element must update correctly every time.
4. Check for any StaticResource references that should be DynamicResource — StaticResource won't update on theme change


#### Verification Checklist

After completing all items, verify:
- [ ] OE shows all categories with Employees expanded (density test)
- [ ] OE has 20px left padding — arrows don't touch the panel edge
- [ ] Only 3 horizontal bars before editor content (menu, main tabs, query tabs+toolbar)
- [ ] No gap between menu bar and main tabs
- [ ] All bars are exactly 28px (status bar 24px)
- [ ] No buttons clipped or overflowing any bar
- [ ] Run and Stop both say just "Run" / "Stop", both min width 60px, not squished
- [ ] Database dropdown has 2px vertical margin in the bar
- [ ] No "1 result set(s)" text in toolbar area
- [ ] No "Ready" text in toolbar area
- [ ] Results "Result 1" and "Messages" look like tabs, not buttons
- [ ] Results "Export", "Edit", and all hidden buttons (Apply, Add Row, etc.) fit in 28px bar
- [ ] Query tab text "Query 1" is 12px, same size as OE item text
- [ ] Results panel is collapsed by default (before any query runs)
- [ ] Tooltips appear on Run, Stop, AC, History, Gear, Export, Edit with 600ms delay
- [ ] Scrollbars are not permanently visible — they auto-hide
- [ ] OE filter box is 24px height with "Filter..." watermark
- [ ] Window can be moved by dragging title bar
- [ ] Window can be resized from edges and corners
- [ ] Window can be maximized, restored, and minimized
- [ ] Window can be dragged/moved by the title bar area
- [ ] Database dropdown border is 1px, all 4 sides visible within the bar
- [ ] Run and Stop button text is vertically centered
- [ ] Row numbers visually separated from first data column (border + gap)
- [ ] Grid columns have subtle vertical separation (faint lines or increased padding)
- [ ] Database dropdown is the exact same height as Run/Stop buttons — no clipping
- [ ] ConnectionDialog has no separate header bar — one uniform background
- [ ] ConnectionDialog title is 18px SemiBold, not larger
- [ ] Light theme: OE header and results tab bar use chrome color, not content cream
- [ ] Light theme: results tab bar visually distinct from grid header below it
- [ ] Theme switching: dark→light→dark→light with no color leaks, every element updates

---

### Phase 2: Light Theme

1. Create `Styles/AppThemeLight.axaml` with the warm cream palette defined in the "Light Theme (Warm)" section above
2. Update ThemeManager to swap between dark and light resource dictionaries
3. Implement syntax highlighting colors for light theme (independent values, NOT inverted dark)
4. Test every view in light theme — ensure no hardcoded hex colors leak through. Search the entire codebase for any raw hex values in .axaml files that should be using theme tokens instead.

---

### Phase 3: Settings & Configuration

5. Add "Appearance" section to SettingsDialog with:
    - Grid Row Height control (slider or numeric input, range 20-32px, default 22px, stored in settings.json)
    - Theme selector should use the warm cream light theme when Light is selected

6. Add "Version History" section to SettingsDialog with:
    - DDL Audit Source fields: Server, Database, Table Name (default empty)
    - "Create Audit Infrastructure" button that generates the DDL trigger creation script and shows it in a preview dialog, or "Copy Script" to clipboard
    - When DDL audit is not configured: Version History tab shows hint text ("Configure DDL audit tracking in Settings → Version History"), NOT the current error message

7. Add "Git Integration" section to SettingsDialog with:
    - Git Export Path — folder picker
    - When set, object definitions export as .sql files to this folder on sync

---

### Phase 4: Logo

8. Design closed-eye logo (AI gen or SVG hand-drawn)
9. Replace app icons (AppIcon.ico / AppIcon.icns)
10. Add to About dialog and Connection dialog

---

## Reference

When in doubt about how something should look, CC should:
1. Check this document for the specific component
2. Look at the mockup screenshots in the project (when added)
3. Default to "less is more" — if unsure whether to add a visual element, don't
4. Match SSMS density with Ghostty color warmth
5. When making OE or tree changes: **always verify density hasn't regressed** by checking how many items fit in the visible panel area before and after
6. **The editor is the hero.** Every design decision should maximize the space available for writing and viewing SQL.
