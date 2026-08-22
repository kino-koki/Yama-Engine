# EngineDiagnostics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a reusable engine-level diagnostics module that reports project, asset, scene-reference, and export-package health.

**Architecture:** Create a focused `ShanyuEngine.Diagnostics` namespace with `EngineDiagnostics`, `ProjectDiagnosticsOptions`, `DiagnosticReport`, `DiagnosticIssue`, and `DiagnosticSeverity`. Keep checks read-only and deterministic so the editor, build pipeline, and tests can reuse them.

**Tech Stack:** C# / .NET 8, existing top-level smoke tests in `ShanyuEngine.Tests\Program.cs`.

---

### Task 1: Diagnostics smoke tests

**Files:**
- Modify: `ShanyuEngine.Tests\Program.cs`
- Create: `ShanyuEngine\Diagnostics\EngineDiagnostics.cs`

- [ ] Add `using ShanyuEngine.Diagnostics;` to the smoke test program.
- [ ] Add `TestEngineDiagnosticsReportsHealthyProject` to verify a valid project reports success with zero issues.
- [ ] Add `TestEngineDiagnosticsFindsMissingProjectFilesAndSceneReferences` to verify missing manifest, missing `Assets`, missing registered asset file, missing scene mesh reference, and incomplete export package are reported with stable issue codes.
- [ ] Run `dotnet run --project ShanyuEngine.Tests\ShanyuEngine.Tests.csproj -c Debug --no-restore` and confirm the new tests fail because `ShanyuEngine.Diagnostics` does not exist yet.

### Task 2: Minimal diagnostics module

**Files:**
- Create: `ShanyuEngine\Diagnostics\EngineDiagnostics.cs`

- [ ] Implement `DiagnosticSeverity` with `Info`, `Warning`, and `Error`.
- [ ] Implement `DiagnosticIssue` with `Severity`, `Code`, `Message`, and `Path`.
- [ ] Implement `DiagnosticReport` with `Success`, `ErrorCount`, `WarningCount`, `Issues`, and `GeneratedAtUtc`.
- [ ] Implement `ProjectDiagnosticsOptions` with `ProjectRoot`, optional `AssetDatabase`, and optional `ExportDirectory`.
- [ ] Implement `EngineDiagnostics.Run(ProjectDiagnosticsOptions options)` with project root, manifest, `Assets`, registered asset file, scene mesh-reference, and export package checks.
- [ ] Run the smoke tests and confirm they pass.

### Task 3: Verify and keep scope small

**Files:**
- Modify: `README.md` only if test list needs updating after implementation.

- [ ] Run `dotnet build ShanyuEngine\ShanyuEngine.csproj -c Debug --no-restore`.
- [ ] Run `dotnet run --project ShanyuEngine.Tests\ShanyuEngine.Tests.csproj -c Debug --no-restore`.
- [ ] Do not add editor UI yet; expose only the core diagnostics API.
