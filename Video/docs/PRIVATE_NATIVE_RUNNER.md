# Private Windows Native Smoke Runner

Updated: 2026-07-18

This checklist provisions the dedicated runner required by
`.github/workflows/windows-native-smoke.yml`. Do not register a general development
workstation or a public/shared runner because the job accesses host-licensed BASS and
FFmpeg binaries.

## Runner Requirements

- Dedicated Windows 10/11 x64 machine or VM.
- GitHub self-hosted runner service installed under a restricted service account.
- Default labels `self-hosted`, `Windows`, and `X64`.
- Custom label `fatzbomb-native`.
- Access limited to the FatzBombMedia repository or a restricted runner group.
- Network access to GitHub and NuGet.org.
- Enough temporary space for self-contained publish output and four local packages.

## Native Directory

Create one directory outside the repository checkout containing the approved x64 DLL
set documented in `docs/NATIVE_DEPENDENCIES.md`, for example:

```text
D:\FatzBombNative\win-x64
```

Grant read access only to the runner service account and administrators. Do not place
the binaries in the runner work directory, repository, GitHub cache, or workflow
artifacts.

Expose the path in one of these ways:

1. Repository variable `FATZBOMB_MEDIA_NATIVE_PATH` containing the runner-local path.
2. Machine/service-account environment variable `FATZBOMB_MEDIA_NATIVE_PATH`.

The value is a filesystem path, not a secret or binary payload. The workflow validates
that the directory exists before running.

## GitHub Configuration

1. Create protected environment `native-media-smoke`.
2. Restrict environment deployment branches to trusted branches.
3. Add required reviewers if native validation should require release approval.
4. Register the runner at repository or restricted organization-runner-group scope.
5. Add custom runner label `fatzbomb-native`.
6. Keep fork pull-request workflows and untrusted branches off this runner.

The workflow is manual `workflow_dispatch` only. It has read-only repository
permissions and uploads no artifacts.

## Validation

1. Push the workflow and managed package commits to the trusted branch.
2. Open Actions > Windows Native Package Smoke > Run workflow.
3. Confirm the selected runner has all four required labels.
4. Confirm both smoke passes report success:
   - caller-supplied native path
   - predefined `runtimes/win-x64/native` path
5. Confirm the Windows package inspection reports no native DLLs.

The workflow places generated package/publish output under `RUNNER_TEMP`, not the
checkout. GitHub Runner normally clears this directory around each job; configure an
additional machine retention policy if required by the host's licensing/security
rules.

## Maintenance

- Update native files only through the process in `docs/NATIVE_DEPENDENCIES.md`.
- Restart the runner service after changing its environment variables.
- Patch Windows, .NET, GitHub Runner, and endpoint protection regularly.
- Rotate runner registration credentials according to organization policy.
- Remove the runner registration before repurposing or decommissioning the machine.
