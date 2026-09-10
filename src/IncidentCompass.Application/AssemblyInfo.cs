using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("IncidentCompass.UnitTests")]

// The Infrastructure grant is load-bearing, not a leftover: Infrastructure adapters implement
// internal Application ports (IMemoryRepository, ITriageToolResultCommitter, the four
// I*FaultInjector seams) and compose internal Application collaborators (CanonicalJsonSerializer,
// the MemorySeed* corpus records, TriageLedgerAppender, WorkerRoleRunner and about two dozen
// more). Narrowing it would mean making those types public, which would put Application
// implementation detail on the assembly's public surface to buy nothing.
[assembly: InternalsVisibleTo("IncidentCompass.Infrastructure")]
[assembly: InternalsVisibleTo("IncidentCompass.IntegrationTests")]
