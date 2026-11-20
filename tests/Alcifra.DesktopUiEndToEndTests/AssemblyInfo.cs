using Xunit;

// The desktop suite MUST run one test at a time (#1925).
//
// Nearly every class here sets the PROCESS-GLOBAL `DesktopClientOptions.ApiBaseUrl` to the host it started (and some
// swap `EnvelopeOpener.Keys`/`SignedIn` for the length of a test) — xUnit's defaults, however, run different
// COLLECTIONS in parallel, and 78 of this assembly's classes are outside the shared `ui-e2e` collection. A class
// running beside them can overwrite the static between another's setup and its HTTP calls, which is how
// DesktopMessagesParityTests' message POST intermittently failed in the full suite while passing alone. The fixtures
// are per-class and cheap; correctness of the statics is not negotiable, so the assembly is serial.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
