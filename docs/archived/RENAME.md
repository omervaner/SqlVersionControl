# App Rename: "SQL Version Control" → "Lookout"

**Created:** March 30, 2026
**Purpose:** Rename the app everywhere — user-facing strings, metadata, config paths, build scripts, docs.

C# namespace stays `SqlVersionControl` — it's internal, users never see it, and renaming it touches every single file for zero user benefit. Can be done later if desired.

Git repo name stays `SqlVersionControl` for now — changing it requires GitHub redirect setup and Velopack source URL changes. Can be done separately.

Local folder stays `/Users/omer/Documents/Projects/SqlVersionControl` — same reason.

---

## Section 1 — macOS Menu Bar Name ("Avalonia Application" Fix)

### 1.1 App.axaml — Set Application Name
**File:** `App.axaml`

Add `Name="Lookout"` to the `Application` tag:
```xml
<Application xmlns="https://github.com/avaloniaui"
             ...
             x:Class="SqlVersionControl.App"
             Name="Lookout"
             RequestedThemeVariant="Dark">
```

This is the Avalonia property that maps to the macOS application menu name. Without it, macOS falls back to the executable name, which in debug mode is "Avalonia Application".

### 1.2 Info.plist — macOS Bundle Metadata
**File:** `Info.plist`

```xml
<key>CFBundleName</key>
<string>Lookout</string>
<key>CFBundleDisplayName</key>
<string>Lookout</string>
<key>CFBundleIdentifier</key>
<string>com.omervaner.lookout</string>
```

This takes effect in packaged .app bundles (releases). Combined with 1.1, both debug and release builds show "Lookout".

---

## Section 2 — Window Titles and Dialogs

### 2.1 MainWindow.axaml
**File:** `Views/MainWindow.axaml`

Change:
```xml
Title="SQL Version Control"
```
To:
```xml
Title="Lookout"
```

### 2.2 ConnectionDialog.axaml
**File:** `Views/ConnectionDialog.axaml`

Change the title TextBlock:
```xml
<TextBlock Text="SQL Version Control" FontSize="18" FontWeight="SemiBold" .../>
```
To:
```xml
<TextBlock Text="Lookout" FontSize="18" FontWeight="SemiBold" .../>
```

Also change the subtitle:
```xml
<TextBlock Text="Connect to your database server" .../>
```
To:
```xml
<TextBlock Text="Connect to your database server" .../>
```
(This one is fine as-is — it describes the action, not the app.)

The dialog window title:
```xml
Title="Connect to SQL Server"
```
Keep as-is — this describes the action.

### 2.3 AboutDialog.axaml
**File:** `Views/AboutDialog.axaml`

Change:
```xml
<TextBlock Text="SQL Version Control" FontSize="18" FontWeight="SemiBold" .../>
```
To:
```xml
<TextBlock Text="Lookout" FontSize="18" FontWeight="SemiBold" .../>
```

Change description:
```xml
<TextBlock Text="Track and manage SQL Server changes" .../>
```
To:
```xml
<TextBlock Text="SQL Server management & version control" .../>
```
(Or whatever tagline Ömer prefers.)

### 2.4 AppVersion.cs
**File:** `Services/AppVersion.cs`

Change:
```csharp
public static string DisplayString => $"SQL Version Control v{Version}";
```
To:
```csharp
public static string DisplayString => $"Lookout v{Version}";
```

---

## Section 3 — Project Metadata

### 3.1 SqlVersionControl.csproj
**File:** `SqlVersionControl.csproj`

Change:
```xml
<AssemblyTitle>SQL Version Control</AssemblyTitle>
<Product>SQL Version Control</Product>
<Description>Track and manage SQL Server stored procedure versions with diff view and rollback support</Description>
```
To:
```xml
<AssemblyTitle>Lookout</AssemblyTitle>
<Product>Lookout</Product>
<Description>SQL Server desktop IDE — queries, version tracking, database comparison, execution plans, and job management</Description>
```

### 3.2 app.manifest (Windows)
**File:** `app.manifest`

Change:
```xml
<assemblyIdentity version="1.0.0.0" name="SqlVersionControl.Desktop"/>
```
To:
```xml
<assemblyIdentity version="1.0.0.0" name="Lookout.Desktop"/>
```

---

## Section 4 — Config / Data Folder Paths

### 4.1 SettingsService.cs — Data Folder Name
**File:** `Services/SettingsService.cs`

Change:
```csharp
public static readonly string DefaultDataFolder = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "SqlVersionControl");
```
To:
```csharp
public static readonly string DefaultDataFolder = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "Lookout");
```

**⚠️ MIGRATION:** This changes where settings.json, credentials.json, session.json, and saved queries live. Add a one-time migration in `SettingsService.Load()`:

```csharp
private static void MigrateLegacyDataFolder()
{
    var legacyFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SqlVersionControl");
    
    if (Directory.Exists(legacyFolder) && !Directory.Exists(DefaultDataFolder))
    {
        try
        {
            Directory.Move(legacyFolder, DefaultDataFolder);
        }
        catch
        {
            // If move fails (permissions, etc.), just start fresh
        }
    }
}
```

Call `MigrateLegacyDataFolder()` at the top of `SettingsService()` constructor, before `Load()`.

### 4.2 PasswordStore.cs — Key Derivation Seed
**File:** `Services/PasswordStore.cs`

Change:
```csharp
var seed = $"{Environment.MachineName}|{Environment.UserName}|SqlVersionControl";
var salt = Encoding.UTF8.GetBytes("SqlVersionControl.PasswordStore");
```
To:
```csharp
var seed = $"{Environment.MachineName}|{Environment.UserName}|Lookout";
var salt = Encoding.UTF8.GetBytes("Lookout.PasswordStore");
```

**⚠️ BREAKING CHANGE:** Changing the key derivation seed means existing encrypted passwords can't be decrypted. The migration path:

Option A (simple): Users re-enter passwords once after the update. The `Load()` catch block already handles decryption failures gracefully — entries that fail to decrypt are silently skipped.

Option B (smooth): Try decrypting with the new seed first, then fall back to the old seed:

```csharp
public static void Load()
{
    // ... existing load logic, but in the per-entry loop:
    foreach (var kvp in encrypted)
    {
        try
        {
            _passwords[kvp.Key] = Decrypt(kvp.Value);
        }
        catch
        {
            // Try legacy key derivation
            try
            {
                _passwords[kvp.Key] = DecryptLegacy(kvp.Value);
            }
            catch { /* truly corrupted — skip */ }
        }
    }
    
    // If any were decrypted with legacy key, re-save with new key
    if (/* any legacy decryptions succeeded */) Save();
}
```

**Recommendation:** Go with Option A. It's one-time friction and avoids carrying legacy crypto code forever.

---

## Section 5 — Build & Release Scripts

### 5.1 GitHub Actions Workflow
**File:** `.github/workflows/release.yml`

Change `--packId SqlVersionControl` to `--packId Lookout`:
```yaml
- name: Pack with Velopack
  run: vpk pack --packId Lookout --packVersion ${{ steps.version.outputs.VERSION }} ...
```

Change `mainExe` references:
```yaml
matrix:
  include:
    - os: macos-latest
      mainExe: SqlVersionControl    # ← Leave as-is (this is the .NET executable name, tied to csproj)
```

Actually, `mainExe` must match the actual executable filename, which comes from the csproj project name. Since we're NOT renaming the csproj file or namespace, the executable is still `SqlVersionControl` / `SqlVersionControl.exe`. Leave `mainExe` as-is.

### 5.2 CLAUDE.md Build Commands
**File:** `CLAUDE.md`

Update all `vpk pack` commands:
```bash
vpk pack --packId Lookout --packVersion X.Y.Z ...
```

Update `gh release create` — file names will change because packId changes:
```bash
gh release create vX.Y.Z \
  Releases/Lookout-X.Y.Z-osx-full.nupkg \
  Releases/Lookout-osx-Portable.zip \
  Releases/Lookout-osx-Setup.pkg \
  ...
```

### 5.3 UpdateService.cs — GitHub Source
**File:** `Services/UpdateService.cs`

The GitHub source URL stays the same (repo isn't being renamed):
```csharp
var source = new GithubSource(
    "https://github.com/omervaner/SqlVersionControl", ...);
```

**However:** Velopack matches updates by packId. If packId changes from `SqlVersionControl` to `Lookout`, existing installations won't find updates. This means the first "Lookout" release is effectively a fresh install for existing users. Since this app has very few external users (primarily Ömer), this is fine.

---

## Section 6 — Documentation

### 6.1 CLAUDE.md
**File:** `CLAUDE.md`

Replace all occurrences of the following:
- "CheatTeam" → "Lookout" (in headers, prose)
- "SQL Version Control" → "Lookout" (in headers, prose, display strings)
- "SqlVersionControl" as a *user-facing name* → "Lookout" (but leave code references like namespace, folder paths, class names as-is)

Update the project identity section:
```
## Project Identity
- **Project Name**: Lookout
- **Folder**: `/Users/omer/Documents/Projects/SqlVersionControl`
- **Repository**: omervaner/SqlVersionControl
- **Purpose**: Cross-platform SQL Server desktop IDE
```

Update the PROJECT STATUS header:
```
## PROJECT STATUS: v2.1.0 (date)
```

Update the data storage paths section to reference `~/Library/Application Support/Lookout/`.

Update the Quick Reference `cd` command (folder stays the same).

### 6.2 README.md
**File:** `README.md`

Change the title and all references:
```markdown
# Lookout

A cross-platform SQL Server desktop IDE...
```

### 6.3 docs/SECURITY.md
**File:** `docs/SECURITY.md`

Update the header and any references to the old name. Most references are to code paths which stay as-is.

### 6.4 All Session Summary docs
**Files:** `docs/SESSION-SUMMARY-*.md`

These are historical records — leave them as-is. They document what happened at the time.

---

## Section 7 — What NOT to Rename

| Item | Reason |
|------|--------|
| C# namespace `SqlVersionControl` | Internal, touches every file, zero user benefit |
| Folder `/Users/omer/Documents/Projects/SqlVersionControl` | Git history, muscle memory, existing scripts |
| GitHub repo `omervaner/SqlVersionControl` | Requires redirect setup, Velopack URL change |
| .csproj filename `SqlVersionControl.csproj` | .NET project identity, tied to namespace |
| Executable name `SqlVersionControl` / `.exe` | Internal, users launch via app icon not CLI |
| Class names, file names | Internal code structure |

---

## Execution Order

CC should do these in this order:
1. **Section 1** — App.axaml Name + Info.plist (fixes the menu bar immediately)
2. **Section 2** — Window titles and dialogs
3. **Section 3** — Project metadata (csproj, manifest)
4. **Section 4** — Config paths + migration code (most complex — test carefully)
5. **Section 5** — Build scripts
6. **Section 6** — Documentation

After all changes, verify:
- [ ] macOS menu bar shows "Lookout" (not "Avalonia Application")
- [ ] Main window title bar shows "Lookout"
- [ ] Connection dialog shows "Lookout"
- [ ] About dialog shows "Lookout vX.Y.Z"
- [ ] Settings/credentials/session migrate from old folder to new
- [ ] Old passwords re-entered once (Option A) or auto-migrated (Option B)
- [ ] `vpk pack --packId Lookout` produces correct output filenames
- [ ] GitHub Actions workflow runs successfully with new packId
