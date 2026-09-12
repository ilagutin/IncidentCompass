namespace IncidentCompass.IntegrationTests;

/// <summary>
/// Produces the token an integration ingest helper uses to keep its signal in a fault of its own.
///
/// A bare <c>Guid.NewGuid().ToString("N")</c> does not do that. Before hashing, the intake
/// fingerprint masks every value it treats as volatile: <c>ErrorMessage</c>, the HTTP route and the
/// operation name each have any run of eight or more hexadecimal characters replaced by a single
/// placeholder. A 32-character hexadecimal GUID in one of those fields therefore contributes nothing
/// to the fingerprint, and two signals that agree on service name, environment and error type collapse
/// into one fault. The second ingest joins the existing fault instead of opening one, so it comes back
/// with no job id and the test fails somewhere far from the cause. The failure also depends on what
/// else the suite ingested, so it can pass when the class runs alone.
///
/// The token returned here begins with a letter that is not a hexadecimal digit and is joined to the
/// GUID with no separator. Every mask pattern that could swallow the GUID (timestamp, GUID, long
/// hexadecimal run and number) begins at a word boundary, and .NET puts a word boundary only between a
/// word character (<c>[A-Za-z0-9_]</c>) and a non-word character. So the rule that governs is this one:
/// the token survives while it is a single unbroken run of word characters whose first character is
/// neither a hexadecimal nor a decimal digit. No boundary then falls in front of the hexadecimal run,
/// so none of those patterns can start on it.
///
/// Judge any separator you are tempted to insert by that rule rather than by a list of examples. A
/// non-word character ends the run and starts a new word at the GUID, which is masked away again: a
/// hyphen, a space and a '.' all behave that way. An underscore does not, because an underscore is a
/// word character: <c>uniq_</c> followed by the GUID is still one word and survives exactly like the
/// unseparated form.
///
/// Service name and error type are not masked, so a token placed in either of those is distinct on its
/// own. Using this token everywhere means an ingest helper stays correct when it moves the token from
/// one field to another.
/// </summary>
internal static class IngestFingerprintUniqueness
{
    public static string Token() => "uniq" + Guid.NewGuid().ToString("N");
}
