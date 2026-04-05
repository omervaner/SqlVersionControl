# History Tab — Git Export Integration

## Vision
The History tab should work for ALL users, not just those with a DDL audit trigger set up. The key insight: Git Export already captures full object definitions as .sql files on disk with timestamps. The live server has current definitions in `sys.sql_modules`. Diffing those two gives you "what changed since last export" — which is 80% of the value of version history without any trigger setup.

## How It Works Today
- History tab relies entirely on `VMAuditDb.dbo.DDL_Log` (a DDL trigger that logs every CREATE/ALTER/DROP)
- Changes are synced into a local `ObjectVersions` table
- Without the trigger: History tab is useless — empty state, no data, confusing UX
- Git Export exists as a separate feature (File → Export to Git) that snapshots all objects as .sql files
- These two features have zero integration

## Proposed: Two-Tier History

### Tier 1: Git Export Diff (No trigger needed — works for everyone)
- **Source**: Last Git Export snapshot on disk (`.sql` files with metadata/timestamps)
- **Live**: Current definitions from `sys.sql_modules` on the connected server
- Diff each object: exported version vs live version
- **Object Browser** shows all objects with status: Unchanged / Modified / New (not in export) / Deleted (in export but gone from server)
- **Recent Changes** becomes "Changes since last export (March 28, 2026 at 14:32)"
- **Diff View** shows exported definition (left) vs current live definition (right)
- No "who changed it" or "when exactly" — just "this is different from the last checkpoint"
- **Sync button** becomes "Re-export" — takes a new snapshot, so you can track the next round of changes

### Tier 2: DDL Audit (Admin setup — full change tracking)
- Everything that exists today
- Who changed what, when, full timeline, rollback capability
- This is the premium tier that requires DBA setup

### UX Flow

**Normal user, no trigger, no Git Export yet:**
> "No version history available. Run your first Git Export to start tracking changes."
> [Export Now] button

**Normal user, Git Export exists:**
> Object Browser shows all objects with change indicators
> "Showing changes since last export (March 28, 2026)"
> Click any object → diff view: exported vs live
> [Re-export] button to checkpoint current state

**Admin user, DDL trigger configured:**
> Full timeline as it works today
> Git Export diff available as a secondary view/filter option

## Implementation Notes

### Reading Git Export Files
- `QueryFileService` already reads .sql files — extend or create `GitExportService` to read the export directory
- Each .sql file in the export has the object schema, name, and full definition
- Git export path is in `AppSettings.GitExportPath`
- Need to parse filenames to map back to `[schema].[objectname]`

### Diffing Logic
- For each exported object: fetch current definition from `sys.sql_modules`, compare
- For objects on server not in export: mark as "New since export"
- For objects in export not on server: mark as "Deleted since export"
- Reuse `NormalizeForComparison()` from CompareViewModel for whitespace-insensitive comparison
- Reuse DiffPlex for side-by-side diff (already used in History and Compare)

### Interaction with Admin/Normal Mode (Task 4)
- Normal user sees Tier 1 only (Git Export diff)
- Admin user sees both tiers, with Tier 2 (DDL audit) as the primary when configured
- The "Re-export" action in Tier 1 is effectively the same as File → Export to Git but surfaced directly in the History tab for convenience

## Dependencies
- Task 4 (Admin/Normal user mode) should land first so we know which UI paths to build
- Git Export feature must be functional (it is)
- No new infrastructure needed — everything builds on existing code

## Not In Scope
- Automatic scheduled exports (could be future enhancement)
- Git commit integration (diffing files on disk, not git history)
- Merging Tier 1 and Tier 2 into a unified timeline (keep them separate for now)
