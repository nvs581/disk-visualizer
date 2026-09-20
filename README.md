# Disk Visualizer

A Windows disk analyzer with an interactive sunburst, file-size and disk-allocation views, and content-based duplicate detection. Version 0.2 is a runnable, read-only preview.

## Run

Open `bin\DiskVisualizer.exe`. Choose a folder, click a drive in the sidebar, or enter a path and press Enter.

- Hover a folder or file row to highlight its visible sunburst branch. Keyboard focus provides the same preview; leaving restores the selected item. Hovering does not change selection.
- Click a chart segment to select it; double-click a folder segment or list row to explore it.
- Click the chart center or press **Alt+Up** to return to the parent folder.
- Click a list column heading to sort. **Ctrl+F** focuses the current-folder filter.
- Select an item and use **Show in Explorer**, or right-click for Explorer / copy path / cleanup-list actions.
- Use **Add to cleanup list**, or drag a list item to the footer, then choose **Review items**. Nested selections are counted once. **No files are deleted.**
- File sizes appear first; **On disk** values populate after the second metadata pass. Select **Size on disk** above the chart to visualize allocation instead of file size. Both columns remain visible.
- Choose **Find duplicate files** in the sidebar, then **Find duplicates** in the dialog. The check is optional, reads file contents, and covers the entire scanned location.
- The drive sidebar refreshes automatically when Windows reports device changes. Manual refresh and refresh on window activation remain available. Connecting a drive never starts a scan.
- Cancel a running scan to inspect the partial results. **Escape** also cancels.

The map appears after the initial file-size scan finishes or is canceled. You can navigate it while disk allocation is measured. Live counters remain available while scanning. A new scan clears current results, duplicate results, and (after confirmation) any cleanup list. Normal startup never scans automatically.

## Build and test

Requires Windows x64 with .NET Framework 4.8 and its built-in C# compiler. No NuGet downloads or browser runtime are required.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Run
```

The execution-policy option applies to that PowerShell process only. The executable and its `.config` file must stay together. Build output and local test artifacts are ignored by Git.

Optional UI smoke test (use a folder containing at least one nonempty subfolder):

```powershell
Start-Process .\bin\DiskVisualizer.exe -ArgumentList '--scan "C:\path\to\project" --ui-test' -Wait
Get-Content .\bin\ui-test-results.txt
```

`--scan "path" --screenshot` explicitly scans a location, writes `bin\preview.png`, and closes. `--ui-test` exercises size metrics, title-bar maximize/restore, the duplicate dialog, navigation, filtering, cleanup-list overlap, row previews, drive-refresh coalescing, simulated drive removal/replacement, and minimum window layout, then saves its report and screenshots. Check the report for failures. Use a small stable fixture for this test, not a whole drive.

## What this version measures

**File size:** logical sizes of unnamed file streams obtained through Windows directory enumeration.

**Size on disk:** allocation reported by Windows for each file entry, with compression/sparse-file metadata used when applicable. Metadata is queried in a separate background pass. Unknown values remain unknown; partial directory totals show `+ unknown`. The allocation chart explicitly shows only known bytes if coverage is partial. Hard-link names are counted independently in storage totals, so these totals are not deduplicated physical usage. Named streams, directory metadata, and filesystem overhead are not included.

Reparse points, including junctions and cloud placeholders, are skipped; affected results are marked partial. Select or hover a skipped row to see the reason. Storage scanning does not read file contents; the optional duplicate check does.

**Duplicates:** group by size, compare samples, hash full contents with SHA-256, and verify matching candidates byte for byte. Check file identity, length, and modification time between stages. Content handles deny concurrent writes/deletion while open; changed or unreadable candidates remain unverified. Hard-link aliases and zero-length files are omitted. Results describe main-stream contents at verification time, not metadata, named streams, semantic similarity, or whether deleting a copy is safe. No duplicates are selected for deletion automatically.

If the scanned volume disconnects or is replaced, active work is canceled and existing results are marked stale. Explorer, duplicate checking, and adding cleanup items remain disabled until an explicit rescan; reconnecting does not restore them automatically. Existing results and the cleanup list remain available for review.

The result is a best-effort observation while files can change. It is neither a filesystem snapshot nor an estimate of recoverable space. Drive free-space values and scanned logical size use different accounting rules.

The scan stops at two million entries and retains explicitly partial results. Disk access can delay cancellation on an unresponsive device. A scan is single-worker in this version; there is no MFT fast path or measured speed advantage over other analyzers yet.

## Current limits

- Read-only collection; recycling, permanent deletion, and automatic updates are not implemented.
- No unique physical-allocation accounting, persistent index, or incremental scan.
- Drive discovery covers local/removable drives and uses debounced Windows notifications with bounded readiness retries. Very slow devices may require manual refresh; network and optical drives are omitted. Physical USB unplug/replug behavior still needs hardware testing.
- Search filters direct children of the current folder. It does not search the entire scan or change chart totals.
- The chart renders up to five levels and 5,000 sectors. Very small sectors are omitted visually; their items remain in the list and their sizes remain in totals.
- Chart/list selection synchronizes for immediate children. Deeper chart selections show details and can be explored by double-clicking.
- Minimum window size is 1120 × 840 device-independent pixels. The title bar and dialog captions match the dark interface. Standard dragging, resizing, maximize/restore, minimize, close, and keyboard window commands use WPF WindowChrome. Windows 11 maximize-hover Snap Layout integration and mixed-DPI behavior need further testing.
- This build is unsigned. Distribution signing and an installer are deferred.

## Implementation

C# / WPF UI, custom cached `StreamGeometry` sunburst, virtualized list, background Win32 `FindFirstFileExW` scanner, iterative tree aggregation, on-demand full paths, and throttled progress. Sorting/filtering runs in background tasks. The Windows shell handles reveal-in-Explorer requests.

## Benchmarks

To run a warm-cache benchmark on an existing folder:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\benchmark.ps1 -Dataset 'C:\path\to\fixture' -Duplicates -ReportName my-results
```

The script performs one warmup and five measured runs per mode, alternates mode order, launches a fresh process for each run, and writes a CSV under `bin`. Omit `-Duplicates` for storage-only tests. It compares the previous executable if `bin\baseline\DiskVisualizer.exe` is present; that preserved local artifact is not required to build or run the app. Run benchmarks without concurrent file-generation or other benchmark jobs. No cold-cache or external-analyzer comparison is implied.
