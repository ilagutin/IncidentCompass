using System.Runtime.CompilerServices;

// The Worker is an executable host, not a library: nothing outside it composes its types, so they
// are internal. The pumps, lease renewers, options and the poll-delay calculation are still worth
// testing directly, and these grants are how the test projects reach them instead of the host
// widening its surface to make them reachable.
[assembly: InternalsVisibleTo("IncidentCompass.UnitTests")]
[assembly: InternalsVisibleTo("IncidentCompass.IntegrationTests")]
