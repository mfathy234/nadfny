# Forced removal and uninstall queue

Implement the supplied 0.3.0 brief within Apps. Preserve scan-owned identities, SafeDeleter checks and existing leftover matching.

1. Add tests for quiet plans, MSI arguments, missing uninstallers/install folders, strict scanned registry keys, and sequential continuation after failure/cancellation.
2. Retain quiet registry commands, expose broken state, and add a registry removal helper that exports before deleting. Validate hive, view and direct Uninstall child before any process. Keep protected folders and shared folders.
3. Add a service queue with an injected runner, sequential execution, individual results and collected leftovers. Serialize uninstalls across callers.
4. Add selection and batch progress in the existing UI. Confirm exact app/folder sizes, warn about user data, require acknowledgement for forced removal, and review queue leftovers together.
5. Run focused and full tests, build and JavaScript syntax checks. Review the final diff, update README and commit without pushing.

Verification uses temporary folders and fake uninstall/registry runners only; no installed app is removed. Review cancellation, rescans, overlapping folders, failed backups and registry view handling in particular.
