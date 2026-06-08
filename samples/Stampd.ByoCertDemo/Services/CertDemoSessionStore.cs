// This file is intentionally empty.
//
// An earlier draft of this demo used an in-memory session store to hold the
// uploaded PFX for the lifetime of the browser session. That approach was
// replaced by CertVaultService (encrypted at rest), which is now the source
// of truth for stored certs. Delete this file from the project the next
// time you're touching the .csproj manifest.

namespace Stampd.ByoCertDemo.Services;
